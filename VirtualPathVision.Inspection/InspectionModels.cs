using OpenCvSharp;

namespace VirtualPathVision.Inspection;

/// <summary>表面缺陷类型。</summary>
public enum DefectClass
{
    /// <summary>无缺陷</summary>
    None = 0,

    /// <summary>划痕：细长、方向性明显</summary>
    Scratch = 1,

    /// <summary>点状缺陷：凹坑、脏点</summary>
    Spot = 2,

    /// <summary>污渍 / 色差：面积大、对比度低</summary>
    Stain = 3
}

/// <summary>单帧图像质量指标，用于决定是否需要重新采集。</summary>
/// <param name="Focus">拉普拉斯方差，越高越清晰</param>
/// <param name="Exposure">工件区域平均灰度</param>
/// <param name="PartFound">是否在画面中找到工件</param>
public readonly record struct ImageQuality(double Focus, double Exposure, bool PartFound);

/// <summary>一个缺陷候选。</summary>
/// <param name="Class">缺陷类型</param>
/// <param name="Bounds">外接矩形（原图坐标）</param>
/// <param name="Score">归一化置信度 0..1</param>
/// <param name="Area">轮廓面积（像素²）</param>
/// <param name="MeanResponse">缺陷区域的平均响应强度</param>
public sealed record DefectFinding(
    DefectClass Class,
    Rect Bounds,
    double Score,
    double Area,
    double MeanResponse)
{
    /// <summary>用于日志/证据的对角线长度，便于比较缺陷尺寸。</summary>
    public double Diag => Math.Sqrt((double)Bounds.Width * Bounds.Width + (double)Bounds.Height * Bounds.Height);
}

/// <summary>一次完整检测的结果。</summary>
/// <param name="Verdict">NG 表示存在达到阈值的缺陷</param>
/// <param name="Defect">判定为主导缺陷类型（无缺陷时为 None）</param>
/// <param name="Score">主导缺陷的分数</param>
/// <param name="Findings">全部候选，按分数降序</param>
/// <param name="Quality">本帧质量指标</param>
/// <param name="ElapsedMs">检测耗时（毫秒）</param>
public sealed record InspectionResult(
    bool Verdict,
    DefectClass Defect,
    double Score,
    IReadOnlyList<DefectFinding> Findings,
    ImageQuality Quality,
    double ElapsedMs)
{
    /// <summary>为 true 时表示判为不合格。</summary>
    public bool IsNg => Verdict;
}

/// <summary>检测器参数。</summary>
public sealed class DetectorOptions
{
    /// <summary>灵敏度 0..1。越高越容易报缺陷，但误报也会上升。</summary>
    public double Sensitivity { get; init; } = 0.6;

    /// <summary>小于该面积的轮廓直接忽略（像素²）。</summary>
    public int MinArea { get; init; } = 6;

    /// <summary>工件最小面积占画面的百分比，低于此值视为没找到工件。</summary>
    public double PartMinAreaPercent { get; init; } = 4.0;

    /// <summary>判定为 NG 的分数阈值。</summary>
    public double NgScoreThreshold { get; init; } = 0.25;

    /// <summary>是否启用 CLAHE 对比度增强（复检时使用）。</summary>
    public bool ContrastBoost { get; init; }

    /// <summary>剥离工件边缘的像素数。边缘本身会产生强响应，必须排除。</summary>
    /// <remarks>
    /// 必须大于背景估计核半径：形态学开运算在工件边缘会把"半亮半暗"的窗口
    /// 当成背景，导致边缘内侧出现一条假响应带。默认核 31 → 半径 15，
    /// 因此这里取 16 以上。
    /// </remarks>
    public int EdgeMargin { get; init; } = 17;

    /// <summary>光照背景估计的高斯核标准差（像素）。</summary>
    /// <remarks>
    /// 用大尺度高斯而不是形态学开运算：开运算对周期性拉丝纹理会产生
    /// "上包络"，把纹理幅值放大成假缺陷；高斯则把周期 25px 的纹波
    /// 衰减掉。工件外侧用工件均值填充，避免边界处出现强台阶。
    /// sigma 取 3：再大就会把 2px 划痕抹成 20px 宽的带，破坏长宽比与定位。
    /// </remarks>
    public double BackgroundSigma { get; init; } = 3.0;

    /// <summary>是否为复检流程（复检允许更激进的参数）。</summary>
    public bool Reinspection { get; init; }
}
