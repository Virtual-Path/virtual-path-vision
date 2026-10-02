using OpenCvSharp;

namespace VirtualPathVision.AI
{
    /// <summary>
    /// 跟踪目标的状态信息。
    /// </summary>
    public class TrackedObject
    {
        /// <summary>轨迹点最大保留数量</summary>
        internal const int MaxTrajectoryPoints = 50;

        /// <summary>默认允许的最大连续未匹配帧数</summary>
        internal const int DefaultMaxLostFrames = 10;

        public int TrackId { get; set; }
        public int ClassId { get; set; } = -1;            // 类别 ID（-1 表示未知）
        public string ClassName { get; set; } = "";
        public float Confidence { get; set; }
        public Rect BoundingBox { get; set; }
        public Point2f Center { get; set; }
        public Point2f Velocity { get; set; }           // 速度向量 (px/frame)
        public Point2f PredictedNextPosition { get; set; } // Kalman 预测的下一帧位置
        public int FramesSinceUpdate { get; set; }       // 距上次匹配的帧数

        /// <summary>
        /// 允许的最大连续未匹配帧数（由 <see cref="KalmanTrackerComponent"/> 的构造参数下发）。
        /// 连续未匹配帧数超过该阈值即视为丢失。
        /// </summary>
        public int MaxLostFrames { get; set; } = DefaultMaxLostFrames;

        public bool IsLost => FramesSinceUpdate > MaxLostFrames; // 超过容忍帧数未匹配视为丢失
        public Scalar Color { get; set; }                // 跟踪可视化颜色

        /// <summary>
        /// 运动轨迹（写时复制）。
        /// 采集线程只通过 <see cref="AppendTrajectoryPoint"/> 追加轨迹，追加时生成新的列表实例，
        /// 因此已发布出去的列表引用不会被就地修改，UI 线程可安全枚举/索引。
        /// </summary>
        public List<Point2f> Trajectory { get; private set; } = new();

        /// <summary>以写时复制方式追加一个轨迹点并裁剪到最大长度</summary>
        internal void AppendTrajectoryPoint(Point2f point)
        {
            var next = new List<Point2f>(Trajectory.Count + 1);
            next.AddRange(Trajectory);
            next.Add(point);
            if (next.Count > MaxTrajectoryPoints)
                next.RemoveRange(0, next.Count - MaxTrajectoryPoints);
            Trajectory = next;
        }

        /// <summary>初始化轨迹起点（仅在创建目标时调用一次）</summary>
        internal void InitializeTrajectory(Point2f start)
        {
            Trajectory = new List<Point2f> { start };
        }

        /// <summary>
        /// 创建当前状态的快照副本。
        /// 返回对象及其轨迹列表此后不再被跟踪线程修改，
        /// 用于把状态安全地交给 UI 线程（数字孪生渲染 / IoT 上报）。
        /// </summary>
        internal TrackedObject Snapshot()
        {
            return new TrackedObject
            {
                TrackId = TrackId,
                ClassId = ClassId,
                ClassName = ClassName,
                Confidence = Confidence,
                BoundingBox = BoundingBox,
                Center = Center,
                Velocity = Velocity,
                PredictedNextPosition = PredictedNextPosition,
                FramesSinceUpdate = FramesSinceUpdate,
                MaxLostFrames = MaxLostFrames,
                Color = Color,
                // COW 轨迹：共享引用但不再被修改，可直接共享
                Trajectory = Trajectory
            };
        }
    }

    /// <summary>
    /// Kalman 多目标跟踪器。
    /// 为每个检测目标维护一个 Kalman 滤波器，实现：
    /// 1. 平滑目标运动轨迹
    /// 2. 预测目标下一帧位置（填补检测丢失）
    /// 3. 目标 ID 关联（按代价贪心匹配，同类别优先）
    /// 4. 轨迹管理（创建/更新/删除）
    /// </summary>
    public class KalmanTrackerComponent : IDisposable
    {
        private readonly Dictionary<int, KalmanFilter> _kalmanFilters = new();
        private readonly Dictionary<int, TrackedObject> _trackedObjects = new();
        private int _nextTrackId = 1;
        private readonly int _maxLostFrames;
        private readonly float _matchThreshold;
        private bool _disposed;

