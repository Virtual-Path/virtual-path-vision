using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace VirtualPathVision.Views;

public partial class CloudPanel : UserControl
{
    // ── Exposed Elements ─────────────────────────────────────────────

    // S3
    public Ellipse S3StatusDotEl => S3StatusDot;
    public TextBlock S3StatusTextEl => S3StatusText;
    public TextBox S3BucketTextBoxEl => S3BucketTextBox;
    public TextBox AwsRegionTextBoxEl => AwsRegionTextBox;
    public Button CloudUploadScreenshotButtonEl => CloudUploadScreenshotButton;
    public TextBlock S3UploadStatusTextEl => S3UploadStatusText;
    public TextBlock S3UploadListTextEl => S3UploadListText;

    // IoT
    public Ellipse IoTStatusDotEl => IoTStatusDot;
    public TextBlock IoTStatusTextEl => IoTStatusText;
    public TextBox IoTEndpointTextBoxEl => IoTEndpointTextBox;
    public TextBox IoTTopicTextBoxEl => IoTTopicTextBox;
    public Button CloudAlertButtonEl => CloudAlertButton;
    public Button CloudPublishStatsButtonEl => CloudPublishStatsButton;
    public TextBlock IoTConnectionStatusTextEl => IoTConnectionStatusText;

    // Lambda
    public Ellipse LambdaStatusDotEl => LambdaStatusDot;
    public TextBlock LambdaStatusTextEl => LambdaStatusText;
    public TextBox LambdaFuncTextBoxEl => LambdaFuncTextBox;
    public Button LambdaInvokeButtonEl => LambdaInvokeButton;
    public TextBlock LambdaResultTextEl => LambdaResultText;

    // Overall
    public Button CloudInitButtonEl => CloudInitButton;
    public TextBlock CloudOverallStatusEl => CloudOverallStatus;

    // ── Events ───────────────────────────────────────────────────────

    public event EventHandler? InitRequested;
    public event EventHandler? UploadScreenshotRequested;
    public event EventHandler? SendAlertRequested;
    public event EventHandler? PublishStatsRequested;
    public event EventHandler? LambdaInvokeRequested;

    // ── Upload history buffer ────────────────────────────────────────

    private readonly System.Collections.Generic.List<string> _uploadHistory = new();

    // ── Constructor ──────────────────────────────────────────────────

    public CloudPanel()
    {
        InitializeComponent();

        CloudInitButton.Click += CloudInitButton_Click;
        CloudUploadScreenshotButton.Click += CloudUploadScreenshotButton_Click;
        CloudAlertButton.Click += CloudAlertButton_Click;
        CloudPublishStatsButton.Click += CloudPublishStatsButton_Click;
        LambdaInvokeButton.Click += LambdaInvokeButton_Click;

        SizeChanged += CloudPanel_SizeChanged;
    }

