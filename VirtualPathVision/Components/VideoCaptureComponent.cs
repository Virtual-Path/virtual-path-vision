using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;

namespace VirtualPathVision.Components
{
    /// <summary>视频信号源类型：本地摄像头 / 网络视频流 / 录像文件回放</summary>
    public enum VideoSourceType
    {
        LocalCamera,   // 本地 USB 摄像头
        NetworkStream, // 网络 RTSP / HTTP MJPEG 流
        FileReplay     // 本地录像文件回放
    }

    /// <summary>网络连接状态</summary>
    public enum ConnectionState
    {
        Disconnected,  // 未连接
        Connecting,    // 连接中
        Connected,     // 已连接
        Failed         // 连接失败
    }

    /// <summary>
    /// 视频采集组件，支持本地摄像头和网络视频流两种信号源。
    /// 提供异步帧捕获、连接状态管理以及资源释放功能。
    ///
    /// 线程模型约定（重要）：
    /// VideoCapture 的所有原生句柄（_capture/_frame/_grayFrame）只允许由采集循环线程
    /// 或在采集循环已退出后访问。StopCapture() 不会直接释放句柄，
    /// 而是先通知循环退出、等循环结束后由 Cleanup 统一释放，从而避免 Read() 执行中途被释放。
    /// 面向 UI 的查询接口（GetFrameRate/GetResolution）只读取缓存的标量，不触碰原生句柄。
    /// </summary>
    public class VideoCaptureComponent : IDisposable
    {
        private VideoCapture? _capture;   // OpenCV 视频捕获对象（仅采集循环/已停止时访问）
        private Mat? _frame;              // 原始帧
        private Mat? _grayFrame;          // 灰度帧
        private volatile bool _isRunning; // 捕获循环运行标志
        private readonly object _lock = new(); // 多线程锁
        private bool _disposed;           // 是否已释放
        private int _cleanupDone;         // Interlocked 守卫：确保原生资源只释放一次
        private ManualResetEventSlim? _loopFinished;
        private Task? _captureLoop;
        private VideoSourceType _sourceType = VideoSourceType.LocalCamera;
        private string _networkUrl = "";
        private string _replayPath = "";

        // 回放专用状态。回放需要在帧率上做节流，否则 VideoCapture 会以最快速度
        // 读完整段录像，产线节拍与录像真实帧率脱节。
        private double _replayTargetFps;
        private bool _replayLoop;
        private long _replayTotalFrames;
        private long _replayPosition;

        // 缓存的采集参数，供 UI 线程安全读取（不触碰原生句柄）
        private double _cachedFps;
        private int _cachedWidth;
        private int _cachedHeight;

        /// <summary>当前使用的信号源类型</summary>
        public VideoSourceType SourceType
        {
            get => _sourceType;
            set => _sourceType = value;
        }

        /// <summary>网络流 URL（如 http://ip:port/video）</summary>
        public string NetworkUrl
        {
            get => _networkUrl;
            set => _networkUrl = value;
        }

        /// <summary>录像文件路径（SourceType 为 FileReplay 时使用）</summary>
        public string ReplayPath
        {
            get => _replayPath;
            set => _replayPath = value;
        }

        /// <summary>
        /// 回放节流帧率。设为 0 表示不节流，按文件原始速度尽快读取。
        /// 默认 25fps，与产线相机常见帧率一致。
        /// </summary>
        public double ReplayFps
        {
            get => _replayTargetFps;
            set => _replayTargetFps = value > 0 ? value : 0;
        }

        /// <summary>回放到文件末尾后是否从头循环。默认 false，播完即停止。</summary>
        public bool ReplayLoop
        {
            get => _replayLoop;
            set => _replayLoop = value;
        }

        /// <summary>回放已读帧数（仅 FileReplay 源有效，供 UI 显示进度）</summary>
        public long ReplayPosition => Interlocked.Read(ref _replayPosition);

        /// <summary>回放总帧数（仅 FileReplay 源有效，0 表示未知）</summary>
        public long ReplayTotalFrames => Interlocked.Read(ref _replayTotalFrames);

        /// <summary>回放进度百分比，0 表示无法确定</summary>
        public double ReplayProgress
        {
            get
            {
                long total = ReplayTotalFrames;
                if (total <= 0) return 0;
                return Math.Clamp((double)ReplayPosition / total * 100.0, 0, 100);
            }
        }