        /// <summary>类别不匹配的惩罚代价（远大于任何距离阈值，排序时恒排在最后并被阈值拒绝）</summary>
        private const float ClassMismatchCost = float.MaxValue;

        private static readonly Scalar[] TrackColors = {
            new(255, 0, 0), new(0, 255, 0), new(0, 0, 255),
            new(255, 255, 0), new(255, 0, 255), new(0, 255, 255),
            new(128, 0, 255), new(255, 128, 0), new(0, 128, 255),
            new(255, 0, 128)
        };

        /// <summary>
        /// 当前所有跟踪目标的快照字典。
        /// 返回防御性副本：枚举时不会因采集线程增删目标而抛异常
        /// （值仍是内部 <see cref="TrackedObject"/> 引用，调用方不得修改其内容）。
        /// </summary>
        public IReadOnlyDictionary<int, TrackedObject> TrackedObjects =>
            new Dictionary<int, TrackedObject>(_trackedObjects);

        /// <summary>活跃（未丢失）跟踪目标数量</summary>
        public int ActiveCount => _trackedObjects.Values.Count(o => !o.IsLost);

        /// <summary>
        /// 初始化 Kalman 跟踪器。
        /// </summary>
        /// <param name="maxLostFrames">目标丢失前的最大未匹配帧数（默认 10）</param>
        /// <param name="matchThreshold">匹配距离阈值（默认 80px）</param>
        public KalmanTrackerComponent(int maxLostFrames = 10, float matchThreshold = 80f)
        {
            _maxLostFrames = Math.Max(1, maxLostFrames);
            _matchThreshold = float.IsFinite(matchThreshold) && matchThreshold > 0f
                ? matchThreshold
                : 80f;
        }

        /// <summary>
        /// 更新跟踪器：输入新检测结果，完成匹配、预测、状态更新。
        /// </summary>
        /// <param name="detections">当前帧的检测结果</param>
        /// <returns>更新后各目标状态的快照列表（含轨迹和预测），可安全交给其他线程</returns>
        public List<TrackedObject> Update(List<Detection> detections)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            detections ??= new List<Detection>();

            // 1. 对所有已有 Kalman 滤波器执行预测
            var predictions = new Dictionary<int, Point2f>();
            foreach (var kvp in _kalmanFilters)
            {
                var predicted = kvp.Value.Predict();
                predictions[kvp.Key] = new Point2f(predicted.At<float>(0), predicted.At<float>(1));
            }

            // 2. 构建代价矩阵（按距离 + 类别一致性做贪心匹配）
            var trackIds = _kalmanFilters.Keys.ToList();
            var unmatchedTracks = new HashSet<int>(trackIds);
            var unmatchedDetections = new HashSet<int>(Enumerable.Range(0, detections.Count));

            if (trackIds.Count > 0 && detections.Count > 0)
            {
                var costMatrix = new float[trackIds.Count, detections.Count];
                for (int t = 0; t < trackIds.Count; t++)
                {
                    int trackId = trackIds[t];
                    var pred = predictions[trackId];
                    _trackedObjects.TryGetValue(trackId, out var trackObj);

                    for (int d = 0; d < detections.Count; d++)
                    {
                        // 类别不一致直接拒绝关联，避免轨迹被绑定到完全不同类别的检测上
                        if (trackObj != null && !IsSameClass(trackObj, detections[d]))
                        {
                            costMatrix[t, d] = ClassMismatchCost;
                            continue;
                        }

                        float dx = pred.X - detections[d].Center.X;
                        float dy = pred.Y - detections[d].Center.Y;
                        costMatrix[t, d] = MathF.Sqrt(dx * dx + dy * dy);
                    }
                }

                var matches = GreedyMatch(costMatrix, trackIds.Count, detections.Count, _matchThreshold);

                foreach (var (trackIdx, detIdx) in matches)
                {
                    int trackId = trackIds[trackIdx];
                    unmatchedTracks.Remove(trackId);
                    unmatchedDetections.Remove(detIdx);

                    // 更新 Kalman 滤波器（measurement 必须释放，否则每帧每个目标泄漏一个原生 Mat）
                    using var measurement = new Mat(2, 1, MatType.CV_32F);
                    measurement.Set(0, 0, detections[detIdx].Center.X);
                    measurement.Set(1, 0, detections[detIdx].Center.Y);
                    _kalmanFilters[trackId].Correct(measurement);

                    // 更新跟踪状态（速度取自 Kalman 后验状态）
                    UpdateTrackedObject(trackId, detections[detIdx]);
                }
            }

