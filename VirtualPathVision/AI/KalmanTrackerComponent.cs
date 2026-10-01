using OpenCvSharp;

namespace VirtualPathVision.AI
{
    /// <summary>
    /// 跟踪目标的状态信息。
    /// </summary>
    public class TrackedObject
    {
        public int TrackId { get; set; }
        public string ClassName { get; set; } = "";
        public float Confidence { get; set; }
        public Rect BoundingBox { get; set; }
        public Point2f Center { get; set; }
        public Point2f Velocity { get; set; }           // 速度向量 (px/frame)
        public Point2f PredictedNextPosition { get; set; } // Kalman 预测的下一帧位置
        public int FramesSinceUpdate { get; set; }       // 距上次匹配的帧数
        public bool IsLost => FramesSinceUpdate > 10;    // 超过 10 帧未匹配视为丢失
        public Scalar Color { get; set; }                // 跟踪可视化颜色
        public List<Point2f> Trajectory { get; set; } = new(); // 运动轨迹
    }

    /// <summary>
    /// Kalman 多目标跟踪器。
    /// 为每个检测目标维护一个 Kalman 滤波器，实现：
    /// 1. 平滑目标运动轨迹
    /// 2. 预测目标下一帧位置（填补检测丢失）
    /// 3. 目标 ID 关联（匈牙利匹配）
    /// 4. 轨迹管理（创建/更新/删除）
    /// </summary>
    public class KalmanTrackerComponent
    {
        private readonly Dictionary<int, KalmanFilter> _kalmanFilters = new();
        private readonly Dictionary<int, TrackedObject> _trackedObjects = new();
        private int _nextTrackId = 1;
        private readonly int _maxLostFrames;
        private readonly float _matchThreshold;
        private static readonly Scalar[] TrackColors = {
            new(255, 0, 0), new(0, 255, 0), new(0, 0, 255),
            new(255, 255, 0), new(255, 0, 255), new(0, 255, 255),
            new(128, 0, 255), new(255, 128, 0), new(0, 128, 255),
            new(255, 0, 128)
        };

        /// <summary>当前所有活跃跟踪目标</summary>
        public IReadOnlyDictionary<int, TrackedObject> TrackedObjects => _trackedObjects;

        /// <summary>活跃跟踪目标数量</summary>
        public int ActiveCount => _trackedObjects.Count;

        /// <summary>
        /// 初始化 Kalman 跟踪器。
        /// </summary>
        /// <param name="maxLostFrames">目标丢失前的最大未匹配帧数（默认 10）</param>
        /// <param name="matchThreshold">匹配距离阈值（默认 80px）</param>
        public KalmanTrackerComponent(int maxLostFrames = 10, float matchThreshold = 80f)
        {
            _maxLostFrames = maxLostFrames;
            _matchThreshold = matchThreshold;
        }

