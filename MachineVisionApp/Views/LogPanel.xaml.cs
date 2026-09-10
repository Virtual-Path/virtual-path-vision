using System;
using System.Collections;
using System.Windows;
using System.Windows.Controls;

namespace MachineVisionApp.Views;

public partial class LogPanel : UserControl
{
    public ListBox LogListBoxEl => LogListBox;

    public event EventHandler? ClearRequested;

    public LogPanel()
    {
        InitializeComponent();
    }

    public void RefreshTexts()
    {
        ClearLogButton.Content = TranslationService.Instance.ClearLog;
        SectionLogTitle.Text = TranslationService.Instance.SectionApplicationLog;
    }

    public void SetLogSource(IEnumerable source)
    {
        LogListBox.ItemsSource = source;
    }

    public void ScrollToBottom()
    {
        if (LogListBox.Items.Count > 0)
        {
            LogListBox.ScrollIntoView(LogListBox.Items[^1]);
        }
    }

    private void ClearLogButton_Click(object sender, RoutedEventArgs e)
    {
        ClearRequested?.Invoke(this, EventArgs.Empty);
    }

    private void LogListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        ScrollToBottom();
    }
}
