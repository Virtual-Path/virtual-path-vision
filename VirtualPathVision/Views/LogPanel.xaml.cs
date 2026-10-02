using System;
using System.Collections;
using System.Windows;
using System.Windows.Controls;

namespace VirtualPathVision.Views;

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

    /// <summary>
    /// 列表选中变化时不再强制滚到底。
    /// 旧实现让用户每次点击历史日志都会被强行拉回最新一条，
    /// 导致旧日志无法查看。
    /// 自动滚动已由 MainWindow 监听 AppLogger.OnLogAdded 完成。
    /// </summary>
    private void LogListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // 故意留空：仅保留以便 XAML 事件绑定不失效
    }
}