        /// <summary>当前连接状态</summary>
        public ConnectionState State { get; private set; } = ConnectionState.Disconnected;

        /// <summary>帧捕获完成事件，参数为 (原始帧, 灰度帧)</summary>
        public event Action<Mat, Mat>? OnFrameCaptured;

        /// <summary>捕获过程出错事件</summary>
        public event Action<string?>? OnCaptureError;

        /// <summary>捕获停止事件</summary>
        public event Action<string?>? OnCaptureStopped;

        /// <summary>连接状态变化事件</summary>
        public event Action<ConnectionState>? OnConnectionStateChanged;

        /// <summary>
        /// 启动视频捕获。
        /// 根据 SourceType 自动选择打开本地摄像头或网络流。
        /// </summary>
        /// <returns>是否成功启动</returns>
        public bool StartCapture()
        {
            lock (_lock)
            {
                if (_disposed) return false;
                if (_isRunning) return true;

                // 若上一次采集循环仍在收尾，先把它的原生资源释放掉
                CleanupCapture();

                try
                {
                    SetState(ConnectionState.Connecting);

                    // 根据信号源类型选择打开方式
                    _capture = _sourceType switch
                    {
                        VideoSourceType.LocalCamera => OpenLocalCamera(),
                        VideoSourceType.FileReplay => OpenFileReplay(),
                        _ => OpenNetworkStream()
                    };

                    // 检查摄像头是否成功打开
                    if (_capture == null || !_capture.IsOpened())
                    {
                        _capture?.Release();
                        _capture = null;
                        SetState(ConnectionState.Failed);
                        OnCaptureError?.Invoke(TranslationService.GetStringStatic("CameraOpenError"));
                        return false;
                    }

                    _frame = new Mat();
                    _grayFrame = new Mat();
                    _cachedFps = _capture.Get(VideoCaptureProperties.Fps);
                    _cachedWidth = (int)_capture.Get(VideoCaptureProperties.FrameWidth);
                    _cachedHeight = (int)_capture.Get(VideoCaptureProperties.FrameHeight);

                    Interlocked.Exchange(ref _cleanupDone, 0);
                    _loopFinished = new ManualResetEventSlim(false);
                    _isRunning = true;
                    SetState(ConnectionState.Connected);

                    // 启动后台异步捕获循环
                    _captureLoop = Task.Run(() => CaptureAndProcessAsync(_loopFinished));
                    return true;
                }
                catch (Exception ex)
                {
                    SetState(ConnectionState.Failed);
                    OnCaptureError?.Invoke(TranslationService.GetStringStatic("CameraError") + $": {ex.Message}");
                    _isRunning = false;
                    _capture?.Release();
                    _capture = null;
                    return false;
                }
            }
        }

