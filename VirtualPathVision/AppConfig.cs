namespace VirtualPathVision
{
    /// <summary>
    /// appsettings.json 的 AI 段落。
    /// 此前这些键写在配置文件里却没有任何代码读取，属于死配置；
    /// 现已接入，实际生效。
    /// </summary>
    public class AiConfig
    {
        /// <summary>YOLO 模型路径（留空则需在 UI 中手动选择）</summary>
        public string YoloModelPath { get; set; } = "";

        /// <summary>置信度阈值 (0, 1]</summary>
        public double ConfidenceThreshold { get; set; } = 0.5;

        /// <summary>NMS 阈值 (0, 1]</summary>
        public double NmsThreshold { get; set; } = 0.45;

        /// <summary>网络输入边长（正方形）</summary>
        public int InputSize { get; set; } = 640;

        /// <summary>目标连续丢失多少帧后从跟踪列表中移除</summary>
        public int MaxLostFrames { get; set; } = 10;
    }

    /// <summary>
    /// appsettings.json 的 AWS 段落。
    /// 此前同样是死配置；现已用于 Cloud 面板的初始回填与 IoT Topic 前缀。
    /// </summary>
    public class AwsConfig
    {
        public string Region { get; set; } = "";
        public string S3Bucket { get; set; } = "";
        public string IoTEndpoint { get; set; } = "";

        /// <summary>设备证书路径（.pem.crt），留空则使用 BaseDirectory/certs 下的默认路径</summary>
        public string IoTCertificatePath { get; set; } = "";

        /// <summary>设备私钥路径（.pem.key），留空则使用 BaseDirectory/certs 下的默认路径</summary>
        public string IoTPrivateKeyPath { get; set; } = "";

        /// <summary>IoT Topic 前缀</summary>
        public string IoTTopicPrefix { get; set; } = "factory/vision";

        public string LambdaFunctionName { get; set; } = "";
    }

    /// <summary>应用级配置根节点</summary>
    public class AppConfig
    {
        public AiConfig AI { get; set; } = new();
        public AwsConfig AWS { get; set; } = new();
    }
}