        /// <summary>
        /// 更新跟踪器：输入新检测结果，完成匹配、预测、状态更新。
        /// </summary>
        /// <param name="detections">当前帧的检测结果</param>
        /// <returns>更新后的跟踪目标列表（含轨迹和预测）</returns>
        public List<TrackedObject> Update(List<Detection> detections)
        {
            // 1. 对所有已有 Kalman 滤波器执行预测
            var predictions = new Dictionary<int, Point2f>();
            foreach (var kvp in _kalmanFilters)
            {
                var predicted = kvp.Value.Predict();
                predictions[kvp.Key] = new Point2f(predicted.At<float>(0), predicted.At<float>(1));
            }

            // 2. 构建代价矩阵（匈牙利匹配）
            var trackIds = _kalmanFilters.Keys.ToList();
            var unmatchedTracks = new HashSet<int>(trackIds);
            var unmatchedDetections = new HashSet<int>(
                Enumerable.Range(0, detections.Count));

            if (trackIds.Count > 0 && detections.Count > 0)
            {
                // 计算距离矩阵
                float[,] costMatrix = new float[trackIds.Count, detections.Count];
                for (int t = 0; t < trackIds.Count; t++)
                {
                    var pred = predictions[trackIds[t]];
                    for (int d = 0; d < detections.Count; d++)
                    {
                        float dx = pred.X - detections[d].Center.X;
                        float dy = pred.Y - detections[d].Center.Y;
                        costMatrix[t, d] = MathF.Sqrt(dx * dx + dy * dy);
                    }
                }

                // 贪心匹配（简单高效）
                var matches = GreedyMatch(costMatrix, trackIds.Count, detections.Count, _matchThreshold);

                foreach (var (trackIdx, detIdx) in matches)
                {
                    int trackId = trackIds[trackIdx];
                    unmatchedTracks.Remove(trackId);
                    unmatchedDetections.Remove(detIdx);

                    // 更新 Kalman 滤波器
                    var measurement = new Mat(2, 1, MatType.CV_32F);
                    measurement.Set(0, 0, detections[detIdx].Center.X);
                    measurement.Set(1, 0, detections[detIdx].Center.Y);
                    _kalmanFilters[trackId].Correct(measurement);

                    // 更新跟踪状态
                    UpdateTrackedObject(trackId, detections[detIdx]);
                }
            }

            // 3. 为未匹配的已有目标更新丢失帧数
            foreach (int trackId in unmatchedTracks)
            {
                if (_trackedObjects.ContainsKey(trackId))
                {
                    _trackedObjects[trackId].FramesSinceUpdate++;
                    _trackedObjects[trackId].PredictedNextPosition = predictions.GetValueOrDefault(trackId);
                }
            }

            // 4. 为未匹配的检测创建新跟踪
            foreach (int detIdx in unmatchedDetections)
            {
                CreateNewTrack(detections[detIdx]);
            }

            // 5. 清理丢失目标
            var lostIds = _trackedObjects
                .Where(kvp => kvp.Value.IsLost)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (int id in lostIds)
            {
                _trackedObjects.Remove(id);
                if (_kalmanFilters.TryGetValue(id, out var kf))
                {
                    kf.Dispose();
                    _kalmanFilters.Remove(id);
                }
            }

            return _trackedObjects.Values.ToList();
        }

        /// <summary>
        /// 在帧上绘制跟踪结果（ID、轨迹、预测位置）。
        /// </summary>
        public void DrawTracks(Mat frame)
        {
            foreach (var obj in _trackedObjects.Values)
            {
                // 绘制轨迹线
                if (obj.Trajectory.Count > 1)
                {
                    for (int i = 1; i < obj.Trajectory.Count; i++)
                    {
                        Cv2.Line(frame,
                            new OpenCvSharp.Point((int)obj.Trajectory[i - 1].X, (int)obj.Trajectory[i - 1].Y),
                            new OpenCvSharp.Point((int)obj.Trajectory[i].X, (int)obj.Trajectory[i].Y),
                            obj.Color, 2, LineTypes.AntiAlias);
                    }
                }

                // 绘制边界框
                Cv2.Rectangle(frame, obj.BoundingBox, obj.Color, 2);

                // 绘制 ID 标签
                string label = $"ID:{obj.TrackId} {obj.ClassName}";
                Cv2.PutText(frame, label,
                    new OpenCvSharp.Point(obj.BoundingBox.X, obj.BoundingBox.Y - 8),
                    HersheyFonts.HersheySimplex, 0.5, obj.Color, 1, LineTypes.AntiAlias);

                // 绘制预测点（红色虚线圆）
                if (obj.PredictedNextPosition != default)
                {
                    Cv2.Circle(frame,
                        new OpenCvSharp.Point(
                            (int)obj.PredictedNextPosition.X,
                            (int)obj.PredictedNextPosition.Y),
                        5, new Scalar(0, 0, 255), 1, LineTypes.AntiAlias);
                }

                // 速度向量箭头
                if (obj.Velocity.X != 0 || obj.Velocity.Y != 0)
                {
                    var arrowEnd = new OpenCvSharp.Point(
                        (int)(obj.Center.X + obj.Velocity.X * 3),
                        (int)(obj.Center.Y + obj.Velocity.Y * 3));
                    Cv2.ArrowedLine(frame,
                        new OpenCvSharp.Point((int)obj.Center.X, (int)obj.Center.Y),
                        arrowEnd, new Scalar(0, 255, 255), 2, LineTypes.AntiAlias);
                }
            }
        }

        /// <summary>重置所有跟踪状态</summary>
        public void Reset()
        {
            foreach (var kf in _kalmanFilters.Values)
                kf.Dispose();
            _kalmanFilters.Clear();
            _trackedObjects.Clear();
            _nextTrackId = 1;
        }

