using System.Globalization;
using System.IO;
using Amazon.S3;
using Amazon.S3.Model;
using OpenCvSharp;

namespace VirtualPathVision.Cloud
{
    /// <summary>
    /// AWS S3 存储服务，负责将检测截图、录像和报告上传到 S3 存储桶。
    /// 支持按日期分目录组织、预签名 URL 生成。
    /// </summary>
    public class S3Service : IDisposable
    {
        private readonly IAmazonS3 _s3Client;
        private readonly string _bucketName;
        private readonly bool _ownsClient;
        private bool _disposed;

        /// <summary>S3 上传成功事件</summary>
        public event Action<string>? OnUploadSuccess;

        /// <summary>S3 上传失败事件</summary>
        public event Action<string, Exception>? OnUploadError;

        /// <summary>
        /// 初始化 S3 服务。
        /// </summary>
        /// <param name="region">AWS 区域（如 "ap-northeast-1"）</param>
        /// <param name="bucketName">S3 存储桶名称</param>
        /// <exception cref="ArgumentException">区域名称无法识别</exception>
        public S3Service(string region, string bucketName)
        {
            var config = new AmazonS3Config
            {
                RegionEndpoint = ResolveRegion(region),
                Timeout = TimeSpan.FromSeconds(30),
                MaxErrorRetry = 3
            };
            _s3Client = new AmazonS3Client(config);
            _bucketName = bucketName;
            _ownsClient = true;
        }

        /// <summary>
        /// 使用预配置的 S3 客户端初始化（用于依赖注入）。
        /// 该构造函数不拥有传入的客户端，调用方负责其生命周期，Dispose 不会释放它。
        /// </summary>
        public S3Service(IAmazonS3 s3Client, string bucketName)
        {
            _s3Client = s3Client;
            _bucketName = bucketName;
            _ownsClient = false;
        }

        /// <summary>
        /// 上传检测截图到 S3。
        /// 路径格式：detections/{yyyy-MM-dd}/{HHmmss-fff}_{mode}_{唯一后缀}.jpg
        /// </summary>
        /// <param name="frame">检测结果帧</param>
        /// <param name="mode">当前处理模式名称</param>
        /// <returns>S3 对象键</returns>
        /// <exception cref="ArgumentException">帧为空</exception>
        /// <exception cref="InvalidOperationException">OpenCV 图像编码失败</exception>
        public async Task<string> UploadDetectionScreenshotAsync(Mat frame, string mode)
        {
            if (frame is null || frame.Empty())
                throw new ArgumentException("检测结果帧为空，无法上传到 S3", nameof(frame));

            string key = GenerateKey("detections", mode, "jpg");

            // Mat → byte[]，编码失败时不能再上传 0 字节的假 JPEG
            if (!Cv2.ImEncode(".jpg", frame, out byte[] imageBytes) || imageBytes.Length == 0)
                throw new InvalidOperationException(
                    $"OpenCV 图像编码失败（.jpg），已中止上传（帧尺寸: {frame.Width}x{frame.Height}）");

            using var stream = new MemoryStream(imageBytes);
            var request = new PutObjectRequest
            {
                BucketName = _bucketName,
                Key = key,
                InputStream = stream,
                ContentType = "image/jpeg",
                Metadata =
                {
                    ["detection-mode"] = mode,
                    ["timestamp"] = DateTime.UtcNow.ToString("O")
                }
            };

            await ExecuteUploadAsync(request, key);
            return key;
        }

        /// <summary>
        /// 上传视频录像文件到 S3。
        /// 路径格式：recordings/{yyyy-MM-dd}/{HHmmss-fff}_{唯一后缀}.avi
        /// </summary>
        /// <param name="filePath">本地录像文件路径</param>
        /// <returns>S3 对象键</returns>
        public async Task<string> UploadRecordingAsync(string filePath)
        {
            ThrowIfDisposed();
            string key = GenerateKey("recordings", null, "avi");

            var request = new PutObjectRequest
            {
                BucketName = _bucketName,
                Key = key,
                FilePath = filePath,
                ContentType = "video/avi"
            };

            await ExecuteUploadAsync(request, key);
            return key;
        }

