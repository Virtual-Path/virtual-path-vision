using System;
using System.Threading.Tasks;

namespace MachineVisionApp.Industrial
{
    /// <summary>
    /// 扫码数据源统一接口（条码枪 / RFID 读卡器等设备协议）。
    /// 串口、网络 TCP、摄像头视觉解码等来源均实现该接口，
    /// 扫码结果统一以条码字符串上报给报工服务。
    /// </summary>
    public interface IScanSource : IDisposable
    {
        /// <summary>数据源名称</summary>
        string Name { get; }

        /// <summary>是否正在监听</summary>
        bool IsRunning { get; }

        /// <summary>扫到条码事件（参数为条码内容）</summary>
        event Action<string>? OnBarcodeScanned;

        /// <summary>数据源错误事件（参数为错误消息）</summary>
        event Action<string>? OnError;

        /// <summary>启动监听</summary>
        Task<bool> StartAsync();

        /// <summary>停止监听</summary>
        void Stop();
    }
}
