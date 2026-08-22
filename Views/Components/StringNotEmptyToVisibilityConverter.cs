using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DesktopZones.Views.Components;

/// <summary>
/// Collapsed when the bound string is null or empty, Visible otherwise.
/// Used by EditableListRow to toggle the resource-key Path vs emoji TextBlock in
/// the icon slot — whichever the row was configured with wins.
/// ponytail: 1-line converter, sits next to IconKeyToGeometryConverter as a pair.
/// </summary>
public class StringNotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