        /// <summary>
        /// 上传文本报告（JSON）到 S3。
        /// 路径格式：reports/{yyyy-MM-dd}/{reportName}.json
        /// </summary>
        public async Task<string> UploadReportAsync(string reportJson, string reportName)
        {
            ThrowIfDisposed();
            string date = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string key = $"reports/{date}/{reportName}.json";
            byte[] data = System.Text.Encoding.UTF8.GetBytes(reportJson);

            using var stream = new MemoryStream(data);
            var request = new PutObjectRequest
            {
                BucketName = _bucketName,
                Key = key,
                InputStream = stream,
                ContentType = "application/json"
            };

            await ExecuteUploadAsync(request, key);
            return key;
        }

        /// <summary>
        /// 生成预签名 URL（有效期 1 小时），用于临时访问 S3 对象。
        /// </summary>
        /// <exception cref="ObjectDisposedException">服务已释放</exception>
        public string GetPreSignedUrl(string objectKey)
        {
            ThrowIfDisposed();

            var request = new GetPreSignedUrlRequest
            {
                BucketName = _bucketName,
                Key = objectKey,
                Expires = DateTime.UtcNow.AddHours(1)
            };
            return _s3Client.GetPreSignedURL(request);
        }

        /// <summary>
        /// 列出指定前缀下的所有对象键。
        /// </summary>
        public async Task<List<string>> ListObjectsAsync(string prefix)
        {
            ThrowIfDisposed();

            var keys = new List<string>();
            var request = new ListObjectsV2Request
            {
                BucketName = _bucketName,
                Prefix = prefix,
                MaxKeys = 1000
            };

            ListObjectsV2Response response;
            do
            {
                response = await _s3Client.ListObjectsV2Async(request);
                keys.AddRange(response.S3Objects.Select(o => o.Key));
                request.ContinuationToken = response.NextContinuationToken;
            } while (response.IsTruncated == true);

            return keys;
        }

        /// <summary>检查存储桶是否可访问</summary>
        public async Task<bool> HealthCheckAsync()
        {
            try
            {
                await _s3Client.GetBucketLocationAsync(_bucketName);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 执行上传。事件在 try/catch 之外触发，订阅者异常不会被误报为上传失败。
        /// </summary>
        private async Task ExecuteUploadAsync(PutObjectRequest request, string key)
        {
            ThrowIfDisposed();

            try
            {
                await _s3Client.PutObjectAsync(request);
            }
            catch (Exception ex)
            {
                OnUploadError?.Invoke(key, ex);
                throw;
            }

            OnUploadSuccess?.Invoke(key);
        }

        /// <summary>
        /// 生成对象键。时间精确到毫秒并附加随机后缀，避免同一秒内同类别同模式的上传互相覆盖。
        /// </summary>
        private static string GenerateKey(string category, string? mode, string ext)
        {
            DateTime now = DateTime.UtcNow;
            string date = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string time = now.ToString("HHmmss-fff", CultureInfo.InvariantCulture);
            string suffix = string.IsNullOrEmpty(mode) ? "" : $"_{mode}";
            string unique = Guid.NewGuid().ToString("N")[..6];
            return $"{category}/{date}/{time}{suffix}_{unique}.{ext}";
        }

        /// <summary>
        /// 解析区域端点。区域无效时抛出明确异常，避免 SDK 静默回退到默认区域。
        /// </summary>
        private static Amazon.RegionEndpoint ResolveRegion(string region)
        {
            if (string.IsNullOrWhiteSpace(region))
                throw new ArgumentException("AWS 区域不能为空", nameof(region));

            var endpoint = Amazon.RegionEndpoint.GetBySystemName(region);
            if (endpoint is null)
                throw new ArgumentException($"未知的 AWS 区域: '{region}'", nameof(region));

            return endpoint;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(S3Service));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // 依赖注入的客户端由调用方负责释放，此处只释放自身创建的实例
            if (_ownsClient)
                _s3Client?.Dispose();

            GC.SuppressFinalize(this);
        }
    }
}