        /// <summary>
        /// 尝试打开本地摄像头。
        /// 依次尝试摄像头索引 0 和 1，每种索引尝试 DSHOW → MSMF → ANY API。
        /// </summary>
        private static VideoCapture? OpenLocalCamera()
        {
            int[] cameraIndices = { 0, 1 };
            VideoCaptureAPIs[] apis = { VideoCaptureAPIs.DSHOW, VideoCaptureAPIs.MSMF, VideoCaptureAPIs.ANY };

            foreach (int index in cameraIndices)
            {
                foreach (VideoCaptureAPIs api in apis)
                {
                    VideoCapture? cap = null;
                    try
                    {
                        cap = new VideoCapture(index, api);
                        if (cap.IsOpened())
                            return cap;
                    }
                    catch { /* 当前组合失败，尝试下一个 */ }
                    finally
                    {
                        // 仅在没被返回时才释放，避免泄漏
                        if (cap != null && !cap.IsOpened())
                        {
                            cap.Release();
                            cap.Dispose();
                        }
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// 尝试打开网络视频流。
        /// 使用配置的 URL（支持 rtsp:// 或 http:// 协议）依次尝试不同的 API 后端。
        /// </summary>
        private VideoCapture? OpenNetworkStream()
        {
            if (string.IsNullOrWhiteSpace(_networkUrl))
                return null;

            VideoCaptureAPIs[] apis = { VideoCaptureAPIs.ANY, VideoCaptureAPIs.DSHOW, VideoCaptureAPIs.MSMF };

            foreach (VideoCaptureAPIs api in apis)
            {
                VideoCapture? cap = null;
                try
                {
                    cap = new VideoCapture(_networkUrl, api);
                    if (cap.IsOpened())
                        return cap;
                }
                catch { }
                finally
                {
                    if (cap != null && !cap.IsOpened())
                    {
                        cap.Release();
                        cap.Dispose();
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// 打开录像文件用于回放。
        ///
        /// 文件路径交给 OpenCV 自行解析，因此 mp4/avi/mkv 等容器以及
        /// 图像序列目录都能直接使用，无需在本组件里做格式判断。
        /// 回放专用状态（总帧数、位置）在此处一次性初始化。
        /// </summary>
        private VideoCapture? OpenFileReplay()
        {
            if (string.IsNullOrWhiteSpace(_replayPath))
                return null;

            if (!File.Exists(_replayPath))
            {
                OnCaptureError?.Invoke(
                    TranslationService.GetStringStatic("CameraOpenError") +
                    $": replay file not found: {_replayPath}");
                return null;
            }

            VideoCapture? cap = null;
            try
            {
                cap = new VideoCapture(_replayPath, VideoCaptureAPIs.ANY);
                if (!cap.IsOpened())
                    return null;

                // 文件源的 FPS 属性在部分容器里读不出来，
                // 此时退回回放节流帧率，再不行则不节流。
                double fileFps = cap.Get(VideoCaptureProperties.Fps);
                if (fileFps > 0.01)
                    _replayTargetFps = fileFps;
                else if (_replayTargetFps <= 0)
                    _replayTargetFps = 25.0;

                long total = (long)cap.Get(VideoCaptureProperties.FrameCount);
                Interlocked.Exchange(ref _replayTotalFrames, total > 0 ? total : 0);
                Interlocked.Exchange(ref _replayPosition, 0);

                return cap;
            }
            catch
            {
                cap?.Release();
                return null;
            }
        }

        /// <summary>
        /// 停止视频捕获。
        /// 先通知采集循环退出并等待其结束，再统一释放原生资源，
        /// 以免在 Read() 执行过程中释放 VideoCapture。
        /// </summary>
        public void StopCapture()
        {
            Task? loop;
            ManualResetEventSlim? finished;

            lock (_lock)
            {
                if (!_isRunning && _capture == null)
                {
                    // 采集循环已自行退出并清理完毕
                    return;
                }
                _isRunning = false;
                loop = _captureLoop;
                finished = _loopFinished;
            }

            // 等待采集循环结束（最多 1 秒）。正常情况下循环会在 ~130ms 内退出。
            // 采集线程在退出时不会再反向等待 UI 线程（finished 先于事件触发），
            // 因此这里不会死锁。
            if (loop != null && finished != null && !finished.Wait(1000))
            {
                OnCaptureError?.Invoke(TranslationService.GetStringStatic("CameraError") +
                    ": capture loop did not stop in time");
            }

            // 循环若已自行清理，这里是空操作；否则强制清理
            CleanupCapture();

            bool wasDisconnected;
            lock (_lock)
            {
                wasDisconnected = State == ConnectionState.Disconnected;
                _loopFinished?.Dispose();
                _loopFinished = null;
                _captureLoop = null;
            }

            if (wasDisconnected)
                OnCaptureStopped?.Invoke(null);
        }

        /// <summary>
        /// 统一释放原生资源。用 Interlocked 守卫保证并发调用下只执行一次，
        /// 且重复调用不会重复触发状态事件。
        /// </summary>
        private void CleanupCapture()
        {
            if (Interlocked.Exchange(ref _cleanupDone, 1) == 1)
                return;

            var capture = _capture;
            _capture = null;
            _frame?.Dispose();
            _frame = null;
            _grayFrame?.Dispose();
            _grayFrame = null;
            _isRunning = false;
            try { capture?.Release(); } catch { }
        }

        /// <summary>更新连接状态并通知订阅者（调用方需持有 _lock）</summary>
        private void SetState(ConnectionState state)
        {
            State = state;
            OnConnectionStateChanged?.Invoke(state);
        }

        /// <summary>
        /// 后台异步捕获循环。
        /// 持续从摄像头读取帧 → 转灰度 → 触发事件回调。
        /// 当 _isRunning 为 false 时自动退出并清理资源。
        /// </summary>
        private Task CaptureAndProcessAsync(ManualResetEventSlim finished)
        {
            string? error = null;
            try
            {
                while (_isRunning)
                {
                    var capture = _capture;
                    var frame = _frame;
                    var gray = _grayFrame;
                    if (capture == null || frame == null || gray == null)
                    {
                        Task.Delay(50).GetAwaiter().GetResult();
                        continue;
                    }

                    try
                    {
                        // 读取一帧。文件源读到末尾时 Read() 返回 false 且帧为空，
                        // 此时按 ReplayLoop 决定是停下还是回绕，不能当作异常重试。
                        bool readSuccess = capture.Read(frame);

                        if (!readSuccess || frame.Empty())
                        {
                            if (_sourceType == VideoSourceType.FileReplay)
                            {
                                if (!_replayLoop)
                                {
                                    // 播完即止：正常结束，不是错误
                                    _isRunning = false;
                                    break;
                                }

                                // 回绕到开头。Read() 失败后句柄位置不确定，
                                // 直接再次读取可能仍返回空帧，因此显式重置。
                                // OpenCvSharp 未把 CAP_PROP_POS_FRAMES 暴露为可读写的枚举，
                                // 但 VideoCapture.Set(int, double) 直接透传原生属性码。
                                const int CapPropPosFrames = 1;
                                if (!capture.Set(CapPropPosFrames, 0))
                                {
                                    error = TranslationService.GetStringStatic("CameraError") +
                                            ": failed to rewind replay file";
                                    break;
                                }

                                Interlocked.Exchange(ref _replayPosition, 0);
                                continue;
                            }

                            Task.Delay(100).GetAwaiter().GetResult();
                            continue;
                        }

                        if (_sourceType == VideoSourceType.FileReplay)
                            Interlocked.Increment(ref _replayPosition);

                        // 转灰度后触发回调（同步执行，调用返回前结果已被消费）
                        Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
                        OnFrameCaptured?.Invoke(frame, gray);
                    }
                    catch (Exception ex)
                    {
                        // 摄像头异常断开/驱动错误：上报错误并退出循环
                        error = TranslationService.GetStringStatic("CameraError") + $": {ex.Message}";
                        break;
                    }

                    // 节流。文件源按 ReplayFps 节流以还原录像真实节拍；
                    // 摄像头/网络流保持原来的 ~33 FPS 上限。
                    if (_sourceType == VideoSourceType.FileReplay && _replayTargetFps > 0)
                    {
                        int interval = (int)Math.Round(1000.0 / _replayTargetFps);
                        Task.Delay(interval).GetAwaiter().GetResult();
                    }
                    else
                    {
                        Task.Delay(30).GetAwaiter().GetResult(); // ~33 FPS 上限
                    }
                }
            }
            finally
            {
                // 循环退出后的资源清理（此后已无人再访问原生句柄）
                CleanupCapture();

                // 必须先 Set 再触发任何会 Dispatcher.Invoke 的事件：
                // StopCapture() 在 UI 线程上等待 finished，
                // 若在 Set 之前同步通知订阅者，UI 线程会被反向等待而死锁。
                finished.Set();

                lock (_lock)
                {
                    if (State != ConnectionState.Disconnected)
                        SetState(ConnectionState.Disconnected);
                }
            }

            if (error != null)
                OnCaptureError?.Invoke(error);
            OnCaptureStopped?.Invoke(error == null
                ? TranslationService.GetStringStatic("CameraStopped")
                : null);
            return Task.CompletedTask;
        }

        /// <summary>获取当前视频帧率（读取缓存值，不触碰原生句柄）</summary>
        public double GetFrameRate() => _cachedFps;

        /// <summary>获取当前视频分辨率（读取缓存值，不触碰原生句柄）</summary>
        public System.Windows.Size GetResolution()
            => new System.Windows.Size(_cachedWidth, _cachedHeight);

        /// <summary>获取当前信号源描述信息</summary>
        public string GetSourceInfo()
        {
            return _sourceType switch
            {
                VideoSourceType.LocalCamera => "Local Camera",
                VideoSourceType.FileReplay => _replayPath,
                _ => _networkUrl
            };
        }

        /// <summary>释放所有资源</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            StopCapture();
            CleanupCapture();
            GC.SuppressFinalize(this);
        }
    }
}