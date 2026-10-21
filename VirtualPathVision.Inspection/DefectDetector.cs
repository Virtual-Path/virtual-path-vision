using System.Diagnostics;
using OpenCvSharp;

namespace VirtualPathVision.Inspection;

/// <summary>
/// 表面缺陷检测器：在单帧灰度/彩色图像中定位划痕、点状缺陷与污渍。
///
/// <para><b>处理链路</b>（全部为 OpenCV 5 原生算子）：</para>
/// <list type="number">
/// <item>Otsu 阈值 + 最大外轮廓 → 工件掩膜，把检测限制在工件内部；</item>
/// <item>大尺度高斯估计光照背景，双向差分抵消明暗不均与拉丝纹理；</item>
/// <item>细尺度抓划痕/点状，粗尺度补大面积低对比污渍；</item>
/// <item>按长宽比与面积分类，并按平均响应给分。</item>
/// </list>
///
/// <para><b>为什么用高斯而不是形态学开运算</b>：开运算对周期性拉丝纹理会产生
/// "上包络"，把纹理幅值放大成假缺陷；高斯差分只保留相对局部背景的异常。</para>
///
/// <para><b>为什么需要两个尺度</b>：sigma=3 能保住 2px 划痕的几何形状，但对
/// 直径 30px 的污渍只留下轮廓环；sigma=12 能把污渍内部抬起来，却会把划痕抹成宽带。
/// 两者各取所长，粗尺度只补"大面积污渍"。</para>
///
/// <para><b>已知边界</b>：工件边缘 <see cref="DetectorOptions.EdgeMargin"/> 像素内不检测，
/// 因为轮廓边缘的梯度响应与划痕同量级；对比度低于约 4 个灰阶的污渍会被漏检。</para>
/// </summary>
public sealed class DefectDetector
{
    /// <summary>把平均响应映射到 0..1 分数时的参考强度。</summary>
    private const double ScoreReference = 30.0;

    /// <summary>粗尺度响应的参考强度：污渍对比度低，用更小的分母。</summary>
    private const double CoarseScoreReference = 20.0;

    /// <summary>对单帧执行检测。线程安全：不持有跨调用状态。</summary>
    public InspectionResult Detect(Mat frame, DetectorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Empty())
            throw new ArgumentException("输入帧为空", nameof(frame));

        var opt = options ?? new DetectorOptions();
        var sw = Stopwatch.StartNew();

        using Mat gray = ToGray(frame);

        // ── 1. 工件定位 ────────────────────────────────────────────────
        using Mat blur = new();
        Cv2.GaussianBlur(gray, blur, new Size(5, 5), 0);

        using Mat otsu = new();
        Cv2.Threshold(blur, otsu, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);

        Cv2.FindContours(otsu, out Point[][] partContours, out _,
            RetrievalModes.External, ContourApproximationModes.ApproxSimple);

        Point[]? part = null;
        double bestArea = 0;
        foreach (var c in partContours)
        {
            double a = Cv2.ContourArea(c);
            if (a > bestArea) { bestArea = a; part = c; }
        }

        double minPartArea = gray.Width * (double)gray.Height * opt.PartMinAreaPercent / 100.0;
        bool partFound = part != null && bestArea >= minPartArea;

        using Mat partMask = Mat.Zeros(gray.Size(), MatType.CV_8UC1);
        if (partFound)
            Cv2.DrawContours(partMask, new[] { part! }, -1, Scalar.All(255), -1);

        // 质量指标：清晰度取拉普拉斯方差，曝光取工件区均值
        using Mat lap = new();
        Cv2.Laplacian(gray, lap, MatType.CV_64F);
        Cv2.MeanStdDev(lap, out _, out Scalar lapStd);
        double focus = lapStd.Val0 * lapStd.Val0;

        double exposure = partFound ? Cv2.Mean(gray, partMask).Val0 : Cv2.Mean(gray).Val0;

        if (!partFound)
        {
            sw.Stop();
            return new InspectionResult(false, DefectClass.None, 0,
                Array.Empty<DefectFinding>(),
                new ImageQuality(focus, exposure, false),
                sw.Elapsed.TotalMilliseconds);
        }

        // ── 2. 光照校正 ────────────────────────────────────────────────
        using Mat work = new();
        if (opt.ContrastBoost)
        {
            using var clahe = Cv2.CreateCLAHE(2.0, new Size(8, 8));
            clahe.Apply(gray, work);
        }
        else
        {
            gray.CopyTo(work);
        }

        // 工件外侧用工件均值填充：否则高斯背景在边界处会跟着 100+ 灰阶的台阶走，
        // 留下一条与边缘平行的假响应带。
        double partMean = Cv2.Mean(work, partMask).Val0;
        using Mat filled = new();
        work.CopyTo(filled);
        using (Mat outside = new())
        {
            Cv2.BitwiseNot(partMask, outside);
            filled.SetTo(Scalar.All(partMean), outside);
        }

        // ── 3. 双尺度响应 ──────────────────────────────────────────────
        double sensitivity = Math.Clamp(opt.Sensitivity, 0, 1);
        double fineSigma = Math.Max(1.5, opt.BackgroundSigma + (opt.Reinspection ? 0.5 : 0));
        double coarseSigma = 8.0 + (opt.Reinspection ? 2 : 0);

        using Mat fineResponse = BuildResponse(work, filled, fineSigma);
        using Mat coarseResponse = BuildResponse(work, filled, coarseSigma);

