using System.Globalization;
using System.Windows.Data;

namespace DesktopZones.Helpers;

public class LocFormatConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length == 0) return "";
        var template = values[0]?.ToString() ?? "";
        var args = values.Skip(1).ToArray();
        return args.Length == 0 ? template : string.Format(template, args);
    }
    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
