using OpenCvSharp;

namespace VirtualPathVision.AI
{
    /// <summary>
    /// 产线布局上的设备/工位信息。
    /// </summary>
    public class StationInfo
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public Point2f Position { get; set; }     // 在产线布局图上的位置
        public Size Size { get; set; } = new(60, 40);
        public Scalar Color { get; set; } = new(80, 80, 80);
        public bool HasDefect { get; set; }
        public int DetectionCount { get; set; }
    }

    /// <summary>
    /// 数字孪生渲染器，将检测结果映射到 2D 产线空间视图。
    /// 实现实时空间数字孪生的雏形：
    /// 1. 产线布局可视化（工位、传送带、检测点）
    /// 2. 实时检测结果叠加（目标位置、轨迹、告警）
    /// 3. 统计信息面板（检测数量、合格率、告警数）
    /// </summary>
    public class DigitalTwinRenderer : IDisposable
    {
        /// <summary>源视频坐标系基准分辨率（相机帧宽高比）</summary>
        private const float SourceWidth = 1920f;
        private const float SourceHeight = 1080f;

        /// <summary>画布最小尺寸（统计面板需要约 190px 宽度）</summary>
        private const int MinCanvasWidth = 240;
        private const int MinCanvasHeight = 140;

        /// <summary>统计面板尺寸</summary>
        private const int StatsPanelWidth = 190;
        private const int StatsPanelHeight = 100;

        /// <summary>工位矩形相对传送带中线的垂直偏移</summary>
        private const int StationTopOffset = 60;

        private Mat? _layoutCanvas;
        private readonly int _canvasWidth;
        private readonly int _canvasHeight;
        private readonly List<StationInfo> _stations = new();
        private bool _disposed;

        // 统计数据：多线程读改写，改用 Interlocked 保证线程安全
        private int _totalDetections;
        private int _totalDefects;
        private int _totalFrames;

        private static readonly List<Detection> NoDetections = new();
        private static readonly List<TrackedObject> NoTracks = new();

        // 颜色常量
        private static readonly Scalar BgColor = new(30, 30, 30);
        private static readonly Scalar ConveyorColor = new(60, 60, 60);
        private static readonly Scalar StationNormalColor = new(80, 120, 80);
        private static readonly Scalar StationDefectColor = new(0, 0, 200);
        private static readonly Scalar TextColor = new(200, 200, 200);
        private static readonly Scalar AccentColor = new(0, 200, 200);

        /// <summary>
        /// 缺陷类别关键字（显式标记为缺陷的类别，命中即计为缺陷）。
        /// 调用方可追加自定义关键字，例如产线专有缺陷类别。
        /// </summary>
        public List<string> DefectClassKeywords { get; } = new()
        {
            "defect", "scratch", "dent", "crack", "hole", "damage", "broken",
            "flaw", "blemish", "ng", "anomaly", "leak", "rust", "missing"
        };

        /// <summary>
        /// 可疑置信度带下限：低于该置信度的检测通常来自噪声/遮挡，不计入缺陷统计。
        /// </summary>
        public float SuspiciousConfidenceMin { get; set; } = 0.35f;

        /// <summary>
        /// 可疑置信度带上限：达到该置信度的普通目标视为可信检测，不计入缺陷统计。
        /// 默认 0.75，配合 YOLO 默认 0.5 置信度阈值，可避免"高置信度=缺陷"的错误统计。
        /// </summary>
        public float SuspiciousConfidenceCeiling { get; set; } = 0.75f;

        /// <summary>画布宽度</summary>
        public int CanvasWidth => _canvasWidth;

        /// <summary>画布高度</summary>
        public int CanvasHeight => _canvasHeight;

        /// <summary>当前检测到的缺陷总数（线程安全读取）</summary>
        public int TotalDefects => Volatile.Read(ref _totalDefects);

        /// <summary>累计检测总数（线程安全读取）</summary>
        public int TotalDetections => Volatile.Read(ref _totalDetections);

        /// <summary>累计渲染帧数（线程安全读取）</summary>
        public int TotalFrames => Volatile.Read(ref _totalFrames);

        /// <summary>
        /// 初始化数字孪生渲染器。
        /// </summary>
        /// <param name="width">画布宽度（小于 <see cref="MinCanvasWidth"/> 时按最小值裁剪）</param>
        /// <param name="height">画布高度（小于 <see cref="MinCanvasHeight"/> 时按最小值裁剪）</param>
        public DigitalTwinRenderer(int width = 800, int height = 400)
        {
            if (width <= 0)
                throw new ArgumentOutOfRangeException(nameof(width), width, "画布宽度必须为正数");
            if (height <= 0)
                throw new ArgumentOutOfRangeException(nameof(height), height, "画布高度必须为正数");

            // 画布尺寸兜底：统计面板宽度为 190px，过窄的画布会导致面板 x 坐标为负
            _canvasWidth = Math.Max(width, MinCanvasWidth);
            _canvasHeight = Math.Max(height, MinCanvasHeight);
            _layoutCanvas = new Mat(_canvasHeight, _canvasWidth, MatType.CV_8UC3, BgColor);

            InitializeDefaultLayout();
        }

        /// <summary>
        /// 初始化默认产线布局（5 个工位 + 传送带）。
        /// </summary>
        private void InitializeDefaultLayout()
        {
            int stationSpacing = _canvasWidth / 6;
            int stationWidth = Math.Max(20, Math.Min(60, _canvasWidth / 8));
            int stationHeight = Math.Max(16, Math.Min(40, _canvasHeight / 8));
            for (int i = 0; i < 5; i++)
            {
                _stations.Add(new StationInfo
                {
                    Id = i + 1,
                    Name = $"S{i + 1}",
                    Position = new Point2f(stationSpacing * (i + 1), _canvasHeight / 2),
                    Size = new Size(stationWidth, stationHeight),
                    Color = StationNormalColor
                });
            }
        }

        /// <summary>
        /// 渲染完整的数字孪生视图。
        /// </summary>
        /// <param name="tracks">当前跟踪目标（元素为快照，读取安全）</param>
        /// <param name="detections">当前检测结果</param>
        /// <returns>渲染后的图像，所有权移交调用方（由调用方 Dispose）</returns>
        public Mat Render(List<TrackedObject> tracks, List<Detection> detections)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Mat? layout = _layoutCanvas;
            if (layout is null || layout.Empty())
                throw new ObjectDisposedException(nameof(DigitalTwinRenderer));

            tracks ??= NoTracks;
            detections ??= NoDetections;

            // 入口处对两个入参列表做防御性快照：调用方可能在另一个线程上改写它们，
            // 快照保证本帧渲染基于一致的输入（并发修改时本帧降级为部分数据而非崩溃）
            List<TrackedObject> trackSnapshot = SnapshotList(tracks);
            List<Detection> detectionSnapshot = SnapshotList(detections);

            // 克隆画布；所有权在成功返回前由本方法持有
            Mat canvas = layout.Clone();
            bool transferred = false;
            try
            {
                Interlocked.Increment(ref _totalFrames);
                Interlocked.Add(ref _totalDetections, detectionSnapshot.Count);

                // 相机帧 → 画布的等比映射（保持宽高比，X/Y 使用同一缩放与同一居中偏移）
                var transform = new MapTransform(_canvasWidth, _canvasHeight);

                // 1. 绘制传送带
                DrawConveyor(canvas);

                // 2. 绘制工位
                DrawStations(canvas, detectionSnapshot, transform);

                // 3. 绘制检测目标及其轨迹
                DrawTrackTargets(canvas, trackSnapshot, transform);

                // 4. 绘制统计面板
                DrawStatsPanel(canvas, detectionSnapshot.Count, trackSnapshot.Count);

                // 5. 绘制标题
                DrawTitle(canvas);

                transferred = true; // 所有权移交调用方
                return canvas;
            }
            finally
            {
                // 渲染过程抛出异常时克隆出的 Mat 无法交给调用方，必须就地释放，避免原生内存泄漏
                if (!transferred)
                    canvas.Dispose();
            }
        }

        /// <summary>
        /// 防御性复制列表。
        /// 列表在并发增删时，List&lt;T&gt;(IEnumerable) 会抛出 ArgumentException
        /// （枚举过程中集合被修改），此时降级为空列表，保证渲染不中断。
        /// </summary>
        private static List<T> SnapshotList<T>(List<T>? source)
        {
            if (source == null)
                return new List<T>();
            try
            {
                return new List<T>(source);
            }
            catch (ArgumentException)
            {
                return new List<T>();
            }
            catch (InvalidOperationException)
            {
                return new List<T>();
            }
        }

        /// <summary>
        /// 缺陷判定。规则本身统一由 <see cref="DefectRules"/> 定义，
        /// 本方法只把可配置的阈值/关键词转发过去，避免两处规则漂移。
        ///
        /// 语义：
        /// 1) 类别显式标记：<see cref="Detection.ClassName"/> 命中 <see cref="DefectClassKeywords"/> → 缺陷；
        /// 2) 可疑置信度带：Confidence 落在 [SuspiciousConfidenceMin, SuspiciousConfidenceCeiling)
        ///    → 视为模型对目标不确定的疑似缺陷；
        /// 3) 其余检测（含高置信度的普通目标）→ 不计为缺陷。
        /// 说明：高置信度只说明模型"确信"，不代表"缺陷"，因此不能像旧实现那样把
        /// Confidence &gt; 0.8 的普通目标算作缺陷，否则 COCO 类模型的合格率恒为 0。
        /// </summary>
        public bool IsDefect(Detection detection)
            => DefectRules.IsDefect(detection, DefectClassKeywords,
                SuspiciousConfidenceMin, SuspiciousConfidenceCeiling);

        /// <summary>绘制传送带</summary>
        private void DrawConveyor(Mat canvas)
        {
            int beltY = _canvasHeight / 2;
            int beltHeight = Math.Max(4, Math.Min(20, _canvasHeight / 8));

            int left = Math.Min(30, Math.Max(0, _canvasWidth / 10));
            int right = Math.Max(left, _canvasWidth - left);
            int beltWidth = right - left;

            // 传送带主体
            Cv2.Rectangle(canvas,
                new Rect(left, beltY - beltHeight / 2, beltWidth, beltHeight),
                ConveyorColor, Cv2.FILLED);

            // 传送带边线
            Cv2.Line(canvas,
                new OpenCvSharp.Point(left, beltY - beltHeight / 2),
                new OpenCvSharp.Point(right, beltY - beltHeight / 2),
                new Scalar(100, 100, 100), 2);
            Cv2.Line(canvas,
                new OpenCvSharp.Point(left, beltY + beltHeight / 2),
                new OpenCvSharp.Point(right, beltY + beltHeight / 2),
                new Scalar(100, 100, 100), 2);

            // 方向箭头
            if (right - left >= 40)
            {
                Cv2.ArrowedLine(canvas,
                    new OpenCvSharp.Point(right - 20, beltY),
                    new OpenCvSharp.Point(right - 5, beltY),
                    new Scalar(150, 150, 150), 2, LineTypes.AntiAlias);
            }
        }

        /// <summary>绘制工位</summary>
        private void DrawStations(Mat canvas, List<Detection> detections, MapTransform transform)
        {
            int defectCount = 0;
            var perStationCount = new int[_stations.Count];
            var perStationDefect = new bool[_stations.Count];

            // 按"最近工位"把每个检测归属到单个工位，得到真正有意义的单工位计数/缺陷标记
            for (int i = 0; i < detections.Count; i++)
            {
                var det = detections[i];
                bool isDefect = IsDefect(det);
                if (isDefect) defectCount++;

                int stationIndex = FindNearestStation(transform.MapX(det.Center.X));
                if (stationIndex < 0) continue;

                perStationCount[stationIndex]++;
                if (isDefect) perStationDefect[stationIndex] = true;
            }

            Interlocked.Add(ref _totalDefects, defectCount);

            for (int i = 0; i < _stations.Count; i++)
            {
                var station = _stations[i];

                // 工位状态按本帧归属到该工位的检测重新计算（不再残留上一帧的红色告警）
                station.DetectionCount = perStationCount[i];
                station.HasDefect = perStationDefect[i];
                station.Color = station.HasDefect ? StationDefectColor : StationNormalColor;

                // 工位矩形（保持在传送带上方，小画布时兜底到 0，避免绘制到画布外）
                int stationTop = (int)station.Position.Y - station.Size.Height / 2 - StationTopOffset;
                var rect = new Rect(
                    (int)station.Position.X - station.Size.Width / 2,
                    Math.Max(0, stationTop),
                    station.Size.Width, station.Size.Height);
                Cv2.Rectangle(canvas, rect, station.Color, Cv2.FILLED);
                Cv2.Rectangle(canvas, rect, new Scalar(200, 200, 200), 1);

                // 工位名称
                Cv2.PutText(canvas, station.Name,
                    new OpenCvSharp.Point(rect.X + 4, rect.Y + Math.Min(26, station.Size.Height - 4)),
                    HersheyFonts.HersheySimplex, 0.5, new Scalar(255, 255, 255), 1, LineTypes.AntiAlias);

                // 工位计数
                Cv2.PutText(canvas, $"[{station.DetectionCount}]",
                    new OpenCvSharp.Point(rect.X + 4, rect.Y + Math.Min(46, station.Size.Height * 3 / 2)),
                    HersheyFonts.HersheySimplex, 0.35, new Scalar(220, 220, 220), 1, LineTypes.AntiAlias);

                // 连接到传送带的竖线
                Cv2.Line(canvas,
                    new OpenCvSharp.Point((int)station.Position.X, rect.Y + station.Size.Height),
                    new OpenCvSharp.Point((int)station.Position.X, _canvasHeight / 2 - 10),
                    new Scalar(100, 100, 100), 1, LineTypes.AntiAlias);
            }
        }

        /// <summary>查找离给定画布 X 坐标最近的工位索引</summary>
        private int FindNearestStation(int canvasX)
        {
            int best = -1;
            int bestDist = int.MaxValue;
            for (int i = 0; i < _stations.Count; i++)
            {
                int dist = Math.Abs((int)_stations[i].Position.X - canvasX);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = i;
                }
            }
            return best;
        }

        /// <summary>绘制跟踪目标</summary>
        private void DrawTrackTargets(Mat canvas, List<TrackedObject> tracks, MapTransform transform)
        {
            // 先对目标列表本身做防御性复制：列表长度可能在渲染期间变化，
            // 直接按索引遍历会抛 ArgumentOutOfRangeException
            var trackList = SnapshotList(tracks);

            for (int t = 0; t < trackList.Count; t++)
            {
                var track = trackList[t];
                if (track == null) continue;

                var center = transform.Map(track.Center);

                // 目标点
                Cv2.Circle(canvas, center, 6, track.Color, -1);
                Cv2.Circle(canvas, center, 8, track.Color, 1);

                // 轨迹连线：先复制出本帧使用的轨迹点，再基于副本绘制。
                // 采集线程可能在渲染期间增删轨迹点，复制过程用 try/catch 兜住并发修改，
                // 保证 UI 线程不会因索引越界抛出 ArgumentOutOfRangeException。
                var trajectory = SnapshotList(track.Trajectory);

                int trajCount = trajectory.Count;
                for (int i = 1; i < trajCount; i++)
                {
                    var prev = trajectory[i - 1];
                    var curr = trajectory[i];
                    if (!float.IsFinite(prev.X) || !float.IsFinite(prev.Y) ||
                        !float.IsFinite(curr.X) || !float.IsFinite(curr.Y))
                        continue;

                    Cv2.Line(canvas, transform.Map(prev), transform.Map(curr),
                        track.Color, 1, LineTypes.AntiAlias);
                }

                // ID 标签
                Cv2.PutText(canvas, $"T{track.TrackId}",
                    new OpenCvSharp.Point(center.X + 10, center.Y - 5),
                    HersheyFonts.HersheySimplex, 0.35, track.Color, 1, LineTypes.AntiAlias);
            }
        }

        /// <summary>绘制统计面板</summary>
        private void DrawStatsPanel(Mat canvas, int detectionCount, int trackCount)
        {
            int panelW = Math.Min(StatsPanelWidth, _canvasWidth);
            int panelH = Math.Min(StatsPanelHeight, _canvasHeight);
            int panelX = Math.Max(0, _canvasWidth - panelW);
            int panelY = Math.Max(0, _canvasHeight - panelH - 10);

            // 面板背景
            Cv2.Rectangle(canvas, new Rect(panelX, panelY, panelW, panelH),
                new Scalar(40, 40, 40), Cv2.FILLED);
            Cv2.Rectangle(canvas, new Rect(panelX, panelY, panelW, panelH),
                AccentColor, 1);

            // 统计文本
            int totalDetections = Volatile.Read(ref _totalDetections);
            int totalDefects = Volatile.Read(ref _totalDefects);
            double passRate = totalDetections > 0
                ? (double)(totalDetections - totalDefects) / totalDetections * 100
                : 100;

            var lines = new[]
            {
                $"Detections: {detectionCount}",
                $"Tracks:     {trackCount}",
                $"Total:      {totalDetections}",
                $"Defects:    {totalDefects}",
                $"Pass Rate:  {passRate:F1}%"
            };

            int y = panelY + 18;
            foreach (var line in lines)
            {
                if (y > panelY + panelH - 2) break;
                Cv2.PutText(canvas, line,
                    new OpenCvSharp.Point(panelX + 8, y),
                    HersheyFonts.HersheySimplex, 0.35, TextColor, 1, LineTypes.AntiAlias);
                y += 17;
            }
        }

        /// <summary>绘制标题</summary>
        private void DrawTitle(Mat canvas)
        {
            Cv2.PutText(canvas, "Digital Twin - Production Line Monitor",
                new OpenCvSharp.Point(15, 25),
                HersheyFonts.HersheySimplex, 0.55, AccentColor, 1, LineTypes.AntiAlias);

            Cv2.PutText(canvas, $"Frame: {Volatile.Read(ref _totalFrames)}  |  OpenCV 5 + Active Perception",
                new OpenCvSharp.Point(15, 45),
                HersheyFonts.HersheySimplex, 0.38, new Scalar(120, 120, 120), 1, LineTypes.AntiAlias);
        }

        /// <summary>重置统计数据，并清除工位上的残留告警状态</summary>
        public void ResetStats()
        {
            Interlocked.Exchange(ref _totalDetections, 0);
            Interlocked.Exchange(ref _totalDefects, 0);
            Interlocked.Exchange(ref _totalFrames, 0);

            foreach (var station in _stations)
            {
                station.DetectionCount = 0;
                station.HasDefect = false;
                station.Color = StationNormalColor;
            }
        }

        /// <summary>释放资源</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _layoutCanvas?.Dispose();
            _layoutCanvas = null; // 防止后续 Render 克隆已释放的 Mat
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// 相机帧坐标 → 画布坐标的映射。
        /// 使用等比缩放（保持 16:9 源画面的宽高比），并在 X/Y 两个轴使用同一缩放系数与同一居中偏移，
        /// 避免非等比拉伸以及"只对 Y 轴加偏移"造成的目标错位。
        /// </summary>
        private readonly struct MapTransform
        {
            public readonly float Scale;
            public readonly int OffsetX;
            public readonly int OffsetY;

            public MapTransform(int canvasWidth, int canvasHeight)
            {
                Scale = MathF.Min((float)canvasWidth / SourceWidth, (float)canvasHeight / SourceHeight);
                if (!float.IsFinite(Scale) || Scale <= 0f) Scale = 1f;

                int mappedWidth = (int)(SourceWidth * Scale);
                int mappedHeight = (int)(SourceHeight * Scale);
                OffsetX = (canvasWidth - mappedWidth) / 2;
                OffsetY = (canvasHeight - mappedHeight) / 2;
            }

            public int MapX(float sourceX) =>
                float.IsFinite(sourceX) ? (int)(sourceX * Scale) + OffsetX : OffsetX;

            public int MapY(float sourceY) =>
                float.IsFinite(sourceY) ? (int)(sourceY * Scale) + OffsetY : OffsetY;

            public OpenCvSharp.Point Map(Point2f source) =>
                new OpenCvSharp.Point(MapX(source.X), MapY(source.Y));
        }
    }
}