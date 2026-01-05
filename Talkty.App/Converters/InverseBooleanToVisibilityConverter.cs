using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Talkty.App.Converters;

/// <summary>
/// Inverse of BooleanToVisibilityConverter.
/// Returns Visible when false, Collapsed when true.
/// </summary>
public class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b)
        {
            return b ? Visibility.Collapsed : Visibility.Visible;
        }
        return Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is Visibility v)
        {
            return v != Visibility.Visible;
        }
        return true;
    }
}
