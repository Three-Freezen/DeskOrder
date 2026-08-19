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
    // ponytail: default true so XAML-load events (e.g. TextAdaptiveBox IsChecked="True" firing
    // Checked during InitializeComponent) are suppressed — those run before any LoadFrom* sets
    // _clockModel/_calModel/_noteModel/_panelConfig, and the unchecked deref would NRE in
    // TriggerRefreshTextColorAdaptive's `_clockModel!.Id` path. LoadFrom* sets _suppressPreview
    // = true at the top and = false at the bottom, so the only events that slip through are
    // genuine user toggles.
    private bool _suppressPreview = true;

    // FillColor round-trip guard: parsing "#AARRGGBB" through ParseOpacity yields a
    // 0-100 double; UpdateFillFromOpacity then casts back to 0-255 byte via
    // `(int)(x/100*255)`. Floating-point + int truncation loses up to 1/255 alpha
    // per round-trip (e.g. "#08000000" → 3.137% → "#07000000"). Without this guard,
    // opening the style dialog for Clock/Calendar and clicking Apply (or even just
    // Cancel in some paths) silently mutates model.FillColor by 1 alpha step even
    // when the user touched nothing. Only re-derive FillColor when the user actually
    // interacted with the opacity slider / a preset swatch / the custom picker.
    private bool _fillColorTouched;

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
    private bool _snapTextAdaptive;
    private bool _snapTitleBarTextAdaptive;
    // Panel-only: cfg.PanelBorderColor is mutated by ApplyTo during preset preview but is
    // not the same field as _snapBorderColor (which holds the clock/calendar/note BorderColor).
    // Kept separately so Cancel can restore the panel border without conflicting with other
    // widget snapshots.
    private string _snapPanelBorderColor = "";
    private string _snapPanelBgImageStretch = "UniformToFill";

    // Public getters for caller to read results
    public double ParsedBorderThickness => double.TryParse(BorderThicknessText, out var v) ? v : 1.0;
    public string ParsedBorderColor => BorderColorValue;
    public string ParsedFillColor => _fillColorTouched ? UpdateFillFromOpacity() : _fillColor;
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
            TitleBarTextAdaptiveBox.Visibility = Visibility.Visible;
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
            DigitalZoomSlider.ValueChanged += (_, _) => { DigitalZoomLabel.Text = $"{DigitalZoomSlider.Value:F1}x"; PushToWidget(); };
            DigitalBgOpacitySlider.ValueChanged += (_, _) => { DigitalBgOpacityLabel.Text = $"{(int)DigitalBgOpacitySlider.Value}%"; PushToWidget(); };
        }
        if (target == WidgetSettingsTarget.Panel)
        {
            WidgetDimensionSection.Visibility = Visibility.Visible;
            TitleBarSection.Visibility = Visibility.Visible;
            BgImageSection.Visibility = Visibility.Visible;
            TitleBarTextAdaptiveBox.Visibility = Visibility.Visible;
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
    public void LoadFromClock(DesktopClock clock) => LoadFromClock(clock, resnapshot: true);
    public void LoadFromClock(DesktopClock clock, bool resnapshot)
    {
        _suppressPreview = true;
        _fillColorTouched = false;
        _clockModel = clock;
        UseGlobalAppearance = clock.UseGlobalAppearance;
        BorderThicknessText = clock.BorderThickness.ToString("F1");
        PullAppearanceFields(clock);
        _bgOpacity = clock.BackgroundImageOpacity;
        BgOpacitySlider.Value = _bgOpacity;
        BgOpacityLabel.Text = $"{(int)_bgOpacity}%";

        // Digital background image (Clock-specific)
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

        if (resnapshot)
        {
            // Snapshot for cancel-revert (only on initial load — applying a preset must
            // NOT clobber the original snapshot, otherwise outer Cancel can't revert).
            // ponytail: snapshot the EFFECTIVE values (whatever PullAppearanceFields resolved)
            // so Cancel restores the same color the widget was actually displaying — matches
            // SyncFillRect's conditional. Without this, when UseGlobal=true, Cancel would
            // restore clock.FillColor (per-widget) even though the user was seeing the
            // GlobalFillColor value, leaving widget and model permanently desynced.
            _snapFillColor = _fillColor; _snapBorderColor = BorderColorValue;
            _snapFillOpacity = _fillOpacityPercent;
            _snapBgImagePath = _bgImagePath; _snapBgOffsetX = _bgOffsetX;
            _snapBgOffsetY = _bgOffsetY; _snapBgZoom = _bgZoom; _snapBgOpacity = _bgOpacity;
            _snapDigitalBgImagePath = _digitalBgImagePath; _snapDigitalBgOffsetX = _digitalBgOffsetX;
            _snapDigitalBgOffsetY = _digitalBgOffsetY; _snapDigitalBgZoom = _digitalBgZoom;
            _snapDigitalBgOpacity = _digitalBgOpacity;
            _snapUseGlobal = clock.UseGlobalAppearance; _snapEnableRestore = _enableRestoreButton;
            _snapBorderThickness = double.TryParse(BorderThicknessText, out var sbt) ? sbt : 1.0;
            _snapLiquidGlass = _liquidGlass; _snapGlassBlur = _glassBlurAmount;
            _snapGlassTintOpacity = _glassTintOpacity; _snapGlassTintLuminosity = _glassTintLuminosity;
            _snapGlassColorMode = _glassColorMode;
            _snapTextAdaptive = clock.TextColorAdaptive;
            // Snapshot global values for cancel-revert
            if (Application.Current is App cApp && cApp.ManagementWindow?.WidgetService is { } cSvc)
            {
                var cCfg = cSvc.GetConfig(); _snapGlobalFillColor = cCfg.GlobalFillColor; _snapGlobalBorderColor = cCfg.GlobalBorderColor; _snapGlobalBorderThickness = cCfg.GlobalBorderThickness;
            }
        }
        _suppressPreview = false;
    }

    /// <summary>Load settings from a calendar model.</summary>
    public void LoadFromCalendar(DesktopCalendar cal) => LoadFromCalendar(cal, resnapshot: true);
    public void LoadFromCalendar(DesktopCalendar cal, bool resnapshot)
    {
        _suppressPreview = true;
        _fillColorTouched = false;
        _calModel = cal;
        UseGlobalAppearance = cal.UseGlobalAppearance;
        BorderThicknessText = cal.BorderThickness.ToString("F1");
        PullAppearanceFields(cal);
        _bgOpacity = cal.BackgroundImageOpacity;
        BgOpacitySlider.Value = _bgOpacity;
        BgOpacityLabel.Text = $"{(int)_bgOpacity}%";

        if (resnapshot)
        {
            // Snapshot for cancel-revert (see LoadFromClock note about resnapshot).
            // ponytail: snapshot EFFECTIVE values (whatever PullAppearanceFields resolved) — see clock comment.
            _snapFillColor = _fillColor; _snapBorderColor = BorderColorValue;
            _snapFillOpacity = _fillOpacityPercent;
            _snapBgImagePath = _bgImagePath; _snapBgOffsetX = _bgOffsetX;
            _snapBgOffsetY = _bgOffsetY; _snapBgZoom = _bgZoom; _snapBgOpacity = _bgOpacity;
            _snapUseGlobal = cal.UseGlobalAppearance; _snapEnableRestore = _enableRestoreButton;
            _snapBorderThickness = double.TryParse(BorderThicknessText, out var sbt) ? sbt : 1.0;
            _snapLiquidGlass = _liquidGlass; _snapGlassBlur = _glassBlurAmount;
            _snapGlassTintOpacity = _glassTintOpacity; _snapGlassTintLuminosity = _glassTintLuminosity;
            _snapGlassColorMode = _glassColorMode;
            _snapTextAdaptive = cal.TextColorAdaptive;
            // Snapshot global values for cancel-revert
            if (Application.Current is App calApp && calApp.ManagementWindow?.WidgetService is { } calSvc)
            {
                var calCfg = calSvc.GetConfig(); _snapGlobalFillColor = calCfg.GlobalFillColor; _snapGlobalBorderColor = calCfg.GlobalBorderColor; _snapGlobalBorderThickness = calCfg.GlobalBorderThickness;
            }
        }
        _suppressPreview = false;
    }

    /// <summary>Load settings from a sticky note model.</summary>
    public void LoadFromNote(StickyNote note, ZoneManager? zoneManager = null) => LoadFromNote(note, zoneManager, resnapshot: true);
    public void LoadFromNote(StickyNote note, ZoneManager? zoneManager, bool resnapshot)
    {
        _suppressPreview = true;
        _fillColorTouched = false;
        _noteModel = note;
        _panelZoneManager = zoneManager;
        UseGlobalAppearance = note.UseGlobalAppearance;
        WidgetWidth = note.Width.ToString("F0");
        WidgetHeight = note.Height.ToString("F0");
        BorderThicknessText = note.BorderThickness.ToString("F1");
        PullAppearanceFields(note);

        // Note-specific title bar / button opacity
        _titleBarFill = note.TitleBarFillColor;
        _titleBarOpacity = note.TitleBarOpacity;
        _buttonOpacity = note.ControlOpacity;
        _titleTextColor = string.IsNullOrEmpty(note.TitleTextColor) ? "#E0E0E0" : note.TitleTextColor;
        TitleOpacitySlider.Value = _titleBarOpacity;
        TitleOpacityLabel.Text = $"{(int)_titleBarOpacity}%";
        ButtonOpacitySlider.Value = _buttonOpacity;
        ButtonOpacityLabel.Text = $"{(int)_buttonOpacity}%";

        // Note-specific dimensions for crop preview
        _noteWidth = note.Width;
        _noteHeight = note.Height;

        // Background image opacity (per-widget)
        _bgOpacity = note.BackgroundImageOpacity;
        BgOpacitySlider.Value = _bgOpacity;
        BgOpacityLabel.Text = $"{(int)_bgOpacity}%";

        // Title bar text adaptive (note-specific)
        if (TitleBarTextAdaptiveBox != null) TitleBarTextAdaptiveBox.IsChecked = note.TitleBarTextColorAdaptive;

        if (resnapshot)
        {
            // Snapshot for cancel-revert (see LoadFromClock note about resnapshot).
            // ponytail: snapshot EFFECTIVE values (whatever PullAppearanceFields resolved) — see clock comment.
            _snapFillColor = _fillColor; _snapBorderColor = BorderColorValue;
            _snapFillOpacity = _fillOpacityPercent; _snapTitleBarFill = _titleBarFill;
            _snapTitleBarOpacity = _titleBarOpacity; _snapButtonOpacity = _buttonOpacity;
            _snapTitleTextColor = _titleTextColor; _snapBgImagePath = _bgImagePath;
            _snapBgOffsetX = _bgOffsetX; _snapBgOffsetY = _bgOffsetY;
            _snapBgZoom = _bgZoom; _snapBgOpacity = _bgOpacity;
            _snapUseGlobal = note.UseGlobalAppearance; _snapEnableRestore = _enableRestoreButton;
            _snapBorderThickness = double.TryParse(BorderThicknessText, out var sbt) ? sbt : 1.0;
            _snapLiquidGlass = _liquidGlass; _snapGlassBlur = _glassBlurAmount;
            _snapGlassTintOpacity = _glassTintOpacity; _snapGlassTintLuminosity = _glassTintLuminosity;
            _snapGlassColorMode = _glassColorMode;
            _snapWidgetWidth = note.Width.ToString("F0"); _snapWidgetHeight = note.Height.ToString("F0");
            // Text color adaptive — snapshot for cancel-revert
            _snapTextAdaptive = note.TextColorAdaptive;
            _snapTitleBarTextAdaptive = note.TitleBarTextColorAdaptive;
            // Snapshot global values for cancel-revert
            if (Application.Current is App nApp && nApp.ManagementWindow?.WidgetService is { } nSvc)
            {
                var nCfg = nSvc.GetConfig(); _snapGlobalFillColor = nCfg.GlobalFillColor; _snapGlobalBorderColor = nCfg.GlobalBorderColor; _snapGlobalBorderThickness = nCfg.GlobalBorderThickness;
            }
        }
        _suppressPreview = false;
    }

    /// <summary>Load settings from global config (panel).</summary>
    public void LoadFromConfig(AppConfig config, ZoneManager? zoneManager = null) => LoadFromConfig(config, zoneManager, resnapshot: true);
    public void LoadFromConfig(AppConfig config, ZoneManager? zoneManager, bool resnapshot)
    {
        _suppressPreview = true;
        _fillColorTouched = false;
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

        // Text color adaptive — Panel uses config-level flags
        if (TextAdaptiveBox != null) TextAdaptiveBox.IsChecked = config.PanelTextColorAdaptive;
        if (TitleBarTextAdaptiveBox != null) TitleBarTextAdaptiveBox.IsChecked = config.PanelTitleBarTextColorAdaptive;

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

        if (resnapshot)
        {
            // Snapshot for cancel-revert (see LoadFromClock note about resnapshot).
            _snapFillColor = config.PanelFillColor; _snapBorderColor = config.GlobalBorderColor;
            // Panel's own border color is mutated by ApplyCardPicked's CopyPanelFields but
            // is a separate field from GlobalBorderColor — snapshot it so Cancel can restore.
            _snapPanelBorderColor = config.PanelBorderColor;
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
            _snapPanelBgImageStretch = config.PanelBgImageStretch;
            // Snapshot global values for cancel-revert
            _snapGlobalFillColor = config.GlobalFillColor; _snapGlobalBorderColor = config.GlobalBorderColor; _snapGlobalBorderThickness = config.GlobalBorderThickness;
            // Snapshot adaptive flags
            _snapTextAdaptive = config.PanelTextColorAdaptive;
            _snapTitleBarTextAdaptive = config.PanelTitleBarTextColorAdaptive;
        }
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
            _fillColorTouched = true;
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
                // ponytail: window sync — read the live widget's actual Width/Height so the
                // crop preview matches what the user sees on screen. Old hardcoded 300×400
                // drifted after ShowCalendar() (default 300×490) and any user resize.
                // Falls back to model values if the window isn't currently shown.
                if (Application.Current is App calSyncApp
                    && calSyncApp.GetCalendarWindow(_calModel!.Id) is { IsVisible: true, ActualWidth: > 0 } liveCal)
                {
                    targetWidth = liveCal.ActualWidth;
                    targetHeight = liveCal.ActualHeight;
                }
                else
                {
                    targetWidth = _calModel!.Width > 0 ? _calModel.Width : 320;
                    targetHeight = _calModel!.Height > 0 ? _calModel.Height : 490;
                }
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
            PushToWidget();
        }
    }

    void DigitalClearBgImage_Click(object s, RoutedEventArgs e)
    {
        _digitalBgImagePath = "";
        DigitalBgImagePathBox.Text = "";
        if (DigitalCropBtn != null) DigitalCropBtn.IsEnabled = false;
        PushToWidget();
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
            PushToWidget();
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
                PushAppearanceFields(_noteModel);
                _noteModel.UseGlobalAppearance = UseGlobalAppearance;
                _noteModel.BorderThickness = bt;
                _noteModel.BackgroundImageOpacity = _bgOpacity;
                _noteModel.TitleBarFillColor = titleBarFill; _noteModel.TitleBarOpacity = _titleBarOpacity;
                _noteModel.ControlOpacity = _buttonOpacity; _noteModel.TitleTextColor = _titleTextColor;
                if (TitleBarTextAdaptiveBox != null) _noteModel.TitleBarTextColorAdaptive = TitleBarTextAdaptiveBox.IsChecked == true;
                if (w >= 100) _noteModel.Width = w; if (h >= 100) _noteModel.Height = h;
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
                _panelConfig.PanelTextColorAdaptive = TextAdaptiveBox?.IsChecked == true;
                _panelConfig.PanelTitleBarTextColorAdaptive = TitleBarTextAdaptiveBox?.IsChecked == true;
                if (UseGlobalAppearance)
                {
                    _panelConfig.GlobalFillColor = fillColor;
                }
                if (w >= 100) _panelConfig.PanelWidth = w; if (h >= 100) _panelConfig.PanelHeight = h;
                if (Application.Current is App panelApp && panelApp.PanelWindow is PanelWindow panelWin)
                {
                    panelWin.ApplyAcrylic();
                    panelWin.ApplyStyle();
                    panelWin.ApplyBackgroundImage();
                }
                break;

            case WidgetSettingsTarget.Clock when _clockModel != null:
                PushAppearanceFields(_clockModel);
                _clockModel.UseGlobalAppearance = UseGlobalAppearance;
                _clockModel.BorderThickness = bt;
                _clockModel.BackgroundImageOpacity = _bgOpacity;
                // When UseGlobalAppearance=true, ApplyAcrylic reads GlobalBorderThickness/GlobalFillColor
                if (UseGlobalAppearance && Application.Current is App cApp && cApp.ManagementWindow?.WidgetService is { } wSvc)
                {
                    var wCfg = wSvc.GetConfig();
                    wCfg.GlobalBorderThickness = bt;
                    wCfg.GlobalFillColor = fillColor;
                }
                _clockModel.DigitalBackgroundImagePath = _digitalBgImagePath;
                _clockModel.DigitalBgImageOffsetX = _digitalBgOffsetX; _clockModel.DigitalBgImageOffsetY = _digitalBgOffsetY;
                _clockModel.DigitalBgImageZoom = _digitalBgZoom; _clockModel.DigitalBackgroundImageOpacity = _digitalBgOpacity;
                if (Application.Current is App app2)
                    app2.GetClockWindow(_clockModel.Id)?.RefreshAppearance();
                break;

            case WidgetSettingsTarget.Calendar when _calModel != null:
                PushAppearanceFields(_calModel);
                _calModel.UseGlobalAppearance = UseGlobalAppearance;
                _calModel.BorderThickness = bt;
                _calModel.BackgroundImageOpacity = _bgOpacity;
                // When UseGlobalAppearance=true, ApplyAcrylic reads GlobalBorderThickness/GlobalFillColor
                if (UseGlobalAppearance && Application.Current is App calApp && calApp.ManagementWindow?.WidgetService is { } wSvc2)
                {
                    var wCfg2 = wSvc2.GetConfig();
                    wCfg2.GlobalBorderThickness = bt;
                    wCfg2.GlobalFillColor = fillColor;
                }
                if (Application.Current is App app3)
                    app3.GetCalendarWindow(_calModel.Id)?.RefreshAppearance(_calModel, rebuildCells: false);
                break;
        }
    }

    void LoadPreset_Click(object s, RoutedEventArgs e)
    {
        // ponytail: Matches ZoneSettingsDialog.LoadPresetButton_Click + MergedGroupSettingsDialog
        // .LoadPreset_Click exactly. Two independent cancel layers:
        //   • LoadPresetDialog Apply/Cancel — only mutates / restores the live widget model
        //     (and re-syncs the dialog UI). Independent of the outer Apply/Cancel.
        //   • Outer Apply/Cancel — uses the dialog-open snapshot (_snap*), untouched here.
        // Outer Cancel therefore reverts to the original (dialog-open) style even if a preset
        // was applied — matches "若点击取消按钮，则回到原来的样式".
        var kind = TargetToKind(_target);
        var snap = BuildCurrentPayload();
        if (snap == null) return;

        // Snapshot the widget-service global config at the moment LoadPreset is opened.
        // ApplyCardPicked's SyncGlobalAppearanceIfUsed writes preset values into the shared
        // GlobalFillColor/GlobalBorderColor/GlobalBorderThickness — Clock/Calendar/StickyNote/
        // Panel all read from these when their UseGlobalAppearance flag is true. Without this
        // snapshot, inner Cancel only restores per-widget fields and leaves the live widget
        // visually stuck on the preset.
        string? snapGlobalFill = null, snapGlobalBorder = null;
        double? snapGlobalThickness = null;
        if (Application.Current is App lpApp && lpApp.ManagementWindow?.WidgetService is { } lpSvc)
        {
            var lpCfg = lpSvc.GetConfig();
            snapGlobalFill = lpCfg.GlobalFillColor;
            snapGlobalBorder = lpCfg.GlobalBorderColor;
            snapGlobalThickness = lpCfg.GlobalBorderThickness;
        }

        var applied = PresetButtonsHelper.OpenLoad(this, kind, snap,
            onPicked: picked => ApplyPayload(picked),       // OK: sync UI from model (now at preset state)
            onCardPicked: record => ApplyCardPicked(record)); // each click: write preset → model + refresh live

        if (applied == true)
        {
            // OK — promote _snap* to current (post-preset) state so a later outer Cancel
            // reverts to "post-preset" baseline, preserving the preset across outer Cancel.
            // Mirrors ZoneSettingsDialog._snapshot = ResultZone.Clone() (line 238) — the
            // preset commit and the outer Cancel are independent: outer Cancel undoes only
            // whatever the user did AFTER the preset was applied, not the preset itself.
            // LoadFromXxx(resnapshot:true) also re-syncs UI (harmless — already in sync via
            // onPicked) and re-captures _snapGlobal* from the now-preset global config.
            switch (_target)
            {
                case WidgetSettingsTarget.Clock when _clockModel != null:
                    LoadFromClock(_clockModel, resnapshot: true); break;
                case WidgetSettingsTarget.Calendar when _calModel != null:
                    LoadFromCalendar(_calModel, resnapshot: true); break;
                case WidgetSettingsTarget.StickyNote when _noteModel != null:
                    LoadFromNote(_noteModel, _panelZoneManager, resnapshot: true); break;
                case WidgetSettingsTarget.Panel when _panelConfig != null:
                    LoadFromConfig(_panelConfig, _panelZoneManager, resnapshot: true); break;
            }
        }
        else
        {
            // LoadPresetDialog Cancel — restore model to pre-picker snapshot, then sync UI.
            switch (_target)
            {
                case WidgetSettingsTarget.Clock when _clockModel != null && snap is DesktopClock sClock:
                    CopyClockFields(sClock, _clockModel); break;
                case WidgetSettingsTarget.Calendar when _calModel != null && snap is DesktopCalendar sCal:
                    CopyCalendarFields(sCal, _calModel); break;
                case WidgetSettingsTarget.StickyNote when _noteModel != null && snap is StickyNote sNote:
                    CopyInto(sNote, _noteModel, _noteExcluded); break;
                case WidgetSettingsTarget.Panel when _panelConfig != null && snap is PanelPresetConfig sPanel:
                    CopyPanelFields(sPanel, _panelConfig); break;
            }
            // Restore the shared global config to pre-picker values so widgets using
            // UseGlobalAppearance actually revert visually (see snapshot rationale above).
            if (snapGlobalFill != null)
            {
                bool useGA = _target switch
                {
                    WidgetSettingsTarget.Clock => _clockModel?.UseGlobalAppearance ?? false,
                    WidgetSettingsTarget.Calendar => _calModel?.UseGlobalAppearance ?? false,
                    WidgetSettingsTarget.StickyNote => _noteModel?.UseGlobalAppearance ?? false,
                    WidgetSettingsTarget.Panel => _panelConfig?.PanelUseGlobalAppearance ?? false,
                    _ => false
                };
                if (useGA && Application.Current is App rApp && rApp.ManagementWindow?.WidgetService is { } rSvc)
                {
                    var rCfg = rSvc.GetConfig();
                    if (snapGlobalFill != null) rCfg.GlobalFillColor = snapGlobalFill;
                    if (snapGlobalBorder != null) rCfg.GlobalBorderColor = snapGlobalBorder;
                    rCfg.GlobalBorderThickness = snapGlobalThickness ?? 1.0;
                }
            }
            ApplyPayload(snap);
        }

        // Defensive repaint — Zone's lines:250-256 pattern. Refresh + UpdateLayout + Refresh
        // guarantees the live widget paints the final state after all in-flight
        // setter→PushToWidget chains settle, regardless of which branch ran above.
        RefreshLiveFinal();
    }

    /// <summary>
    /// Defensive chain (ZoneSettingsDialog:253-256) — Refresh + UpdateLayout + Refresh
    /// guarantees the live widget paints the final state after all in-flight
    /// setter→PushToWidget chains settle, regardless of which branch (OK/Cancel) ran.
    /// </summary>
    void RefreshLiveFinal()
    {
        var app = Application.Current as App;
        switch (_target)
        {
            case WidgetSettingsTarget.Clock when _clockModel != null:
                var cWin = app?.GetClockWindow(_clockModel.Id);
                if (cWin != null) { cWin.RefreshAppearance(_clockModel); cWin.UpdateLayout(); cWin.RefreshAppearance(_clockModel); }
                break;
            case WidgetSettingsTarget.Calendar when _calModel != null:
                var calWin = app?.GetCalendarWindow(_calModel.Id);
                if (calWin != null) { calWin.RefreshAppearance(_calModel); calWin.UpdateLayout(); calWin.RefreshAppearance(_calModel); }
                break;
            case WidgetSettingsTarget.StickyNote when _noteModel != null:
                if (app?.NotesService?.Windows.TryGetValue(_noteModel.Id, out var nw) == true && nw is StickyNoteWindow snw)
                    snw.RefreshAppearance(_noteModel);
                break;
            case WidgetSettingsTarget.Panel when _panelConfig != null:
                if (app?.PanelWindow is PanelWindow pw) pw.RefreshAppearance();
                break;
        }
    }

    /// <summary>
    /// Per-card click hook for the Load Preset dialog. Writes the preset's payload into
    /// the live model and refreshes the live widget — mirrors ZoneSettingsDialog's
    /// onCardPicked exactly (single Refresh, no triple-refresh band-aid).
    /// </summary>
    void ApplyCardPicked(PresetRecord record)
    {
        var app = Application.Current as App;
        switch (_target)
        {
            case WidgetSettingsTarget.Clock when _clockModel != null && record is ClockPreset c:
                CopyClockFields(c.Clock, _clockModel);
                SyncGlobalAppearanceIfUsed(_clockModel.UseGlobalAppearance, c.Clock.FillColor, c.Clock.BorderColor, c.Clock.BorderThickness);
                app?.GetClockWindow(_clockModel.Id)?.RefreshAppearance(_clockModel);
                break;
            case WidgetSettingsTarget.Calendar when _calModel != null && record is CalendarPreset cal:
                CopyCalendarFields(cal.Calendar, _calModel);
                SyncGlobalAppearanceIfUsed(_calModel.UseGlobalAppearance, cal.Calendar.FillColor, cal.Calendar.BorderColor, cal.Calendar.BorderThickness);
                app?.GetCalendarWindow(_calModel.Id)?.RefreshAppearance(_calModel);
                break;
            case WidgetSettingsTarget.StickyNote when _noteModel != null && record is StickyNotePreset n:
                CopyInto(n.Note, _noteModel, _noteExcluded);
                // StickyNoteWindow.ApplyAcrylic (StickyNoteWindow.xaml.cs:465-467) reads from
                // the widget service's global config when UseGlobalAppearance=true — same
                // routing as Clock/Calendar. Mirror the sync so preview reaches the live widget.
                SyncGlobalAppearanceIfUsed(_noteModel.UseGlobalAppearance, n.Note.FillColor, n.Note.BorderColor, n.Note.BorderThickness);
                if (app?.NotesService?.Windows.TryGetValue(_noteModel.Id, out var nw) == true && nw is StickyNoteWindow snw)
                    snw.RefreshAppearance(_noteModel);
                break;
            case WidgetSettingsTarget.Panel when _panelConfig != null && record is PanelPreset p:
                CopyPanelFields(p.Config, _panelConfig);
                // PanelWindow.ApplyStyle (PanelWindow.xaml.cs:249-250) reads GlobalBorderColor/
                // GlobalBorderThickness when PanelUseGA=true. Sync just the border fields —
                // do NOT touch GlobalFillColor (Panel always reads its own PanelFillColor,
                // and writing to GlobalFillColor would contaminate Clock/Calendar/StickyNote
                // widgets that share that field).
                if (p.Config.PanelUseGlobalAppearance && Application.Current is App pApp && pApp.ManagementWindow?.WidgetService is { } pSvc)
                {
                    pSvc.GetConfig().GlobalBorderColor = p.Config.PanelBorderColor;
                }
                if (app?.PanelWindow is PanelWindow pw) pw.RefreshAppearance();
                break;
        }
    }

    /// <summary>
    /// ponytail: ApplyAcrylic / ApplyStyle on ClockWidget and CalendarWidget read
    /// FillColor/BorderColor/BorderThickness from the widget service's GLOBAL config
    /// when the model has UseGlobalAppearance=true (see ClockWidget.xaml.cs:261-264,
    /// CalendarWidget.xaml.cs:92-94). CopyClockFields / CopyCalendarFields only update
    /// the per-widget model — leaving the global config stale — so a preset saved
    /// with UseGA=true clicks into ApplyCardPicked, mutates the model, but the widget
    /// keeps reading the OLD GlobalFillColor and visually never changes. PushToWidget
    /// already handled this for slider tweaks (WidgetSettingsDialog.xaml.cs:855-860);
    /// preset card clicks did not. Mirror that logic here so per-card preview reaches
    /// the same global-config branch that PushToWidget keeps in sync.
    /// </summary>
    void SyncGlobalAppearanceIfUsed(bool useGlobal, string fillColor, string borderColor, double borderThickness)
    {
        if (!useGlobal) return;
        if (Application.Current is not App gApp || gApp.ManagementWindow?.WidgetService is not { } wSvc) return;
        var wCfg = wSvc.GetConfig();
        wCfg.GlobalFillColor = fillColor;
        wCfg.GlobalBorderColor = borderColor;
        wCfg.GlobalBorderThickness = borderThickness;
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

    /// <summary>Sync the dialog UI from the live model. Called both after OK (model is at preset state via ApplyCardPicked) and after Cancel (model is at snap state via CopyXxxFields restore). Mirrors MergedGroupSettingsDialog.LoadPreset_Click's onPicked = `picked => SyncFromZone()`.</summary>
    private void ApplyPayload(object? picked)
    {
        // resnapshot: false — must NOT overwrite the original cancel snapshot (taken when the dialog first opened).
        switch (_target)
        {
            case WidgetSettingsTarget.Clock when _clockModel != null:
                LoadFromClock(_clockModel, resnapshot: false);
                break;
            case WidgetSettingsTarget.Calendar when _calModel != null:
                LoadFromCalendar(_calModel, resnapshot: false);
                break;
            case WidgetSettingsTarget.StickyNote when _noteModel != null:
                LoadFromNote(_noteModel, _panelZoneManager, resnapshot: false);
                break;
            case WidgetSettingsTarget.Panel when _panelConfig != null:
                LoadFromConfig(_panelConfig, _panelZoneManager, resnapshot: false);
                break;
        }
    }

    // Ponytail: explicit whitelists mirror ZoneSettingsDialog.CopyZoneFields and
    // MergedGroupSettingsDialog.CopyMergedGroupFields. Blacklist reflection copying was
    // fragile (any new user-state field silently copied) and the redundant re-copy in
    // ApplyPayload was clobbering the UseGA=false restore from ApplyCardPicked.
    // StickyNote still uses the reflection CopyInto + _noteExcluded below — its preview
    // path was reported OK by the user and is not part of this refactor.
    private static readonly HashSet<string> _noteExcluded = new(StringComparer.Ordinal)
    {
        nameof(StickyNote.Id),
        nameof(StickyNote.X), nameof(StickyNote.Y),
        nameof(StickyNote.Width), nameof(StickyNote.Height),
        nameof(StickyNote.IsVisible),
        nameof(StickyNote.Title), nameof(StickyNote.Content), // user data
        nameof(StickyNote.NoteColor),
        nameof(StickyNote.FontSize),
        nameof(StickyNote.PinnedTop),
        nameof(StickyNote.LastSavePath),
        nameof(StickyNote.HotkeyEnabled),
        nameof(StickyNote.HotkeyModifiers),
        nameof(StickyNote.HotkeyKey),
        nameof(StickyNote.CustomHotkeys),
        nameof(StickyNote.CreatedAt), nameof(StickyNote.ModifiedAt),
    };

    private static void CopyInto<T>(T src, T dst, HashSet<string> excluded) where T : class
    {
        // POCOs in this project expose public mutable properties — assignment is enough.
        // Used only by StickyNote (Clock/Calendar/Panel now use explicit whitelists above).
        foreach (var prop in typeof(T).GetProperties())
        {
            if (!prop.CanRead || !prop.CanWrite) continue;
            if (prop.GetSetMethod(true) == null) continue;
            if (excluded.Contains(prop.Name)) continue;
            prop.SetValue(dst, prop.GetValue(src));
        }
    }

    private static void CopyClockFields(DesktopClock src, DesktopClock dst)
    {
        // Pure styling — never copy user-state fields (Id/X/Y/Width/Height/IsVisible/Mode/
        // ShowSeconds/ShowDate/Use24Hour/TextColor/FontSize/FontFamily/Opacity/AccentColor).
        // AnalogFillColor + DigitalFillColor ARE copied (style fields, see Models/AppearanceModel.cs:21-22).
        dst.BorderColor = src.BorderColor;
        dst.FillColor = src.FillColor;
        dst.EnableLiquidGlass = src.EnableLiquidGlass;
        dst.EnableAcrylic = src.EnableAcrylic;     // P3 miss — preset with EnableAcrylic=false needs to actually disable blur on preview
        dst.GlassBlurAmount = src.GlassBlurAmount;
        dst.GlassTintOpacity = src.GlassTintOpacity;
        dst.GlassTintLuminosity = src.GlassTintLuminosity;
        dst.GlassColorMode = src.GlassColorMode;
        dst.BackgroundImagePath = src.BackgroundImagePath;
        dst.BgImageStretch = src.BgImageStretch;   // P3 miss — Zone honors it; harmless for Clock/Calendar but keeps the whitelist in sync with AppearanceModel
        dst.BgImageZoom = src.BgImageZoom;
        dst.BgImageOffsetX = src.BgImageOffsetX;
        dst.BgImageOffsetY = src.BgImageOffsetY;
        dst.EnableRestoreButton = src.EnableRestoreButton;
        dst.AnalogFillColor = src.AnalogFillColor;
        dst.DigitalFillColor = src.DigitalFillColor;
        dst.BackgroundImageOpacity = src.BackgroundImageOpacity;
        dst.BorderThickness = src.BorderThickness;
        dst.UseGlobalAppearance = src.UseGlobalAppearance;
        dst.DigitalBackgroundImagePath = src.DigitalBackgroundImagePath;
        dst.DigitalBgImageZoom = src.DigitalBgImageZoom;
        dst.DigitalBgImageOffsetX = src.DigitalBgImageOffsetX;
        dst.DigitalBgImageOffsetY = src.DigitalBgImageOffsetY;
        dst.DigitalBackgroundImageOpacity = src.DigitalBackgroundImageOpacity;
        dst.DigitalBgImageStretch = src.DigitalBgImageStretch;
    }

    private static void CopyCalendarFields(DesktopCalendar src, DesktopCalendar dst)
    {
        // Pure styling — never copy Id/X/Y/Width/Height/IsVisible/ShowWeekNumbers/
        // StartOnMonday/Notes/Opacity.
        dst.BorderColor = src.BorderColor;
        dst.FillColor = src.FillColor;
        dst.EnableLiquidGlass = src.EnableLiquidGlass;
        dst.EnableAcrylic = src.EnableAcrylic;     // P3 miss — same rationale as CopyClockFields
        dst.GlassBlurAmount = src.GlassBlurAmount;
        dst.GlassTintOpacity = src.GlassTintOpacity;
        dst.GlassTintLuminosity = src.GlassTintLuminosity;
        dst.GlassColorMode = src.GlassColorMode;
        dst.BackgroundImagePath = src.BackgroundImagePath;
        dst.BgImageStretch = src.BgImageStretch;
        dst.BgImageZoom = src.BgImageZoom;
        dst.BgImageOffsetX = src.BgImageOffsetX;
        dst.BgImageOffsetY = src.BgImageOffsetY;
        dst.EnableRestoreButton = src.EnableRestoreButton;
        dst.BorderThickness = src.BorderThickness;
        dst.UseGlobalAppearance = src.UseGlobalAppearance;
        dst.BackgroundImageOpacity = src.BackgroundImageOpacity;
        dst.TextColor = src.TextColor;
        dst.TodayColor = src.TodayColor;
        dst.FontSize = src.FontSize;
    }

    private static void CopyPanelFields(Models.PanelPresetConfig src, AppConfig dst)
    {
        // Pure styling — PanelX/PanelY/PanelWidth/PanelHeight are intentionally NOT
        // copied: user wants panel position/size preserved when applying a style preset.
        dst.PanelUseGlobalAppearance = src.PanelUseGlobalAppearance;
        dst.PanelFillColor = src.PanelFillColor;
        dst.PanelBorderColor = src.PanelBorderColor;
        dst.PanelTitleBarFillColor = src.PanelTitleBarFillColor;
        dst.PanelControlOpacity = src.PanelControlOpacity;
        dst.PanelBackgroundImagePath = src.PanelBackgroundImagePath;
        dst.PanelBgImageStretch = src.PanelBgImageStretch;
        dst.PanelBackgroundImageOpacity = src.PanelBackgroundImageOpacity;
        dst.PanelBgImageZoom = src.PanelBgImageZoom;
        dst.PanelBgImageOffsetX = src.PanelBgImageOffsetX;
        dst.PanelBgImageOffsetY = src.PanelBgImageOffsetY;
        dst.GlassColorMode = src.GlassColorMode;
    }

    // ── Appearance field mappers (P3) ──
    // Push: dialog → model. Pull: model → dialog. Generic over AppearanceModel
    // so the same 14 fields are handled for all 3 widget kinds (Clock/Calendar/StickyNote).
    // BorderThickness and BackgroundImageOpacity stay per-model and are handled by the caller.

    /// <summary>Writes the 13 shared AppearanceModel fields from dialog state into <paramref name="model"/>.
    /// UseGlobalAppearance is per-widget (Zone has none) — caller sets it after.</summary>
    void PushAppearanceFields<T>(T model) where T : AppearanceModel
    {
        model.BorderColor = BorderColorValue;
        // Round-trip guard: only re-derive FillColor when the user touched the
        // opacity slider / a preset / the custom picker. Otherwise pass through
        // the original hex from LoadFromXxx (already byte-exact). Prevents
        // "#08000000" → "#07000000" drift on a no-op Apply.
        model.FillColor = _fillColorTouched ? UpdateFillFromOpacity() : _fillColor;
        model.EnableLiquidGlass = _liquidGlass;
        model.GlassBlurAmount = _glassBlurAmount;
        model.GlassTintOpacity = _glassTintOpacity;
        model.GlassTintLuminosity = _glassTintLuminosity;
        model.GlassColorMode = _glassColorMode;
        model.BackgroundImagePath = _bgImagePath;
        model.BgImageOffsetX = _bgOffsetX;
        model.BgImageOffsetY = _bgOffsetY;
        model.BgImageZoom = _bgZoom;
        model.EnableRestoreButton = _enableRestoreButton;
        // Text color adaptive — checkbox state writes back to model
        if (TextAdaptiveBox != null)
            model.TextColorAdaptive = TextAdaptiveBox.IsChecked == true;
    }

    /// <summary>Reads the 13 shared AppearanceModel fields from <paramref name="model"/> into dialog state.
    /// UseGlobalAppearance is per-widget — caller sets UseGlobalAppearance property after.
    /// ponytail: FillColor / BorderColor / BorderThickness resolve to the EFFECTIVE value
    /// (UseGlobal ? Global* : per-widget) — same conditional as ClockWidget.SyncFillRect /
    /// CalendarWidget.SyncFillRect — so the dialog picker shows exactly what the widget
    /// is actually displaying. Without this, when UseGlobalAppearance=true, opening the
    /// dialog would overwrite config.GlobalFillColor with clock.FillColor (different field)
    /// the moment Apply was clicked, even if the user changed nothing.</summary>
    void PullAppearanceFields<T>(T model) where T : AppearanceModel
    {
        var cfg = TryGetWidgetConfig();
        dynamic dyn = model;   // UseGlobalAppearance + BorderThickness aren't on AppearanceModel; resolve at runtime
        bool useGA = cfg != null && (bool)dyn.UseGlobalAppearance;
        string effectiveFill = useGA ? cfg!.GlobalFillColor : model.FillColor;
        string effectiveBorder = useGA ? cfg!.GlobalBorderColor : model.BorderColor;
        double effectiveBorderThickness = useGA ? cfg!.GlobalBorderThickness : (double)dyn.BorderThickness;

        BorderColorValue = effectiveBorder;
        _fillColor = effectiveFill;
        _fillOpacityPercent = ParseOpacity(_fillColor);
        FillOpacitySlider.Value = _fillOpacityPercent;
        FillOpacityLabel.Text = $"{(int)_fillOpacityPercent}%";
        _liquidGlass = model.EnableLiquidGlass;
        LiquidGlassToggle.IsChecked = _liquidGlass;
        UpdateLiquidButton();
        _glassBlurAmount = model.GlassBlurAmount;
        _glassTintOpacity = model.GlassTintOpacity;
        _glassTintLuminosity = model.GlassTintLuminosity;
        _glassColorMode = model.GlassColorMode;
        _enableRestoreButton = model.EnableRestoreButton;
        EnableRestoreButtonToggle.IsChecked = _enableRestoreButton;
        _bgImagePath = model.BackgroundImagePath;
        BgImagePathBox.Text = _bgImagePath;
        if (CropBtn != null) CropBtn.IsEnabled = !string.IsNullOrEmpty(_bgImagePath) && System.IO.File.Exists(_bgImagePath);
        _bgOffsetX = model.BgImageOffsetX;
        OffsetXBox.Text = _bgOffsetX.ToString("F0");
        _bgOffsetY = model.BgImageOffsetY;
        OffsetYBox.Text = _bgOffsetY.ToString("F0");
        _bgZoom = model.BgImageZoom;
        ZoomSlider.Value = _bgZoom;
        ZoomLabel.Text = $"{_bgZoom:F1}x";
        BorderThicknessText = effectiveBorderThickness.ToString("F1");
        // Text color adaptive — sync checkbox
        if (TextAdaptiveBox != null) TextAdaptiveBox.IsChecked = model.TextColorAdaptive;
        // Title bar adaptive (sticky-note-only) — handled per-target in LoadFrom*
        UpdateHighlights();
    }

    /// <summary>Restores the 13 shared AppearanceModel fields on <paramref name="model"/> from the
    /// dialog's cancel-revert snapshot. UseGlobalAppearance is per-widget — caller sets after.</summary>
    void CancelRestoreFields<T>(T model) where T : AppearanceModel
    {
        model.BorderColor = _snapBorderColor;
        model.FillColor = _snapFillColor;
        model.EnableLiquidGlass = _snapLiquidGlass;
        model.GlassBlurAmount = _snapGlassBlur;
        model.GlassTintOpacity = _snapGlassTintOpacity;
        model.GlassTintLuminosity = _snapGlassTintLuminosity;
        model.GlassColorMode = _snapGlassColorMode;
        model.BackgroundImagePath = _snapBgImagePath;
        model.BgImageOffsetX = _snapBgOffsetX;
        model.BgImageOffsetY = _snapBgOffsetY;
        model.BgImageZoom = _snapBgZoom;
        model.EnableRestoreButton = _snapEnableRestore;
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
                CancelRestoreFields(_noteModel);
                _noteModel.UseGlobalAppearance = _snapUseGlobal;
                _noteModel.BorderThickness = _snapBorderThickness;
                _noteModel.BackgroundImageOpacity = _snapBgOpacity;
                _noteModel.TitleBarFillColor = _snapTitleBarFill; _noteModel.TitleBarOpacity = _snapTitleBarOpacity;
                _noteModel.ControlOpacity = _snapButtonOpacity; _noteModel.TitleTextColor = _snapTitleTextColor;
                _noteModel.TextColorAdaptive = _snapTextAdaptive;
                _noteModel.TitleBarTextColorAdaptive = _snapTitleBarTextAdaptive;
                if (double.TryParse(_snapWidgetWidth, out var rw)) _noteModel.Width = rw;
                if (double.TryParse(_snapWidgetHeight, out var rh)) _noteModel.Height = rh;
                if (Application.Current is App app3 && app3.NotesService?.Windows.TryGetValue(_noteModel.Id, out var noteWin3) == true)
                    noteWin3.RefreshAppearance();
                break;

            case WidgetSettingsTarget.Panel when _panelConfig != null:
                _panelConfig.PanelFillColor = _snapFillColor; _panelConfig.GlobalBorderColor = _snapBorderColor;
                _panelConfig.PanelBorderColor = _snapPanelBorderColor;
                _panelConfig.GlobalBorderThickness = _snapBorderThickness; _panelConfig.PanelUseGlobalAppearance = _snapUseGlobal;
                _panelConfig.PanelTitleBarFillColor = _snapTitleBarFill; _panelConfig.PanelControlOpacity = _snapButtonOpacity;
                _panelConfig.PanelBackgroundImagePath = _snapBgImagePath; _panelConfig.PanelBgImageOffsetX = _snapBgOffsetX;
                _panelConfig.PanelBgImageOffsetY = _snapBgOffsetY; _panelConfig.PanelBgImageZoom = _snapBgZoom;
                _panelConfig.PanelBackgroundImageOpacity = _snapBgOpacity;
                _panelConfig.EnableLiquidGlass = _snapLiquidGlass; _panelConfig.GlassBlurAmount = _snapGlassBlur;
                _panelConfig.GlassTintOpacity = _snapGlassTintOpacity; _panelConfig.GlassTintLuminosity = _snapGlassTintLuminosity;
                _panelConfig.GlassColorMode = _snapGlassColorMode;
                _panelConfig.PanelBgImageStretch = _snapPanelBgImageStretch;
                // Always restore global values (PushToWidget may have modified them when UseGlobal was toggled)
                _panelConfig.GlobalFillColor = _snapGlobalFillColor;
                _panelConfig.PanelTextColorAdaptive = _snapTextAdaptive;
                _panelConfig.PanelTitleBarTextColorAdaptive = _snapTitleBarTextAdaptive;
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
                CancelRestoreFields(_clockModel);
                _clockModel.UseGlobalAppearance = _snapUseGlobal;
                _clockModel.BorderThickness = _snapBorderThickness;
                _clockModel.BackgroundImageOpacity = _snapBgOpacity;
                _clockModel.TextColorAdaptive = _snapTextAdaptive;
                // Always restore global values
                if (Application.Current is App cAppC && cAppC.ManagementWindow?.WidgetService is { } wSvcC)
                {
                    var wCfgC = wSvcC.GetConfig();
                    wCfgC.GlobalBorderThickness = _snapGlobalBorderThickness;
                    wCfgC.GlobalFillColor = _snapGlobalFillColor;
                }
                _clockModel.DigitalBackgroundImagePath = _snapDigitalBgImagePath;
                _clockModel.DigitalBgImageOffsetX = _snapDigitalBgOffsetX; _clockModel.DigitalBgImageOffsetY = _snapDigitalBgOffsetY;
                _clockModel.DigitalBgImageZoom = _snapDigitalBgZoom; _clockModel.DigitalBackgroundImageOpacity = _snapDigitalBgOpacity;
                if (Application.Current is App appC2)
                    appC2.GetClockWindow(_clockModel.Id)?.RefreshAppearance();
                break;

            case WidgetSettingsTarget.Calendar when _calModel != null:
                CancelRestoreFields(_calModel);
                _calModel.UseGlobalAppearance = _snapUseGlobal;
                _calModel.BorderThickness = _snapBorderThickness;
                _calModel.BackgroundImageOpacity = _snapBgOpacity;
                _calModel.TextColorAdaptive = _snapTextAdaptive;
                // Always restore global values
                if (Application.Current is App calAppC && calAppC.ManagementWindow?.WidgetService is { } wSvcCal)
                {
                    var wCfgCal = wSvcCal.GetConfig();
                    wCfgCal.GlobalBorderThickness = _snapGlobalBorderThickness;
                    wCfgCal.GlobalFillColor = _snapGlobalFillColor;
                }
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

    void TextAdaptive_Changed(object s, RoutedEventArgs e)
    {
        // Live preview: refresh the target widget's text colors
        TriggerRefreshTextColorAdaptive();
    }

    void TitleBarTextAdaptive_Changed(object s, RoutedEventArgs e)
    {
        TriggerRefreshTextColorAdaptive();
    }

    void TriggerRefreshTextColorAdaptive()
    {
        if (_suppressPreview) return;
        switch (_target)
        {
            case WidgetSettingsTarget.Clock:
                {
                    // ponytail: null-guard the model — XAML-load Checked event can fire before
                    // LoadFrom* assigns _clockModel. Belt-and-suspenders alongside _suppressPreview.
                    if (_clockModel == null) break;
                    if (Application.Current is App app && app.ManagementWindow?.WidgetService is { } svc)
                    {
                        if (svc.GetClockWindow(_clockModel.Id) is ClockWidget cw)
                            cw.RefreshTextColorAdaptive();
                    }
                    break;
                }
            case WidgetSettingsTarget.Calendar:
                {
                    if (_calModel == null) break;
                    if (Application.Current is App app && app.ManagementWindow?.WidgetService is { } svc)
                    {
                        if (svc.GetCalendarWindow(_calModel.Id) is CalendarWidget calw)
                            calw.RefreshTextColorAdaptive();
                    }
                    break;
                }
            case WidgetSettingsTarget.Panel:
                {
                    if (_panelConfig == null) break;
                    if (Application.Current is App pApp && pApp.PanelWindow is PanelWindow pw)
                    {
                        pw.RefreshTextColorAdaptive();
                    }
                    break;
                }
            case WidgetSettingsTarget.StickyNote:
                {
                    if (_noteModel == null) break;
                    if (Application.Current is App app && app.ManagementWindow?.NotesService is { } nsvc)
                    {
                        if (nsvc.Windows.TryGetValue(_noteModel.Id, out var w) && w is StickyNoteWindow snw)
                            snw.RefreshTextColorAdaptive();
                    }
                    break;
                }
        }
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

    /// <summary>Resolve the shared widget-service config so PullAppearanceFields can compute the
    /// "effective" (UseGlobal-aware) FillColor/BorderColor/BorderThickness — same source as
    /// ClockWidget.SyncFillRect / CalendarWidget.SyncFillRect. Returns null when App/ManagementWindow/
    /// WidgetService aren't reachable (test environments, design-time, etc.) so the caller can
    /// fall back to per-widget values without crashing.</summary>
    AppConfig? TryGetWidgetConfig()
    {
        if (Application.Current is App app && app.ManagementWindow?.WidgetService is { } svc)
            return svc.GetConfig();
        return null;
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
