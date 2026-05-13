using System.Globalization;
using System.Windows.Data;

namespace StarCitizenJapaneseTextCreater;

public class ChatWidthConverter : IValueConverter
{
    public static readonly ChatWidthConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is double width && width > 0)
            return width * 0.75;
        return 600.0;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
