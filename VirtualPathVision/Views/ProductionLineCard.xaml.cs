using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using VirtualPathVision.Industrial;

namespace VirtualPathVision.Views
{
    /// <summary>
    /// 产线统计卡片：显示合格/不合格计数与良率，并提供编排层的开关与参数。
    ///
    /// 本控件只负责显示与收集参数，不持有业务逻辑；
    /// 真正的编排在 <see cref="ProductionLineService"/>。
    /// </summary>
    public partial class ProductionLineCard : UserControl
    {
        /// <summary>编排层启用状态变化时触发。</summary>
        public event EventHandler? EnabledChanged;

        /// <summary>参数（稳定帧数 / ROI 开关 / MES 地址）发生变化时触发。</summary>
        public event EventHandler? SettingsChanged;

        public ProductionLineCard()
        {
            InitializeComponent();

            // Checked/Unchecked rather than IsCheckedChanged: the latter is not
            // exposed on this target framework's ToggleButton.
            EnabledToggle.Checked += (_, _) => EnabledChanged?.Invoke(this, EventArgs.Empty);
            EnabledToggle.Unchecked += (_, _) => EnabledChanged?.Invoke(this, EventArgs.Empty);
            RoiCheckBox.Checked += (_, _) => SettingsChanged?.Invoke(this, EventArgs.Empty);
            RoiCheckBox.Unchecked += (_, _) => SettingsChanged?.Invoke(this, EventArgs.Empty);

            StableFramesTextBox.TextChanged += (_, _) => SettingsChanged?.Invoke(this, EventArgs.Empty);
            RoiXTextBox.TextChanged += (_, _) => SettingsChanged?.Invoke(this, EventArgs.Empty);
RoiYTextBox.TextChanged += (_, _) => SettingsChanged?.Invoke(this, EventArgs.Empty);
RoiWTextBox.TextChanged += (_, _) => SettingsChanged?.Invoke(this, EventArgs.Empty);
RoiHTextBox.TextChanged += (_, _) => SettingsChanged?.Invoke(this, EventArgs.Empty);
            MesUrlTextBox.TextChanged += (_, _) => SettingsChanged?.Invoke(this, EventArgs.Empty);
            MesTokenBox.PasswordChanged += (_, _) => SettingsChanged?.Invoke(this, EventArgs.Empty);

            UpdateCounters(0, 0, 0);
            RefreshTexts();
        }

        // ── Exposed Elements ───────────────────────────────────────────────
        public ToggleButton EnabledToggleEl => EnabledToggle;
        public CheckBox RoiCheckBoxEl => RoiCheckBox;
        public TextBox StableFramesTextBoxEl => StableFramesTextBox;
        public TextBox MesUrlTextBoxEl => MesUrlTextBox;
        public TextBlock PassedCountTextEl => PassedCountText;
        public TextBlock FailedCountTextEl => FailedCountText;
        public TextBlock YieldTextEl => YieldText;
        public TextBlock LastEventTextEl => LastEventText;

        /// <summary>编排层是否启用。</summary>
        public bool IsLineEnabled => EnabledToggle.IsChecked == true;

        /// <summary>稳定判定所需连续帧数，非法输入时回退到 3。</summary>
        public int StableFrames =>
            int.TryParse(StableFramesTextBox.Text.Trim(), out int n) && n >= 1 && n <= 30 ? n : 3;

        /// <summary>是否把检测限制在检测区内。</summary>
        public bool UseRoi => RoiCheckBox.IsChecked == true;
        /// <summary>
        /// 检测区（像素）。默认对准 VirtualPath-Core 虚拟相机在 1280x720 下的
        /// 检测工位；换分辨率或换相机必须重新标定。
        /// </summary>
        /// <remarks>
        /// 非法输入一律回退到默认值：ROI 填错会让真实工件被判为区外而漏检，
        /// 而那种错误在产线上是静默的——宁可退回已知可用的值。
        /// </remarks>
        public OpenCvSharp.Rect RoiRect => new(
            ReadNonNegative(RoiXTextBox, DefaultRoiX),
            ReadNonNegative(RoiYTextBox, DefaultRoiY),
            Math.Max(1, ReadNonNegative(RoiWTextBox, DefaultRoiW)),
            Math.Max(1, ReadNonNegative(RoiHTextBox, DefaultRoiH)));

