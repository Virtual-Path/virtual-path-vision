using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace VirtualPathVision.Views;

public partial class AIPanel : UserControl
{
    // ── Exposed Elements ─────────────────────────────────────────────
    public Ellipse AiStatusDotEl => AiStatusDot;
    public TextBlock AiStatusTextEl => AiStatusText;
    public Button LoadYoloModelButtonEl => LoadYoloModelButton;
    public TextBlock YoloModelPathTextEl => YoloModelPathText;
    public TextBox ConfThresholdTextBoxEl => ConfThresholdTextBox;
    public CheckBox AiEnableCheckBoxEl => AiEnableCheckBox;
    public TextBlock AiDetectionsTextEl => AiDetectionsText;
    public TextBlock AiTracksTextEl => AiTracksText;
    public TextBlock AiSampleTextEl => AiSampleText;
    public TextBlock TwinStatusTextEl => TwinStatusText;
    public Image DigitalTwinImageEl => DigitalTwinImage;
    public TextBlock TwinDetectionsTextEl => TwinDetectionsText;
    public TextBlock TwinDefectsTextEl => TwinDefectsText;
    public TextBlock TwinPassRateTextEl => TwinPassRateText;
    public TextBlock AiFrameCountTextEl => AiFrameCountText;
    public TextBlock AiActiveTracksTextEl => AiActiveTracksText;
    public TextBlock AiLostTracksTextEl => AiLostTracksText;
    public TextBlock AiIntervalTextEl => AiIntervalText;
    public TextBlock AiRoiCountTextEl => AiRoiCountText;
    public Border DetectionBreakdownPanelEl => DetectionBreakdownPanel;
    public TextBlock DetectionBreakdownTextEl => DetectionBreakdownText;
    public TextBox MaxTrailTextBoxEl => MaxTrailTextBox;
    public TextBox MatchThresholdTextBoxEl => MatchThresholdTextBox;

    // ── Events ───────────────────────────────────────────────────────
    public event EventHandler? LoadModelRequested;
    public event EventHandler? EnableChanged;
    public event Action<float>? ConfidenceChanged;

    public AIPanel()
    {
        InitializeComponent();

        LoadYoloModelButton.Click += LoadYoloModelButton_Click;
        AiEnableCheckBox.Checked += AiEnableCheckBox_Changed;
        AiEnableCheckBox.Unchecked += AiEnableCheckBox_Changed;
        ConfThresholdTextBox.LostFocus += ConfThresholdTextBox_LostFocus;

        SizeChanged += AIPanel_SizeChanged;
    }

