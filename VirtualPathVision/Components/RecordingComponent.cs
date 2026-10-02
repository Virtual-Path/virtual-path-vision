using System;
using OpenCvSharp;

namespace VirtualPathVision.Components
{
    /// <summary>
    /// 视频录制组件，将处理后的帧写入 AVI 文件。
    /// 使用 OpenCV VideoWriter，编码格式为 MJPG。
    /// </summary>
    public class RecordingComponent : IDisposable
    {
        private VideoWriter? _writer;
        private string? _outputPath;
        private volatile bool _isRecording;
        private readonly object _lock = new();
        private bool _disposed;

        // 写入分辨率（用于检测运行中分辨率变化导致的静默丢帧）
        private int _frameWidth;
        private int _frameHeight;

        /// <summary>录制过程中因分辨率不匹配而丢弃的帧数</summary>
        public long DroppedFrames { get; private set; }

        /// <summary>是否正在录制</summary>
        public bool IsRecording => _isRecording;

        /// <summary>当前输出文件路径</summary>
        public string? OutputPath => _outputPath;

        /// <summary>录制状态变更事件</summary>
        public event Action<bool>? OnRecordingStateChanged;

        /// <summary>
        /// 开始录制视频。
        /// </summary>
        /// <param name="filePath">输出文件路径（.avi）</param>
        /// <param name="fps">帧率</param>
        /// <param name="width">画面宽度</param>
        /// <param name="height">画面高度</param>
        /// <returns>是否成功启动</returns>
        public bool StartRecording(string filePath, double fps, int width, int height)
        {
            bool started;
            lock (_lock)
            {
                if (_disposed || _isRecording)
                    return false;

                try
                {
                    int fourCC = VideoWriter.FourCC('M', 'J', 'P', 'G');
                    _writer = new VideoWriter(filePath, fourCC, fps, new OpenCvSharp.Size(width, height));

                    if (!_writer.IsOpened())
                    {
                        _writer.Dispose();
                        _writer = null;
                        return false;
                    }

                    _outputPath = filePath;
                    _frameWidth = width;
                    _frameHeight = height;
                    DroppedFrames = 0;
                    _isRecording = true;
                    started = true;
                }
                catch
                {
                    _writer?.Dispose();
                    _writer = null;
                    _isRecording = false;
                    return false;
                }
            }

            // 在锁外触发事件，避免订阅者阻塞时卡住写帧线程
            if (started)
                OnRecordingStateChanged?.Invoke(true);
            return started;
        }

        /// <summary>写入一帧到视频文件</summary>
        public void WriteFrame(Mat frame)
        {
            if (!_isRecording || frame == null || frame.Empty())
                return;

            // 全部访问都在锁内完成：避免 StopRecording 并发把 _writer 置空
            lock (_lock)
            {
                var writer = _writer;
                if (!_isRecording || writer == null)
                    return;

                // 运行中分辨率变化会让 VideoWriter 静默丢弃每一帧，必须显式拦截并计数
                if (frame.Width != _frameWidth || frame.Height != _frameHeight)
                {
                    DroppedFrames++;
                    return;
                }

                try
                {
                    if (!writer.Write(frame))
                        DroppedFrames++;
                }
                catch
                {
                    DroppedFrames++;
                }
            }
        }

        /// <summary>停止录制并关闭文件</summary>
        public void StopRecording()
        {
            lock (_lock)
            {
                if (!_isRecording)
                    return;

                _isRecording = false;
                var writer = _writer;
                _writer = null;
                // Release 释放原生资源，Dispose 释放托管包装（两者都需要）
                try { writer?.Release(); } catch { }
                writer?.Dispose();
            }

            OnRecordingStateChanged?.Invoke(false);
        }

        /// <summary>释放资源</summary>
        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
            }
            StopRecording();
            GC.SuppressFinalize(this);
        }
    }
}