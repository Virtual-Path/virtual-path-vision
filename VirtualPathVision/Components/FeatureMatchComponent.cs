using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace VirtualPathVision.Components
{
    /// <summary>
    /// 特征点匹配组件，基于 ORB + BFMatcher 实现。
    /// 相比模板匹配，对旋转、缩放、光照变化更鲁棒，
    /// 适合作为"图片识别"：加载模板后定位画面中的同类目标。
    /// 当优秀匹配点足够多时，用 RANSAC 单应矩阵绘制定位框。
    /// </summary>
    public class FeatureMatchComponent : IDisposable
    {
        private readonly ORB _orb = ORB.Create(1500);   // ORB 特征检测器
        private readonly BFMatcher _matcher = new(NormTypes.Hamming, false); // 暴力匹配器（复用）
        private Mat? _template;                          // 模板原图
        private Mat? _templateDescriptors;               // 模板描述子
        private KeyPoint[] _templateKeypoints = Array.Empty<KeyPoint>();

        // 模板在 UI 线程加载/清除，Match 在采集线程执行 → 用同一把锁保护原生 Mat 生命周期
        private readonly object _templateLock = new();
        private bool _disposed;

        /// <summary>RANSAC 求单应矩阵所需的最小内点数</summary>
        private const int MinGoodMatches = 8;

        /// <summary>比率测试阈值：最近邻距离需小于次近邻的该比例</summary>
        private const double LoweRatio = 0.75;

        /// <summary>是否已加载模板</summary>
        public bool HasTemplate
        {
            get { lock (_templateLock) return _template != null; }
        }

        /// <summary>模板宽度</summary>
        public int TemplateWidth { get { lock (_templateLock) return _template?.Width ?? 0; } }

        /// <summary>模板高度</summary>
        public int TemplateHeight { get { lock (_templateLock) return _template?.Height ?? 0; } }

        /// <summary>
        /// 从文件加载模板并提取 ORB 特征。
        /// 特征点不足时返回 false，且保持原有模板不变。
        /// </summary>
        public bool LoadTemplate(string path)
        {
            Mat template = Cv2.ImRead(path);
            if (template.Empty())
            {
                template.Dispose();
                return false;
            }

            Mat? descriptors = null;
            try
            {
                using Mat gray = new Mat();
                Cv2.CvtColor(template, gray, ColorConversionCodes.BGR2GRAY);

                descriptors = new Mat();
                _orb.DetectAndCompute(gray, default, out var keypoints, descriptors);
                if (keypoints.Length < 4 || descriptors.Empty())
                {
                    // 特征点太少，无法可靠匹配；保持原有模板不变
                    descriptors.Dispose();
                    template.Dispose();
                    return false;
                }

                lock (_templateLock)
                {
                    _template?.Dispose();
                    _templateDescriptors?.Dispose();
                    _template = template;
                    _templateDescriptors = descriptors;
                    _templateKeypoints = keypoints;
                }
                // 所有权已转交
                descriptors = null;
                return true;
            }
            catch
            {
                descriptors?.Dispose();
                template.Dispose();
                return false;
            }
            finally
            {
                if (descriptors != null) descriptors.Dispose();
            }
        }

        /// <summary>清除模板</summary>
        public void Clear()
        {
            lock (_templateLock)
            {
                _template?.Dispose();
                _template = null;
                _templateDescriptors?.Dispose();
                _templateDescriptors = null;
                _templateKeypoints = Array.Empty<KeyPoint>();
            }
        }

        /// <summary>
        /// 在灰度帧中匹配模板特征点。
        /// </summary>
        /// <param name="grayFrame">输入灰度帧</param>
        /// <param name="matchCount">输出：优秀匹配点数量</param>
        /// <returns>结果图像（匹配成功时含绿色定位框）</returns>
        public Mat Match(Mat grayFrame, out int matchCount)
        {
            Mat result = new Mat();
            matchCount = 0;

            if (grayFrame == null || grayFrame.Empty())
                return result;

            Cv2.CvtColor(grayFrame, result, ColorConversionCodes.GRAY2BGR);

            // 持锁执行整个匹配过程：防止 Clear/LoadTemplate 在中途释放原生模板 Mat
            lock (_templateLock)
            {
                if (_templateDescriptors == null || _templateDescriptors.Empty())
                    return result;

                using var frameDescriptors = new Mat();
                _orb.DetectAndCompute(grayFrame, default, out var frameKeypoints, frameDescriptors);

                // 低纹理/过曝画面下 ORB 可能一个关键点都找不到，此时描述子是 0x0，
                // 直接送入 KnnMatch 会触发 OpenCV 的 CV_Assert 抛 cv::Exception。
                // 必须提前短路，否则每帧都会抛异常并弹错误横幅。
                if (frameDescriptors.Empty() || frameKeypoints.Length == 0)
                    return result;

                // 暴力匹配 + 最近邻/次近邻比率测试
                DMatch[][] knnMatches = _matcher.KnnMatch(_templateDescriptors, frameDescriptors, 2, null, false);

                var srcPoints = new List<Point2f>();
                var dstPoints = new List<Point2f>();
                foreach (var pair in knnMatches)
                {
                    if (pair.Length < 2) continue;
                    int query = pair[0].QueryIdx;
                    int train = pair[0].TrainIdx;
                    if (query < 0 || query >= _templateKeypoints.Length) continue;
                    if (train < 0 || train >= frameKeypoints.Length) continue;
                    if (pair[0].Distance < LoweRatio * pair[1].Distance)
                    {
                        srcPoints.Add(_templateKeypoints[query].Pt);
                        dstPoints.Add(frameKeypoints[train].Pt);
                    }
                }

                matchCount = srcPoints.Count;
                if (matchCount < MinGoodMatches)
                    return result; // 匹配点不足，不绘制定位框

                int tw = _template?.Width ?? 0;
                int th = _template?.Height ?? 0;

                using Mat? homography = Cv2.FindHomography(
                    InputArray.Create(srcPoints), InputArray.Create(dstPoints),
                    HomographyMethods.Ransac, 3.0);
                if (homography == null || homography.Empty())
                    return result;

                // 将模板四角映射到帧坐标并绘制定位框
                var corners = new Point2f[]
                {
                    new(0, 0),
                    new(tw, 0),
                    new(tw, th),
                    new(0, th)
                };
                var transformed = Cv2.PerspectiveTransform(corners, homography);
                var box = new OpenCvSharp.Point[4];
                for (int i = 0; i < 4; i++)
                    box[i] = new OpenCvSharp.Point((int)transformed[i].X, (int)transformed[i].Y);

                Cv2.Polylines(result, new[] { box }, true, new Scalar(0, 255, 0), 3);
                Cv2.PutText(result, $"Match {matchCount}",
                    new OpenCvSharp.Point(box[0].X, Math.Max(20, box[0].Y - 10)),
                    HersheyFonts.HersheySimplex, 0.8, new Scalar(0, 255, 0), 2, LineTypes.AntiAlias);
            }
            return result;
        }

        /// <summary>释放 ORB / BFMatcher 等原生句柄与模板</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Clear();
            _orb.Dispose();
            _matcher.Dispose();
        }
    }
}
