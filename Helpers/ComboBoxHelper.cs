using System.Windows;
using System.Windows.Controls;

namespace DesktopZones.Helpers;

/// <summary>
/// Lightweight ComboBox factory. The implicit <see cref="ComboBox"/> /
/// <see cref="ComboBoxItem"/> styles in <c>Resources/Controls/ComboBox.xaml</c>
/// already provide the theme-aware template, PART_Popup, hover/selected
/// triggers, and DynamicResource bindings — so this helper just constructs
/// the ComboBox with a few layout knobs and lets the implicit style do
/// the rest. The previous implementation overrode those styles with the
/// legacy <c>DarkComboTemplate</c> / <c>DarkComboItemStyle</c> from
/// Theme.xaml (which lacked PART_Popup and used StaticResource), causing
/// the dropdown to render in a default chrome that didn't follow theme
/// switching.
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
        // ponytail: implicit Style from Controls/ComboBox.xaml handles theming — see class doc.
        if (fontSize != 12) combo.FontSize = fontSize;
        return combo;
    }

    /// <summary>No-op kept for caller compatibility. The implicit ComboBox style
    /// (see class doc) already provides PART_Popup, theme-following brushes,
    /// and hover/selected triggers.</summary>
    public static void ApplyDarkTheme(ComboBox combo) { }
}
