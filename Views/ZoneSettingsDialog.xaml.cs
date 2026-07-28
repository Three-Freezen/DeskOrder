using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopZones.Helpers;
using DesktopZones.Models;
using DesktopZones.Services;

namespace DesktopZones.Views;

public partial class ZoneSettingsDialog : Window, INotifyPropertyChanged
{
    private readonly Zone _editingZone;
    private readonly ZoneManager _zoneManager;
    private readonly LocalizationService _loc = LocalizationService.Instance;
    private Zone _snapshot; // for cancel-revert — reassigned after LoadPreset Apply to preserve the preset across an outer Cancel
    private bool _suppressPreview; // suppress live preview during init
    public Zone ResultZone { get; private set; }

    private string _zoneName = "";
    public string ZoneName { get => _zoneName; set { _zoneName = value; OnPropertyChanged(); PushToZone(); } }
    private string _zoneWidth = "400";
    public string ZoneWidth { get => _zoneWidth; set { _zoneWidth = value; OnPropertyChanged(); PushToZone(); } }
    private string _zoneHeight = "300";
    public string ZoneHeight { get => _zoneHeight; set { _zoneHeight = value; OnPropertyChanged(); PushToZone(); } }
    private string _gridSize = "80";
    public string GridSize
    {
        get => _gridSize;
        set
        {
            _gridSize = value;
            OnPropertyChanged();
            PushToZone();
        }
    }
    private bool _snapToGrid = true;
    public bool SnapToGrid { get => _snapToGrid; set { _snapToGrid = value; OnPropertyChanged(); PushToZone(); } }
    private string _borderThicknessText = "1.5";
    public string BorderThicknessText { get => _borderThicknessText; set { _borderThicknessText = value; OnPropertyChanged(); PushToZone(); } }

    private string _borderColor = "#30FFFFFF";
    public string BorderColorValue { get => _borderColor; set { _borderColor = value; UpdateHighlights(); OnPropertyChanged(); PushToZone(); } }
    private string _fillColor = "#08000000";
    public string FillColorValue { get => _fillColor; set { _fillColor = value; _fillOpacityPercent = ParseOpacity(value); UpdateHighlights(); OnPropertyChanged(); OnPropertyChanged(nameof(FillOpacityPercent)); PushToZone(); } }
    private string _titleBarFill = "#10FFFFFF";
    public string TitleBarFillValue { get => _titleBarFill; set { _titleBarFill = value; _titleBarOpacityPercent = ParseOpacity(value); UpdateHighlights(); OnPropertyChanged(); OnPropertyChanged(nameof(TitleBarOpacityPercent)); PushToZone(); } }
    private string _bgImagePath = "";
    private bool _isLoading = true;
    public string BgImagePath { get => _bgImagePath; set { _bgImagePath = value; if (!string.IsNullOrEmpty(value) && !_isLoading) _fillColor = "#01000000"; if (CropBtn != null) CropBtn.IsEnabled = !string.IsNullOrEmpty(value) && File.Exists(value); OnPropertyChanged(); PushToZone(); } }
    private string _iconCharText = "";
    public string IconCharText { get => _iconCharText; set { _iconCharText = value; IconPreview.Text = string.IsNullOrEmpty(value) ? "⊞" : value[..Math.Min(value.Length, 2)]; OnPropertyChanged(); PushToZone(); } }
    private string _iconColor = "#FFFFFF";
    public string IconColorValue { get => _iconColor; set { _iconColor = value; UpdateHighlights(); OnPropertyChanged(); PushToZone(); } }
    private string _textColor = "#A0FFFFFF";
    public string TextColorValue { get => _textColor; set { _textColor = value; UpdateHighlights(); OnPropertyChanged(); PushToZone(); } }

