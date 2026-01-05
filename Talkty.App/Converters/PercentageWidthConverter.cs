using System.Globalization;
using System.Windows.Data;

namespace Talkty.App.Converters;

/// <summary>
/// Converts a percentage (0-100) and container width to a proportional width.
/// MultiBinding: [0] = percentage (0-100), [1] = container ActualWidth
/// </summary>
public class PercentageWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2)
            return 0.0;

        double percentage = values[0] switch
        {
            double d => d,
            float f => f,
            int i => i,
            _ => 0.0
        };

        double containerWidth = values[1] switch
        {
            double d => d,
            float f => f,
            int i => i,
            _ => 0.0
        };

        // Clamp percentage between 0 and 100
        percentage = Math.Clamp(percentage, 0, 100);

        return (percentage / 100.0) * containerWidth;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
