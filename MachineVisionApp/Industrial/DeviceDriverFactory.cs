using System;

namespace MachineVisionApp.Industrial
{
    /// <summary>
    /// 设备驱动工厂：按协议名创建驱动实例。
    /// 新增协议驱动时只需在工厂中注册，业务代码无需修改（开闭原则）。
    /// </summary>
    public static class DeviceDriverFactory
    {
        /// <summary>支持的协议名常量</summary>
        public const string ProtocolModbus = "modbus";
        public const string ProtocolOpcUa = "opcua";

        /// <summary>
        /// 根据协议名创建驱动实例。
        /// </summary>
        /// <param name="protocol">协议名（modbus / opcua）</param>
        /// <param name="config">工业互联配置</param>
        public static IDeviceDriver Create(string protocol, IndustrialConfig config)
        {
            return protocol.ToLowerInvariant() switch
            {
                ProtocolModbus => new ModbusTcpDriver(
                    config.Modbus.Ip, config.Modbus.Port, config.Modbus.UnitId),
                ProtocolOpcUa => new OpcUaDriver(config.OpcUa.Endpoint),
                _ => throw new NotSupportedException($"不支持的协议: {protocol}")
            };
        }
    }
}
