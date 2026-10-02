using System;
using System.Windows;
using System.Windows.Controls;

namespace VirtualPathVision.Components
{
    /// <summary>
    /// Canny 阈值参数组件，负责处理阈值输入框的验证与回调。
    /// 用户点击"应用"按钮时，校验输入是否为有效整数并触发事件。
    /// </summary>
    public class ThresholdParameterComponent : IDisposable
    {
        private readonly TextBox _threshold1TextBox; // 低阈值输入框
        private readonly TextBox _threshold2TextBox; // 高阈值输入框

        /// <summary>
        /// 初始化阈值组件，绑定按钮点击事件。
        /// </summary>
        /// <param name="threshold1TextBox">低阈值输入框</param>
        /// <param name="threshold2TextBox">高阈值输入框</param>
        /// <param name="applyThresholdsButton">应用按钮</param>
        public ThresholdParameterComponent(
            TextBox threshold1TextBox,
            TextBox threshold2TextBox,
            Button applyThresholdsButton)
        {
            _threshold1TextBox = threshold1TextBox;
            _threshold2TextBox = threshold2TextBox;
            _applyButton = applyThresholdsButton;
            _applyButton.Click += ApplyThresholdsButton_Click;
        }

        private readonly Button _applyButton;
        private bool _disposed;

        /// <summary>阈值变更事件，参数依次为 (threshold1, threshold2)</summary>
        public event Action<int, int>? OnThresholdsChanged;

        /// <summary>OpenCV 灰度图的有效阈值范围</summary>
        private const int MinThreshold = 0;
        private const int MaxThreshold = 255;

        /// <summary>
        /// 应用按钮点击处理：解析输入值并触发 OnThresholdsChanged 事件。
        /// 输入无效时弹出错误提示。
        /// </summary>
        private void ApplyThresholdsButton_Click(object sender, RoutedEventArgs e)
        {
            if (!TryParseValid(_threshold1TextBox.Text, out int threshold1) ||
                !TryParseValid(_threshold2TextBox.Text, out int threshold2))
            {
                ShowError(TranslationService.GetStringStatic("InvalidThreshold"));
                return;
            }

            // Canny 要求低阈值 < 高阈值，否则结果全黑且没有任何提示
            if (threshold1 > threshold2)
            {
                ShowError(TranslationService.GetStringStatic("InvalidThreshold"));
                return;
            }

            OnThresholdsChanged?.Invoke(threshold1, threshold2);
        }

        /// <summary>
        /// 解析并校验单个阈值。
        /// 旧实现只做 int.TryParse，负数或 &gt;255 的值会被原样送进
        /// Cv2.Canny / Cv2.Threshold，结果是静默的全黑画面而非报错。
        /// </summary>
        private static bool TryParseValid(string? text, out int value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(text))
                return false;
            if (!int.TryParse(text.Trim(), out value))
                return false;
            return value >= MinThreshold && value <= MaxThreshold;
        }

        /// <summary>统一的错误提示（带窗口标题，避免被主窗口遮挡）</summary>
        private void ShowError(string message)
        {
            var owner = System.Windows.Application.Current?.MainWindow;
            if (owner != null && owner.IsLoaded)
                MessageBox.Show(owner, message, TranslationService.Instance.AppTitle,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            else
                MessageBox.Show(message, TranslationService.Instance.AppTitle,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        /// <summary>解除对按钮的订阅，避免面板无法回收</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _applyButton.Click -= ApplyThresholdsButton_Click;
        }
    }
}
