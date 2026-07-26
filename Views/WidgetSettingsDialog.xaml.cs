using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DesktopZones.Helpers;
using DesktopZones.Models;
using DesktopZones.Services;

namespace DesktopZones.Views;

public enum WidgetSettingsTarget { Clock, Calendar, StickyNote, Panel }

public partial class WidgetSettingsDialog : Window, INotifyPropertyChanged
{
    private readonly WidgetSettingsTarget _target;
    private readonly LocalizationService _loc = LocalizationService.Instance;
    private Action<Services.Language>? _langChanged;

    // Common settings
    private string _borderThicknessText = "1.0";
    public string BorderThicknessText { get => _borderThicknessText; set { _borderThicknessText = value; OnPropertyChanged(); } }

    // Widget dimensions (sticky note / calendar only)
    private string _widgetWidth = "260";
    public string WidgetWidth { get => _widgetWidth; set { _widgetWidth = value; OnPropertyChanged(); } }
    private string _widgetHeight = "200";
    public string WidgetHeight { get => _widgetHeight; set { _widgetHeight = value; OnPropertyChanged(); } }
    private string _borderColor = "#40FFFFFF";
    public string BorderColorValue { get => _borderColor; set { _borderColor = value; UpdateHighlights(); OnPropertyChanged(); } }
    private string _fillColor = "#08000000";
    public string FillColorValue { get => _fillColor; set { _fillColor = value; UpdateHighlights(); OnPropertyChanged(); } }
    private double _fillOpacityPercent = 8;
    public double FillOpacityPercent { get => _fillOpacityPercent; set { _fillOpacityPercent = value; OnPropertyChanged(); } }
    private bool _useGlobalAppearance = true;
    public bool UseGlobalAppearance { get => _useGlobalAppearance; set { _useGlobalAppearance = value; OnPropertyChanged(); } }

    // Glass settings
    private int _glassBlurAmount = 18;
    private int _glassTintOpacity = 50;
    private int _glassTintLuminosity = 100;
    private string _glassColorMode = "Default";
    private bool _liquidGlass = true;
    public bool LiquidGlassEnabled { get => _liquidGlass; set { _liquidGlass = value; OnPropertyChanged(); } }

    // Title bar (sticky note only)
    private string _titleBarFill = "#10FFFFFF";
    private double _titleBarOpacity = 6;
    private double _buttonOpacity = 40;
    private string _titleTextColor = "#E0E0E0";
    public string TitleTextColorValue { get => _titleTextColor; set { _titleTextColor = value; OnPropertyChanged(); } }

    // Background image (clock only)
    private string _bgImagePath = "";
    private double _bgOffsetX = 0;
    private double _bgOffsetY = 0;
    private double _bgZoom = 1.0;
    private double _bgOpacity = 30;

    public bool DialogResultOk { get; private set; }

    // Public getters for caller to read results
    public double ParsedBorderThickness => double.TryParse(BorderThicknessText, out var v) ? v : 1.0;
    public string ParsedBorderColor => BorderColorValue;
    public string ParsedFillColor => UpdateFillFromOpacity();
    public int ParsedGlassBlur => _glassBlurAmount;
    public int ParsedGlassTintOpacity => _glassTintOpacity;
    public int ParsedGlassLuminosity => _glassTintLuminosity;
    public string ParsedGlassColorMode => _glassColorMode;
    public bool ParsedLiquidGlass => _liquidGlass;
    public double ParsedWidth => double.TryParse(WidgetWidth, out var v) ? v : 260;
    public double ParsedHeight => double.TryParse(WidgetHeight, out var v) ? v : 200;
    public string ParsedTitleBarFill => _target == WidgetSettingsTarget.Panel
        ? $"#{(int)(_titleBarOpacity / 100 * 255):X2}{(_titleBarFill.Length > 3 ? _titleBarFill[3..] : "FFFFFF")}"
        : _titleBarFill;
    public double ParsedTitleBarOpacity => _titleBarOpacity;
    public string ParsedTitleTextColor => _titleTextColor;
    public double ParsedButtonOpacity => _buttonOpacity;
    public string ParsedBgImagePath => _bgImagePath;
    public double ParsedBgOffsetX => _bgOffsetX;
    public double ParsedBgOffsetY => _bgOffsetY;
    public double ParsedBgZoom => _bgZoom;
    public double ParsedBgOpacity => _bgOpacity;
    public bool ParsedUseGlobalAppearance => _useGlobalAppearance;
    // Digital background image
    private string _digitalBgImagePath = "";
    private double _digitalBgOffsetX = 0;
    private double _digitalBgOffsetY = 0;
    private double _digitalBgZoom = 1.0;
    private double _digitalBgOpacity = 30;
    public string ParsedDigitalBgImagePath => _digitalBgImagePath;
    public double ParsedDigitalBgOffsetX => _digitalBgOffsetX;
    public double ParsedDigitalBgOffsetY => _digitalBgOffsetY;
    public double ParsedDigitalBgZoom => _digitalBgZoom;
    public double ParsedDigitalBgOpacity => _digitalBgOpacity;

