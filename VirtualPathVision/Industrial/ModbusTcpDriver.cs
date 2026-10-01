using System;
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
    public class ModbusTcpDriver : IDeviceDriver
    {
        private string _ip;
        private int _port;
        private byte _unitId;
        private readonly object _sync = new();
        private TcpClient? _client;
        private IModbusMaster? _master;

        public string Name => "Modbus TCP";

        /// <summary>PLC IP 地址</summary>
        public string Ip => _ip;

        /// <summary>Modbus 端口（默认 502）</summary>
        public int Port => _port;

        /// <summary>从站地址（Unit ID）</summary>
        public byte UnitId => _unitId;

        public DeviceDriverState State { get; private set; } = DeviceDriverState.Disconnected;

        public event Action<DeviceDriverState>? OnStateChanged;
        public event Action<string>? OnError;

        public ModbusTcpDriver(string ip, int port, byte unitId)
        {
            _ip = ip;
            _port = port;
            _unitId = unitId;
        }

        /// <summary>更新连接参数（先断开再应用，下次连接生效）</summary>
        public void UpdateSettings(string ip, int port, byte unitId)
        {
            Disconnect();
            _ip = ip;
            _port = port;
            _unitId = unitId;
        }

        /// <summary>异步连接 PLC</summary>
        public async Task<bool> ConnectAsync()
        {
            if (State == DeviceDriverState.Connected)
                return true;

            SetState(DeviceDriverState.Connecting);
            try
            {
                var client = new TcpClient();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await client.ConnectAsync(_ip, _port, cts.Token);
                if (!client.Connected)
                {
                    client.Dispose();
                    SetState(DeviceDriverState.Failed);
                    return false;
                }

                var master = new ModbusFactory().CreateMaster(client);

                lock (_sync)
                {
                    Cleanup();
                    _client = client;
                    _master = master;
                }

                SetState(DeviceDriverState.Connected);
                return true;
            }
            catch (Exception ex)
            {
                SetState(DeviceDriverState.Failed);
                OnError?.Invoke(ex.Message);
                return false;
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
                if (_master == null) throw new InvalidOperationException("Modbus 未连接");
                return _master.ReadHoldingRegisters(_unitId, startAddress, numberOfPoints);
            }
        }

        /// <summary>读取线圈（功能码 01）</summary>
        public bool[] ReadCoils(ushort startAddress, ushort numberOfPoints)
        {
            lock (_sync)
            {
                if (_master == null) throw new InvalidOperationException("Modbus 未连接");
                return _master.ReadCoils(_unitId, startAddress, numberOfPoints);
            }
        }

        /// <summary>写单个保持寄存器（功能码 06）</summary>
        public void WriteSingleRegister(ushort address, ushort value)
        {
            lock (_sync)
            {
                if (_master == null) throw new InvalidOperationException("Modbus 未连接");
                _master.WriteSingleRegister(_unitId, address, value);
            }
        }

        /// <summary>写单个线圈（功能码 05）</summary>
        public void WriteSingleCoil(ushort address, bool value)
        {
            lock (_sync)
            {
                if (_master == null) throw new InvalidOperationException("Modbus 未连接");
                _master.WriteSingleCoil(_unitId, address, value);
            }
        }

        private void Cleanup()
        {
            try { _master?.Dispose(); } catch { }
            _master = null;
            try { _client?.Close(); } catch { }
            _client = null;
        }

        private void SetState(DeviceDriverState state)
        {
            State = state;
            OnStateChanged?.Invoke(state);
        }

        public void Dispose()
        {
            Disconnect();
            GC.SuppressFinalize(this);
        }
    }
}
