using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace VirtualPathVision.Converters;

public class LogLevelToColorConverter : IValueConverter
{
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
            _       => (Color.FromRgb(0x8E, 0x8E, 0x93), 1.0),
        };

        if (string.Equals(mode, "Background", StringComparison.OrdinalIgnoreCase))
        {
            return new SolidColorBrush(Color.FromArgb(38, color.R, color.G, color.B));
        }

        return new SolidColorBrush(color);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
