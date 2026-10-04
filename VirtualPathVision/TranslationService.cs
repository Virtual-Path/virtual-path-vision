using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Resources;

namespace VirtualPathVision
{
    /// <summary>
    /// 国际化翻译服务（单例模式）。
    /// 基于 .resx 资源文件提供中英文切换功能。
    /// 实现 INotifyPropertyChanged，切换语言时通过 PropertyChanged("") 刷新所有 XAML 绑定。
    /// 支持语言偏好持久化到 user_settings.json。
    /// </summary>
    public class TranslationService : INotifyPropertyChanged
    {
        private static readonly ResourceManager _resourceManager = new ResourceManager(
            "VirtualPathVision.Resources.Strings", typeof(TranslationService).Assembly);

        private static readonly TranslationService _instance = new();
        public static TranslationService Instance => _instance;

        /// <summary>支持的语言：英语（默认）与简体中文</summary>
        private static readonly string[] SupportedCultures = { "en-US", "zh-CN" };

        public event PropertyChangedEventHandler? PropertyChanged;

        public void ChangeLanguage(string cultureName)
        {
            if (!TryNormalizeCulture(cultureName, out string normalized))
                normalized = "en-US";

            var culture = new CultureInfo(normalized);

            // 仅设置 CurrentUICulture/CurrentCulture 只影响调用线程。
            // 采集循环等长期存活的任务在启动时就把 CultureInfo 捕获进了 ExecutionContext，
            // 之后切换语言对它们无效（例如形状标签会永久停留在旧语言）。
            // 因此必须同时设置 DefaultThreadCurrent*，让所有新线程继承新语言。
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            CultureInfo.CurrentCulture = culture;

            SaveLanguage(normalized);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(""));
        }

        public string CurrentLanguage => CultureInfo.CurrentUICulture.Name;

        private static bool TryNormalizeCulture(string? cultureName, out string normalized)
        {
            normalized = "en-US";
            if (string.IsNullOrWhiteSpace(cultureName))
                return false;

            foreach (var supported in SupportedCultures)
            {
                if (string.Equals(supported, cultureName, StringComparison.OrdinalIgnoreCase))
                {
                    normalized = supported;
                    return true;
                }
            }
            return false;
        }

        private void SaveLanguage(string culture) => UserSettings.Set("Language", culture);

        public string LoadLanguage()
        {
            // 精确读取 Language 键，而不是在整份 JSON 文本里搜子串
            // （旧实现只要文件任意位置出现 "zh-CN" 就会误判语言）
            var saved = UserSettings.GetString("Language");
            return TryNormalizeCulture(saved, out string normalized) ? normalized : "en-US";
        }

        public string this[string key] => GetString(key);

        // ---- UI 绑定属性 ----
        public string AppTitle => GetString("AppTitle");
        public string OriginalStream => GetString("OriginalStream");
        public string EdgeDetection => GetString("EdgeDetection");
        public string Camera => GetString("Camera");
        public string CameraWaiting => GetString("CameraWaiting");
        public string Faces => GetString("Faces");
        public string CannyThreshold => GetString("CannyThreshold");
        public string ThresholdSeparator => GetString("ThresholdSeparator");
        public string Apply => GetString("Apply");
        public string StartCamera => GetString("StartCamera");
        public string StopCamera => GetString("StopCamera");
        public string LoadImage => GetString("LoadImage");
        public string CameraStopped => GetString("CameraStopped");
        public string Minimize => GetString("Minimize");
        public string Maximize => GetString("Maximize");
        public string Close => GetString("Close");
        public string SourceType => GetString("SourceType");
        public string LocalCamera => GetString("LocalCamera");
        public string NetworkStream => GetString("NetworkStream");
        public string IPAddress => GetString("IPAddress");
        public string Port => GetString("Port");
        public string Connect => GetString("Connect");
        public string Disconnect => GetString("Disconnect");
        public string RTSPHint => GetString("RTSPHint");
        public string StatusDisconnected => GetString("StatusDisconnected");
        public string ProcessingMode => GetString("ProcessingMode");
        public string ModeCanny => GetString("ModeCanny");
        public string ModeSobel => GetString("ModeSobel");
        public string ModeLaplacian => GetString("ModeLaplacian");
        public string ModeBinary => GetString("ModeBinary");
        public string ModeContour => GetString("ModeContour");
        public string ModeQRCode => GetString("ModeQRCode");
        public string ModeColorDetection => GetString("ModeColorDetection");
        public string ModeTemplateMatch => GetString("ModeTemplateMatch");
        public string ModeShapeDetection => GetString("ModeShapeDetection");
        public string ModeFeatureMatch => GetString("ModeFeatureMatch");
        public string ModeEnhancement => GetString("ModeEnhancement");
        public string ResultView => GetString("ResultView");
        public string TargetColor => GetString("TargetColor");
        public string LoadTemplate => GetString("LoadTemplate");
        public string NoTemplate => GetString("NoTemplate");
        public string TemplateLoaded => GetString("TemplateLoaded");
        public string QRDecoded => GetString("QRDecoded");
        public string PickColorHint => GetString("PickColorHint");
        public string FeatureMatches => GetString("FeatureMatches");
        public string Industrial => GetString("Industrial");
        public string ModbusPLC => GetString("ModbusPLC");
        public string OpcUa => GetString("OpcUa");
        public string WorkReport => GetString("WorkReport");
        public string UnitId => GetString("UnitId");
        public string RegisterAddress => GetString("RegisterAddress");
        public string Value => GetString("Value");
        public string Read => GetString("Read");
        public string Write => GetString("Write");
        public string PlcLink => GetString("PlcLink");
        public string PlcCountRegister => GetString("PlcCountRegister");
        public string WorkOrder => GetString("WorkOrder");
        public string ScanSource => GetString("ScanSource");
        public string ScanCamera => GetString("ScanCamera");
        public string ScanSerial => GetString("ScanSerial");
        public string ScanTcp => GetString("ScanTcp");
        public string TodayCount => GetString("TodayCount");
        public string LastBarcode => GetString("LastBarcode");
        public string ExportCsv => GetString("ExportCsv");
        public string CameraReport => GetString("CameraReport");
        public string Start => GetString("Start");
        public string Stop => GetString("Stop");

