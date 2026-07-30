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
    public string BorderThicknessText { get => _borderThicknessText; set { _borderThicknessText = value; OnPropertyChanged(); PushToWidget(); } }

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
    private bool _suppressPreview; // suppress live preview during LoadFrom* initialization

    // Model references for live preview
    private DesktopClock? _clockModel;
    private DesktopCalendar? _calModel;
    private StickyNote? _noteModel;
    private AppConfig? _panelConfig;
    private ZoneManager? _panelZoneManager;

    // Snapshot for cancel-revert (dialog local state)
    private string _snapFillColor = "";
    private string _snapBorderColor = "";
    private double _snapFillOpacity;
    private string _snapTitleBarFill = "";
    private double _snapTitleBarOpacity;
    private double _snapButtonOpacity;
    private string _snapTitleTextColor = "";
    private string _snapBgImagePath = "";
    private double _snapBgOffsetX, _snapBgOffsetY, _snapBgZoom, _snapBgOpacity;
    private string _snapDigitalBgImagePath = "";
    private double _snapDigitalBgOffsetX, _snapDigitalBgOffsetY, _snapDigitalBgZoom, _snapDigitalBgOpacity;
    private bool _snapUseGlobal;
    private bool _snapEnableRestore;
    private double _snapBorderThickness;
    private bool _snapLiquidGlass;
    private int _snapGlassBlur, _snapGlassTintOpacity, _snapGlassTintLuminosity;
    private string _snapGlassColorMode = "";
    private string _snapWidgetWidth = "", _snapWidgetHeight = "";
    private string _snapGlobalFillColor = "", _snapGlobalBorderColor = "";
    private double _snapGlobalBorderThickness;

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
            TitleOpacitySlider.ValueChanged += (_, _) => { TitleOpacityLabel.Text = $"{(int)TitleOpacitySlider.Value}%"; PushToWidget(); };
            ButtonOpacitySlider.ValueChanged += (_, _) => { ButtonOpacityLabel.Text = $"{(int)ButtonOpacitySlider.Value}%"; PushToWidget(); };
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
            TitleOpacitySlider.ValueChanged += (_, _) => { TitleOpacityLabel.Text = $"{(int)TitleOpacitySlider.Value}%"; PushToWidget(); };
            ButtonOpacitySlider.ValueChanged += (_, _) => { ButtonOpacityLabel.Text = $"{(int)ButtonOpacitySlider.Value}%"; PushToWidget(); };
        }

        // Wire up slider labels
        ZoomSlider.ValueChanged += (_, _) => { ZoomLabel.Text = $"{ZoomSlider.Value:F1}x"; };
        BgOpacitySlider.ValueChanged += (_, _) => { BgOpacityLabel.Text = $"{(int)BgOpacitySlider.Value}%"; PushToWidget(); };

        _langChanged = _ => ApplyLoc();
        _loc.LanguageChanged += _langChanged;
        UpdateLiquidButton();

        // Wire up EnableRestoreButton toggle
        EnableRestoreButtonToggle.Checked += (_, _) => PushToWidget();
        EnableRestoreButtonToggle.Unchecked += (_, _) => PushToWidget();

        // Wire up liquid glass toggle
        LiquidGlassToggle.Checked += (_, _) => { _liquidGlass = true; UpdateLiquidButton(); PushToWidget(); };
        LiquidGlassToggle.Unchecked += (_, _) => { _liquidGlass = false; UpdateLiquidButton(); PushToWidget(); };
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
        _suppressPreview = true;
        _clockModel = clock;
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

        // Snapshot for cancel-revert
        _snapFillColor = clock.FillColor; _snapBorderColor = clock.BorderColor;
        _snapFillOpacity = _fillOpacityPercent;
        _snapBgImagePath = _bgImagePath; _snapBgOffsetX = _bgOffsetX;
        _snapBgOffsetY = _bgOffsetY; _snapBgZoom = _bgZoom; _snapBgOpacity = _bgOpacity;
        _snapDigitalBgImagePath = _digitalBgImagePath; _snapDigitalBgOffsetX = _digitalBgOffsetX;
        _snapDigitalBgOffsetY = _digitalBgOffsetY; _snapDigitalBgZoom = _digitalBgZoom;
        _snapDigitalBgOpacity = _digitalBgOpacity;
        _snapUseGlobal = clock.UseGlobalAppearance; _snapEnableRestore = _enableRestoreButton;
        _snapBorderThickness = clock.BorderThickness;
        _snapLiquidGlass = _liquidGlass; _snapGlassBlur = _glassBlurAmount;
        _snapGlassTintOpacity = _glassTintOpacity; _snapGlassTintLuminosity = _glassTintLuminosity;
        _snapGlassColorMode = _glassColorMode;
        // Snapshot global values for cancel-revert
        if (Application.Current is App cApp && cApp.ManagementWindow?.WidgetService is { } cSvc)
        {
            var cCfg = cSvc.GetConfig(); _snapGlobalFillColor = cCfg.GlobalFillColor; _snapGlobalBorderColor = cCfg.GlobalBorderColor; _snapGlobalBorderThickness = cCfg.GlobalBorderThickness;
        }
        _suppressPreview = false;
    }

    /// <summary>Load settings from a calendar model.</summary>
    public void LoadFromCalendar(DesktopCalendar cal)
    {
        _suppressPreview = true;
        _calModel = cal;
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

        // Snapshot for cancel-revert
        _snapFillColor = cal.FillColor; _snapBorderColor = cal.BorderColor;
        _snapFillOpacity = _fillOpacityPercent;
        _snapBgImagePath = _bgImagePath; _snapBgOffsetX = _bgOffsetX;
        _snapBgOffsetY = _bgOffsetY; _snapBgZoom = _bgZoom; _snapBgOpacity = _bgOpacity;
        _snapUseGlobal = cal.UseGlobalAppearance; _snapEnableRestore = _enableRestoreButton;
        _snapBorderThickness = cal.BorderThickness;
        _snapLiquidGlass = _liquidGlass; _snapGlassBlur = _glassBlurAmount;
        _snapGlassTintOpacity = _glassTintOpacity; _snapGlassTintLuminosity = _glassTintLuminosity;
        _snapGlassColorMode = _glassColorMode;
        // Snapshot global values for cancel-revert
        if (Application.Current is App calApp && calApp.ManagementWindow?.WidgetService is { } calSvc)
        {
            var calCfg = calSvc.GetConfig(); _snapGlobalFillColor = calCfg.GlobalFillColor; _snapGlobalBorderColor = calCfg.GlobalBorderColor; _snapGlobalBorderThickness = calCfg.GlobalBorderThickness;
        }
        _suppressPreview = false;
    }

    /// <summary>Load settings from a sticky note model.</summary>
    public void LoadFromNote(StickyNote note, ZoneManager? zoneManager = null)
    {
        _suppressPreview = true;
        _noteModel = note;
        _panelZoneManager = zoneManager;
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

        // Snapshot for cancel-revert
        _snapFillColor = note.FillColor; _snapBorderColor = note.BorderColor;
        _snapFillOpacity = _fillOpacityPercent; _snapTitleBarFill = _titleBarFill;
        _snapTitleBarOpacity = _titleBarOpacity; _snapButtonOpacity = _buttonOpacity;
        _snapTitleTextColor = _titleTextColor; _snapBgImagePath = _bgImagePath;
        _snapBgOffsetX = _bgOffsetX; _snapBgOffsetY = _bgOffsetY;
        _snapBgZoom = _bgZoom; _snapBgOpacity = _bgOpacity;
        _snapUseGlobal = note.UseGlobalAppearance; _snapEnableRestore = _enableRestoreButton;
        _snapBorderThickness = note.BorderThickness;
        _snapLiquidGlass = _liquidGlass; _snapGlassBlur = _glassBlurAmount;
        _snapGlassTintOpacity = _glassTintOpacity; _snapGlassTintLuminosity = _glassTintLuminosity;
        _snapGlassColorMode = _glassColorMode;
        _snapWidgetWidth = note.Width.ToString("F0"); _snapWidgetHeight = note.Height.ToString("F0");
        // Snapshot global values for cancel-revert
        if (Application.Current is App nApp && nApp.ManagementWindow?.WidgetService is { } nSvc)
        {
            var nCfg = nSvc.GetConfig(); _snapGlobalFillColor = nCfg.GlobalFillColor; _snapGlobalBorderColor = nCfg.GlobalBorderColor; _snapGlobalBorderThickness = nCfg.GlobalBorderThickness;
        }
        _suppressPreview = false;
    }

    /// <summary>Load settings from global config (panel).</summary>
    public void LoadFromConfig(AppConfig config, ZoneManager? zoneManager = null)
    {
        _suppressPreview = true;
        _panelConfig = config;
        _panelZoneManager = zoneManager;
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

        // Snapshot for cancel-revert
        _snapFillColor = config.PanelFillColor; _snapBorderColor = config.GlobalBorderColor;
        _snapFillOpacity = _fillOpacityPercent; _snapTitleBarFill = _titleBarFill;
        _snapTitleBarOpacity = _titleBarOpacity; _snapButtonOpacity = _buttonOpacity;
        _snapBgImagePath = _bgImagePath; _snapBgOffsetX = _bgOffsetX;
        _snapBgOffsetY = _bgOffsetY; _snapBgZoom = _bgZoom; _snapBgOpacity = _bgOpacity;
        _snapUseGlobal = config.PanelUseGlobalAppearance; _snapEnableRestore = _enableRestoreButton;
        _snapBorderThickness = config.GlobalBorderThickness;
        _snapLiquidGlass = _liquidGlass; _snapGlassBlur = _glassBlurAmount;
        _snapGlassTintOpacity = _glassTintOpacity; _snapGlassTintLuminosity = _glassTintLuminosity;
        _snapGlassColorMode = _glassColorMode;
        _snapWidgetWidth = config.PanelWidth.ToString("F0"); _snapWidgetHeight = config.PanelHeight.ToString("F0");
        // Snapshot global values for cancel-revert
        _snapGlobalFillColor = config.GlobalFillColor; _snapGlobalBorderColor = config.GlobalBorderColor; _snapGlobalBorderThickness = config.GlobalBorderThickness;
        _suppressPreview = false;
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
        LabelUseGlobal.Text = cn ? "全局外观" : "Global Appearance";
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
        LiquidGlassToggle.Content = cn ? "液态玻璃" : "Liquid Glass";

        // Enable restore button
        LabelEnableRestoreButton.Text = cn ? "恢复按钮" : "Restore Button";

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

    void BorderColorPreset_Click(object s, MouseButtonEventArgs e) { if (s is Border b && b.Tag is string c) { BorderColorValue = c; PushToWidget(); } }
    void FillColorPreset_Click(object s, MouseButtonEventArgs e) { if (s is Border b && b.Tag is string c) { _fillColor = c; _fillOpacityPercent = ParseOpacity(c); FillOpacitySlider.Value = _fillOpacityPercent; FillOpacityLabel.Text = $"{(int)_fillOpacityPercent}%"; UpdateHighlights(); OnPropertyChanged(nameof(FillColorValue)); OnPropertyChanged(nameof(FillOpacityPercent)); PushToWidget(); } }
    void TitleBarPreset_Click(object s, MouseButtonEventArgs e) { if (s is Border b && b.Tag is string c) { _titleBarFill = c; UpdateHighlights(); PushToWidget(); } }
    void TitleTextColorPreset_Click(object s, MouseButtonEventArgs e) { if (s is Border b && b.Tag is string c) { TitleTextColorValue = c; UpdateHighlights(); PushToWidget(); } }
    void TitleTextColorCustom_Click(object s, RoutedEventArgs e) { var d = new ColorPickerDialog(TitleTextColorValue.Length >= 7 ? TitleTextColorValue[1..] : "E0E0E0") { Owner = this }; if (d.ShowDialog() == true) { TitleTextColorValue = "#" + d.SelectedColor; PushToWidget(); } }

    void BorderCustom_Click(object s, RoutedEventArgs e) { var d = new ColorPickerDialog(BorderColorValue.Length >= 9 ? BorderColorValue[3..] : "FFFFFF") { Owner = this }; if (d.ShowDialog() == true) { BorderColorValue = (BorderColorValue.Length >= 3 ? BorderColorValue[..3] : "#40") + d.SelectedColor; PushToWidget(); } }
    void FillCustom_Click(object s, RoutedEventArgs e) { var d = new ColorPickerDialog(FillColorValue.Length >= 9 ? FillColorValue[3..] : "000000") { Owner = this }; if (d.ShowDialog() == true) { var alpha = FillColorValue.Length >= 3 ? FillColorValue[..3] : "#08"; _fillColor = alpha + d.SelectedColor; _fillOpacityPercent = ParseOpacity(_fillColor); FillOpacitySlider.Value = _fillOpacityPercent; FillOpacityLabel.Text = $"{(int)_fillOpacityPercent}%"; UpdateHighlights(); OnPropertyChanged(nameof(FillColorValue)); OnPropertyChanged(nameof(FillOpacityPercent)); PushToWidget(); } }

    void FillOpacity_Changed(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (FillOpacityLabel != null)
        {
            FillOpacityLabel.Text = $"{(int)FillOpacitySlider.Value}%";
            _fillOpacityPercent = FillOpacitySlider.Value;
            PushToWidget();
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
            ref blur, ref opacity, ref luminosity, ref colorMode, cn,
            onPreviewChanged: (b, o, l, m) =>
            {
                _glassBlurAmount = b; _glassTintOpacity = o;
                _glassTintLuminosity = l; _glassColorMode = m;
                PushToWidget();
            });

        if (saved)
        {
            _glassBlurAmount = blur;
            _glassTintOpacity = opacity;
            _glassTintLuminosity = luminosity;
            _glassColorMode = colorMode;
        }
        PushToWidget();
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
            PushToWidget();
        }
    }

    void ClearBgImage_Click(object s, RoutedEventArgs e)
    {
        _bgImagePath = "";
        BgImagePathBox.Text = "";
        if (CropBtn != null) CropBtn.IsEnabled = false;
        PushToWidget();
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
            PushToWidget();
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

    /// <summary>Push current dialog state to the live model for real-time preview.</summary>
    void PushToWidget()
    {
        if (_suppressPreview) return;
        var fillColor = UpdateFillFromOpacity();
        var titleBarFill = _target == WidgetSettingsTarget.Panel
            ? $"#{(int)(_titleBarOpacity / 100 * 255):X2}{(_titleBarFill.Length > 3 ? _titleBarFill[3..] : "FFFFFF")}"
            : _titleBarFill;

        double.TryParse(BorderThicknessText, out var bt);
        double.TryParse(WidgetWidth, out var w);
        double.TryParse(WidgetHeight, out var h);

        // Read slider values
        if (_target == WidgetSettingsTarget.StickyNote || _target == WidgetSettingsTarget.Panel)
        {
            _titleBarOpacity = TitleOpacitySlider.Value;
            _buttonOpacity = ButtonOpacitySlider.Value;
        }
        _enableRestoreButton = EnableRestoreButtonToggle.IsChecked == true;

        // Read bg image values
        double.TryParse(OffsetXBox.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _bgOffsetX);
        double.TryParse(OffsetYBox.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _bgOffsetY);
        _bgZoom = ZoomSlider.Value;
        _bgOpacity = BgOpacitySlider.Value;

        if (_target == WidgetSettingsTarget.Clock)
        {
            double.TryParse(DigitalOffsetXBox.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _digitalBgOffsetX);
            double.TryParse(DigitalOffsetYBox.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _digitalBgOffsetY);
            _digitalBgZoom = DigitalZoomSlider.Value;
            _digitalBgOpacity = DigitalBgOpacitySlider.Value;
        }

        // Push to the actual model
        switch (_target)
        {
            case WidgetSettingsTarget.StickyNote when _noteModel != null:
                _noteModel.FillColor = fillColor; _noteModel.BorderColor = BorderColorValue;
                _noteModel.BorderThickness = bt; _noteModel.UseGlobalAppearance = UseGlobalAppearance;
                _noteModel.EnableRestoreButton = _enableRestoreButton;
                _noteModel.TitleBarFillColor = titleBarFill; _noteModel.TitleBarOpacity = _titleBarOpacity;
                _noteModel.ControlOpacity = _buttonOpacity; _noteModel.TitleTextColor = _titleTextColor;
                _noteModel.BackgroundImagePath = _bgImagePath; _noteModel.BgImageOffsetX = _bgOffsetX;
                _noteModel.BgImageOffsetY = _bgOffsetY; _noteModel.BgImageZoom = _bgZoom;
                _noteModel.BackgroundImageOpacity = _bgOpacity;
                _noteModel.EnableLiquidGlass = _liquidGlass; _noteModel.GlassBlurAmount = _glassBlurAmount;
                _noteModel.GlassTintOpacity = _glassTintOpacity; _noteModel.GlassTintLuminosity = _glassTintLuminosity;
                _noteModel.GlassColorMode = _glassColorMode;
                if (w >= 100) _noteModel.Width = w; if (h >= 100) _noteModel.Height = h;
                // Refresh the note window via App.NotesService.Windows
                if (Application.Current is App noteApp && noteApp.NotesService?.Windows.TryGetValue(_noteModel.Id, out var noteWin) == true)
                    noteWin.RefreshAppearance();
                break;

            case WidgetSettingsTarget.Panel when _panelConfig != null:
                _panelConfig.PanelFillColor = fillColor; _panelConfig.GlobalBorderColor = BorderColorValue;
                _panelConfig.GlobalBorderThickness = bt; _panelConfig.PanelUseGlobalAppearance = UseGlobalAppearance;
                _panelConfig.PanelTitleBarFillColor = titleBarFill; _panelConfig.PanelControlOpacity = _buttonOpacity;
                _panelConfig.PanelBackgroundImagePath = _bgImagePath; _panelConfig.PanelBgImageOffsetX = _bgOffsetX;
                _panelConfig.PanelBgImageOffsetY = _bgOffsetY; _panelConfig.PanelBgImageZoom = _bgZoom;
                _panelConfig.PanelBackgroundImageOpacity = _bgOpacity;
                _panelConfig.EnableLiquidGlass = _liquidGlass; _panelConfig.GlassBlurAmount = _glassBlurAmount;
                _panelConfig.GlassTintOpacity = _glassTintOpacity; _panelConfig.GlassTintLuminosity = _glassTintLuminosity;
                _panelConfig.GlassColorMode = _glassColorMode;
                if (UseGlobalAppearance)
                {
                    _panelConfig.GlobalFillColor = fillColor;
                }
                if (w >= 100) _panelConfig.PanelWidth = w; if (h >= 100) _panelConfig.PanelHeight = h;
                // Refresh panel window via App lookup (same pattern as notes)
                if (Application.Current is App panelApp && panelApp.PanelWindow is PanelWindow panelWin)
                {
                    panelWin.ApplyAcrylic();
                    panelWin.ApplyStyle();
                    panelWin.ApplyBackgroundImage();
                }
                break;

            case WidgetSettingsTarget.Clock when _clockModel != null:
                _clockModel.FillColor = fillColor; _clockModel.BorderColor = BorderColorValue;
                _clockModel.BorderThickness = bt; _clockModel.UseGlobalAppearance = UseGlobalAppearance;
                // When UseGlobalAppearance=true, ApplyAcrylic reads GlobalBorderThickness/GlobalFillColor
                if (UseGlobalAppearance && Application.Current is App cApp && cApp.ManagementWindow?.WidgetService is { } wSvc)
                {
                    var wCfg = wSvc.GetConfig();
                    wCfg.GlobalBorderThickness = bt;
                    wCfg.GlobalFillColor = fillColor;
                }
                _clockModel.EnableRestoreButton = _enableRestoreButton;
                _clockModel.EnableLiquidGlass = _liquidGlass; _clockModel.GlassBlurAmount = _glassBlurAmount;
                _clockModel.GlassTintOpacity = _glassTintOpacity; _clockModel.GlassTintLuminosity = _glassTintLuminosity;
                _clockModel.GlassColorMode = _glassColorMode;
                _clockModel.BackgroundImagePath = _bgImagePath; _clockModel.BgImageOffsetX = _bgOffsetX;
                _clockModel.BgImageOffsetY = _bgOffsetY; _clockModel.BgImageZoom = _bgZoom;
                _clockModel.BackgroundImageOpacity = _bgOpacity;
                _clockModel.DigitalBackgroundImagePath = _digitalBgImagePath;
                _clockModel.DigitalBgImageOffsetX = _digitalBgOffsetX; _clockModel.DigitalBgImageOffsetY = _digitalBgOffsetY;
                _clockModel.DigitalBgImageZoom = _digitalBgZoom; _clockModel.DigitalBackgroundImageOpacity = _digitalBgOpacity;
                // Refresh clock window via App lookup (same pattern as notes)
                if (Application.Current is App app2)
                    app2.GetClockWindow(_clockModel.Id)?.RefreshAppearance();
                break;

            case WidgetSettingsTarget.Calendar when _calModel != null:
                _calModel.FillColor = fillColor; _calModel.BorderColor = BorderColorValue;
                _calModel.BorderThickness = bt; _calModel.UseGlobalAppearance = UseGlobalAppearance;
                // When UseGlobalAppearance=true, ApplyAcrylic reads GlobalBorderThickness/GlobalFillColor
                if (UseGlobalAppearance && Application.Current is App calApp && calApp.ManagementWindow?.WidgetService is { } wSvc2)
                {
                    var wCfg2 = wSvc2.GetConfig();
                    wCfg2.GlobalBorderThickness = bt;
                    wCfg2.GlobalFillColor = fillColor;
                }
                _calModel.EnableRestoreButton = _enableRestoreButton;
                _calModel.EnableLiquidGlass = _liquidGlass; _calModel.GlassBlurAmount = _glassBlurAmount;
                _calModel.GlassTintOpacity = _glassTintOpacity; _calModel.GlassTintLuminosity = _glassTintLuminosity;
                _calModel.GlassColorMode = _glassColorMode;
                _calModel.BackgroundImagePath = _bgImagePath; _calModel.BgImageOffsetX = _bgOffsetX;
                _calModel.BgImageOffsetY = _bgOffsetY; _calModel.BgImageZoom = _bgZoom;
                _calModel.BackgroundImageOpacity = _bgOpacity;
                // Refresh calendar window via App lookup (same pattern as notes)
                if (Application.Current is App app3)
                    app3.GetCalendarWindow(_calModel.Id)?.RefreshAppearance();
                break;
        }
    }

    void LoadPreset_Click(object s, RoutedEventArgs e)
    {
        var kind = TargetToKind(_target);
        var snap = BuildCurrentPayload();
        if (snap == null) return;
        var applied = PresetButtonsHelper.OpenLoad(this, kind, snap,
            picked => { /* onCardPicked already applied — no-op */ },
            record => ApplyCardPicked(record));
        if (applied != true)
        {
            // Cancel — restore model + dialog UI from snapshot, then push to live widget.
            ApplyPayload(snap);
            PushToWidget();
        }
        else
        {
            // Apply — model + UI are already at preset's state; ensure live widget reflects it.
            // (LoadFromXxx-driven setters already fired PushToWidget via PropertyChanged, but
            // be defensive in case a path didn't reach one.)
            PushToWidget();
        }
        // Outer dialog STAYS OPEN so the user can verify and decide Apply / Cancel on
        // the widget settings themselves. The previous behavior auto-closing via
        // DialogResult=true was losing the in-dialog verification step.
    }

    /// <summary>
    /// Per-card click hook for the Load Preset dialog. Writes the preset's payload into
    /// the live model, refreshes the in-dialog controls (which in turn fires the
    /// setter→PushToWidget chain so the live widget updates too), but never closes this
    /// dialog or saves config — that's the outer Apply's job.
    /// </summary>
    void ApplyCardPicked(PresetRecord record)
    {
        // Direct copy → model + direct refresh of live window. Mirrors
        // ZoneSettingsDialog / MergedGroupSettingsDialog preview pattern:
        // dialog controls stay untouched during preview (they only sync on
        // OK via ApplyPayload in LoadPreset_Click). Passes the model to
        // RefreshAppearance so the widget reassigns its cached field to
        // the dialog's fresh reference (KEY FIX pattern from
        // ZoneWindow.RefreshZone — otherwise OnClocksChanged could have
        // swapped the widget's _clock to a stale object).
        System.Diagnostics.Debug.WriteLine($"[preview] enter target={_target} recordType={record?.GetType().Name} modelClock={_clockModel!=null} modelCal={_calModel!=null} modelNote={_noteModel!=null} modelPanel={_panelConfig!=null}");
        var app = Application.Current as App;
        switch (_target)
        {
            case WidgetSettingsTarget.Clock when _clockModel != null && record is ClockPreset c:
                System.Diagnostics.Debug.WriteLine($"[preview] Clock before CopyInto: FillColor={_clockModel.FillColor} BorderColor={_clockModel.BorderColor}");
                CopyInto(c.Clock, _clockModel);
                System.Diagnostics.Debug.WriteLine($"[preview] Clock after  CopyInto: FillColor={_clockModel.FillColor} BorderColor={_clockModel.BorderColor}");
                var cw = app?.GetClockWindow(_clockModel.Id);
                System.Diagnostics.Debug.WriteLine($"[preview] Clock GetClockWindow id={_clockModel.Id} null? {cw==null}");
                cw?.RefreshAppearance(_clockModel);
                System.Diagnostics.Debug.WriteLine($"[preview] Clock RefreshAppearance done");
                break;
            case WidgetSettingsTarget.Calendar when _calModel != null && record is CalendarPreset cal:
                CopyInto(cal.Calendar, _calModel);
                app?.GetCalendarWindow(_calModel.Id)?.RefreshAppearance(_calModel);
                break;
            case WidgetSettingsTarget.StickyNote when _noteModel != null && record is StickyNotePreset n:
                CopyInto(n.Note, _noteModel);
                if (app?.NotesService?.Windows.TryGetValue(_noteModel.Id, out var nw) == true && nw is StickyNoteWindow snw)
                    snw.RefreshAppearance(_noteModel);
                break;
            case WidgetSettingsTarget.Panel when _panelConfig != null && record is PanelPreset p:
                if (app?.PanelWindow is PanelWindow pw)
                    pw.RefreshAppearance(p.Config);
                break;
        }
    }

    void SavePreset_Click(object s, RoutedEventArgs e)
    {
        var kind = TargetToKind(_target);
        var payload = BuildCurrentPayload();
        if (payload == null) return;
        PresetButtonsHelper.OpenSave(this, kind, payload);
    }

    private static PresetKind TargetToKind(WidgetSettingsTarget t) => t switch
    {
        WidgetSettingsTarget.Clock => PresetKind.Clock,
        WidgetSettingsTarget.Calendar => PresetKind.Calendar,
        WidgetSettingsTarget.StickyNote => PresetKind.StickyNote,
        WidgetSettingsTarget.Panel => PresetKind.Panel,
        _ => PresetKind.Zone
    };

    /// <summary>Snapshot the dialog's current state into the right typed payload.</summary>
    private object? BuildCurrentPayload() => _target switch
    {
        WidgetSettingsTarget.Clock when _clockModel != null => _clockModel.Clone(),
        WidgetSettingsTarget.Calendar when _calModel != null => _calModel.Clone(),
        WidgetSettingsTarget.StickyNote when _noteModel != null => _noteModel.Clone(),
        WidgetSettingsTarget.Panel when _panelConfig != null => PanelPresetConfig.FromConfig(_panelConfig),
        _ => null
    };

    /// <summary>Replace the dialog's model with the picked preset's payload and refresh widgets.</summary>
    private void ApplyPayload(object picked)
    {
        switch (_target)
        {
            case WidgetSettingsTarget.Clock when _clockModel != null && picked is DesktopClock c:
                CopyInto(c, _clockModel);
                LoadFromClock(_clockModel);
                break;
            case WidgetSettingsTarget.Calendar when _calModel != null && picked is DesktopCalendar cal:
                CopyInto(cal, _calModel);
                LoadFromCalendar(_calModel);
                break;
            case WidgetSettingsTarget.StickyNote when _noteModel != null && picked is StickyNote n:
                CopyInto(n, _noteModel);
                LoadFromNote(_noteModel, _panelZoneManager);
                break;
            case WidgetSettingsTarget.Panel when _panelConfig != null && picked is PanelPresetConfig pcfg:
                pcfg.ApplyTo(_panelConfig);
                LoadFromConfig(_panelConfig, _panelZoneManager);
                break;
        }
    }

    private static void CopyInto<T>(T src, T dst) where T : class
    {
        // POCOs in this project expose public mutable properties — assignment is enough.
        foreach (var prop in typeof(T).GetProperties())
        {
            if (!prop.CanRead || !prop.CanWrite) continue;
            if (prop.GetSetMethod(true) == null) continue;
            prop.SetValue(dst, prop.GetValue(src));
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

        // Push final state (already previewed)
        PushToWidget();

        // Save config
        _panelZoneManager?.SaveConfig();

        DialogResultOk = true;
        DialogResult = true;
        Close();
    }

    void CancelButton_Click(object s, RoutedEventArgs e)
    {
        // Restore model to snapshot state
        switch (_target)
        {
            case WidgetSettingsTarget.StickyNote when _noteModel != null:
                _noteModel.FillColor = _snapFillColor; _noteModel.BorderColor = _snapBorderColor;
                _noteModel.BorderThickness = _snapBorderThickness; _noteModel.UseGlobalAppearance = _snapUseGlobal;
                _noteModel.EnableRestoreButton = _snapEnableRestore;
                _noteModel.TitleBarFillColor = _snapTitleBarFill; _noteModel.TitleBarOpacity = _snapTitleBarOpacity;
                _noteModel.ControlOpacity = _snapButtonOpacity; _noteModel.TitleTextColor = _snapTitleTextColor;
                _noteModel.BackgroundImagePath = _snapBgImagePath; _noteModel.BgImageOffsetX = _snapBgOffsetX;
                _noteModel.BgImageOffsetY = _snapBgOffsetY; _noteModel.BgImageZoom = _snapBgZoom;
                _noteModel.BackgroundImageOpacity = _snapBgOpacity;
                _noteModel.EnableLiquidGlass = _snapLiquidGlass; _noteModel.GlassBlurAmount = _snapGlassBlur;
                _noteModel.GlassTintOpacity = _snapGlassTintOpacity; _noteModel.GlassTintLuminosity = _snapGlassTintLuminosity;
                _noteModel.GlassColorMode = _snapGlassColorMode;
                if (double.TryParse(_snapWidgetWidth, out var rw)) _noteModel.Width = rw;
                if (double.TryParse(_snapWidgetHeight, out var rh)) _noteModel.Height = rh;
                if (Application.Current is App app3 && app3.NotesService?.Windows.TryGetValue(_noteModel.Id, out var noteWin3) == true)
                    noteWin3.RefreshAppearance();
                break;

            case WidgetSettingsTarget.Panel when _panelConfig != null:
                _panelConfig.PanelFillColor = _snapFillColor; _panelConfig.GlobalBorderColor = _snapBorderColor;
                _panelConfig.GlobalBorderThickness = _snapBorderThickness; _panelConfig.PanelUseGlobalAppearance = _snapUseGlobal;
                _panelConfig.PanelTitleBarFillColor = _snapTitleBarFill; _panelConfig.PanelControlOpacity = _snapButtonOpacity;
                _panelConfig.PanelBackgroundImagePath = _snapBgImagePath; _panelConfig.PanelBgImageOffsetX = _snapBgOffsetX;
                _panelConfig.PanelBgImageOffsetY = _snapBgOffsetY; _panelConfig.PanelBgImageZoom = _snapBgZoom;
                _panelConfig.PanelBackgroundImageOpacity = _snapBgOpacity;
                _panelConfig.EnableLiquidGlass = _snapLiquidGlass; _panelConfig.GlassBlurAmount = _snapGlassBlur;
                _panelConfig.GlassTintOpacity = _snapGlassTintOpacity; _panelConfig.GlassTintLuminosity = _snapGlassTintLuminosity;
                _panelConfig.GlassColorMode = _snapGlassColorMode;
                // Always restore global values (PushToWidget may have modified them when UseGlobal was toggled)
                _panelConfig.GlobalFillColor = _snapGlobalFillColor;
                if (double.TryParse(_snapWidgetWidth, out var pw)) _panelConfig.PanelWidth = pw;
                if (double.TryParse(_snapWidgetHeight, out var ph)) _panelConfig.PanelHeight = ph;
                // NOTE: Do NOT call SaveConfig() here — it reloads from disk and overwrites restored values
                if (Application.Current is App appC1 && appC1.PanelWindow is PanelWindow panelWinC)
                {
                    panelWinC.ApplyAcrylic();
                    panelWinC.ApplyStyle();
                    panelWinC.ApplyBackgroundImage();
                }
                break;

            case WidgetSettingsTarget.Clock when _clockModel != null:
                _clockModel.FillColor = _snapFillColor; _clockModel.BorderColor = _snapBorderColor;
                _clockModel.BorderThickness = _snapBorderThickness; _clockModel.UseGlobalAppearance = _snapUseGlobal;
                // Always restore global values
                if (Application.Current is App cAppC && cAppC.ManagementWindow?.WidgetService is { } wSvcC)
                {
                    var wCfgC = wSvcC.GetConfig();
                    wCfgC.GlobalBorderThickness = _snapGlobalBorderThickness;
                    wCfgC.GlobalFillColor = _snapGlobalFillColor;
                }
                _clockModel.EnableRestoreButton = _snapEnableRestore;
                _clockModel.EnableLiquidGlass = _snapLiquidGlass; _clockModel.GlassBlurAmount = _snapGlassBlur;
                _clockModel.GlassTintOpacity = _snapGlassTintOpacity; _clockModel.GlassTintLuminosity = _snapGlassTintLuminosity;
                _clockModel.GlassColorMode = _snapGlassColorMode;
                _clockModel.BackgroundImagePath = _snapBgImagePath; _clockModel.BgImageOffsetX = _snapBgOffsetX;
                _clockModel.BgImageOffsetY = _snapBgOffsetY; _clockModel.BgImageZoom = _snapBgZoom;
                _clockModel.BackgroundImageOpacity = _snapBgOpacity;
                _clockModel.DigitalBackgroundImagePath = _snapDigitalBgImagePath;
                _clockModel.DigitalBgImageOffsetX = _snapDigitalBgOffsetX; _clockModel.DigitalBgImageOffsetY = _snapDigitalBgOffsetY;
                _clockModel.DigitalBgImageZoom = _snapDigitalBgZoom; _clockModel.DigitalBackgroundImageOpacity = _snapDigitalBgOpacity;
                if (Application.Current is App appC2)
                    appC2.GetClockWindow(_clockModel.Id)?.RefreshAppearance();
                break;

            case WidgetSettingsTarget.Calendar when _calModel != null:
                _calModel.FillColor = _snapFillColor; _calModel.BorderColor = _snapBorderColor;
                _calModel.BorderThickness = _snapBorderThickness; _calModel.UseGlobalAppearance = _snapUseGlobal;
                // Always restore global values
                if (Application.Current is App calAppC && calAppC.ManagementWindow?.WidgetService is { } wSvcCal)
                {
                    var wCfgCal = wSvcCal.GetConfig();
                    wCfgCal.GlobalBorderThickness = _snapGlobalBorderThickness;
                    wCfgCal.GlobalFillColor = _snapGlobalFillColor;
                }
                _calModel.EnableRestoreButton = _snapEnableRestore;
                _calModel.EnableLiquidGlass = _snapLiquidGlass; _calModel.GlassBlurAmount = _snapGlassBlur;
                _calModel.GlassTintOpacity = _snapGlassTintOpacity; _calModel.GlassTintLuminosity = _snapGlassTintLuminosity;
                _calModel.GlassColorMode = _snapGlassColorMode;
                _calModel.BackgroundImagePath = _snapBgImagePath; _calModel.BgImageOffsetX = _snapBgOffsetX;
                _calModel.BgImageOffsetY = _snapBgOffsetY; _calModel.BgImageZoom = _snapBgZoom;
                _calModel.BackgroundImageOpacity = _snapBgOpacity;
                if (Application.Current is App appC3)
                    appC3.GetCalendarWindow(_calModel.Id)?.RefreshAppearance();
                break;
        }

        DialogResult = false;
        Close();
    }

    void UseGlobal_Changed(object s, RoutedEventArgs e)
    {
        _useGlobalAppearance = UseGlobalAppearanceBox.IsChecked == true;
        PushToWidget();
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
