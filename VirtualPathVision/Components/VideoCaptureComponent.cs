using System;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;

namespace VirtualPathVision.Components
{
    /// <summary>视频信号源类型：本地摄像头 / 网络视频流</summary>
    public enum VideoSourceType
    {
        LocalCamera,   // 本地 USB 摄像头
        NetworkStream  // 网络 RTSP / HTTP MJPEG 流
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
                    _capture = _sourceType == VideoSourceType.LocalCamera
                        ? OpenLocalCamera()
                        : OpenNetworkStream();

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
                        // 读取一帧
                        bool readSuccess = capture.Read(frame);
                        if (!readSuccess || frame.Empty())
                        {
                            Task.Delay(100).GetAwaiter().GetResult();
                            continue;
                        }

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

                    Task.Delay(30).GetAwaiter().GetResult(); // ~33 FPS 上限
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
            if (_sourceType == VideoSourceType.LocalCamera)
                return "Local Camera";
            return _networkUrl;
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