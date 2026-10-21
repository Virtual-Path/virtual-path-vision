using System.Text.Json;
using Amazon;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using OpenCvSharp;
using VirtualPathVision.Cloud;

namespace VirtualPathVision.Competition;

/// <summary>
/// `aws` 命令：把一次运行的结果送到云端。
///
/// <para><b>AWS 里跑的是什么</b>：边缘端（本程序）完成 OpenCV 5 检测与动作决策，
/// 云端只做三件事——</para>
/// <list type="number">
/// <item>S3 保存证据图与评测 JSON（可追溯、可审计）；</item>
/// <item>Lambda 汇总本次运行的缺陷分布，返回批次统计；</item>
/// <item>出现隔离/升级样本时经 SNS 通知质量负责人。</item>
/// </list>
///
/// <para>没有凭据或没有配置桶名时自动退化为 <c>--dry-run</c>：只打印计划，
/// 不发起任何网络请求。凭据使用 AWS SDK 默认链（环境变量 / SSO / 实例角色）。</para>
/// </summary>
public static class AwsCommands
{
    public static int Run(CliOptions options)
    {
        string outDir = Paths.Artifacts(options);
        string region = options.GetEnv("region", "AWS_REGION", "us-east-1");
        string bucket = options.GetEnv("bucket", "VPV_S3_BUCKET", "");
        string lambda = options.GetEnv("lambda", "VPV_LAMBDA_FUNCTION", "");
        string topic = options.GetEnv("topic", "VPV_SNS_TOPIC", "");

        bool dryRun = options.DryRun || string.IsNullOrWhiteSpace(bucket);

        string runId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        string evidenceDir = Path.Combine(outDir, "evidence");

        var evidence = Directory.Exists(evidenceDir)
            ? Directory.GetFiles(evidenceDir, "*.jpg").OrderBy(p => p, StringComparer.Ordinal).ToArray()
            : Array.Empty<string>();

        string evalPath = Path.Combine(outDir, "evaluation.json");
        string summaryPath = Path.Combine(outDir, "agent-summary.json");

        var payload = new
        {
            runId,
            generatedAt = DateTime.UtcNow.ToString("O"),
            region,
            evidenceCount = evidence.Length,
            evaluation = ReadJson(evalPath),
            agent = ReadJson(summaryPath)
        };

        if (dryRun)
        {
            Console.WriteLine("[aws] DRY RUN —— 未配置 S3 桶或显式指定 --dry-run");
            Console.WriteLine($"[aws] region={region} bucket={(bucket == "" ? "(未配置)" : bucket)} lambda={(lambda == "" ? "(未配置)" : lambda)} topic={(topic == "" ? "(未配置)" : topic)}");
            Console.WriteLine($"[aws] 计划上传 {evidence.Length} 张证据图 + {Path.GetFileName(evalPath)} + {Path.GetFileName(summaryPath)}");
            foreach (string file in evidence.Take(5))
                Console.WriteLine($"[aws]   s3://{bucket}/detections/.../{Path.GetFileName(file)}");
            if (evidence.Length > 5) Console.WriteLine($"[aws]   ... 其余 {evidence.Length - 5} 张");

            WriteReceipt(outDir, new
            {
                mode = "dry-run",
                runId,
                region,
                bucket,
                lambda,
                topic,
                evidenceCount = evidence.Length,
                reason = dryRun && string.IsNullOrWhiteSpace(bucket)
                    ? "VPV_S3_BUCKET 未配置"
                    : "--dry-run"
            });

            return 0;
        }

        try
        {
            using var s3 = new S3Service(region, bucket);
            var uploaded = new List<string>();

            foreach (string file in evidence)
            {
                using Mat image = Cv2.ImRead(file, ImreadModes.Color);
                if (image.Empty()) continue;
                string key = s3.UploadDetectionScreenshotAsync(image, "evidence").GetAwaiter().GetResult();
                uploaded.Add(key);
            }

            if (File.Exists(evalPath))
            {
                string key = s3.UploadReportAsync(File.ReadAllText(evalPath), $"evaluation-{runId}.json")
                    .GetAwaiter().GetResult();
                uploaded.Add(key);
            }

            if (File.Exists(summaryPath))
            {
                string key = s3.UploadReportAsync(File.ReadAllText(summaryPath), $"agent-summary-{runId}.json")
                    .GetAwaiter().GetResult();
                uploaded.Add(key);
            }

            Console.WriteLine($"[aws] 已上传 {uploaded.Count} 个对象到 s3://{bucket}");

            Dictionary<string, JsonElement>? lambdaResult = null;
            if (!string.IsNullOrWhiteSpace(lambda))
            {
                using var client = new LambdaClient(region);
                lambdaResult = client.InvokeAsync<Dictionary<string, JsonElement>>(lambda, payload)
                    .GetAwaiter().GetResult();
                Console.WriteLine($"[aws] Lambda `{lambda}` 返回 {JsonSerializer.Serialize(lambdaResult)}");
            }

            string? messageId = null;
            int escalated = CountEscalations(summaryPath);
            if (!string.IsNullOrWhiteSpace(topic) && escalated > 0)
            {
                using var sns = new AmazonSimpleNotificationServiceClient(
                    RegionEndpoint.GetBySystemName(region));

                var response = sns.PublishAsync(new PublishRequest
                {
                    TopicArn = topic,
                    Subject = $"Vision inspection {runId}: {escalated} item(s) need review",
                    Message = $"Run {runId}\n escalated={escalated}\n artifacts: s3://{bucket}/detections/"
                }).GetAwaiter().GetResult();

                messageId = response.MessageId;
                Console.WriteLine($"[aws] SNS 已发送 messageId={messageId}");
            }

            WriteReceipt(outDir, new
            {
                mode = "aws",
                runId,
                region,
                bucket,
                lambda,
                topic,
                evidenceCount = evidence.Length,
                uploaded,
                lambdaResult,
                snsMessageId = messageId
            });

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[aws] 失败: {ex.GetType().Name}: {ex.Message}");
            WriteReceipt(outDir, new
            {
                mode = "error",
                runId,
                region,
                bucket,
                error = ex.Message
            });
            return 4;
        }
    }

    private static JsonElement? ReadJson(string path)
    {
        if (!File.Exists(path)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.Clone();
    }

    private static int CountEscalations(string summaryPath)
    {
        if (!File.Exists(summaryPath)) return 0;
        using var doc = JsonDocument.Parse(File.ReadAllText(summaryPath));
        if (!doc.RootElement.TryGetProperty("byAction", out var actions)) return 0;

        int total = 0;
        foreach (string name in new[] { "Quarantine", "Escalate" })
        {
            if (actions.TryGetProperty(name, out var value) && value.TryGetInt32(out int count))
                total += count;
        }
        return total;
    }

    private static void WriteReceipt(string outDir, object receipt)
    {
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "aws-receipt.json"),
            JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }));
    }
}
