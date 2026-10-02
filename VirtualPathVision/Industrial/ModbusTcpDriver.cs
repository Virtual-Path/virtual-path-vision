using System;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NModbus;

namespace VirtualPathVision.Industrial
{
    /// <summary>
    /// Modbus TCP 主站驱动。
    /// 支持读写保持寄存器（功能码 03/06）和线圈（功能码 01/05），
    /// 用于与西门子 S7-1200/1500、汇川、三菱等 PLC 直连。
    /// </summary>
    /// <remarks>
    /// 线程安全约定：所有状态字段与 _master/_client 的安装、释放都在 <c>_sync</c> 内完成，
    /// 但 <see cref="OnStateChanged"/> / <see cref="OnError"/> 一律在锁外触发。
    /// 原因：宿主（MainWindow）用 <c>Dispatcher.Invoke</c> 订阅这两个事件，
    /// 若在锁内触发就会形成“后台线程持锁等 UI 线程、UI 线程等锁”的死锁。
    /// </remarks>
    public class ModbusTcpDriver : IDeviceDriver
    {
        /// <summary>默认读写超时（毫秒）</summary>
        public const int DefaultTimeoutMilliseconds = 3000;

        /// <summary>允许的超时上限（毫秒），防止误填成无限等待</summary>
        public const int MaxTimeoutMilliseconds = 600_000;

        private readonly object _sync = new();
        private string _ip;
        private int _port;
        private byte _unitId;
        private int _timeoutMilliseconds = DefaultTimeoutMilliseconds;
        private DeviceDriverState _state = DeviceDriverState.Disconnected;
        private TcpClient? _client;
        private IModbusMaster? _master;
        private bool _disposed;

        public string Name => "Modbus TCP";

        /// <summary>PLC IP 地址</summary>
        public string Ip
        {
            get { lock (_sync) return _ip; }
        }

        /// <summary>Modbus 端口（默认 502）</summary>
        public int Port
        {
            get { lock (_sync) return _port; }
        }

        /// <summary>从站地址（Unit ID）</summary>
        public byte UnitId
        {
            get { lock (_sync) return _unitId; }
        }

