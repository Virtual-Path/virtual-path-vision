using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Ports;
using System.Text;
using System.Threading.Tasks;

namespace VirtualPathVision.Industrial
{
    /// <summary>
    /// 串口扫码源驱动（RS232/RS485）。
    /// 适用于串口条码枪、RFID 读卡器等设备，
    /// 数据以回车/换行结尾作为一条码记录上报。
    /// </summary>
    public class SerialScanDriver : IScanSource
    {
        /// <summary>单行扫码数据的最大字符数，超过视为异常数据并丢弃该行</summary>
        public const int MaxLineLength = 4096;

        private readonly object _sync = new();
        private readonly StringBuilder _buffer = new();

        private SerialPort? _port;
        private string _portName = "COM1";
        private int _baudRate = 9600;
        private volatile bool _isRunning;
        private bool _disposed;

        public string Name => "串口 (RS232)";

        /// <summary>串口号（如 COM3）。由 DataReceived 线程读取，故加锁保护</summary>
        public string PortName
        {
            get { lock (_sync) return _portName; }
            set { lock (_sync) _portName = value ?? ""; }
        }

        /// <summary>波特率。由 DataReceived 线程读取，故加锁保护</summary>
        public int BaudRate
        {
            get { lock (_sync) return _baudRate; }
            set { lock (_sync) _baudRate = value; }
        }

        public bool IsRunning => _isRunning;

        public event Action<string>? OnBarcodeScanned;
        public event Action<string>? OnError;

        /// <summary>获取系统可用串口列表</summary>
        public static string[] GetPortNames()
        {
            try { return SerialPort.GetPortNames(); }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SerialScanDriver] 枚举串口失败: {ex.Message}");
                return Array.Empty<string>();
            }
        }

        /// <summary>打开串口开始监听</summary>
        public Task<bool> StartAsync()
        {
            SerialPort? newPort = null;
            try
            {
                lock (_sync)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);

                    // 重复启动保护：先释放可能已存在的旧端口，否则旧句柄/事件订阅会泄漏
                    ClosePort();

                    newPort = new SerialPort(_portName, _baudRate, Parity.None, 8, StopBits.One);
                    newPort.DataReceived += OnDataReceived;
                    newPort.Open();

                    _port = newPort;
                    _buffer.Clear();
                }
                _isRunning = true;
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                // 打开失败：解绑并释放半成品端口，避免占用 COM 口
                if (newPort != null)
                {
                    try { newPort.DataReceived -= OnDataReceived; } catch { /* 已未打开，忽略 */ }
                    try { newPort.Dispose(); } catch { /* 忽略 */ }
                }
                lock (_sync)
                {
                    _port = null;
                }
                _isRunning = false;
                OnError?.Invoke(ex.Message);
                return Task.FromResult(false);
            }
        }

        /// <summary>关闭串口</summary>
        public void Stop()
        {
            _isRunning = false;
            lock (_sync)
            {
                ClosePort();
                _buffer.Clear();
            }
        }

        /// <summary>关闭并释放当前串口。必须在 _sync 内调用</summary>
        private void ClosePort()
        {
            SerialPort? port = _port;
            _port = null;
            if (port == null)
                return;

            // 先解绑事件：串口释放后若仍订阅，DataReceived 可能访问到已释放对象
            try { port.DataReceived -= OnDataReceived; }
            catch (Exception ex) { Debug.WriteLine($"[SerialScanDriver] 解绑 DataReceived 失败: {ex.Message}"); }

            try { port.Close(); }
            catch (Exception ex) { Debug.WriteLine($"[SerialScanDriver] 关闭串口失败: {ex.Message}"); }

            // Close 只停用通信，Dispose 才真正释放底层句柄
            try { port.Dispose(); }
            catch (Exception ex) { Debug.WriteLine($"[SerialScanDriver] 释放串口失败: {ex.Message}"); }
        }

        private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            string? data = null;
            string? readError = null;
            lock (_sync)
            {
                SerialPort? port = _port;
                if (port == null)
                    return;
                try
                {
                    data = port.ReadExisting();
                }
                catch (Exception ex)
                {
                    // 不再静默丢弃：串口被拔出/占用会表现为这里的异常，必须让上层看到
                    readError = $"串口读取失败: {ex.Message}";
                }
            }

            if (readError != null)
            {
                // 事件在锁外触发，避免订阅者切回 UI 线程时与本线程互等
                Debug.WriteLine($"[SerialScanDriver] {readError}");
                OnError?.Invoke(readError);
                return;
            }

            if (string.IsNullOrEmpty(data))
                return;

            var codes = new List<string>();
            string? overflow = null;
            lock (_sync)
            {
                foreach (char ch in data!)  // 此处 data 已由上面的空值检查保证非 null
                {
                    if (ch == '\r' || ch == '\n')
                    {
                        if (_buffer.Length > 0)
                        {
                            string code = _buffer.ToString().Trim();
                            _buffer.Clear();
                            if (code.Length > 0)
                                codes.Add(code);
                        }
                    }
                    else
                    {
                        if (_buffer.Length >= MaxLineLength)
                        {
                            // 未见结束符的超长数据：丢弃该行并上报，避免缓冲区无限增长
                            _buffer.Clear();
                            overflow = $"扫码数据超过 {MaxLineLength} 字符且未见行结束符，已丢弃";
                            break;
                        }
                        _buffer.Append(ch);
                    }
                }
            }

            if (overflow != null)
                OnError?.Invoke(overflow);

            // 事件在锁外触发：UI 订阅者会同步切回 UI 线程，锁内触发有死锁风险
            foreach (string code in codes)
            {
                try
                {
                    OnBarcodeScanned?.Invoke(code);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SerialScanDriver] 条码回调异常: {ex.Message}");
                    OnError?.Invoke($"条码回调异常: {ex.Message}");
                }
            }
        }

        public void Dispose()
        {
            Stop();
            lock (_sync)
            {
                _disposed = true;
            }
            GC.SuppressFinalize(this);
        }
    }
}