        /// <summary>默认 ROI（1280x720 画幅下的传送带检测工位）。</summary>
        public const int DefaultRoiX = 360;
        public const int DefaultRoiY = 180;
        public const int DefaultRoiW = 560;
        public const int DefaultRoiH = 280;

        private static int ReadNonNegative(TextBox box, int fallback)
            => int.TryParse(box.Text.Trim(), out int v) && v >= 0 ? v : fallback;

        private void RoiCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            RoiValuesPanel.Visibility = UseRoi ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>MES 网关地址，空字符串表示不上报。</summary>
        public string MesUrl => MesUrlTextBox.Text.Trim();

        /// <summary>
        /// MES 网关 JWT。网关的 <c>JwtAuthGlobalFilter</c> 白名单不含 quality 路径，
        /// 为空时所有上报都会被 401 拒掉。
        /// </summary>
        public string MesToken => MesTokenBox.Password.Trim();

        /// <summary>刷新计数与良率显示。</summary>
        public void UpdateCounters(int passed, int failed, double yieldRate)
        {
            PassedCountText.Text = passed.ToString();
            FailedCountText.Text = failed.ToString();

            bool hasData = passed + failed > 0;
            double pct = hasData ? yieldRate * 100.0 : 0;

            YieldText.Text = hasData ? $"{pct:F1}%" : "-";
            YieldPercentText.Text = hasData ? $"{pct:F1}%" : "0.0%";
            YieldBar.Value = Math.Clamp(pct, 0, 100);

            LineStatusDot.Fill = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(
                    hasData ? "#34D399" : "#8B98A9"));
        }

        /// <summary>显示最近一条事件（判定完成 / 上报失败）。</summary>
        public void ShowEvent(string text, bool isError = false)
        {
            LastEventText.Text = text;
            LastEventText.Foreground = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(
                    isError ? "#F87171" : "#8B98A9"));
        }

        /// <summary>更新 MES 连接状态徽标。</summary>
        public void SetMesState(string text, bool isError = false)
        {
            MesStateText.Text = text;
            MesStateText.Foreground = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(
                    isError ? "#F87171" : "#8B98A9"));
        }

        /// <summary>重置计数显示。</summary>
        public void ResetCounters() => UpdateCounters(0, 0, 0);

        /// <summary>按当前语言刷新文本。</summary>
        public void RefreshTexts()
        {
            bool zh = TranslationService.Instance.CurrentLanguage
                .StartsWith("zh", StringComparison.OrdinalIgnoreCase);

            LineTitleText.Text = zh ? "产线统计" : "Production Line";
            EnabledLabelText.Text = zh ? "启用" : "Enabled";
            PassedLabelText.Text = zh ? "合格" : "Passed";
            FailedLabelText.Text = zh ? "不合格" : "Failed";
            YieldLabelText.Text = zh ? "良率" : "Yield";
            RoiLabelText.Text = zh ? "检测区域" : "Inspection ROI";
            RoiHintText.Text = zh
                ? "仅统计检测区内的目标，避免背景与网格线被当成工件"
                : "Count only targets inside the inspection zone";
            StableFramesLabelText.Text = zh ? "稳定帧数" : "Stable frames";
            StableFramesHintText.Text = zh
                ? "连续 N 帧结论一致才判定，避免单帧抖动误报"
                : "Require N consecutive frames with the same verdict";
            MesLabelText.Text = zh ? "MES 网关" : "MES gateway";
            MesTokenLabelText.Text = zh ? "网关令牌" : "MES token";
        }
    }
}