            // 3. 为未匹配的已有目标更新丢失帧数
            foreach (int trackId in unmatchedTracks)
            {
                if (_trackedObjects.TryGetValue(trackId, out var lostTrack))
                {
                    lostTrack.FramesSinceUpdate++;
                }
            }

            // 4. 为未匹配的检测创建新跟踪
            for (int d = 0; d < detections.Count; d++)
            {
                if (unmatchedDetections.Contains(d))
                    CreateNewTrack(detections[d]);
            }

            // 5. 每帧为所有目标（含已匹配目标）刷新下一帧预测位置。
            //    预测来自 Predict() 之后、Correct() 之前的状态，匹配与否都必须写入，
            //    否则持续被检测到的目标会一直停留在 default(0,0)，主动感知无法生成 ROI。
            foreach (var kvp in _trackedObjects)
            {
                if (predictions.TryGetValue(kvp.Key, out var predicted) && IsFinitePoint(predicted))
                    kvp.Value.PredictedNextPosition = predicted;
            }

            // 6. 清理丢失目标（依据构造参数 maxLostFrames，而非硬编码常量）
            var lostIds = _trackedObjects
                .Where(kvp => kvp.Value.IsLost)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (int id in lostIds)
            {
                _trackedObjects.Remove(id);
                if (_kalmanFilters.Remove(id, out var kf))
                    kf.Dispose();
            }

            // 7. 输出快照，避免把内部可变对象交给 UI 线程
            return _trackedObjects.Values.Select(o => o.Snapshot()).ToList();
        }

