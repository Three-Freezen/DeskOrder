using System.Windows;
using System.Windows.Controls;

namespace DesktopZones.Helpers;

/// <summary>
/// Creates dark-themed ComboBoxes by referencing styles from Theme.xaml.
/// </summary>
public static class ComboBoxHelper
{
    /// <summary>Create a dark-themed ComboBox.</summary>
    public static ComboBox Create(double width = 0, double height = 0, double fontSize = 12,
        Thickness? margin = null)
    {
        var combo = new ComboBox
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = margin ?? default
        };
        if (width > 0) combo.Width = width;
        if (height > 0) combo.Height = height;
        ApplyDarkTheme(combo);
        if (fontSize != 12) combo.FontSize = fontSize;
        return combo;
    }

    /// <summary>Apply dark theme to an existing ComboBox.</summary>
    public static void ApplyDarkTheme(ComboBox combo)
    {
        var app = Application.Current;
        if (app == null) return;

        var template = app.TryFindResource("DarkComboTemplate") as ControlTemplate;
        var itemStyle = app.TryFindResource("DarkComboItemStyle") as Style;

        if (template != null) combo.Template = template;
        if (itemStyle != null) combo.ItemContainerStyle = itemStyle;
    }
}
