using System.Globalization;
using System.Windows.Data;

namespace StarCitizenJapaneseTextCreater;

public class PositiveConverter : IValueConverter
{
    public static readonly PositiveConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double d ? d >= 0 : value is int i && i >= 0;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}
