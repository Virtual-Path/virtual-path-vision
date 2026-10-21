using System.Text;
using System.Text.Json;
using OpenCvSharp;

namespace VirtualPathVision.Inspection.Agent;

/// <summary>代理在一次判定后要执行的产线动作。</summary>
public enum LineAction
{
    /// <summary>放行</summary>
    Pass = 0,

    /// <summary>剔除（执行机构动作）</summary>
    Reject = 1,

    /// <summary>隔离到待检区，等待人工复核</summary>
    Quarantine = 2,

    /// <summary>请求人工介入（图像不可用 / 无法判定）</summary>
    Escalate = 3
}

/// <summary>一次工具调用记录。</summary>
public sealed record AgentStep(
    int Index,
    string Tool,
    IReadOnlyDictionary<string, object?> Args,
    string Observation,
    string? ReasonCode);

/// <summary>代理对一件产品的完整结论。</summary>
public sealed record AgentOutcome(
    string PartId,
    LineAction Action,
    string ReasonCode,
    IReadOnlyList<AgentStep> Steps,
    InspectionResult Final,
    double TotalMs)
{
    /// <summary>本次判定是否发生过"因为视觉结果而改变后续动作"的复检。</summary>
    public bool VisionChangedAction => Steps.Count > 1;
}

/// <summary>代理参数。</summary>
public sealed class AgentOptions
{
    /// <summary>低于该清晰度视为图像不可用。</summary>
    /// <remarks>
    /// 实测：对焦良好的合成样本拉普拉斯方差在 100~800；生成器里最软的样本约 20；
    /// 人为施加 sigma=18 的离焦后降到 0.4。阈值取 10，落在"可用但偏软"与"真失焦"之间。
    /// </remarks>
    public double MinFocus { get; init; } = 10;

    /// <summary>首次检测灵敏度。</summary>
    public double BaseSensitivity { get; init; } = 0.6;

    /// <summary>复检灵敏度。</summary>
    public double ReinspectSensitivity { get; init; } = 0.85;

    /// <summary>分数达到该值即直接剔除（无需复检）。</summary>
    public double HighConfidenceScore { get; init; } = 0.6;

    /// <summary>接近判定阈值但未过线时触发复检的带宽。</summary>
    public double NearThresholdMargin { get; init; } = 0.05;

    /// <summary>污渍达到该分数时转人工复核。</summary>
    public double StainQuarantineScore { get; init; } = 0.5;

    /// <summary>缺陷判定阈值，需与检测器保持一致。</summary>
    public double NgScoreThreshold { get; init; } = 0.25;
}

/// <summary>
/// 检测代理：把"看 → 判断 → 行动"做成可审计的循环。
///
/// <para><b>Agentic 判据</b>（对应竞赛规则）：视觉输出必须改变后续动作，
/// 而不只是被记录。本实现里视觉结果会改变三件事之一：</para>
/// <list type="bullet">
/// <item>清晰度不足 → 用对比度增强重新检测（改变下一次工具调用的参数）；</item>
/// <item>缺陷分数落在灰区 → 用更高灵敏度复检，第二次结果才决定放行/剔除；</item>
/// <item>检出污渍且分数较高 → 不直接剔除，而是转人工复核并保留证据。</item>
/// </list>
///
/// <para>每次工具调用都记入 <see cref="AgentStep"/>，包括参数与观测值，
/// 因此可以从 trace 复原"哪一项视觉证据导致了哪一个动作"。</para>
/// </summary>
public sealed class InspectionAgent
{
    private readonly DefectDetector _detector;
    private readonly AgentOptions _options;

    public InspectionAgent(DefectDetector? detector = null, AgentOptions? options = null)
    {
        _detector = detector ?? new DefectDetector();
        _options = options ?? new AgentOptions();
    }

