using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using DesktopZones.Helpers;
using DesktopZones.Services;

namespace DesktopZones.Views.Components;

/// <summary>
/// Converts a hex color string ("#RRGGBB" or "RRGGBB", or "Transparent") into a SolidColorBrush.
/// </summary>
public class HexToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string s || string.IsNullOrWhiteSpace(s)) return Brushes.Transparent;
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(s);
            return new SolidColorBrush(color);
        }
        catch
        {
            return Brushes.Transparent;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 24x24 color button that opens a popup palette. Bound via CurrentColor dependency property.
/// </summary>
public partial class ColorSwatchButton : UserControl
{
    readonly LocalizationService _loc = LocalizationService.Instance;

    public static readonly DependencyProperty CurrentColorProperty = DependencyProperty.Register(
        nameof(CurrentColor), typeof(string), typeof(ColorSwatchButton),
        new FrameworkPropertyMetadata("#00000000", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public string CurrentColor
    {
        get => (string)GetValue(CurrentColorProperty);
        set => SetValue(CurrentColorProperty, value);
    }

    static readonly string[] Presets =
    {
        "Transparent", "#000000", "#FFFFFF", "#808080",
        "#FF5252", "#FFA726", "#FFEB3B", "#66BB6A",
        "#42A5F5", "#26C6DA", "#AB47BC", "#8D6E63",
    };

    public ColorSwatchButton()
    {
        InitializeComponent();
        Loaded += (_, _) => BuildSwatches();
    }

    void BuildSwatches()
    {
        Swatches.Children.Clear();
        foreach (var hex in Presets)
        {
            Brush brush;
            bool isTransparent = false;
            if (hex == "Transparent")
            {
                // ponytail 2026-08-25: 透明预设就是 Brushes.Transparent —
                // 直接显示下层 popup 背景（Brush.Bg.Surface），周围 Border 仍然区分得出格子边界。
                isTransparent = true;
                brush = Brushes.Transparent;
            }
            else
            {
                try
                {
                    var color = (Color)ColorConverter.ConvertFromString(hex);
                    brush = new SolidColorBrush(color);
                }
                catch
                {
                    brush = Brushes.Transparent;
                }
            }

            var btn = new Button
            {
                Width = 24,
                Height = 24,
                Margin = new Thickness(2),
                Background = brush,
                BorderBrush = (Brush)FindResource("Brush.Border.Default"),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
            };
            btn.Resources.Add(typeof(Border), new Style(typeof(Border))
            {
                Setters = { new Setter(Border.CornerRadiusProperty, new CornerRadius(3)) },
            });
            // ponytail 2026-08-25: 透明预设鼠标悬浮显示「透明」提示，避免用户以为是空格/不可点。
            if (isTransparent)
                ToolTipService.SetToolTip(btn, _loc["Common.Transparent"]);
            var captured = hex;
            btn.Click += (_, _) =>
            {
                CurrentColor = captured;
                PART_Popup.IsOpen = false;
            };
            Swatches.Children.Add(btn);
        }
    }

    void PART_Button_Click(object sender, RoutedEventArgs e) => PART_Popup.IsOpen = true;

    void Custom_Click(object sender, RoutedEventArgs e)
    {
        PART_Popup.IsOpen = false;
        var dlg = new ColorPickerDialog(CurrentColor ?? "FFFFFF")
        {
            Owner = Window.GetWindow(this)
        };
        if (dlg.ShowDialog() == true)
            CurrentColor = dlg.SelectedColor;
    }
}