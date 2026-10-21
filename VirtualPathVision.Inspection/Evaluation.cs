using System.Text;
using System.Text.Json;
using OpenCvSharp;

namespace VirtualPathVision.Inspection;

/// <summary>评测汇总。</summary>
public sealed record EvaluationReport(
    string DatasetDir,
    int Total,
    int TruePositive,
    int FalsePositive,
    int TrueNegative,
    int FalseNegative,
    double Precision,
    double Recall,
    double F1,
    double Accuracy,
    double ClassAccuracy,
    double MeanIou,
    double LatencyMeanMs,
    double LatencyP50Ms,
    double LatencyP95Ms,
    double ThroughputFps,
    IReadOnlyDictionary<string, ClassStat> PerClass,
    IReadOnlyList<FailureCase> Failures)
{
    /// <summary>把报告渲染为 Markdown 表格，供技术报告直接引用。</summary>
    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine("| 指标 | 数值 |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| 样本数 | {Total} |");
        sb.AppendLine($"| Precision | {Precision:P2} |");
        sb.AppendLine($"| Recall | {Recall:P2} |");
        sb.AppendLine($"| F1 | {F1:P2} |");
        sb.AppendLine($"| 图像级准确率 | {Accuracy:P2} |");
        sb.AppendLine($"| 缺陷分类准确率（TP 内） | {ClassAccuracy:P2} |");
        sb.AppendLine($"| 定位平均 IoU | {MeanIou:F3} |");
        sb.AppendLine($"| 单帧延迟 p50 / p95 | {LatencyP50Ms:F2} ms / {LatencyP95Ms:F2} ms |");
        sb.AppendLine($"| 吞吐 | {ThroughputFps:F1} FPS |");
        sb.AppendLine();
        sb.AppendLine("| 类别 | 样本 | 检出 | 召回 |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var (name, stat) in PerClass)
            sb.AppendLine($"| {name} | {stat.Total} | {stat.Detected} | {stat.Recall:P2} |");
        return sb.ToString();
    }
}

/// <summary>单个类别的统计。</summary>
public sealed record ClassStat(int Total, int Detected, double Recall);

/// <summary>一个被判错的样本。</summary>
public sealed record FailureCase(string File, string Truth, string Predicted, double Score, string Note);

/// <summary>
/// 批量评测：在带标注的数据集上运行 <see cref="DefectDetector"/>，
/// 产出图像级混淆矩阵、分类准确率、定位 IoU 与时延统计。
///
/// <para><b>为什么时延要报 p50/p95 而不是均值</b>：产线节拍由尾部延迟决定，
/// 均值会被大量快帧拉低，掩盖偶发的慢帧。</para>
/// </summary>
public static class EvaluationRunner
{
    /// <summary>读取数据集并执行评测。</summary>
    public static EvaluationReport Run(
        string datasetDir,
        DetectorOptions? options = null,
        int maxSamples = int.MaxValue)
    {
        options ??= new DetectorOptions();
        string labelsPath = Path.Combine(datasetDir, "labels.jsonl");
        if (!File.Exists(labelsPath))
            throw new FileNotFoundException("数据集缺少 labels.jsonl", labelsPath);

        var labels = File.ReadLines(labelsPath)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => JsonSerializer.Deserialize<DatasetLabel>(l)!)
            .Take(maxSamples)
            .ToList();

        var detector = new DefectDetector();
        var latencies = new List<double>(labels.Count);
        var failures = new List<FailureCase>();
        var perClass = new Dictionary<string, (int Total, int Detected)>(StringComparer.Ordinal);

        int tp = 0, fp = 0, tn = 0, fn = 0;
        int classCorrect = 0, classTotal = 0;
        double iouSum = 0;
        int iouCount = 0;

