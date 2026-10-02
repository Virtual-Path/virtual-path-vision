using OpenCvSharp;

namespace VirtualPathVision.Components
{
    /// <summary>图像处理模式枚举</summary>
    public enum ProcessingMode
    {
        Canny,          // Canny 边缘检测
        Sobel,          // Sobel 梯度
        Laplacian,      // Laplacian 算子
        Binary,         // 二值化
        Contour,        // 轮廓检测
        QRCode,         // 二维码/条码识别
        ColorDetection, // 颜色检测
        TemplateMatch,  // 模板匹配
        ShapeDetection, // 形状识别
        FeatureMatch,   // 特征点匹配
        Enhancement     // 图像增强
    }

    /// <summary>
    /// 图像处理组件，提供多种视觉算法：
    /// Canny/Sobel/Laplacian/二值化/轮廓检测。
    /// 所有方法都包含预处理步骤（高斯模糊）以提高结果质量。
    /// </summary>
    public class ImageProcessingComponent
    {
        private int _gaussianKernelSize = 5;

        /// <summary>高斯模糊核大小（奇数，默认 5）。OpenCV 对 ksize 有上限约束，这里限制在 3~99。</summary>
        public int GaussianKernelSize
        {
            get => _gaussianKernelSize;
            set
            {
                int v = Math.Clamp(value, 3, 99);
                _gaussianKernelSize = v % 2 == 1 ? v : v + 1;
            }
        }

        /// <summary>
        /// 按指定模式处理灰度图像。
        /// </summary>
        /// <param name="grayFrame">输入灰度图</param>
        /// <param name="mode">处理模式</param>
        /// <param name="threshold1">Canny/二值化低阈值</param>
        /// <param name="threshold2">Canny/二值化高阈值</param>
        /// <param name="contourCount">输出参数：轮廓数量（仅 Contour 模式）</param>
        /// <returns>处理后的图像</returns>
        public Mat Process(Mat grayFrame, ProcessingMode mode, int threshold1, int threshold2, out int contourCount)
        {
            contourCount = 0;

            // 空图直接返回，避免 OpenCV 内部断言失败
            if (grayFrame == null || grayFrame.Empty())
                return new Mat();

            using Mat blurred = new Mat();
            Cv2.GaussianBlur(grayFrame, blurred, new Size(_gaussianKernelSize, _gaussianKernelSize), 1.5);

            return mode switch
            {
                ProcessingMode.Canny => ProcessCanny(blurred, threshold1, threshold2),
                ProcessingMode.Sobel => ProcessSobel(blurred),
                ProcessingMode.Laplacian => ProcessLaplacian(blurred),
                ProcessingMode.Binary => ProcessBinary(blurred, threshold1),
                ProcessingMode.Contour => ProcessContour(blurred, threshold1, threshold2, out contourCount),
                _ => ProcessCanny(blurred, threshold1, threshold2)
            };
        }

        /// <summary>Canny 边缘检测</summary>
        private static Mat ProcessCanny(Mat blurred, int t1, int t2)
        {
            Mat edges = new Mat();
            Cv2.Canny(blurred, edges, t1, t2);
            return edges;
        }

        /// <summary>Sobel 梯度幅值</summary>
        private static Mat ProcessSobel(Mat blurred)
        {
            // gradX/gradY 仅为中间量，必须释放，否则每帧泄漏两个原生 Mat
            using Mat gradX = new Mat();
            using Mat gradY = new Mat();
            Mat grad = new Mat();
            Cv2.Sobel(blurred, gradX, MatType.CV_16S, 1, 0, 3);
            Cv2.Sobel(blurred, gradY, MatType.CV_16S, 0, 1, 3);
            Cv2.ConvertScaleAbs(gradX, gradX);
            Cv2.ConvertScaleAbs(gradY, gradY);
            Cv2.AddWeighted(gradX, 0.5, gradY, 0.5, 0, grad);
            return grad;
        }

        /// <summary>Laplacian 边缘检测</summary>
        private static Mat ProcessLaplacian(Mat blurred)
        {
            Mat lap = new Mat();
            Cv2.Laplacian(blurred, lap, MatType.CV_16S, 3);
            Cv2.ConvertScaleAbs(lap, lap);
            return lap;
        }

        /// <summary>二值化（大津法或固定阈值）</summary>
        private static Mat ProcessBinary(Mat blurred, int threshold)
        {
            Mat binary = new Mat();
            if (threshold <= 0)
                Cv2.Threshold(blurred, binary, 0, 255, ThresholdTypes.Otsu);
            else
                Cv2.Threshold(blurred, binary, threshold, 255, ThresholdTypes.Binary);
            return binary;
        }

        /// <summary>轮廓检测：返回边缘 + 在原图上绘制轮廓并计数</summary>
        private Mat ProcessContour(Mat blurred, int t1, int t2, out int count)
        {
            // 先用 Canny 得到二值边缘
            using Mat edges = new Mat();
            Cv2.Canny(blurred, edges, t1, t2);

            // 膨胀使边缘连续（kernel 为原生 Mat，必须释放）
            using Mat dilated = new Mat();
            using Mat kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
            Cv2.Dilate(edges, dilated, kernel);

            // 查找轮廓
            Cv2.FindContours(dilated, out OpenCvSharp.Point[][] contours, out _,
                RetrievalModes.External, ContourApproximationModes.ApproxSimple);

            // 在彩色底图上绘制轮廓
            Mat result = new Mat();
            Cv2.CvtColor(edges, result, ColorConversionCodes.GRAY2BGR);
            for (int i = 0; i < contours.Length; i++)
            {
                Scalar color = new Scalar(0, 255, 0);
                Cv2.DrawContours(result, contours, i, color, 2);
            }

            count = contours.Length;
            return result;
        }
    }
}
