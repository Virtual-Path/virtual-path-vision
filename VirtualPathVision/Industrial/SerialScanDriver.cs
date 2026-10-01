using System;
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
        private SerialPort? _port;
        private readonly StringBuilder _buffer = new();
        private readonly object _sync = new();

        public string Name => "串口 (RS232)";

        /// <summary>串口号（如 COM3）</summary>
        public string PortName { get; set; } = "COM1";

        /// <summary>波特率</summary>
        public int BaudRate { get; set; } = 9600;

        public bool IsRunning { get; private set; }

        public event Action<string>? OnBarcodeScanned;
        public event Action<string>? OnError;

        /// <summary>获取系统可用串口列表</summary>
        public static string[] GetPortNames()
        {
            try { return SerialPort.GetPortNames(); }
            catch { return Array.Empty<string>(); }
        }

        /// <summary>打开串口开始监听</summary>
        public Task<bool> StartAsync()
        {
            try
            {
                lock (_sync)
                {
                    _port = new SerialPort(PortName, BaudRate, Parity.None, 8, StopBits.One);
                    _port.DataReceived += OnDataReceived;
                    _port.Open();
                }
                IsRunning = true;
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                IsRunning = false;
                OnError?.Invoke(ex.Message);
                return Task.FromResult(false);
            }
        }

        /// <summary>关闭串口</summary>
        public void Stop()
        {
            lock (_sync)
            {
                try { _port?.Close(); } catch { }
                _port = null;
            }
            IsRunning = false;
        }

        private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            string? data;
            lock (_sync)
            {
                if (_port == null) return;
                try { data = _port.ReadExisting(); }
                catch { return; }
            }

            if (string.IsNullOrEmpty(data)) return;

            foreach (char ch in data)
            {
                if (ch == '\r' || ch == '\n')
                {
                    if (_buffer.Length > 0)
                    {
                        string code = _buffer.ToString().Trim();
                        _buffer.Clear();
                        if (code.Length > 0)
                            OnBarcodeScanned?.Invoke(code);
                    }
                }
                else
                {
                    _buffer.Append(ch);
                }
            }
        }

        public void Dispose()
        {
            Stop();
            GC.SuppressFinalize(this);
        }
    }
}
