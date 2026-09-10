using OpenCvSharp;

namespace MachineVisionApp.AI
{
    /// <summary>
    /// 主动感知引擎，协调 YOLO 检测、Kalman 跟踪和视觉分析。
    /// 实现"主动感知"范式：根据检测结果动态调整感知策略，
    /// 如对高置信度目标区域增加采样频率、对丢失目标进行预测搜索。
    /// </summary>
    public class ActivePerceptionEngine
    {
        private readonly YoloDetectionComponent _detector;
        private readonly KalmanTrackerComponent _tracker;

        // 主动感知参数
        private int _frameCount;
        private int _adaptiveInterval = 1;       // 自适应采样间隔（帧）
        private readonly int _minInterval = 1;   // 最小采样间隔
        private readonly int _maxInterval = 5;   // 最大采样间隔

        // 兴趣区域（ROI）管理
        private readonly List<Rect> _regionsOfInterest = new();
        private readonly int _maxRois = 5;

        /// <summary>当前帧的检测结果</summary>
        public List<Detection> CurrentDetections { get; private set; } = new();

        /// <summary>当前跟踪目标</summary>
        public List<TrackedObject> CurrentTracks { get; private set; } = new();

        /// <summary>当前自适应采样间隔</summary>
        public int AdaptiveInterval => _adaptiveInterval;

        /// <summary>当前激活的兴趣区域数量</summary>
        public int ActiveRoiCount => _regionsOfInterest.Count;

        /// <summary>
        /// 初始化主动感知引擎。
        /// </summary>
        public ActivePerceptionEngine(
            YoloDetectionComponent detector,
            KalmanTrackerComponent tracker)
        {
            _detector = detector;
            _tracker = tracker;
        }

        /// <summary>
        /// 处理一帧图像，执行检测 → 跟踪 → 策略调整的完整流程。
        /// </summary>
        /// <param name="frame">输入 BGR 帧</param>
        /// <returns>当前帧是否需要执行完整检测（自适应采样）</returns>
        public bool ProcessFrame(Mat frame)
        {
            _frameCount++;

            // 自适应采样：根据场景复杂度调整检测频率
            bool shouldDetect = _frameCount % _adaptiveInterval == 0;

            if (shouldDetect)
            {
                // 在全局或 ROI 区域执行检测
                CurrentDetections = DetectInRegions(frame);

                // 更新跟踪器
                CurrentTracks = _tracker.Update(CurrentDetections);

                // 根据跟踪状态调整策略
                AdjustPerceptionStrategy();
            }
            else
            {
                // 非检测帧：仅用 Kalman 预测维持跟踪状态
                CurrentTracks = _tracker.Update(new List<Detection>());
            }

            return shouldDetect;
        }

        /// <summary>
        /// 在全局和 ROI 区域执行目标检测。
        /// </summary>
        private List<Detection> DetectInRegions(Mat frame)
        {
            var allDetections = new List<Detection>();

            // 全局检测
            var globalDetections = _detector.Detect(frame);
            allDetections.AddRange(globalDetections);

            // 在 ROI 区域执行局部检测（提高小目标检出率）
            foreach (var roi in _regionsOfInterest)
            {
                // 确保 ROI 在帧范围内
                var safeRoi = ClampRoi(roi, frame.Width, frame.Height);
                using Mat roiMat = new Mat(frame, safeRoi);
                var roiDetections = _detector.Detect(roiMat);

                // 将 ROI 检测坐标转换回全局坐标
                foreach (var det in roiDetections)
                {
                    det.BoundingBox = new Rect(
                        det.BoundingBox.X + safeRoi.X,
                        det.BoundingBox.Y + safeRoi.Y,
                        det.BoundingBox.Width,
                        det.BoundingBox.Height);
                    det.Center = new Point2f(
                        det.Center.X + safeRoi.X,
                        det.Center.Y + safeRoi.Y);
                }

                // 避免重复（与全局检测去重）
                foreach (var roiDet in roiDetections)
                {
                    bool isDuplicate = allDetections.Any(g =>
                        Math.Abs(g.Center.X - roiDet.Center.X) < 30 &&
                        Math.Abs(g.Center.Y - roiDet.Center.Y) < 30);
                    if (!isDuplicate)
                        allDetections.Add(roiDet);
                }
            }

            return allDetections;
        }