    /// <summary>自适应：窄窗口时左右两列改为上下堆叠。</summary>
    private void AIPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool narrow = e.NewSize.Width < 900;
        if (narrow)
        {
            AiColRight.Width = new GridLength(0);
            Grid.SetColumn(LeftColumn, 0);
            Grid.SetRow(LeftColumn, 0);
            LeftColumn.Margin = new Thickness(0, 0, 0, 14);
            Grid.SetColumn(RightColumn, 0);
            Grid.SetRow(RightColumn, 1);
            RightColumn.Margin = new Thickness(0);
        }
        else
        {
            AiColRight.Width = new GridLength(1, GridUnitType.Star);
            Grid.SetColumn(LeftColumn, 0);
            Grid.SetRow(LeftColumn, 0);
            LeftColumn.Margin = new Thickness(0, 0, 12, 0);
            Grid.SetColumn(RightColumn, 2);
            Grid.SetRow(RightColumn, 0);
            RightColumn.Margin = new Thickness(12, 0, 0, 0);
        }
    }

    // ── 状态（语言切换时用于恢复） ────────────────────────────────────
    private bool _modelLoaded;
    private bool _breakdownEmpty = true;
    private int _detections;
    private int _tracks;
    private int _interval = 1;
    private int _twinDetections;
    private int _twinDefects;
    private double _twinPassRate = 100;

    // ── Public Helpers ───────────────────────────────────────────────

    public void RefreshTexts()
    {
        var t = TranslationService.Instance;
        LoadYoloModelButton.Content = t.BtnLoadModel;
        AiEnableCheckBox.Content = t.BtnEnable;

        SectionAITitle.Text = t.SectionAIDetection;
        SectionDigitalTwinTitle.Text = t.SectionDigitalTwin;
        SectionActivePerceptionTitle.Text = t.SectionActivePerception;
        SectionBreakdownTitle.Text = t.SectionDetectionBreakdown;
        SectionTrackingTitle.Text = t.SectionTrackingTrail;

        // 字段标签
        ConfidenceLabel.Text = t.FieldConfidence;
        FrameCountLabel.Text = t.FrameCount;
        ActiveTracksLabel.Text = t.ActiveTracks;
        LostTracksLabel.Text = t.LostTracks;
        AdaptiveIntervalLabel.Text = t.AdaptiveInterval;
        RoiRegionsLabel.Text = t.ROIRegions;
        MaxTrailLabel.Text = t.FieldMaxTrailLength;
        MatchThresholdLabel.Text = t.FieldMatchThreshold;

        // 状态（保留当前是否已加载）
        SetModelStatus(_modelLoaded, YoloModelPathText.Text);
        if (!_modelLoaded)
            YoloModelPathText.Text = t.StatusNoModelLoaded;
        if (_breakdownEmpty)
            DetectionBreakdownText.Text = t.StatusNoDetections;

        // 动态统计文本按当前语言重绘
        RenderDetectionStats();
        RenderTwinStats();
    }

    public void SetModelStatus(bool loaded, string path)
    {
        _modelLoaded = loaded;
        AiStatusDot.Fill = loaded
            ? (Brush)FindResource("SuccessBrush")
            : (Brush)FindResource("TextMutedBrush");

        AiStatusText.Text = loaded
            ? TranslationService.Instance.StatusLoaded
            : TranslationService.Instance.StatusNotLoaded;
        YoloModelPathText.Text = path;
        YoloModelPathText.Foreground = loaded
            ? (Brush)FindResource("TextSecondaryBrush")
            : (Brush)FindResource("TextMutedBrush");
    }

    public void UpdateDetectionStats(int detections, int tracks, int interval)
    {
        _detections = detections;
        _tracks = tracks;
        _interval = interval;
        RenderDetectionStats();
    }

    public void UpdateTwinStats(int detections, int defects, double passRate)
    {
        _twinDetections = detections;
        _twinDefects = defects;
        _twinPassRate = passRate;
        RenderTwinStats();
    }

    /// <summary>按当前语言渲染检测/孪生统计文本（更新与语言切换共用）</summary>
    private void RenderDetectionStats()
    {
        var t = TranslationService.Instance;
        AiDetectionsText.Text = string.Format(t.StatDetections, _detections);
        AiTracksText.Text = string.Format(t.StatTracks, _tracks);
        AiSampleText.Text = string.Format(t.StatInterval, _interval);
    }

    private void RenderTwinStats()
    {
        var t = TranslationService.Instance;
        TwinDetectionsText.Text = string.Format(t.StatDetections, _twinDetections);
        TwinDefectsText.Text = string.Format(t.StatDefects, _twinDefects);
        TwinPassRateText.Text = string.Format(t.StatPass, _twinPassRate.ToString("F0"));
    }

    public void UpdatePerceptionStats(int frameCount, int activeTracks, int lostTracks, int interval, int roiCount)
    {
        AiFrameCountText.Text = frameCount.ToString();
        AiActiveTracksText.Text = activeTracks.ToString();
        AiLostTracksText.Text = lostTracks.ToString();
        AiIntervalText.Text = interval.ToString();
        AiRoiCountText.Text = roiCount.ToString();
    }

    public void UpdateDetectionBreakdown(Dictionary<string, int> classCounts)
    {
        if (classCounts == null || classCounts.Count == 0)
        {
            _breakdownEmpty = true;
            DetectionBreakdownText.Text = TranslationService.Instance.StatusNoDetections;
            DetectionBreakdownText.Foreground = (Brush)FindResource("TextMutedBrush");
            return;
        }

        _breakdownEmpty = false;
        DetectionBreakdownText.Foreground = (Brush)FindResource("TextSecondaryBrush");

        var lines = new List<string>();
        foreach (var kvp in classCounts)
        {
            lines.Add($"{kvp.Key}: {kvp.Value}");
        }

        DetectionBreakdownText.Text = string.Join("\n", lines);
    }

    // ── Event Handlers ───────────────────────────────────────────────

    private void LoadYoloModelButton_Click(object sender, RoutedEventArgs e)
    {
        LoadModelRequested?.Invoke(this, EventArgs.Empty);
    }

    private void AiEnableCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        EnableChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ConfThresholdTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (TryReadConfidence(ConfThresholdTextBox.Text, out float value))
        {
            value = Math.Clamp(value, 0f, 1f);
            _lastConfidence = value;
            ConfThresholdTextBox.Text = value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            ConfidenceChanged?.Invoke(value);
        }
        else
        {
            // 解析失败时恢复上一次的有效值，而不是硬编码回 0.5
            ConfThresholdTextBox.Text = _lastConfidence
                .ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private float _lastConfidence = 0.5f;

    /// <summary>
    /// 解析置信度输入。
    /// 先按不变文化解析（本面板写回时用的是不变文化），
    /// 再回退到当前文化，以兼容用户直接键入本地化小数分隔符的情况。
    /// </summary>
    private static bool TryReadConfidence(string? text, out float value)
    {
        value = 0.5f;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string t = text.Trim();
        if (float.TryParse(t, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out value))
            return true;

        return float.TryParse(t, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.CurrentCulture, out value);
    }

    /// <summary>把外部设置写入置信度输入框（加载模型后回填）</summary>
    public void SetConfidence(float value)
    {
        _lastConfidence = Math.Clamp(value, 0f, 1f);
        ConfThresholdTextBox.Text = _lastConfidence
            .ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>把外部设置写入模板匹配阈值输入框</summary>
    public void SetMatchThreshold(float value)
    {
        MatchThresholdTextBox.Text = value.ToString("0.##",
            System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>把外部设置写入「最大丢失帧数」输入框</summary>
    public void SetMaxTrail(int value)
    {
        MaxTrailTextBox.Text = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
