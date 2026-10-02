using OpenCvSharp;

namespace VirtualPathVision.AI
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

        // 自适应采样状态（_frameCount 用 long，避免 2^31 帧后溢出为负数）
        private long _frameCount;
        private long _detectionFrameIndex;                 // 检测帧计数（仅在执行检测时递增）
        private int _adaptiveInterval = 1;                 // 自适应采样间隔（帧）
        private readonly int _minInterval = 1;             // 最小采样间隔
        private readonly int _maxInterval = 5;             // 最大采样间隔

        // 兴趣区域（ROI）管理
        private readonly List<Rect> _regionsOfInterest = new();
        private readonly int _maxRois = 5;                 // 最多保留的 ROI 数量
        private readonly int _maxRoisPerFrame = 2;         // 单个检测帧最多执行的 ROI 推理次数
        private readonly int _roiDetectInterval = 3;       // ROI 推理帧间隔（按检测帧计数）

        // 去重参数
        private const float MinDedupTolerance = 30f;       // 去重容差下限(px)
        private const float DedupToleranceRatio = 0.5f;    // 去重容差 = 比例 * 目标尺寸

        // ROI 最小边长：过小的 ROI 送入网络没有意义，且放大误差会放大检测结果
        private const int MinRoiSize = 24;

        /// <summary>运动目标才建立 ROI 的最小速度(px/frame)</summary>
        private const float RoiMinSpeed = 2.0f;

        private readonly object _stateLock = new();

        // 已发布状态：发布后不再就地修改，外部只能拿到快照
        private List<Detection> _currentDetections = new();
        private List<TrackedObject> _currentTracks = new();

        /// <summary>
        /// 当前帧的检测结果（返回副本，UI 线程可安全读取）。
        /// </summary>
        public List<Detection> CurrentDetections
        {
            get
            {
                lock (_stateLock) return new List<Detection>(_currentDetections);
            }
        }

        /// <summary>
        /// 当前跟踪目标（返回副本；元素为跟踪器的状态快照，UI 线程可安全读取）。
        /// </summary>
        public List<TrackedObject> CurrentTracks
        {
            get
            {
                lock (_stateLock) return new List<TrackedObject>(_currentTracks);
            }
        }

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
            if (frame is null || frame.Empty())
                return false;

            _frameCount++;

            // 自适应采样：根据场景复杂度调整检测频率
            bool shouldDetect = _frameCount % _adaptiveInterval == 0;

            if (shouldDetect)
            {
                // 在全局或 ROI 区域执行检测
                var detections = DetectInRegions(frame);
                Publish(detections, _tracker.Update(detections));

                // 根据跟踪状态调整策略
                AdjustPerceptionStrategy();
            }
            else
            {
                // 非检测帧：仅用 Kalman 预测维持跟踪状态
                // 注意：这里会让所有轨迹的 FramesSinceUpdate 递增，因此
                // maxLostFrames 实际约束的是"允许跳过的总帧数"，此处保持原有行为不变。
                Publish(_currentDetections, _tracker.Update(new List<Detection>()));
            }

            return shouldDetect;
        }

        /// <summary>
        /// 发布本帧状态（原子替换，外部通过快照读取）。
        /// </summary>
        private void Publish(List<Detection> detections, List<TrackedObject> tracks)
        {
            lock (_stateLock)
            {
                _currentDetections = detections;
                _currentTracks = tracks;
            }
        }

        /// <summary>
        /// 在全局和 ROI 区域执行目标检测。
        /// </summary>
        private List<Detection> DetectInRegions(Mat frame)
        {
            var allDetections = new List<Detection>();

            // 全局检测（每次检测帧一次推理，覆盖常规目标）
            var globalDetections = _detector.Detect(frame);
            allDetections.AddRange(globalDetections);

            // ---- ROI 局部检测的算力取舍 ----
            // ROI 推理与全局推理是同一个 640x640 网络，单次耗时相同。若每帧都跑满 _maxRois(5) 个 ROI，
            // 单帧最多 6 次 CPU 推理，实时帧率会被直接压垮。
            // 因此：每 _roiDetectInterval(3) 个检测帧才执行一次 ROI，且单帧最多 _maxRoisPerFrame(2) 个 ROI。
            // 代价是小目标/偶发漏检目标的复查频率下降；收益是检测帧耗时可控且仍保留 ROI 增强效果。
            bool runRoiPass = _detectionFrameIndex % _roiDetectInterval == 0;
            int roiBudget = runRoiPass ? _maxRoisPerFrame : 0;
            _detectionFrameIndex++;

            int roiCount = Math.Min(roiBudget, _regionsOfInterest.Count);
            for (int i = 0; i < roiCount; i++)
            {
                var roi = _regionsOfInterest[i];

                // 确保 ROI 完全落在帧范围内；越界/过小直接跳过，
                // 否则 new Mat(frame, roi) 会触发 OpenCVSharp 的 ROI 断言异常
                var safeRoi = ClampRoi(roi, frame.Width, frame.Height);
                if (safeRoi.Width <= 0 || safeRoi.Height <= 0)
                    continue;

                using Mat roiMat = new Mat(frame, safeRoi);
                var roiDetections = _detector.Detect(roiMat);
                if (roiDetections.Count == 0)
                    continue;

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

                // 与已有检测（全局 + 已处理的 ROI）去重，容差随目标尺寸成比例放大
                foreach (var roiDet in roiDetections)
                {
                    bool isDuplicate = false;
                    foreach (var g in allDetections)
                    {
                        float tol = MathF.Max(MinDedupTolerance,
                            DedupToleranceRatio * MathF.Max(
                                MathF.Max(g.BoundingBox.Width, g.BoundingBox.Height),
                                MathF.Max(roiDet.BoundingBox.Width, roiDet.BoundingBox.Height)));
                        if (MathF.Abs(g.Center.X - roiDet.Center.X) < tol &&
                            MathF.Abs(g.Center.Y - roiDet.Center.Y) < tol)
                        {
                            isDuplicate = true;
                            break;
                        }
                    }

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
            int lostCount = _currentTracks.Count(t => t.IsLost);

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

            foreach (var track in _currentTracks)
            {
                if (track.IsLost) continue;

                // 根据速度预测下一步可能位置（跟踪器每帧都会刷新该预测）
                var predicted = track.PredictedNextPosition;
                if (predicted == default) continue;
                if (!float.IsFinite(predicted.X) || !float.IsFinite(predicted.Y)) continue;

                float speed = MathF.Sqrt(
                    track.Velocity.X * track.Velocity.X +
                    track.Velocity.Y * track.Velocity.Y);

                // 只对运动目标创建 ROI
                if (speed > RoiMinSpeed)
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
            _detector.DrawDetections(frame, _currentDetections);

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
                $"Detections: {_currentDetections.Count}",
                $"Tracks: {_currentTracks.Count} (Active: {_tracker.ActiveCount})",
                $"Sample Interval: {_adaptiveInterval}",
                $"ROI Regions: {_regionsOfInterest.Count}"
            };

            int panelWidth = Math.Min(280, Math.Max(40, frame.Width - 20));
            int panelHeight = Math.Min(lines.Length * lineHeight + 16, Math.Max(lineHeight + 16, frame.Height - 20));

            // 背景面板
            Cv2.Rectangle(frame,
                new Rect(10, 10, panelWidth, panelHeight),
                new Scalar(0, 0, 0), Cv2.FILLED);
            Cv2.Rectangle(frame,
                new Rect(10, 10, panelWidth, panelHeight),
                new Scalar(100, 100, 100), 1);

            foreach (var line in lines)
            {
                Cv2.PutText(frame, line,
                    new OpenCvSharp.Point(18, panelY + 4),
                    HersheyFonts.HersheySimplex, 0.45, new Scalar(0, 255, 0), 1, LineTypes.AntiAlias);
                panelY += lineHeight;
            }
        }

        /// <summary>
        /// 将 ROI 与帧范围求交，并把结果裁剪到帧内。
        /// 四个边界都会裁剪；完全不相交（或裁剪后小于 <see cref="MinRoiSize"/>）时返回
        /// 宽度/高度为 0 的无效 Rect，调用方必须据此跳过 ROI Mat 的构造。
        /// </summary>
        private static Rect ClampRoi(Rect roi, int width, int height)
        {
            if (width <= 0 || height <= 0 || roi.Width <= 0 || roi.Height <= 0)
                return default;

            // 左/上边界裁剪
            int x1 = Math.Max(0, roi.X);
            int y1 = Math.Max(0, roi.Y);
            // 右/下边界裁剪（同样不能越界，否则 new Mat(frame, roi) 断言失败）
            int x2 = Math.Min(width, roi.X + roi.Width);
            int y2 = Math.Min(height, roi.Y + roi.Height);

            int w = x2 - x1;
            int h = y2 - y1;

            if (w < MinRoiSize || h < MinRoiSize)
                return default; // 无有效交集区域

            return new Rect(x1, y1, w, h);
        }

        /// <summary>重置引擎状态</summary>
        public void Reset()
        {
            _frameCount = 0;
            _detectionFrameIndex = 0;
            _adaptiveInterval = 1;
            _regionsOfInterest.Clear();
            _tracker.Reset();
            Publish(new List<Detection>(), new List<TrackedObject>());
        }
    }
}