        // 工件内缩：边缘梯度不参与判定
        using Mat inner = new();
        if (opt.EdgeMargin > 0)
        {
            int k = opt.EdgeMargin * 2 + 1;
            using Mat erodeKernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(k, k));
            Cv2.Erode(partMask, inner, erodeKernel);
        }
        else
        {
            partMask.CopyTo(inner);
        }

        // 灵敏度越高阈值越低
        double fineThreshold = 16.0 - 12.0 * sensitivity;
        double coarseThreshold = 10.0 - 4.0 * sensitivity;

        // ── 4. 细尺度轮廓 ──────────────────────────────────────────────
        using Mat binaryFine = ThresholdAndClean(fineResponse, fineThreshold, inner, 3);
        Cv2.FindContours(binaryFine, out Point[][] defects, out _,
            RetrievalModes.External, ContourApproximationModes.ApproxSimple);

        var findings = new List<DefectFinding>(defects.Length);
        foreach (var c in defects)
        {
            double area = Cv2.ContourArea(c);
            if (area < opt.MinArea) continue;

            var rr = Cv2.MinAreaRect(c);
            double longSide = Math.Max(rr.Size.Width, rr.Size.Height);
            double shortSide = Math.Max(1.0, Math.Min(rr.Size.Width, rr.Size.Height));
            double aspect = longSide / shortSide;

            double meanResponse = MeanInside(fineResponse, c, gray.Size());
            DefectClass cls = ClassifyDefect(area, aspect, longSide, out _);
            double score = Math.Clamp(meanResponse / ScoreReference, 0, 1);

            findings.Add(new DefectFinding(cls, Cv2.BoundingRect(c), score, area, meanResponse));
        }

        // ── 5. 粗尺度只补"污渍" ────────────────────────────────────────
        using Mat binaryCoarse = ThresholdAndClean(coarseResponse, coarseThreshold, inner, 5);
        Cv2.FindContours(binaryCoarse, out Point[][] coarse, out _,
            RetrievalModes.External, ContourApproximationModes.ApproxSimple);

        foreach (var c in coarse)
        {
            double area = Cv2.ContourArea(c);
            if (area < 300) continue;   // 小目标交给细尺度，避免重复

            var bounds = Cv2.BoundingRect(c);
            if (findings.Any(f => IoU(f.Bounds, bounds) > 0.2)) continue;

            double meanResponse = MeanInside(coarseResponse, c, gray.Size());
            double score = Math.Clamp(meanResponse / CoarseScoreReference, 0, 1);
            findings.Add(new DefectFinding(DefectClass.Stain, bounds, score, area, meanResponse));
        }

        findings.Sort((a, b) => b.Score.CompareTo(a.Score));

        var dominant = findings.Count > 0 ? findings[0] : null;
        bool ng = dominant != null && dominant.Score >= opt.NgScoreThreshold;

        sw.Stop();
        return new InspectionResult(
            ng,
            ng ? dominant!.Class : DefectClass.None,
            dominant?.Score ?? 0,
            findings,
            new ImageQuality(focus, exposure, true),
            sw.Elapsed.TotalMilliseconds);
    }

    /// <summary>
    /// 按几何特征分类：细长 → 划痕；大而扁 → 污渍；其余 → 点状。
    /// </summary>
    /// <remarks>
    /// 用最小外接矩形的长边/短边比而不是外接矩形，是为了让斜向划痕
    /// 也能得到正确的高长宽比（外接矩形会把 45° 划痕算成近似正方形）。
    /// </remarks>
    internal static DefectClass ClassifyDefect(double area, double aspect, double longSide, out string reason)
    {
        if (aspect >= 3.0 && longSide >= 12)
        {
            reason = $"细长形状 aspect={aspect:F1} len={longSide:F0}";
            return DefectClass.Scratch;
        }

        if (area >= 400)
        {
            reason = $"大面积低对比区域 area={area:F0}";
            return DefectClass.Stain;
        }

        reason = $"紧凑区域 area={area:F0}";
        return DefectClass.Spot;
    }

    /// <summary>用大尺度高斯作为背景，返回 |图像 - 背景| 的双向高响应。</summary>
    private static Mat BuildResponse(Mat work, Mat filled, double sigma)
    {
        using Mat background = new();
        Cv2.GaussianBlur(filled, background, new Size(0, 0), sigma);

        using Mat darker = new();
        Cv2.Subtract(background, work, darker);
        using Mat brighter = new();
        Cv2.Subtract(work, background, brighter);

        var response = new Mat();
        Cv2.Max(darker, brighter, response);
        Cv2.GaussianBlur(response, response, new Size(3, 3), 0);
        return response;
    }

    /// <summary>阈值化 + 限制在工件内部 + 形态学清理。</summary>
    private static Mat ThresholdAndClean(Mat response, double threshold, Mat inner, int morphSize)
    {
        var binary = new Mat();
        Cv2.Threshold(response, binary, threshold, 255, ThresholdTypes.Binary);
        Cv2.BitwiseAnd(binary, inner, binary);

        using Mat kernel = Cv2.GetStructuringElement(
            MorphShapes.Rect, new Size(morphSize, morphSize));
        Cv2.MorphologyEx(binary, binary, MorphTypes.Close, kernel);
        Cv2.MorphologyEx(binary, binary, MorphTypes.Open, kernel);
        return binary;
    }

    /// <summary>轮廓内的平均响应。</summary>
    private static double MeanInside(Mat response, Point[] contour, Size size)
    {
        using Mat mask = Mat.Zeros(size, MatType.CV_8UC1);
        Cv2.DrawContours(mask, new[] { contour }, -1, Scalar.All(255), -1);
        return Cv2.Mean(response, mask).Val0;
    }

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

    private static Mat ToGray(Mat frame)
    {
        if (frame.Channels() == 1)
            return frame.Clone();

        var gray = new Mat();
        Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
        return gray;
    }
}
