using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DesktopZones.Helpers;
using DesktopZones.Services;

namespace DesktopZones.Views;

public partial class ColorPickerDialog : Window
{
    private bool _updating;
    private readonly bool _followSystemTheme;
    private readonly LocalizationService _loc = LocalizationService.Instance;
    public string SelectedColor { get; private set; } = "FFFFFF";

    /// <param name="followSystemTheme">
    /// true = 跟随系统深浅色（Menu.*，供便签字体颜色等非管理界面调用）；
    /// false = 跟随管理界面主题（Brush.*，供设置面板取色等管理界面调用）。
    /// </param>
    public ColorPickerDialog(string initialHex = "FFFFFF", bool followSystemTheme = false)
    {
        InitializeComponent();
        _followSystemTheme = followSystemTheme;
        if (!followSystemTheme)
        {
            ApplyManagementPalette();
            ThemeService.Changed += OnThemeChanged;
            Closed += (_, _) => ThemeService.Changed -= OnThemeChanged;
        }
        else
        {
            AcrylicHelper.ApplyMenuSurface(this, 12);
        }
        SelectedColor = initialHex;
        SetFromHex(initialHex);
        ApplyLoc();
    }

    void OnThemeChanged(AppThemeMode _)
    {
        if (!_followSystemTheme) ApplyManagementPalette();
    }

    /// <summary>管理界面模式：把本窗口用到的 Menu.* 键局部覆盖为 Brush.* 管理主题画刷。</summary>
    void ApplyManagementPalette()
    {
        Resources["Menu.Bg.Surface"]   = new SolidColorBrush(BrushColor("Brush.Bg.Chrome",      Color.FromRgb(0x1E, 0x1E, 0x24)));
        Resources["Menu.Bg.Hover"]     = new SolidColorBrush(BrushColor("Brush.Bg.Input",       Color.FromRgb(0x2A, 0x2A, 0x33)));
        Resources["Menu.Border.Subtle"]= new SolidColorBrush(BrushColor("Brush.Border.Subtle",  Color.FromRgb(0x3A, 0x3A, 0x44)));
        Resources["Menu.Text.Primary"] = new SolidColorBrush(BrushColor("Brush.Text.Primary",   Colors.White));
        Resources["Menu.Text.Secondary"]= new SolidColorBrush(BrushColor("Brush.Text.Secondary", Color.FromRgb(0xB0, 0xB0, 0xB8)));
    }

    static Color BrushColor(string key, Color fallback)
    {
        try { return Application.Current?.TryFindResource(key) is SolidColorBrush b ? b.Color : fallback; }
        catch { return fallback; }
    }

    void ApplyLoc()
    {
        TitleLabel.Text = _loc["ColorPicker.Title"];
        OkBtn.Content = _loc["ColorPicker.Ok"];
        CancelBtn.Content = _loc["ColorPicker.Cancel"];
    }

    private void SetFromHex(string hex)
    {
        _updating = true;
        hex = hex.TrimStart('#');
        try
        {
            int r = Convert.ToInt32(hex[..2], 16);
            int g = Convert.ToInt32(hex[2..4], 16);
            int b = Convert.ToInt32(hex[4..6], 16);
            SliderR.Value = r; SliderG.Value = g; SliderB.Value = b;
            ValR.Text = r.ToString(); ValG.Text = g.ToString(); ValB.Text = b.ToString();
            HexBox.Text = hex.ToUpper();
            UpdatePreview(r, g, b);
        }
        catch { }
        _updating = false;
    }

    private void UpdatePreview(int r, int g, int b)
    {
        SelectedColor = $"{r:X2}{g:X2}{b:X2}";
        ColorPreview.Background = new SolidColorBrush(Color.FromRgb((byte)r, (byte)g, (byte)b));
    }

    private void Slider_Changed(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating) return;
        _updating = true;
        int r = (int)SliderR.Value, g = (int)SliderG.Value, b = (int)SliderB.Value;
        ValR.Text = r.ToString(); ValG.Text = g.ToString(); ValB.Text = b.ToString();
        HexBox.Text = $"{r:X2}{g:X2}{b:X2}";
        UpdatePreview(r, g, b);
        _updating = false;
    }

    private void HexBox_Changed(object s, TextChangedEventArgs e)
    {
        if (_updating) return;
        var hex = HexBox.Text.Trim();
        if (hex.Length == 6) SetFromHex(hex);
    }

    private void ValR_Changed(object s, TextChangedEventArgs e) { if (!_updating && int.TryParse(ValR.Text, out int v) && v >= 0 && v <= 255) { _updating = true; SliderR.Value = v; _updating = false; } }
    private void ValG_Changed(object s, TextChangedEventArgs e) { if (!_updating && int.TryParse(ValG.Text, out int v) && v >= 0 && v <= 255) { _updating = true; SliderG.Value = v; _updating = false; } }
    private void ValB_Changed(object s, TextChangedEventArgs e) { if (!_updating && int.TryParse(ValB.Text, out int v) && v >= 0 && v <= 255) { _updating = true; SliderB.Value = v; _updating = false; } }

    private void Ok_Click(object s, RoutedEventArgs e) { DialogResult = true; Close(); }
    private void Cancel_Click(object s, RoutedEventArgs e) { DialogResult = false; Close(); }
    private void TitleBar_Down(object s, MouseButtonEventArgs e) { try { DragMove(); } catch { } }
}
