using System.IO;
using System.Text;
using Amazon.Lambda;
using Amazon.Lambda.Model;
using System.Text.Json;

namespace VirtualPathVision.Cloud
{
    /// <summary>
    /// AWS Lambda 客户端，用于触发云端事件处理函数。
    /// 典型用途：调用 Lambda 生成检测报告、触发后处理流水线、执行 AI 推理。
    /// </summary>
    public class LambdaClient : IDisposable
    {
        private readonly IAmazonLambda _lambdaClient;
        private bool _disposed;

        /// <summary>调用成功事件</summary>
        public event Action<string>? OnInvocationSuccess;

        /// <summary>调用失败事件</summary>
        public event Action<string, Exception>? OnInvocationError;

        /// <summary>
        /// 初始化 Lambda 客户端。
        /// </summary>
        /// <param name="region">AWS 区域</param>
        public LambdaClient(string region)
        {
            var config = new AmazonLambdaConfig
            {
                RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(region),
                Timeout = TimeSpan.FromSeconds(30)
            };
            _lambdaClient = new AmazonLambdaClient(config);
        }

        /// <summary>
        /// 使用预配置的 Lambda 客户端初始化（用于依赖注入）。
        /// </summary>
        public LambdaClient(IAmazonLambda lambdaClient)
        {
            _lambdaClient = lambdaClient;
        }

        /// <summary>
        /// 同步调用 Lambda 函数并返回结果。
        /// </summary>
        /// <param name="functionName">Lambda 函数名称或 ARN</param>
        /// <param name="payload">请求负载（会被序列化为 JSON）</param>
        /// <returns>Lambda 函数返回的响应</returns>
        public async Task<T?> InvokeAsync<T>(string functionName, object payload)
        {
            try
            {
                string json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });

                var request = new InvokeRequest
                {
                    FunctionName = functionName,
                    InvocationType = InvocationType.RequestResponse,
                    Payload = json
                };

                InvokeResponse response = await _lambdaClient.InvokeAsync(request);

                if (response.StatusCode != 200)
                {
                    using var errorReader = new StreamReader(response.Payload);
                    string errorPayload = await errorReader.ReadToEndAsync();
                    throw new Exception($"Lambda returned status {response.StatusCode}: {errorPayload}");
                }

                using var reader = new StreamReader(response.Payload);
                string resultJson = await reader.ReadToEndAsync();
                var result = JsonSerializer.Deserialize<T>(resultJson, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                OnInvocationSuccess?.Invoke(functionName);
                return result;
            }
            catch (Exception ex)
            {
                OnInvocationError?.Invoke(functionName, ex);
                throw;
            }
        }

        /// <summary>
        /// 异步调用 Lambda 函数（不等待返回结果）。
        /// 适用于事件触发型场景，如通知、日志记录。
        /// </summary>
        public async Task InvokeAsync(string functionName, object payload)
        {
            try
            {
                string json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });

                var request = new InvokeRequest
                {
                    FunctionName = functionName,
                    InvocationType = InvocationType.Event, // 异步调用
                    Payload = json
                };

                await _lambdaClient.InvokeAsync(request);
                OnInvocationSuccess?.Invoke(functionName);
            }
            catch (Exception ex)
            {
                OnInvocationError?.Invoke(functionName, ex);
            }
        }

        /// <summary>
        /// 调用 Lambda 生成检测报告。
        /// </summary>
        /// <param name="functionName">报告生成 Lambda 函数名</param>
        /// <param name="detectionData">检测数据</param>
        /// <returns>报告 S3 URL</returns>
        public async Task<string?> InvokeReportGeneratorAsync(
            string functionName, object detectionData)
        {
            var payload = new
            {
                action = "generate_report",
                data = detectionData,
                format = "pdf"
            };

            var response = await InvokeAsync<ReportResponse>(functionName, payload);
            return response?.ReportUrl;
        }

        /// <summary>
        /// 调用 Lambda 执行 AI 推理（如高级图像分析）。
        /// </summary>
        public async Task<Dictionary<string, object>?> InvokeAiInferenceAsync(
            string functionName, byte[] imageBytes, string modelType = "default")
        {
            var payload = new
            {
                action = "infer",
                image = Convert.ToBase64String(imageBytes),
                model = modelType
            };

            return await InvokeAsync<Dictionary<string, object>>(functionName, payload);
        }

        /// <summary>检查 Lambda 函数是否存在且可调用</summary>
        public async Task<bool> HealthCheckAsync(string functionName)
        {
            try
            {
                await _lambdaClient.GetFunctionAsync(new GetFunctionRequest
                {
                    FunctionName = functionName
                });
                return true;
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _lambdaClient?.Dispose();
            GC.SuppressFinalize(this);
        }

        private class ReportResponse
        {
            public string? ReportUrl { get; set; }
        }
    }
}
