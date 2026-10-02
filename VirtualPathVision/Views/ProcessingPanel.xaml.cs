using System;
using System.Windows;
using System.Windows.Controls;

namespace VirtualPathVision.Views;

public partial class ProcessingPanel : UserControl
{
    // ── Exposed Elements ─────────────────────────────────────────────
    public ComboBox ProcessingModeComboBoxEl => ProcessingModeComboBox;
    public TextBlock ModeLabelTextEl => ModeLabelText;
    public Border ThresholdPanelEl => ThresholdPanel;
    public Slider Threshold1SliderEl => Threshold1Slider;
    public Slider Threshold2SliderEl => Threshold2Slider;
    public TextBox Threshold1TextBoxEl => Threshold1TextBox;
    public TextBox Threshold2TextBoxEl => Threshold2TextBox;
    public Button ApplyThresholdsButtonEl => ApplyThresholdsButton;
    public Border ColorPanelEl => ColorPanel;
    public ComboBox ColorComboBoxEl => ColorComboBox;
    public TextBlock PickColorHintTextEl => PickColorHintText;
    public Border TemplatePanelEl => TemplatePanel;
    public Button LoadTemplateButtonEl => LoadTemplateButton;
    public TextBlock TemplateStatusTextEl => TemplateStatusText;
    public TextBlock FeatureMatchTextEl => FeatureMatchText;
    public TextBlock ResultTitleTextEl => ResultTitleText;
    public TextBlock FaceCountTextBlockEl => FaceCountTextBlock;
    public TextBlock ContourCountTextBlockEl => ContourCountTextBlock;
    public TextBlock ThresholdInfoTextEl => ThresholdInfoText;
    public TextBlock ModeResultTextBlockEl => ModeResultTextBlock;

    // ── Events ───────────────────────────────────────────────────────
    public event Action<int, int>? ThresholdsChanged;
    public event EventHandler? LoadTemplateRequested;
    public event Action<int>? ModeChanged;
    public event Action<int>? ColorChanged;

    private bool _suppressEvents;

    public ProcessingPanel()
    {
        _suppressEvents = true;
        InitializeComponent();
        _suppressEvents = false;
    }

    // ── Public Helpers ───────────────────────────────────────────────
    public void SetModeName(string name)
    {
        ModeLabelText.Text = name;
    }

    public void RefreshTexts()
    {
        var t = TranslationService.Instance;
        ApplyThresholdsButton.Content = t.Apply;
        LoadTemplateButton.Content = t.LoadTemplate;
        PickColorHintText.Text = t.PickColorHint;
        SectionProcessingTitle.Text = t.SectionImageProcessing;
        TemplateFeatureTitle.Text = t.SectionTemplateFeature;
        ResultTitle.Text = t.SectionResult;
        ThresholdsLabel.Text = t.Thresholds;
        ColorDetectionLabel.Text = t.ModeColorDetection;
        FacesLabel.Text = t.Faces;
        ObjectsLabel.Text = t.Objects;
    }

    public void SetResultTitle(string title)
    {
        ResultTitleText.Text = title;
    }

    public void SetTemplateStatus(string status)
    {
        TemplateStatusText.Text = status;
    }

    public void UpdateResults(int faceCount, int contourCount, string thresholdInfo, string modeResult)
    {
        FaceCountTextBlock.Text = faceCount.ToString();
        ContourCountTextBlock.Text = contourCount.ToString();
        ThresholdInfoText.Text = thresholdInfo;
        ModeResultTextBlock.Text = modeResult;
    }

    // ── Event Handlers ───────────────────────────────────────────────
    private void Threshold1Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents || Threshold1TextBox == null || Threshold2Slider == null) return;

        Threshold1TextBox.Text = ((int)Threshold1Slider.Value).ToString(System.Globalization.CultureInfo.InvariantCulture);
        ThresholdsChanged?.Invoke((int)Threshold1Slider.Value, (int)Threshold2Slider.Value);
    }

    private void Threshold2Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents || Threshold2TextBox == null || Threshold1Slider == null) return;

        Threshold2TextBox.Text = ((int)Threshold2Slider.Value).ToString(System.Globalization.CultureInfo.InvariantCulture);
        ThresholdsChanged?.Invoke((int)Threshold1Slider.Value, (int)Threshold2Slider.Value);
    }

    // 注意：Apply 按钮的点击已不在本面板处理。
    // 它由 ThresholdParameterComponent 统一订阅（MainWindow 构造时注入），
    // 因为只有那里会做范围与大小关系校验。
    // 此前本面板另有一个无校验的 Click 处理器，导致输入非法时
    // 一边弹错误提示、一边又按滑块值静默生效。

    /// <summary>
    /// 处理模式下拉框变化。
    ///
    /// 旧实现依赖下拉项的显示文本判断该显示哪些面板，但下拉项是本地化后的
    /// <see cref="string"/>（MainWindow.RefreshLocalizedControls 统一赋值），
    /// 因此取到的 mode 恒为空串，整段可见性判断永远是 False。
    /// 这里改为直接依据下拉索引（与 ProcessingMode 枚举顺序一致）判断，
    /// 与语言无关。
    /// </summary>
    private void ProcessingModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || ThresholdPanel == null || ColorPanel == null || TemplatePanel == null || ModeLabelText == null) return;

        int index = ProcessingModeComboBox.SelectedIndex;
        if (index < 0 || index >= ProcessingModeComboBox.Items.Count) return;

        // 索引与 MainWindow.ProcessingPanelCtrl_ModeChanged 的映射保持一致
        var mode = index switch
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

        // 形状识别内部同样使用 Canny，故一并开放阈值调节
        bool showThreshold = mode is Components.ProcessingMode.Canny
            or Components.ProcessingMode.Contour
            or Components.ProcessingMode.ShapeDetection;
        ThresholdPanel.Visibility = showThreshold ? Visibility.Visible : Visibility.Collapsed;

        bool isColorMode = mode == Components.ProcessingMode.ColorDetection;
        ColorPanel.Visibility = isColorMode ? Visibility.Visible : Visibility.Collapsed;
        PickColorHintText.Visibility = isColorMode ? Visibility.Visible : Visibility.Collapsed;

        TemplatePanel.Visibility = mode is Components.ProcessingMode.TemplateMatch
            or Components.ProcessingMode.FeatureMatch
            ? Visibility.Visible
            : Visibility.Collapsed;

        // 显示名由 MainWindow 通过 SetModeName 统一设置（使用当前语言的资源），
        // 此处不再用可能为空的显示文本覆盖它。
        ModeChanged?.Invoke(index);
    }

    private void ColorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;

        ColorChanged?.Invoke(ColorComboBox.SelectedIndex);
    }

    private void LoadTemplateButton_Click(object sender, RoutedEventArgs e)
    {
        LoadTemplateRequested?.Invoke(this, EventArgs.Empty);
    }
}
