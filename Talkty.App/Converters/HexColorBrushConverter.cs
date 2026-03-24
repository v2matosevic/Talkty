using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Talkty.App.Converters;

/// <summary>
/// Converts a hex color string (e.g. "#8B5CF6") to a SolidColorBrush.
/// Pass ConverterParameter="0.2" to apply opacity to the brush.
/// </summary>
public class HexColorBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string hex || string.IsNullOrEmpty(hex))
            return Brushes.Transparent;

        try
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var brush = new SolidColorBrush(color);

            if (parameter is string opacityStr && double.TryParse(opacityStr, CultureInfo.InvariantCulture, out var opacity))
                brush.Opacity = opacity;

            brush.Freeze();
            return brush;
        }
        catch
        {
            return Brushes.Transparent;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