    private double _fillOpacityPercent = 8;
    public double FillOpacityPercent { get => _fillOpacityPercent; set { _fillOpacityPercent = value; UpdateFillFromOpacity(); OnPropertyChanged(); } }
    private double _titleBarOpacityPercent = 6;
    public double TitleBarOpacityPercent { get => _titleBarOpacityPercent; set { _titleBarOpacityPercent = value; UpdateTitleBarFromOpacity(); OnPropertyChanged(); } }
    private double _ctrlOpacity = 40;
    public double CtrlOpacity { get => _ctrlOpacity; set { _ctrlOpacity = value; OnPropertyChanged(); PushToZone(); } }
    private double _bgImageOpacity = 40;
    public double BgImageOpacityPercent { get => _bgImageOpacity; set { _bgImageOpacity = value; OnPropertyChanged(); PushToZone(); } }
    private bool _autoArrange;
    public bool AutoArrange { get => _autoArrange; set { _autoArrange = value; OnPropertyChanged(); PushToZone(); } }
    private double _bgOffsetX;
    public string BgOffsetX { get => _bgOffsetX.ToString("F0"); set { if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) _bgOffsetX = v; OnPropertyChanged(); PushToZone(); } }
    private double _bgOffsetY;
    public string BgOffsetY { get => _bgOffsetY.ToString("F0"); set { if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) _bgOffsetY = v; OnPropertyChanged(); PushToZone(); } }
    private double _bgZoomVal = 1.0;
    public double BgZoomVal { get => _bgZoomVal; set { _bgZoomVal = value; OnPropertyChanged(); PushToZone(); } }

    private bool _acrylicEnabled = true;
    public bool AcrylicEnabled { get => _acrylicEnabled; set { _acrylicEnabled = value; OnPropertyChanged(); PushToZone(); } }

    // ── Liquid Glass settings ──
    private int _glassBlurAmount = 18;
    public int GlassBlurAmount { get => _glassBlurAmount; set { _glassBlurAmount = value; OnPropertyChanged(); PushToZone(); } }
    private int _glassTintOpacity = 50;
    public int GlassTintOpacity { get => _glassTintOpacity; set { _glassTintOpacity = value; OnPropertyChanged(); PushToZone(); } }
    private int _glassTintLuminosity = 100;
    public int GlassTintLuminosity { get => _glassTintLuminosity; set { _glassTintLuminosity = value; OnPropertyChanged(); PushToZone(); } }
    private string _glassColorMode = "Default";
    public string GlassColorMode { get => _glassColorMode; set { _glassColorMode = value; OnPropertyChanged(); PushToZone(); } }
    private bool _liquidGlass = true;
    public bool LiquidGlassEnabled { get => _liquidGlass; set { _liquidGlass = value; OnPropertyChanged(); PushToZone(); } }

    private bool _quickBarMode;
    public bool QuickBarMode { get => _quickBarMode; set { _quickBarMode = value; OnPropertyChanged(); PushToZone(); } }

    private bool _enableRestoreButton = true;
    public bool EnableRestoreButton { get => _enableRestoreButton; set { _enableRestoreButton = value; OnPropertyChanged(); PushToZone(); } }

    private bool _useGlobalAppearance = true;
    public bool UseGlobalAppearance { get => _useGlobalAppearance; set { _useGlobalAppearance = value; OnPropertyChanged(); PushToZone(); } }

    private Action<Services.Language>? _langChanged;
    private Zone? _loadDialogSnapshot;        // captured before opening LoadPresetDialog for cancel-revert
    private DispatcherTimer? _savedHintTimer;
    private PresetService? _zonePresetService;
    private PresetService ZonePresetService => _zonePresetService ??= new PresetService("Zones");

    public ZoneSettingsDialog(Zone zone, ZoneManager zoneManager)
    {
        InitializeComponent();
        _editingZone = zone.Clone(); _zoneManager = zoneManager; ResultZone = zone;
        _snapshot = zone.Clone(); // snapshot for cancel-revert
        _suppressPreview = true;

        ZoneName = zone.Name; ZoneWidth = zone.Width.ToString("F0"); ZoneHeight = zone.Height.ToString("F0");
        GridSize = zone.GridSize.ToString(); SnapToGrid = zone.SnapToGrid;
        BorderThicknessText = zone.BorderThickness.ToString("F1");
        BorderColorValue = zone.BorderColor; FillColorValue = zone.FillColor;
        TitleBarFillValue = zone.TitleBarFillColor; BgImagePath = zone.BackgroundImagePath;
        IconCharText = zone.IconChar; CtrlOpacity = zone.ControlOpacity;
        BgImageOpacityPercent = zone.BackgroundImageOpacity; AutoArrange = zone.AutoArrange;
        _bgOffsetX = zone.BgImageOffsetX; _bgOffsetY = zone.BgImageOffsetY; _bgZoomVal = zone.BgImageZoom;
        AcrylicEnabled = zone.EnableAcrylic;
        _glassBlurAmount = zone.GlassBlurAmount;
        _glassTintOpacity = zone.GlassTintOpacity;
        _glassTintLuminosity = zone.GlassTintLuminosity;
        _glassColorMode = zone.GlassColorMode;
        _liquidGlass = zone.EnableLiquidGlass;
        _quickBarMode = zone.QuickBarMode;
        _enableRestoreButton = zone.EnableRestoreButton;
        IconColorValue = string.IsNullOrEmpty(zone.IconColor) ? "#FFFFFF" : zone.IconColor;
        _useGlobalAppearance = _zoneManager.GetConfig().UseGlobalAppearance;

        TextColorValue = string.IsNullOrEmpty(zone.TitleTextColor) ? "#A0FFFFFF" : zone.TitleTextColor;

        // Stretch unified to UniformToFill — BgStretchCombo removed

        DataContext = this; ApplyLoc();
        UpdateGlassSection();
        _langChanged = _ => ApplyLoc();
        _loc.LanguageChanged += _langChanged;
        _isLoading = false;
        _suppressPreview = false;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_langChanged != null) _loc.LanguageChanged -= _langChanged;
        _langChanged = null;
        base.OnClosed(e);
    }

    void ApplyLoc()
    {
        var cn = _loc.CurrentLanguage == Services.Language.Chinese;
        DialogTitle.Text = _loc["Settings.Title"];

        // Glass effect labels
        LiquidGlassSettingsBtn.Content = cn ? "💧 液态玻璃设置" : "💧 Liquid Glass Settings";
        LabelGlassIntensity.Text = cn ? "液态玻璃" : "Liquid Glass";

        LabelName.Text = _loc["Settings.Name"];
        LabelQuickBarMode.Text = cn ? "极简模式" : "Minimal Mode";
        LabelEnableRestoreButton.Text = cn ? "恢复按钮" : "Restore Button";
        LabelIcon.Text = _loc["Settings.Icon"]; LabelWidth.Text = _loc["Settings.Width"];
        LabelHeight.Text = _loc["Settings.Height"]; LabelGridSize.Text = _loc["Settings.GridSize"];
        LabelSnapToGrid.Text = _loc["Settings.SnapToGrid"];
        LabelUseGlobal.Text = cn ? "全局外观" : "Global Appearance";
        LabelBorderThickness.Text = _loc["Settings.BorderThickness"];
        LabelBorderColor.Text = _loc["Settings.BorderColor"]; LabelFillColor.Text = _loc["Settings.FillColor"];
        LabelOpacity.Text = cn ? "填充透明度" : "Fill Opacity";
        LabelTitleOpacity.Text = cn ? "标题栏透明度" : "Title Bar Opacity";
        LabelCtrlOpacity.Text = cn ? "按钮透明度" : "Button Opacity";
        LabelTitleBar.Text = cn ? "标题栏填充" : "Title Bar Fill";
        LabelBgImage.Text = _loc["Settings.BgImage"];
        ApplyButton.Content = _loc["Settings.Apply"]; CancelButton.Content = _loc["Settings.Cancel"];
        BorderCustomBtn.Content = cn ? "自定义..." : "Custom..."; FillCustomBtn.Content = cn ? "自定义..." : "Custom...";
        TitleCustomBtn.Content = cn ? "自定义..." : "Custom...";
        IconColorCustomBtn.Content = cn ? "自定义..." : "Custom...";
        TextColorCustomBtn.Content = cn ? "自定义..." : "Custom...";
        ClearBgBtn.Content = cn ? "清除" : "Clear";
        LabelBgOpacity.Text = cn ? "图片透明度" : "Image Opacity";
        MoreEmojiBtn.Content = "…";
        LabelAutoArrange.Text = cn ? "尺寸变化时自动重排" : "Auto-rearrange on resize";
        // LabelBgStretch removed — stretch unified
        LabelOffsetX.Text = cn ? "水平偏移" : "Offset X";
        LabelOffsetY.Text = cn ? "垂直偏移" : "Offset Y";
        LabelZoom.Text = cn ? "缩放" : "Zoom";
        LabelTextColor.Text = cn ? "分区名称颜色" : "Zone Name Color";
        LabelIconColor.Text = cn ? "分区图标颜色" : "Zone Icon Color";
        CropFill.Content = cn ? "拉伸填充" : "Fill";
        CropUniform.Content = cn ? "等比缩放" : "Uniform";
        CropUniformToFill.Content = cn ? "等比填充" : "UniformToFill";
        CropNone.Content = cn ? "原始尺寸" : "None";

        SavePresetButton.Content = _loc["Preset.SaveButton"];
        LoadPresetButton.Content = _loc["Preset.LoadButton"];
        SavedHint.Text = _loc["Preset.Saved"];
    }

    // ── Preset actions ──

    void SavePresetButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SavePresetDialog(ZonePresetService, ResultZone) { Owner = this };
        if (dlg.ShowDialog() == true) ShowSavedHint();
    }

    void LoadPresetButton_Click(object sender, RoutedEventArgs e)
    {
        // Snapshot the zone before opening, so Cancel can restore both the live window
        // and the dialog state. Outer Cancel still reverts from _snapshot (unchanged).
        _loadDialogSnapshot = ResultZone.Clone();

        var dlg = new LoadPresetDialog(ZonePresetService, onCardPicked: preset =>
        {
            // Real-time preview: write preset values to ResultZone and refresh the live window.
            CopyZoneFields(preset.Zone, ResultZone);
            _zoneManager.GetZoneWindow(ResultZone.Id)?.RefreshZone(ResultZone);
        }) { Owner = this };

        if (dlg.ShowDialog() == true)
        {
            // Final commit: copy the chosen preset's values to ResultZone and sync all
            // dialog controls (TextBox/CheckBox/Slider/...) so subsequent edits stay consistent.
            CopyZoneFields(dlg.SelectedPreset!.Zone, ResultZone);
            SyncFromZone(ResultZone);
            _zoneManager.GetZoneWindow(ResultZone.Id)?.RefreshZone(ResultZone);

            // Promote the applied preset to the snapshot baseline so a later outer Cancel
            // reverts to "post-preset" state — the preset itself is preserved across Cancel.
            _snapshot = ResultZone.Clone();
        }
        else
        {
            // Cancel — revert ResultZone to the state captured before the dialog opened.
            if (_loadDialogSnapshot != null)
            {
                CopyZoneFields(_loadDialogSnapshot, ResultZone);
                _zoneManager.GetZoneWindow(ResultZone.Id)?.RefreshZone(ResultZone);
            }
        }

        // Defensive chain: ensure all in-flight setter→PushToZone→RefreshZone cycles have
        // settled, then force one more layout pass + refresh so the live window paints
        // the final state regardless of which branch ran above.
        var win = _zoneManager.GetZoneWindow(ResultZone.Id);
        win?.RefreshZone(ResultZone);
        win?.UpdateLayout();
        win?.RefreshZone(ResultZone);

        _loadDialogSnapshot = null;
    }

    static void CopyZoneFields(Zone src, Zone dst)
    {
        dst.Name = src.Name; dst.Width = src.Width; dst.Height = src.Height;
        dst.GridSize = src.GridSize; dst.SnapToGrid = src.SnapToGrid;
        dst.BorderThickness = src.BorderThickness; dst.BorderColor = src.BorderColor;
        dst.FillColor = src.FillColor; dst.TitleBarFillColor = src.TitleBarFillColor;
        dst.BackgroundImagePath = src.BackgroundImagePath; dst.IconChar = src.IconChar;
        dst.ControlOpacity = src.ControlOpacity;
        dst.BackgroundImageOpacity = src.BackgroundImageOpacity;
        dst.AutoArrange = src.AutoArrange;
        dst.BgImageOffsetX = src.BgImageOffsetX; dst.BgImageOffsetY = src.BgImageOffsetY;
        dst.BgImageZoom = src.BgImageZoom;
        dst.IconColor = src.IconColor; dst.TitleTextColor = src.TitleTextColor;
        dst.EnableAcrylic = src.EnableAcrylic;
        dst.GlassBlurAmount = src.GlassBlurAmount;
        dst.GlassTintOpacity = src.GlassTintOpacity;
        dst.GlassTintLuminosity = src.GlassTintLuminosity;
        dst.GlassColorMode = src.GlassColorMode;
        dst.EnableLiquidGlass = src.EnableLiquidGlass;
        dst.QuickBarMode = src.QuickBarMode;
        dst.EnableRestoreButton = src.EnableRestoreButton;
    }

    void ShowSavedHint()
    {
        SavedHint.Visibility = Visibility.Visible;
        _savedHintTimer?.Stop();
        _savedHintTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _savedHintTimer.Tick += (_, _) =>
        {
            SavedHint.Visibility = Visibility.Collapsed;
            _savedHintTimer!.Stop();
        };
        _savedHintTimer.Start();
    }

    void UpdateHighlights()
    {
        HP(BorderColorPresets, BorderColorValue); HP(FillColorPresets, FillColorValue);
        HP(TitleBarPresets, TitleBarFillValue); HP(IconColorPresets, IconColorValue);
        HP(TextColorPresets, TextColorValue);
    }
    static void HP(Panel p, string s) { foreach (var c in p.Children) { if (c is Border b && b.Tag is string t) b.BorderThickness = new Thickness(t == s ? 3 : 1); } }

    void BorderColorPreset_Click(object s, MouseButtonEventArgs e) { if (s is Border b && b.Tag is string c) BorderColorValue = c; }
    void FillColorPreset_Click(object s, MouseButtonEventArgs e) { if (s is Border b && b.Tag is string c) FillColorValue = c; }
    void TitleBarColorPreset_Click(object s, MouseButtonEventArgs e) { if (s is Border b && b.Tag is string c) TitleBarFillValue = c; }
    void IconColorPreset_Click(object s, MouseButtonEventArgs e) { if (s is Border b && b.Tag is string c) IconColorValue = c; }
    void TextColorPreset_Click(object s, MouseButtonEventArgs e) { if (s is Border b && b.Tag is string c) TextColorValue = c; }

    void BorderCustom_Click(object s, RoutedEventArgs e) { var d = new ColorPickerDialog(BorderColorValue.Length >= 9 ? BorderColorValue[3..] : "FFFFFF") { Owner = this }; if (d.ShowDialog() == true) BorderColorValue = (BorderColorValue.Length >= 3 ? BorderColorValue[..3] : "#40") + d.SelectedColor; }
    void FillCustom_Click(object s, RoutedEventArgs e) { var d = new ColorPickerDialog(FillColorValue.Length >= 9 ? FillColorValue[3..] : "000000") { Owner = this }; if (d.ShowDialog() == true) { var alpha = FillColorValue.Length >= 3 ? FillColorValue[..3] : "#08"; FillColorValue = alpha + d.SelectedColor; } }
    void TitleCustom_Click(object s, RoutedEventArgs e) { var d = new ColorPickerDialog(TitleBarFillValue.Length >= 9 ? TitleBarFillValue[3..] : "FFFFFF") { Owner = this }; if (d.ShowDialog() == true) TitleBarFillValue = (TitleBarFillValue.Length >= 3 ? TitleBarFillValue[..3] : "#10") + d.SelectedColor; }
    void IconColorCustom_Click(object s, RoutedEventArgs e) { var d = new ColorPickerDialog(IconColorValue.Length >= 7 ? IconColorValue[1..] : "FFFFFF") { Owner = this }; if (d.ShowDialog() == true) IconColorValue = "#" + d.SelectedColor; }
    void TextColorCustom_Click(object s, RoutedEventArgs e) { var d = new ColorPickerDialog(TextColorValue.Length >= 9 ? TextColorValue[3..] : TextColorValue.Length >= 7 ? TextColorValue[1..] : "FFFFFF") { Owner = this }; if (d.ShowDialog() == true) TextColorValue = (TextColorValue.Length >= 3 ? TextColorValue[..3] : "#A0") + d.SelectedColor; }

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
                GlassBlurAmount = b; GlassTintOpacity = o;
                GlassTintLuminosity = l; GlassColorMode = m;
            });

        if (saved)
        {
            GlassBlurAmount = blur;
            GlassTintOpacity = opacity;
            GlassTintLuminosity = luminosity;
            GlassColorMode = colorMode;
        }
        PushToZone();
    }

    void LiquidGlass_Changed(object s, RoutedEventArgs e)
    {
        UpdateLiquidButton();
        UpdateGlassSection();
    }

    void UpdateLiquidButton()
    {
        if (LiquidGlassSettingsBtn == null) return;
        var accent = (Color)ColorConverter.ConvertFromString("#7C3AED");
        var muted = (Color)ColorConverter.ConvertFromString("#1E1E36");
        LiquidGlassSettingsBtn.Background = new SolidColorBrush(_liquidGlass ? accent : muted);
        LiquidGlassSettingsBtn.Foreground = System.Windows.Media.Brushes.White;
        LiquidGlassSettingsBtn.BorderBrush = new SolidColorBrush(_liquidGlass
            ? Color.FromArgb(0x80, 0x7C, 0x3A, 0xED)
            : (Color)ColorConverter.ConvertFromString("#404060"));
    }

    void UpdateGlassSection()
    {
        UpdateLiquidButton();
        if (GlassIntensityPanel != null) GlassIntensityPanel.Visibility = Visibility.Visible;
        if (LabelGlassIntensity != null) LabelGlassIntensity.Visibility = Visibility.Visible;
        if (GlassSectionTitle != null) GlassSectionTitle.Text = _loc.CurrentLanguage == Services.Language.Chinese ? "玻璃效果" : "Glass Effect";
        if (LabelLiquidGlassToggle != null) LabelLiquidGlassToggle.Text = _loc.CurrentLanguage == Services.Language.Chinese ? "液态玻璃" : "Liquid Glass";
    }

    void UpdateFillFromOpacity() { _fillColor = $"#{(int)(_fillOpacityPercent / 100 * 255):X2}{(_fillColor.Length > 3 ? _fillColor[3..] : "000000")}"; PushToZone(); }
    void UpdateTitleBarFromOpacity() { _titleBarFill = $"#{(int)(_titleBarOpacityPercent / 100 * 255):X2}{(_titleBarFill.Length > 3 ? _titleBarFill[3..] : "FFFFFF")}"; PushToZone(); }
    static double ParseOpacity(string a) { if (a.Length >= 3 && a[0] == '#') try { return int.Parse(a[1..3], System.Globalization.NumberStyles.HexNumber) / 255.0 * 100; } catch { } return 8; }

    /// <summary>
    /// Re-read zone properties into dialog local state so subsequent edits stay in sync after Apply.
    /// </summary>
    void SyncFromZone(Zone zone)
    {
        ZoneName = zone.Name; ZoneWidth = zone.Width.ToString("F0"); ZoneHeight = zone.Height.ToString("F0");
        GridSize = zone.GridSize.ToString(); SnapToGrid = zone.SnapToGrid;
        BorderThicknessText = zone.BorderThickness.ToString("F1");
        BorderColorValue = zone.BorderColor; FillColorValue = zone.FillColor;
        TitleBarFillValue = zone.TitleBarFillColor; BgImagePath = zone.BackgroundImagePath;
        IconCharText = zone.IconChar; CtrlOpacity = zone.ControlOpacity;
        BgImageOpacityPercent = zone.BackgroundImageOpacity; AutoArrange = zone.AutoArrange;
        _bgOffsetX = zone.BgImageOffsetX; _bgOffsetY = zone.BgImageOffsetY; _bgZoomVal = zone.BgImageZoom;
        AcrylicEnabled = zone.EnableAcrylic;
        _glassBlurAmount = zone.GlassBlurAmount;
        _glassTintOpacity = zone.GlassTintOpacity;
        _glassTintLuminosity = zone.GlassTintLuminosity;
        _glassColorMode = zone.GlassColorMode;
        _liquidGlass = zone.EnableLiquidGlass;
        _quickBarMode = zone.QuickBarMode;
        _enableRestoreButton = zone.EnableRestoreButton;
        IconColorValue = string.IsNullOrEmpty(zone.IconColor) ? "#FFFFFF" : zone.IconColor;
        TextColorValue = string.IsNullOrEmpty(zone.TitleTextColor) ? "#A0FFFFFF" : zone.TitleTextColor;
        UpdateGlassSection();
    }

    /// <summary>Push current dialog state to the live zone for real-time preview.</summary>
    void PushToZone()
    {
        if (_suppressPreview) return;

        var zone = ResultZone;
        double.TryParse(ZoneWidth, out var w); double.TryParse(ZoneHeight, out var h);
        double.TryParse(BorderThicknessText, out var bt);
        int.TryParse(GridSize, out var gs);

        zone.Name = ZoneName; zone.Width = w; zone.Height = h;
        zone.GridSize = gs; zone.SnapToGrid = SnapToGrid;
        zone.BorderThickness = bt; zone.BorderColor = BorderColorValue;
        zone.FillColor = FillColorValue; zone.TitleBarFillColor = TitleBarFillValue;
        zone.BackgroundImagePath = BgImagePath; zone.IconChar = IconCharText;
        zone.ControlOpacity = CtrlOpacity;
        zone.BackgroundImageOpacity = BgImageOpacityPercent;
        zone.AutoArrange = AutoArrange;
        zone.BgImageOffsetX = _bgOffsetX; zone.BgImageOffsetY = _bgOffsetY;
        zone.BgImageZoom = _bgZoomVal;
        zone.IconColor = IconColorValue; zone.TitleTextColor = TextColorValue;
        zone.EnableAcrylic = AcrylicEnabled;
        zone.GlassBlurAmount = GlassBlurAmount;
        zone.GlassTintOpacity = GlassTintOpacity;
        zone.GlassTintLuminosity = GlassTintLuminosity;
        zone.GlassColorMode = GlassColorMode;
        zone.EnableLiquidGlass = _liquidGlass;
        zone.QuickBarMode = _quickBarMode;
        zone.EnableRestoreButton = EnableRestoreButton;

        // Apply visual changes to the live zone window
        if (_zoneManager.GetZoneWindow(zone.Id) is { } win)
            win.RefreshZone(zone);
    }

    void ApplyButton_Click(object s, RoutedEventArgs e)
    {
        var err = _loc["Settings.ValidationError"];
        if (string.IsNullOrWhiteSpace(ZoneName)) { MessageBox.Show(_loc["Settings.NameEmpty"], err); return; }
        if (!double.TryParse(ZoneWidth, out var w) || w < 100 || w > 4000) { MessageBox.Show(_loc["Settings.WidthRange"], err); return; }
        if (!double.TryParse(ZoneHeight, out var h) || h < 100 || h > 4000) { MessageBox.Show(_loc["Settings.HeightRange"], err); return; }
        if (!int.TryParse(GridSize, out var gs) || gs < 32 || gs > 256) { MessageBox.Show(_loc["Settings.GridRange"], err); return; }
        if (!double.TryParse(BorderThicknessText, out var bt) || bt < 0.5 || bt > 10) { MessageBox.Show(_loc["Settings.BorderRange"], err); return; }

        // Push final state (already previewed, but ensure consistency)
        PushToZone();

        // Save UseGlobalAppearance to config
        var config = _zoneManager.GetConfig();
        config.UseGlobalAppearance = UseGlobalAppearance;
        _zoneManager.SaveConfig();

        DialogResult = true;
        Close();
    }

    void CancelButton_Click(object s, RoutedEventArgs e)
    {
        // Restore zone to snapshot state
        var zone = ResultZone;
        zone.Name = _snapshot.Name; zone.Width = _snapshot.Width; zone.Height = _snapshot.Height;
        zone.GridSize = _snapshot.GridSize; zone.SnapToGrid = _snapshot.SnapToGrid;
        zone.BorderThickness = _snapshot.BorderThickness; zone.BorderColor = _snapshot.BorderColor;
        zone.FillColor = _snapshot.FillColor; zone.TitleBarFillColor = _snapshot.TitleBarFillColor;
        zone.BackgroundImagePath = _snapshot.BackgroundImagePath; zone.IconChar = _snapshot.IconChar;
        zone.ControlOpacity = _snapshot.ControlOpacity;
        zone.BackgroundImageOpacity = _snapshot.BackgroundImageOpacity;
        zone.AutoArrange = _snapshot.AutoArrange;
        zone.BgImageOffsetX = _snapshot.BgImageOffsetX; zone.BgImageOffsetY = _snapshot.BgImageOffsetY;
        zone.BgImageZoom = _snapshot.BgImageZoom;
        zone.IconColor = _snapshot.IconColor; zone.TitleTextColor = _snapshot.TitleTextColor;
        zone.EnableAcrylic = _snapshot.EnableAcrylic;
        zone.GlassBlurAmount = _snapshot.GlassBlurAmount;
        zone.GlassTintOpacity = _snapshot.GlassTintOpacity;
        zone.GlassTintLuminosity = _snapshot.GlassTintLuminosity;
        zone.GlassColorMode = _snapshot.GlassColorMode;
        zone.EnableLiquidGlass = _snapshot.EnableLiquidGlass;
        zone.QuickBarMode = _snapshot.QuickBarMode;
        zone.EnableRestoreButton = _snapshot.EnableRestoreButton;

        // Refresh window with restored state
        if (_zoneManager.GetZoneWindow(zone.Id) is { } win)
            win.RefreshZone(zone);

        DialogResult = false;
        Close();
    }

    void UseGlobal_Changed(object s, RoutedEventArgs e)
    {
        // Defer save to Apply — don't modify config on toggle
    }

    void IconPreset_Click(object s, RoutedEventArgs e) { if (s is Button b && b.Tag is string ic) IconCharText = ic; }
    void BrowseBgImage_Click(object s, RoutedEventArgs e) { var d = new Microsoft.Win32.OpenFileDialog { Title = _loc["Settings.BrowseBg"], Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp;*.gif|All|*.*" }; if (d.ShowDialog() == true) BgImagePath = d.FileName; }
    void ClearBgImage_Click(object s, RoutedEventArgs e) => BgImagePath = "";
    void CropBgImage_Click(object s, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(BgImagePath) || !File.Exists(BgImagePath))
            return;

        double.TryParse(ZoneWidth, out double targetWidth);
        double.TryParse(ZoneHeight, out double targetHeight);

        // Zone uses rectangular crop
        string cropShape = "Rectangle";

        var cropWindow = new ImageCropPreviewWindow(
            imagePath: BgImagePath,
            targetWidth: targetWidth,
            targetHeight: targetHeight,
            initialOffsetX: _bgOffsetX,
            initialOffsetY: _bgOffsetY,
            initialZoom: _bgZoomVal,
            initialOpacity: BgImageOpacityPercent,
            cropShape: cropShape)
        {
            Owner = this
        };

        if (cropWindow.ShowDialog() == true && cropWindow.Result != null)
        {
            _bgOffsetX = cropWindow.Result.OffsetX;
            _bgOffsetY = cropWindow.Result.OffsetY;
            _bgZoomVal = cropWindow.Result.Zoom;
            BgImageOpacityPercent = cropWindow.Result.Opacity;

            // Update UI controls
            OffsetXBox.Text = _bgOffsetX.ToString("F0");
            OffsetYBox.Text = _bgOffsetY.ToString("F0");
        }
    }
    void MoreEmoji_Click(object s, RoutedEventArgs e) { var d = new EmojiPickerDialog { Owner = this }; if (d.ShowDialog() == true && !string.IsNullOrEmpty(d.SelectedEmoji)) IconCharText = d.SelectedEmoji; }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    { base.OnMouseLeftButtonDown(e); var s = e.OriginalSource; if (s is Border && s is not Button && s.GetType().Name != "TextBoxView" && s.GetType().Name != "ScrollViewer") { try { DragMove(); } catch { } } }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
