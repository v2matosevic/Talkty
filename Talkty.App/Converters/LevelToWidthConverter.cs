using System.Globalization;
using System.Windows.Data;

namespace Talkty.App.Converters;

/// <summary>
/// Converts level (0.0 to 100.0 or 0.0 to 1.0) to pixel width for bar visualization.
/// For values > 1, assumes 0-100 percentage scale.
/// For values <= 1, assumes 0-1 scale.
/// Default max width is 180px, customize via ConverterParameter.
/// </summary>
public class LevelToWidthConverter : IValueConverter
{
    private const double DefaultMaxWidth = 180.0;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double level = value switch
        {
            float f => f,
            double d => d,
            int i => i,
            _ => 0.0
        };

        double maxWidth = DefaultMaxWidth;
        if (parameter is string paramStr && double.TryParse(paramStr, out double parsed))
        {
            maxWidth = parsed;
        }

        // If level > 1, treat as percentage (0-100)
        if (level > 1)
        {
            level = level / 100.0;
        }

        // Clamp level between 0 and 1
        level = Math.Clamp(level, 0, 1);

        return level * maxWidth;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