        // ---- Section Headers ----
        public string SectionCamera => GetString("SectionCamera");
        public string SectionImageProcessing => GetString("SectionImageProcessing");
        public string SectionAIDetection => GetString("SectionAIDetection");
        public string SectionDigitalTwin => GetString("SectionDigitalTwin");
        public string SectionActivePerception => GetString("SectionActivePerception");
        public string SectionDetectionBreakdown => GetString("SectionDetectionBreakdown");
        public string SectionTrackingTrail => GetString("SectionTrackingTrail");
        public string SectionAWSS3 => GetString("SectionAWSS3");
        public string SectionAWSIoT => GetString("SectionAWSIoT");
        public string SectionAWSLambda => GetString("SectionAWSLambda");
        public string SectionModbusTCP => GetString("SectionModbusTCP");
        public string SectionOPCUA => GetString("SectionOPCUA");
        public string SectionBarcodeScanner => GetString("SectionBarcodeScanner");
        public string SectionWorkReport => GetString("SectionWorkReport");
        public string SectionApplicationLog => GetString("SectionApplicationLog");
        public string SectionResult => GetString("SectionResult");
        public string SectionTemplateFeature => GetString("SectionTemplateFeature");

        // ---- Field Labels ----
        public string FieldSource => GetString("FieldSource");
        public string FieldIP => GetString("FieldIP");
        public string FieldPort => GetString("FieldPort");
        public string FieldConfidence => GetString("FieldConfidence");
        public string FieldBucket => GetString("FieldBucket");
        public string FieldRegion => GetString("FieldRegion");
        public string FieldEndpoint => GetString("FieldEndpoint");
        public string FieldTopicPrefix => GetString("FieldTopicPrefix");
        public string FieldFunction => GetString("FieldFunction");
        public string FieldIPAddress => GetString("FieldIPAddress");
        public string FieldUnitID => GetString("FieldUnitID");
        public string FieldRegisterAddress => GetString("FieldRegisterAddress");
        public string FieldNodeID => GetString("FieldNodeID");
        public string FieldScanSource => GetString("FieldScanSource");
        public string FieldWorkOrder => GetString("FieldWorkOrder");
        public string FieldPLCRegister => GetString("FieldPLCRegister");
        public string FieldTodayCount => GetString("FieldTodayCount");
        public string FieldLastBarcode => GetString("FieldLastBarcode");
        public string FieldMaxTrailLength => GetString("MaxTrailLength");
        public string FieldMatchThreshold => GetString("MatchThreshold");
        public string FieldBaudRate => GetString("BaudRate");

        // ---- Status ----
        public string StatusNotLoaded => GetString("NotLoaded");
        public string StatusNotConfigured => GetString("NotConfigured");
        public string StatusNoSignal => GetString("NoSignal");
        public string StatusNoModelLoaded => GetString("NoModelLoaded");
        public string StatusNoDetections => GetString("NoDetections");
        public string StatusServicesNotInit => GetString("ServicesNotInit");

