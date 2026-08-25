using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DesktopZones.ViewModels;

/// <summary>CellLayout (1/2/3) → SubFolder name font size in px.
/// 1×1 网格 → 10(已有大小), 2×2 → 13, 3×3 → 16,默认 10。
/// ponytail: 父子方框变大时字号等比放大,避免大格子里字太小看不清。</summary>
public class CellLayoutToFontSizeConverter : IValueConverter
{
    public static readonly CellLayoutToFontSizeConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is int cells
            ? cells switch { 3 => 16.0, 2 => 13.0, _ => 10.0 }
            : 10.0;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => DependencyProperty.UnsetValue;
}