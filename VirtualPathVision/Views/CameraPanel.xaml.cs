using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using VirtualPathVision.Components;

namespace VirtualPathVision.Views;

public partial class CameraPanel : UserControl
{
    /// <summary>
    /// 最近一次的连接状态。状态徽标文案由 XAML 里的英文默认值起步，
    /// 语言切换时需要按这个状态重绘，否则中文界面仍显示 "Disconnected"。
    /// </summary>
    private ConnectionState _connectionState = ConnectionState.Disconnected;

    // ── Exposed Elements ─────────────────────────────────────────────
    public Ellipse ConnectionDotEl => ConnectionDot;
    public TextBlock ConnectionStatusTextEl => ConnectionStatusText;
    public ComboBox SourceTypeComboBoxEl => SourceTypeComboBox;
    public Border NetworkConfigPanelEl => NetworkConfigPanel;
    public TextBox IPTextBoxEl => IPTextBox;
    public TextBox PortTextBoxEl => PortTextBox;
    public Button ConnectButtonEl => ConnectButton;
    public Button DisconnectButtonEl => DisconnectButton;
    public Button LoadImageButtonEl => LoadImageButton;
    public Button StartCameraButtonEl => StartCameraButton;
    public Button StopCameraButtonEl => StopCameraButton;
    public Button SaveScreenshotButtonEl => SaveScreenshotButton;
    public Button RecordButtonEl => RecordButton;
    public Image OriginalImageEl => OriginalImage;
    public Image EdgeImageEl => EdgeImage;
    public StackPanel EmptyOverlayLeftEl => EmptyOverlayLeft;
    public StackPanel EmptyOverlayRightEl => EmptyOverlayRight;
    public TextBlock FpsTextBlockEl => FpsTextBlock;
    public TextBlock ProcessTimeTextBlockEl => ProcessTimeTextBlock;
    public TextBlock SourceInfoTextEl => SourceInfoText;

    // ── Events ───────────────────────────────────────────────────────
    public event EventHandler? ConnectRequested;
    public event EventHandler? DisconnectRequested;
    public event EventHandler? LoadImageRequested;
    public event EventHandler? StartCameraRequested;
    public event EventHandler? StopCameraRequested;
    public event EventHandler? ScreenshotRequested;
    public event EventHandler? RecordRequested;

    public CameraPanel()
    {
        InitializeComponent();
    }

    // ── Public Helpers ───────────────────────────────────────────────
    public void RefreshTexts()
    {
        var t = TranslationService.Instance;
        ConnectButton.Content = t.Connect;
        DisconnectButton.Content = t.Disconnect;
        LoadImageButton.Content = t.LoadImage;
        StartCameraButton.Content = t.StartCamera;
        StopCameraButton.Content = t.StopCamera;
        SaveScreenshotButton.Content = t.SaveScreenshot;
        RecordButton.Content = t.StartRecording;
        DualViewLabelText.Text = t.DualView;
        DualViewToggle.ToolTip = t.DualViewHint;
        SectionTitleText.Text = t.SectionCamera;
        SourceLabel.Text = t.FieldSource;
        CameraIpLabel.Text = t.FieldIP;
        NoSignalOriginalText.Text = t.StatusNoSignal;
        NoSignalEdgeText.Text = t.StatusNoSignal;
        ConnectionStatusText.Text = t.GetConnectionStatusText(_connectionState);
    }

    /// <summary>按当前连接状态刷新指示灯、状态徽标与按钮可用性</summary>
    public void SetConnected(ConnectionState state)
    {
        _connectionState = state;
        bool connected = state == ConnectionState.Connected;

        if (ConnectionDot == null || ConnectionStatusText == null || ConnectButton == null || DisconnectButton == null || StartCameraButton == null) return;
        ConnectionStatusText.Text = TranslationService.Instance.GetConnectionStatusText(state);
        ConnectionDot.Fill = connected
            ? (Brush)FindResource("SuccessBrush")
            : (Brush)FindResource("DangerBrush");

        ConnectButton.IsEnabled = !connected;
        DisconnectButton.IsEnabled = connected;
        StartCameraButton.IsEnabled = connected;
    }

    // ── Event Handlers ───────────────────────────────────────────────
    /// <summary>
    /// 双视图开关：默认仅显示单屏，用户开启后才显示第二个预览面板。
    /// </summary>
    private void DualViewToggle_Click(object sender, RoutedEventArgs e)
    {
        // 与下方两处保持一致的 null 检查风格。InitializeComponent 生成的字段不会为 null，
        // 但 DualViewToggle 此前完全没有保护，异常会逃逸到全局异常处理器。
        bool enabled = DualViewToggle != null && DualViewToggle.IsChecked == true;
        if (SecondViewBorder != null)
            SecondViewBorder.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        if (SecondViewColumn != null)
            SecondViewColumn.Width = enabled ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
    }

    private void SourceTypeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NetworkConfigPanel == null) return;
        if (SourceTypeComboBox.SelectedIndex == 1)
        {
            NetworkConfigPanel.Visibility = Visibility.Visible;
        }
        else
        {
            NetworkConfigPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        ConnectRequested?.Invoke(this, EventArgs.Empty);
    }

    private void DisconnectButton_Click(object sender, RoutedEventArgs e)
    {
        DisconnectRequested?.Invoke(this, EventArgs.Empty);
    }

    private void LoadImageButton_Click(object sender, RoutedEventArgs e)
    {
        LoadImageRequested?.Invoke(this, EventArgs.Empty);
    }

    private void StartCameraButton_Click(object sender, RoutedEventArgs e)
    {
        StartCameraRequested?.Invoke(this, EventArgs.Empty);
    }

    private void StopCameraButton_Click(object sender, RoutedEventArgs e)
    {
        StopCameraRequested?.Invoke(this, EventArgs.Empty);
    }

    private void SaveScreenshotButton_Click(object sender, RoutedEventArgs e)
    {
        ScreenshotRequested?.Invoke(this, EventArgs.Empty);
    }

    private void RecordButton_Click(object sender, RoutedEventArgs e)
    {
        RecordRequested?.Invoke(this, EventArgs.Empty);
    }
}