    /// <summary>
    /// 判定一件产品。
    /// </summary>
    /// <param name="partId">工件标识</param>
    /// <param name="frame">当前帧</param>
    /// <param name="reacquire">
    /// 重新取图回调。为 null 时无法重拍，只能按现有图像判定。
    /// </param>
    public AgentOutcome Inspect(string partId, Mat frame, Func<Mat>? reacquire = null)
    {
        var steps = new List<AgentStep>(3);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var result = RunInspect(steps, frame, _options.BaseSensitivity, contrastBoost: false);

        // ── 1. 找不到工件：请求重新取图 ─────────────────────────────
        if (!result.Quality.PartFound)
        {
            if (reacquire != null)
            {
                using Mat again = reacquire();
                result = RunInspect(steps, again, _options.BaseSensitivity, contrastBoost: false,
                    reason: "part_not_found");
            }

            if (!result.Quality.PartFound)
                return Finish(partId, LineAction.Escalate, "part_not_found", steps, result, sw);
        }

        // ── 2. 图像不可用：换一套预处理再试一次 ─────────────────────
        if (result.Quality.Focus < _options.MinFocus)
        {
            result = RunInspect(steps, frame, _options.ReinspectSensitivity, contrastBoost: true,
                reason: "low_focus");

            if (result.Quality.Focus < _options.MinFocus)
                return Finish(partId, LineAction.Escalate, "image_unusable", steps, result, sw);
        }

        // ── 3. 未检出缺陷，但分数贴近阈值：复检一次再放行 ───────────
        if (!result.IsNg && result.Score >= Math.Max(0.001, _options.NgScoreThreshold - _options.NearThresholdMargin))
        {
            result = RunInspect(steps, frame, _options.ReinspectSensitivity, contrastBoost: true,
                reason: "near_threshold");

            return Finish(partId,
                result.IsNg ? LineAction.Reject : LineAction.Pass,
                result.IsNg ? "confirmed_by_reinspection" : "clear_after_reinspection",
                steps, result, sw);
        }

        if (!result.IsNg)
            return Finish(partId, LineAction.Pass, "clean", steps, result, sw);

        // ── 4. 污渍：需要人工确认是否整批隔离 ───────────────────────
        if (result.Defect == DefectClass.Stain && result.Score >= _options.StainQuarantineScore)
            return Finish(partId, LineAction.Quarantine, "stain_requires_review", steps, result, sw);

        // ── 5. 高置信度缺陷：直接剔除 ───────────────────────────────
        if (result.Score >= _options.HighConfidenceScore)
            return Finish(partId, LineAction.Reject, "high_confidence_defect", steps, result, sw);

        // ── 6. 灰区缺陷：提高灵敏度复检，二次结果说了算 ─────────────
        result = RunInspect(steps, frame, _options.ReinspectSensitivity, contrastBoost: true,
            reason: "ambiguous_defect");

        return Finish(partId,
            result.IsNg ? LineAction.Reject : LineAction.Pass,
            result.IsNg ? "confirmed_by_reinspection" : "cleared_by_reinspection",
            steps, result, sw);
    }

    private InspectionResult RunInspect(
        List<AgentStep> steps,
        Mat frame,
        double sensitivity,
        bool contrastBoost,
        string? reason = null)
    {
        var options = new DetectorOptions
        {
            Sensitivity = sensitivity,
            ContrastBoost = contrastBoost,
            Reinspection = contrastBoost,
            NgScoreThreshold = _options.NgScoreThreshold
        };

        var result = _detector.Detect(frame, options);
        string observation = result.Quality.PartFound
            ? $"part found, focus={result.Quality.Focus:F0}, ng={result.IsNg}, class={result.Defect}, score={result.Score:F2}, findings={result.Findings.Count}"
            : "part not found";

        steps.Add(new AgentStep(
            steps.Count,
            "inspect",
            new Dictionary<string, object?>
            {
                ["sensitivity"] = sensitivity,
                ["contrastBoost"] = contrastBoost,
                ["ngThreshold"] = options.NgScoreThreshold
            },
            observation,
            reason));

        return result;
    }

    private static AgentOutcome Finish(
        string partId,
        LineAction action,
        string reason,
        List<AgentStep> steps,
        InspectionResult result,
        System.Diagnostics.Stopwatch sw)
    {
        sw.Stop();
        return new AgentOutcome(partId, action, reason, steps, result, sw.Elapsed.TotalMilliseconds);
    }
}

/// <summary>动作执行端。实现可以是 PLC、MES、模拟传送带或 SNS。</summary>
public interface ILineActuator
{
    /// <summary>执行动作并返回可记录的回执。</summary>
    Task<string> ApplyAsync(AgentOutcome outcome, CancellationToken ct = default);
}

/// <summary>
/// 模拟产线执行机构：把动作写成结构化日志，不触碰真实设备。
/// 用于离线回放与竞赛演示。
/// </summary>
public sealed class SimulatedLineActuator : ILineActuator
{
    private readonly List<string> _log = new();
    private readonly object _gate = new();

    public IReadOnlyList<string> Log
    {
        get { lock (_gate) return _log.ToArray(); }
    }

    public Task<string> ApplyAsync(AgentOutcome outcome, CancellationToken ct = default)
    {
        string command = outcome.Action switch
        {
            LineAction.Pass => "conveyor.advance",
            LineAction.Reject => "diverter.reject",
            LineAction.Quarantine => "diverter.quarantine",
            _ => "andon.request_human"
        };

        string line = $"{outcome.PartId}\t{outcome.Action}\t{command}\t{outcome.ReasonCode}";
        lock (_gate) _log.Add(line);
        return Task.FromResult(command);
    }
}

/// <summary>把代理轨迹写成 JSONL，作为竞赛要求的可审计证据。</summary>
public static class AgentTrace
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>逐条追加；调用方负责创建目录。</summary>
    public static void Append(string path, AgentOutcome outcome)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string line = JsonSerializer.Serialize(new
        {
            partId = outcome.PartId,
            action = outcome.Action.ToString(),
            reasonCode = outcome.ReasonCode,
            visionChangedAction = outcome.VisionChangedAction,
            totalMs = Math.Round(outcome.TotalMs, 3),
            final = new
            {
                ng = outcome.Final.IsNg,
                defect = outcome.Final.Defect.ToString(),
                score = Math.Round(outcome.Final.Score, 4),
                focus = Math.Round(outcome.Final.Quality.Focus, 2)
            },
            steps = outcome.Steps.Select(s => new
            {
                s.Index,
                s.Tool,
                s.Args,
                s.Observation,
                s.ReasonCode
            })
        }, Options);

        File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
    }
}
