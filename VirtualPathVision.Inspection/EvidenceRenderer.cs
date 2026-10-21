using OpenCvSharp;

namespace VirtualPathVision.Inspection;

/// <summary>
/// 证据渲染：把检测结果画在图上，作为提交给 MES / S3 的可追溯凭证。
///
/// <para>标注只使用英文，避免 OpenCV 内置字体无法渲染中文导致出现问号方框。</para>
/// </summary>
public static class EvidenceRenderer
{
    /// <summary>渲染一张带判定的证据图（BGR）。调用方负责释放返回的 Mat。</summary>
    public static Mat Render(string partId, Mat frame, InspectionResult result, string? caption = null)
    {
        Mat bgr = ToBgr(frame);

        // 顶部信息条：判定结论 + 分数，保证缩略图也能看出结果
        var header = new Rect(0, 0, bgr.Width, 46);
        using (Mat roi = new(bgr, header))
        {
            roi.SetTo(result.IsNg ? new Scalar(30, 30, 140) : new Scalar(40, 110, 40));
        }

        string verdict = result.IsNg
            ? $"NG  {result.Defect}  score={result.Score:F2}"
            : $"OK  score={result.Score:F2}";

        Cv2.PutText(bgr, $"{partId}", new Point(12, 20),
            HersheyFonts.HersheySimplex, 0.55, Scalar.White, 1, LineTypes.AntiAlias);
        Cv2.PutText(bgr, verdict, new Point(12, 40),
            HersheyFonts.HersheySimplex, 0.65, Scalar.White, 2, LineTypes.AntiAlias);

        string quality = $"focus={result.Quality.Focus:F0} exposure={result.Quality.Exposure:F0} {result.ElapsedMs:F1}ms";
        var size = Cv2.GetTextSize(quality, HersheyFonts.HersheySimplex, 0.45, 1, out _);
        Cv2.PutText(bgr, quality, new Point(bgr.Width - size.Width - 12, 28),
            HersheyFonts.HersheySimplex, 0.45, new Scalar(220, 220, 220), 1, LineTypes.AntiAlias);

        // 缺陷框：按类别着色，分数附在框上方
        foreach (var f in result.Findings)
        {
            if (f.Score < 0.05) continue;

            Scalar color = f.Class switch
            {
                DefectClass.Scratch => new Scalar(0, 200, 255),   // 黄
                DefectClass.Spot => new Scalar(255, 120, 0),     // 蓝
                DefectClass.Stain => new Scalar(0, 0, 255),      // 红
                _ => new Scalar(200, 200, 200)
            };

            Cv2.Rectangle(bgr, f.Bounds, color, 2);
            string label = $"{f.Class} {f.Score:F2}";
            var pos = new Point(f.Bounds.Left, Math.Max(58, f.Bounds.Top - 6));
            Cv2.PutText(bgr, label, pos, HersheyFonts.HersheySimplex, 0.5,
                Scalar.Black, 3, LineTypes.AntiAlias);
            Cv2.PutText(bgr, label, pos, HersheyFonts.HersheySimplex, 0.5,
                color, 1, LineTypes.AntiAlias);
        }

        if (!string.IsNullOrEmpty(caption))
        {
            Cv2.PutText(bgr, caption, new Point(12, bgr.Height - 12),
                HersheyFonts.HersheySimplex, 0.5, new Scalar(230, 230, 230), 1, LineTypes.AntiAlias);
        }

        return bgr;
    }

    private static Mat ToBgr(Mat frame)
    {
        if (frame.Channels() == 3)
            return frame.Clone();

        var bgr = new Mat();
        Cv2.CvtColor(frame, bgr, ColorConversionCodes.GRAY2BGR);
        return bgr;
    }
}
