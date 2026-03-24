using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Talkty.App.Converters;

/// <summary>
/// Multi-value converter that returns true when both bound values are equal.
/// Used to compare a model item's Profile with the window's SelectedProfile.
/// </summary>
public class EqualityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] == DependencyProperty.UnsetValue || values[1] == DependencyProperty.UnsetValue)
            return false;

        return Equals(values[0], values[1]);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
