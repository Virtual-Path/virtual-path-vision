using System;
using OpenCvSharp;

namespace VirtualPathVision.Components
{
    /// <summary>
    /// 模板匹配组件，基于 OpenCV MatchTemplate 实现。
    /// 使用归一化相关系数法（CCoeffNormed）在灰度图上滑动匹配模板，
    /// 当最高匹配度超过阈值时在画面上绘制绿色定位框和匹配分数。
    /// </summary>
    public class TemplateMatchComponent : IDisposable
    {
        private Mat? _template;     // 原始模板图像
        private Mat? _templateGray; // 灰度模板（用于匹配）

        // 模板在 UI 线程加载/清除，而 Match 在采集线程执行。
        // 必须用同一把锁保护，避免原生 Mat 在 MatchTemplate 执行中途被释放。
        private readonly object _templateLock = new();

        /// <summary>匹配阈值（0~1），低于该值的匹配结果不显示</summary>
        public double Threshold
        {
            get => _threshold;
            set => _threshold = Math.Clamp(value, 0.0, 1.0);
        }

        private double _threshold = 0.6;

        /// <summary>是否已加载模板</summary>
        public bool HasTemplate
        {
            get { lock (_templateLock) return _template != null; }
        }

        /// <summary>模板宽度</summary>
        public int TemplateWidth { get { lock (_templateLock) return _templateGray?.Width ?? 0; } }

        /// <summary>模板高度</summary>
        public int TemplateHeight { get { lock (_templateLock) return _templateGray?.Height ?? 0; } }

        /// <summary>
        /// 从文件加载模板图像并转为灰度。
        /// 加载失败时保持原有模板不变（不进入半更新状态）。
        /// </summary>
        /// <param name="path">模板图片路径</param>
        /// <returns>是否加载成功</returns>
        public bool LoadTemplate(string path)
        {
            Mat template = Cv2.ImRead(path);
            if (template.Empty())
            {
                template.Dispose();
                return false;
            }

            // 先在锁外完成可能抛异常的转换，锁内只做指针交换。
            // 注意：template / templateGray 的所有权在成功时转交给本组件，
            // 因此这里不能用 using（否则会把刚存下的 Mat 又释放掉）。
            Mat templateGray = new Mat();
            try
            {
                Cv2.CvtColor(template, templateGray, ColorConversionCodes.BGR2GRAY);
            }
            catch
            {
                template.Dispose();
                templateGray.Dispose();
                return false;
            }

            lock (_templateLock)
            {
                _template?.Dispose();
                _templateGray?.Dispose();
                _template = template;
                _templateGray = templateGray;
            }
            return true;
        }

        /// <summary>清除已加载的模板</summary>
        public void Clear()
        {
            lock (_templateLock)
            {
                _template?.Dispose();
                _template = null;
                _templateGray?.Dispose();
                _templateGray = null;
            }
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
            score = 0;

            if (grayFrame == null || grayFrame.Empty())
                return result;

            Cv2.CvtColor(grayFrame, result, ColorConversionCodes.GRAY2BGR);

            // 持锁执行整个匹配过程：防止 Clear/LoadTemplate 在中途释放原生模板 Mat
            lock (_templateLock)
            {
                if (_templateGray == null) return result;

                int tw = _templateGray.Width, th = _templateGray.Height;
                if (grayFrame.Width < tw || grayFrame.Height < th)
                    return result;

                using Mat matchResult = new Mat();
                Cv2.MatchTemplate(grayFrame, _templateGray, matchResult, TemplateMatchModes.CCoeffNormed);
                Cv2.MinMaxLoc(matchResult, out _, out double maxVal, out _, out OpenCvSharp.Point maxLoc);

                // 模板零方差时 CCoeffNormed 会产生 NaN，必须拦截，否则 UI 会显示 "NaN%"
                if (double.IsNaN(maxVal) || double.IsInfinity(maxVal))
                    return result;

                score = maxVal;

                if (maxVal >= Threshold)
                {
                    var rect = new OpenCvSharp.Rect(maxLoc.X, maxLoc.Y, tw, th);
                    Cv2.Rectangle(result, rect, new Scalar(0, 255, 0), 2);
                    Cv2.PutText(result, $"{maxVal:P1}",
                        new OpenCvSharp.Point(maxLoc.X, Math.Max(20, maxLoc.Y - 6)),
                        HersheyFonts.HersheySimplex, 0.8, new Scalar(0, 255, 0), 2, LineTypes.AntiAlias);
                }
            }

            return result;
        }

        /// <summary>释放已加载的模板（与 Clear 等价，供 IDisposable 使用）</summary>
        public void Dispose() => Clear();
    }
}
