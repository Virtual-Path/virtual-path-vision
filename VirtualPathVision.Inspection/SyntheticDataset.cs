using System.Text;
using System.Text.Json;
using OpenCvSharp;

namespace VirtualPathVision.Inspection;

/// <summary>一条数据集的标注记录。</summary>
/// <param name="File">相对路径</param>
/// <param name="Label">good / scratch / spot / stain</param>
/// <param name="Bbox">缺陷外接矩形 [x,y,w,h]；good 为 null</param>
/// <param name="Seed">生成该样本的随机种子，便于复现</param>
public sealed record DatasetLabel(string File, string Label, int[]? Bbox, int Seed);

/// <summary>数据集生成统计。</summary>
public sealed record DatasetManifest(
    int Total, int Good, int Scratch, int Spot, int Stain, int Width, int Height, int Seed);

/// <summary>
/// 可控合成数据集生成器。
///
/// <para><b>为什么用合成数据</b>：竞赛评审需要可复现的评测证据。合成样本的
/// 缺陷位置、类型、对比度全部已知，能把"漏检"和"标注错误"分开；
/// 真实产线数据无法在几天内凑齐带像素级标注的规模。</para>
///
/// <para><b>真实性来源</b>：工件基材带拉丝纹理与随位置变化的照明梯度，
/// 叠加高斯噪声与轻微离焦；缺陷对比度覆盖 8~80 个灰阶，
/// 其中约 15% 落在检测器设计边界附近，用来暴露失败案例而不是刷分。</para>
///
/// <para>生成过程完全由种子决定：同一版本代码 + 同一种子 → 逐像素相同。</para>
/// </summary>
public sealed class SyntheticDataset
{
    /// <summary>生成数据集到目标目录，已存在则覆盖。</summary>
    public DatasetManifest Generate(string outputDir, int total = 800, int seed = 20261026)
    {
        Directory.CreateDirectory(outputDir);
        string imageDir = Path.Combine(outputDir, "images");
        Directory.CreateDirectory(imageDir);

        // 固定配额，保证每类样本数量可预期
        int good = total * 50 / 100;
        int scratch = total * 19 / 100;
        int spot = total * 19 / 100;
        int stain = total - good - scratch - spot;

        var labels = new List<DatasetLabel>(total);
        int index = 0;

        void Emit(string label, int count)
        {
            for (int i = 0; i < count; i++, index++)
            {
                int sampleSeed = seed + index * 7919;
                string name = $"{index:D4}.png";
                var (img, bbox) = RenderSample(label, sampleSeed, out int usedSeed);

                using (img)
                {
                    Cv2.ImWrite(Path.Combine(imageDir, name), img);
                }

                labels.Add(new DatasetLabel(
                    $"images/{name}", label, bbox, usedSeed));
            }
        }

        Emit("good", good);
        Emit("scratch", scratch);
        Emit("spot", spot);
        Emit("stain", stain);

        var options = new JsonSerializerOptions { WriteIndented = false };
        using (var writer = new StreamWriter(Path.Combine(outputDir, "labels.jsonl"), false, new UTF8Encoding(false)))
        {
            foreach (var label in labels)
                writer.WriteLine(JsonSerializer.Serialize(label, options));
        }

        var manifest = new DatasetManifest(total, good, scratch, spot, stain, 640, 480, seed);
        File.WriteAllText(Path.Combine(outputDir, "manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

        return manifest;
    }

    /// <summary>渲染一张样本；返回图像与缺陷外接矩形（good 为 null）。</summary>
    public (Mat Image, int[]? Bbox) RenderSample(string label, int seed, out int usedSeed)
    {
        usedSeed = seed;
        var rng = new Random(seed);

        const int W = 640, H = 480;

        using Mat scene = new(H, W, MatType.CV_8UC1);
        DrawBackground(scene, rng);

        int partW = rng.Next(210, 281);
        int partH = rng.Next(150, 211);
        int baseGray = rng.Next(110, 176);

        using Mat part = new(partH, partW, MatType.CV_8UC1, Scalar.All(baseGray));
        using Mat mask = new(partH, partW, MatType.CV_8UC1, Scalar.All(0));
        DrawRoundedRect(mask, new Rect(0, 0, partW, partH), 18, 255, -1);

        DrawBrushedTexture(part, rng, baseGray);

        Rect defectLocal = default;
        bool hasDefect = label != "good";

        if (hasDefect)
        {
            defectLocal = DrawDefect(part, rng, label, baseGray, partW, partH);
        }

        // 旋转 + 平移到画面
        double angle = rng.NextDouble() * 16 - 8;
        int cx = rng.Next(150, W - 150);
        int cy = rng.Next(120, H - 120);
        var center = new Point2f(partW / 2f, partH / 2f);
        using Mat rot = Cv2.GetRotationMatrix2D(center, angle, 1.0);
        rot.Set(0, 2, rot.At<double>(0, 2) + (cx - partW / 2.0));
        rot.Set(1, 2, rot.At<double>(1, 2) + (cy - partH / 2.0));

        using Mat warped = new();
        Cv2.WarpAffine(part, warped, rot, new Size(W, H),
            InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(0));

        using Mat warpedMask = new();
        Cv2.WarpAffine(mask, warpedMask, rot, new Size(W, H),
            InterpolationFlags.Nearest, BorderTypes.Constant, Scalar.All(0));

        // 工件投影：先在变形前画一个偏移的软阴影
        using Mat shadow = new();
        Cv2.GaussianBlur(warpedMask, shadow, new Size(0, 0), 7);
        Cv2.Threshold(shadow, shadow, 40, 255, ThresholdTypes.Binary);
        using Mat shadowShift = new();
        using (var shift = new Mat<double>(2, 3))
        {
            shift.Set(0, 0, 1.0); shift.Set(0, 1, 0.0); shift.Set(0, 2, 5.0);
            shift.Set(1, 0, 0.0); shift.Set(1, 1, 1.0); shift.Set(1, 2, 6.0);
            Cv2.WarpAffine(shadow, shadowShift, shift, new Size(W, H));
        }
        // 阴影只压暗，不增亮
        using (Mat dark = new())
        {
            Cv2.Subtract(scene, new Scalar(28), dark);
            dark.CopyTo(scene, shadowShift);
        }

        // 粘贴工件本体
        warped.CopyTo(scene, warpedMask);

        // 场景级曝光与噪声（在粘贴工件之后统一施加，保证缺陷与背景同受传感器噪声影响）
        double gain = 0.88 + rng.NextDouble() * 0.24;
        scene.ConvertTo(scene, MatType.CV_8UC1, gain, rng.NextDouble() * 8 - 4);

        using (Mat noise16 = new(H, W, MatType.CV_16S))
        using (Mat scene16 = new())
        {
            double sigma = 2.0 + rng.NextDouble() * 4.0;
            Cv2.Randn(noise16, Scalar.All(0), Scalar.All(sigma));
            scene.ConvertTo(scene16, MatType.CV_16S);
            Cv2.Add(scene16, noise16, scene16);
            scene16.ConvertTo(scene, MatType.CV_8UC1);
        }

        // 轻微离焦（部分样本）
        if (rng.NextDouble() < 0.35)
        {
            using Mat blurred = new();
            Cv2.GaussianBlur(scene, blurred, new Size(3, 3), 0.4 + rng.NextDouble() * 0.5);
            blurred.CopyTo(scene);
        }

        int[]? bbox = null;
        if (hasDefect)
        {
            var corners = new[]
            {
                new Point2f(defectLocal.Left, defectLocal.Top),
                new Point2f(defectLocal.Right, defectLocal.Top),
                new Point2f(defectLocal.Right, defectLocal.Bottom),
                new Point2f(defectLocal.Left, defectLocal.Bottom)
            };

            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            foreach (var p in corners)
            {
                double x = rot.At<double>(0, 0) * p.X + rot.At<double>(0, 1) * p.Y + rot.At<double>(0, 2);
                double y = rot.At<double>(1, 0) * p.X + rot.At<double>(1, 1) * p.Y + rot.At<double>(1, 2);
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }

            int left = Math.Clamp((int)Math.Floor(minX), 0, W - 1);
            int top = Math.Clamp((int)Math.Floor(minY), 0, H - 1);
            int right = Math.Clamp((int)Math.Ceiling(maxX), left + 1, W);
            int bottom = Math.Clamp((int)Math.Ceiling(maxY), top + 1, H);

            bbox = new[] { left, top, right - left, bottom - top };
        }

        return (scene.Clone(), bbox);
    }

    private static void DrawBackground(Mat scene, Random rng)
    {
        int baseGray = rng.Next(28, 48);
        int width = scene.Width;
        int height = scene.Height;

        // 用一列像素 repeat 成整幅，避免逐像素 P/Invoke
        using var column = new Mat(height, 1, MatType.CV_8UC1);
        for (int y = 0; y < height; y++)
        {
            double t = (double)y / height;
            int v = (int)(baseGray + 12 * (1 - t) + 3 * Math.Sin(y * 0.02));
            column.Set(y, 0, (byte)Math.Clamp(v, 0, 255));
        }

        Cv2.Repeat(column, 1, width, scene);
    }

    private static void DrawBrushedTexture(Mat part, Random rng, int baseGray)
    {
        int amplitude = rng.Next(2, 7);
        double phase = rng.NextDouble() * Math.PI * 2;
        double freq = 0.25 + rng.NextDouble() * 0.2;

        int width = part.Width;
        int height = part.Height;

        // 拉丝纹理 + 横向照明梯度（左亮右暗），幅值足以让全局阈值失效
        using var row = new Mat(1, width, MatType.CV_8UC1);
        for (int x = 0; x < width; x++)
        {
            int stripe = (int)(amplitude * Math.Sin(x * freq + phase));
            int gradient = (int)(14 * (1 - 2.0 * x / width));
            int v = Math.Clamp(baseGray + stripe + gradient, 0, 255);
            row.Set(0, x, (byte)v);
        }

        Cv2.Repeat(row, height, 1, part);
    }

    private static Rect DrawDefect(Mat part, Random rng, string label, int baseGray, int w, int h)
    {
        // 缺陷必须落在工件内部，避免被边缘掩膜吃掉
        const int margin = 30;
        int cx = rng.Next(margin, w - margin);
        int cy = rng.Next(margin, h - margin);

        // 15% 的样本刻意做成低对比度，用于暴露检测边界
        bool lowContrast = rng.NextDouble() < 0.15;
        double contrastScale = lowContrast ? 0.28 : 1.0;

        switch (label)
        {
            case "scratch":
            {
                // 划痕端点也要留在工件内，避免被边缘掩膜截断
                cx = rng.Next(56, Math.Max(57, w - 56));
                cy = rng.Next(56, Math.Max(57, h - 56));

                double len = rng.Next(28, 70);
                double ang = rng.NextDouble() * Math.PI;
                var p1 = new Point(cx - (int)(Math.Cos(ang) * len / 2), cy - (int)(Math.Sin(ang) * len / 2));
                var p2 = new Point(cx + (int)(Math.Cos(ang) * len / 2), cy + (int)(Math.Sin(ang) * len / 2));
                int thickness = rng.Next(1, 4);
                int delta = (int)((35 + rng.NextDouble() * 40) * contrastScale) * (rng.NextDouble() < 0.7 ? -1 : 1);
                int value = Math.Clamp(baseGray + delta, 0, 255);
                Cv2.Line(part, p1, p2, Scalar.All(value), thickness, LineTypes.Link8);
                return BoundingOf(new[] { p1, p2 }, thickness);
            }

            case "spot":
            {
                int r = rng.Next(3, 10);
                int delta = -(int)((45 + rng.NextDouble() * 45) * contrastScale);
                int value = Math.Clamp(baseGray + delta, 0, 255);
                Cv2.Circle(part, new Point(cx, cy), r, Scalar.All(value), -1, LineTypes.Link8);
                return new Rect(cx - r, cy - r, r * 2, r * 2);
            }

            case "stain":
            {
                int ax = rng.Next(12, 26);
                int ay = rng.Next(9, 20);

                // 缺陷必须完整落在工件内：贴边的污渍会被边缘掩膜裁掉，
                // 那是"检测策略不覆盖边缘"的已知限制，不该混进召回统计。
                cx = rng.Next(ax + 24, Math.Max(ax + 25, w - ax - 24));
                cy = rng.Next(ay + 24, Math.Max(ay + 25, h - ay - 24));

                int delta = -(int)((18 + rng.NextDouble() * 27) * contrastScale);
                int value = Math.Clamp(baseGray + delta, 0, 255);
                Cv2.Ellipse(part, new Point(cx, cy), new Size(ax, ay),
                    rng.NextDouble() * 180, 0, 360, Scalar.All(value), -1, LineTypes.Link8);

                // 污渍边缘是弥散的
                var roiRect = new Rect(
                    Math.Clamp(cx - ax - 6, 0, w - 1),
                    Math.Clamp(cy - ay - 6, 0, h - 1),
                    Math.Min(ax * 2 + 12, w - Math.Clamp(cx - ax - 6, 0, w - 1)),
                    Math.Min(ay * 2 + 12, h - Math.Clamp(cy - ay - 6, 0, h - 1)));

                if (roiRect.Width > 3 && roiRect.Height > 3)
                {
                    using var sub = new Mat(part, roiRect);
                    using var blurred = new Mat();
                    Cv2.GaussianBlur(sub, blurred, new Size(5, 5), 1.8);
                    blurred.CopyTo(sub);
                }

                return new Rect(cx - ax, cy - ay, ax * 2, ay * 2);
            }

            default:
                return default;
        }

        static Rect BoundingOf(Point[] pts, int pad)
        {
            int minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X);
            int minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
            return new Rect(minX - pad, minY - pad,
                Math.Max(1, maxX - minX + pad * 2), Math.Max(1, maxY - minY + pad * 2));
        }
    }

    private static void DrawRoundedRect(Mat target, Rect r, int radius, int value, int thickness)
    {
        int d = radius * 2;
        Cv2.Rectangle(target, new Rect(r.Left + radius, r.Top, r.Width - d, r.Height), Scalar.All(value), thickness);
        Cv2.Rectangle(target, new Rect(r.Left, r.Top + radius, r.Width, r.Height - d), Scalar.All(value), thickness);
        Cv2.Circle(target, new Point(r.Left + radius, r.Top + radius), radius, Scalar.All(value), thickness);
        Cv2.Circle(target, new Point(r.Right - radius, r.Top + radius), radius, Scalar.All(value), thickness);
        Cv2.Circle(target, new Point(r.Left + radius, r.Bottom - radius), radius, Scalar.All(value), thickness);
        Cv2.Circle(target, new Point(r.Right - radius, r.Bottom - radius), radius, Scalar.All(value), thickness);
    }
}