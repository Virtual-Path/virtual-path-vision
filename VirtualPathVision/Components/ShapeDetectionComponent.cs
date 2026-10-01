using System.Collections.Generic;
using OpenCvSharp;

namespace VirtualPathVision.Components
{
    /// <summary>
    /// 形状识别组件。
    /// 基于 Canny 边缘 + 轮廓多边形逼近 + 圆形度分析，
    /// 将目标分类为圆形、矩形、三角形、五边形、多边形，
    /// 在结果图上绘制彩色轮廓、质心和类型标签，并输出分类统计。
    /// </summary>
    public class ShapeDetectionComponent
    {
        private const double MinArea = 400;             // 最小目标面积，过滤噪点
        private const double PolyEpsilon = 0.03;        // 多边形逼近精度（周长的比例）
        private const double CircleCircularity = 0.78;  // 圆形度阈值（1 = 完美圆）

        /// <summary>
        /// 检测灰度帧中的几何形状并分类。
        /// </summary>
        /// <param name="grayFrame">输入灰度帧</param>
        /// <param name="shapeCount">输出：识别到的形状总数</param>
        /// <param name="summary">输出：分类统计文本（如 "圆形x2 矩形x3"）</param>
        /// <returns>结果图像（边缘底图 + 彩色轮廓与标签）</returns>
        public Mat Detect(Mat grayFrame, out int shapeCount, out string summary)
        {
            using Mat blurred = new Mat();
            Cv2.GaussianBlur(grayFrame, blurred, new Size(5, 5), 1.5);

            using Mat edges = new Mat();
            Cv2.Canny(blurred, edges, 80, 180);

            using Mat kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
            Cv2.Dilate(edges, edges, kernel);

            Cv2.FindContours(edges, out OpenCvSharp.Point[][] contours, out _,
                RetrievalModes.External, ContourApproximationModes.ApproxSimple);

            Mat result = new Mat();
            Cv2.CvtColor(edges, result, ColorConversionCodes.GRAY2BGR);

            var counts = new Dictionary<string, int>();
            shapeCount = 0;

            foreach (var contour in contours)
            {
                double area = Cv2.ContourArea(contour);
                if (area < MinArea) continue;

                double perimeter = Cv2.ArcLength(contour, true);
                if (perimeter <= 0) continue;
                double circularity = 4 * Math.PI * area / (perimeter * perimeter);

                var approx = Cv2.ApproxPolyDP(contour, PolyEpsilon * perimeter, true);
                int vertices = approx.Length;

                // 分类：圆形度优先（圆会被逼近成多顶点多边形）
                string label;
                Scalar color;
                if (vertices > 5 && circularity > CircleCircularity)
                {
                    label = TranslationService.GetStringStatic("ShapeCircle");
                    color = new Scalar(0, 0, 255);       // 红
                }
                else if (vertices == 3)
                {
                    label = TranslationService.GetStringStatic("ShapeTriangle");
                    color = new Scalar(0, 255, 0);       // 绿
                }
                else if (vertices == 4)
                {
                    label = TranslationService.GetStringStatic("ShapeRect");
                    color = new Scalar(0, 180, 255);     // 橙
                }
                else if (vertices == 5)
                {
                    label = TranslationService.GetStringStatic("ShapePentagon");
                    color = new Scalar(255, 0, 180);     // 紫
                }
                else
                {
                    label = TranslationService.GetStringStatic("ShapePolygon");
                    color = new Scalar(255, 200, 0);     // 青黄
                }

                // 绘制轮廓 + 质心 + 类型标签
                Cv2.DrawContours(result, new[] { contour }, -1, color, 2);
                var moments = Cv2.Moments(contour);
                if (moments.M00 > 0)
                {
                    int cx = (int)(moments.M10 / moments.M00);
                    int cy = (int)(moments.M01 / moments.M00);
                    Cv2.Circle(result, new OpenCvSharp.Point(cx, cy), 3, color, -1);
                    Cv2.PutText(result, label, new OpenCvSharp.Point(cx - 22, cy - 10),
                        HersheyFonts.HersheySimplex, 0.6, color, 2, LineTypes.AntiAlias);
                }

                counts.TryGetValue(label, out int c);
                counts[label] = c + 1;
                shapeCount++;
            }

            var parts = new List<string>();
            foreach (var kv in counts)
                parts.Add($"{kv.Key}x{kv.Value}");
            summary = string.Join(" ", parts);
            return result;
        }
    }
}
