using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace MachineVisionApp.Cloud
{
    /// <summary>
    /// AWS IoT Core 服务，通过 IoT Data Plane REST API 发布 MQTT 消息。
    /// 使用 X.509 客户端证书认证（IoT Device Certificate）。
    /// </summary>
    public class IoTService : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly string _iotEndpoint;
        private readonly string _topicPrefix;
        private bool _disposed;

        /// <summary>消息发布成功事件</summary>
        public event Action<string>? OnPublishSuccess;

        /// <summary>消息发布失败事件</summary>
        public event Action<string, Exception>? OnPublishError;

        /// <summary>
        /// 初始化 IoT Core 服务。
        /// </summary>
        /// <param name="iotEndpoint">IoT Data Plane 端点（如 "xxx-ats.iot.us-east-1.amazonaws.com"）</param>
        /// <param name="certificatePath">设备证书文件路径（.pem.crt）</param>
        /// <param name="privateKeyPath">私钥文件路径（.pem.key）</param>
        /// <param name="topicPrefix">IoT Topic 前缀（如 "factory/vision"）</param>
        public IoTService(
            string iotEndpoint,
            string certificatePath,
            string privateKeyPath,
            string topicPrefix = "factory/vision")
        {
            _iotEndpoint = iotEndpoint.TrimEnd('/');
            _topicPrefix = topicPrefix;

            var cert = new X509Certificate2(certificatePath);
            var handler = new HttpClientHandler();
            handler.ClientCertificates.Add(cert);

            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(10)
            };
        }

        /// <summary>
        /// 使用已配置的 HttpClient 初始化（用于依赖注入或测试）。
        /// </summary>
        public IoTService(HttpClient httpClient, string iotEndpoint, string topicPrefix = "factory/vision")
        {
            _httpClient = httpClient;
            _iotEndpoint = iotEndpoint.TrimEnd('/');
            _topicPrefix = topicPrefix;
        }

        /// <summary>
        /// 发布检测结果到 IoT Core。
        /// Topic 格式：{prefix}/detection/{mode}
        /// </summary>
        public async Task PublishDetectionResultAsync(
            string mode, int detectionCount, float confidence,
            Dictionary<string, object>? metadata = null)
        {
            var payload = new
            {
                timestamp = DateTime.UtcNow.ToString("O"),
                mode,
                detectionCount,
                confidence,
                metadata
            };

            string topic = $"{_topicPrefix}/detection/{mode.ToLower()}";
            await PublishAsync(topic, payload);
        }

        /// <summary>
        /// 发布检测告警到 IoT Core。
        /// </summary>
        public async Task PublishAlertAsync(
            string alertType, int severity, string message, string? imageUrl = null)
        {
            var payload = new
            {
                timestamp = DateTime.UtcNow.ToString("O"),
                alertType,
                severity = Math.Clamp(severity, 1, 5),
                message,
                imageUrl,
                source = "MachineVisionApp"
            };

            string topic = $"{_topicPrefix}/alert/{alertType}";
            await PublishAsync(topic, payload);
        }

        /// <summary>
        /// 发布生产统计数据到 IoT Core。
        /// </summary>
        public async Task PublishProductionStatsAsync(
            int totalProcessed, int defectCount, double passRate)
        {
            var payload = new
            {
                timestamp = DateTime.UtcNow.ToString("O"),
                totalProcessed,
                defectCount,
                passRate = Math.Round(passRate, 4),
                stationId = Environment.MachineName
            };

            string topic = $"{_topicPrefix}/production/stats";
            await PublishAsync(topic, payload);
        }

        /// <summary>
        /// 发布自定义 JSON 消息到指定 Topic。
        /// </summary>
        public async Task PublishRawAsync(string topicSuffix, object payload)
        {
            string topic = $"{_topicPrefix}/{topicSuffix}";
            await PublishAsync(topic, payload);
        }

        /// <summary>
        /// 检查 IoT Core 连接是否可用。
        /// </summary>
        public async Task<bool> HealthCheckAsync()
        {
            try
            {
                string url = $"https://{_iotEndpoint}/topics/health/ping";
                var response = await _httpClient.GetAsync(url);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        private async Task PublishAsync(string topic, object payload)
        {
            try
            {
                string json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = false
                });

                string url = $"https://{_iotEndpoint}/topics/{Uri.EscapeDataString(topic)}?qos=1";
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync(url, content);
                response.EnsureSuccessStatusCode();

                OnPublishSuccess?.Invoke(topic);
            }
            catch (Exception ex)
            {
                OnPublishError?.Invoke(topic, ex);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _httpClient?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
