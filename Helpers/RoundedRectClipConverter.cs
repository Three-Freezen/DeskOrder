using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace DesktopZones.Helpers;

// ponytail 2026-08-26: 多值 → Rect(0,0,w,h) + RadiusX/Y 的圆角 RectangleGeometry。
//
// 用途:ContextMenu 外 Border 想把子元素裁到 8px 圆角 —— 但 Rect 是 struct 不是
// DependencyObject,不能直接绑 Binding,也不能用静态大 Rect(那只对左上角生效,
// 其他三个角的圆角在屏外)。
//
// 用法 XAML:
//   <Border.Clip>
//     <MultiBinding Converter="{StaticResource RoundedRectClip}">
//       <Binding Path="ActualWidth"  RelativeSource="{RelativeSource Self}"/>
//       <Binding Path="ActualHeight" RelativeSource="{RelativeSource Self}"/>
//     </MultiBinding>
//   </Border.Clip>
//
// 第三参 (parameter) 可选,写 "8" 会用 8px 圆角;缺省 8。
public sealed class RoundedRectClipConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        double w = values.Length > 0 && values[0] is double dw ? dw : 0;
        double h = values.Length > 1 && values[1] is double dh ? dh : 0;
        double r = 8;
        if (parameter is string s && double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var pr)) r = pr;
        else if (parameter is double pd) r = pd;
        if (w <= 0 || h <= 0) return Geometry.Empty;
        return new RectangleGeometry(new Rect(0, 0, w, h), r, r);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}