    // Sticky note dimensions (for crop preview)
    private double _noteWidth = 260;
    private double _noteHeight = 200;

    // Panel dimensions (for crop preview)
    private double _panelWidth = 800;
    private double _panelHeight = 450;

    // Enable restore button
    private bool _enableRestoreButton = true;
    public bool EnableRestoreButton { get => _enableRestoreButton; set { _enableRestoreButton = value; OnPropertyChanged(); } }

    public WidgetSettingsDialog(WidgetSettingsTarget target)
    {
        InitializeComponent();
        _target = target;

        DataContext = this;
        ApplyLoc();

        // Show/hide sections based on target
        if (target == WidgetSettingsTarget.StickyNote)
        {
            WidgetDimensionSection.Visibility = Visibility.Visible;
            TitleTextColorSection.Visibility = Visibility.Visible;
            TitleBarSection.Visibility = Visibility.Visible;
            BgImageSection.Visibility = Visibility.Visible;
            TitleOpacitySlider.ValueChanged += (_, _) => { TitleOpacityLabel.Text = $"{(int)TitleOpacitySlider.Value}%"; };
            ButtonOpacitySlider.ValueChanged += (_, _) => { ButtonOpacityLabel.Text = $"{(int)ButtonOpacitySlider.Value}%"; };
        }
        if (target == WidgetSettingsTarget.Calendar)
        {
            BgImageSection.Visibility = Visibility.Visible;
        }
        if (target == WidgetSettingsTarget.Clock)
        {
            BgImageSection.Visibility = Visibility.Visible;
            DigitalBgImageSection.Visibility = Visibility.Visible;
            DigitalZoomSlider.ValueChanged += (_, _) => { DigitalZoomLabel.Text = $"{DigitalZoomSlider.Value:F1}x"; };
            DigitalBgOpacitySlider.ValueChanged += (_, _) => { DigitalBgOpacityLabel.Text = $"{(int)DigitalBgOpacitySlider.Value}%"; };
        }
        if (target == WidgetSettingsTarget.Panel)
        {
            WidgetDimensionSection.Visibility = Visibility.Visible;
            TitleBarSection.Visibility = Visibility.Visible;
            BgImageSection.Visibility = Visibility.Visible;
            TitleOpacitySlider.ValueChanged += (_, _) => { TitleOpacityLabel.Text = $"{(int)TitleOpacitySlider.Value}%"; };
            ButtonOpacitySlider.ValueChanged += (_, _) => { ButtonOpacityLabel.Text = $"{(int)ButtonOpacitySlider.Value}%"; };
        }

        // Wire up slider labels
        ZoomSlider.ValueChanged += (_, _) => { ZoomLabel.Text = $"{ZoomSlider.Value:F1}x"; };
        BgOpacitySlider.ValueChanged += (_, _) => { BgOpacityLabel.Text = $"{(int)BgOpacitySlider.Value}%"; };

        _langChanged = _ => ApplyLoc();
        _loc.LanguageChanged += _langChanged;
        UpdateLiquidButton();

        // Wire up liquid glass toggle
        LiquidGlassToggle.Checked += (_, _) => { _liquidGlass = true; UpdateLiquidButton(); };
        LiquidGlassToggle.Unchecked += (_, _) => { _liquidGlass = false; UpdateLiquidButton(); };
        LiquidGlassToggle.IsChecked = _liquidGlass;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_langChanged != null) _loc.LanguageChanged -= _langChanged;
        _langChanged = null;
        base.OnClosed(e);
    }

    /// <summary>Load settings from a Zone model (used by ZoneSettingsDialog alternative path).</summary>
    public void LoadFromZone(Zone zone)
    {
        BorderThicknessText = zone.BorderThickness.ToString("F1");
        BorderColorValue = zone.BorderColor;
        _fillColor = zone.FillColor;
        _fillOpacityPercent = ParseOpacity(zone.FillColor);
        FillOpacitySlider.Value = _fillOpacityPercent;
        FillOpacityLabel.Text = $"{(int)_fillOpacityPercent}%";
        _glassBlurAmount = zone.GlassBlurAmount;
        _glassTintOpacity = zone.GlassTintOpacity;
        _glassTintLuminosity = zone.GlassTintLuminosity;
        _glassColorMode = zone.GlassColorMode;
        _liquidGlass = zone.EnableLiquidGlass;
        LiquidGlassToggle.IsChecked = _liquidGlass;
        UpdateHighlights();
    }

    /// <summary>Load settings from a clock model.</summary>
    public void LoadFromClock(DesktopClock clock)
    {
        UseGlobalAppearance = clock.UseGlobalAppearance;
        BorderThicknessText = clock.BorderThickness.ToString("F1");
        BorderColorValue = clock.BorderColor;
        _fillColor = clock.FillColor;
        _fillOpacityPercent = ParseOpacity(clock.FillColor);
        FillOpacitySlider.Value = _fillOpacityPercent;
        FillOpacityLabel.Text = $"{(int)_fillOpacityPercent}%";
        _glassBlurAmount = clock.GlassBlurAmount;
        _glassTintOpacity = clock.GlassTintOpacity;
        _glassTintLuminosity = clock.GlassTintLuminosity;
        _glassColorMode = clock.GlassColorMode;
        _liquidGlass = clock.EnableLiquidGlass;
        LiquidGlassToggle.IsChecked = _liquidGlass;

        // Enable restore button
        _enableRestoreButton = clock.EnableRestoreButton;
        EnableRestoreButtonToggle.IsChecked = _enableRestoreButton;

        // Background image
        _bgImagePath = clock.BackgroundImagePath;
        BgImagePathBox.Text = _bgImagePath;
        if (CropBtn != null) CropBtn.IsEnabled = !string.IsNullOrEmpty(_bgImagePath) && System.IO.File.Exists(_bgImagePath);
        _bgOffsetX = clock.BgImageOffsetX;
        _bgOffsetY = clock.BgImageOffsetY;
        _bgZoom = clock.BgImageZoom;
        _bgOpacity = clock.BackgroundImageOpacity;
        OffsetXBox.Text = _bgOffsetX.ToString("F0");
        OffsetYBox.Text = _bgOffsetY.ToString("F0");
        ZoomSlider.Value = _bgZoom;
        ZoomLabel.Text = $"{_bgZoom:F1}x";
        BgOpacitySlider.Value = _bgOpacity;
        BgOpacityLabel.Text = $"{(int)_bgOpacity}%";

        // Digital background image
        _digitalBgImagePath = clock.DigitalBackgroundImagePath;
        DigitalBgImagePathBox.Text = _digitalBgImagePath;
        if (DigitalCropBtn != null) DigitalCropBtn.IsEnabled = !string.IsNullOrEmpty(_digitalBgImagePath) && System.IO.File.Exists(_digitalBgImagePath);
        _digitalBgOffsetX = clock.DigitalBgImageOffsetX;
        _digitalBgOffsetY = clock.DigitalBgImageOffsetY;
        _digitalBgZoom = clock.DigitalBgImageZoom;
        _digitalBgOpacity = clock.DigitalBackgroundImageOpacity;
        DigitalOffsetXBox.Text = _digitalBgOffsetX.ToString("F0");
        DigitalOffsetYBox.Text = _digitalBgOffsetY.ToString("F0");
        DigitalZoomSlider.Value = _digitalBgZoom;
        DigitalZoomLabel.Text = $"{_digitalBgZoom:F1}x";
        DigitalBgOpacitySlider.Value = _digitalBgOpacity;
        DigitalBgOpacityLabel.Text = $"{(int)_digitalBgOpacity}%";
        UpdateHighlights();
    }

    /// <summary>Load settings from a calendar model.</summary>
    public void LoadFromCalendar(DesktopCalendar cal)
    {
        UseGlobalAppearance = cal.UseGlobalAppearance;
        BorderThicknessText = cal.BorderThickness.ToString("F1");
        BorderColorValue = cal.BorderColor;
        _fillColor = cal.FillColor;
        _fillOpacityPercent = ParseOpacity(cal.FillColor);
        FillOpacitySlider.Value = _fillOpacityPercent;
        FillOpacityLabel.Text = $"{(int)_fillOpacityPercent}%";
        _glassBlurAmount = cal.GlassBlurAmount;
        _glassTintOpacity = cal.GlassTintOpacity;
        _glassTintLuminosity = cal.GlassTintLuminosity;
        _glassColorMode = cal.GlassColorMode;
        _liquidGlass = cal.EnableLiquidGlass;
        LiquidGlassToggle.IsChecked = _liquidGlass;

        // Enable restore button
        _enableRestoreButton = cal.EnableRestoreButton;
        EnableRestoreButtonToggle.IsChecked = _enableRestoreButton;

        // Background image
        _bgImagePath = cal.BackgroundImagePath;
        BgImagePathBox.Text = _bgImagePath;
        if (CropBtn != null) CropBtn.IsEnabled = !string.IsNullOrEmpty(_bgImagePath) && System.IO.File.Exists(_bgImagePath);
        _bgOffsetX = cal.BgImageOffsetX;
        _bgOffsetY = cal.BgImageOffsetY;
        _bgZoom = cal.BgImageZoom;
        _bgOpacity = cal.BackgroundImageOpacity;
        OffsetXBox.Text = _bgOffsetX.ToString("F0");
        OffsetYBox.Text = _bgOffsetY.ToString("F0");
        ZoomSlider.Value = _bgZoom;
        ZoomLabel.Text = $"{_bgZoom:F1}x";
        BgOpacitySlider.Value = _bgOpacity;
        BgOpacityLabel.Text = $"{(int)_bgOpacity}%";
        UpdateHighlights();
    }

    /// <summary>Load settings from a sticky note model.</summary>
    public void LoadFromNote(StickyNote note)
    {
        UseGlobalAppearance = note.UseGlobalAppearance;
        WidgetWidth = note.Width.ToString("F0");
        WidgetHeight = note.Height.ToString("F0");
        BorderThicknessText = note.BorderThickness.ToString("F1");
        BorderColorValue = note.BorderColor;
        _fillColor = note.FillColor;
        _fillOpacityPercent = ParseOpacity(note.FillColor);
        FillOpacitySlider.Value = _fillOpacityPercent;
        FillOpacityLabel.Text = $"{(int)_fillOpacityPercent}%";
        _glassBlurAmount = note.GlassBlurAmount;
        _glassTintOpacity = note.GlassTintOpacity;
        _glassTintLuminosity = note.GlassTintLuminosity;
        _glassColorMode = note.GlassColorMode;
        _liquidGlass = note.EnableLiquidGlass;
        LiquidGlassToggle.IsChecked = _liquidGlass;

        _titleBarFill = note.TitleBarFillColor;
        _titleBarOpacity = note.TitleBarOpacity;
        _buttonOpacity = note.ControlOpacity;
        _titleTextColor = string.IsNullOrEmpty(note.TitleTextColor) ? "#E0E0E0" : note.TitleTextColor;
        TitleOpacitySlider.Value = _titleBarOpacity;
        TitleOpacityLabel.Text = $"{(int)_titleBarOpacity}%";
        ButtonOpacitySlider.Value = _buttonOpacity;
        ButtonOpacityLabel.Text = $"{(int)_buttonOpacity}%";

        // Enable restore button
        _enableRestoreButton = note.EnableRestoreButton;
        EnableRestoreButtonToggle.IsChecked = _enableRestoreButton;

        // Store note dimensions for crop preview
        _noteWidth = note.Width;
        _noteHeight = note.Height;

        // Background image
        _bgImagePath = note.BackgroundImagePath;
        BgImagePathBox.Text = _bgImagePath;
        if (CropBtn != null) CropBtn.IsEnabled = !string.IsNullOrEmpty(_bgImagePath) && System.IO.File.Exists(_bgImagePath);
        _bgOffsetX = note.BgImageOffsetX;
        _bgOffsetY = note.BgImageOffsetY;
        _bgZoom = note.BgImageZoom;
        _bgOpacity = note.BackgroundImageOpacity;
        OffsetXBox.Text = _bgOffsetX.ToString("F0");
        OffsetYBox.Text = _bgOffsetY.ToString("F0");
        ZoomSlider.Value = _bgZoom;
        ZoomLabel.Text = $"{_bgZoom:F1}x";
        BgOpacitySlider.Value = _bgOpacity;
        BgOpacityLabel.Text = $"{(int)_bgOpacity}%";
        UpdateHighlights();
    }

    /// <summary>Load settings from global config (panel).</summary>
    public void LoadFromConfig(AppConfig config)
    {
        UseGlobalAppearance = config.PanelUseGlobalAppearance;
        WidgetWidth = config.PanelWidth.ToString("F0");
        WidgetHeight = config.PanelHeight.ToString("F0");
        BorderThicknessText = config.GlobalBorderThickness.ToString("F1");
        BorderColorValue = config.GlobalBorderColor;
        _fillColor = config.PanelFillColor;
        _fillOpacityPercent = ParseOpacity(config.PanelFillColor);
        FillOpacitySlider.Value = _fillOpacityPercent;
        FillOpacityLabel.Text = $"{(int)_fillOpacityPercent}%";
        _glassBlurAmount = config.GlassBlurAmount;
        _glassTintOpacity = config.GlassTintOpacity;
        _glassTintLuminosity = config.GlassTintLuminosity;
        _glassColorMode = config.GlassColorMode;
        _liquidGlass = config.EnableLiquidGlass;
        LiquidGlassToggle.IsChecked = _liquidGlass;

        // Panel title bar
        _titleBarFill = config.PanelTitleBarFillColor;
        _titleBarOpacity = ParseOpacity(config.PanelTitleBarFillColor);
        _buttonOpacity = config.PanelControlOpacity;
        TitleOpacitySlider.Value = _titleBarOpacity;
        TitleOpacityLabel.Text = $"{(int)_titleBarOpacity}%";
        ButtonOpacitySlider.Value = _buttonOpacity;
        ButtonOpacityLabel.Text = $"{(int)_buttonOpacity}%";

        // Panel dimensions
        _panelWidth = config.PanelWidth > 200 ? config.PanelWidth : 800;
        _panelHeight = config.PanelHeight > 200 ? config.PanelHeight : 450;

        // Panel background image
        _bgImagePath = config.PanelBackgroundImagePath;
        BgImagePathBox.Text = _bgImagePath;
        if (CropBtn != null) CropBtn.IsEnabled = !string.IsNullOrEmpty(_bgImagePath) && System.IO.File.Exists(_bgImagePath);
        _bgOffsetX = config.PanelBgImageOffsetX;
        _bgOffsetY = config.PanelBgImageOffsetY;
        _bgZoom = config.PanelBgImageZoom;
        _bgOpacity = config.PanelBackgroundImageOpacity;
        OffsetXBox.Text = _bgOffsetX.ToString("F0");
        OffsetYBox.Text = _bgOffsetY.ToString("F0");
        ZoomSlider.Value = _bgZoom;
        ZoomLabel.Text = $"{_bgZoom:F1}x";
        BgOpacitySlider.Value = _bgOpacity;
        BgOpacityLabel.Text = $"{(int)_bgOpacity}%";
        UpdateHighlights();
    }

    void SetColorModeCombo(string mode) { _glassColorMode = mode; }

    void ApplyLoc()
    {
        var cn = _loc.CurrentLanguage == Services.Language.Chinese;
        DialogTitle.Text = _target switch
        {
            WidgetSettingsTarget.Clock => cn ? "时钟样式" : "Clock Settings",
            WidgetSettingsTarget.Calendar => cn ? "日历样式" : "Calendar Settings",
            WidgetSettingsTarget.StickyNote => cn ? "便签样式" : "Note Settings",
            WidgetSettingsTarget.Panel => cn ? "面板样式" : "Panel Settings",
            _ => cn ? "组件样式" : "Widget Settings"
        };

        // Common
        LabelUseGlobal.Text = cn ? "使用全局外观" : "Use Global Appearance";
        LabelWidth.Text = _loc["Settings.Width"];
        LabelHeight.Text = _loc["Settings.Height"];
        LabelBorderThickness.Text = cn ? "边框粗细" : "Border Thickness";
        LabelBorderColor.Text = cn ? "边框颜色" : "Border Color";
        LabelFillColor.Text = cn ? "填充颜色" : "Fill Color";
        LabelFillOpacity.Text = cn ? "填充透明度" : "Fill Opacity";
        ApplyButton.Content = _loc["Settings.Apply"];
        CancelButton.Content = _loc["Settings.Cancel"];
        BorderCustomBtn.Content = cn ? "自定义..." : "Custom...";
        FillCustomBtn.Content = cn ? "自定义..." : "Custom...";

        // Glass
        GlassSectionTitle.Text = cn ? "玻璃效果" : "Glass Effect";
        LiquidGlassSettingsBtn.Content = cn ? "💧 液态玻璃设置" : "💧 Liquid Glass Settings";
        LabelGlassIntensity.Text = cn ? "液态玻璃" : "Liquid Glass";
        LiquidGlassToggle.Content = cn ? "启用液态玻璃" : "Enable Liquid Glass";

        // Enable restore button
        LabelEnableRestoreButton.Text = cn ? "启用恢复按钮" : "Enable Restore Button";

        // Title bar (sticky note)
        LabelTitleBar.Text = cn ? "标题栏填充" : "Title Bar Fill";
        LabelTitleTextColor.Text = cn ? "便签名称颜色" : "Title Text Color";
        LabelTitleOpacity.Text = cn ? "标题栏透明度" : "Title Bar Opacity";
        LabelButtonOpacity.Text = cn ? "按钮透明度" : "Button Opacity";

        // Background image
        LabelBgImageAnalog.Text = _target switch
        {
            WidgetSettingsTarget.Clock => cn ? "钟表模式背景图片" : "Analog Background Image",
            WidgetSettingsTarget.Calendar => cn ? "背景图片" : "Background Image",
            WidgetSettingsTarget.StickyNote => cn ? "背景图片" : "Background Image",
            _ => cn ? "背景图片" : "Background Image"
        };
        LabelBgStretch.Text = cn ? "图片裁剪" : "Crop";
        LabelOffsetX.Text = cn ? "水平偏移" : "Offset X";
        LabelOffsetY.Text = cn ? "垂直偏移" : "Offset Y";
        LabelZoom.Text = cn ? "缩放" : "Zoom";
        LabelBgOpacity.Text = cn ? "图片透明度" : "Image Opacity";
        ClearBgBtn.Content = cn ? "清除" : "Clear";
        CropFill.Content = cn ? "拉伸填充" : "Fill";
        CropUniform.Content = cn ? "等比缩放" : "Uniform";
        CropUniformToFill.Content = cn ? "等比填充" : "UniformToFill";
        CropNone.Content = cn ? "原始尺寸" : "None";
        // Digital background image
        LabelBgImageDigital.Text = cn ? "数字模式背景图片" : "Digital Background Image";
        LabelDigitalBgStretch.Text = cn ? "图片裁剪" : "Crop";
        LabelDigitalOffsetX.Text = cn ? "水平偏移" : "Offset X";
        LabelDigitalOffsetY.Text = cn ? "垂直偏移" : "Offset Y";
        LabelDigitalZoom.Text = cn ? "缩放" : "Zoom";
        LabelDigitalBgOpacity.Text = cn ? "图片透明度" : "Image Opacity";
        DigitalClearBgBtn.Content = cn ? "清除" : "Clear";
        DCropFill.Content = cn ? "拉伸填充" : "Fill";
        DCropUniform.Content = cn ? "等比缩放" : "Uniform";
        DCropUniformToFill.Content = cn ? "等比填充" : "UniformToFill";
        DCropNone.Content = cn ? "原始尺寸" : "None";
    }

    void UpdateHighlights()
    {
        HP(BorderColorPresets, BorderColorValue);
        HP(FillColorPresets, FillColorValue);
        HP(TitleBarPresets, _titleBarFill);
        HP(TitleTextColorPresets, TitleTextColorValue);
    }

    static void HP(Panel p, string s)
    {
        if (p == null || string.IsNullOrEmpty(s)) return;
        foreach (var c in p.Children)
        {
            if (c is Border b && b.Tag is string t)
                b.BorderThickness = new Thickness(string.Equals(t, s, StringComparison.OrdinalIgnoreCase) ? 3 : 1);
        }
    }

    void BorderColorPreset_Click(object s, MouseButtonEventArgs e) { if (s is Border b && b.Tag is string c) BorderColorValue = c; }
    void FillColorPreset_Click(object s, MouseButtonEventArgs e) { if (s is Border b && b.Tag is string c) { _fillColor = c; _fillOpacityPercent = ParseOpacity(c); FillOpacitySlider.Value = _fillOpacityPercent; FillOpacityLabel.Text = $"{(int)_fillOpacityPercent}%"; UpdateHighlights(); OnPropertyChanged(nameof(FillColorValue)); OnPropertyChanged(nameof(FillOpacityPercent)); } }
    void TitleBarPreset_Click(object s, MouseButtonEventArgs e) { if (s is Border b && b.Tag is string c) { _titleBarFill = c; UpdateHighlights(); } }
    void TitleTextColorPreset_Click(object s, MouseButtonEventArgs e) { if (s is Border b && b.Tag is string c) { TitleTextColorValue = c; UpdateHighlights(); } }
    void TitleTextColorCustom_Click(object s, RoutedEventArgs e) { var d = new ColorPickerDialog(TitleTextColorValue.Length >= 7 ? TitleTextColorValue[1..] : "E0E0E0") { Owner = this }; if (d.ShowDialog() == true) TitleTextColorValue = "#" + d.SelectedColor; }

    void BorderCustom_Click(object s, RoutedEventArgs e) { var d = new ColorPickerDialog(BorderColorValue.Length >= 9 ? BorderColorValue[3..] : "FFFFFF") { Owner = this }; if (d.ShowDialog() == true) BorderColorValue = (BorderColorValue.Length >= 3 ? BorderColorValue[..3] : "#40") + d.SelectedColor; }
    void FillCustom_Click(object s, RoutedEventArgs e) { var d = new ColorPickerDialog(FillColorValue.Length >= 9 ? FillColorValue[3..] : "000000") { Owner = this }; if (d.ShowDialog() == true) { var alpha = FillColorValue.Length >= 3 ? FillColorValue[..3] : "#08"; _fillColor = alpha + d.SelectedColor; _fillOpacityPercent = ParseOpacity(_fillColor); FillOpacitySlider.Value = _fillOpacityPercent; FillOpacityLabel.Text = $"{(int)_fillOpacityPercent}%"; UpdateHighlights(); OnPropertyChanged(nameof(FillColorValue)); OnPropertyChanged(nameof(FillOpacityPercent)); } }

    void FillOpacity_Changed(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (FillOpacityLabel != null)
        {
            FillOpacityLabel.Text = $"{(int)FillOpacitySlider.Value}%";
            _fillOpacityPercent = FillOpacitySlider.Value;
        }
    }

    // ── Liquid Glass settings ──

    void LiquidGlassSettings_Click(object s, RoutedEventArgs e)
    {
        var cn = _loc.CurrentLanguage == Services.Language.Chinese;
        int blur = _glassBlurAmount, opacity = _glassTintOpacity, luminosity = _glassTintLuminosity;
        string colorMode = _glassColorMode;

        bool saved = AcrylicHelper.ShowLiquidGlassDialog(this,
            cn ? "液态玻璃设置" : "Liquid Glass Settings",
            ref blur, ref opacity, ref luminosity, ref colorMode, cn);

        if (saved)
        {
            _glassBlurAmount = blur;
            _glassTintOpacity = opacity;
            _glassTintLuminosity = luminosity;
            _glassColorMode = colorMode;
        }
    }

    void UpdateLiquidButton()
    {
        if (LiquidGlassSettingsBtn == null) return;
        var accent = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#7C3AED");
        var muted = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#1E1E36");
        LiquidGlassSettingsBtn.Background = new SolidColorBrush(_liquidGlass ? accent : muted);
        LiquidGlassSettingsBtn.Foreground = System.Windows.Media.Brushes.White;
        LiquidGlassSettingsBtn.BorderBrush = new SolidColorBrush(_liquidGlass
            ? System.Windows.Media.Color.FromArgb(0x80, 0x7C, 0x3A, 0xED)
            : (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#404060"));
    }

    void BrowseBgImage_Click(object s, RoutedEventArgs e)
    {
        var d = new Microsoft.Win32.OpenFileDialog
        {
            Title = _loc["Settings.BrowseBg"],
            Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp;*.gif|All|*.*"
        };
        if (d.ShowDialog() == true)
        {
            _bgImagePath = d.FileName;
            BgImagePathBox.Text = _bgImagePath;
            if (CropBtn != null) CropBtn.IsEnabled = !string.IsNullOrEmpty(_bgImagePath) && System.IO.File.Exists(_bgImagePath);
        }
    }

    void ClearBgImage_Click(object s, RoutedEventArgs e)
    {
        _bgImagePath = "";
        BgImagePathBox.Text = "";
        if (CropBtn != null) CropBtn.IsEnabled = false;
    }

    void CropBgImage_Click(object s, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_bgImagePath) || !System.IO.File.Exists(_bgImagePath))
            return;

        // Determine dimensions and crop shape based on widget type
        double targetWidth, targetHeight;
        string cropShape;

        switch (_target)
        {
            case WidgetSettingsTarget.Clock:
                // Analog clock: 200x200 with circular crop
                targetWidth = 200;
                targetHeight = 200;
                cropShape = "Circle";
                break;
            case WidgetSettingsTarget.Calendar:
                // Calendar: use default size with rectangular crop
                targetWidth = 300;
                targetHeight = 400;
                cropShape = "Rectangle";
                break;
            case WidgetSettingsTarget.StickyNote:
                // Sticky note: use user-entered dimensions with rectangular crop
                targetWidth = ParsedWidth;
                targetHeight = ParsedHeight;
                cropShape = "Rectangle";
                break;
            case WidgetSettingsTarget.Panel:
                // Panel: use actual dimensions with rectangular crop
                targetWidth = _panelWidth;
                targetHeight = _panelHeight;
                cropShape = "Rectangle";
                break;
            default:
                targetWidth = 200;
                targetHeight = 200;
                cropShape = "Rectangle";
                break;
        }

        var cropWindow = new ImageCropPreviewWindow(
            imagePath: _bgImagePath,
            targetWidth: targetWidth,
            targetHeight: targetHeight,
            initialOffsetX: _bgOffsetX,
            initialOffsetY: _bgOffsetY,
            initialZoom: _bgZoom,
            initialOpacity: _bgOpacity,
            cropShape: cropShape)
        {
            Owner = this
        };

        if (cropWindow.ShowDialog() == true && cropWindow.Result != null)
        {
            _bgOffsetX = cropWindow.Result.OffsetX;
            _bgOffsetY = cropWindow.Result.OffsetY;
            _bgZoom = cropWindow.Result.Zoom;
            _bgOpacity = cropWindow.Result.Opacity;

            // Update UI controls
            OffsetXBox.Text = _bgOffsetX.ToString("F0");
            OffsetYBox.Text = _bgOffsetY.ToString("F0");
            ZoomSlider.Value = _bgZoom;
            ZoomLabel.Text = $"{_bgZoom:F1}x";
            BgOpacitySlider.Value = _bgOpacity;
            BgOpacityLabel.Text = $"{(int)_bgOpacity}%";
        }
    }

    // ── Digital background image handlers ──

    // DigitalBgStretch_Changed removed — stretch unified to UniformToFill

    void DigitalBrowseBgImage_Click(object s, RoutedEventArgs e)
    {
        var d = new Microsoft.Win32.OpenFileDialog
        {
            Title = _loc["Settings.BrowseBg"],
            Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp;*.gif|All|*.*"
        };
        if (d.ShowDialog() == true)
        {
            _digitalBgImagePath = d.FileName;
            DigitalBgImagePathBox.Text = _digitalBgImagePath;
            if (DigitalCropBtn != null) DigitalCropBtn.IsEnabled = !string.IsNullOrEmpty(_digitalBgImagePath) && System.IO.File.Exists(_digitalBgImagePath);
        }
    }

    void DigitalClearBgImage_Click(object s, RoutedEventArgs e)
    {
        _digitalBgImagePath = "";
        DigitalBgImagePathBox.Text = "";
        if (DigitalCropBtn != null) DigitalCropBtn.IsEnabled = false;
    }

    void DigitalCropBgImage_Click(object s, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_digitalBgImagePath) || !System.IO.File.Exists(_digitalBgImagePath))
            return;

        // Digital clock uses 320x140 with rectangular crop
        double targetWidth = 320;
        double targetHeight = 140;
        string cropShape = "Rectangle";

        var cropWindow = new ImageCropPreviewWindow(
            imagePath: _digitalBgImagePath,
            targetWidth: targetWidth,
            targetHeight: targetHeight,
            initialOffsetX: _digitalBgOffsetX,
            initialOffsetY: _digitalBgOffsetY,
            initialZoom: _digitalBgZoom,
            initialOpacity: _digitalBgOpacity,
            cropShape: cropShape)
        {
            Owner = this
        };

        if (cropWindow.ShowDialog() == true && cropWindow.Result != null)
        {
            _digitalBgOffsetX = cropWindow.Result.OffsetX;
            _digitalBgOffsetY = cropWindow.Result.OffsetY;
            _digitalBgZoom = cropWindow.Result.Zoom;
            _digitalBgOpacity = cropWindow.Result.Opacity;

            // Update UI controls
            DigitalOffsetXBox.Text = _digitalBgOffsetX.ToString("F0");
            DigitalOffsetYBox.Text = _digitalBgOffsetY.ToString("F0");
            DigitalZoomSlider.Value = _digitalBgZoom;
            DigitalZoomLabel.Text = $"{_digitalBgZoom:F1}x";
            DigitalBgOpacitySlider.Value = _digitalBgOpacity;
            DigitalBgOpacityLabel.Text = $"{(int)_digitalBgOpacity}%";
        }
    }

    void ApplyButton_Click(object s, RoutedEventArgs e)
    {
        if (!double.TryParse(BorderThicknessText, out var bt) || bt < 0.5 || bt > 10)
        {
            MessageBox.Show(_loc["Settings.BorderRange"], _loc["Settings.ValidationError"]);
            return;
        }

        // Validate dimensions for sticky note / panel
        if (_target == WidgetSettingsTarget.StickyNote || _target == WidgetSettingsTarget.Panel)
        {
            if (!double.TryParse(WidgetWidth, out var w) || w < 100 || w > 2000)
            {
                MessageBox.Show(_loc["Settings.WidthRange"], _loc["Settings.ValidationError"]);
                return;
            }
            if (!double.TryParse(WidgetHeight, out var h) || h < 100 || h > 2000)
            {
                MessageBox.Show(_loc["Settings.HeightRange"], _loc["Settings.ValidationError"]);
                return;
            }
        }

        // Title bar
        if (_target == WidgetSettingsTarget.StickyNote || _target == WidgetSettingsTarget.Panel)
        {
            _titleBarOpacity = TitleOpacitySlider.Value;
            _buttonOpacity = ButtonOpacitySlider.Value;
        }

        // Enable restore button
        _enableRestoreButton = EnableRestoreButtonToggle.IsChecked == true;

        // Background image
        if (_target == WidgetSettingsTarget.Clock)
        {
            double.TryParse(OffsetXBox.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _bgOffsetX);
            double.TryParse(OffsetYBox.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _bgOffsetY);
            _bgZoom = ZoomSlider.Value;
            _bgOpacity = BgOpacitySlider.Value;
            // Digital background image
            double.TryParse(DigitalOffsetXBox.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _digitalBgOffsetX);
            double.TryParse(DigitalOffsetYBox.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _digitalBgOffsetY);
            _digitalBgZoom = DigitalZoomSlider.Value;
            _digitalBgOpacity = DigitalBgOpacitySlider.Value;
        }
        else if (_target == WidgetSettingsTarget.Calendar || _target == WidgetSettingsTarget.StickyNote)
        {
            double.TryParse(OffsetXBox.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _bgOffsetX);
            double.TryParse(OffsetYBox.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _bgOffsetY);
            _bgZoom = ZoomSlider.Value;
            _bgOpacity = BgOpacitySlider.Value;
        }
        else if (_target == WidgetSettingsTarget.Panel)
        {
            double.TryParse(OffsetXBox.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _bgOffsetX);
            double.TryParse(OffsetYBox.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _bgOffsetY);
            _bgZoom = ZoomSlider.Value;
            _bgOpacity = BgOpacitySlider.Value;
        }

        DialogResultOk = true;
        DialogResult = true;
        Close();
    }

    void CancelButton_Click(object s, RoutedEventArgs e) { DialogResult = false; Close(); }

    void UseGlobal_Changed(object s, RoutedEventArgs e)
    {
        // Value is stored in _useGlobalAppearance and read via ParsedUseGlobalAppearance
        // Caller (ManagementWindow/PanelWindow) saves it on Apply
    }

    string UpdateFillFromOpacity()
    {
        var rgb = _fillColor.Length > 3 ? _fillColor[3..] : "000000";
        if (rgb.Length < 6) rgb = rgb.PadLeft(6, '0');
        return $"#{(int)(_fillOpacityPercent / 100 * 255):X2}{rgb}";
    }

    static double ParseOpacity(string a)
    {
        if (a.Length >= 3 && a[0] == '#')
        {
            try { return int.Parse(a[1..3], System.Globalization.NumberStyles.HexNumber) / 255.0 * 100; } catch { }
        }
        return 8;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var src = e.OriginalSource;
        if (src is Border && src is not Button && src.GetType().Name != "TextBoxView" && src.GetType().Name != "ScrollViewer")
        {
            try { DragMove(); } catch { }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
