using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace VirtualPathVision.Converters;

/// <summary>
/// 日志级别 → 画刷。日志列表的每一行绑定 <c>Level</c>，由本转换器决定文字颜色，
/// 使 INFO/WARN/ERROR/DEBUG 在深浅两套主题下都能一眼区分。
/// </summary>
/// <remarks>
/// <para><b>ConverterParameter 语义</b>：缺省（或任意非 <c>Background</c> 值）返回前景画刷；
/// 传 <c>Background</c> 时返回同色系的 15% 透明度画刷，用作行底纹。</para>
/// <para><b>为什么每次都 new 一个画刷</b>：<see cref="SolidColorBrush"/> 一旦被 WPF 冻结就不能再改，
/// 而主题切换不会重建这些画刷；这里返回的是未冻结实例，由绑定层自行管理生命周期。
/// 行数受日志上限约束，这点分配开销可以忽略。</para>
/// <para>级别文本统一大写后比较，因此 <c>info</c>、<c>Info</c> 都能命中。</para>
/// </remarks>
public class LogLevelToColorConverter : IValueConverter
{
    /// <summary>未知级别回退到次要文本色，而不是透明——避免日志整行不可见。</summary>
    private static readonly Color FallbackColor = Color.FromRgb(0x8E, 0x8E, 0x93);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string level = value?.ToString()?.ToUpperInvariant() ?? "";
        string mode = parameter?.ToString() ?? "Foreground";

        (Color color, double opacity) = level switch
        {
            "INFO"  => (Color.FromRgb(0x0A, 0x84, 0xFF), 1.0),   // Accent blue
            "WARN"  => (Color.FromRgb(0xFF, 0x9F, 0x0A), 1.0),   // WarningBrush orange
            "ERROR" => (Color.FromRgb(0xFF, 0x45, 0x3A), 1.0),   // DangerBrush red
            "DEBUG" => (Color.FromRgb(0x8E, 0x8E, 0x93), 1.0),   // TextMutedBrush gray
            _       => (FallbackColor, 1.0),
        };

        // 背景模式：固定 38/255 的不透明度，既能让颜色可辨又不盖住文字
        if (string.Equals(mode, "Background", StringComparison.OrdinalIgnoreCase))
        {
            return new SolidColorBrush(Color.FromArgb(38, color.R, color.G, color.B));
        }

        return new SolidColorBrush(color);
    }

    /// <summary>单向转换，日志颜色不回写数据源。</summary>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