        /// <summary>
        /// 根据跟踪状态自适应调整感知策略。
        /// </summary>
        private void AdjustPerceptionStrategy()
        {
            int activeCount = _tracker.ActiveCount;
            int lostCount = CurrentTracks.Count(t => t.IsLost);

            // 策略 1：目标多时降低采样频率（节省算力）
            if (activeCount > 10)
                _adaptiveInterval = Math.Min(_adaptiveInterval + 1, _maxInterval);
            // 策略 2：有丢失目标时提高采样频率（尝试重新捕获）
            else if (lostCount > 0 || activeCount < 3)
                _adaptiveInterval = Math.Max(_adaptiveInterval - 1, _minInterval);
            // 策略 3：正常情况下保持中等频率
            else
                _adaptiveInterval = 2;

            // 策略 4：为运动目标创建 ROI
            UpdateRegionsOfInterest();
        }

        /// <summary>
        /// 根据跟踪目标的运动趋势更新兴趣区域。
        /// 对高速运动的目标预测其未来位置，作为 ROI。
        /// </summary>
        private void UpdateRegionsOfInterest()
        {
            _regionsOfInterest.Clear();

            foreach (var track in CurrentTracks.Where(t => !t.IsLost))
            {
                // 根据速度预测下一步可能位置
                var predicted = track.PredictedNextPosition;
                if (predicted == default) continue;

                float speed = MathF.Sqrt(
                    track.Velocity.X * track.Velocity.X +
                    track.Velocity.Y * track.Velocity.Y);

                // 只对运动目标创建 ROI
                if (speed > 2.0f)
                {
                    int roiSize = Math.Max(track.BoundingBox.Width, track.BoundingBox.Height) * 2;
                    var roi = new Rect(
                        (int)predicted.X - roiSize / 2,
                        (int)predicted.Y - roiSize / 2,
                        roiSize, roiSize);
                    _regionsOfInterest.Add(roi);

                    if (_regionsOfInterest.Count >= _maxRois)
                        break;
                }
            }
        }

        /// <summary>在帧上绘制所有感知信息</summary>
        public void DrawOverlay(Mat frame)
        {
            // 绘制 ROI 区域（黄色虚线框）
            foreach (var roi in _regionsOfInterest)
            {
                Cv2.Rectangle(frame, roi, new Scalar(0, 255, 255), 1, LineTypes.AntiAlias);
            }

            // 绘制检测结果
            _detector.DrawDetections(frame, CurrentDetections);

            // 绘制跟踪结果（含轨迹和预测）
            _tracker.DrawTracks(frame);

            // 状态信息面板
            DrawStatusPanel(frame);
        }

        /// <summary>绘制状态面板</summary>
        private void DrawStatusPanel(Mat frame)
        {
            int panelY = 20;
            int lineHeight = 22;

            string[] lines = {
                $"[Active Perception] Frame: {_frameCount}",
                $"Detections: {CurrentDetections.Count}",
                $"Tracks: {CurrentTracks.Count} (Active: {_tracker.ActiveCount})",
                $"Sample Interval: {_adaptiveInterval}",
                $"ROI Regions: {_regionsOfInterest.Count}"
            };

            // 背景面板
            Cv2.Rectangle(frame,
                new Rect(10, 10, 280, lines.Length * lineHeight + 16),
                new Scalar(0, 0, 0), Cv2.FILLED);
            Cv2.Rectangle(frame,
                new Rect(10, 10, 280, lines.Length * lineHeight + 16),
                new Scalar(100, 100, 100), 1);

            foreach (var line in lines)
            {
                Cv2.PutText(frame, line,
                    new OpenCvSharp.Point(18, panelY + 4),
                    HersheyFonts.HersheySimplex, 0.45, new Scalar(0, 255, 0), 1, LineTypes.AntiAlias);
                panelY += lineHeight;
            }
        }

        /// <summary>将 ROI 裁剪到帧范围内</summary>
        private static Rect ClampRoi(Rect roi, int width, int height)
        {
            int x = Math.Max(0, roi.X);
            int y = Math.Max(0, roi.Y);
            int w = Math.Min(roi.Width, width - x);
            int h = Math.Min(roi.Height, height - y);
            return new Rect(x, y, Math.Max(w, 1), Math.Max(h, 1));
        }

        /// <summary>重置引擎状态</summary>
        public void Reset()
        {
            _frameCount = 0;
            _adaptiveInterval = 1;
            _regionsOfInterest.Clear();
            _tracker.Reset();
            CurrentDetections = new List<Detection>();
            CurrentTracks = new List<TrackedObject>();
        }
    }
}
