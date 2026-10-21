using System.Text.Json;
using VirtualPathVision.Inspection;

namespace VirtualPathVision.Competition;

/// <summary>数据集的生成与批量评测。</summary>
public static class DatasetCommands
{
    /// <summary>数据集不存在时才生成。</summary>
    public static int EnsureDataset(CliOptions options)
    {
        string dir = Paths.Dataset(options);
        return File.Exists(Path.Combine(dir, "labels.jsonl")) ? 0 : Generate(options);
    }

    /// <summary>`dataset` 命令：生成可控合成数据集。</summary>
    public static int Generate(CliOptions options)
    {
        string dir = Paths.Dataset(options);
        int count = options.GetInt("count", 800);
        int seed = options.GetInt("seed", 20261026);

        Console.WriteLine($"[dataset] 生成 {count} 张样本 → {dir} (seed={seed})");
        var manifest = new SyntheticDataset().Generate(dir, count, seed);

        Console.WriteLine($"[dataset] 完成 good={manifest.Good} scratch={manifest.Scratch} " +
                          $"spot={manifest.Spot} stain={manifest.Stain} size={manifest.Width}x{manifest.Height}");
        return 0;
    }

    /// <summary>`eval` 命令：批量评测 + 灵敏度扫描。</summary>
    public static int Evaluate(CliOptions options)
    {
        string dir = Paths.Dataset(options);
        string outDir = Paths.Artifacts(options);

        if (!File.Exists(Path.Combine(dir, "labels.jsonl")))
        {
            Console.Error.WriteLine($"[eval] 数据集不存在: {dir}，先运行 dataset 命令");
            return 1;
        }

        double sensitivity = options.GetDouble("sensitivity", 0.6);

        Console.WriteLine($"[eval] sensitivity={sensitivity:F2} dataset={dir}");
        var report = EvaluationRunner.Run(dir, new DetectorOptions { Sensitivity = sensitivity });
        var (jsonPath, mdPath) = EvaluationRunner.Save(report, outDir);

        Console.WriteLine();
        Console.WriteLine(report.ToMarkdown());
        Console.WriteLine($"[eval] 写入 {jsonPath}");
        Console.WriteLine($"[eval] 写入 {mdPath}");

        // 灵敏度扫描：证明阈值不是拍脑袋选的，同时展示精度/召回权衡
        double[] sweepValues = { 0.35, 0.45, 0.55, 0.65, 0.75 };
        var sweep = new List<SweepPoint>(sweepValues.Length);

        foreach (double s in sweepValues)
        {
            var r = Math.Abs(s - sensitivity) < 1e-9
                ? report
                : EvaluationRunner.Run(dir, new DetectorOptions { Sensitivity = s });

            sweep.Add(new SweepPoint(s, r.Precision, r.Recall, r.F1, r.Accuracy,
                r.LatencyP50Ms, r.LatencyP95Ms));
            Console.WriteLine($"[sweep] sens={s:F2} P={r.Precision:P1} R={r.Recall:P1} F1={r.F1:P1} p95={r.LatencyP95Ms:F1}ms");
        }

        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "sweep.json"),
            JsonSerializer.Serialize(sweep, new JsonSerializerOptions { WriteIndented = true }));

        var md = new System.Text.StringBuilder();
        md.AppendLine("| Sensitivity | Precision | Recall | F1 | Accuracy | p50 (ms) | p95 (ms) |");
        md.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var p in sweep)
            md.AppendLine($"| {p.Sensitivity:F2} | {p.Precision:P2} | {p.Recall:P2} | {p.F1:P2} | {p.Accuracy:P2} | {p.P50Ms:F2} | {p.P95Ms:F2} |");
        File.WriteAllText(Path.Combine(outDir, "sweep.md"), md.ToString());

        return 0;
    }

    /// <summary>一行灵敏度扫描结果。</summary>
    public sealed record SweepPoint(
        double Sensitivity, double Precision, double Recall, double F1,
        double Accuracy, double P50Ms, double P95Ms);

    /// <summary>`probe` 命令：单张图的检测明细，用于排查误报/漏检。</summary>
    public static int Probe(CliOptions options)
    {
        string datasetDir = Paths.Dataset(options);
        string outDir = Paths.Artifacts(options);
        int index = options.GetInt("index", 0);

        OpenCvSharp.Mat frame;
        DatasetLabel label;
        string tag;

        if (options.Get("label") is { Length: > 0 } syntheticLabel)
        {
            // 直接渲染指定种子，便于复现测试里的样本
            int seed = options.GetInt("seed", 0);
            (frame, int[]? bbox) = new SyntheticDataset().RenderSample(syntheticLabel, seed, out int usedSeed);
            label = new DatasetLabel($"synthetic/{syntheticLabel}@{seed}", syntheticLabel, bbox, usedSeed);
            tag = $"{syntheticLabel}-{seed}";
        }
        else
        {
            var labels = File.ReadLines(Path.Combine(datasetDir, "labels.jsonl"))
                .Select(l => System.Text.Json.JsonSerializer.Deserialize<DatasetLabel>(l)!)
                .ToList();

            if (index < 0 || index >= labels.Count)
            {
                Console.Error.WriteLine($"[probe] index 越界: {index} (0..{labels.Count - 1})");
                return 1;
            }

            label = labels[index];
            frame = OpenCvSharp.Cv2.ImRead(
                Path.Combine(datasetDir, label.File), OpenCvSharp.ImreadModes.Grayscale);
            tag = index.ToString("D4");
        }

        using (frame)
        {
            var result = new DefectDetector().Detect(frame, new DetectorOptions
            {
                Sensitivity = options.GetDouble("sensitivity", 0.6)
            });

            Console.WriteLine($"[probe] file={label.File} truth={label.Label} " +
                              $"ng={result.IsNg} class={result.Defect} score={result.Score:F3} " +
                              $"focus={result.Quality.Focus:F0} part={result.Quality.PartFound}");
            foreach (var f in result.Findings.Take(12))
                Console.WriteLine($"  {f.Class,-8} area={f.Area,6:F0} score={f.Score:F3} mean={f.MeanResponse:F1} bounds={f.Bounds}");

            Directory.CreateDirectory(outDir);
            using var evidence = EvidenceRenderer.Render(
                $"#{tag}", frame, result, $"truth={label.Label}");
            string path = Path.Combine(outDir, $"probe-{tag}.jpg");
            OpenCvSharp.Cv2.ImWrite(path, evidence);
            Console.WriteLine($"[probe] 写出 {path}");
        }

        return 0;
    }
}