        /// <summary>
        /// 同步读写的超时（毫秒），必须为有限正值。
        /// NModbus 的 <c>IModbusTransport.ReadTimeout/WriteTimeout</c> 默认为
        /// <see cref="Timeout.Infinite"/>，PLC 无响应时会把调用线程（通常是 UI 线程）永久挂死，
        /// 因此这里强制为有限值，并在每次连接时写入底层传输层。
        /// </summary>
        public int TimeoutMilliseconds
        {
            get { lock (_sync) return _timeoutMilliseconds; }
            set
            {
                if (value <= 0 || value > MaxTimeoutMilliseconds)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(value), value,
                        $"Modbus 超时必须在 1..{MaxTimeoutMilliseconds} 毫秒之间");
                }
                lock (_sync)
                {
                    _timeoutMilliseconds = value;
                }
            }
        }

        public DeviceDriverState State
        {
            get { lock (_sync) return _state; }
        }

        public event Action<DeviceDriverState>? OnStateChanged;
        public event Action<string>? OnError;

        /// <summary>
        /// 创建驱动。参数非法时立即抛出 <see cref="ArgumentOutOfRangeException"/>，
        /// 避免把框架原始异常推迟到连接阶段才暴露。
        /// </summary>
        public ModbusTcpDriver(string ip, int port, byte unitId)
        {
            _ip = ValidateIp(ip, nameof(ip));
            _port = ValidatePort(port, nameof(port));
            _unitId = unitId;
        }

        /// <summary>
        /// 更新连接参数（先断开再应用，下次连接生效）。
        /// 参数非法时先做校验再断开，避免错误输入把一个正常工作的连接踢掉。
        /// </summary>
        public void UpdateSettings(string ip, int port, byte unitId)
        {
            string newIp = ValidateIp(ip, nameof(ip));
            int newPort = ValidatePort(port, nameof(port));

            Disconnect();

            lock (_sync)
            {
                _ip = newIp;
                _port = newPort;
                _unitId = unitId;
            }
        }

        /// <summary>异步连接 PLC</summary>
        public async Task<bool> ConnectAsync()
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_state == DeviceDriverState.Connected)
                    return true;
            }

            string ip;
            int port;
            int timeoutMs;
            lock (_sync)
            {
                ip = _ip;
                port = _port;
                timeoutMs = _timeoutMilliseconds;
            }

            SetState(DeviceDriverState.Connecting);

            // 由本方法独占，接管成功后置空；失败路径在 finally 中释放，避免 TcpClient 泄漏
            TcpClient? client = null;
            IModbusMaster? master = null;
            try
            {
                client = new TcpClient();
                // 兜底：即便底层传输层未生效，Socket 自身的收发超时也保证不会无限等待
                client.ReceiveTimeout = timeoutMs;
                client.SendTimeout = timeoutMs;

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await client.ConnectAsync(ip, port, cts.Token).ConfigureAwait(false);

                // 不再检查 client.Connected：它只是最后一次 I/O 状态的缓存值，
                // 既不能证明链路可用（可能早已断开），也不能证明链路不可用。
                // ConnectAsync 正常返回即代表三次握手成功，真正的可用性由后续带超时的读写验证。

                master = new ModbusFactory().CreateMaster(client);

                // 关键修复：显式设置有限超时，避免 NModbus 默认 Timeout.Infinite 挂死 UI 线程
                if (master.Transport is { } transport)
                {
                    transport.ReadTimeout = timeoutMs;
                    transport.WriteTimeout = timeoutMs;
                }

                lock (_sync)
                {
                    // 先释放旧连接再安装新的，保证任意时刻只有一组 _master/_client 有效
                    Cleanup();
                    _client = client;
                    _master = master;
                }
                client = null;   // 所有权已移交给 _client
                master = null;

                SetState(DeviceDriverState.Connected);
                return true;
            }
            catch (Exception ex)
            {
                SetState(DeviceDriverState.Failed);
                OnError?.Invoke(ex.Message);
                return false;
            }
            finally
            {
                // 失败路径：释放尚未被接管的资源
                DisposeQuietly(master, "主站");
                DisposeQuietly(client, "连接");
            }
        }

        /// <summary>断开连接</summary>
        public void Disconnect()
        {
            lock (_sync)
            {
                Cleanup();
            }
            SetState(DeviceDriverState.Disconnected);
        }

        /// <summary>读取保持寄存器（功能码 03）</summary>
        public ushort[] ReadHoldingRegisters(ushort startAddress, ushort numberOfPoints)
        {
            lock (_sync)
            {
                IModbusMaster master = RequireMaster();
                byte unitId = _unitId;
                return master.ReadHoldingRegisters(unitId, startAddress, numberOfPoints);
            }
        }

        /// <summary>读取线圈（功能码 01）</summary>
        public bool[] ReadCoils(ushort startAddress, ushort numberOfPoints)
        {
            lock (_sync)
            {
                IModbusMaster master = RequireMaster();
                byte unitId = _unitId;
                return master.ReadCoils(unitId, startAddress, numberOfPoints);
            }
        }

        /// <summary>写单个保持寄存器（功能码 06）</summary>
        public void WriteSingleRegister(ushort address, ushort value)
        {
            lock (_sync)
            {
                IModbusMaster master = RequireMaster();
                byte unitId = _unitId;
                master.WriteSingleRegister(unitId, address, value);
            }
        }

        /// <summary>写单个线圈（功能码 05）</summary>
        public void WriteSingleCoil(ushort address, bool value)
        {
            lock (_sync)
            {
                IModbusMaster master = RequireMaster();
                byte unitId = _unitId;
                master.WriteSingleCoil(unitId, address, value);
            }
        }

        private IModbusMaster RequireMaster()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _master ?? throw new InvalidOperationException("Modbus 未连接");
        }

        private static string ValidateIp(string ip, string paramName)
        {
            if (string.IsNullOrWhiteSpace(ip))
                throw new ArgumentOutOfRangeException(paramName, ip, "PLC IP 地址不能为空");
            string trimmed = ip.Trim();
            if (!System.Net.IPAddress.TryParse(trimmed, out _))
                throw new ArgumentOutOfRangeException(paramName, ip, $"无效的 PLC IP 地址: {ip}");
            return trimmed;
        }

        private static int ValidatePort(int port, string paramName)
        {
            if (port < 1 || port > 65535)
            {
                throw new ArgumentOutOfRangeException(
                    paramName, port, $"Modbus 端口必须在 1..65535 之间，当前值: {port}");
            }
            return port;
        }

        /// <summary>
        /// 释放当前连接。必须在 <c>_sync</c> 内调用；
        /// 先把字段置空再释放资源，避免其他线程拿到已释放对象。
        /// </summary>
        private void Cleanup()
        {
            IModbusMaster? master = _master;
            TcpClient? client = _client;
            _master = null;
            _client = null;

            DisposeQuietly(master, "主站");
            DisposeQuietly(client, "连接");
        }

        private static void DisposeQuietly(IDisposable? resource, string what)
        {
            if (resource == null)
                return;
            try
            {
                resource.Dispose();
            }
            catch (Exception ex)
            {
                // 释放失败只记调试日志，不向上抛：清理路径不应掩盖原始异常
                Debug.WriteLine($"[ModbusTcpDriver] 释放{what}失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 更新状态并在锁外触发事件（订阅方会同步切回 UI 线程，锁内触发会死锁）。
        /// 相同状态的重复通知会被合并。
        /// </summary>
        private void SetState(DeviceDriverState state)
        {
            lock (_sync)
            {
                if (_state == state)
                    return;
                _state = state;
            }
            OnStateChanged?.Invoke(state);
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                    return;
                _disposed = true;
            }
            Disconnect();
            GC.SuppressFinalize(this);
        }
    }
}
