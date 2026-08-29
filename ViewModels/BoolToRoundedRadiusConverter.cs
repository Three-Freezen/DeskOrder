using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DesktopZones.ViewModels;

/// <summary>
/// ponytail 2026-08-29: Boolean → 圆角像素值(double)。true=8px 圆角,false=0px 尖角。
/// 供 Rectangle.RadiusX/RadiusY 使用 — 分区里次级分区图标格的填充/玻璃矩形必须
/// 跟随边框的圆角形状,否则正方形填充会从圆角边框的四角“戳出来”(填充生硬)。
/// </summary>
public class BoolToRoundedRadiusConverter : IValueConverter
{
    public static readonly BoolToRoundedRadiusConverter Instance = new();

    public double RadiusWhenTrue { get; set; } = 8;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? RadiusWhenTrue : 0.0;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => DependencyProperty.UnsetValue;
}
