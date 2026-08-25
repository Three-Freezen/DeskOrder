using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DesktopZones.ViewModels;

/// <summary>true → 8px; false → 0px (尖角).</summary>
public class BoolToCornerRadiusConverter : IValueConverter
{
    public static readonly BoolToCornerRadiusConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? new CornerRadius(8) : new CornerRadius(0);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => DependencyProperty.UnsetValue;
}