using System.IO;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace VirtualPathVision.Cloud
{
    /// <summary>
    /// AWS IoT Core 服务，通过 IoT Data Plane REST API 发布 MQTT 消息。
    /// 使用 X.509 客户端证书认证（IoT Device Certificate），必须加载私钥才能完成 mTLS 握手。
    /// </summary>
    public class IoTService : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly string _iotEndpoint;
        private readonly string _topicPrefix;
        private readonly bool _ownsHttpClient;
        private readonly X509Certificate2? _certificate;
        private bool _disposed;

        /// <summary>消息发布成功事件</summary>
        public event Action<string>? OnPublishSuccess;

        /// <summary>消息发布失败事件</summary>
        public event Action<string, Exception>? OnPublishError;

        /// <summary>
        /// 初始化 IoT Core 服务（使用设备证书 + 私钥进行 mTLS 认证）。
        /// </summary>
        /// <param name="iotEndpoint">IoT Data Plane 端点（如 "xxx-ats.iot.us-east-1.amazonaws.com"）</param>
        /// <param name="certificatePath">设备证书文件路径（.pem.crt）</param>
        /// <param name="privateKeyPath">私钥文件路径（.pem.key）</param>
        /// <param name="topicPrefix">IoT Topic 前缀（如 "factory/vision"）</param>
        /// <exception cref="FileNotFoundException">证书或私钥文件不存在</exception>
        /// <exception cref="CryptographicException">证书或私钥无法解析，或不包含私钥</exception>
        public IoTService(
            string iotEndpoint,
            string certificatePath,
            string privateKeyPath,
            string topicPrefix = "factory/vision")
        {
            if (string.IsNullOrWhiteSpace(certificatePath))
                throw new ArgumentException("证书文件路径不能为空", nameof(certificatePath));
            if (string.IsNullOrWhiteSpace(privateKeyPath))
                throw new ArgumentException("私钥文件路径不能为空", nameof(privateKeyPath));

            _iotEndpoint = iotEndpoint.TrimEnd('/');
            _topicPrefix = topicPrefix;

            _certificate = LoadDeviceCertificate(certificatePath, privateKeyPath);

            // SocketsHttpHandler 才能配置 ALPN（HttpClientHandler 未暴露 SslOptions）
            var handler = new SocketsHttpHandler
            {
                SslOptions = new SslClientAuthenticationOptions
                {
                    ClientCertificates = new X509CertificateCollection { _certificate },
                    // AWS IoT Data Plane 在 443 端口上要求 ALPN 协商 "x-amzn-http-ca"
                    ApplicationProtocols = new List<SslApplicationProtocol>
                    {
                        new SslApplicationProtocol("x-amzn-http-ca")
                    }
                }
            };

            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(10)
            };
            _ownsHttpClient = true;
        }

        /// <summary>
        /// 使用已配置的 HttpClient 初始化（用于依赖注入或测试）。
        /// 该构造函数不拥有传入的 HttpClient，调用方负责其生命周期，Dispose 不会释放它。
        /// </summary>
        public IoTService(HttpClient httpClient, string iotEndpoint, string topicPrefix = "factory/vision")
        {
            _httpClient = httpClient;
            _iotEndpoint = iotEndpoint.TrimEnd('/');
            _topicPrefix = topicPrefix;
            _ownsHttpClient = false;
            _certificate = null;
        }

        /// <summary>
        /// 加载设备证书并绑定私钥。
        /// 优先使用 X509Certificate2 的文件构造函数，若该运行时不支持 PEM 私钥则回退到 CreateFromPemFile。
        /// </summary>
        private static X509Certificate2 LoadDeviceCertificate(string certificatePath, string privateKeyPath)
        {
            if (!File.Exists(certificatePath))
                throw new FileNotFoundException($"未找到 IoT 设备证书文件: {certificatePath}", certificatePath);
            if (!File.Exists(privateKeyPath))
                throw new FileNotFoundException($"未找到 IoT 设备私钥文件: {privateKeyPath}", privateKeyPath);

            X509Certificate2? certificate = null;
            Exception? firstFailure = null;

            // 路径一：证书 + 私钥（部分运行时/文件格式下可用）
            try
            {
                certificate = new X509Certificate2(certificatePath, privateKeyPath);
            }
            catch (Exception ex)
            {
                firstFailure = ex;
            }

            // 路径二：PEM 证书 + PEM 私钥
            if (certificate is null)
            {
                try
                {
                    certificate = X509Certificate2.CreateFromPemFile(certificatePath, privateKeyPath);
                }
                catch (Exception ex)
                {
                    throw new CryptographicException(
                        $"无法加载 IoT 设备证书与私钥（证书: {certificatePath}，私钥: {privateKeyPath}）。" +
                        "请确认文件为 PEM 格式，且私钥与证书匹配。原因: " +
                        $"{firstFailure?.Message} | {ex.Message}", ex);
                }
            }

            if (!certificate.HasPrivateKey)
            {
                certificate.Dispose();
                throw new CryptographicException(
                    $"IoT 设备证书未包含私钥，mTLS 认证无法完成（证书: {certificatePath}，私钥: {privateKeyPath}）");
            }

            return certificate;
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

            string topic = $"{_topicPrefix}/detection/{mode.ToLowerInvariant()}";
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
                source = "VirtualPathVision"
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
        /// IoT Data Plane 的发布操作仅支持 POST，因此此处使用 POST + 最小 JSON 体进行探测。
        /// </summary>
        public async Task<bool> HealthCheckAsync()
        {
            try
            {
                string url = $"https://{_iotEndpoint}/topics/health/ping?qos=0";
                using var content = new StringContent("{\"ping\":1}", Encoding.UTF8, "application/json");
                using var response = await _httpClient.PostAsync(url, content);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 发布消息。失败时向调用方抛出异常（不再静默吞掉）。
        /// 成功/失败事件在 try/catch 之外触发，避免订阅者异常被误判为发布失败。
        /// </summary>
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
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var response = await _httpClient.PostAsync(url, content);
                response.EnsureSuccessStatusCode();
            }
            catch (Exception ex)
            {
                OnPublishError?.Invoke(topic, ex);
                throw;
            }

            OnPublishSuccess?.Invoke(topic);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // 依赖注入的 HttpClient 由调用方负责释放，此处只释放自身创建的实例
            if (_ownsHttpClient)
                _httpClient?.Dispose();

            _certificate?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}