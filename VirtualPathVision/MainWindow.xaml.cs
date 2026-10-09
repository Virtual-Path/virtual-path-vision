using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VirtualPathVision.AI;
using VirtualPathVision.Cloud;
using VirtualPathVision.Industrial;
using VirtualPathVision.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using OpenCvSharp;

namespace VirtualPathVision
{
    /// <summary>
    /// 主窗口，负责整个应用的 UI 交互和业务逻辑编排。
    /// 协调视频采集、图像处理（多模式）、人脸检测、录制、日志等各组件的协作。
    /// </summary>
    public partial class MainWindow : System.Windows.Window
    {
        // ---- 各功能组件 ----
        private Components.VideoCaptureComponent _videoCaptureComponent;
        private Components.ImageDisplayComponent _imageDisplayComponent;
        private Components.ThresholdParameterComponent _thresholdParameterComponent;

        // ---- 产线编排 ----
        // 编排层默认不启用：它会引入计数与 MES 上报这类有外部副作用的行为，
        // 必须由用户在界面上显式打开。
        private Industrial.ProductionLineService? _productionLine;
        private readonly List<Components.ColorDetectionComponent.Detection> _detectionBuffer = new();
        private readonly List<Industrial.ProductionLineService.Detection> _lineDetectionBuffer = new();
        private string _colorNameForTracking = "Red";

        // ---- 关窗期协作 ----
        /// <summary>
        /// 窗口是否正在关闭。
        ///
        /// <para>关闭一开始就要置位：采集线程跑在 <c>ProcessFrame</c> 里，
        /// 而 <c>ProcessFrame</c> 会向 UI 线程封送更新。若此时 UI 线程已经
        /// 进入 <c>MainWindow_Closed</c> 并阻塞在 <c>StopCapture</c> 的
        /// <c>finished.Wait(1000)</c> 上，两者互等，那 1 秒必然超时，
        /// 随后原生句柄会在采集循环还活着的时候被释放。</para>
        /// </summary>
        private volatile bool _shuttingDown;

        /// <summary>是否有一次 UI 更新正在排队/执行。</summary>
        private int _uiDispatchInFlight;

