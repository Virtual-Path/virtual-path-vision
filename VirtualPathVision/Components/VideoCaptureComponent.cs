using System;
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
    /// </summary>
    public class VideoCaptureComponent : IDisposable
    {
        private VideoCapture? _capture;   // OpenCV 视频捕获对象
        private Mat? _frame;              // 原始帧
        private Mat? _grayFrame;          // 灰度帧
        private bool _isRunning;          // 捕获循环运行标志
        private readonly object _lock = new(); // 多线程锁
        private bool _disposed;           // 是否已释放
        private VideoSourceType _sourceType = VideoSourceType.LocalCamera;
        private string _networkUrl = "";

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
                if (_isRunning)
                    return true;

                try
                {
                    State = ConnectionState.Connecting;
                    OnConnectionStateChanged?.Invoke(State);

                    // 根据信号源类型选择打开方式
                    if (_sourceType == VideoSourceType.LocalCamera)
                        _capture = OpenLocalCamera();
                    else
                        _capture = OpenNetworkStream();

                    // 检查摄像头是否成功打开
                    if (_capture == null || !_capture.IsOpened())
                    {
                        State = ConnectionState.Failed;
                        OnConnectionStateChanged?.Invoke(State);
                        OnCaptureError?.Invoke(TranslationService.GetStringStatic("CameraOpenError"));
                        _capture?.Release();
                        _capture = null;
                        return false;
                    }

                    _frame = new Mat();
                    _grayFrame = new Mat();
                    _isRunning = true;
                    State = ConnectionState.Connected;
                    OnConnectionStateChanged?.Invoke(State);

                    // 启动后台异步捕获循环
                    _ = CaptureAndProcessAsync();
                    return true;
                }
                catch (Exception ex)
                {
                    State = ConnectionState.Failed;
                    OnConnectionStateChanged?.Invoke(State);
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
                    try
                    {
                        var cap = new VideoCapture(index, api);
                        if (cap.IsOpened())
                            return cap;
                        cap.Release();
                    }
                    catch { /* 当前组合失败，尝试下一个 */ }
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
                try
                {
                    var cap = new VideoCapture(_networkUrl, api);
                    if (cap.IsOpened())
                        return cap;
                    cap.Release();
                }
                catch { }
            }
            return null;
        }

        /// <summary>停止视频捕获，释放摄像头资源</summary>
        public void StopCapture()
        {
            lock (_lock)
            {
                if (!_isRunning)
                    return;

                _isRunning = false;
                _capture?.Release();
                _capture = null;

                State = ConnectionState.Disconnected;
                OnConnectionStateChanged?.Invoke(State);
                OnCaptureStopped?.Invoke(null);
            }
        }

        /// <summary>
        /// 后台异步捕获循环。
        /// 持续从摄像头读取帧 → 转灰度 → 触发事件回调。
        /// 当 _isRunning 为 false 时自动退出并清理资源。
        /// </summary>
        private async Task CaptureAndProcessAsync()
        {
            while (_isRunning)
            {
                if (_capture == null || _frame == null || _grayFrame == null)
                {
                    await Task.Delay(50);
                    continue;
                }

                try
                {
                    // 读取一帧
                    bool readSuccess = _capture.Read(_frame);
                    if (!readSuccess || _frame.Empty())
                    {
                        await Task.Delay(100);
                        continue;
                    }

                    // 转灰度后触发回调
                    Cv2.CvtColor(_frame, _grayFrame, ColorConversionCodes.BGR2GRAY);
                    OnFrameCaptured?.Invoke(_frame, _grayFrame);
                }
                catch (Exception ex)
                {
                    // 摄像头异常断开/驱动错误：上报错误并退出循环
                    OnCaptureError?.Invoke(TranslationService.GetStringStatic("CameraError") + $": {ex.Message}");
                    break;
                }

                await Task.Delay(30); // ~33 FPS 上限
            }

            // 循环退出后的资源清理
            lock (_lock)
            {
                try { _capture?.Release(); _capture = null; } catch { }
                _isRunning = false;
                if (State != ConnectionState.Disconnected)
                {
                    State = ConnectionState.Disconnected;
                    OnConnectionStateChanged?.Invoke(State);
                }
                OnCaptureStopped?.Invoke(TranslationService.GetStringStatic("CameraStopped"));
            }
        }

        /// <summary>获取当前视频帧率</summary>
        public double GetFrameRate()
        {
            return _capture?.Get(VideoCaptureProperties.Fps) ?? 0;
        }

        /// <summary>获取当前视频分辨率</summary>
        public System.Windows.Size GetResolution()
        {
            if (_capture == null) return new System.Windows.Size(0, 0);
            int width = (int)_capture.Get(VideoCaptureProperties.FrameWidth);
            int height = (int)_capture.Get(VideoCaptureProperties.FrameHeight);
            return new System.Windows.Size(width, height);
        }

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
            _frame?.Dispose();
            _grayFrame?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