    /// <summary>自适应：窄窗口时三列改为纵向堆叠。</summary>
    private void CloudPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool narrow = e.NewSize.Width < 1080;
        if (narrow)
        {
            CloudCol2.Width = new GridLength(0);
            CloudCol4.Width = new GridLength(0);
            Grid.SetColumn(S3Card, 0); Grid.SetRow(S3Card, 0);
            S3Card.Margin = new Thickness(0, 0, 0, 12);
            Grid.SetColumn(IoTCard, 0); Grid.SetRow(IoTCard, 1);
            IoTCard.Margin = new Thickness(0, 0, 0, 12);
            Grid.SetColumn(LambdaCard, 0); Grid.SetRow(LambdaCard, 2);
            LambdaCard.Margin = new Thickness(0, 0, 0, 12);
            Grid.SetColumn(InitCard, 0); Grid.SetColumnSpan(InitCard, 1); Grid.SetRow(InitCard, 3);
            InitCard.Margin = new Thickness(0);
        }
        else
        {
            CloudCol2.Width = new GridLength(1, GridUnitType.Star);
            CloudCol4.Width = new GridLength(1, GridUnitType.Star);
            Grid.SetColumn(S3Card, 0); Grid.SetRow(S3Card, 0);
            S3Card.Margin = new Thickness(0, 0, 12, 0);
            Grid.SetColumn(IoTCard, 2); Grid.SetRow(IoTCard, 0);
            IoTCard.Margin = new Thickness(12, 0, 12, 0);
            Grid.SetColumn(LambdaCard, 4); Grid.SetRow(LambdaCard, 0);
            LambdaCard.Margin = new Thickness(12, 0, 0, 0);
            Grid.SetColumn(InitCard, 0); Grid.SetColumnSpan(InitCard, 5); Grid.SetRow(InitCard, 1);
            InitCard.Margin = new Thickness(0, 12, 0, 0);
        }
    }

    // ── Public Methods ───────────────────────────────────────────────

    // ── 状态（语言切换时用于恢复） ────────────────────────────────────
    private bool _s3Connected;
    private bool _iotConnected;
    private bool _lambdaConnected;
    private bool _uploadsEmpty = true;

    public void RefreshTexts()
    {
        var t = TranslationService.Instance;

        // 按钮
        CloudUploadScreenshotButton.Content = t.BtnUploadScreenshot;
        CloudAlertButton.Content = t.BtnSendAlert;
        CloudPublishStatsButton.Content = t.BtnPublishStats;
        LambdaInvokeButton.Content = t.BtnInvoke;
        CloudInitButton.Content = t.BtnInitializeAll;

        // 分节标题
        SectionS3Title.Text = t.SectionAWSS3;
        SectionIoTTitle.Text = t.SectionAWSIoT;
        SectionLambdaTitle.Text = t.SectionAWSLambda;

        // 字段标签
        BucketLabel.Text = t.FieldBucket;
        RegionLabel.Text = t.FieldRegion;
        RecentUploadsLabel.Text = t.RecentUploads;
        EndpointLabel.Text = t.FieldEndpoint;
        TopicPrefixLabel.Text = t.FieldTopicPrefix;
        FunctionLabel.Text = t.FieldFunction;
        LastResultLabel.Text = t.LastResult;

        // 状态：无论是否已连接都要重绘。
        // 旧实现只在「未连接」时刷新，已连接时保留上一次写入的文本，
        // 于是切换语言后状态标签仍是旧语言（中文界面显示「已连接」）。
        S3StatusText.Text = _s3Connected ? t.StatusConnected : t.StatusNotConfigured;
        IoTStatusText.Text = _iotConnected ? t.StatusConnected : t.StatusNotConfigured;
        LambdaStatusText.Text = _lambdaConnected ? t.StatusReady : t.StatusNotConfigured;

        bool anyConnected = _s3Connected || _iotConnected || _lambdaConnected;
        CloudOverallStatus.Text = anyConnected ? t.StatusConnected : t.StatusServicesNotInit;
        CloudOverallStatus.Foreground = anyConnected
            ? (Brush)FindResource("SuccessBrush")
            : (Brush)FindResource("TextMutedBrush");

        if (_uploadsEmpty) S3UploadListText.Text = t.NoUploads;
    }

    public void SetS3Status(bool connected, string text)
    {
        _s3Connected = connected;
        S3StatusDot.Fill = connected
            ? (Brush)FindResource("SuccessBrush")
            : (Brush)FindResource("TextMutedBrush");

        S3StatusText.Text = text;
        CloudUploadScreenshotButton.IsEnabled = connected;
    }

    public void SetIoTStatus(bool connected, string text)
    {
        _iotConnected = connected;
        IoTStatusDot.Fill = connected
            ? (Brush)FindResource("SuccessBrush")
            : (Brush)FindResource("TextMutedBrush");

        IoTStatusText.Text = text;
        CloudAlertButton.IsEnabled = connected;
        CloudPublishStatsButton.IsEnabled = connected;
    }

    public void SetLambdaStatus(bool connected, string text)
    {
        _lambdaConnected = connected;
        LambdaStatusDot.Fill = connected
            ? (Brush)FindResource("SuccessBrush")
            : (Brush)FindResource("TextMutedBrush");

        LambdaStatusText.Text = text;
        LambdaInvokeButton.IsEnabled = connected;
    }

    public void SetCloudOverallStatus(string text)
    {
        CloudOverallStatus.Text = text;

        // 用 _s3Connected/_iotConnected/_lambdaConnected 字段判断，
        // 不要拿指示点的 Fill 去和 FindResource 的结果做引用比较：
        // 主题切换会替换资源字典里的画刷实例，引用比较必然失配，
        // 导致服务明明已连接却显示成未连接。
        bool anyConnected = _s3Connected || _iotConnected || _lambdaConnected;

        CloudOverallStatus.Foreground = anyConnected
            ? (Brush)FindResource("SuccessBrush")
            : (Brush)FindResource("TextMutedBrush");
    }

    public void AddUploadRecord(string key)
    {
        _uploadsEmpty = false;
        _uploadHistory.Insert(0, key);
        if (_uploadHistory.Count > 20)
            _uploadHistory.RemoveRange(20, _uploadHistory.Count - 20);

        S3UploadListText.Text = string.Join("\n", _uploadHistory);
        S3UploadListText.Foreground = (Brush)FindResource("TextSecondaryBrush");
    }

    // ── Event Handlers ───────────────────────────────────────────────

    private void CloudInitButton_Click(object sender, RoutedEventArgs e)
    {
        InitRequested?.Invoke(this, EventArgs.Empty);
    }

    private void CloudUploadScreenshotButton_Click(object sender, RoutedEventArgs e)
    {
        UploadScreenshotRequested?.Invoke(this, EventArgs.Empty);
    }

    private void CloudAlertButton_Click(object sender, RoutedEventArgs e)
    {
        SendAlertRequested?.Invoke(this, EventArgs.Empty);
    }

    private void CloudPublishStatsButton_Click(object sender, RoutedEventArgs e)
    {
        PublishStatsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void LambdaInvokeButton_Click(object sender, RoutedEventArgs e)
    {
        LambdaInvokeRequested?.Invoke(this, EventArgs.Empty);
    }
}
