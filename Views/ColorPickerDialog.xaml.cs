using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DesktopZones.Services;

namespace DesktopZones.Views;

public partial class ColorPickerDialog : Window
{
    private bool _updating;
    private readonly LocalizationService _loc = LocalizationService.Instance;
    public string SelectedColor { get; private set; } = "FFFFFF";

    public ColorPickerDialog(string initialHex = "FFFFFF")
    {
        InitializeComponent();
        SelectedColor = initialHex;
        SetFromHex(initialHex);
        ApplyLoc();
    }

    void ApplyLoc()
    {
        var cn = _loc.CurrentLanguage == Services.Language.Chinese;
        TitleLabel.Text = cn ? "选择颜色" : "Pick Color";
        OkBtn.Content = cn ? "确定" : "OK";
        CancelBtn.Content = cn ? "取消" : "Cancel";
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
