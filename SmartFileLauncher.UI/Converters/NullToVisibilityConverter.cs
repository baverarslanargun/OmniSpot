using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SmartFileLauncher.UI.Converters;

public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        // Eğer value null ise Visible (icon göster), değilse Collapsed (thumbnail göster)
        return value == null ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
