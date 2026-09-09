using System;

namespace MachineVisionApp.Industrial
{
    /// <summary>设备驱动连接状态</summary>
    public enum DeviceDriverState
    {
        Disconnected, // 未连接
        Connecting,   // 连接中
        Connected,    // 已连接
        Failed        // 连接失败
    }

    /// <summary>
    /// 设备协议驱动统一接口（企业化驱动抽象层）。
    /// Modbus TCP、OPC-UA 等现场总线/工业协议驱动均实现该接口，
    /// 上层业务通过接口编程，与具体协议解耦。
    /// </summary>
    public interface IDeviceDriver : IDisposable
    {
        /// <summary>驱动名称（协议名）</summary>
        string Name { get; }

        /// <summary>当前连接状态</summary>
        DeviceDriverState State { get; }

        /// <summary>连接状态变化事件</summary>
        event Action<DeviceDriverState>? OnStateChanged;

        /// <summary>驱动错误事件（参数为错误消息）</summary>
        event Action<string>? OnError;

        /// <summary>异步连接设备</summary>
        Task<bool> ConnectAsync();

        /// <summary>断开连接</summary>
        void Disconnect();
    }
}