        /// <summary>
        /// 把 UI 更新<b>非阻塞地</b>封送到 Dispatcher。
        ///
        /// <para><b>为什么不能用 Dispatcher.Invoke</b>：<c>ProcessFrame</c> 跑在采集
        /// 线程上，一次 <c>Invoke</c> 就等于让采集线程等 UI 线程。UI 线程一旦因为
        /// 关窗而停止泵消息，采集循环就再也退不出来。实测关窗稳定耗时 1.09 秒，
        /// 并伴随 <c>capture loop did not stop in time</c> 错误——正是这个互等。</para>
        ///
        /// <para><b>为什么要有在途去重</b>：采集可达 30~120 fps，而 UI 未必跟得上。
        /// 不去重的话 Dispatcher 队列会无限增长，内存涨、界面越来越滞后。
        /// 丢掉过期帧在语义上也是对的：预览本来就只需要"最新一帧"。</para>
        ///
        /// <para>顺序性：同一个采集线程、同优先级投递，Dispatcher 按 FIFO 处理，
        /// 因此不会乱序。</para>
        /// </summary>
        private void InvokeUi(Action action)
        {
            if (_shuttingDown) return;

            // 已有一帧在途就丢弃本次更新：只保留最新状态
            if (Interlocked.CompareExchange(ref _uiDispatchInFlight, 1, 0) != 0)
                return;

            // 单个委托：更新与"解除在途标记"必须原子地排在同一次投递里。
            // 拆成两次投递、或者用更高优先级解除，都会插队并破坏 FIFO。
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                try { action(); }
                finally { Interlocked.Exchange(ref _uiDispatchInFlight, 0); }
            }), DispatcherPriority.Background);
        }

        /// <summary>
        /// 非阻塞投递一次 UI 更新，<b>不去重</b>。
        /// </summary>
        /// <remarks>
        /// 给一次性事件用（采集停止、连接状态变化等）：这些更新丢了就再也
        /// 不会补上——面板会停在脏状态。不能用 <see cref="InvokeUi"/>，
        /// 它为每帧刷新设计的在途去重会把它们挤掉。
        /// </remarks>
        private void PostUi(Action action)
        {
            if (_shuttingDown) return;
            _ = Dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
        }

        /// <summary>
        /// 检测区 ROI。
        ///
        /// 默认对准传送带中部的检测工位（画面中部偏下）。
        /// 真实产线需要按相机标定结果调整，这里先给一个可用值并允许用户后续改。
        /// </summary>
        /// <summary>
        /// 检测区 ROI。
        ///
        /// <para>取自产线卡片上的输入框，而不是写死在此处：ROI 依赖具体的
        /// 分辨率与相机标定，写死会在换分辨率时静默漏检——而漏检在产线上
        /// 是不会报警的。默认值见 <c>ProductionLineCard.DefaultRoi*</c>。</para>
        /// </summary>
        private OpenCvSharp.Rect InspectionRoiRect => IndustrialPanelCtrl.LineCard.RoiRect;
        private Components.FaceDetectionComponent _faceDetectionComponent;
        private Components.ImageProcessingComponent _imageProcessingComponent;
        private Components.RecordingComponent _recordingComponent;
        private Components.BarcodeDetectionComponent _barcodeDetectionComponent;
        private Components.ColorDetectionComponent _colorDetectionComponent;
        private Components.TemplateMatchComponent _templateMatchComponent;
        private Components.ShapeDetectionComponent _shapeDetectionComponent;
        private Components.FeatureMatchComponent _featureMatchComponent;
        private Components.EnhancementComponent _enhancementComponent;

        // ---- 工业互联（企业化）服务 ----
        private Industrial.ModbusTcpDriver _modbusDriver;
        private Industrial.OpcUaDriver _opcUaDriver;
        private Industrial.SerialScanDriver _serialDriver;
        private Industrial.TcpScanDriver _tcpDriver;
        private Industrial.WorkReportService _workReportService;
        private Industrial.IndustrialConfig _industrialConfig;
        private AppConfig _appConfig = new();

        // ---- AI 模块 ----
        private AI.YoloDetectionComponent? _yoloComponent;
        private AI.KalmanTrackerComponent? _kalmanTracker;
        private AI.ActivePerceptionEngine? _activePerception;
        private AI.DigitalTwinRenderer? _digitalTwin;
        private bool _aiEnabled;
        private readonly object _aiLock = new();

        // ---- Cloud 模块 ----
        private Cloud.S3Service? _s3Service;
        private Cloud.IoTService? _iotService;
        private Cloud.LambdaClient? _lambdaClient;
        private int _detectionTotalCount;
        private int _detectionDefectCount;
        private int _aiFramesSinceIotPublish;
        private long _aiDetectionFrameCount;

        /// <summary>AI 检测帧数达到该间隔时向 IoT 上报一次遥测</summary>
        private const int IotPublishIntervalFrames = 30;

        // ---- 处理参数 ----
        private Components.ProcessingMode _currentMode = Components.ProcessingMode.Canny;
        private int _threshold1 = 100;
        private int _threshold2 = 200;
        private bool _networkConfigured;
        private string? _lastDecodedText;
        private bool _suppressSelectionEvents;
        private Mat? _lastOriginalFrame;

        // ---- 性能追踪 ----
        private readonly Stopwatch _frameStopwatch = new();
        private int _frameCount;
        private double _currentFps;
        private DateTime _lastFpsUpdate = DateTime.Now;

        /// <summary>
        /// 构造函数：初始化组件、注册事件。
        /// </summary>
        public MainWindow()
        {
            InitializeComponent();
            InitErrorTimer();
            StateChanged += (_, _) => UpdateMaximizeIcon();

            _faceDetectionComponent = new Components.FaceDetectionComponent(
                System.IO.Path.Combine(AppContext.BaseDirectory, "face_detection_yunet_2023mar.onnx"));
            _imageProcessingComponent = new Components.ImageProcessingComponent();
            _videoCaptureComponent = new Components.VideoCaptureComponent();
            _imageDisplayComponent = new Components.ImageDisplayComponent(
                CameraPanelCtrl.OriginalImageEl, CameraPanelCtrl.EdgeImageEl);
            _thresholdParameterComponent = new Components.ThresholdParameterComponent(
                ProcessingPanelCtrl.Threshold1TextBoxEl, ProcessingPanelCtrl.Threshold2TextBoxEl,
                ProcessingPanelCtrl.ApplyThresholdsButtonEl);
            _recordingComponent = new Components.RecordingComponent();
            // 订阅录制状态变化：此前该事件无人订阅，导致语言切换时
            // RefreshTexts 会把按钮文案无条件重置为「开始录制」，
            // 即使此刻正在录制，按钮也会显示成开始。
            _recordingComponent.OnRecordingStateChanged += OnRecordingStateChanged;
            _barcodeDetectionComponent = new Components.BarcodeDetectionComponent();
            _colorDetectionComponent = new Components.ColorDetectionComponent();
            _templateMatchComponent = new Components.TemplateMatchComponent();
            _shapeDetectionComponent = new Components.ShapeDetectionComponent();
            _featureMatchComponent = new Components.FeatureMatchComponent();
            _enhancementComponent = new Components.EnhancementComponent();

            // 工业互联服务（依赖注入容器解析）
            _modbusDriver = App.Services.GetRequiredService<Industrial.ModbusTcpDriver>();
            _opcUaDriver = App.Services.GetRequiredService<Industrial.OpcUaDriver>();
            _serialDriver = App.Services.GetRequiredService<Industrial.SerialScanDriver>();
            _tcpDriver = App.Services.GetRequiredService<Industrial.TcpScanDriver>();
            _workReportService = App.Services.GetRequiredService<Industrial.WorkReportService>();
            _industrialConfig = App.Services.GetRequiredService<Industrial.IndustrialConfig>();
            _appConfig = App.Services.GetRequiredService<AppConfig>();

            // 设备驱动状态/错误事件（后台线程 → Dispatcher 封送）
            _modbusDriver.OnStateChanged += s => Dispatcher.Invoke(() => UpdateModbusState(s));
            _modbusDriver.OnError += msg => Dispatcher.Invoke(() => AppLogger.Instance.Error($"Modbus: {msg}"));
            _opcUaDriver.OnStateChanged += s => Dispatcher.Invoke(() => UpdateOpcUaState(s));
            _opcUaDriver.OnError += msg => Dispatcher.Invoke(() => AppLogger.Instance.Error($"OPC-UA: {msg}"));

            // 扫码数据源事件：条码 → 报工
            _serialDriver.OnBarcodeScanned += code => Dispatcher.Invoke(() => AddReport(code, TranslationService.Instance.ScanSerial));
            _serialDriver.OnError += msg => Dispatcher.Invoke(() => AppLogger.Instance.Error($"串口: {msg}"));
            _tcpDriver.OnBarcodeScanned += code => Dispatcher.Invoke(() => AddReport(code, TranslationService.Instance.ScanTcp));
            _tcpDriver.OnError += msg => Dispatcher.Invoke(() => AppLogger.Instance.Error($"TCP: {msg}"));

            _videoCaptureComponent.OnFrameCaptured += ProcessFrame;
            _videoCaptureComponent.OnCaptureStopped += OnCaptureStoppedHandler;
            _videoCaptureComponent.OnCaptureError += OnCaptureErrorHandler;
            _videoCaptureComponent.OnConnectionStateChanged += OnConnectionStateChangedHandler;

            IndustrialPanelCtrl.LineCard.EnabledChanged += (_s, _e) => ApplyProductionLineSettings();
            IndustrialPanelCtrl.LineCard.SettingsChanged += (_s, _e) => ApplyProductionLineSettings();

            CameraPanelCtrl.BrowseReplayRequested += CameraPanelCtrl_BrowseReplayRequested;
            CameraPanelCtrl.ReplayConfigChanged += (_s, _e) => ApplyReplaySettings();

            // 阈值来源只保留一处：ThresholdParameterComponent 的 Apply 按钮。
            // ProcessingPanelCtrl.ThresholdsChanged（滑块）已在下面单独订阅，
            // 若两处都订阅 UpdateThresholds，每次调整会写两条重复日志。
            _thresholdParameterComponent.OnThresholdsChanged += UpdateThresholds;

            TranslationService.Instance.PropertyChanged += OnLanguageChangedHandler;

            // ---- UserControl 事件订阅 ----
            CameraPanelCtrl.ConnectRequested += CameraPanelCtrl_ConnectRequested;
            CameraPanelCtrl.DisconnectRequested += CameraPanelCtrl_DisconnectRequested;
            CameraPanelCtrl.LoadImageRequested += CameraPanelCtrl_LoadImageRequested;
            CameraPanelCtrl.StartCameraRequested += CameraPanelCtrl_StartCameraRequested;
            CameraPanelCtrl.StopCameraRequested += CameraPanelCtrl_StopCameraRequested;
            CameraPanelCtrl.ScreenshotRequested += CameraPanelCtrl_ScreenshotRequested;
            CameraPanelCtrl.RecordRequested += CameraPanelCtrl_RecordRequested;

            ProcessingPanelCtrl.ThresholdsChanged += ProcessingPanelCtrl_ThresholdsChanged;
            ProcessingPanelCtrl.ModeChanged += ProcessingPanelCtrl_ModeChanged;
            ProcessingPanelCtrl.ColorChanged += ProcessingPanelCtrl_ColorChanged;
            ProcessingPanelCtrl.LoadTemplateRequested += ProcessingPanelCtrl_LoadTemplateRequested;

            AIPanelCtrl.LoadModelRequested += AIPanelCtrl_LoadModelRequested;
            AIPanelCtrl.EnableChanged += AIPanelCtrl_EnableChanged;
            // 置信度此前只有声明没有任何订阅，改了也不生效；这里接上实时更新
            AIPanelCtrl.ConfidenceChanged += AIPanelCtrl_ConfidenceChanged;

            CloudPanelCtrl.InitRequested += CloudPanelCtrl_InitRequested;
            CloudPanelCtrl.UploadScreenshotRequested += CloudPanelCtrl_UploadScreenshotRequested;
            CloudPanelCtrl.SendAlertRequested += CloudPanelCtrl_SendAlertRequested;
            CloudPanelCtrl.PublishStatsRequested += CloudPanelCtrl_PublishStatsRequested;
            CloudPanelCtrl.LambdaInvokeRequested += CloudPanelCtrl_LambdaInvokeRequested;

            IndustrialPanelCtrl.ModbusConnectRequested += IndustrialPanelCtrl_ModbusConnectRequested;
            IndustrialPanelCtrl.ModbusDisconnectRequested += IndustrialPanelCtrl_ModbusDisconnectRequested;
            IndustrialPanelCtrl.ModbusReadRequested += IndustrialPanelCtrl_ModbusReadRequested;
            IndustrialPanelCtrl.ModbusWriteRequested += IndustrialPanelCtrl_ModbusWriteRequested;
            IndustrialPanelCtrl.OpcUaConnectRequested += IndustrialPanelCtrl_OpcUaConnectRequested;
            IndustrialPanelCtrl.OpcUaDisconnectRequested += IndustrialPanelCtrl_OpcUaDisconnectRequested;
            IndustrialPanelCtrl.OpcUaReadRequested += IndustrialPanelCtrl_OpcUaReadRequested;
            IndustrialPanelCtrl.OpcUaWriteRequested += IndustrialPanelCtrl_OpcUaWriteRequested;
            IndustrialPanelCtrl.SerialToggleRequested += IndustrialPanelCtrl_SerialToggleRequested;
            IndustrialPanelCtrl.TcpToggleRequested += IndustrialPanelCtrl_TcpToggleRequested;
            IndustrialPanelCtrl.ExportCsvRequested += IndustrialPanelCtrl_ExportCsvRequested;
            IndustrialPanelCtrl.ClearReportRequested += IndustrialPanelCtrl_ClearReportRequested;
            IndustrialPanelCtrl.ScanSourceChanged += IndustrialPanelCtrl_ScanSourceChanged;

            LogPanelCtrl.ClearRequested += LogPanelCtrl_ClearRequested;

            // 侧栏行控件缓存：折叠/展开时需要批量切换内容对齐方式
            _sidebarNavItems = new Control[] { NavVision, NavProcessing, NavAI, NavCloud, NavIndustrial, NavLog };
            _sidebarFooterItems = new Control[] { SettingsButton, SidebarToggleButton };

            // 尽早应用侧栏布局：此前等到 Loaded 才设置，首帧会先按 XAML 里的
            // 宽度渲染再跳变一次，视觉上像闪烁。
            _sidebarUserExpanded = LoadSidebarExpanded();
            UpdateLayoutForWidth(Width);

            Loaded += MainWindow_Loaded;
            Closed += MainWindow_Closed;
            SizeChanged += MainWindow_SizeChanged;
        }

        // ==================== 自适应布局 ====================

        /// <summary>
        /// 窗口大小变化时根据宽度调整侧栏布局。
        /// </summary>
        private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.WidthChanged)
            {
                UpdateLayoutForWidth(e.NewSize.Width);
            }
        }

        /// <summary>侧栏用户偏好：是否展开（持久化到 user_settings.json）</summary>
        private bool _sidebarUserExpanded = true;

        /// <summary>侧栏宽度：展开 / 折叠</summary>
        private const double SidebarExpandedWidth = 216;
        private const double SidebarCollapsedWidth = 76;

        /// <summary>窗口窄于此宽度时侧栏强制折叠</summary>
        private const double SidebarNarrowThresholdWidth = 1180;

        /// <summary>
        /// 侧栏当前的实际展开状态（= 用户偏好 && 窗口够宽）。
        /// 与 _sidebarUserExpanded 区分：窗口过窄时用户偏好仍是展开，
        /// 但视觉上已折叠，折叠按钮文案必须按这个值走。
        /// </summary>
        private bool _sidebarCurrentlyExpanded = true;

        /// <summary>
        /// 侧栏行控件（导航项 / 底栏按钮）。折叠态需要把内容整体居中，
        /// 而这些控件只在 XAML 中定义一次，故集中缓存以便统一调整对齐方式。
        /// 构造函数中 InitializeComponent 之后赋值。
        /// </summary>
        private readonly Control[] _sidebarNavItems = Array.Empty<Control>();
        private readonly Control[] _sidebarFooterItems = Array.Empty<Control>();

        /// <summary>
        /// 根据用户偏好与窗口宽度调整侧栏：
        /// - 展开：216px（图标 + 文字 + 分组标题）
        /// - 折叠：76px（仅图标，文字与分组标题隐藏）
        /// 窗口过窄时强制折叠并隐藏折叠按钮。
        /// </summary>
        private void UpdateLayoutForWidth(double width)
        {
            bool narrowWindow = width < SidebarNarrowThresholdWidth;
            bool expanded = _sidebarUserExpanded && !narrowWindow;
            _sidebarCurrentlyExpanded = expanded;

            if (SidebarColumn != null)
                SidebarColumn.Width = new GridLength(expanded ? SidebarExpandedWidth : SidebarCollapsedWidth);

            var vis = expanded ? Visibility.Visible : Visibility.Collapsed;
            SetVisibility(SidebarText, vis);
            SetVisibility(SidebarGroupWorkspace, vis);
            SetVisibility(SidebarGroupSystem, vis);
            SetVisibility(SidebarGroupDivider, vis);
            SetVisibility(SidebarSettingsLabel, vis);
            SetVisibility(SidebarToggleLabel, vis);
            SetVisibility(NavVisionLabel, vis);
            SetVisibility(NavProcessingLabel, vis);
            SetVisibility(NavAILabel, vis);
            SetVisibility(NavCloudLabel, vis);
            SetVisibility(NavIndustrialLabel, vis);
            SetVisibility(NavLogLabel, vis);

            // 折叠态只剩图标，内容需整体居中；展开态左对齐到导航图标列。
            var contentAlign = expanded ? HorizontalAlignment.Left : HorizontalAlignment.Center;
            foreach (var item in _sidebarNavItems)
                item.HorizontalContentAlignment = contentAlign;
            foreach (var item in _sidebarFooterItems)
                item.HorizontalContentAlignment = contentAlign;

            // Logo：展开时左对齐到导航图标列，折叠时整体居中
            if (SidebarLogoStack != null)
            {
                SidebarLogoStack.HorizontalAlignment = expanded ? HorizontalAlignment.Left : HorizontalAlignment.Center;
                SidebarLogoStack.Margin = expanded ? new Thickness(27, 0, 0, 0) : new Thickness(0);
            }

            if (SidebarToggleButton != null)
                SidebarToggleButton.Visibility = narrowWindow ? Visibility.Collapsed : Visibility.Visible;

            UpdateSidebarToggleAffordance(expanded);
        }

        /// <summary>刷新折叠按钮的图标方向、提示文案与底部分组分隔线</summary>
        private void UpdateSidebarToggleAffordance(bool expanded)
        {
            var t = TranslationService.Instance;

            // E76B = 左箭头（可折叠）；E76C = 右箭头（可展开）
            if (SidebarToggleIcon != null)
                SidebarToggleIcon.Text = expanded ? "\uE76B" : "\uE76C";

            if (SidebarToggleButton != null)
                SidebarToggleButton.ToolTip = expanded ? t.NavCollapse : t.NavExpand;

            // 折叠态只剩两枚图标按钮，分隔线反而多余
            if (SidebarFooterDivider != null)
                SidebarFooterDivider.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>侧边栏文字/分组标题的显隐统一入口</summary>
        private static void SetVisibility(UIElement? element, Visibility visibility)
        {
            if (element != null) element.Visibility = visibility;
        }

        /// <summary>
        /// 侧边栏文案刷新。导航项使用短标签而非面板标题
        /// （「AI 检测 (YOLO)」「AWS S3 存储」在窄侧栏里会被截断）。
        /// </summary>
        private void RefreshSidebarTexts()
        {
            var t = TranslationService.Instance;

            NavVisionLabel.Text = t.NavCamera;
            NavProcessingLabel.Text = t.NavImage;
            NavAILabel.Text = t.NavAI;
            NavCloudLabel.Text = t.NavCloud;
            NavIndustrialLabel.Text = t.NavIndustrial;
            NavLogLabel.Text = t.NavLog;

            SidebarGroupWorkspace.Text = t.NavGroupWorkspace;
            SidebarGroupSystem.Text = t.NavGroupSystem;
            SidebarSettingsLabel.Text = t.Settings;
            SidebarToggleLabel.Text = _sidebarCurrentlyExpanded ? t.NavCollapse : t.NavExpand;

            // 折叠态没有文字可看，Tooltip 是唯一标识，必须跟着语言走
            NavVision.ToolTip = t.NavCamera;
            NavProcessing.ToolTip = t.NavImage;
            NavAI.ToolTip = t.NavAI;
            NavCloud.ToolTip = t.NavCloud;
            NavIndustrial.ToolTip = t.NavIndustrial;
            NavLog.ToolTip = t.NavLog;
            SettingsButton.ToolTip = t.Settings;

            UpdateSidebarToggleAffordance(_sidebarCurrentlyExpanded);
        }

        /// <summary>折叠 / 展开侧边栏</summary>
        private void SidebarToggleButton_Click(object sender, RoutedEventArgs e)
        {
            _sidebarUserExpanded = !_sidebarUserExpanded;
            SaveSidebarExpanded(_sidebarUserExpanded);
            UpdateLayoutForWidth(ActualWidth);
            // 折叠按钮自身的文字也要跟着切换（折叠 / 展开）
            RefreshSidebarTexts();
        }

        /// <summary>侧栏展开状态（统一走 UserSettings，避免与主题/语言互相覆盖）</summary>
        private static bool LoadSidebarExpanded()
            => UserSettings.GetBool("SidebarExpanded", true);

        private static void SaveSidebarExpanded(bool expanded)
            => UserSettings.Set("SidebarExpanded", expanded);

        // ==================== 侧边栏导航 ====================

        /// <summary>
        /// 侧边栏导航按钮点击：切换面板可见性。
        /// </summary>
        private void NavButton_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as RadioButton;
            string tag = btn?.Tag as string ?? "";

            CameraPanelCtrl.Visibility = tag == "Vision" ? Visibility.Visible : Visibility.Collapsed;
            ProcessingPanelCtrl.Visibility = tag == "Processing" ? Visibility.Visible : Visibility.Collapsed;
            AIPanelCtrl.Visibility = tag == "AI" ? Visibility.Visible : Visibility.Collapsed;
            CloudPanelCtrl.Visibility = tag == "Cloud" ? Visibility.Visible : Visibility.Collapsed;
            IndustrialPanelCtrl.Visibility = tag == "Industrial" ? Visibility.Visible : Visibility.Collapsed;
            LogPanelCtrl.Visibility = tag == "Log" ? Visibility.Visible : Visibility.Collapsed;
        }

        // ==================== 窗口控制按钮 ====================

        /// <summary>最小化窗口</summary>
        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        /// <summary>最大化/还原窗口</summary>
        private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }

        /// <summary>根据窗口状态切换最大化/还原图标</summary>
        private void UpdateMaximizeIcon()
        {
            MaximizeBtn.Content = WindowState == WindowState.Maximized ? "" : "";
        }

        /// <summary>关闭窗口</summary>
        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        /// <summary>隐藏错误横幅</summary>
        private void HideErrorButton_Click(object sender, RoutedEventArgs e)
        {
            HideError();
        }

        /// <summary>拖拽移动窗口</summary>
        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                WindowState = WindowState == WindowState.Maximized
                    ? WindowState.Normal
                    : WindowState.Maximized;
            }
            else
            {
                DragMove();
            }
        }

        /// <summary>打开设置窗口</summary>
        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            var settings = new SettingsWindow(this) { Owner = this };
            settings.ShowDialog();
        }

        // ==================== 语言切换 ====================

        /// <summary>
        /// 语言切换处理：重建所有本地化控件文本。
        /// </summary>
        private void OnLanguageChangedHandler(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            Dispatcher.Invoke(RefreshAllTexts);
        }

        /// <summary>
        /// 刷新所有面板的文本（语言切换时调用）。
        /// </summary>
        public void RefreshAllTexts()
        {
            RefreshLocalizedControls();

            // 侧边栏导航文字与提示（宽侧栏时显示文字，折叠态靠 Tooltip）
            RefreshSidebarTexts();

            // 标题栏/状态栏连接状态文本按当前语言重绘
            var t = TranslationService.Instance;
            string connText = t.GetConnectionStatusText(_lastConnState);
            StatusText.Text = connText;
            StatusTextFooter.Text = connText;

            CameraPanelCtrl.RefreshTexts();
            ProcessingPanelCtrl.RefreshTexts();
            AIPanelCtrl.RefreshTexts();
            CloudPanelCtrl.RefreshTexts();
            IndustrialPanelCtrl.RefreshTexts();
            // 扫码开关按钮按实际运行状态重绘（RefreshTexts 默认置为“启动”）
            IndustrialPanelCtrl.SerialToggleButtonEl.Content =
                _serialDriver.IsRunning ? TranslationService.Instance.Stop : TranslationService.Instance.Start;
            IndustrialPanelCtrl.TcpToggleButtonEl.Content =
                _tcpDriver.IsRunning ? TranslationService.Instance.Stop : TranslationService.Instance.Start;
            LogPanelCtrl.RefreshTexts();
        }

        /// <summary>
        /// 重建下拉框等代码设置的本地化文本（初始加载和语言切换时调用）。
        /// </summary>
        private void RefreshLocalizedControls()
        {
            _suppressSelectionEvents = true;
            try
            {
                int srcIndex = CameraPanelCtrl.SourceTypeComboBoxEl.SelectedIndex;
                CameraPanelCtrl.SourceTypeComboBoxEl.ItemsSource = null;
                CameraPanelCtrl.SourceTypeComboBoxEl.Items.Clear();
                CameraPanelCtrl.SourceTypeComboBoxEl.ItemsSource = new string[]
                {
                    TranslationService.Instance.LocalCamera,
                    TranslationService.Instance.NetworkStream,
                    TranslationService.Instance.FileReplay
                };
                CameraPanelCtrl.SourceTypeComboBoxEl.SelectedIndex = srcIndex < 0 ? 0 : srcIndex;

                int modeIndex = ProcessingPanelCtrl.ProcessingModeComboBoxEl.SelectedIndex;
                ProcessingPanelCtrl.ProcessingModeComboBoxEl.ItemsSource = null;
                ProcessingPanelCtrl.ProcessingModeComboBoxEl.Items.Clear();
                ProcessingPanelCtrl.ProcessingModeComboBoxEl.ItemsSource = new string[]
                {
                    TranslationService.Instance.ModeCanny,
                    TranslationService.Instance.ModeSobel,
                    TranslationService.Instance.ModeLaplacian,
                    TranslationService.Instance.ModeBinary,
                    TranslationService.Instance.ModeContour,
                    TranslationService.Instance.ModeQRCode,
                    TranslationService.Instance.ModeColorDetection,
                    TranslationService.Instance.ModeTemplateMatch,
                    TranslationService.Instance.ModeShapeDetection,
                    TranslationService.Instance.ModeFeatureMatch,
                    TranslationService.Instance.ModeEnhancement
                };
                ProcessingPanelCtrl.ProcessingModeComboBoxEl.SelectedIndex = modeIndex < 0 ? 0 : modeIndex;

                int colorIndex = ProcessingPanelCtrl.ColorComboBoxEl.SelectedIndex;
                ProcessingPanelCtrl.ColorComboBoxEl.ItemsSource = null;
                ProcessingPanelCtrl.ColorComboBoxEl.Items.Clear();
                ProcessingPanelCtrl.ColorComboBoxEl.ItemsSource = TranslationService.Instance.ColorNames;
                ProcessingPanelCtrl.ColorComboBoxEl.SelectedIndex = colorIndex < 0 ? 0 : colorIndex;

                int scanIndex = IndustrialPanelCtrl.ScanSourceComboBoxEl.SelectedIndex;
                IndustrialPanelCtrl.ScanSourceComboBoxEl.ItemsSource = null;
                IndustrialPanelCtrl.ScanSourceComboBoxEl.Items.Clear();
                IndustrialPanelCtrl.ScanSourceComboBoxEl.ItemsSource = TranslationService.Instance.ScanSourceNames;
                IndustrialPanelCtrl.ScanSourceComboBoxEl.SelectedIndex = scanIndex < 0 ? 0 : scanIndex;
            }
            finally
            {
                _suppressSelectionEvents = false;
            }

            ProcessingPanelCtrl.SetModeName(GetModeName(_currentMode));
            ProcessingPanelCtrl.SetResultTitle(GetResultTitle(_currentMode));
            ProcessingPanelCtrl.SetTemplateStatus(_templateMatchComponent.HasTemplate
                ? $"{TranslationService.Instance.TemplateLoaded} ({_templateMatchComponent.TemplateWidth}x{_templateMatchComponent.TemplateHeight})"
                : TranslationService.Instance.NoTemplate);
        }

        /// <summary>获取模式显示名称</summary>
        private string GetModeName(Components.ProcessingMode mode)
        {
            return mode switch
            {
                Components.ProcessingMode.Sobel => TranslationService.Instance.ModeSobel,
                Components.ProcessingMode.Laplacian => TranslationService.Instance.ModeLaplacian,
                Components.ProcessingMode.Binary => TranslationService.Instance.ModeBinary,
                Components.ProcessingMode.Contour => TranslationService.Instance.ModeContour,
                Components.ProcessingMode.QRCode => TranslationService.Instance.ModeQRCode,
                Components.ProcessingMode.ColorDetection => TranslationService.Instance.ModeColorDetection,
                Components.ProcessingMode.TemplateMatch => TranslationService.Instance.ModeTemplateMatch,
                Components.ProcessingMode.ShapeDetection => TranslationService.Instance.ModeShapeDetection,
                Components.ProcessingMode.FeatureMatch => TranslationService.Instance.ModeFeatureMatch,
                Components.ProcessingMode.Enhancement => TranslationService.Instance.ModeEnhancement,
                _ => TranslationService.Instance.ModeCanny
            };
        }

        /// <summary>获取结果面板标题</summary>
        private string GetResultTitle(Components.ProcessingMode mode)
        {
            return mode switch
            {
                Components.ProcessingMode.QRCode => TranslationService.Instance.ModeQRCode,
                Components.ProcessingMode.ColorDetection => TranslationService.Instance.ModeColorDetection,
                Components.ProcessingMode.TemplateMatch => TranslationService.Instance.ModeTemplateMatch,
                Components.ProcessingMode.ShapeDetection => TranslationService.Instance.ModeShapeDetection,
                Components.ProcessingMode.FeatureMatch => TranslationService.Instance.ModeFeatureMatch,
                Components.ProcessingMode.Enhancement => TranslationService.Instance.ModeEnhancement,
                _ => TranslationService.Instance.ResultView
            };
        }

        // ==================== 窗口生命周期 ====================

        /// <summary>
        /// 窗口加载完成：初始化下拉框和面板状态。
        /// </summary>
        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            RestoreWindowState();
            UpdateLayoutForWidth(ActualWidth); // RestoreWindowState 可能改变宽度，重算一次
            RefreshAllTexts(); // 语言切换 + 本地化控件全部刷新（启动时按已保存语言初始化）

            // 日志面板
            LogPanelCtrl.SetLogSource(AppLogger.Instance.Entries);

            // AppLogger 已把 OnLogAdded 封送到 UI 线程，这里无需再 Dispatcher.Invoke。
            // 同时保存委托引用以便在 Closed 时退订，避免静态单例上的事件
            // 把已关闭的窗口及其 6 个面板永久挂在内存里。
            _logAddedHandler = _ =>
            {
                if (LogPanelCtrl.Visibility == Visibility.Visible)
                    LogPanelCtrl.ScrollToBottom();
            };
            AppLogger.Instance.OnLogAdded += _logAddedHandler;

            // ---- 工业互联面板初始化 ----
            IndustrialPanelCtrl.SetSerialPortNames(Industrial.SerialScanDriver.GetPortNames());
            IndustrialPanelCtrl.SetSerialBaudRates(new[] { "9600", "19200", "38400", "57600", "115200" });
            IndustrialPanelCtrl.ReportListBoxEl.ItemsSource = _workReportService.Records;
            IndustrialPanelCtrl.UpdateReportStats(_workReportService.TodayCount, "--");

            // 配置回填
            IndustrialPanelCtrl.ModbusIpTextBoxEl.Text = _industrialConfig.Modbus.Ip;
            IndustrialPanelCtrl.ModbusPortTextBoxEl.Text = _industrialConfig.Modbus.Port.ToString();
            IndustrialPanelCtrl.ModbusUnitTextBoxEl.Text = _industrialConfig.Modbus.UnitId.ToString();
            IndustrialPanelCtrl.OpcUaEndpointTextBoxEl.Text = _industrialConfig.OpcUa.Endpoint;
            IndustrialPanelCtrl.TcpScanPortTextBoxEl.Text = _industrialConfig.TcpScanner.Port.ToString();
            IndustrialPanelCtrl.PlcRegisterTextBoxEl.Text = _industrialConfig.Report.PlcCountRegister.ToString();
            IndustrialPanelCtrl.ModbusLinkCheckBoxEl.IsChecked = _industrialConfig.Report.PlcReportEnable;

            // ---- Cloud / AI 配置回填 ----
            // 此前 AWS/AI 段落虽写在 appsettings.json，却完全没有被读取，
            // 导致区域/桶等参数每次启动都要手工重填。
            var aws = _appConfig.AWS;
            if (!string.IsNullOrWhiteSpace(aws.Region))
                CloudPanelCtrl.AwsRegionTextBoxEl.Text = aws.Region;
            if (!string.IsNullOrWhiteSpace(aws.S3Bucket))
                CloudPanelCtrl.S3BucketTextBoxEl.Text = aws.S3Bucket;
            if (!string.IsNullOrWhiteSpace(aws.IoTEndpoint))
                CloudPanelCtrl.IoTEndpointTextBoxEl.Text = aws.IoTEndpoint;
            if (!string.IsNullOrWhiteSpace(aws.IoTTopicPrefix))
                CloudPanelCtrl.IoTTopicTextBoxEl.Text = aws.IoTTopicPrefix;
            if (!string.IsNullOrWhiteSpace(aws.LambdaFunctionName))
                CloudPanelCtrl.LambdaFuncTextBoxEl.Text = aws.LambdaFunctionName;

            AIPanelCtrl.SetConfidence((float)Math.Clamp(_appConfig.AI.ConfidenceThreshold, 0.01, 1.0));
            AIPanelCtrl.SetMatchThreshold(_appConfig.AI.NmsThreshold <= 0 ? 0.6f : (float)_appConfig.AI.NmsThreshold);
            AIPanelCtrl.SetMaxTrail(Math.Clamp(_appConfig.AI.MaxLostFrames, 2, 500));
        }

        /// <summary>
        /// 窗口关闭：释放所有组件资源。
        /// </summary>

        private static readonly string TracePath =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vpshutdown_trace.txt");
        private static readonly System.Diagnostics.Stopwatch TraceWatch =
            System.Diagnostics.Stopwatch.StartNew();
        private static void ___TRACE_BEGIN(string what)
        {
            try
            {
                System.IO.File.AppendAllText(TracePath,
                    $"{TraceWatch.Elapsed.TotalMilliseconds,10:F0}ms  -> {what}{System.Environment.NewLine}");
            }
            catch { }
        }
        private static void ___TRACE_END(string what)
        {
            try
            {
                System.IO.File.AppendAllText(TracePath,
                    $"{TraceWatch.Elapsed.TotalMilliseconds,10:F0}ms  <- {what}{System.Environment.NewLine}");
            }
            catch { }
        }
        private void MainWindow_Closed(object? sender, EventArgs e)
        {
            // 封送。晚置位就等于采集线程可能在下面任意一个 Dispose 里
            // 已经开始等 UI 线程，而 UI 线程正等它退出。
            _shuttingDown = true;

            // 退订静态单例上的事件：否则已关闭的窗口（及其全部面板）无法被 GC 回收
            if (_logAddedHandler != null)
                AppLogger.Instance.OnLogAdded -= _logAddedHandler;
            TranslationService.Instance.PropertyChanged -= OnLanguageChangedHandler;

            SaveWindowState();

            _recordingComponent.Dispose();
            _videoCaptureComponent.Dispose();
            _productionLine?.Dispose();
            _productionLine = null;
            _thresholdParameterComponent.Dispose();
            _templateMatchComponent.Clear();
            _featureMatchComponent.Dispose();
            _templateMatchComponent.Dispose();
            DisposeLastOriginalFrame();

            _serialDriver.Dispose();
            _tcpDriver.Dispose();
            _modbusDriver.Dispose();
            _opcUaDriver.Dispose();

            lock (_aiLock)
            {
                // _kalmanTracker 此前从未被释放，其内部每个 KalmanFilter 持有的
                // 原生 Mat 会在进程退出时全部泄漏
                _yoloComponent?.Dispose();
                _kalmanTracker?.Dispose();
                _digitalTwin?.Dispose();
            }
            _s3Service?.Dispose();
            _iotService?.Dispose();
            _lambdaClient?.Dispose();
        }

        // ==================== 信号源 & 连接 ====================

        /// <summary>根据 IP、端口和路径构建网络流 URL，并同步信号源类型</summary>
        private void BuildNetworkUrl()
        {
            string ip = CameraPanelCtrl.IPTextBoxEl.Text.Trim();
            string port = CameraPanelCtrl.PortTextBoxEl.Text.Trim();
            string path = CameraPanelCtrl.PathTextBoxEl.Text.Trim();

            // 路径必须补前导斜杠：用户填 "cam1" 与 "/cam1" 语义相同，
            // 不补会拼出 "http://ip:portcam1" 这种连不上的地址。
            if (path.Length > 0 && !path.StartsWith('/')) path = "/" + path;

            // 路径为空时退回 /cam1（VirtualPath-Core 的虚拟相机约定），
            // 而不是退回 /video —— 引擎只服务 /cam1。
            if (path.Length == 0) path = "/cam1";

            _videoCaptureComponent.NetworkUrl = $"http://{ip}:{port}{path}";
            _videoCaptureComponent.SourceType = Components.VideoSourceType.NetworkStream;
            _networkConfigured = !string.IsNullOrWhiteSpace(ip) && !string.IsNullOrWhiteSpace(port);
        }

        /// <summary>
        /// 把回放面板的输入同步到采集组件：文件路径、节流帧率、循环开关。
        /// FPS 留空或非数字时保持组件内的既有值，不强行改成 0（0 表示不节流）。
        /// </summary>
        private void ApplyReplaySettings()
        {
            string path = CameraPanelCtrl.ReplayPathTextBoxEl.Text.Trim();
            if (!string.IsNullOrWhiteSpace(path))
                _videoCaptureComponent.ReplayPath = path;

            if (double.TryParse(CameraPanelCtrl.ReplayFpsTextBoxEl.Text.Trim(),
                                out double fps) && fps > 0)
            {
                _videoCaptureComponent.ReplayFps = fps;
            }

            _videoCaptureComponent.ReplayLoop =
                CameraPanelCtrl.ReplayLoopCheckBoxEl.IsChecked == true;

            _videoCaptureComponent.SourceType = Components.VideoSourceType.FileReplay;
        }

        /// <summary>弹出文件选择器选择录像文件，取消则保持原路径。</summary>
        private void CameraPanelCtrl_BrowseReplayRequested(object? sender, EventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = TranslationService.GetStringStatic("SelectReplayFile"),
                Filter = "Video files|*.mp4;*.avi;*.mkv;*.mov;*.wmv;*.m4v|" +
                         "All files|*.*",
                CheckFileExists = true,
            };

            if (dialog.ShowDialog() != true) return;

            CameraPanelCtrl.ReplayPathTextBoxEl.Text = dialog.FileName;
            ApplyReplaySettings();
        }

        // ==================== CameraPanel 事件处理 ====================

        /// <summary>
    /// 把本帧的颜色检出喂给产线编排层：过滤 ROI 外的目标，交给稳定判定器，
    /// 并更新界面计数。
    /// </summary>
    /// <remarks>
    /// <b>ROI 的必要性</b>：不做 ROI 时，背景墙上与工件同色的诱饵色块、地面的
    /// 网格线都会满足 HSV 阈值并被当成工件。端到端实测 150 帧虚报 76 件，
    /// 而场景里只有 4 件在循环；加上 ROI 后降到 18 件。
    /// </remarks>
    /// <summary>
    /// 根据界面上的开关与参数重建（或释放）产线编排层。
    ///
    /// 编排层带外部副作用（计数、MES 上报），因此任何参数变化都整体重建而非就地修改，
    /// 避免旧实例残留未完成的判定状态。
    /// </summary>
    private void ApplyProductionLineSettings()
    {
        var card = IndustrialPanelCtrl.LineCard;

        _productionLine?.Dispose();
        _productionLine = null;

        if (!card.IsLineEnabled)
        {
            Dispatcher.Invoke(() =>
            {
                card.ResetCounters();
                card.SetMesState("Off");
                card.ShowEvent("");
            });
            AppLogger.Instance.Info("产线编排已停用");
            return;
        }

        var stability = new Industrial.StabilityFilter(
            requiredFrames: card.StableFrames,
            timeoutSeconds: 1.0);

        Industrial.MesClient? mes = null;
        string url = card.MesUrl;
        string token = card.MesToken;
        if (!string.IsNullOrWhiteSpace(url))
        {
            try
            {
                mes = new Industrial.MesClient(url, token);
            }
            catch (Exception ex)
            {
                // 网关地址非法不应阻断采集：编排层降级为"只统计不上报"
                AppLogger.Instance.Warn($"MES 客户端创建失败，仅本地统计: {ex.Message}");
                Dispatcher.Invoke(() => card.SetMesState("Invalid", isError: true));
            }

            if (mes != null && !mes.HasToken)
            {
                // 网关 JwtAuthGlobalFilter 的白名单只有 /api/auth/login、
                // /api/auth/register、/actuator/**，quality 路径需要 Bearer token。
                // 不提示的话，用户会看到本地计数正常但上报全部 401。
                AppLogger.Instance.Warn(
                    "MES 未提供令牌：网关对 quality 路径强制鉴权，上报将全部返回 401");
                Dispatcher.Invoke(() => card.SetMesState("No token", isError: true));
            }
        }

        _productionLine = new Industrial.ProductionLineService(stability, mes);

        _productionLine.OnPieceSettled += piece => Dispatcher.Invoke(() =>
        {
            IndustrialPanelCtrl.LineCard.UpdateCounters(
                _productionLine?.PassedCount ?? 0,
                _productionLine?.FailedCount ?? 0,
                _productionLine?.YieldRate ?? 0);

            string tag = piece.Passed ? "PASS" : "FAIL";
            IndustrialPanelCtrl.LineCard.ShowEvent(
                $"{DateTime.Now:HH:mm:ss}  {tag}  {piece.WorkpieceId}  ({piece.StableFrames} frames)");
        });

        _productionLine.OnMesReportFailed += (id, result) => Dispatcher.Invoke(() =>
        {
            IndustrialPanelCtrl.LineCard.SetMesState($"{result.StatusCode}", isError: true);
            AppLogger.Instance.Warn(
                $"MES 上报失败 {id}: state={result.State} status={result.StatusCode} {result.ResponseBody}");
        });

        Dispatcher.Invoke(() =>
        {
            IndustrialPanelCtrl.LineCard.UpdateCounters(0, 0, 0);
            IndustrialPanelCtrl.LineCard.SetMesState(mes != null ? "Ready" : "Local only");
        });

        AppLogger.Instance.Info(
            $"产线编排已启用：稳定帧={card.StableFrames} ROI={(card.UseRoi ? "开" : "关")} " +
            $"MES={(mes != null ? url : "未配置")}");
    }

    /// <summary>
    /// 把本帧的颜色检出喂给产线编排层：过滤 ROI 外的目标，交给稳定判定器，
    /// 并更新界面计数。
    /// </summary>
    /// <remarks>
    /// <b>ROI 的必要性</b>：不做 ROI 时，背景墙上与工件同色的诱饵色块、地面的
    /// 网格线都会满足 HSV 阈值并被当成工件。端到端实测 150 帧虚报 76 件，
    /// 而场景里只有 4 件在循环；加上 ROI 后降到 18 件。
    /// </remarks>
    private void FeedProductionLine(Mat frame, int rawCount)
    {
        if (_productionLine == null) return;

        var card = IndustrialPanelCtrl.LineCard;
        var dets = _lineDetectionBuffer;
        dets.Clear();

        OpenCvSharp.Rect? roi = null;
        if (card.UseRoi)
        {
            var r = InspectionRoiRect;
            // ROI 必须夹在画面内，否则用户改了参数会直接抛异常中断采集循环
            var clamped = OpenCvSharp.Rect.Intersect(r,
                new OpenCvSharp.Rect(0, 0, frame.Width, frame.Height));
            roi = clamped.Width > 0 && clamped.Height > 0 ? clamped : null;
        }

        foreach (var d in _detectionBuffer)
        {
            if (roi.HasValue)
            {
                // 用包围盒与 ROI 的相交面积占比判定是否"在区内"，
                // 而不是只看中心点：工件压着边界时中心会跑出区外。
                var inter = OpenCvSharp.Rect.Intersect(d.Bounds, roi.Value);
                if (inter.Width <= 0 || inter.Height <= 0) continue;
                double frac = (double)(inter.Width * inter.Height) /
                              Math.Max(1, d.Bounds.Width * d.Bounds.Height);
                if (frac < 0.6) continue;
            }

            dets.Add(new Industrial.ProductionLineService.Detection(
                d.CenterX, d.CenterY, _colorNameForTracking, d.Area / 4000.0));
        }

        _productionLine.ProcessFrame(dets, DateTime.Now);
        _productionLine.ExpireIdle(DateTime.Now);
    }

    private void CameraPanelCtrl_ConnectRequested(object? sender, EventArgs e)
        {
            int srcIndex = CameraPanelCtrl.SourceTypeComboBoxEl.SelectedIndex;

            if (srcIndex == 1)
            {
                BuildNetworkUrl();
                if (!_networkConfigured)
                {
                    ShowError(TranslationService.GetStringStatic("CameraOpenError"));
                    return;
                }
            }
            else if (srcIndex == 2)
            {
                ApplyReplaySettings();
                if (string.IsNullOrWhiteSpace(_videoCaptureComponent.ReplayPath))
                {
                    ShowError(TranslationService.GetStringStatic("CameraOpenError") +
                              ": replay file not selected");
                    return;
                }
            }

            HideError();
            _videoCaptureComponent.StopCapture();
            bool success = _videoCaptureComponent.StartCapture();
            if (success)
            {
                CameraPanelCtrl.ConnectButtonEl.IsEnabled = false;
                CameraPanelCtrl.DisconnectButtonEl.IsEnabled = true;
                AppLogger.Instance.Info("视频采集已启动");
            }
            else
            {
                CameraPanelCtrl.ConnectButtonEl.IsEnabled = true;
                CameraPanelCtrl.DisconnectButtonEl.IsEnabled = false;
                AppLogger.Instance.Error("视频采集启动失败");
            }
        }

        private void CameraPanelCtrl_DisconnectRequested(object? sender, EventArgs e)
        {
            _videoCaptureComponent.StopCapture();
            AppLogger.Instance.Info("视频采集已断开");
        }

        private void CameraPanelCtrl_StartCameraRequested(object? sender, EventArgs e)
        {
            HideError();
            bool success = _videoCaptureComponent.StartCapture();
            if (success)
            {
                CameraPanelCtrl.StartCameraButtonEl.IsEnabled = false;
                CameraPanelCtrl.StopCameraButtonEl.IsEnabled = true;
                AppLogger.Instance.Info("摄像头已启动");
            }
            else
            {
                CameraPanelCtrl.StartCameraButtonEl.IsEnabled = true;
                CameraPanelCtrl.StopCameraButtonEl.IsEnabled = false;
                AppLogger.Instance.Error("摄像头启动失败");
            }
        }

        private void CameraPanelCtrl_StopCameraRequested(object? sender, EventArgs e)
        {
            _videoCaptureComponent.StopCapture();
            AppLogger.Instance.Info("摄像头已关闭");
        }

        private void CameraPanelCtrl_ScreenshotRequested(object? sender, EventArgs e)
        {
            try
            {
                string dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "VirtualPathVision");
                System.IO.Directory.CreateDirectory(dir);
                string path = System.IO.Path.Combine(dir,
                    $"Screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png");

                var source = CameraPanelCtrl.OriginalImageEl.Source as System.Windows.Media.Imaging.BitmapSource;
                if (source == null)
                {
                    ShowError(TranslationService.GetStringStatic("SaveFailed"));
                    return;
                }

                using var fileStream = new System.IO.FileStream(path, System.IO.FileMode.Create);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source));
                encoder.Save(fileStream);

                AppLogger.Instance.Info($"{TranslationService.Instance.ScreenshotSaved} {path}");
            }
            catch (Exception ex)
            {
                ShowError($"{TranslationService.GetStringStatic("SaveFailed")}: {ex.Message}");
                AppLogger.Instance.Error($"截图保存失败: {ex.Message}");
            }
        }

        private void CameraPanelCtrl_RecordRequested(object? sender, EventArgs e)
        {
            if (_recordingComponent.IsRecording)
            {
                _recordingComponent.StopRecording();
                CameraPanelCtrl.RecordButtonEl.Content = TranslationService.Instance.StartRecording;
                CameraPanelCtrl.RecordButtonEl.ClearValue(Button.BackgroundProperty);
                AppLogger.Instance.Info(TranslationService.Instance.RecordingStopped);
            }
            else
            {
                string dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "VirtualPathVision");
                System.IO.Directory.CreateDirectory(dir);
                string path = System.IO.Path.Combine(dir,
                    $"Recording_{DateTime.Now:yyyyMMdd_HHmmss}.avi");

                var size = _videoCaptureComponent.GetResolution();
                int width = (int)size.Width;
                int height = (int)size.Height;
                if (width <= 0 || height <= 0)
                {
                    ShowError(TranslationService.GetStringStatic("SaveFailed"));
                    return;
                }

                // 使用信号源实际帧率。此前硬编码 15fps，而采集循环约 33fps，
                // 导致录出来的视频播放速度只有实际的一半。
                double captureFps = _videoCaptureComponent.GetFrameRate();
                if (double.IsNaN(captureFps) || double.IsInfinity(captureFps) || captureFps <= 1 || captureFps > 240)
                    captureFps = DefaultCaptureFps;

                bool started = _recordingComponent.StartRecording(path, captureFps, width, height);
                if (started)
                {
                    CameraPanelCtrl.RecordButtonEl.Content = TranslationService.Instance.StopRecording;
                    CameraPanelCtrl.RecordButtonEl.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A));
                    AppLogger.Instance.Info($"{TranslationService.Instance.RecordingStarted} {path}");
                }
                else
                {
                    ShowError(TranslationService.GetStringStatic("SaveFailed"));
                    AppLogger.Instance.Error("录像启动失败");
                }
            }
        }

        // ==================== ProcessingPanel 事件处理 ====================

        private void ProcessingPanelCtrl_ThresholdsChanged(int t1, int t2)
        {
            _threshold1 = t1;
            _threshold2 = t2;
            // 与 UpdateThresholds 共用节流日志，避免同一事件被记录两次
            LogThresholdChange(t1, t2);
        }

        private void ProcessingPanelCtrl_ModeChanged(int index)
        {
            if (_suppressSelectionEvents) return;

            _currentMode = index switch
            {
                1 => Components.ProcessingMode.Sobel,
                2 => Components.ProcessingMode.Laplacian,
                3 => Components.ProcessingMode.Binary,
                4 => Components.ProcessingMode.Contour,
                5 => Components.ProcessingMode.QRCode,
                6 => Components.ProcessingMode.ColorDetection,
                7 => Components.ProcessingMode.TemplateMatch,
                8 => Components.ProcessingMode.ShapeDetection,
                9 => Components.ProcessingMode.FeatureMatch,
                10 => Components.ProcessingMode.Enhancement,
                _ => Components.ProcessingMode.Canny
            };

            ProcessingPanelCtrl.SetModeName(GetModeName(_currentMode));

            // 形状识别内部也用 Canny 提取边缘，一并开放阈值调节
            bool showThreshold = _currentMode is Components.ProcessingMode.Canny
                or Components.ProcessingMode.Contour
                or Components.ProcessingMode.ShapeDetection;
            ProcessingPanelCtrl.ThresholdPanelEl.Visibility = showThreshold ? Visibility.Visible : Visibility.Collapsed;
            ProcessingPanelCtrl.ColorPanelEl.Visibility = _currentMode == Components.ProcessingMode.ColorDetection
                ? Visibility.Visible : Visibility.Collapsed;
            bool showTemplatePanel = _currentMode is Components.ProcessingMode.TemplateMatch or Components.ProcessingMode.FeatureMatch;
            ProcessingPanelCtrl.TemplatePanelEl.Visibility = showTemplatePanel ? Visibility.Visible : Visibility.Collapsed;

            CameraPanelCtrl.OriginalImageEl.Cursor = _currentMode == Components.ProcessingMode.ColorDetection
                ? System.Windows.Input.Cursors.Cross : System.Windows.Input.Cursors.Arrow;
            UpdatePickColorHint();

            ProcessingPanelCtrl.SetResultTitle(GetResultTitle(_currentMode));

            _lastDecodedText = null;
            ProcessingPanelCtrl.UpdateResults(0, 0, "", "");
            AppLogger.Instance.Info($"处理模式切换为: {GetModeName(_currentMode)}");
        }

        private void ProcessingPanelCtrl_ColorChanged(int index)
        {
            if (_suppressSelectionEvents) return;

            _colorDetectionComponent.Target = index switch
            {
                1 => Components.ColorDetectionComponent.TargetColor.Green,
                2 => Components.ColorDetectionComponent.TargetColor.Blue,
                3 => Components.ColorDetectionComponent.TargetColor.Yellow,
                4 => Components.ColorDetectionComponent.TargetColor.Orange,
                5 => Components.ColorDetectionComponent.TargetColor.Purple,
                6 => Components.ColorDetectionComponent.TargetColor.Cyan,
                7 => Components.ColorDetectionComponent.TargetColor.White,
                8 => Components.ColorDetectionComponent.TargetColor.Black,
                9 => Components.ColorDetectionComponent.TargetColor.Custom,
                _ => Components.ColorDetectionComponent.TargetColor.Red
            };
            UpdatePickColorHint();
            AppLogger.Instance.Info($"目标颜色切换为: {ProcessingPanelCtrl.ColorComboBoxEl.SelectedItem}");

            // 编排层用颜色名作为目标签名：签名不同必然是不同目标，
            // 跟踪时不会把红箱与绿球并成一件。
            _colorNameForTracking = _colorDetectionComponent.Target.ToString();
        }

        private void ProcessingPanelCtrl_LoadTemplateRequested(object? sender, EventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog
            {
                Filter = TranslationService.GetStringStatic("ImageFilter")
            };
            if (openFileDialog.ShowDialog() == true)
            {
                bool okTemplate = _templateMatchComponent.LoadTemplate(openFileDialog.FileName);
                bool okFeature = _featureMatchComponent.LoadTemplate(openFileDialog.FileName);

                if (okTemplate || okFeature)
                {
                    ProcessingPanelCtrl.SetTemplateStatus(
                        $"{TranslationService.Instance.TemplateLoaded} ({_templateMatchComponent.TemplateWidth}x{_templateMatchComponent.TemplateHeight})");
                    AppLogger.Instance.Info($"{TranslationService.Instance.TemplateLoaded}: {openFileDialog.FileName}");
                }
                else
                {
                    ShowError(TranslationService.GetStringStatic("TemplateLoadFailed"));
                }
            }
        }

        /// <summary>更新取色提示的显示状态（仅颜色检测 + 自定义取色时显示）</summary>
        private void UpdatePickColorHint()
        {
            ProcessingPanelCtrl.PickColorHintTextEl.Visibility =
                _currentMode == Components.ProcessingMode.ColorDetection &&
                _colorDetectionComponent.Target == Components.ColorDetectionComponent.TargetColor.Custom
                    ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// 采集线程每帧写入取色用的原始帧副本，UI 线程点击时读取。
        /// 两端会并发访问同一个原生 Mat，必须用锁交接，
        /// 否则采集线程 Dispose 时 UI 线程可能正在 CvtColor（use-after-free）。
        /// </summary>
        private readonly object _pickFrameLock = new();

        /// <summary>日志新增事件的具名委托（用于 Closed 时退订）</summary>
        private Action<LogEntry>? _logAddedHandler;

        /// <summary>更新取色用的帧副本（采集线程调用）</summary>
        private void SetLastOriginalFrame(Mat frame)
        {
            Mat? copy = null;
            try
            {
                if (frame != null && !frame.Empty())
                    copy = frame.Clone();
            }
            catch { /* 拷贝失败时保留旧帧 */ }

            lock (_pickFrameLock)
            {
                _lastOriginalFrame?.Dispose();
                _lastOriginalFrame = copy;
            }
        }

        /// <summary>取出一份独立的帧副本供 UI 线程安全使用（调用方负责 Dispose）</summary>
        private Mat? TakeLastOriginalFrameCopy()
        {
            lock (_pickFrameLock)
            {
                if (_lastOriginalFrame == null || _lastOriginalFrame.Empty())
                    return null;
                try { return _lastOriginalFrame.Clone(); }
                catch { return null; }
            }
        }

        /// <summary>释放取色帧（窗口关闭时调用）</summary>
        private void DisposeLastOriginalFrame()
        {
            lock (_pickFrameLock)
            {
                _lastOriginalFrame?.Dispose();
                _lastOriginalFrame = null;
            }
        }

        /// <summary>
        /// 点击左侧画面取色：将点击位置的像素颜色设为颜色检测的自定义目标。
        /// </summary>
        private void OriginalViewGrid_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_currentMode != Components.ProcessingMode.ColorDetection)
                return;

            // 在锁内取出一份独立副本，之后全程只操作本地副本，
            // 避免与采集线程的写入/释放竞争。
            using Mat? pickFrame = TakeLastOriginalFrameCopy();
            if (pickFrame == null)
                return;

            try
            {
                var pos = e.GetPosition(CameraPanelCtrl.OriginalImageEl);
                double srcW = pickFrame.Width;
                double srcH = pickFrame.Height;
                double elemW = CameraPanelCtrl.OriginalImageEl.ActualWidth;
                double elemH = CameraPanelCtrl.OriginalImageEl.ActualHeight;
                if (srcW <= 0 || srcH <= 0 || elemW <= 0 || elemH <= 0)
                    return;

                double scale = Math.Min(elemW / srcW, elemH / srcH);
                double offsetX = (elemW - srcW * scale) / 2;
                double offsetY = (elemH - srcH * scale) / 2;
                double px = (pos.X - offsetX) / scale;
                double py = (pos.Y - offsetY) / scale;
                if (px < 0 || py < 0 || px >= srcW || py >= srcH)
                    return;

                using Mat hsv = new Mat();
                Cv2.CvtColor(pickFrame, hsv, ColorConversionCodes.BGR2HSV);
                var pixel = hsv.At<Vec3b>((int)py, (int)px);
                _colorDetectionComponent.SetCustomRange(pixel.Item0, pixel.Item1, pixel.Item2);

                _suppressSelectionEvents = true;
                ProcessingPanelCtrl.ColorComboBoxEl.SelectedIndex = 9;
                _suppressSelectionEvents = false;
                _colorDetectionComponent.Target = Components.ColorDetectionComponent.TargetColor.Custom;
                UpdatePickColorHint();

                AppLogger.Instance.Info($"已取色: HSV({pixel.Item0}, {pixel.Item1}, {pixel.Item2})");
            }
            catch (Exception ex)
            {
                AppLogger.Instance.Error($"取色失败: {ex.Message}");
            }
        }

        // ==================== AIPanel 事件处理 ====================

        private void AIPanelCtrl_LoadModelRequested(object? sender, EventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "ONNX Model (*.onnx)|*.onnx|All Files (*.*)|*.*"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                // 按不变文化解析：AIPanel 写回时用的是不变文化，
                // 若这里用 CurrentCulture 解析，在逗号小数文化下 "0.50" 会变成 50，
                // 导致所有检测都被阈值拒绝。
                float conf = 0.5f;
                if (!float.TryParse(AIPanelCtrl.ConfThresholdTextBoxEl.Text,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out conf))
                {
                    float.TryParse(AIPanelCtrl.ConfThresholdTextBoxEl.Text, out conf);
                }
                conf = Math.Clamp(conf, 0.01f, 1.0f);
                _desiredConfidence = conf;
                _yoloModelPath = dlg.FileName;

                lock (_aiLock)
                {
                    _yoloComponent?.Dispose();
                    _yoloComponent = new AI.YoloDetectionComponent(
                        dlg.FileName, confidenceThreshold: conf);

                    // 轨迹长度上限来自 AI 面板的设置
                    int maxTrail = ReadIntSetting(AIPanelCtrl.MaxTrailTextBoxEl.Text, DefaultMaxTrail);
                    _kalmanTracker?.Dispose();
                    _kalmanTracker = new AI.KalmanTrackerComponent(maxLostFrames: maxTrail);
                    _activePerception = new AI.ActivePerceptionEngine(_yoloComponent, _kalmanTracker);
                    _digitalTwin?.Dispose();
                    _digitalTwin = new AI.DigitalTwinRenderer();
                }

                AIPanelCtrl.SetModelStatus(true, Path.GetFileName(dlg.FileName));
                AppLogger.Instance.Info($"YOLO 模型已加载: {dlg.FileName}");
            }
            catch (Exception ex)
            {
                AIPanelCtrl.SetModelStatus(false, TranslationService.Instance.StatusLoadFailed);
                ShowError($"YOLO 模型加载失败: {ex.Message}");
                AppLogger.Instance.Error($"YOLO 模型加载失败: {ex.Message}");
            }
        }

        /// <summary>目标最大丢失帧数默认值</summary>
        private const int DefaultMaxTrail = 10;

        /// <summary>读取整数型设置项，失败时回退到默认值</summary>
        private static int ReadIntSetting(string? text, int fallback)
            => int.TryParse(text, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out int v) && v > 0
                ? v : fallback;

        /// <summary>读取浮点型设置项，失败时回退到默认值</summary>
        private static float ReadFloatSetting(string? text, float fallback)
        {
            if (string.IsNullOrWhiteSpace(text)) return fallback;
            if (float.TryParse(text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float v)) return v;
            return float.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.CurrentCulture, out v) ? v : fallback;
        }

        /// <summary>按类别统计检测数量，供 AI 面板的「检测分解」区域展示</summary>
        private static System.Collections.Generic.Dictionary<string, int> BuildClassBreakdown(
            IReadOnlyList<AI.Detection>? detections)
        {
            var result = new System.Collections.Generic.Dictionary<string, int>();
            if (detections == null) return result;

            foreach (var d in detections)
            {
                string key = string.IsNullOrEmpty(d.ClassName) ? "?" : d.ClassName;
                result.TryGetValue(key, out int n);
                result[key] = n + 1;
            }
            return result;
        }

        /// <summary>
        /// 统计缺陷数量。规则统一由 <see cref="AI.DefectRules"/> 定义；
        /// 若数字孪生渲染器已创建，则沿用它可配置的关键词与置信度带，
        /// 保证画面上的红框与统计数字始终一致。
        /// </summary>
        private int CountDefects(IReadOnlyList<AI.Detection>? detections)
        {
            if (detections == null || detections.Count == 0) return 0;

            var twin = _digitalTwin;
            if (twin != null)
            {
                int n = 0;
                foreach (var d in detections)
                {
                    if (twin.IsDefect(d)) n++;
                }
                return n;
            }

            return AI.DefectRules.CountDefects(detections);
        }

        /// <summary>用户在 AI 面板设置的置信度阈值（加载模型时生效）</summary>
        private float _desiredConfidence = 0.5f;

        /// <summary>置信度实时更新：重建检测器使新阈值立即生效</summary>
        private void AIPanelCtrl_ConfidenceChanged(float value)
        {
            float conf = Math.Clamp(value, 0.01f, 1.0f);
            _desiredConfidence = conf;

            lock (_aiLock)
            {
                if (_yoloComponent == null || !_yoloComponent.IsModelLoaded) return;
                if (string.IsNullOrEmpty(_yoloModelPath)) return;

                // ConfidenceThreshold 是模型加载期参数，YoloDetectionComponent 未暴露 setter，
                // 因此这里按新阈值重建整条 AI 链路（仅在用户改动时发生，不在每帧路径上）。
                try
                {
                    int maxTrail = ReadIntSetting(AIPanelCtrl.MaxTrailTextBoxEl.Text, DefaultMaxTrail);

                    _yoloComponent.Dispose();
                    _yoloComponent = new AI.YoloDetectionComponent(_yoloModelPath, confidenceThreshold: conf);

                    _kalmanTracker?.Dispose();
                    _kalmanTracker = new AI.KalmanTrackerComponent(maxLostFrames: maxTrail);
                    _activePerception = new AI.ActivePerceptionEngine(_yoloComponent, _kalmanTracker);

                    AppLogger.Instance.Info($"置信度已更新为 {conf:F2}");
                }
                catch (Exception ex)
                {
                    AppLogger.Instance.Warn($"置信度更新失败: {ex.Message}");
                }
            }
        }

        /// <summary>当前加载的模型路径（用于按新阈值重建检测器）</summary>
        private string _yoloModelPath = "";

        private void AIPanelCtrl_EnableChanged(object? sender, EventArgs e)
        {
            if (!IsLoaded) return;
            _aiEnabled = AIPanelCtrl.AiEnableCheckBoxEl.IsChecked == true;

            if (_aiEnabled && (_yoloComponent == null || !_yoloComponent.IsModelLoaded))
            {
                ShowError(TranslationService.Instance.PromptLoadModel);
                AIPanelCtrl.AiEnableCheckBoxEl.IsChecked = false;
                _aiEnabled = false;
                return;
            }

            if (_aiEnabled)
            {
                lock (_aiLock) { _activePerception?.Reset(); }
                AIPanelCtrl.SetModelStatus(true, AIPanelCtrl.YoloModelPathTextEl.Text);
                _detectionTotalCount = 0;
                _detectionDefectCount = 0;
                _aiFramesSinceIotPublish = 0;
                _aiDetectionFrameCount = 0;
                AppLogger.Instance.Info("AI 主动感知已启用");
            }
            else
            {
                AppLogger.Instance.Info("AI 主动感知已禁用");
            }
        }

        // ==================== CloudPanel 事件处理 ====================

        private async void CloudPanelCtrl_InitRequested(object? sender, EventArgs e)
        {
            string region = CloudPanelCtrl.AwsRegionTextBoxEl.Text.Trim();
            string bucket = CloudPanelCtrl.S3BucketTextBoxEl.Text.Trim();
            string iotEndpoint = CloudPanelCtrl.IoTEndpointTextBoxEl.Text.Trim();
            string lambdaFunc = CloudPanelCtrl.LambdaFuncTextBoxEl.Text.Trim();

            if (string.IsNullOrEmpty(region))
            {
                ShowError(TranslationService.Instance.PromptRegion);
                return;
            }

            try
            {
                if (!string.IsNullOrEmpty(bucket))
                {
                    _s3Service?.Dispose();
                    _s3Service = new S3Service(region, bucket);
                    _s3Service.OnUploadSuccess += key =>
                        Dispatcher.Invoke(() => AppLogger.Instance.Info($"S3 上传成功: {key}"));
                    _s3Service.OnUploadError += (key, ex) =>
                        Dispatcher.Invoke(() => AppLogger.Instance.Error($"S3 上传失败: {key} - {ex.Message}"));
                    CloudPanelCtrl.SetS3Status(true, TranslationService.Instance.StatusConnected);
                    AppLogger.Instance.Info($"S3 服务已初始化: {bucket} ({region})");
                }

                if (!string.IsNullOrEmpty(iotEndpoint))
                {
                    // 证书路径优先取 appsettings.json 的 AWS 段，未配置则回落到 BaseDirectory/certs
                    var awsCfg = _appConfig.AWS;
                    string certPath = !string.IsNullOrWhiteSpace(awsCfg.IoTCertificatePath)
                        ? awsCfg.IoTCertificatePath
                        : Path.Combine(AppContext.BaseDirectory, "certs", "device-certificate.pem.crt");
                    string keyPath = !string.IsNullOrWhiteSpace(awsCfg.IoTPrivateKeyPath)
                        ? awsCfg.IoTPrivateKeyPath
                        : Path.Combine(AppContext.BaseDirectory, "certs", "private.pem.key");

                    // Topic 前缀：UI 输入框优先，其次配置，最后默认值
                    string topicPrefix = CloudPanelCtrl.IoTTopicTextBoxEl.Text.Trim();
                    if (string.IsNullOrEmpty(topicPrefix))
                        topicPrefix = string.IsNullOrWhiteSpace(awsCfg.IoTTopicPrefix)
                            ? "factory/vision"
                            : awsCfg.IoTTopicPrefix;

                    _iotService?.Dispose();
                    if (File.Exists(certPath) && File.Exists(keyPath))
                    {
                        _iotService = new IoTService(iotEndpoint, certPath, keyPath, topicPrefix);
                    }
                    else
                    {
                        var handler = new System.Net.Http.HttpClientHandler();
                        _iotService = new IoTService(
                            new System.Net.Http.HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) },
                            iotEndpoint, topicPrefix);
                        AppLogger.Instance.Warn("IoT: 未找到设备证书，使用无认证模式（仅限测试）");
                    }
                    _iotService.OnPublishSuccess += topic =>
                        Dispatcher.Invoke(() => AppLogger.Instance.Info($"IoT 发布成功: {topic}"));
                    _iotService.OnPublishError += (topic, ex) =>
                        Dispatcher.Invoke(() => AppLogger.Instance.Error($"IoT 发布失败: {topic} - {ex.Message}"));
                    CloudPanelCtrl.SetIoTStatus(true, TranslationService.Instance.StatusConnected);
                    AppLogger.Instance.Info($"IoT 服务已初始化: {iotEndpoint}");
                }

                if (!string.IsNullOrEmpty(lambdaFunc))
                {
                    _lambdaClient?.Dispose();
                    _lambdaClient = new LambdaClient(region);
                    _lambdaClient.OnInvocationSuccess += fn =>
                        Dispatcher.Invoke(() => AppLogger.Instance.Info($"Lambda 调用成功: {fn}"));
                    _lambdaClient.OnInvocationError += (fn, ex) =>
                        Dispatcher.Invoke(() => AppLogger.Instance.Error($"Lambda 调用失败: {fn} - {ex.Message}"));
                    CloudPanelCtrl.SetLambdaStatus(true, TranslationService.Instance.StatusReady);
                    AppLogger.Instance.Info($"Lambda 客户端已初始化: {lambdaFunc}");
                }

                CloudPanelCtrl.SetCloudOverallStatus(TranslationService.Instance.StatusConnected);
                HideError();
            }
            catch (Exception ex)
            {
                CloudPanelCtrl.SetCloudOverallStatus(TranslationService.Instance.StatusInitFailed);
                ShowError($"Cloud 初始化失败: {ex.Message}");
                AppLogger.Instance.Error($"Cloud 初始化失败: {ex.Message}");
            }

            await Task.CompletedTask;
        }

        private async void CloudPanelCtrl_UploadScreenshotRequested(object? sender, EventArgs e)
        {
            if (_s3Service == null)
            {
                ShowError(TranslationService.Instance.PromptInitS3);
                return;
            }

            try
            {
                var source = CameraPanelCtrl.OriginalImageEl.Source as System.Windows.Media.Imaging.BitmapSource;
                if (source == null)
                {
                    ShowError(TranslationService.Instance.PromptNoScreenshot);
                    return;
                }

                int w = source.PixelWidth;
                int h = source.PixelHeight;
                int stride = w * 4;
                byte[] pixels = new byte[stride * h];
                source.CopyPixels(pixels, stride, 0);

                using var mat = new Mat(h, w, MatType.CV_8UC4);
                System.Runtime.InteropServices.Marshal.Copy(pixels, 0, mat.Data, pixels.Length);
                using var bgr = new Mat();
                Cv2.CvtColor(mat, bgr, ColorConversionCodes.BGRA2BGR);

                string key = await _s3Service.UploadDetectionScreenshotAsync(bgr, _currentMode.ToString());
                CloudPanelCtrl.AddUploadRecord(key);
                AppLogger.Instance.Info($"截图已上传 S3: {key}");
            }
            catch (Exception ex)
            {
                ShowError($"S3 上传失败: {ex.Message}");
                AppLogger.Instance.Error($"S3 上传失败: {ex.Message}");
            }
        }

        private async void CloudPanelCtrl_SendAlertRequested(object? sender, EventArgs e)
        {
            if (_iotService == null)
            {
                ShowError(TranslationService.Instance.PromptInitIoT);
                return;
            }

            try
            {
                await _iotService.PublishAlertAsync(
                    "manual_alert", 3,
                    $"手动告警 - 站点: {Environment.MachineName}, 模式: {_currentMode}");
                AppLogger.Instance.Info("告警已发送到 IoT Core");
            }
            catch (Exception ex)
            {
                ShowError($"IoT 告警发送失败: {ex.Message}");
                AppLogger.Instance.Error($"IoT 告警发送失败: {ex.Message}");
            }
        }

        private async void CloudPanelCtrl_PublishStatsRequested(object? sender, EventArgs e)
        {
            if (_iotService == null)
            {
                ShowError(TranslationService.Instance.PromptInitIoT);
                return;
            }

            try
            {
                int total = _workReportService.TodayCount;
                double passRate = total > 0 ? (double)(total - _detectionDefectCount) / total : 1.0;
                await _iotService.PublishProductionStatsAsync(total, _detectionDefectCount, passRate);
                AppLogger.Instance.Info($"生产统计已发布: 总数={total}, 缺陷={_detectionDefectCount}, 合格率={passRate:P1}");
            }
            catch (Exception ex)
            {
                ShowError($"IoT 统计发布失败: {ex.Message}");
                AppLogger.Instance.Error($"IoT 统计发布失败: {ex.Message}");
            }
        }

        private async void CloudPanelCtrl_LambdaInvokeRequested(object? sender, EventArgs e)
        {
            if (_lambdaClient == null)
            {
                ShowError(TranslationService.Instance.PromptInitLambda);
                return;
            }

            try
            {
                string funcName = CloudPanelCtrl.LambdaFuncTextBoxEl.Text.Trim();
                var payload = new { mode = _currentMode.ToString(), detections = _detectionTotalCount };
                await _lambdaClient.InvokeAsync(funcName, payload);
                CloudPanelCtrl.LambdaResultTextEl.Text = "Invoked successfully";
                AppLogger.Instance.Info($"Lambda 调用成功: {funcName}");
            }
            catch (Exception ex)
            {
                ShowError($"Lambda 调用失败: {ex.Message}");
                AppLogger.Instance.Error($"Lambda 调用失败: {ex.Message}");
            }
        }

        // ==================== IndustrialPanel 事件处理 ====================

        private void IndustrialPanelCtrl_ScanSourceChanged(int index)
        {
            IndustrialPanelCtrl.SerialScanPanelEl.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
            IndustrialPanelCtrl.TcpScanPanelEl.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void IndustrialPanelCtrl_ModbusConnectRequested(object? sender, EventArgs e)
        {
            if (!byte.TryParse(IndustrialPanelCtrl.ModbusUnitTextBoxEl.Text, out byte unitId) ||
                !int.TryParse(IndustrialPanelCtrl.ModbusPortTextBoxEl.Text, out int port) ||
                string.IsNullOrWhiteSpace(IndustrialPanelCtrl.ModbusIpTextBoxEl.Text))
            {
                ShowError(TranslationService.GetStringStatic("InvalidThreshold"));
                return;
            }

            HideError();
            _modbusDriver.UpdateSettings(IndustrialPanelCtrl.ModbusIpTextBoxEl.Text.Trim(), port, unitId);
            IndustrialPanelCtrl.ModbusConnectButtonEl.IsEnabled = false;
            bool ok = await _modbusDriver.ConnectAsync();
            if (ok)
            {
                AppLogger.Instance.Info($"Modbus 已连接: {_modbusDriver.Ip}:{_modbusDriver.Port} (从站 {_modbusDriver.UnitId})");
            }
        }

        private void IndustrialPanelCtrl_ModbusDisconnectRequested(object? sender, EventArgs e)
        {
            _modbusDriver.Disconnect();
            AppLogger.Instance.Info("Modbus 已断开");
        }

        private void IndustrialPanelCtrl_ModbusReadRequested(object? sender, EventArgs e)
        {
            try
            {
                if (!ushort.TryParse(IndustrialPanelCtrl.ModbusAddrTextBoxEl.Text, out ushort address))
                    return;
                ushort[] values = _modbusDriver.ReadHoldingRegisters(address, 1);
                IndustrialPanelCtrl.UpdateModbusResult($"= {values[0]}");
                AppLogger.Instance.Info($"Modbus 读取: [{address}] = {values[0]}");
            }
            catch (Exception ex)
            {
                IndustrialPanelCtrl.UpdateModbusResult("");
                AppLogger.Instance.Error($"Modbus 读取失败: {ex.Message}");
            }
        }

        private void IndustrialPanelCtrl_ModbusWriteRequested(object? sender, EventArgs e)
        {
            try
            {
                if (!ushort.TryParse(IndustrialPanelCtrl.ModbusAddrTextBoxEl.Text, out ushort address) ||
                    !ushort.TryParse(IndustrialPanelCtrl.ModbusValueTextBoxEl.Text, out ushort value))
                    return;
                _modbusDriver.WriteSingleRegister(address, value);
                IndustrialPanelCtrl.UpdateModbusResult("OK");
                AppLogger.Instance.Info($"Modbus 写入: [{address}] = {value}");
            }
            catch (Exception ex)
            {
                IndustrialPanelCtrl.UpdateModbusResult("");
                AppLogger.Instance.Error($"Modbus 写入失败: {ex.Message}");
            }
        }

        private async void IndustrialPanelCtrl_OpcUaConnectRequested(object? sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(IndustrialPanelCtrl.OpcUaEndpointTextBoxEl.Text)) return;

            HideError();
            _opcUaDriver.UpdateSettings(IndustrialPanelCtrl.OpcUaEndpointTextBoxEl.Text.Trim());
            IndustrialPanelCtrl.OpcUaConnectButtonEl.IsEnabled = false;
            bool ok = await _opcUaDriver.ConnectAsync();
            if (ok)
            {
                AppLogger.Instance.Info($"OPC-UA 已连接: {_opcUaDriver.EndpointUrl}");
            }
        }

        private void IndustrialPanelCtrl_OpcUaDisconnectRequested(object? sender, EventArgs e)
        {
            _opcUaDriver.Disconnect();
            AppLogger.Instance.Info("OPC-UA 已断开");
        }

        private async void IndustrialPanelCtrl_OpcUaReadRequested(object? sender, EventArgs e)
        {
            try
            {
                string? result = await _opcUaDriver.ReadNodeAsync(IndustrialPanelCtrl.OpcUaNodeTextBoxEl.Text.Trim());
                IndustrialPanelCtrl.UpdateOpcUaResult($"{IndustrialPanelCtrl.OpcUaNodeTextBoxEl.Text.Trim()} = {result}");
                AppLogger.Instance.Info($"OPC-UA 读取: {IndustrialPanelCtrl.OpcUaNodeTextBoxEl.Text.Trim()} = {result}");
            }
            catch (Exception ex)
            {
                IndustrialPanelCtrl.UpdateOpcUaResult("");
                AppLogger.Instance.Error($"OPC-UA 读取失败: {ex.Message}");
            }
        }

        private async void IndustrialPanelCtrl_OpcUaWriteRequested(object? sender, EventArgs e)
        {
            try
            {
                object value = double.TryParse(IndustrialPanelCtrl.OpcUaValueTextBoxEl.Text, out double number)
                    ? number : IndustrialPanelCtrl.OpcUaValueTextBoxEl.Text;
                await _opcUaDriver.WriteNodeAsync(IndustrialPanelCtrl.OpcUaNodeTextBoxEl.Text.Trim(), value);
                IndustrialPanelCtrl.UpdateOpcUaResult("OK");
                AppLogger.Instance.Info($"OPC-UA 写入: {IndustrialPanelCtrl.OpcUaNodeTextBoxEl.Text.Trim()} = {value}");
            }
            catch (Exception ex)
            {
                IndustrialPanelCtrl.UpdateOpcUaResult("");
                AppLogger.Instance.Error($"OPC-UA 写入失败: {ex.Message}");
            }
        }

        private async void IndustrialPanelCtrl_SerialToggleRequested(object? sender, EventArgs e)
        {
            if (_serialDriver.IsRunning)
            {
                _serialDriver.Stop();
                IndustrialPanelCtrl.SerialToggleButtonEl.Content = TranslationService.Instance.Start;
                AppLogger.Instance.Info("串口扫码已停止");
                return;
            }

            string? portName = IndustrialPanelCtrl.SerialPortComboBoxEl.SelectedItem as string;
            if (string.IsNullOrEmpty(portName)) return;
            _serialDriver.PortName = portName;
            if (int.TryParse(IndustrialPanelCtrl.SerialBaudComboBoxEl.SelectedItem as string, out int baud))
                _serialDriver.BaudRate = baud;

            bool ok = await _serialDriver.StartAsync();
            if (ok)
            {
                IndustrialPanelCtrl.SerialToggleButtonEl.Content = TranslationService.Instance.Stop;
                AppLogger.Instance.Info($"串口扫码已启动: {portName} @ {_serialDriver.BaudRate}");
            }
        }

        private async void IndustrialPanelCtrl_TcpToggleRequested(object? sender, EventArgs e)
        {
            if (_tcpDriver.IsRunning)
            {
                _tcpDriver.Stop();
                IndustrialPanelCtrl.TcpToggleButtonEl.Content = TranslationService.Instance.Start;
                AppLogger.Instance.Info("TCP 扫码已停止");
                return;
            }

            if (!int.TryParse(IndustrialPanelCtrl.TcpScanPortTextBoxEl.Text, out int port) || port <= 0 || port > 65535)
                return;
            _tcpDriver.Port = port;

            bool ok = await _tcpDriver.StartAsync();
            if (ok)
            {
                IndustrialPanelCtrl.TcpToggleButtonEl.Content = TranslationService.Instance.Stop;
                AppLogger.Instance.Info($"TCP 扫码已启动: 端口 {port}");
            }
        }

        private void IndustrialPanelCtrl_ExportCsvRequested(object? sender, EventArgs e)
        {
            try
            {
                string path = _workReportService.ExportCsv();
                AppLogger.Instance.Info($"{TranslationService.GetStringStatic("CsvExported")}: {path}");
                MessageBox.Show($"{TranslationService.GetStringStatic("CsvExported")}:\n{path}",
                    TranslationService.Instance.AppTitle, MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                AppLogger.Instance.Error($"CSV 导出失败: {ex.Message}");
            }
        }

        private void IndustrialPanelCtrl_ClearReportRequested(object? sender, EventArgs e)
        {
            _workReportService.ClearToday();
            IndustrialPanelCtrl.UpdateReportStats(0, "--");
            AppLogger.Instance.Info("今日报工记录已清空");
        }

        /// <summary>
        /// 报工登记：条码 → 报工记录（SQLite 持久化 + 今日产量 + PLC 联动）。
        /// 必须在 UI 线程调用。
        /// </summary>
        private void AddReport(string barcode, string source)
        {
            if (string.IsNullOrWhiteSpace(barcode)) return;

            var record = _workReportService.AddRecord(
                IndustrialPanelCtrl.WorkOrderTextBoxEl.Text.Trim(), barcode, source);
            IndustrialPanelCtrl.UpdateReportStats(_workReportService.TodayCount, barcode);
            IndustrialPanelCtrl.ReportListBoxEl.ScrollIntoView(record);
            AppLogger.Instance.Info($"{TranslationService.Instance.WorkReport}: {barcode} ({source})");

            // PLC 联动：将今日产量写入 Modbus 保持寄存器
            if (IndustrialPanelCtrl.ModbusLinkCheckBoxEl.IsChecked == true)
            {
                try
                {
                    if (_modbusDriver.State != DeviceDriverState.Connected)
                    {
                        AppLogger.Instance.Warn(TranslationService.GetStringStatic("ModbusNotConnected"));
                        return;
                    }
                    if (ushort.TryParse(IndustrialPanelCtrl.PlcRegisterTextBoxEl.Text, out ushort register))
                    {
                        // TodayCount 超过 ushort 上限时饱和到 65535，而不是回绕成 0
                        ushort plcCount = Industrial.WorkReportService.ClampToUshort(_workReportService.TodayCount);
                        _modbusDriver.WriteSingleRegister(register, plcCount);
                        AppLogger.Instance.Info($"PLC联动: [{register}] = {plcCount}");
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Instance.Error($"PLC联动失败: {ex.Message}");
                }
            }
        }

        // ==================== Modbus / OPC-UA 状态更新 ====================

        /// <summary>更新 Modbus 状态指示</summary>
        private void UpdateModbusState(DeviceDriverState state)
        {
            string statusText = state switch
            {
                DeviceDriverState.Connected => TranslationService.GetStringStatic("StatusConnected"),
                DeviceDriverState.Connecting => TranslationService.GetStringStatic("StatusConnecting"),
                DeviceDriverState.Failed => TranslationService.GetStringStatic("StatusFailed"),
                _ => TranslationService.GetStringStatic("StatusDisconnected")
            };
            bool connected = state == DeviceDriverState.Connected;
            IndustrialPanelCtrl.SetModbusState(connected, statusText);
        }

        /// <summary>更新 OPC-UA 状态指示</summary>
        private void UpdateOpcUaState(DeviceDriverState state)
        {
            string statusText = state switch
            {
                DeviceDriverState.Connected => TranslationService.GetStringStatic("StatusConnected"),
                DeviceDriverState.Connecting => TranslationService.GetStringStatic("StatusConnecting"),
                DeviceDriverState.Failed => TranslationService.GetStringStatic("StatusFailed"),
                _ => TranslationService.GetStringStatic("StatusDisconnected")
            };
            bool connected = state == DeviceDriverState.Connected;
            IndustrialPanelCtrl.SetOpcUaState(connected, statusText);
        }

        // ==================== LogPanel 事件处理 ====================

        private void LogPanelCtrl_ClearRequested(object? sender, EventArgs e)
        {
            AppLogger.Instance.Clear();
        }

        // ==================== 连接状态 & 帧处理 ====================

        /// <summary>
        /// 连接状态变更处理：更新指示灯颜色、按钮启用状态、空状态遮罩。
        /// </summary>
        private void OnConnectionStateChangedHandler(Components.ConnectionState state)
        {
            Dispatcher.Invoke(() =>
            {
                _lastConnState = state;
                string statusText = TranslationService.Instance.GetConnectionStatusText(state);
                bool connected = state == Components.ConnectionState.Connected;

                CameraPanelCtrl.SetConnected(state);

                ConnectionIndicator.Fill = connected
                    ? new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF))
                    : new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93));
                StatusIndicator.Fill = connected
                    ? new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF))
                    : new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93));
                StatusText.Text = statusText;
                StatusTextFooter.Text = statusText;

                if (connected)
                {
                    CameraPanelCtrl.EmptyOverlayLeftEl.Visibility = Visibility.Collapsed;
                    CameraPanelCtrl.EmptyOverlayRightEl.Visibility = Visibility.Collapsed;
                    StatusTextFooter.Foreground = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF));
                    AppLogger.Instance.Info("设备已连接");
                }
                else if (state == Components.ConnectionState.Disconnected)
                {
                    CameraPanelCtrl.EmptyOverlayLeftEl.Visibility = Visibility.Visible;
                    CameraPanelCtrl.EmptyOverlayRightEl.Visibility = Visibility.Visible;
                    StatusTextFooter.Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93));
                }
                else if (state == Components.ConnectionState.Failed)
                {
                    AppLogger.Instance.Error("设备连接失败");
                }
                else if (state == Components.ConnectionState.Connecting)
                {
                    AppLogger.Instance.Info("正在连接设备...");
                }
            });
        }

        /// <summary>
        /// 按当前模式处理单帧图像，返回处理结果图像。
        /// </summary>
        private Mat ProcessByMode(Mat originalFrame, Mat grayFrame, out int count, out string modeResult)
        {
            count = 0;
            modeResult = "";

            switch (_currentMode)
            {
                case Components.ProcessingMode.QRCode:
                    Mat qrDisplay = originalFrame.Clone();
                    modeResult = _barcodeDetectionComponent.Detect(originalFrame, qrDisplay) ?? "";
                    return qrDisplay;

                case Components.ProcessingMode.ColorDetection:
                    // 产线编排启用时收集每个目标的位置，交给编排层跟踪同一件工件；
                    // 未启用时传 null，保持原有的零额外开销。
                    if (_productionLine != null && IndustrialPanelCtrl.LineCard.IsLineEnabled)
                    {
                        _detectionBuffer.Clear();
                        Mat colorResult = _colorDetectionComponent.Detect(
                            originalFrame, _detectionBuffer, out count);
                        FeedProductionLine(originalFrame, count);
                        return colorResult;
                    }

                    Mat colorOnly = _colorDetectionComponent.Detect(originalFrame, out count);
                    return colorOnly;

                case Components.ProcessingMode.TemplateMatch:
                    // 匹配阈值此前没有任何写入方（恒为 0.6），AI 面板里的
                    // "Match Threshold" 输入框完全无效。这里接入。
                    _templateMatchComponent.Threshold =
                        Math.Clamp(ReadFloatSetting(AIPanelCtrl.MatchThresholdTextBoxEl.Text, 0.6f), 0.01, 1.0);
                    Mat tmResult = _templateMatchComponent.Match(grayFrame, out double score);
                    modeResult = _templateMatchComponent.HasTemplate
                        ? (double.IsNaN(score) ? "" : $"{score:P1}")
                        : "";
                    return tmResult;

                case Components.ProcessingMode.ShapeDetection:
                    // 同步主界面的 Canny 阈值到形状识别组件
                    _shapeDetectionComponent.CannyLow = _threshold1;
                    _shapeDetectionComponent.CannyHigh = _threshold2;
                    Mat shapeResult = _shapeDetectionComponent.Detect(grayFrame, out count, out string shapeSummary);
                    modeResult = shapeSummary;
                    return shapeResult;

                case Components.ProcessingMode.FeatureMatch:
                    Mat featureResult = _featureMatchComponent.Match(grayFrame, out int matches);
                    modeResult = _featureMatchComponent.HasTemplate
                        ? $"{TranslationService.Instance.FeatureMatches}: {matches}"
                        : TranslationService.Instance.NoTemplate;
                    return featureResult;

                case Components.ProcessingMode.Enhancement:
                    return _enhancementComponent.Enhance(grayFrame);

                default:
                    return _imageProcessingComponent.Process(
                        grayFrame, _currentMode, _threshold1, _threshold2, out count);
            }
        }

        /// <summary>
        /// 每帧处理：执行图像处理（多模式）+ 人脸检测 + 更新显示 + FPS统计。
        /// 该方法在后台（采集）线程调用，UI 更新通过 <see cref="InvokeUi"/> 非阻塞封送。
        /// </summary>
        private void ProcessFrame(Mat originalFrame, Mat grayFrame)
        {
            // 关闭已经开始：立刻返回。
            //
            // 采集循环退出后不会再有帧，但当前这一帧可能已经在路上了。
            // 此时继续跑会走到 InvokeUi，而 InvokeUi 虽然不阻塞，
            // 仍会投递一批注定没人看的 UI 更新。更重要的是：
            // 若不检查，采集线程可能正好在这一帧里等着 UI 线程，
            // 而 UI 线程正阻塞在 StopCapture 的 finished.Wait 上——互等。
            if (_shuttingDown || _videoCaptureComponent.IsStopping)
                return;

            try
            {
                _frameStopwatch.Restart();

                if (_currentMode == Components.ProcessingMode.ColorDetection)
                {
                    SetLastOriginalFrame(originalFrame);
                }

                Mat resultImage = ProcessByMode(originalFrame, grayFrame, out int contourCount, out string modeResult);
                int faceCount = 0;
                try
                {
                    faceCount = _faceDetectionComponent.DetectFaces(originalFrame);

                    // ---- AI 主动感知 ----
                    int aiDetCount = 0;
                    int aiTrackCount = 0;
                    IReadOnlyList<AI.Detection>? aiDetections = null;
                    IReadOnlyList<AI.TrackedObject>? aiTracks = null;
                    if (_aiEnabled && _activePerception != null && _yoloComponent != null && _yoloComponent.IsModelLoaded)
                    {
                        lock (_aiLock)
                        {
                            _activePerception.ProcessFrame(originalFrame);
                            _activePerception.DrawOverlay(resultImage);
                            var detections = _activePerception.CurrentDetections;
                            aiDetCount = detections.Count;
                            var tracks = _activePerception.CurrentTracks;
                            aiTrackCount = tracks.Count;
                            aiDetections = detections;
                            aiTracks = tracks;
                            _aiDetectionFrameCount++;

                            _detectionTotalCount += aiDetCount;
                            _detectionDefectCount += CountDefects(detections);

                            // 按「帧」而非「累计目标数」节流上报。
                            // 旧实现判断 _detectionTotalCount % 30 == 0，而该值在
                            // 每帧累加 aiDetCount（通常 >1），会直接跨过 30 的整数倍，
                            // 导致遥测静默停止。
                            if (_iotService != null && ++_aiFramesSinceIotPublish >= IotPublishIntervalFrames)
                            {
                                _aiFramesSinceIotPublish = 0;
                                var iot = _iotService;
                                string modeName = _currentMode.ToString();
                                int detCount = aiDetCount;
                                _ = Task.Run(async () =>
                                {
                                    try
                                    {
                                        await iot.PublishDetectionResultAsync(modeName, detCount, 0.8f);
                                    }
                                    catch (Exception ex)
                                    {
                                        AppLogger.Instance.Warn($"IoT 发布失败: {ex.Message}");
                                    }
                                });
                            }
                        }
                    }

                    _imageDisplayComponent.UpdateImages(originalFrame, resultImage, _threshold1, _threshold2);

                    if (_recordingComponent.IsRecording)
                    {
                        _recordingComponent.WriteFrame(originalFrame);
                    }

                    // Dispatcher 更新 AI 面板
                    if (_aiEnabled)
                    {
                        var perception = _activePerception;
                        var detectionsSnapshot = aiDetections;
                        var tracksSnapshot = aiTracks;
                        InvokeUi(() =>
                        {
                            AIPanelCtrl.UpdateDetectionStats(aiDetCount, aiTrackCount,
                                perception?.AdaptiveInterval ?? 0);

                            // 主动感知统计与按类别分解此前从未被调用，
                            // 面板上的这几项永远是初始占位值。
                            if (perception != null && tracksSnapshot != null)
                            {
                                int lost = tracksSnapshot.Count(t => t.IsLost);
                                AIPanelCtrl.UpdatePerceptionStats(
                                    frameCount: (int)Math.Min(_aiDetectionFrameCount, int.MaxValue),
                                    activeTracks: tracksSnapshot.Count(t => !t.IsLost),
                                    lostTracks: lost,
                                    interval: perception.AdaptiveInterval,
                                    roiCount: perception.ActiveRoiCount);

                                AIPanelCtrl.UpdateDetectionBreakdown(
                                    BuildClassBreakdown(detectionsSnapshot));
                            }
                        });
                    }
                }
                finally
                {
                    resultImage.Dispose();
                }

                _frameStopwatch.Stop();
                long processTimeMs = _frameStopwatch.ElapsedMilliseconds;

                _frameCount++;
                var now = DateTime.Now;
                if ((now - _lastFpsUpdate).TotalSeconds >= 1.0)
                {
                    _currentFps = _frameCount / (now - _lastFpsUpdate).TotalSeconds;
                    _frameCount = 0;
                    _lastFpsUpdate = now;
                }

                InvokeUi(() =>
                {
                    bool showThreshold = _currentMode is Components.ProcessingMode.Canny or Components.ProcessingMode.Contour;
                    string thresholdInfo = showThreshold ? $"{_threshold1} ~ {_threshold2}" : "";
                    ProcessingPanelCtrl.UpdateResults(faceCount, contourCount, thresholdInfo, modeResult);

                    FpsTextBlock.Text = $"{_currentFps:F1} FPS";
                    ProcessTimeTextBlock.Text = $"{processTimeMs} ms";

                    // 回放进度只在回放源下才有意义，避免在实时采集时做无谓的字符串拼接
                    if (_videoCaptureComponent.SourceType == Components.VideoSourceType.FileReplay)
                    {
                        CameraPanelCtrl.UpdateReplayProgress(
                            _videoCaptureComponent.ReplayPosition,
                            _videoCaptureComponent.ReplayTotalFrames);
                    }

                    // QR/条码识别到新内容时记录日志（去重）+ 摄像头扫码报工
                    if (_currentMode == Components.ProcessingMode.QRCode &&
                        !string.IsNullOrEmpty(modeResult) && modeResult != _lastDecodedText)
                    {
                        _lastDecodedText = modeResult;
                        AppLogger.Instance.Info($"{TranslationService.Instance.QRDecoded}: {modeResult}");
                        if (IndustrialPanelCtrl.CameraReportCheckBoxEl.IsChecked == true)
                            AddReport(modeResult, TranslationService.Instance.ScanCamera);
                    }

                    UpdateCameraData();
                });

                // 数字孪生更新
                if (_aiEnabled && _digitalTwin != null)
                {
                    InvokeUi(UpdateDigitalTwin);
                }
            }
            catch (Exception ex)
            {
                InvokeUi(() =>
                {
                    ShowError(TranslationService.GetStringStatic("FrameProcessError") + $": {ex.Message}");
                    AppLogger.Instance.Error($"帧处理异常: {ex.Message}");
                });
            }
        }

        /// <summary>录制状态变化：统一刷新录像按钮的文案与配色</summary>
        private void OnRecordingStateChanged(bool recording)
        {
            Dispatcher.Invoke(() =>
            {
                CameraPanelCtrl.RecordButtonEl.Content = recording
                    ? TranslationService.Instance.StopRecording
                    : TranslationService.Instance.StartRecording;

                if (recording)
                    CameraPanelCtrl.RecordButtonEl.Background =
                        new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A));
                else
                    CameraPanelCtrl.RecordButtonEl.ClearValue(Button.BackgroundProperty);
            });
        }

        /// <summary>阈值更新回调（供 ThresholdParameterComponent 使用）</summary>
        private void UpdateThresholds(int threshold1, int threshold2)
        {
            _threshold1 = threshold1;
            _threshold2 = threshold2;
            LogThresholdChange(threshold1, threshold2);
        }

        private DateTime _lastThresholdLog = DateTime.MinValue;

        /// <summary>
        /// 记录阈值变化。滑块拖动会逐像素触发（范围 0~255、TickFrequency=1），
        /// 若每次都写日志，一次拖动就会产生数百条日志，因此按时间节流。
        /// </summary>
        private void LogThresholdChange(int threshold1, int threshold2)
        {
            var now = DateTime.Now;
            if ((now - _lastThresholdLog).TotalMilliseconds < 500)
                return;
            _lastThresholdLog = now;
            AppLogger.Instance.Info($"阈值更新: {threshold1} ~ {threshold2}");
        }

        /// <summary>加载图片按钮：打开本地图片并执行处理管线</summary>
        private void CameraPanelCtrl_LoadImageRequested(object? sender, EventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog
            {
                Filter = TranslationService.GetStringStatic("ImageFilter")
            };
            if (openFileDialog.ShowDialog() == true)
            {
                try
                {
                    Mat image = Cv2.ImRead(openFileDialog.FileName);
                    if (image.Empty())
                    {
                        image.Dispose();
                        ShowError(TranslationService.GetStringStatic("FailedLoadImage"));
                        return;
                    }

                    using Mat grayImage = new Mat();
                    Cv2.CvtColor(image, grayImage, ColorConversionCodes.BGR2GRAY);
                    using Mat edges = ProcessByMode(image, grayImage, out int contourCount, out string modeResult);
                    int faceCount = _faceDetectionComponent.DetectFaces(image);
                    _imageDisplayComponent.UpdateImages(image, edges, _threshold1, _threshold2);

                    bool showThreshold = _currentMode is Components.ProcessingMode.Canny or Components.ProcessingMode.Contour;
                    string thresholdInfo = showThreshold ? $"{_threshold1} ~ {_threshold2}" : "";
                    ProcessingPanelCtrl.UpdateResults(faceCount, contourCount, thresholdInfo, modeResult);

                    CameraPanelCtrl.EmptyOverlayLeftEl.Visibility = Visibility.Collapsed;
                    CameraPanelCtrl.EmptyOverlayRightEl.Visibility = Visibility.Collapsed;
                    HideError();

                    CameraDataTextBlock.Text = System.IO.Path.GetFileName(openFileDialog.FileName);
                    CameraPanelCtrl.SourceInfoTextEl.Text = openFileDialog.FileName;

                    SetLastOriginalFrame(image);
                    image.Dispose();

                    AppLogger.Instance.Info($"已加载图片: {openFileDialog.FileName}");
                }
                catch (Exception ex)
                {
                    ShowError(TranslationService.GetStringStatic("ImageProcessError") + $": {ex.Message}");
                    AppLogger.Instance.Error($"图片处理异常: {ex.Message}");
                }
            }
        }

        /// <summary>更新摄像头信息显示（帧率、分辨率）</summary>
        private void UpdateCameraData()
        {
            double frameRate = _videoCaptureComponent.GetFrameRate();
            System.Windows.Size resolution = _videoCaptureComponent.GetResolution();
            CameraDataTextBlock.Text = TranslationService.Instance.FormatCameraData(
                frameRate, resolution.Width, resolution.Height);
            CameraPanelCtrl.SourceInfoTextEl.Text = _videoCaptureComponent.GetSourceInfo();
        }

        /// <summary>捕获停止事件处理：清空画面、重置状态</summary>
        private void OnCaptureStoppedHandler(string? reason)
        {
            // 关窗期不再碰 UI，也不再碰编排层：
            // 本处理器可能在采集线程上被调用，而 UI 线程此时正阻塞在
            // MainWindow_Closed 里。阻塞式 Dispatcher.Invoke 会把采集线程
            // 一起拖住；直接改控件则会在窗口拆除过程中触碰已释放的对象。
            if (_shuttingDown) return;

            // 采集停止后必须清空编排层的跟踪状态：否则残留的 trackKey 会让
            // 重新连接后的第一件被误判为"已判定过"而漏检。
            _productionLine?.Reset();

            // 一次性事件：用 PostUi 而不是 InvokeUi。InvokeUi 的在途去重是
            // 为每帧刷新设计的，会把"采集停止"这种状态迁移挤掉，
            // 面板就停在脏状态（图像不清、按钮仍禁用）。
            PostUi(() =>
            {
                IndustrialPanelCtrl.LineCard.ResetCounters();
                IndustrialPanelCtrl.LineCard.ShowEvent("");

                if (_recordingComponent.IsRecording)
                {
                    _recordingComponent.StopRecording();
                    CameraPanelCtrl.RecordButtonEl.Content = TranslationService.Instance.StartRecording;
                    CameraPanelCtrl.RecordButtonEl.ClearValue(Button.BackgroundProperty);
                }

                CameraPanelCtrl.OriginalImageEl.Source = null;
                CameraPanelCtrl.EdgeImageEl.Source = null;
                CameraDataTextBlock.Text = "";
                ProcessingPanelCtrl.UpdateResults(0, 0, "", "");
                FpsTextBlock.Text = "0 FPS";
                ProcessTimeTextBlock.Text = "0 ms";
                CameraPanelCtrl.EmptyOverlayLeftEl.Visibility = Visibility.Visible;
                CameraPanelCtrl.EmptyOverlayRightEl.Visibility = Visibility.Visible;
                CameraPanelCtrl.StopCameraButtonEl.IsEnabled = false;
                CameraPanelCtrl.StartCameraButtonEl.IsEnabled = true;

                if (!string.IsNullOrEmpty(reason))
                {
                    ShowError(TranslationService.GetStringStatic("CameraStopped") + $": {reason}");
                    AppLogger.Instance.Warn($"采集停止: {reason}");
                }
            });
        }

        /// <summary>捕获错误事件处理</summary>
        private void OnCaptureErrorHandler(string? error)
        {
            string msg = error ?? TranslationService.GetStringStatic("UnknownError");

            // 关窗期只写日志：ShowError 要动 UI 元素，而此时 UI 线程正卡在
            // MainWindow_Closed 里。错误信息不能因为要退出就整个丢掉。
            if (_shuttingDown)
            {
                AppLogger.Instance.Error(msg + "（关窗期，未弹提示）");
                return;
            }

            // 一次性事件：不去重，否则可能被在途的每帧刷新挤掉
            PostUi(() =>
            {
                ShowError(msg);
                AppLogger.Instance.Error(msg);
            });
        }

        private readonly DispatcherTimer _errorAutoCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };

        /// <summary>信号源未报告帧率时使用的录制帧率兜底值</summary>
        private const double DefaultCaptureFps = 30.0;

        /// <summary>最近一次连接状态（语言切换时用于重绘状态文本）</summary>
        private Components.ConnectionState _lastConnState = Components.ConnectionState.Disconnected;

        /// <summary>初始化错误提示计时器（5秒后自动关闭）</summary>
        private void InitErrorTimer()
        {
            _errorAutoCloseTimer.Tick += (_, _) =>
            {
                _errorAutoCloseTimer.Stop();
                HideError();
            };
        }

        // ==================== 窗口状态持久化 ====================

        private static readonly string WindowSettingsPath = Path.Combine(
            AppContext.BaseDirectory, "window_settings.json");

        private void SaveWindowState()
        {
            try
            {
                var state = new
                {
                    WindowLeft = Left,
                    WindowTop = Top,
                    WindowWidth = Width,
                    WindowHeight = Height,
                    IsMaximized = WindowState == WindowState.Maximized
                };
                File.WriteAllText(WindowSettingsPath, JsonSerializer.Serialize(state));
            }
            catch { }
        }

        private void RestoreWindowState()
        {
            try
            {
                if (!File.Exists(WindowSettingsPath)) return;
                string json = File.ReadAllText(WindowSettingsPath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("WindowLeft", out var left) &&
                    root.TryGetProperty("WindowTop", out var top) &&
                    root.TryGetProperty("WindowWidth", out var w) &&
                    root.TryGetProperty("WindowHeight", out var h))
                {
                    Left = left.GetDouble();
                    Top = top.GetDouble();
                    Width = w.GetDouble();
                    Height = h.GetDouble();
                }

                if (root.TryGetProperty("IsMaximized", out var maximized) && maximized.GetBoolean())
                    WindowState = WindowState.Maximized;
            }
            catch { }
        }

        // ==================== 错误提示 ====================

        /// <summary>显示错误信息（带滑入动画 + 5秒自动关闭）</summary>
        private void ShowError(string message)
        {
            ErrorBorder.Visibility = Visibility.Visible;
            ErrorMessageTextBlock.Text = message;

            // 滑入动画
            var slideIn = new DoubleAnimation(-40, 0, TimeSpan.FromMilliseconds(250))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            ErrorTranslateY.BeginAnimation(TranslateTransform.YProperty, slideIn);

            // 重置自动关闭计时器
            _errorAutoCloseTimer.Stop();
            _errorAutoCloseTimer.Start();
        }

        /// <summary>隐藏错误信息（带滑出动画）</summary>
        private void HideError()
        {
            _errorAutoCloseTimer.Stop();

            var slideOut = new DoubleAnimation(0, -40, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };
            slideOut.Completed += (_, _) =>
            {
                ErrorBorder.Visibility = Visibility.Collapsed;
                ErrorMessageTextBlock.Text = "";
            };
            ErrorTranslateY.BeginAnimation(TranslateTransform.YProperty, slideOut);
        }

        // ==================== 数字孪生 ====================

        /// <summary>数字孪生视图更新（每帧 ProcessFrame 后更新）</summary>
        private void UpdateDigitalTwin()
        {
            if (_digitalTwin == null || _activePerception == null) return;

            try
            {
                using Mat twin = _digitalTwin.Render(
                    _activePerception.CurrentTracks,
                    _activePerception.CurrentDetections);
                AIPanelCtrl.DigitalTwinImageEl.Dispatcher.Invoke(() =>
                {
                    AIPanelCtrl.DigitalTwinImageEl.Source = Components.DpiAwareBitmapSource.FromMat(twin);
                });

                var twinDetections = _activePerception.CurrentDetections;
                int det = twinDetections.Count;
                int defects = CountDefects(twinDetections);
                double passRate = _detectionTotalCount > 0
                    ? (double)(_detectionTotalCount - _detectionDefectCount) / _detectionTotalCount * 100
                    : 100;

                AIPanelCtrl.UpdateTwinStats(det, defects, passRate);
            }
            catch { }
        }
    }
}
