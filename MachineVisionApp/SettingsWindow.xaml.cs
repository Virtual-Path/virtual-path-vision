using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MachineVisionApp
{
    public partial class SettingsWindow : Window
    {
        private readonly MainWindow _mainWindow;

        public SettingsWindow(MainWindow mainWindow)
        {
            _mainWindow = mainWindow;
            InitializeComponent();
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
        }

        /// <summary>按当前语言刷新本窗口文本</summary>
        private void ApplyTexts()
        {
            var t = TranslationService.Instance;
            SettingsTitle.Text = t.Settings;
            DisplayLanguageLabel.Text = t.DisplayLanguage;
            SettingsCloseButton.Content = t.Close;
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
            if (LanguageComboBox.SelectedItem is ComboBoxItem item && item.Tag is string culture)
            {
                TranslationService.Instance.ChangeLanguage(culture);
                _mainWindow.RefreshAllTexts();
                ApplyTexts();
            }
        }
    }
}
