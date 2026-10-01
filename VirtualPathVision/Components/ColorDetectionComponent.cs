using System;
using OpenCvSharp;

namespace VirtualPathVision.Components
{
    /// <summary>
    /// 颜色检测组件，基于 HSV 色彩空间实现。
    /// 将帧转换到 HSV 后按目标颜色范围提取掩码，经形态学去噪后
    /// 查找轮廓并统计目标数量，最终在掩码图上绘制包围框。
    /// </summary>
    public class ColorDetectionComponent
    {
        /// <summary>目标颜色枚举</summary>
        public enum TargetColor
        {
            Red, Green, Blue, Yellow, Orange, Purple, Cyan, White, Black, Custom
        }

        private TargetColor _target = TargetColor.Red; // 当前目标颜色（默认红色）
        private const double MinArea = 200;            // 最小目标面积，过滤噪点
        private Scalar _customLower = new(0, 100, 100); // 自定义取色的 HSV 下界
        private Scalar _customUpper = new(10, 255, 255); // 自定义取色的 HSV 上界

        /// <summary>当前目标颜色</summary>
        public TargetColor Target
        {
            get => _target;
            set => _target = value;
        }

        /// <summary>
        /// 根据点击像素的 HSV 值设置自定义取色范围（色调 ±12，饱和度/明度向下放宽）。
        /// </summary>
        public void SetCustomRange(int hue, int saturation, int value)
        {
            int hueLow = Math.Max(hue - 12, 0);
            int hueHigh = Math.Min(hue + 12, 179);
            int satLow = Math.Clamp(saturation - 70, 30, 255);
            int valLow = Math.Clamp(value - 70, 30, 255);
            _customLower = new Scalar(hueLow, satLow, valLow);
            _customUpper = new Scalar(hueHigh, 255, 255);
        }

        /// <summary>
        /// 检测帧中指定颜色的目标区域。
        /// </summary>
        /// <param name="frame">输入 BGR 帧</param>
        /// <param name="objectCount">输出：检测到的目标数量</param>
        /// <returns>检测结果图像（掩码 + 绿色包围框）</returns>
        public Mat Detect(Mat frame, out int objectCount)
        {
            using Mat hsv = new Mat();
            Cv2.CvtColor(frame, hsv, ColorConversionCodes.BGR2HSV);

            using Mat mask = new Mat();
            BuildMask(hsv, mask);

            // 形态学开闭运算去除噪点、填充空洞
            using Mat kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
            Cv2.MorphologyEx(mask, mask, MorphTypes.Open, kernel);
            Cv2.MorphologyEx(mask, mask, MorphTypes.Close, kernel);

            // 查找轮廓并过滤过小区域
            Cv2.FindContours(mask, out OpenCvSharp.Point[][] contours, out _,
                RetrievalModes.External, ContourApproximationModes.ApproxSimple);

            Mat result = new Mat();
            Cv2.CvtColor(mask, result, ColorConversionCodes.GRAY2BGR);

            int count = 0;
            foreach (var contour in contours)
            {
                if (Cv2.ContourArea(contour) < MinArea) continue;
                OpenCvSharp.Rect rect = Cv2.BoundingRect(contour);
                Cv2.Rectangle(result, rect, new Scalar(0, 255, 0), 2);
                count++;
            }

            objectCount = count;
            return result;
        }

        /// <summary>根据目标颜色生成 HSV 掩码（红色需两段范围拼接）</summary>
        private void BuildMask(Mat hsv, Mat mask)
        {
            switch (_target)
            {
                case TargetColor.Red:
                    // 红色在 HSV 色环两端：0~10 和 170~180
                    using (Mat mask1 = new Mat(), mask2 = new Mat())
                    {
                        Cv2.InRange(hsv, new Scalar(0, 100, 100), new Scalar(10, 255, 255), mask1);
                        Cv2.InRange(hsv, new Scalar(170, 100, 100), new Scalar(180, 255, 255), mask2);
                        Cv2.BitwiseOr(mask1, mask2, mask);
                    }
                    break;
                case TargetColor.Green:
                    Cv2.InRange(hsv, new Scalar(40, 70, 70), new Scalar(85, 255, 255), mask);
                    break;
                case TargetColor.Blue:
                    Cv2.InRange(hsv, new Scalar(100, 70, 70), new Scalar(130, 255, 255), mask);
                    break;
                case TargetColor.Yellow:
                    Cv2.InRange(hsv, new Scalar(20, 100, 100), new Scalar(35, 255, 255), mask);
                    break;
                case TargetColor.Orange:
                    Cv2.InRange(hsv, new Scalar(10, 100, 100), new Scalar(20, 255, 255), mask);
                    break;
                case TargetColor.Purple:
                    Cv2.InRange(hsv, new Scalar(130, 70, 70), new Scalar(160, 255, 255), mask);
                    break;
                case TargetColor.Cyan:
                    Cv2.InRange(hsv, new Scalar(85, 70, 70), new Scalar(100, 255, 255), mask);
                    break;
                case TargetColor.White:
                    Cv2.InRange(hsv, new Scalar(0, 0, 180), new Scalar(180, 40, 255), mask);
                    break;
                case TargetColor.Black:
                    Cv2.InRange(hsv, new Scalar(0, 0, 0), new Scalar(180, 255, 60), mask);
                    break;
                case TargetColor.Custom:
                    Cv2.InRange(hsv, _customLower, _customUpper, mask);
                    break;
                default:
                    Cv2.InRange(hsv, new Scalar(0, 0, 0), new Scalar(180, 255, 255), mask);
                    break;
            }
        }
    }
}
