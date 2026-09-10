using System.IO;
using Amazon.S3;
using Amazon.S3.Model;
using OpenCvSharp;

namespace MachineVisionApp.Cloud
{
    /// <summary>
    /// AWS S3 存储服务，负责将检测截图、录像和报告上传到 S3 存储桶。
    /// 支持按日期分目录组织、预签名 URL 生成。
    /// </summary>
    public class S3Service : IDisposable
    {
        private readonly IAmazonS3 _s3Client;
        private readonly string _bucketName;
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
        public S3Service(string region, string bucketName)
        {
            var config = new AmazonS3Config
            {
                RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(region),
                Timeout = TimeSpan.FromSeconds(30),
                MaxErrorRetry = 3
            };
            _s3Client = new AmazonS3Client(config);
            _bucketName = bucketName;
        }

        /// <summary>
        /// 使用预配置的 S3 客户端初始化（用于依赖注入）。
        /// </summary>
        public S3Service(IAmazonS3 s3Client, string bucketName)
        {
            _s3Client = s3Client;
            _bucketName = bucketName;
        }

        /// <summary>
        /// 上传检测截图到 S3。
        /// 路径格式：detections/{yyyy-MM-dd}/{HHmmss}_{mode}.jpg
        /// </summary>
        /// <param name="frame">检测结果帧</param>
        /// <param name="mode">当前处理模式名称</param>
        /// <returns>S3 对象键</returns>
        public async Task<string> UploadDetectionScreenshotAsync(Mat frame, string mode)
        {
            string key = GenerateKey("detections", mode, "jpg");

            // Mat → byte[]
            Cv2.ImEncode(".jpg", frame, out byte[] imageBytes);

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
        /// 路径格式：recordings/{yyyy-MM-dd}/{HHmmss}.avi
        /// </summary>
        /// <param name="filePath">本地录像文件路径</param>
        /// <returns>S3 对象键</returns>
        public async Task<string> UploadRecordingAsync(string filePath)
        {
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
        /// </summary>
        public async Task<string> UploadReportAsync(string reportJson, string reportName)
        {
            string key = $"reports/{DateTime.UtcNow:yyyy-MM-dd}/{reportName}.json";
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
        public string GetPreSignedUrl(string objectKey)
        {
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

        private async Task ExecuteUploadAsync(PutObjectRequest request, string key)
        {
            try
            {
                await _s3Client.PutObjectAsync(request);
                OnUploadSuccess?.Invoke(key);
            }
            catch (Exception ex)
            {
                OnUploadError?.Invoke(key, ex);
                throw;
            }
        }

        private static string GenerateKey(string category, string? mode, string ext)
        {
            string date = DateTime.UtcNow.ToString("yyyy-MM-dd");
            string time = DateTime.UtcNow.ToString("HHmmss");
            string suffix = string.IsNullOrEmpty(mode) ? "" : $"_{mode}";
            return $"{category}/{date}/{time}{suffix}.{ext}";
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _s3Client?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
