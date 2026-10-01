using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace MachineVisionApp.Converters;

public class LogLevelToColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string level = value?.ToString()?.ToUpperInvariant() ?? "";
        string mode = parameter?.ToString() ?? "Foreground";

        (Color color, double opacity) = level switch
        {
            "INFO"  => (Color.FromRgb(0x78, 0x8F, 0x94), 1.0),   // SuccessBrush green
            "WARN"  => (Color.FromRgb(0xC9, 0xA4, 0x6A), 1.0),   // WarningBrush yellow
            "ERROR" => (Color.FromRgb(0xC4, 0x67, 0x5C), 1.0),   // DangerBrush red
            "DEBUG" => (Color.FromRgb(0x7C, 0x7C, 0x85), 1.0),   // TextMutedBrush gray
            _       => (Color.FromRgb(0x7C, 0x7C, 0x85), 1.0),
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
