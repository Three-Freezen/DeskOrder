using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace DesktopZones.Views.Components;

/// <summary>
/// Resolves an icon key string (e.g. "Icon.Zones") to its Geometry resource from
/// Application.Current.Resources. Returns null on miss so the caller can fall back to a placeholder.
/// ponytail: Application.Current.Resources lookup is O(1) on success; not cached because
/// ResourceDictionary already memoizes.
/// </summary>
public class IconKeyToGeometryConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string key || string.IsNullOrWhiteSpace(key)) return null;
        var app = Application.Current;
        if (app == null) return null;
        var res = app.TryFindResource(key);
        return res is Geometry geo ? geo : null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
