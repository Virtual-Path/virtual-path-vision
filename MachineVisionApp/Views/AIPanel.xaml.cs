using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MachineVisionApp.Views;

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
    }

    // ── Public Helpers ───────────────────────────────────────────────

    public void SetModelStatus(bool loaded, string path)
    {
        AiStatusDot.Fill = loaded
            ? (Brush)FindResource("SuccessBrush")
            : (Brush)FindResource("TextMutedBrush");

        AiStatusText.Text = loaded ? "Loaded" : "Not Loaded";
        YoloModelPathText.Text = path;
        YoloModelPathText.Foreground = loaded
            ? (Brush)FindResource("TextSecondaryBrush")
            : (Brush)FindResource("TextMutedBrush");
    }

    public void UpdateDetectionStats(int detections, int tracks, int interval)
    {
        AiDetectionsText.Text = $"Detections: {detections}";
        AiTracksText.Text = $"Tracks: {tracks}";
        AiSampleText.Text = $"Interval: {interval}";
    }

    public void UpdateTwinStats(int detections, int defects, double passRate)
    {
        TwinDetectionsText.Text = $"Detections: {detections}";
        TwinDefectsText.Text = $"Defects: {defects}";
        TwinPassRateText.Text = $"Pass: {passRate:F0}%";
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
            DetectionBreakdownText.Text = "No detections yet";
            DetectionBreakdownText.Foreground = (Brush)FindResource("TextMutedBrush");
            return;
        }

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
        if (float.TryParse(ConfThresholdTextBox.Text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float value))
        {
            value = Math.Clamp(value, 0f, 1f);
            ConfThresholdTextBox.Text = value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            ConfidenceChanged?.Invoke(value);
        }
        else
        {
            ConfThresholdTextBox.Text = "0.5";
        }
    }
}
