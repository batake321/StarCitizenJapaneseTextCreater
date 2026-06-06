using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace StarCitizenJapaneseTextCreater;

public class StringToVisConverter : IValueConverter
{
    public static readonly StringToVisConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string s && !string.IsNullOrEmpty(s) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}
