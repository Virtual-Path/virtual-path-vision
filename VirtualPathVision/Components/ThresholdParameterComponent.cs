using System;
using System.Windows;
using System.Windows.Controls;

namespace VirtualPathVision.Components
{
    /// <summary>
    /// Canny 阈值参数组件，负责处理阈值输入框的验证与回调。
    /// 用户点击"应用"按钮时，校验输入是否为有效整数并触发事件。
    /// </summary>
    public class ThresholdParameterComponent
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
            applyThresholdsButton.Click += ApplyThresholdsButton_Click;
        }

        /// <summary>阈值变更事件，参数依次为 (threshold1, threshold2)</summary>
        public event Action<int, int>? OnThresholdsChanged;

        /// <summary>
        /// 应用按钮点击处理：解析输入值并触发 OnThresholdsChanged 事件。
        /// 输入无效时弹出错误提示。
        /// </summary>
        private void ApplyThresholdsButton_Click(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(_threshold1TextBox.Text, out int threshold1) &&
                int.TryParse(_threshold2TextBox.Text, out int threshold2))
            {
                OnThresholdsChanged?.Invoke(threshold1, threshold2);
            }
            else
            {
                MessageBox.Show(TranslationService.GetStringStatic("InvalidThreshold"));
            }
        }
    }
}