        /// <summary>创建新的跟踪目标并初始化 Kalman 滤波器</summary>
        private void CreateNewTrack(Detection detection)
        {
            int id = _nextTrackId++;
            Scalar color = TrackColors[id % TrackColors.Length];

            // Kalman 滤波器：状态 [x, y, vx, vy]，观测 [x, y]
            var kf = new KalmanFilter(4, 2, 0);

            // 状态转移矩阵（匀速模型）
            kf.TransitionMatrix = Mat.Eye(4, 4, MatType.CV_32F);
            kf.TransitionMatrix.Set(0, 2, 1.0f); // x += vx
            kf.TransitionMatrix.Set(1, 3, 1.0f); // y += vy

            // 观测矩阵
            kf.MeasurementMatrix = Mat.Zeros(new OpenCvSharp.MatShape(2, 4), MatType.CV_32F);
            kf.MeasurementMatrix.Set(0, 0, 1.0f);
            kf.MeasurementMatrix.Set(1, 1, 1.0f);

            // 过程噪声
            kf.ProcessNoiseCov = Mat.Eye(4, 4, MatType.CV_32F) * 1e-2;

            // 测量噪声
            kf.MeasurementNoiseCov = Mat.Eye(2, 2, MatType.CV_32F) * 1e-1;

            // 后验误差协方差
            kf.ErrorCovPost = Mat.Eye(4, 4, MatType.CV_32F);

            // 初始化状态
            kf.StatePost.Set(0, 0, detection.Center.X);
            kf.StatePost.Set(1, 0, detection.Center.Y);
            kf.StatePost.Set(2, 0, 0f); // 初始速度为 0
            kf.StatePost.Set(3, 0, 0f);

            _kalmanFilters[id] = kf;
            _trackedObjects[id] = new TrackedObject
            {
                TrackId = id,
                ClassName = detection.ClassName,
                Confidence = detection.Confidence,
                BoundingBox = detection.BoundingBox,
                Center = detection.Center,
                Velocity = new Point2f(0, 0),
                FramesSinceUpdate = 0,
                Color = color,
                Trajectory = new List<Point2f> { detection.Center }
            };
        }

        /// <summary>更新已有跟踪目标的状态</summary>
        private void UpdateTrackedObject(int trackId, Detection detection)
        {
            if (!_trackedObjects.TryGetValue(trackId, out var obj)) return;

            // 计算速度
            float vx = detection.Center.X - obj.Center.X;
            float vy = detection.Center.Y - obj.Center.Y;

            obj.BoundingBox = detection.BoundingBox;
            obj.Center = detection.Center;
            obj.Velocity = new Point2f(vx, vy);
            obj.Confidence = detection.Confidence;
            obj.ClassName = detection.ClassName;
            obj.FramesSinceUpdate = 0;

            // 记录轨迹（最多保留 50 个点）
            obj.Trajectory.Add(detection.Center);
            if (obj.Trajectory.Count > 50)
                obj.Trajectory.RemoveAt(0);
        }

        /// <summary>
        /// 简单贪心匹配：按最小距离依次匹配，超过阈值则放弃。
        /// 生产环境可替换为匈牙利算法（LinearSumAssignment）。
        /// </summary>
        private static List<(int trackIdx, int detIdx)> GreedyMatch(
            float[,] costMatrix, int numTracks, int numDets, float threshold)
        {
            var matches = new List<(int, int)>();
            var usedTracks = new bool[numTracks];
            var usedDets = new bool[numDets];

            // 收集所有 (cost, trackIdx, detIdx) 并排序
            var allPairs = new List<(float cost, int t, int d)>();
            for (int t = 0; t < numTracks; t++)
                for (int d = 0; d < numDets; d++)
                    allPairs.Add((costMatrix[t, d], t, d));

            allPairs.Sort((a, b) => a.cost.CompareTo(b.cost));

            foreach (var (cost, t, d) in allPairs)
            {
                if (cost > threshold) break;
                if (usedTracks[t] || usedDets[d]) continue;

                matches.Add((t, d));
                usedTracks[t] = true;
                usedDets[d] = true;
            }

            return matches;
        }
    }
}
