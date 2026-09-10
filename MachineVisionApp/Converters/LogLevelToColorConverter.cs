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
            "INFO"  => (Color.FromRgb(0x3F, 0xB9, 0x50), 1.0),   // SuccessBrush green
            "WARN"  => (Color.FromRgb(0xD2, 0x99, 0x22), 1.0),   // WarningBrush yellow
            "ERROR" => (Color.FromRgb(0xF8, 0x51, 0x49), 1.0),   // DangerBrush red
            "DEBUG" => (Color.FromRgb(0x7A, 0x8B, 0x9E), 1.0),   // TextMutedBrush gray
            _       => (Color.FromRgb(0x7A, 0x8B, 0x9E), 1.0),
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
