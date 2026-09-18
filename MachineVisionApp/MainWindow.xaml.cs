using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MachineVisionApp.AI;
using MachineVisionApp.Cloud;
using MachineVisionApp.Industrial;
using MachineVisionApp.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using OpenCvSharp;

namespace MachineVisionApp
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

            Loaded += MainWindow_Loaded;
            Closed += MainWindow_Closed;
        }

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
            CameraPanelCtrl.RefreshTexts();
            ProcessingPanelCtrl.RefreshTexts();
            AIPanelCtrl.RefreshTexts();
            CloudPanelCtrl.RefreshTexts();
            IndustrialPanelCtrl.RefreshTexts();
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
                    TranslationService.Instance.NetworkStream
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
            RefreshLocalizedControls();

            // 日志面板
            LogPanelCtrl.SetLogSource(AppLogger.Instance.Entries);

            AppLogger.Instance.OnLogAdded += entry => Dispatcher.Invoke(() =>
            {
                if (LogPanelCtrl.Visibility == Visibility.Visible)
                    LogPanelCtrl.ScrollToBottom();
            });

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
        }

        /// <summary>
        /// 窗口关闭：释放所有组件资源。
        /// </summary>
        private void MainWindow_Closed(object? sender, EventArgs e)
        {
            _recordingComponent.Dispose();
            _videoCaptureComponent.Dispose();
            _templateMatchComponent.Clear();
            _featureMatchComponent.Clear();
            _lastOriginalFrame?.Dispose();

            _serialDriver.Dispose();
            _tcpDriver.Dispose();
            _modbusDriver.Dispose();
            _opcUaDriver.Dispose();

            lock (_aiLock)
            {
                _yoloComponent?.Dispose();
                _digitalTwin?.Dispose();
            }
            _s3Service?.Dispose();
            _iotService?.Dispose();
            _lambdaClient?.Dispose();
        }

        // ==================== 信号源 & 连接 ====================

        /// <summary>根据 IP 和端口构建网络流 URL</summary>
        private void BuildNetworkUrl()
        {
            string ip = CameraPanelCtrl.IPTextBoxEl.Text.Trim();
            string port = CameraPanelCtrl.PortTextBoxEl.Text.Trim();
            _videoCaptureComponent.NetworkUrl = $"http://{ip}:{port}/video";
            _networkConfigured = !string.IsNullOrWhiteSpace(ip) && !string.IsNullOrWhiteSpace(port);
        }

        // ==================== CameraPanel 事件处理 ====================

        private void CameraPanelCtrl_ConnectRequested(object? sender, EventArgs e)
        {
            if (CameraPanelCtrl.SourceTypeComboBoxEl.SelectedIndex == 1)
            {
                BuildNetworkUrl();
                if (!_networkConfigured)
                {
                    ShowError(TranslationService.GetStringStatic("CameraOpenError"));
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
                    Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "MachineVisionApp");
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
                    Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "MachineVisionApp");
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

                bool started = _recordingComponent.StartRecording(path, 15.0, width, height);
                if (started)
                {
                    CameraPanelCtrl.RecordButtonEl.Content = TranslationService.Instance.StopRecording;
                    CameraPanelCtrl.RecordButtonEl.Background = new SolidColorBrush(Color.FromRgb(0xF8, 0x51, 0x49));
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
            AppLogger.Instance.Info($"阈值更新: {t1} ~ {t2}");
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

            bool showThreshold = _currentMode is Components.ProcessingMode.Canny or Components.ProcessingMode.Contour;
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
        /// 点击左侧画面取色：将点击位置的像素颜色设为颜色检测的自定义目标。
        /// </summary>
        private void OriginalViewGrid_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_currentMode != Components.ProcessingMode.ColorDetection || _lastOriginalFrame == null)
                return;

            try
            {
                var pos = e.GetPosition(CameraPanelCtrl.OriginalImageEl);
                double srcW = _lastOriginalFrame.Width;
                double srcH = _lastOriginalFrame.Height;
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
                Cv2.CvtColor(_lastOriginalFrame, hsv, ColorConversionCodes.BGR2HSV);
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
                float conf = 0.5f;
                float.TryParse(AIPanelCtrl.ConfThresholdTextBoxEl.Text, out conf);

                lock (_aiLock)
                {
                    _yoloComponent?.Dispose();
                    _yoloComponent = new AI.YoloDetectionComponent(
                        dlg.FileName, confidenceThreshold: conf);

                    _kalmanTracker = new AI.KalmanTrackerComponent();
                    _activePerception = new AI.ActivePerceptionEngine(_yoloComponent, _kalmanTracker);
                    _digitalTwin?.Dispose();
                    _digitalTwin = new AI.DigitalTwinRenderer();
                }

                AIPanelCtrl.SetModelStatus(true, Path.GetFileName(dlg.FileName));
                AppLogger.Instance.Info($"YOLO 模型已加载: {dlg.FileName}");
            }
            catch (Exception ex)
            {
                AIPanelCtrl.SetModelStatus(false, "Load Failed");
                ShowError($"YOLO 模型加载失败: {ex.Message}");
                AppLogger.Instance.Error($"YOLO 模型加载失败: {ex.Message}");
            }
        }

        private void AIPanelCtrl_EnableChanged(object? sender, EventArgs e)
        {
            if (!IsLoaded) return;
            _aiEnabled = AIPanelCtrl.AiEnableCheckBoxEl.IsChecked == true;

            if (_aiEnabled && (_yoloComponent == null || !_yoloComponent.IsModelLoaded))
            {
                ShowError("请先加载 YOLO 模型");
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
                ShowError("请输入 AWS Region");
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
                    CloudPanelCtrl.SetS3Status(true, "Connected");
                    AppLogger.Instance.Info($"S3 服务已初始化: {bucket} ({region})");
                }

                if (!string.IsNullOrEmpty(iotEndpoint))
                {
                    string certPath = Path.Combine(AppContext.BaseDirectory, "certs", "device-certificate.pem.crt");
                    string keyPath = Path.Combine(AppContext.BaseDirectory, "certs", "private.pem.key");

                    _iotService?.Dispose();
                    if (File.Exists(certPath) && File.Exists(keyPath))
                    {
                        _iotService = new IoTService(iotEndpoint, certPath, keyPath);
                    }
                    else
                    {
                        var handler = new System.Net.Http.HttpClientHandler();
                        _iotService = new IoTService(
                            new System.Net.Http.HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) },
                            iotEndpoint);
                        AppLogger.Instance.Warn("IoT: 未找到设备证书，使用无认证模式（仅限测试）");
                    }
                    _iotService.OnPublishSuccess += topic =>
                        Dispatcher.Invoke(() => AppLogger.Instance.Info($"IoT 发布成功: {topic}"));
                    _iotService.OnPublishError += (topic, ex) =>
                        Dispatcher.Invoke(() => AppLogger.Instance.Error($"IoT 发布失败: {topic} - {ex.Message}"));
                    CloudPanelCtrl.SetIoTStatus(true, "Connected");
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
                    CloudPanelCtrl.SetLambdaStatus(true, "Ready");
                    AppLogger.Instance.Info($"Lambda 客户端已初始化: {lambdaFunc}");
                }

                CloudPanelCtrl.SetCloudOverallStatus("Connected");
                HideError();
            }
            catch (Exception ex)
            {
                CloudPanelCtrl.SetCloudOverallStatus("Init Failed");
                ShowError($"Cloud 初始化失败: {ex.Message}");
                AppLogger.Instance.Error($"Cloud 初始化失败: {ex.Message}");
            }

            await Task.CompletedTask;
        }

        private async void CloudPanelCtrl_UploadScreenshotRequested(object? sender, EventArgs e)
        {
            if (_s3Service == null)
            {
                ShowError("请先初始化 S3 服务");
                return;
            }

            try
            {
                var source = CameraPanelCtrl.OriginalImageEl.Source as System.Windows.Media.Imaging.BitmapSource;
                if (source == null)
                {
                    ShowError("没有可上传的截图");
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
                ShowError("请先初始化 IoT 服务");
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
                ShowError("请先初始化 IoT 服务");
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
                ShowError("请先初始化 Lambda 客户端");
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
                        _modbusDriver.WriteSingleRegister(register, (ushort)_workReportService.TodayCount);
                        AppLogger.Instance.Info($"PLC联动: [{register}] = {_workReportService.TodayCount}");
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
                string statusText = TranslationService.Instance.GetConnectionStatusText(state);
                bool connected = state == Components.ConnectionState.Connected;

                CameraPanelCtrl.SetConnected(connected, statusText);

                ConnectionIndicator.Fill = connected
                    ? new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50))
                    : new SolidColorBrush(Color.FromRgb(0x48, 0x4F, 0x58));
                StatusIndicator.Fill = connected
                    ? new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50))
                    : new SolidColorBrush(Color.FromRgb(0x48, 0x4F, 0x58));
                StatusText.Text = statusText;
                StatusTextFooter.Text = statusText;

                if (connected)
                {
                    CameraPanelCtrl.EmptyOverlayLeftEl.Visibility = Visibility.Collapsed;
                    CameraPanelCtrl.EmptyOverlayRightEl.Visibility = Visibility.Collapsed;
                    StatusTextFooter.Foreground = new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50));
                    AppLogger.Instance.Info("设备已连接");
                }
                else if (state == Components.ConnectionState.Disconnected)
                {
                    CameraPanelCtrl.EmptyOverlayLeftEl.Visibility = Visibility.Visible;
                    CameraPanelCtrl.EmptyOverlayRightEl.Visibility = Visibility.Visible;
                    StatusTextFooter.Foreground = new SolidColorBrush(Color.FromRgb(0x48, 0x4F, 0x58));
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
                    Mat colorResult = _colorDetectionComponent.Detect(originalFrame, out count);
                    return colorResult;

                case Components.ProcessingMode.TemplateMatch:
                    Mat tmResult = _templateMatchComponent.Match(grayFrame, out double score);
                    modeResult = _templateMatchComponent.HasTemplate ? $"{score:P1}" : "";
                    return tmResult;

                case Components.ProcessingMode.ShapeDetection:
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
        /// 该方法在后台线程调用，UI 更新通过 Dispatcher 封送。
        /// </summary>
        private void ProcessFrame(Mat originalFrame, Mat grayFrame)
        {
            try
            {
                _frameStopwatch.Restart();

                if (_currentMode == Components.ProcessingMode.ColorDetection)
                {
                    _lastOriginalFrame?.Dispose();
                    _lastOriginalFrame = originalFrame.Clone();
                }

                Mat resultImage = ProcessByMode(originalFrame, grayFrame, out int contourCount, out string modeResult);
                int faceCount = 0;
                try
                {
                    faceCount = _faceDetectionComponent.DetectFaces(originalFrame);

                    // ---- AI 主动感知 ----
                    int aiDetCount = 0;
                    int aiTrackCount = 0;
                    if (_aiEnabled && _activePerception != null && _yoloComponent != null && _yoloComponent.IsModelLoaded)
                    {
                        lock (_aiLock)
                        {
                            _activePerception.ProcessFrame(originalFrame);
                            _activePerception.DrawOverlay(resultImage);
                            aiDetCount = _activePerception.CurrentDetections.Count;
                            aiTrackCount = _activePerception.CurrentTracks.Count;

                            _detectionTotalCount += aiDetCount;
                            int defects = _activePerception.CurrentDetections.Count(d => d.Confidence > 0.8f);
                            _detectionDefectCount += defects;

                            if (_iotService != null && _detectionTotalCount % 30 == 0)
                            {
                                var iot = _iotService;
                                var tracks = _activePerception.CurrentTracks;
                                _ = Task.Run(async () =>
                                {
                                    try
                                    {
                                        await iot.PublishDetectionResultAsync(
                                            _currentMode.ToString(), aiDetCount, 0.8f);
                                    }
                                    catch { }
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
                        Dispatcher.Invoke(() =>
                        {
                            AIPanelCtrl.UpdateDetectionStats(aiDetCount, aiTrackCount,
                                _activePerception?.AdaptiveInterval ?? 0);
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

                Dispatcher.Invoke(() =>
                {
                    bool showThreshold = _currentMode is Components.ProcessingMode.Canny or Components.ProcessingMode.Contour;
                    string thresholdInfo = showThreshold ? $"{_threshold1} ~ {_threshold2}" : "";
                    ProcessingPanelCtrl.UpdateResults(faceCount, contourCount, thresholdInfo, modeResult);

                    FpsTextBlock.Text = $"{_currentFps:F1} FPS";
                    ProcessTimeTextBlock.Text = $"{processTimeMs} ms";

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
                    Dispatcher.Invoke(UpdateDigitalTwin);
                }
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    ShowError(TranslationService.GetStringStatic("FrameProcessError") + $": {ex.Message}");
                    AppLogger.Instance.Error($"帧处理异常: {ex.Message}");
                });
            }
        }

        /// <summary>阈值更新回调</summary>
        private void UpdateThresholds(int threshold1, int threshold2)
        {
            _threshold1 = threshold1;
            _threshold2 = threshold2;
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

                    _lastOriginalFrame?.Dispose();
                    _lastOriginalFrame = image.Clone();
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
            Dispatcher.Invoke(() =>
            {
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
            Dispatcher.Invoke(() =>
            {
                ShowError(msg);
                AppLogger.Instance.Error(msg);
            });
        }

        private readonly DispatcherTimer _errorAutoCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };

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

                int det = _activePerception.CurrentDetections.Count;
                int defects = _activePerception.CurrentDetections.Count(d => d.Confidence > 0.8f);
                double passRate = _detectionTotalCount > 0
                    ? (double)(_detectionTotalCount - _detectionDefectCount) / _detectionTotalCount * 100
                    : 100;

                AIPanelCtrl.UpdateTwinStats(det, defects, passRate);
            }
            catch { }
        }
    }
}
