using OpenCvSharp;

namespace VirtualPathVision.Components
{
    /// <summary>
    /// 图像增强组件。
    /// 先进行 CLAHE 直方图均衡化增强对比度，
    /// 再通过非锐化掩模（Unsharp Masking）锐化边缘，
    /// 用于在识别前改善暗光/低对比度画面质量。
    /// </summary>
    public class EnhancementComponent
    {
        /// <summary>
        /// 增强灰度图像（CLAHE + 锐化）。
        /// </summary>
        /// <param name="grayFrame">输入灰度帧</param>
        /// <returns>增强后的图像</returns>
        public Mat Enhance(Mat grayFrame)
        {
            if (grayFrame == null || grayFrame.Empty())
                return new Mat();

            // 1. CLAHE 自适应直方图均衡：局部对比度增强
            using Mat clahe = new Mat();
            using (var c = Cv2.CreateCLAHE(2.0, new Size(8, 8)))
            {
                c.Apply(grayFrame, clahe);
            }

            // 2. 非锐化掩模：原图 + 增益 ×（原图 - 模糊图）
            using Mat blur = new Mat();
            Cv2.GaussianBlur(clahe, blur, new Size(0, 0), 3);
            Mat sharp = new Mat();
            Cv2.AddWeighted(clahe, 1.8, blur, -0.8, 0, sharp);
            return sharp;
        }
    }
}
