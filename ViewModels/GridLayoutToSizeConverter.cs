using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using DesktopZones.Helpers;

namespace DesktopZones.ViewModels;

/// <summary>
/// CellLayout (1 or 2) → SubFolder icon box size in px.
/// parameter: "W" → box width; "H" → box height; "TotalH" → box height + name label area.
/// The icon box sits on one grid cell (or 2×2 when CellLayout=2) so it lines up with
/// normal zone items (GridSize 56); the name label hangs below the box like a normal icon.
/// </summary>
public class GridLayoutToSizeConverter : IValueConverter
{
    public static readonly GridLayoutToSizeConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        int cells = value is int n ? n : 1;
        double box = cells * 56.0;
        return parameter as string switch
        {
            "W" => box,
            "H" => box,
            "TotalH" => box + ZoneLayout.LabelArea,
            _ => box,
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => DependencyProperty.UnsetValue;
}
