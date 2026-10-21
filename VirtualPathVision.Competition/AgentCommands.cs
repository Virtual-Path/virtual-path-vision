using System.Text;
using System.Text.Json;
using OpenCvSharp;
using VirtualPathVision.Inspection;
using VirtualPathVision.Inspection.Agent;

namespace VirtualPathVision.Competition;

/// <summary>`agent` 命令：在数据集上跑感知—决策—动作闭环并留痕。</summary>
public static class AgentCommands
{
    public static int Run(CliOptions options)
    {
        string datasetDir = Paths.Dataset(options);
        string outDir = Paths.Artifacts(options);
        string evidenceDir = Path.Combine(outDir, "evidence");

        string labelsPath = Path.Combine(datasetDir, "labels.jsonl");
        if (!File.Exists(labelsPath))
        {
            Console.Error.WriteLine($"[agent] 数据集不存在: {datasetDir}");
            return 1;
        }

        int limit = options.GetInt("limit", int.MaxValue);
        int evidenceLimit = options.GetInt("evidence-limit", 40);

        var labels = File.ReadLines(labelsPath)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => JsonSerializer.Deserialize<DatasetLabel>(l)!)
            .Take(limit)
            .ToList();

        Directory.CreateDirectory(outDir);
        if (Directory.Exists(evidenceDir))
            Directory.Delete(evidenceDir, recursive: true);
        Directory.CreateDirectory(evidenceDir);

        string tracePath = Path.Combine(outDir, "agent-trace.jsonl");
        if (File.Exists(tracePath)) File.Delete(tracePath);

        var agent = new InspectionAgent();
        var actuator = new SimulatedLineActuator();

        var byAction = new Dictionary<string, int>(StringComparer.Ordinal);
        var byReason = new Dictionary<string, int>(StringComparer.Ordinal);
        int visualReplans = 0;
        int agreeWithTruth = 0;
        int evaluated = 0;
        var records = new List<(int Index, string Truth, AgentOutcome Outcome)>(labels.Count);

        foreach (var (label, index) in labels.Select((l, i) => (l, i)))
        {
            using Mat frame = Cv2.ImRead(Path.Combine(datasetDir, label.File), ImreadModes.Grayscale);
            if (frame.Empty()) continue;

            string partId = $"P{index:D4}";
            var outcome = agent.Inspect(partId, frame, reacquire: () => frame.Clone());

            actuator.ApplyAsync(outcome).GetAwaiter().GetResult();
            AgentTrace.Append(tracePath, outcome);

            byAction[outcome.Action.ToString()] = byAction.GetValueOrDefault(outcome.Action.ToString()) + 1;
            byReason[outcome.ReasonCode] = byReason.GetValueOrDefault(outcome.ReasonCode) + 1;
            if (outcome.VisionChangedAction) visualReplans++;

            bool truthNg = label.Label != "good";
            bool actedNg = outcome.Action is LineAction.Reject or LineAction.Quarantine or LineAction.Escalate;
            if (truthNg == actedNg) agreeWithTruth++;
            evaluated++;

            records.Add((index, label.Label, outcome));
        }

        // 证据图按"(真实类别, 动作)"分层抽样，而不是只取前 N 张——
        // 数据集前半段全是正常件，顺序截断会导致报告里看不到缺陷样本。
        var selected = records
            .GroupBy(r => (r.Truth, r.Outcome.Action))
            .Select(g => g.First())
            .Take(evidenceLimit)
            .ToList();

        foreach (var extra in records.GroupBy(r => r.Outcome.ReasonCode).Select(g => g.First()))
        {
            if (selected.Count >= evidenceLimit) break;
            if (selected.All(s => s.Index != extra.Index)) selected.Add(extra);
        }

        foreach (var record in selected)
        {
            var label = labels[record.Index];
            using Mat frame = Cv2.ImRead(Path.Combine(datasetDir, label.File), ImreadModes.Grayscale);
            if (frame.Empty()) continue;

            using Mat evidence = EvidenceRenderer.Render(
                $"P{record.Index:D4}", frame, record.Outcome.Final,
                $"truth={record.Truth} action={record.Outcome.Action} reason={record.Outcome.ReasonCode}");
            Cv2.ImWrite(
                Path.Combine(evidenceDir, $"{record.Index:D4}_{record.Truth}_{record.Outcome.Action}.jpg"),
                evidence);
        }

        var summary = new
        {
            total = evaluated,
            byAction,
            byReason,
            visionChangedAction = visualReplans,
            agreementWithTruth = evaluated == 0 ? 0 : (double)agreeWithTruth / evaluated,
            tracePath = Path.GetRelativePath(outDir, tracePath),
            evidenceCount = selected.Count
        };

        string summaryPath = Path.Combine(outDir, "agent-summary.json");
        File.WriteAllText(summaryPath, JsonSerializer.Serialize(summary,
            new JsonSerializerOptions { WriteIndented = true }));

        File.WriteAllLines(Path.Combine(outDir, "actuator.log"), actuator.Log, new UTF8Encoding(false));

        Console.WriteLine($"[agent] 处理 {evaluated} 件");
        foreach (var (action, count) in byAction.OrderBy(kv => kv.Key))
            Console.WriteLine($"[agent]   {action,-10} {count,5}  ({100.0 * count / Math.Max(1, evaluated):F1}%)");
        Console.WriteLine($"[agent] 因视觉证据改变后续动作: {visualReplans} 件");
        Console.WriteLine($"[agent] 与标注一致: {100.0 * agreeWithTruth / Math.Max(1, evaluated):F2}%");
        Console.WriteLine($"[agent] trace={tracePath}");
        Console.WriteLine($"[agent] 证据图 {selected.Count} 张 → {evidenceDir}");

        return 0;
    }
}
