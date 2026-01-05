using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Talkty.App.Converters;

public class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var count = value is int i ? i : 0;
        var inverse = parameter?.ToString() == "inverse";

        if (inverse)
            return count == 0 ? Visibility.Visible : Visibility.Collapsed;

        return count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
