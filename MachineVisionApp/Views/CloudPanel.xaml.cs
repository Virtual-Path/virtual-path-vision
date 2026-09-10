using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MachineVisionApp.Views;

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
    }

    // ── Public Methods ───────────────────────────────────────────────

    public void SetS3Status(bool connected, string text)
    {
        S3StatusDot.Fill = connected
            ? (Brush)FindResource("SuccessBrush")
            : (Brush)FindResource("TextMutedBrush");

        S3StatusText.Text = text;
        CloudUploadScreenshotButton.IsEnabled = connected;
    }

    public void SetIoTStatus(bool connected, string text)
    {
        IoTStatusDot.Fill = connected
            ? (Brush)FindResource("SuccessBrush")
            : (Brush)FindResource("TextMutedBrush");

        IoTStatusText.Text = text;
        CloudAlertButton.IsEnabled = connected;
        CloudPublishStatsButton.IsEnabled = connected;
    }

    public void SetLambdaStatus(bool connected, string text)
    {
        LambdaStatusDot.Fill = connected
            ? (Brush)FindResource("SuccessBrush")
            : (Brush)FindResource("TextMutedBrush");

        LambdaStatusText.Text = text;
        LambdaInvokeButton.IsEnabled = connected;
    }

    public void SetCloudOverallStatus(string text)
    {
        CloudOverallStatus.Text = text;

        bool anyConnected =
            S3StatusDot.Fill == (Brush)FindResource("SuccessBrush") ||
            IoTStatusDot.Fill == (Brush)FindResource("SuccessBrush") ||
            LambdaStatusDot.Fill == (Brush)FindResource("SuccessBrush");

        CloudOverallStatus.Foreground = anyConnected
            ? (Brush)FindResource("SuccessBrush")
            : (Brush)FindResource("TextMutedBrush");
    }

    public void AddUploadRecord(string key)
    {
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
