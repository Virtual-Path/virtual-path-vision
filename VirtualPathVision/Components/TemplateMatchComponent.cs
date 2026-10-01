using System;
using OpenCvSharp;

namespace VirtualPathVision.Components
{
    /// <summary>
    /// 模板匹配组件，基于 OpenCV MatchTemplate 实现。
    /// 使用归一化相关系数法（CCoeffNormed）在灰度图上滑动匹配模板，
    /// 当最高匹配度超过阈值时在画面上绘制绿色定位框和匹配分数。
    /// </summary>
    public class TemplateMatchComponent
    {
        private Mat? _template;     // 原始模板图像
        private Mat? _templateGray; // 灰度模板（用于匹配）

        /// <summary>匹配阈值（0~1），低于该值的匹配结果不显示</summary>
        public double Threshold { get; set; } = 0.6;

        /// <summary>是否已加载模板</summary>
        public bool HasTemplate => _template != null;

        /// <summary>模板宽度</summary>
        public int TemplateWidth => _templateGray?.Width ?? 0;

        /// <summary>模板高度</summary>
        public int TemplateHeight => _templateGray?.Height ?? 0;

        /// <summary>
        /// 从文件加载模板图像并转为灰度。
        /// </summary>
        /// <param name="path">模板图片路径</param>
        /// <returns>是否加载成功</returns>
        public bool LoadTemplate(string path)
        {
            Mat template = Cv2.ImRead(path);
            if (template.Empty())
                return false;

            _templateGray?.Dispose();
            _templateGray = new Mat();
            Cv2.CvtColor(template, _templateGray, ColorConversionCodes.BGR2GRAY);
            _template?.Dispose();
            _template = template;
            return true;
        }

        /// <summary>清除已加载的模板</summary>
        public void Clear()
        {
            _template?.Dispose();
            _template = null;
            _templateGray?.Dispose();
            _templateGray = null;
        }

        /// <summary>
        /// 在灰度帧中匹配模板。
        /// </summary>
        /// <param name="grayFrame">输入灰度帧</param>
        /// <param name="score">输出：最高匹配分数（0~1）</param>
        /// <returns>结果图像（彩色，含定位框和分数）</returns>
        public Mat Match(Mat grayFrame, out double score)
        {
            Mat result = new Mat();
            Cv2.CvtColor(grayFrame, result, ColorConversionCodes.GRAY2BGR);
            score = 0;

            if (_templateGray == null) return result;
            if (grayFrame.Width < _templateGray.Width || grayFrame.Height < _templateGray.Height)
                return result;

            using Mat matchResult = new Mat();
            Cv2.MatchTemplate(grayFrame, _templateGray, matchResult, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(matchResult, out _, out double maxVal, out _, out OpenCvSharp.Point maxLoc);
            score = maxVal;

            if (maxVal >= Threshold)
            {
                var rect = new OpenCvSharp.Rect(maxLoc.X, maxLoc.Y, _templateGray.Width, _templateGray.Height);
                Cv2.Rectangle(result, rect, new Scalar(0, 255, 0), 2);
                Cv2.PutText(result, $"{maxVal:P1}", new OpenCvSharp.Point(maxLoc.X, Math.Max(20, maxLoc.Y - 6)),
                    HersheyFonts.HersheySimplex, 0.8, new Scalar(0, 255, 0), 2, LineTypes.AntiAlias);
            }

            return result;
        }
    }
}
