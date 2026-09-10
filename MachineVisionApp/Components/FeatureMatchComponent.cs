using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace MachineVisionApp.Components
{
    /// <summary>
    /// 特征点匹配组件，基于 ORB + BFMatcher 实现。
    /// 相比模板匹配，对旋转、缩放、光照变化更鲁棒，
    /// 适合作为"图片识别"：加载模板后定位画面中的同类目标。
    /// 当优秀匹配点足够多时，用 RANSAC 单应矩阵绘制定位框。
    /// </summary>
    public class FeatureMatchComponent
    {
        private readonly ORB _orb = ORB.Create(1500);   // ORB 特征检测器
        private readonly BFMatcher _matcher = new(NormTypes.Hamming, false); // 暴力匹配器（复用）
        private Mat? _template;                          // 模板原图
        private Mat? _templateDescriptors;               // 模板描述子
        private KeyPoint[] _templateKeypoints = Array.Empty<KeyPoint>();

        /// <summary>是否已加载模板</summary>
        public bool HasTemplate => _template != null;

        /// <summary>模板宽度</summary>
        public int TemplateWidth => _template?.Width ?? 0;

        /// <summary>模板高度</summary>
        public int TemplateHeight => _template?.Height ?? 0;

        /// <summary>
        /// 从文件加载模板并提取 ORB 特征。
        /// </summary>
        public bool LoadTemplate(string path)
        {
            Mat template = Cv2.ImRead(path);
            if (template.Empty())
                return false;

            using Mat gray = new Mat();
            Cv2.CvtColor(template, gray, ColorConversionCodes.BGR2GRAY);

            var descriptors = new Mat();
            _orb.DetectAndCompute(gray, default, out var keypoints, descriptors);
            if (keypoints.Length < 4)
            {
                descriptors.Dispose();
                template.Dispose();
                return false; // 特征点太少，无法可靠匹配
            }

            Clear();
            _template = template;
            _templateKeypoints = keypoints;
            _templateDescriptors = descriptors;
            return true;
        }

        /// <summary>清除模板</summary>
        public void Clear()
        {
            _template?.Dispose();
            _template = null;
            _templateDescriptors?.Dispose();
            _templateDescriptors = null;
            _templateKeypoints = Array.Empty<KeyPoint>();
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
            Cv2.CvtColor(grayFrame, result, ColorConversionCodes.GRAY2BGR);
            matchCount = 0;

            if (_templateDescriptors == null)
                return result;

            using var frameDescriptors = new Mat();
            _orb.DetectAndCompute(grayFrame, default, out var frameKeypoints, frameDescriptors);

            // 暴力匹配 + 最近邻/次近邻比率测试
            DMatch[][] knnMatches = _matcher.KnnMatch(_templateDescriptors, frameDescriptors, 2, null, false);

            var goodMatches = new List<DMatch>();
            var srcPoints = new List<Point2f>();
            var dstPoints = new List<Point2f>();
            foreach (var pair in knnMatches)
            {
                if (pair.Length < 2) continue;
                if (pair[0].Distance < 0.75 * pair[1].Distance)
                {
                    goodMatches.Add(pair[0]);
                    srcPoints.Add(_templateKeypoints[pair[0].QueryIdx].Pt);
                    dstPoints.Add(frameKeypoints[pair[0].TrainIdx].Pt);
                }
            }

            matchCount = goodMatches.Count;
            if (matchCount < 8)
                return result; // 匹配点不足，不绘制定位框

            using Mat? homography = Cv2.FindHomography(
                InputArray.Create(srcPoints), InputArray.Create(dstPoints),
                HomographyMethods.Ransac, 3.0);
            if (homography == null || homography.Empty())
                return result;

            // 将模板四角映射到帧坐标并绘制定位框
            var corners = new Point2f[]
            {
                new(0, 0),
                new(TemplateWidth, 0),
                new(TemplateWidth, TemplateHeight),
                new(0, TemplateHeight)
            };
            var transformed = Cv2.PerspectiveTransform(corners, homography);
            var box = new OpenCvSharp.Point[4];
            for (int i = 0; i < 4; i++)
                box[i] = new OpenCvSharp.Point((int)transformed[i].X, (int)transformed[i].Y);

            Cv2.Polylines(result, new[] { box }, true, new Scalar(0, 255, 0), 3);
            Cv2.PutText(result, $"Match {matchCount}",
                new OpenCvSharp.Point(box[0].X, Math.Max(20, box[0].Y - 10)),
                HersheyFonts.HersheySimplex, 0.8, new Scalar(0, 255, 0), 2, LineTypes.AntiAlias);
            return result;
        }
    }
}
