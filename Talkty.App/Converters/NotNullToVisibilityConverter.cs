using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Talkty.App.Converters;

/// <summary>
/// Returns Visible when the bound value is non-null (and non-empty for strings),
/// Collapsed otherwise.
/// </summary>
public class NotNullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hasValue = value switch
        {
            null => false,
            string s => !string.IsNullOrEmpty(s),
            _ => true
        };

        return hasValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
