using System;
using System.Windows;
using System.Windows.Controls;

namespace MachineVisionApp.Views;

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

        Threshold1TextBox.Text = ((int)Threshold1Slider.Value).ToString();
        ThresholdsChanged?.Invoke((int)Threshold1Slider.Value, (int)Threshold2Slider.Value);
    }

    private void Threshold2Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents || Threshold2TextBox == null || Threshold1Slider == null) return;

        Threshold2TextBox.Text = ((int)Threshold2Slider.Value).ToString();
        ThresholdsChanged?.Invoke((int)Threshold1Slider.Value, (int)Threshold2Slider.Value);
    }

    private void ApplyThresholdsButton_Click(object sender, RoutedEventArgs e)
    {
        ThresholdsChanged?.Invoke((int)Threshold1Slider.Value, (int)Threshold2Slider.Value);
    }

    private void ProcessingModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || ThresholdPanel == null || ColorPanel == null || TemplatePanel == null || ModeLabelText == null) return;

        int index = ProcessingModeComboBox.SelectedIndex;
        if (index < 0 || index >= ProcessingModeComboBox.Items.Count) return;
        string mode = ProcessingModeComboBox.Items[index] is ComboBoxItem item
            ? item.Content?.ToString() ?? ""
            : "";

        ModeLabelText.Text = mode;

        bool showThreshold = mode is "Canny" or "Contour";
        ThresholdPanel.Visibility = showThreshold ? Visibility.Visible : Visibility.Collapsed;

        ColorPanel.Visibility = mode == "Color Detection" ? Visibility.Visible : Visibility.Collapsed;
        PickColorHintText.Visibility = mode == "Color Detection" ? Visibility.Visible : Visibility.Collapsed;

        TemplatePanel.Visibility = mode is "Template Match" or "Feature Match"
            ? Visibility.Visible
            : Visibility.Collapsed;

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
