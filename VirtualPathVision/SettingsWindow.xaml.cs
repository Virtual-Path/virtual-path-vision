using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace VirtualPathVision
{
    public partial class SettingsWindow : Window
    {
        private readonly MainWindow _mainWindow;

        /// <summary>XAML 解析完成标志（解析期间 ComboBox 会提前触发 SelectionChanged）</summary>
        private bool _initialized;

        public SettingsWindow(MainWindow mainWindow)
        {
            _mainWindow = mainWindow;
            InitializeComponent();
            _initialized = true;
            ApplyTexts();

            string current = CultureInfo.CurrentUICulture.Name;
            for (int i = 0; i < LanguageComboBox.Items.Count; i++)
            {
                if (LanguageComboBox.Items[i] is ComboBoxItem item && item.Tag?.ToString() == current)
                {
                    LanguageComboBox.SelectedIndex = i;
                    break;
                }
            }

            LoadThemePreference();
            SetAboutInfo();
        }

        /// <summary>填充 About 信息（版本号取程序集版本，随 csproj 的 &lt;Version&gt; 变化）。</summary>
        private void SetAboutInfo()
        {
            try
            {
                var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                if (v != null && AboutVersion != null)
                    AboutVersion.Text = $"v{v.Major}.{v.Minor}.{v.Build}";
            }
            catch { }
        }

        /// <summary>点击 About 中的仓库链接时用系统浏览器打开。</summary>
        private void AboutLink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch { }
            e.Handled = true;
        }

        /// <summary>按当前语言刷新本窗口文本</summary>
        private void ApplyTexts()
        {
            var t = TranslationService.Instance;
            if (SettingsTitle != null) SettingsTitle.Text = t.Settings;
            if (DisplayLanguageLabel != null) DisplayLanguageLabel.Text = t.DisplayLanguage;
            if (SettingsCloseButton != null) SettingsCloseButton.Content = t.Close;
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
                Close();
            else
                DragMove();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void LanguageComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            // XAML 解析期间控件尚未全部连接，忽略提前触发的事件
            if (!_initialized) return;

            if (LanguageComboBox.SelectedItem is ComboBoxItem item && item.Tag is string culture)
            {
                TranslationService.Instance.ChangeLanguage(culture);
                _mainWindow.RefreshAllTexts();
                ApplyTexts();
            }
        }

        /// <summary>加载当前主题设置并选中对应 RadioButton</summary>
        private void LoadThemePreference()
        {
            var currentTheme = ThemeService.Instance.CurrentTheme;
            switch (currentTheme)
            {
                case AppTheme.Light:
                    LightThemeRadio.IsChecked = true;
                    break;
                case AppTheme.Dark:
                    DarkThemeRadio.IsChecked = true;
                    break;
                case AppTheme.System:
                    SystemThemeRadio.IsChecked = true;
                    break;
            }
        }

        /// <summary>主题 RadioButton 选中事件，切换主题</summary>
        private void ThemeRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton radio && radio.IsChecked == true)
            {
                var theme = radio.Tag?.ToString() switch
                {
                    "Light" => AppTheme.Light,
                    "Dark" => AppTheme.Dark,
                    "System" => AppTheme.System,
                    _ => AppTheme.Dark
                };
                ThemeService.Instance.SetTheme(theme);
            }
        }
    }
}