        // ---- Buttons ----
        public string BtnEnable => GetString("BtnEnable");
        public string BtnSendAlert => GetString("BtnSendAlert");
        public string BtnPublishStats => GetString("BtnPublishStats");
        public string BtnInvoke => GetString("BtnInvoke");
        public string BtnInitializeAll => GetString("BtnInitializeAll");
        public string BtnExportCSV => GetString("BtnExportCSV");
        public string BtnClear => GetString("BtnClear");
        public string BtnUploadScreenshot => GetString("BtnUploadScreenshot");
        public string BtnLoadModel => GetString("BtnLoadModel");
        public string StatusLoaded => GetString("StatusLoaded");
        public string NoUploads => GetString("NoUploads");
        public string NoBarcodeYet => GetString("NoBarcodeYet");
        public string Thresholds => GetString("Thresholds");
        public string Objects => GetString("Objects");
        public string EndpointUrl => GetString("EndpointUrl");
        public string StatDetections => GetString("StatDetections");
        public string StatTracks => GetString("StatTracks");
        public string StatInterval => GetString("StatInterval");
        public string StatDefects => GetString("StatDefects");
        public string StatPass => GetString("StatPass");
        public string StatusConnected => GetString("StatusConnected");
        public string StatusReady => GetString("StatusReady");
        public string StatusLoadFailed => GetString("StatusLoadFailed");
        public string StatusInitFailed => GetString("StatusInitFailed");
        public string PromptLoadModel => GetString("PromptLoadModel");
        public string PromptRegion => GetString("PromptRegion");
        public string PromptInitS3 => GetString("PromptInitS3");
        public string PromptNoScreenshot => GetString("PromptNoScreenshot");
        public string PromptInitIoT => GetString("PromptInitIoT");
        public string PromptInitLambda => GetString("PromptInitLambda");

        // ---- Other ----
        public string DisabledLabel => GetString("DisabledLabel");
        public string SerialPort => GetString("SerialPort");
        public string SerialConfig => GetString("SerialConfig");
        public string TCPConfig => GetString("TCPConfig");
        public string FrameCount => GetString("FrameCount");
        public string ActiveTracks => GetString("ActiveTracks");
        public string LostTracks => GetString("LostTracks");
        public string AdaptiveInterval => GetString("AdaptiveInterval");
        public string ROIRegions => GetString("ROIRegions");
        public string RecentUploads => GetString("RecentUploads");
        public string LastResult => GetString("LastResult");
        public string SectionPLCLink => GetString("SectionPLCLink");
        public string SettingsLabel => GetString("Settings");
        public string LanguageLabel => GetString("Language");
        public string DisplayLanguage => GetString("DisplayLanguage");
        public string Settings => GetString("Settings");
        public string About => GetString("About");

        // ---- Sidebar navigation (short labels + group headers) ----
        // 侧边栏宽度有限，导航项不能直接复用 SectionXxx 面板标题
        // （例如「AI 检测 (YOLO)」「AWS S3 存储」在 76px 折叠态下会被截断）。
        public string NavGroupWorkspace => GetString("NavGroupWorkspace");
        public string NavGroupSystem => GetString("NavGroupSystem");
        public string NavCamera => GetString("NavCamera");
        public string NavImage => GetString("NavImage");
        public string NavAI => GetString("NavAI");
        public string NavCloud => GetString("NavCloud");
        public string NavIndustrial => GetString("NavIndustrial");
        public string NavLog => GetString("NavLog");
        public string NavCollapse => GetString("NavCollapse");
        public string NavExpand => GetString("NavExpand");

        /// <summary>扫码源名称列表（供报工面板下拉框使用）</summary>
        public string[] ScanSourceNames => new[]
        {
            GetString("ScanCamera"), GetString("ScanSerial"), GetString("ScanTcp")
        };

        /// <summary>目标颜色名称列表（供颜色检测下拉框使用）</summary>
        public string[] ColorNames => new[]
        {
            GetString("ColorRed"), GetString("ColorGreen"), GetString("ColorBlue"),
            GetString("ColorYellow"), GetString("ColorOrange"), GetString("ColorPurple"),
            GetString("ColorCyan"), GetString("ColorWhite"), GetString("ColorBlack"),
            GetString("ColorCustom")
        };
        public string DualView => GetString("DualView");
        public string DualViewHint => GetString("DualViewHint");
        public string SaveScreenshot => GetString("SaveScreenshot");
        public string StartRecording => GetString("StartRecording");
        public string StopRecording => GetString("StopRecording");
        public string Recording => GetString("Recording");
        public string Log => GetString("Log");
        public string ClearLog => GetString("ClearLog");
        public string ScreenshotSaved => GetString("ScreenshotSaved");
        public string RecordingStarted => GetString("RecordingStarted");
        public string RecordingStopped => GetString("RecordingStopped");
        public string ProcessingTime => GetString("ProcessingTime");

        public string FormatCameraData(double fps, double width, double height)
        {
            return $"{fps:F1} FPS \u00B7 {width}x{height}";
        }

        public string GetConnectionStatusText(Components.ConnectionState state)
        {
            return state switch
            {
                Components.ConnectionState.Connected => GetString("StatusConnected"),
                Components.ConnectionState.Connecting => GetString("StatusConnecting"),
                Components.ConnectionState.Failed => GetString("StatusFailed"),
                _ => GetString("StatusDisconnected")
            };
        }

        public string GetString(string key)
        {
            string? value = _resourceManager.GetString(key);
            return value ?? key;
        }

        public string GetString(string key, params object[] args)
        {
            string? value = _resourceManager.GetString(key);
            return value != null ? string.Format(value, args) : key;
        }

        public static string GetStringStatic(string key)
        {
            string? value = _resourceManager.GetString(key);
            return value ?? key;
        }
    }
}
