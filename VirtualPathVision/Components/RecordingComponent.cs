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
        private bool _isRecording;
        private readonly object _lock = new();

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
            lock (_lock)
            {
                if (_isRecording)
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
                    _isRecording = true;
                    OnRecordingStateChanged?.Invoke(true);
                    return true;
                }
                catch
                {
                    _writer?.Dispose();
                    _writer = null;
                    return false;
                }
            }
        }

        /// <summary>写入一帧到视频文件</summary>
        public void WriteFrame(Mat frame)
        {
            if (!_isRecording || _writer == null)
                return;

            lock (_lock)
            {
                if (_isRecording && _writer != null)
                {
                    _writer.Write(frame);
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
                _writer?.Release();
                _writer = null;
                OnRecordingStateChanged?.Invoke(false);
            }
        }

        /// <summary>释放资源</summary>
        public void Dispose()
        {
            StopRecording();
            GC.SuppressFinalize(this);
        }
    }
}
