using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MachineVisionApp.AI;
using MachineVisionApp.Cloud;
using MachineVisionApp.Industrial;
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
        private string? _lastDecodedText; // 最近一次记录到日志的识别文本（去重）
        private bool _suppressSelectionEvents; // 语言切换重建下拉框时抑制 SelectionChanged 副作用
        private Mat? _lastOriginalFrame; // 最近一帧原始图像（用于点击取色）

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
            _imageDisplayComponent = new Components.ImageDisplayComponent(OriginalImage, EdgeImage);
            _thresholdParameterComponent = new Components.ThresholdParameterComponent(
                Threshold1TextBox, Threshold2TextBox, ApplyThresholdsButton);
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

            Loaded += MainWindow_Loaded;
            Closed += MainWindow_Closed;
        }

        /// <summary>
        /// 语言切换处理：重建所有本地化控件文本。
        /// </summary>
        private void OnLanguageChangedHandler(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            Dispatcher.Invoke(RefreshLocalizedControls);
        }

        /// <summary>
        /// 重建下拉框等代码设置的本地化文本（初始加载和语言切换时调用）。
        /// </summary>
        private void RefreshLocalizedControls()
        {
            _suppressSelectionEvents = true;
            try
            {
                int srcIndex = SourceTypeComboBox.SelectedIndex;
                SourceTypeComboBox.ItemsSource = new string[]
                {
                    TranslationService.Instance.LocalCamera,
                    TranslationService.Instance.NetworkStream
                };
                SourceTypeComboBox.SelectedIndex = srcIndex < 0 ? 0 : srcIndex;

                int modeIndex = ProcessingModeComboBox.SelectedIndex;
                ProcessingModeComboBox.ItemsSource = new string[]
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
                ProcessingModeComboBox.SelectedIndex = modeIndex < 0 ? 0 : modeIndex;

                int colorIndex = ColorComboBox.SelectedIndex;
                ColorComboBox.ItemsSource = TranslationService.Instance.ColorNames;
                ColorComboBox.SelectedIndex = colorIndex < 0 ? 0 : colorIndex;

                int scanIndex = ScanSourceComboBox.SelectedIndex;
                ScanSourceComboBox.ItemsSource = TranslationService.Instance.ScanSourceNames;
                ScanSourceComboBox.SelectedIndex = scanIndex < 0 ? 0 : scanIndex;
            }
            finally
            {
                _suppressSelectionEvents = false;
            }

            ModeLabelText.Text = GetModeName(_currentMode);
            ResultTitleText.Text = GetResultTitle(_currentMode);
            TemplateStatusText.Text = _templateMatchComponent.HasTemplate
                ? $"{TranslationService.Instance.TemplateLoaded} ({_templateMatchComponent.TemplateWidth}x{_templateMatchComponent.TemplateHeight})"
                : TranslationService.Instance.NoTemplate;
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

        /// <summary>
        /// 窗口加载完成：初始化下拉框和网络面板状态。
        /// </summary>
        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            RefreshLocalizedControls();

            NetworkConfigPanel.IsEnabled = false;
            NetworkConfigPanel.Opacity = 0.4;

            LogListBox.ItemsSource = AppLogger.Instance.Entries;

            // 新日志自动滚动到底部
            AppLogger.Instance.OnLogAdded += entry => Dispatcher.Invoke(() =>
            {
                if (LogPanel.Visibility == Visibility.Visible)
                    LogListBox.ScrollIntoView(entry);
            });

            // ---- 工业互联面板初始化 ----
            ScanSourceComboBox.ItemsSource = TranslationService.Instance.ScanSourceNames;
            ScanSourceComboBox.SelectedIndex = 0;
            SerialPortComboBox.ItemsSource = Industrial.SerialScanDriver.GetPortNames();
            SerialBaudComboBox.ItemsSource = new[] { "9600", "19200", "38400", "57600", "115200" };
            SerialBaudComboBox.SelectedIndex = 0;
            ReportListBox.ItemsSource = _workReportService.Records;
            TodayCountTextBlock.Text = _workReportService.TodayCount.ToString();

            // 配置回填
            ModbusIpTextBox.Text = _industrialConfig.Modbus.Ip;
            ModbusPortTextBox.Text = _industrialConfig.Modbus.Port.ToString();
            ModbusUnitTextBox.Text = _industrialConfig.Modbus.UnitId.ToString();
            OpcUaEndpointTextBox.Text = _industrialConfig.OpcUa.Endpoint;
            TcpScanPortTextBox.Text = _industrialConfig.TcpScanner.Port.ToString();
            PlcRegisterTextBox.Text = _industrialConfig.Report.PlcCountRegister.ToString();
            ModbusLinkCheckBox.IsChecked = _industrialConfig.Report.PlcReportEnable;
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

        /// <summary>
        /// 信号源类型切换：显示/隐藏网络配置面板。
        /// </summary>
        private void SourceTypeComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_suppressSelectionEvents) return;

            bool isNetwork = SourceTypeComboBox.SelectedIndex == 1;
            NetworkConfigPanel.IsEnabled = isNetwork;
            NetworkConfigPanel.Opacity = isNetwork ? 1.0 : 0.4;

            _videoCaptureComponent.SourceType = isNetwork
                ? Components.VideoSourceType.NetworkStream
                : Components.VideoSourceType.LocalCamera;

            if (isNetwork)
                BuildNetworkUrl();
        }

        /// <summary>根据 IP 和端口构建网络流 URL</summary>
        private void BuildNetworkUrl()
        {
            string ip = IPTextBox.Text.Trim();
            string port = PortTextBox.Text.Trim();
            _videoCaptureComponent.NetworkUrl = $"http://{ip}:{port}/video";
            _networkConfigured = !string.IsNullOrWhiteSpace(ip) && !string.IsNullOrWhiteSpace(port);
        }

        /// <summary>连接按钮：启动视频采集</summary>
        private void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (SourceTypeComboBox.SelectedIndex == 1)
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
                ConnectButton.IsEnabled = false;
                DisconnectButton.IsEnabled = true;
                AppLogger.Instance.Info("视频采集已启动");
            }
            else
            {
                ConnectButton.IsEnabled = true;
                DisconnectButton.IsEnabled = false;
                AppLogger.Instance.Error("视频采集启动失败");
            }
        }

        /// <summary>断开按钮：停止视频采集</summary>
        private void DisconnectButton_Click(object sender, RoutedEventArgs e)
        {
            _videoCaptureComponent.StopCapture();
            AppLogger.Instance.Info("视频采集已断开");
        }

        /// <summary>开启摄像头按钮</summary>
        private void StartCameraButton_Click(object sender, RoutedEventArgs e)
        {
            HideError();
            bool success = _videoCaptureComponent.StartCapture();
            if (success)
            {
                StartCameraButton.IsEnabled = false;
                StopCameraButton.IsEnabled = true;
                AppLogger.Instance.Info("摄像头已启动");
            }
            else
            {
                StartCameraButton.IsEnabled = true;
                StopCameraButton.IsEnabled = false;
                AppLogger.Instance.Error("摄像头启动失败");
            }
        }

        /// <summary>关闭摄像头按钮</summary>
        private void StopCameraButton_Click(object sender, RoutedEventArgs e)
        {
            _videoCaptureComponent.StopCapture();
            AppLogger.Instance.Info("摄像头已关闭");
        }

        /// <summary>
        /// 处理模式切换：更新当前处理模式，显示/隐藏对应的参数面板。
        /// </summary>
        private void ProcessingModeComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_suppressSelectionEvents) return;

            _currentMode = ProcessingModeComboBox.SelectedIndex switch
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

            ModeLabelText.Text = GetModeName(_currentMode);

            bool showThreshold = _currentMode is Components.ProcessingMode.Canny or Components.ProcessingMode.Contour;
            ThresholdPanel.Visibility = showThreshold ? Visibility.Visible : Visibility.Collapsed;
            ColorPanel.Visibility = _currentMode == Components.ProcessingMode.ColorDetection
                ? Visibility.Visible : Visibility.Collapsed;
            bool showTemplatePanel = _currentMode is Components.ProcessingMode.TemplateMatch or Components.ProcessingMode.FeatureMatch;
            TemplatePanel.Visibility = showTemplatePanel ? Visibility.Visible : Visibility.Collapsed;

            // 颜色检测模式下点击画面可取色
            OriginalImage.Cursor = _currentMode == Components.ProcessingMode.ColorDetection
                ? System.Windows.Input.Cursors.Cross : System.Windows.Input.Cursors.Arrow;
            UpdatePickColorHint();

            ResultTitleText.Text = GetResultTitle(_currentMode);

            _lastDecodedText = null;
            ModeResultTextBlock.Text = "";
            AppLogger.Instance.Info($"处理模式切换为: {ModeLabelText.Text}");
        }

        /// <summary>
        /// 目标颜色切换：更新颜色检测组件的目标颜色。
        /// </summary>
        private void ColorComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_suppressSelectionEvents) return;

            _colorDetectionComponent.Target = ColorComboBox.SelectedIndex switch
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
            AppLogger.Instance.Info($"目标颜色切换为: {ColorComboBox.SelectedItem}");
        }

        /// <summary>更新取色提示的显示状态（仅颜色检测 + 自定义取色时显示）</summary>
        private void UpdatePickColorHint()
        {
            PickColorHintText.Visibility =
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
                // 将控件坐标映射回原始图像像素坐标（考虑 Uniform 缩放和黑边）
                var pos = e.GetPosition(OriginalImage);
                double srcW = _lastOriginalFrame.Width;
                double srcH = _lastOriginalFrame.Height;
                double elemW = OriginalImage.ActualWidth;
                double elemH = OriginalImage.ActualHeight;
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
                ColorComboBox.SelectedIndex = 9; // 自定义(取色)
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

        /// <summary>
        /// 加载模板按钮：打开图片文件作为模板匹配/特征点匹配的模板。
        /// </summary>
        private void LoadTemplateButton_Click(object sender, RoutedEventArgs e)
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
                    TemplateStatusText.Text =
                        $"{TranslationService.Instance.TemplateLoaded} ({_templateMatchComponent.TemplateWidth}x{_templateMatchComponent.TemplateHeight})";
                    AppLogger.Instance.Info($"{TranslationService.Instance.TemplateLoaded}: {openFileDialog.FileName}");
                }
                else
                {
                    ShowError(TranslationService.GetStringStatic("TemplateLoadFailed"));
                }
            }
        }

        /// <summary>
        /// 保存截图：将当前原始帧保存为 PNG 文件。
        /// </summary>
        private void SaveScreenshotButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "MachineVisionApp");
                System.IO.Directory.CreateDirectory(dir);
                string path = System.IO.Path.Combine(dir,
                    $"Screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png");

                var source = OriginalImage.Source as System.Windows.Media.Imaging.BitmapSource;
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

        /// <summary>
        /// 录制按钮：切换录制状态（开始/停止）。
        /// </summary>
        private void RecordButton_Click(object sender, RoutedEventArgs e)
        {
            if (_recordingComponent.IsRecording)
            {
                _recordingComponent.StopRecording();
                RecordButton.Content = TranslationService.Instance.StartRecording;
                RecordButton.ClearValue(Button.BackgroundProperty);
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
                    RecordButton.Content = TranslationService.Instance.StopRecording;
                    RecordButton.Background = new SolidColorBrush(Color.FromRgb(0xF8, 0x51, 0x49));
                    AppLogger.Instance.Info($"{TranslationService.Instance.RecordingStarted} {path}");
                }
                else
                {
                    ShowError(TranslationService.GetStringStatic("SaveFailed"));
                    AppLogger.Instance.Error("录像启动失败");
                }
            }
        }

        /// <summary>
        /// 日志切换按钮：显示/隐藏日志面板。
        /// </summary>
        private void LogToggleButton_Click(object sender, RoutedEventArgs e)
        {
            bool isVisible = LogPanel.Visibility == Visibility.Visible;
            LogPanel.Visibility = isVisible ? Visibility.Collapsed : Visibility.Visible;
        }

        // ==================== 工业互联（企业化） ====================

        /// <summary>工业互联面板开关</summary>
        private void IndustrialToggleButton_Click(object sender, RoutedEventArgs e)
        {
            IndustrialPanel.Visibility = IndustrialPanel.Visibility == Visibility.Visible
                ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>更新 Modbus 状态指示</summary>
        private void UpdateModbusState(DeviceDriverState state)
        {
            Color color = state switch
            {
                DeviceDriverState.Connected => Color.FromRgb(0x3F, 0xB9, 0x50),
                DeviceDriverState.Connecting => Color.FromRgb(0xD2, 0x99, 0x22),
                DeviceDriverState.Failed => Color.FromRgb(0xF8, 0x51, 0x49),
                _ => Color.FromRgb(0x48, 0x4F, 0x58)
            };
            ModbusStatusDot.Fill = new SolidColorBrush(color);
            ModbusStatusText.Text = state switch
            {
                DeviceDriverState.Connected => TranslationService.GetStringStatic("StatusConnected"),
                DeviceDriverState.Connecting => TranslationService.GetStringStatic("StatusConnecting"),
                DeviceDriverState.Failed => TranslationService.GetStringStatic("StatusFailed"),
                _ => TranslationService.GetStringStatic("StatusDisconnected")
            };
            ModbusConnectButton.IsEnabled = state != DeviceDriverState.Connected;
            ModbusDisconnectButton.IsEnabled = state == DeviceDriverState.Connected;
        }

        /// <summary>更新 OPC-UA 状态指示</summary>
        private void UpdateOpcUaState(DeviceDriverState state)
        {
            Color color = state switch
            {
                DeviceDriverState.Connected => Color.FromRgb(0x3F, 0xB9, 0x50),
                DeviceDriverState.Connecting => Color.FromRgb(0xD2, 0x99, 0x22),
                DeviceDriverState.Failed => Color.FromRgb(0xF8, 0x51, 0x49),
                _ => Color.FromRgb(0x48, 0x4F, 0x58)
            };
            OpcUaStatusDot.Fill = new SolidColorBrush(color);
            OpcUaStatusText.Text = state switch
            {
                DeviceDriverState.Connected => TranslationService.GetStringStatic("StatusConnected"),
                DeviceDriverState.Connecting => TranslationService.GetStringStatic("StatusConnecting"),
                DeviceDriverState.Failed => TranslationService.GetStringStatic("StatusFailed"),
                _ => TranslationService.GetStringStatic("StatusDisconnected")
            };
            OpcUaConnectButton.IsEnabled = state != DeviceDriverState.Connected;
            OpcUaDisconnectButton.IsEnabled = state == DeviceDriverState.Connected;
        }

        /// <summary>
        /// 报工登记：条码 → 报工记录（SQLite 持久化 + 今日产量 + PLC 联动）。
        /// 必须在 UI 线程调用。
        /// </summary>
        private void AddReport(string barcode, string source)
        {
            if (string.IsNullOrWhiteSpace(barcode)) return;

            var record = _workReportService.AddRecord(WorkOrderTextBox.Text.Trim(), barcode, source);
            TodayCountTextBlock.Text = _workReportService.TodayCount.ToString();
            LastBarcodeTextBlock.Text = barcode;
            ReportListBox.ScrollIntoView(record);
            AppLogger.Instance.Info($"{TranslationService.Instance.WorkReport}: {barcode} ({source})");

            // PLC 联动：将今日产量写入 Modbus 保持寄存器
            if (ModbusLinkCheckBox.IsChecked == true)
            {
                try
                {
                    if (_modbusDriver.State != DeviceDriverState.Connected)
                    {
                        AppLogger.Instance.Warn(TranslationService.GetStringStatic("ModbusNotConnected"));
                        return;
                    }
                    if (ushort.TryParse(PlcRegisterTextBox.Text, out ushort register))
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

        /// <summary>Modbus 连接按钮</summary>
        private async void ModbusConnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (!byte.TryParse(ModbusUnitTextBox.Text, out byte unitId) ||
                !int.TryParse(ModbusPortTextBox.Text, out int port) ||
                string.IsNullOrWhiteSpace(ModbusIpTextBox.Text))
            {
                ShowError(TranslationService.GetStringStatic("InvalidThreshold"));
                return;
            }

            HideError();
            _modbusDriver.UpdateSettings(ModbusIpTextBox.Text.Trim(), port, unitId);
            ModbusConnectButton.IsEnabled = false;
            bool ok = await _modbusDriver.ConnectAsync();
            if (ok)
            {
                AppLogger.Instance.Info($"Modbus 已连接: {_modbusDriver.Ip}:{_modbusDriver.Port} (从站 {_modbusDriver.UnitId})");
            }
        }

        /// <summary>Modbus 断开按钮</summary>
        private void ModbusDisconnectButton_Click(object sender, RoutedEventArgs e)
        {
            _modbusDriver.Disconnect();
            AppLogger.Instance.Info("Modbus 已断开");
        }

        /// <summary>Modbus 读取保持寄存器</summary>
        private void ModbusReadButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!ushort.TryParse(ModbusAddrTextBox.Text, out ushort address))
                    return;
                ushort[] values = _modbusDriver.ReadHoldingRegisters(address, 1);
                ModbusResultText.Text = $"= {values[0]}";
                AppLogger.Instance.Info($"Modbus 读取: [{address}] = {values[0]}");
            }
            catch (Exception ex)
            {
                ModbusResultText.Text = "";
                AppLogger.Instance.Error($"Modbus 读取失败: {ex.Message}");
            }
        }

        /// <summary>Modbus 写单个保持寄存器</summary>
        private void ModbusWriteButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!ushort.TryParse(ModbusAddrTextBox.Text, out ushort address) ||
                    !ushort.TryParse(ModbusValueTextBox.Text, out ushort value))
                    return;
                _modbusDriver.WriteSingleRegister(address, value);
                ModbusResultText.Text = "OK";
                AppLogger.Instance.Info($"Modbus 写入: [{address}] = {value}");
            }
            catch (Exception ex)
            {
                ModbusResultText.Text = "";
                AppLogger.Instance.Error($"Modbus 写入失败: {ex.Message}");
            }
        }

        /// <summary>OPC-UA 连接按钮</summary>
        private async void OpcUaConnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(OpcUaEndpointTextBox.Text)) return;

            HideError();
            _opcUaDriver.UpdateSettings(OpcUaEndpointTextBox.Text.Trim());
            OpcUaConnectButton.IsEnabled = false;
            bool ok = await _opcUaDriver.ConnectAsync();
            if (ok)
            {
                AppLogger.Instance.Info($"OPC-UA 已连接: {_opcUaDriver.EndpointUrl}");
            }
        }

        /// <summary>OPC-UA 断开按钮</summary>
        private void OpcUaDisconnectButton_Click(object sender, RoutedEventArgs e)
        {
            _opcUaDriver.Disconnect();
            AppLogger.Instance.Info("OPC-UA 已断开");
        }

        /// <summary>OPC-UA 读取节点</summary>
        private async void OpcUaReadButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string? result = await _opcUaDriver.ReadNodeAsync(OpcUaNodeTextBox.Text.Trim());
                OpcUaResultText.Text = $"{OpcUaNodeTextBox.Text.Trim()} = {result}";
                AppLogger.Instance.Info($"OPC-UA 读取: {OpcUaNodeTextBox.Text.Trim()} = {result}");
            }
            catch (Exception ex)
            {
                OpcUaResultText.Text = "";
                AppLogger.Instance.Error($"OPC-UA 读取失败: {ex.Message}");
            }
        }

        /// <summary>OPC-UA 写入节点（数值优先按 double 解析，否则按字符串写入）</summary>
        private async void OpcUaWriteButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                object value = double.TryParse(OpcUaValueTextBox.Text, out double number)
                    ? number : OpcUaValueTextBox.Text;
                await _opcUaDriver.WriteNodeAsync(OpcUaNodeTextBox.Text.Trim(), value);
                OpcUaResultText.Text = "OK";
                AppLogger.Instance.Info($"OPC-UA 写入: {OpcUaNodeTextBox.Text.Trim()} = {value}");
            }
            catch (Exception ex)
            {
                OpcUaResultText.Text = "";
                AppLogger.Instance.Error($"OPC-UA 写入失败: {ex.Message}");
            }
        }

        /// <summary>扫码源切换：显示对应配置面板</summary>
        private void ScanSourceComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (ScanSourceComboBox.SelectedIndex < 0) return;
            SerialScanPanel.Visibility = ScanSourceComboBox.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
            TcpScanPanel.Visibility = ScanSourceComboBox.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>串口扫码开关</summary>
        private async void SerialToggleButton_Click(object sender, RoutedEventArgs e)
        {
            if (_serialDriver.IsRunning)
            {
                _serialDriver.Stop();
                SerialToggleButton.Content = TranslationService.Instance.Start;
                AppLogger.Instance.Info("串口扫码已停止");
                return;
            }

            string? portName = SerialPortComboBox.SelectedItem as string;
            if (string.IsNullOrEmpty(portName)) return;
            _serialDriver.PortName = portName;
            if (int.TryParse(SerialBaudComboBox.SelectedItem as string, out int baud))
                _serialDriver.BaudRate = baud;

            bool ok = await _serialDriver.StartAsync();
            if (ok)
            {
                SerialToggleButton.Content = TranslationService.Instance.Stop;
                AppLogger.Instance.Info($"串口扫码已启动: {portName} @ {_serialDriver.BaudRate}");
            }
        }

        /// <summary>TCP 扫码开关</summary>
        private async void TcpToggleButton_Click(object sender, RoutedEventArgs e)
        {
            if (_tcpDriver.IsRunning)
            {
                _tcpDriver.Stop();
                TcpToggleButton.Content = TranslationService.Instance.Start;
                AppLogger.Instance.Info("TCP 扫码已停止");
                return;
            }

            if (!int.TryParse(TcpScanPortTextBox.Text, out int port) || port <= 0 || port > 65535)
                return;
            _tcpDriver.Port = port;

            bool ok = await _tcpDriver.StartAsync();
            if (ok)
            {
                TcpToggleButton.Content = TranslationService.Instance.Stop;
                AppLogger.Instance.Info($"TCP 扫码已启动: 端口 {port}");
            }
        }

        /// <summary>导出报工记录 CSV</summary>
        private void ExportCsvButton_Click(object sender, RoutedEventArgs e)
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

        /// <summary>清空今日报工记录</summary>
        private void ClearReportButton_Click(object sender, RoutedEventArgs e)
        {
            _workReportService.ClearToday();
            TodayCountTextBlock.Text = "0";
            LastBarcodeTextBlock.Text = "--";
            AppLogger.Instance.Info("今日报工记录已清空");
        }

        /// <summary>清空日志按钮：清空所有日志条目</summary>
        private void ClearLogButton_Click(object sender, RoutedEventArgs e)
        {
            AppLogger.Instance.Clear();
        }

        /// <summary>
        /// 连接状态变更处理：更新指示灯颜色、按钮启用状态、空状态遮罩。
        /// </summary>
        private void OnConnectionStateChangedHandler(Components.ConnectionState state)
        {
            Dispatcher.Invoke(() =>
            {
                Color color = state switch
                {
                    Components.ConnectionState.Connected => Color.FromRgb(0x3F, 0xB9, 0x50),
                    Components.ConnectionState.Connecting => Color.FromRgb(0xD2, 0x99, 0x22),
                    Components.ConnectionState.Failed => Color.FromRgb(0xF8, 0x51, 0x49),
                    _ => Color.FromRgb(0x48, 0x4F, 0x58)
                };

                var brush = new SolidColorBrush(color);
                ConnectionIndicator.Fill = brush;
                StatusIndicator.Fill = brush;
                StatusText.Text = TranslationService.Instance.GetConnectionStatusText(state);
                StatusTextFooter.Text = TranslationService.Instance.GetConnectionStatusText(state);

                if (state == Components.ConnectionState.Connected)
                {
                    ConnectButton.IsEnabled = false;
                    DisconnectButton.IsEnabled = true;
                    StartCameraButton.IsEnabled = false;
                    StopCameraButton.IsEnabled = true;
                    EmptyOverlayLeft.Visibility = Visibility.Collapsed;
                    EmptyOverlayRight.Visibility = Visibility.Collapsed;
                    StatusTextFooter.Foreground = new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50));
                    AppLogger.Instance.Info("设备已连接");
                }
                else if (state == Components.ConnectionState.Disconnected)
                {
                    ConnectButton.IsEnabled = true;
                    DisconnectButton.IsEnabled = false;
                    StartCameraButton.IsEnabled = true;
                    StopCameraButton.IsEnabled = false;
                    EmptyOverlayLeft.Visibility = Visibility.Visible;
                    EmptyOverlayRight.Visibility = Visibility.Visible;
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
        /// <param name="originalFrame">原始彩色帧</param>
        /// <param name="grayFrame">灰度帧</param>
        /// <param name="count">输出：目标数量（轮廓数/颜色目标数）</param>
        /// <param name="modeResult">输出：模式相关结果文本（识别内容/匹配分数）</param>
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

                // 颜色检测模式下保存最近一帧原始图像，供点击取色使用
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

                            // 统计
                            _detectionTotalCount += aiDetCount;
                            int defects = _activePerception.CurrentDetections.Count(d => d.Confidence > 0.8f);
                            _detectionDefectCount += defects;

                            // 异步发布到云端（每 30 帧一次，避免过频）
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
                            AiDetectionsText.Text = $"Detections: {aiDetCount}";
                            AiTracksText.Text = $"Tracks: {aiTrackCount}";
                            if (_activePerception != null)
                                AiSampleText.Text = $"Interval: {_activePerception.AdaptiveInterval}";
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
                    FaceCountTextBlock.Text = $"{faceCount}";
                    ContourCountTextBlock.Text = $"{contourCount}";
                    FpsTextBlock.Text = $"{_currentFps:F1} FPS";
                    ProcessTimeTextBlock.Text = $"{processTimeMs} ms";
                    bool showThreshold = _currentMode is Components.ProcessingMode.Canny or Components.ProcessingMode.Contour;
                    ThresholdInfoText.Text = showThreshold ? $"{_threshold1} ~ {_threshold2}" : "";
                    ModeResultTextBlock.Text = modeResult;

                    // QR/条码识别到新内容时记录日志（去重）+ 摄像头扫码报工
                    if (_currentMode == Components.ProcessingMode.QRCode &&
                        !string.IsNullOrEmpty(modeResult) && modeResult != _lastDecodedText)
                    {
                        _lastDecodedText = modeResult;
                        AppLogger.Instance.Info($"{TranslationService.Instance.QRDecoded}: {modeResult}");
                        if (CameraReportCheckBox.IsChecked == true)
                            AddReport(modeResult, TranslationService.Instance.ScanCamera);
                    }

                    UpdateCameraData();
                });

                // 数字孪生更新（在 Dispatcher 外收集数据，在 Dispatcher 内更新 UI）
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
            ThresholdInfoText.Text = $"{threshold1} ~ {threshold2}";
            AppLogger.Instance.Info($"阈值更新: {threshold1} ~ {threshold2}");
        }

        /// <summary>加载图片按钮：打开本地图片并执行处理管线</summary>
        private void LoadImageButton_Click(object sender, RoutedEventArgs e)
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
                    FaceCountTextBlock.Text = $"{faceCount}";
                    ContourCountTextBlock.Text = $"{contourCount}";
                    bool showThreshold = _currentMode is Components.ProcessingMode.Canny or Components.ProcessingMode.Contour;
                    ThresholdInfoText.Text = showThreshold ? $"{_threshold1} ~ {_threshold2}" : "";
                    ModeResultTextBlock.Text = modeResult;
                    EmptyOverlayLeft.Visibility = Visibility.Collapsed;
                    EmptyOverlayRight.Visibility = Visibility.Collapsed;
                    HideError();

                    // 左卡片头部显示已加载图片的文件名
                    CameraDataTextBlock.Text = System.IO.Path.GetFileName(openFileDialog.FileName);
                    SourceInfoText.Text = openFileDialog.FileName;

                    // 更新最近帧引用，供颜色检测取色使用
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
            SourceInfoText.Text = _videoCaptureComponent.GetSourceInfo();
        }

        /// <summary>捕获停止事件处理：清空画面、重置状态</summary>
        private void OnCaptureStoppedHandler(string? reason)
        {
            Dispatcher.Invoke(() =>
            {
                if (_recordingComponent.IsRecording)
                {
                    _recordingComponent.StopRecording();
                    RecordButton.Content = TranslationService.Instance.StartRecording;
                    RecordButton.ClearValue(Button.BackgroundProperty);
                }

                OriginalImage.Source = null;
                EdgeImage.Source = null;
                CameraDataTextBlock.Text = "";
                FaceCountTextBlock.Text = "0";
                ContourCountTextBlock.Text = "0";
                FpsTextBlock.Text = "0 FPS";
                ProcessTimeTextBlock.Text = "0 ms";
                EmptyOverlayLeft.Visibility = Visibility.Visible;
                EmptyOverlayRight.Visibility = Visibility.Visible;
                StopCameraButton.IsEnabled = false;
                StartCameraButton.IsEnabled = true;

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

        /// <summary>显示错误信息</summary>
        private void ShowError(string message)
        {
            ErrorBorder.Visibility = Visibility.Visible;
            ErrorMessageTextBlock.Text = message;
        }

        /// <summary>隐藏错误信息</summary>
        private void HideError()
        {
            ErrorBorder.Visibility = Visibility.Collapsed;
            ErrorMessageTextBlock.Text = "";
        }

        // ==================== AI + Cloud ====================

        /// <summary>AI / Cloud 面板开关</summary>
        private void AiCloudToggleButton_Click(object sender, RoutedEventArgs e)
        {
            AiCloudPanel.Visibility = AiCloudPanel.Visibility == Visibility.Visible
                ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>加载 YOLO 模型</summary>
        private void LoadYoloModelButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "ONNX Model (*.onnx)|*.onnx|All Files (*.*)|*.*"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                float conf = 0.5f;
                float.TryParse(ConfThresholdTextBox.Text, out conf);

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

                YoloModelPathText.Text = Path.GetFileName(dlg.FileName);
                AiStatusDot.Fill = new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50));
                AiStatusText.Text = "Model Loaded";
                AiStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50));
                AppLogger.Instance.Info($"YOLO 模型已加载: {dlg.FileName}");
            }
            catch (Exception ex)
            {
                AiStatusDot.Fill = new SolidColorBrush(Color.FromRgb(0xF8, 0x51, 0x49));
                AiStatusText.Text = "Load Failed";
                ShowError($"YOLO 模型加载失败: {ex.Message}");
                AppLogger.Instance.Error($"YOLO 模型加载失败: {ex.Message}");
            }
        }

        /// <summary>AI 启用/禁用</summary>
        private void AiEnableCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            _aiEnabled = AiEnableCheckBox.IsChecked == true;

            if (_aiEnabled && (_yoloComponent == null || !_yoloComponent.IsModelLoaded))
            {
                ShowError("请先加载 YOLO 模型");
                AiEnableCheckBox.IsChecked = false;
                _aiEnabled = false;
                return;
            }

            if (_aiEnabled)
            {
                lock (_aiLock) { _activePerception?.Reset(); }
                AiStatusDot.Fill = new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50));
                _detectionTotalCount = 0;
                _detectionDefectCount = 0;
                AppLogger.Instance.Info("AI 主动感知已启用");
            }
            else
            {
                AiStatusDot.Fill = new SolidColorBrush(Color.FromRgb(0x48, 0x4F, 0x58));
                AppLogger.Instance.Info("AI 主动感知已禁用");
            }
        }

        /// <summary>初始化 Cloud 服务</summary>
        private async void CloudInitButton_Click(object sender, RoutedEventArgs e)
        {
            string region = AwsRegionTextBox.Text.Trim();
            string bucket = S3BucketTextBox.Text.Trim();
            string iotEndpoint = IoTEndpointTextBox.Text.Trim();
            string lambdaFunc = LambdaFuncTextBox.Text.Trim();

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
                    CloudUploadScreenshotButton.IsEnabled = true;
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
                    CloudAlertButton.IsEnabled = true;
                    CloudPublishStatsButton.IsEnabled = true;
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
                    AppLogger.Instance.Info($"Lambda 客户端已初始化: {lambdaFunc}");
                }

                CloudStatusDot.Fill = new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50));
                CloudStatusText.Text = "Connected";
                CloudStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50));
                HideError();
            }
            catch (Exception ex)
            {
                CloudStatusDot.Fill = new SolidColorBrush(Color.FromRgb(0xF8, 0x51, 0x49));
                CloudStatusText.Text = "Init Failed";
                ShowError($"Cloud 初始化失败: {ex.Message}");
                AppLogger.Instance.Error($"Cloud 初始化失败: {ex.Message}");
            }

            await Task.CompletedTask;
        }

        /// <summary>上传截图到 S3</summary>
        private async void CloudUploadScreenshotButton_Click(object sender, RoutedEventArgs e)
        {
            if (_s3Service == null)
            {
                ShowError("请先初始化 S3 服务");
                return;
            }

            try
            {
                var source = OriginalImage.Source as System.Windows.Media.Imaging.BitmapSource;
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
                AppLogger.Instance.Info($"截图已上传 S3: {key}");
            }
            catch (Exception ex)
            {
                ShowError($"S3 上传失败: {ex.Message}");
                AppLogger.Instance.Error($"S3 上传失败: {ex.Message}");
            }
        }

        /// <summary>发送告警到 IoT</summary>
        private async void CloudAlertButton_Click(object sender, RoutedEventArgs e)
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

        /// <summary>发布生产统计到 IoT</summary>
        private async void CloudPublishStatsButton_Click(object sender, RoutedEventArgs e)
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

        /// <summary>数字孪生视图更新（每帧 ProcessFrame 后更新）</summary>
        private void UpdateDigitalTwin()
        {
            if (_digitalTwin == null || _activePerception == null) return;

            try
            {
                using Mat twin = _digitalTwin.Render(
                    _activePerception.CurrentTracks,
                    _activePerception.CurrentDetections);
                DigitalTwinImage.Dispatcher.Invoke(() =>
                {
                    DigitalTwinImage.Source = OpenCvSharp.WpfExtensions.BitmapSourceConverter.ToBitmapSource(twin);
                });

                int det = _activePerception.CurrentDetections.Count;
                int defects = _activePerception.CurrentDetections.Count(d => d.Confidence > 0.8f);
                double passRate = _detectionTotalCount > 0
                    ? (double)(_detectionTotalCount - _detectionDefectCount) / _detectionTotalCount * 100
                    : 100;

                TwinDetectionsText.Text = $"Detections: {det}";
                TwinDefectsText.Text = $"Defects: {defects}";
                TwinPassRateText.Text = $"Pass: {passRate:F1}%";

                if (_aiEnabled)
                {
                    TwinStatusDot.Fill = new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50));
                    TwinStatusText.Text = "Active";
                }
            }
            catch { }
        }
    }
}
