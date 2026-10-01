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
    public class DigitalTwinRenderer
    {
        private readonly Mat _layoutCanvas;
        private readonly int _canvasWidth;
        private readonly int _canvasHeight;
        private readonly List<StationInfo> _stations = new();

        // 统计数据
        private int _totalDetections;
        private int _totalDefects;
        private int _totalFrames;

        // 颜色常量
        private static readonly Scalar BgColor = new(30, 30, 30);
        private static readonly Scalar ConveyorColor = new(60, 60, 60);
        private static readonly Scalar StationNormalColor = new(80, 120, 80);
        private static readonly Scalar StationDefectColor = new(0, 0, 200);
        private static readonly Scalar TextColor = new(200, 200, 200);
        private static readonly Scalar AccentColor = new(0, 200, 200);

        /// <summary>画布宽度</summary>
        public int CanvasWidth => _canvasWidth;

        /// <summary>画布高度</summary>
        public int CanvasHeight => _canvasHeight;

        /// <summary>当前检测到的缺陷总数</summary>
        public int TotalDefects => _totalDefects;

        /// <summary>
        /// 初始化数字孪生渲染器。
        /// </summary>
        /// <param name="width">画布宽度</param>
        /// <param name="height">画布高度</param>
        public DigitalTwinRenderer(int width = 800, int height = 400)
        {
            _canvasWidth = width;
            _canvasHeight = height;
            _layoutCanvas = new Mat(height, width, MatType.CV_8UC3, BgColor);

            InitializeDefaultLayout();
        }

        /// <summary>
        /// 初始化默认产线布局（5 个工位 + 传送带）。
        /// </summary>
        private void InitializeDefaultLayout()
        {
            int stationSpacing = _canvasWidth / 6;
            for (int i = 0; i < 5; i++)
            {
                _stations.Add(new StationInfo
                {
                    Id = i + 1,
                    Name = $"S{i + 1}",
                    Position = new Point2f(stationSpacing * (i + 1), _canvasHeight / 2),
                    Size = new Size(60, 40),
                    Color = StationNormalColor
                });
            }
        }

        /// <summary>
        /// 渲染完整的数字孪生视图。
        /// </summary>
        /// <param name="tracks">当前跟踪目标</param>
        /// <param name="detections">当前检测结果</param>
        /// <returns>渲染后的图像</returns>
        public Mat Render(List<TrackedObject> tracks, List<Detection> detections)
        {
            Mat canvas = _layoutCanvas.Clone();

            _totalFrames++;
            _totalDetections += detections.Count;

            // 1. 绘制传送带
            DrawConveyor(canvas);

            // 2. 绘制工位
            DrawStations(canvas, detections);

            // 3. 绘制检测目标及其轨迹
            DrawTrackTargets(canvas, tracks);

            // 4. 绘制统计面板
            DrawStatsPanel(canvas, detections.Count, tracks.Count);

            // 5. 绘制标题
            DrawTitle(canvas);

            return canvas;
        }

        /// <summary>绘制传送带</summary>
        private void DrawConveyor(Mat canvas)
        {
            int beltY = _canvasHeight / 2;
            int beltHeight = 20;

            // 传送带主体
            Cv2.Rectangle(canvas,
                new Rect(30, beltY - beltHeight / 2, _canvasWidth - 60, beltHeight),
                ConveyorColor, Cv2.FILLED);

            // 传送带边线
            Cv2.Line(canvas,
                new OpenCvSharp.Point(30, beltY - beltHeight / 2),
                new OpenCvSharp.Point(_canvasWidth - 30, beltY - beltHeight / 2),
                new Scalar(100, 100, 100), 2);
            Cv2.Line(canvas,
                new OpenCvSharp.Point(30, beltY + beltHeight / 2),
                new OpenCvSharp.Point(_canvasWidth - 30, beltY + beltHeight / 2),
                new Scalar(100, 100, 100), 2);

            // 方向箭头
            Cv2.ArrowedLine(canvas,
                new OpenCvSharp.Point(_canvasWidth - 50, beltY),
                new OpenCvSharp.Point(_canvasWidth - 35, beltY),
                new Scalar(150, 150, 150), 2, LineTypes.AntiAlias);
        }

        /// <summary>绘制工位</summary>
        private void DrawStations(Mat canvas, List<Detection> detections)
        {
            int defectCount = detections.Count(d =>
                d.ClassName.Contains("defect") || d.Confidence > 0.8f);
            _totalDefects += defectCount;

            foreach (var station in _stations)
            {
                // 根据检测状态更新工位颜色
                station.DetectionCount = detections.Count;
                station.HasDefect = defectCount > 0;
                station.Color = station.HasDefect ? StationDefectColor : StationNormalColor;

                // 工位矩形
                var rect = new Rect(
                    (int)station.Position.X - station.Size.Width / 2,
                    (int)station.Position.Y - station.Size.Height / 2 - 60,
                    station.Size.Width, station.Size.Height);
                Cv2.Rectangle(canvas, rect, station.Color, Cv2.FILLED);
                Cv2.Rectangle(canvas, rect, new Scalar(200, 200, 200), 1);

                // 工位名称
                Cv2.PutText(canvas, station.Name,
                    new OpenCvSharp.Point(rect.X + 18, rect.Y + 26),
                    HersheyFonts.HersheySimplex, 0.5, new Scalar(255, 255, 255), 1, LineTypes.AntiAlias);

                // 连接到传送带的竖线
                Cv2.Line(canvas,
                    new OpenCvSharp.Point((int)station.Position.X, rect.Y + station.Size.Height),
                    new OpenCvSharp.Point((int)station.Position.X, _canvasHeight / 2 - 10),
                    new Scalar(100, 100, 100), 1, LineTypes.AntiAlias);
            }
        }

        /// <summary>绘制跟踪目标</summary>
        private void DrawTrackTargets(Mat canvas, List<TrackedObject> tracks)
        {
            foreach (var track in tracks)
            {
                // 映射到产线坐标（简化：按比例映射）
                float scaleX = (float)_canvasWidth / 1920; // 假设原始帧 1920 宽
                float scaleY = (float)_canvasHeight / 1080;
                int cx = (int)(track.Center.X * scaleX);
                int cy = (int)(track.Center.Y * scaleY) + _canvasHeight / 4;

                // 目标点
                Cv2.Circle(canvas, new OpenCvSharp.Point(cx, cy), 6, track.Color, -1);
                Cv2.Circle(canvas, new OpenCvSharp.Point(cx, cy), 8, track.Color, 1);

                // 轨迹连线
                if (track.Trajectory.Count > 1)
                {
                    for (int i = 1; i < track.Trajectory.Count; i++)
                    {
                        int px = (int)(track.Trajectory[i - 1].X * scaleX);
                        int py = (int)(track.Trajectory[i - 1].Y * scaleY) + _canvasHeight / 4;
                        int nx = (int)(track.Trajectory[i].X * scaleX);
                        int ny = (int)(track.Trajectory[i].Y * scaleY) + _canvasHeight / 4;
                        Cv2.Line(canvas,
                            new OpenCvSharp.Point(px, py),
                            new OpenCvSharp.Point(nx, ny),
                            track.Color, 1, LineTypes.AntiAlias);
                    }
                }

                // ID 标签
                Cv2.PutText(canvas, $"T{track.TrackId}",
                    new OpenCvSharp.Point(cx + 10, cy - 5),
                    HersheyFonts.HersheySimplex, 0.35, track.Color, 1, LineTypes.AntiAlias);
            }
        }

        /// <summary>绘制统计面板</summary>
        private void DrawStatsPanel(Mat canvas, int detectionCount, int trackCount)
        {
            int panelX = _canvasWidth - 200;
            int panelY = 10;
            int panelW = 190;
            int panelH = 100;

            // 面板背景
            Cv2.Rectangle(canvas, new Rect(panelX, panelY, panelW, panelH),
                new Scalar(40, 40, 40), Cv2.FILLED);
            Cv2.Rectangle(canvas, new Rect(panelX, panelY, panelW, panelH),
                AccentColor, 1);

            // 统计文本
            double passRate = _totalDetections > 0
                ? (double)(_totalDetections - _totalDefects) / _totalDetections * 100
                : 100;

            var lines = new[]
            {
                $"Detections: {detectionCount}",
                $"Tracks:     {trackCount}",
                $"Total:      {_totalDetections}",
                $"Defects:    {_totalDefects}",
                $"Pass Rate:  {passRate:F1}%"
            };

            int y = panelY + 18;
            foreach (var line in lines)
            {
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

            Cv2.PutText(canvas, $"Frame: {_totalFrames}  |  OpenCV 5 + Active Perception",
                new OpenCvSharp.Point(15, 45),
                HersheyFonts.HersheySimplex, 0.38, new Scalar(120, 120, 120), 1, LineTypes.AntiAlias);
        }

        /// <summary>重置统计数据</summary>
        public void ResetStats()
        {
            _totalDetections = 0;
            _totalDefects = 0;
            _totalFrames = 0;
        }

        /// <summary>释放资源</summary>
        public void Dispose()
        {
            _layoutCanvas?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