        foreach (var label in labels)
        {
            string path = Path.Combine(datasetDir, label.File);
            using Mat frame = Cv2.ImRead(path, ImreadModes.Grayscale);
            if (frame.Empty())
            {
                failures.Add(new FailureCase(label.File, label.Label, "read-error", 0, "图像无法读取"));
                continue;
            }

            var result = detector.Detect(frame, options);
            latencies.Add(result.ElapsedMs);

            bool truthNg = label.Label != "good";
            bool predNg = result.IsNg;

            if (truthNg && predNg) tp++;
            else if (!truthNg && predNg) fp++;
            else if (!truthNg && !predNg) tn++;
            else fn++;

            string predicted = predNg ? result.Defect.ToString().ToLowerInvariant() : "good";

            perClass.TryGetValue(label.Label, out var stat);
            perClass[label.Label] = (stat.Total + 1, stat.Detected + (predNg ? 1 : 0));

            if (truthNg && predNg)
            {
                classTotal++;
                if (string.Equals(predicted, label.Label, StringComparison.OrdinalIgnoreCase))
                    classCorrect++;

                if (label.Bbox is { Length: 4 })
                {
                    var truth = new Rect(label.Bbox[0], label.Bbox[1], label.Bbox[2], label.Bbox[3]);
                    var pred = result.Findings[0].Bounds;
                    double iou = IoU(truth, pred);
                    iouSum += iou;
                    iouCount++;
                    if (iou < 0.1)
                    {
                        failures.Add(new FailureCase(label.File, label.Label, predicted,
                            result.Score, $"定位偏差 IoU={iou:F2}"));
                    }
                }
            }
            else if (truthNg != predNg)
            {
                failures.Add(new FailureCase(label.File, label.Label, predicted,
                    result.Score, truthNg ? "漏检" : "误报"));
            }
            else if (truthNg && predNg && !string.Equals(predicted, label.Label, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add(new FailureCase(label.File, label.Label, predicted,
                    result.Score, "类别错分"));
            }
        }

        double precision = tp + fp == 0 ? 0 : (double)tp / (tp + fp);
        double recall = tp + fn == 0 ? 0 : (double)tp / (tp + fn);
        double f1 = precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall);
        double accuracy = labels.Count == 0 ? 0 : (double)(tp + tn) / (tp + tn + fp + fn);

        latencies.Sort();
        double p50 = Percentile(latencies, 0.50);
        double p95 = Percentile(latencies, 0.95);
        double mean = latencies.Count == 0 ? 0 : latencies.Average();

        var perClassOut = perClass.ToDictionary(
            kv => kv.Key,
            kv => new ClassStat(kv.Value.Total, kv.Value.Detected,
                kv.Value.Total == 0 ? 0 : (double)kv.Value.Detected / kv.Value.Total),
            StringComparer.Ordinal);

        return new EvaluationReport(
            datasetDir, labels.Count, tp, fp, tn, fn,
            precision, recall, f1, accuracy,
            classTotal == 0 ? 0 : (double)classCorrect / classTotal,
            iouCount == 0 ? 0 : iouSum / iouCount,
            mean, p50, p95,
            mean <= 0 ? 0 : 1000.0 / mean,
            perClassOut,
            failures);
    }

    /// <summary>把报告写入 JSON 与 Markdown，返回两个文件路径。</summary>
    public static (string Json, string Markdown) Save(EvaluationReport report, string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        string jsonPath = Path.Combine(outputDir, "evaluation.json");
        string mdPath = Path.Combine(outputDir, "evaluation.md");

        File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
        File.WriteAllText(mdPath, report.ToMarkdown(), new UTF8Encoding(false));

        return (jsonPath, mdPath);
    }

    private static double Percentile(List<double> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        int index = (int)Math.Ceiling(p * sorted.Count) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }

    /// <summary>交并比；用于判断预测框是否落在标注框附近。</summary>
    private static double IoU(Rect a, Rect b)
    {
        int x1 = Math.Max(a.Left, b.Left);
        int y1 = Math.Max(a.Top, b.Top);
        int x2 = Math.Min(a.Right, b.Right);
        int y2 = Math.Min(a.Bottom, b.Bottom);

        int iw = Math.Max(0, x2 - x1);
        int ih = Math.Max(0, y2 - y1);
        double inter = iw * ih;
        double union = (double)a.Width * a.Height + (double)b.Width * b.Height - inter;
        return union <= 0 ? 0 : inter / union;
    }
}