        /// <summary>
        /// 在帧上绘制跟踪结果（ID、轨迹、预测位置）。
        /// </summary>
        public void DrawTracks(Mat frame)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            foreach (var obj in _trackedObjects.Values)
            {
                // COW 轨迹：一次性取出当前列表引用，枚举期间不会被修改
                var trajectory = obj.Trajectory;
                if (trajectory.Count > 1)
                {
                    for (int i = 1; i < trajectory.Count; i++)
                    {
                        Cv2.Line(frame,
                            new OpenCvSharp.Point((int)trajectory[i - 1].X, (int)trajectory[i - 1].Y),
                            new OpenCvSharp.Point((int)trajectory[i].X, (int)trajectory[i].Y),
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
                if (obj.PredictedNextPosition != default && IsFinitePoint(obj.PredictedNextPosition))
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

        /// <summary>重置所有跟踪状态并释放全部原生 Kalman 资源</summary>
        public void Reset()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ReleaseAllFilters();
            _trackedObjects.Clear();
            _nextTrackId = 1;
        }

        /// <summary>释放所有 KalmanFilter 的原生资源</summary>
        private void ReleaseAllFilters()
        {
            foreach (var kf in _kalmanFilters.Values)
            {
                try
                {
                    kf.Dispose();
                }
                catch
                {
                    // 单个滤波器释放失败不应影响其余资源的回收
                }
            }
            _kalmanFilters.Clear();
        }

        /// <summary>创建新的跟踪目标并初始化 Kalman 滤波器</summary>
        private void CreateNewTrack(Detection detection)
        {
            int id = _nextTrackId++;
            // TrackId 从 1 开始，索引相应减 1，避免第一个目标与第 11 个目标撞色
            Scalar color = TrackColors[((id - 1) % TrackColors.Length + TrackColors.Length) % TrackColors.Length];

            // Kalman 滤波器：状态 [x, y, vx, vy]，观测 [x, y]
            var kf = new KalmanFilter(4, 2, 0);

            try
            {
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
            }
            catch
            {
                // 矩阵装配失败时必须释放刚创建的滤波器，否则原生内存泄漏
                kf.Dispose();
                throw;
            }

            _kalmanFilters[id] = kf;
            var trackedObject = new TrackedObject
            {
                TrackId = id,
                ClassId = detection.ClassId,
                ClassName = detection.ClassName,
                Confidence = detection.Confidence,
                BoundingBox = detection.BoundingBox,
                Center = detection.Center,
                Velocity = new Point2f(0, 0),
                // 新建轨迹速度为 0，下一帧位置即当前位置
                PredictedNextPosition = detection.Center,
                FramesSinceUpdate = 0,
                MaxLostFrames = _maxLostFrames,
                Color = color
            };
            trackedObject.InitializeTrajectory(detection.Center);
            _trackedObjects[id] = trackedObject;
        }

        /// <summary>更新已有跟踪目标的状态</summary>
        private void UpdateTrackedObject(int trackId, Detection detection)
        {
            if (!_trackedObjects.TryGetValue(trackId, out var obj)) return;

            // 速度取自 Kalman 后验状态的 vx/vy 分量（已按帧归一，无需再除以帧数）
            Point2f velocity = new Point2f(0, 0);
            if (_kalmanFilters.TryGetValue(trackId, out var kf))
            {
                var statePost = kf.StatePost;
                velocity = new Point2f(statePost.At<float>(2), statePost.At<float>(3));
            }

            obj.BoundingBox = detection.BoundingBox;
            obj.Center = detection.Center;
            obj.Velocity = velocity;
            obj.Confidence = detection.Confidence;
            obj.ClassName = detection.ClassName;
            obj.ClassId = detection.ClassId;
            obj.FramesSinceUpdate = 0;

            // 记录轨迹（写时复制，最多保留 MaxTrajectoryPoints 个点）
            obj.AppendTrajectoryPoint(detection.Center);
        }

        /// <summary>判断检测与轨迹是否属于同一类别</summary>
        private static bool IsSameClass(TrackedObject track, Detection detection)
        {
            if (track.ClassId >= 0 && detection.ClassId >= 0)
                return track.ClassId == detection.ClassId;

            return string.Equals(track.ClassName, detection.ClassName,
                StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>点是否为有限值（非 NaN / Inf）</summary>
        private static bool IsFinitePoint(Point2f p) =>
            float.IsFinite(p.X) && float.IsFinite(p.Y);

        /// <summary>
        /// 简单贪心匹配：按代价升序依次匹配，超过阈值则放弃。
        /// 排序带确定性次级关键字（trackIdx、detIdx），代价相同的情况下关联结果可复现，
        /// 生产环境可替换为匈牙利算法（LinearSumAssignment）。
        /// </summary>
        private static List<(int trackIdx, int detIdx)> GreedyMatch(
            float[,] costMatrix, int numTracks, int numDets, float threshold)
        {
            var matches = new List<(int, int)>();
            var usedTracks = new bool[numTracks];
            var usedDets = new bool[numDets];

            // 收集所有 (cost, trackIdx, detIdx) 并按 (cost, trackIdx, detIdx) 排序
            var allPairs = new List<(float cost, int t, int d)>(numTracks * numDets);
            for (int t = 0; t < numTracks; t++)
                for (int d = 0; d < numDets; d++)
                    allPairs.Add((costMatrix[t, d], t, d));

            allPairs.Sort((a, b) =>
            {
                int c = a.cost.CompareTo(b.cost);
                if (c != 0) return c;
                c = a.t.CompareTo(b.t);   // 次级关键字：轨迹序号
                if (c != 0) return c;
                return a.d.CompareTo(b.d); // 三级关键字：检测序号
            });

            foreach (var (cost, t, d) in allPairs)
            {
                // 不可分配的代价（类别不匹配）统一视为超阈值，直接终止
                if (cost > threshold) break;
                if (usedTracks[t] || usedDets[d]) continue;

                matches.Add((t, d));
                usedTracks[t] = true;
                usedDets[d] = true;
            }

            return matches;
        }

        /// <summary>释放全部原生资源</summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>释放资源（托管/非托管两种路径）</summary>
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;
            ReleaseAllFilters();
            _trackedObjects.Clear();
        }

        /// <summary>
        /// 终结器兜底：调用方（当前由 MainWindow 负责）若未显式 Dispose，
        /// 仍可在 GC 时回收 KalmanFilter 持有的原生 Mats。
        /// </summary>
        ~KalmanTrackerComponent()
        {
            try
            {
                Dispose(false);
            }
            catch
            {
                // 终结器中不得抛出异常
            }
        }
    }
}