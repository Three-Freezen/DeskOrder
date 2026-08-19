using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DesktopZones.Helpers;
using DesktopZones.Models;
using DesktopZones.Services;

namespace DesktopZones.Views;

public partial class MergedGroupSettingsDialog : Window, INotifyPropertyChanged
{
    private readonly Zone _zone;
    private Zone _snapshot; // for cancel-revert — reassigned after LoadPreset Apply to preserve the preset across an outer Cancel
    private readonly ZoneManager _zoneManager;
    private readonly LocalizationService _loc = LocalizationService.Instance;

    // Glass settings
    private int _glassBlurAmount;
    private int _glassTintOpacity;
    private int _glassTintLuminosity;
    private string _glassColorMode;
    private bool _liquidGlass;

    // Suppress PushToZone while we bulk-write controls from a preset (mirrors
    // ZoneSettingsDialog._suppressPreview — without it the TextChanged/Slider.ValueChanged
    // handlers fire and overwrite the values we just laid down).
    private bool _suppressPush;

    public MergedGroupSettingsDialog(Zone zone, ZoneManager zoneManager)
    {
        InitializeComponent();
        _zone = zone;
        _snapshot = zone.Clone(); // snapshot for cancel-revert
        _zoneManager = zoneManager;
        DataContext = this;

        // Initialize glass settings from zone
        _glassBlurAmount = zone.GlassBlurAmount;
        _glassTintOpacity = zone.GlassTintOpacity;
        _glassTintLuminosity = zone.GlassTintLuminosity;
        _glassColorMode = zone.GlassColorMode;
        _liquidGlass = zone.EnableLiquidGlass;

        ApplyLoc();

        // Populate controls from zone
        NameBox.Text = zone.MergedGroupName;
        IconCharBox.Text = zone.MergedGroupIcon;
        QuickBarModeToggle.IsChecked = zone.MergedGroupQuickBarMode;
        QuickBarModeToggle.Checked += (_, _) => PushToZone();
        QuickBarModeToggle.Unchecked += (_, _) => PushToZone();
        WidthBox.Text = zone.Width.ToString("F0");
        HeightBox.Text = zone.Height.ToString("F0");
        BorderThicknessBox.Text = zone.MergedGroupBorderThickness.ToString("F1");
        BgImagePathBox.Text = zone.MergedGroupBackgroundImagePath;
        OffsetXBox.Text = zone.MergedGroupBgImageOffsetX.ToString("F0");
        OffsetYBox.Text = zone.MergedGroupBgImageOffsetY.ToString("F0");

        // Wire up TextChanged for live preview
        NameBox.TextChanged += (_, _) => PushToZone();
        IconCharBox.TextChanged += (_, _) => PushToZone();
        WidthBox.TextChanged += (_, _) => PushToZone();
        HeightBox.TextChanged += (_, _) => PushToZone();
        BorderThicknessBox.TextChanged += (_, _) => PushToZone();
        OffsetXBox.TextChanged += (_, _) => PushToZone();
        OffsetYBox.TextChanged += (_, _) => PushToZone();

        // Opacity sliders
        TitleOpacitySlider.Value = zone.MergedGroupTitleBarOpacity;
        TitleOpacityValue.Text = $"{(int)zone.MergedGroupTitleBarOpacity}%";
        TitleOpacitySlider.ValueChanged += (_, _) => { TitleOpacityValue.Text = $"{(int)TitleOpacitySlider.Value}%"; PushToZone(); };

        CtrlOpacitySlider.Value = zone.MergedGroupControlOpacity;
        CtrlOpacityValue.Text = $"{(int)zone.MergedGroupControlOpacity}%";
        CtrlOpacitySlider.ValueChanged += (_, _) => { CtrlOpacityValue.Text = $"{(int)CtrlOpacitySlider.Value}%"; PushToZone(); };

        FillOpacitySlider.Value = zone.MergedGroupBackgroundImageOpacity;
        FillOpacityValue.Text = $"{(int)zone.MergedGroupBackgroundImageOpacity}%";
        FillOpacitySlider.ValueChanged += (_, _) => { FillOpacityValue.Text = $"{(int)FillOpacitySlider.Value}%"; PushToZone(); };

        BgZoomSlider.Value = zone.MergedGroupBgImageZoom;
        BgZoomValue.Text = $"{zone.MergedGroupBgImageZoom:F1}x";
        BgZoomSlider.ValueChanged += (_, _) => { BgZoomValue.Text = $"{BgZoomSlider.Value:F1}x"; PushToZone(); };

        // Bg stretch: always UniformToFill
        // Fill mode
        UnifiedFillRadio.IsChecked = zone.MergedGroupUseUnifiedFill;
        KeepOriginalRadio.IsChecked = !zone.MergedGroupUseUnifiedFill;
        UnifiedFillRadio.Checked += (_, _) => PushToZone();
        KeepOriginalRadio.Checked += (_, _) => PushToZone();

        // Text color adaptive (top-row checkbox)
        TextAdaptiveBox.IsChecked = zone.TextColorAdaptive;
        TitleBarTextAdaptiveBox.IsChecked = zone.MergedGroupTitleBarTextColorAdaptive;

        // Highlight selected colors
        UpdateHighlights();
        UpdateCropBtnState();

        // Wire up events
        CancelButton.Click += CancelButton_Click;
        ApplyButton.Click += ApplyButton_Click;
        BrowseBgBtn.Click += BrowseBgImage_Click;
        ClearBgBtn.Click += (_, _) => { BgImagePathBox.Text = ""; UpdateCropBtnState(); };
        // Liquid Glass toggle + settings
        LiquidGlassToggle.IsChecked = zone.EnableLiquidGlass;
        LiquidGlassToggle.Checked += (_, _) => { _liquidGlass = true; UpdateLiquidButton(); PushToZone(); };
        LiquidGlassToggle.Unchecked += (_, _) => { _liquidGlass = false; UpdateLiquidButton(); PushToZone(); };
        LiquidGlassSettingsBtn.Click += LiquidGlassSettings_Click;
        UpdateLiquidButton();

        // Wire adaptive checkboxes — TextAdaptive + TitleBarTextAdaptive both call PushToZone
        // which now writes the boolean back to _zone; live preview reaches the window via
        // the zone manager's RefreshAppearance-style flow (PushToZone → OnZonesChanged → ApplyStyle).
        TextAdaptiveBox.Checked += (_, _) => PushToZone();
        TextAdaptiveBox.Unchecked += (_, _) => PushToZone();
        TitleBarTextAdaptiveBox.Checked += (_, _) => PushToZone();
        TitleBarTextAdaptiveBox.Unchecked += (_, _) => PushToZone();

        // Crop button
        CropBtn.Click += CropBgImage_Click;

        // Color preset clicks
        TextColorPresets.MouseLeftButtonDown += (s, e) => HandleColorPresetClick(TextColorPresets, e);
        IconColorPresets.MouseLeftButtonDown += (s, e) => HandleColorPresetClick(IconColorPresets, e);
        BorderColorPresets.MouseLeftButtonDown += (s, e) => HandleColorPresetClick(BorderColorPresets, e);
        TitleBarPresets.MouseLeftButtonDown += (s, e) => HandleColorPresetClick(TitleBarPresets, e);
        FillColorPresets.MouseLeftButtonDown += (s, e) => HandleColorPresetClick(FillColorPresets, e);

        // Custom color buttons
        TextColorCustomBtn.Click += (_, _) => OpenCustomColor(TextColorPresets, "#A0FFFFFF");
        IconColorCustomBtn.Click += (_, _) => OpenCustomColor(IconColorPresets, "#FFFFFF");
        BorderCustomBtn.Click += (_, _) => OpenCustomColor(BorderColorPresets, "#60FFFFFF");
        TitleCustomBtn.Click += (_, _) => OpenCustomColor(TitleBarPresets, "#60FFFFFF");
        FillCustomBtn.Click += (_, _) => OpenCustomColor(FillColorPresets, "#60FFFFFF");

        // Icon preset buttons
        foreach (var child in FindIconPresets())
        {
            if (child is Button btn && btn.Tag is string ic)
                btn.Click += (_, _) => IconCharBox.Text = ic;
        }
    }

    void UpdateCropBtnState()
    {
        if (CropBtn != null)
            CropBtn.IsEnabled = !string.IsNullOrEmpty(BgImagePathBox.Text) && File.Exists(BgImagePathBox.Text);
    }

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
                PushToZone();
            });

        if (saved)
        {
            _glassBlurAmount = blur;
            _glassTintOpacity = opacity;
            _glassTintLuminosity = luminosity;
            _glassColorMode = colorMode;
        }
        PushToZone();
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

    /// <summary>Push current dialog state to the live zone for real-time preview.</summary>
    void PushToZone()
    {
        if (_suppressPush) return;
        _zone.MergedGroupName = NameBox.Text;
        _zone.MergedGroupIcon = IconCharBox.Text;
        _zone.MergedGroupQuickBarMode = QuickBarModeToggle.IsChecked == true;

        double.TryParse(WidthBox.Text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var w);
        double.TryParse(HeightBox.Text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var h);
        if (w >= 100 && w <= 4000) _zone.Width = w;
        if (h >= 100 && h <= 4000) _zone.Height = h;

        _zone.MergedGroupTitleTextColor = GetSelectedTextColor();
        _zone.MergedGroupIconColor = GetSelectedIconColor();

        if (double.TryParse(BorderThicknessBox.Text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var bt))
            _zone.MergedGroupBorderThickness = bt;
        _zone.MergedGroupBorderColor = GetSelectedBorderColor();
        _zone.MergedGroupTitleBarFillColor = GetSelectedTitleBarColor();
        _zone.MergedGroupTitleBarOpacity = TitleOpacitySlider.Value;
        _zone.MergedGroupControlOpacity = CtrlOpacitySlider.Value;
        _zone.MergedGroupFillColor = GetSelectedFillColor();
        _zone.MergedGroupUseUnifiedFill = UnifiedFillRadio.IsChecked == true;
        _zone.TextColorAdaptive = TextAdaptiveBox.IsChecked == true;
        _zone.MergedGroupTitleBarTextColorAdaptive = TitleBarTextAdaptiveBox.IsChecked == true;

        _zone.GlassBlurAmount = _glassBlurAmount;
        _zone.GlassTintOpacity = _glassTintOpacity;
        _zone.GlassTintLuminosity = _glassTintLuminosity;
        _zone.GlassColorMode = _glassColorMode;
        _zone.EnableLiquidGlass = _liquidGlass;

        _zone.MergedGroupBackgroundImagePath = BgImagePathBox.Text;
        double.TryParse(OffsetXBox.Text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var ox);
        double.TryParse(OffsetYBox.Text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var oy);
        _zone.MergedGroupBgImageOffsetX = ox;
        _zone.MergedGroupBgImageOffsetY = oy;
        _zone.MergedGroupBgImageZoom = BgZoomSlider.Value;
        _zone.MergedGroupBackgroundImageOpacity = FillOpacitySlider.Value;

        // Apply visual changes to the live zone window
        if (_zoneManager.GetZoneWindow(_zone.Id) is { } win)
            win.RefreshZone(_zone);
    }

    /// <summary>Bulk-write all dialog controls from <see cref="_zone"/>. Mirrors
    /// ZoneSettingsDialog.SyncFromZone — used by the Load Preset OK / Cancel branches to
    /// keep dialog controls in sync with the model after a preset has been applied or
    /// reverted. Suppresses <see cref="PushToZone"/> so the TextChanged/ValueChanged
    /// handlers don't immediately overwrite the values we just wrote.</summary>
    void SyncFromZone()
    {
        var prev = _suppressPush;
        _suppressPush = true;
        try
        {
            NameBox.Text = _zone.MergedGroupName;
            IconCharBox.Text = _zone.MergedGroupIcon;
            QuickBarModeToggle.IsChecked = _zone.MergedGroupQuickBarMode;
            WidthBox.Text = _zone.Width.ToString("F0");
            HeightBox.Text = _zone.Height.ToString("F0");
            BorderThicknessBox.Text = _zone.MergedGroupBorderThickness.ToString("F1");
            BgImagePathBox.Text = _zone.MergedGroupBackgroundImagePath;
            OffsetXBox.Text = _zone.MergedGroupBgImageOffsetX.ToString("F0");
            OffsetYBox.Text = _zone.MergedGroupBgImageOffsetY.ToString("F0");

            // Sliders — also update their value labels (setters fired on ValueChanged handlers).
            TitleOpacitySlider.Value = _zone.MergedGroupTitleBarOpacity;
            TitleOpacityValue.Text = $"{(int)_zone.MergedGroupTitleBarOpacity}%";
            CtrlOpacitySlider.Value = _zone.MergedGroupControlOpacity;
            CtrlOpacityValue.Text = $"{(int)_zone.MergedGroupControlOpacity}%";
            FillOpacitySlider.Value = _zone.MergedGroupBackgroundImageOpacity;
            FillOpacityValue.Text = $"{(int)_zone.MergedGroupBackgroundImageOpacity}%";
            BgZoomSlider.Value = _zone.MergedGroupBgImageZoom;
            BgZoomValue.Text = $"{_zone.MergedGroupBgImageZoom:F1}x";

            UnifiedFillRadio.IsChecked = _zone.MergedGroupUseUnifiedFill;
            KeepOriginalRadio.IsChecked = !_zone.MergedGroupUseUnifiedFill;

            // Color preset panels — highlight the chip matching each color string.
            SelectPresetByTag(TextColorPresets, _zone.MergedGroupTitleTextColor);
            SelectPresetByTag(IconColorPresets, _zone.MergedGroupIconColor);
            SelectPresetByTag(BorderColorPresets, _zone.MergedGroupBorderColor);
            SelectPresetByTag(TitleBarPresets, _zone.MergedGroupTitleBarFillColor);
            SelectPresetByTag(FillColorPresets, _zone.MergedGroupFillColor);
            UpdateHighlights();

            // Liquid glass
            _glassBlurAmount = _zone.GlassBlurAmount;
            _glassTintOpacity = _zone.GlassTintOpacity;
            _glassTintLuminosity = _zone.GlassTintLuminosity;
            _glassColorMode = _zone.GlassColorMode;
            _liquidGlass = _zone.EnableLiquidGlass;
            LiquidGlassToggle.IsChecked = _liquidGlass;
            UpdateLiquidButton();

            UpdateCropBtnState();
        }
        finally { _suppressPush = prev; }
    }

    /// <summary>Highlight the preset chip in <paramref name="panel"/> whose Tag matches
    /// <paramref name="value"/> (case-insensitive). Clears the highlight from all others.</summary>
    static void SelectPresetByTag(Panel panel, string value)
    {
        foreach (var child in panel.Children)
        {
            if (child is Border b && b.Tag is string t)
            {
                bool match = string.Equals(t, value, StringComparison.OrdinalIgnoreCase);
                b.BorderThickness = new Thickness(match ? 3 : 1);
            }
        }
    }

    void CropBgImage_Click(object s, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(BgImagePathBox.Text) || !File.Exists(BgImagePathBox.Text))
            return;

        double.TryParse(OffsetXBox.Text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double offsetX);
        double.TryParse(OffsetYBox.Text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double offsetY);
        double zoom = BgZoomSlider.Value;

        var cropWindow = new ImageCropPreviewWindow(
            imagePath: BgImagePathBox.Text,
            targetWidth: 400,
            targetHeight: 300,
            initialOffsetX: offsetX,
            initialOffsetY: offsetY,
            initialZoom: zoom,
            initialOpacity: FillOpacitySlider.Value,
            cropShape: "Rectangle")
        {
            Owner = this
        };

        if (cropWindow.ShowDialog() == true && cropWindow.Result != null)
        {
            offsetX = cropWindow.Result.OffsetX;
            offsetY = cropWindow.Result.OffsetY;
            zoom = cropWindow.Result.Zoom;
            FillOpacitySlider.Value = cropWindow.Result.Opacity;

            OffsetXBox.Text = offsetX.ToString("F0");
            OffsetYBox.Text = offsetY.ToString("F0");
            BgZoomSlider.Value = zoom;
        }
    }

    System.Collections.Generic.IEnumerable<System.Windows.DependencyObject> FindIconPresets()
    {
        // Search in the XAML visual tree for icon preset buttons
        // They are defined directly in the XAML, so we find them by searching the tree
        var stack = FindName("IconCharBox") as TextBox;
        if (stack?.Parent is StackPanel sp1)
        {
            foreach (var child in sp1.Children)
                yield return child as System.Windows.DependencyObject;
        }
    }

    void ApplyLoc()
    {
        var cn = _loc.CurrentLanguage == Services.Language.Chinese;
        DialogTitle.Text = cn ? "组合分区设置" : "Merged Group Settings";
        LabelName.Text = cn ? "组合名称" : "Group Name";
        LabelQuickBarMode.Text = cn ? "极简模式" : "Minimal Mode";
        LabelWidth.Text = _loc["Settings.Width"];
        LabelHeight.Text = _loc["Settings.Height"];
        LabelTextColor.Text = cn ? "分区名称颜色" : "Zone Name Color";
        LabelIcon.Text = cn ? "组合图标" : "Group Icon";
        LabelIconColor.Text = cn ? "分区图标颜色" : "Zone Icon Color";
        LabelBorderThickness.Text = cn ? "边框粗细" : "Border Thickness";
        LabelBorderColor.Text = cn ? "边框颜色" : "Border Color";
        LabelTitleBar.Text = cn ? "标题栏填充" : "Title Bar Fill";
        LabelTitleOpacity.Text = cn ? "标题栏透明度" : "Title Bar Opacity";
        LabelCtrlOpacity.Text = cn ? "按钮透明度" : "Button Opacity";
        LabelFillColor.Text = cn ? "填充颜色" : "Fill Color";
        LabelOpacity.Text = cn ? "图片透明度" : "Image Opacity";
        LabelFillMode.Text = cn ? "填充模式" : "Fill Mode";
        UnifiedFillRadio.Content = cn ? "统一填充" : "Unified Fill";
        KeepOriginalRadio.Content = cn ? "保留原有填充" : "Keep Original Fill";
        GlassSectionTitle.Text = cn ? "玻璃效果" : "Glass Effect";
        LabelGlassIntensity.Text = cn ? "液态玻璃" : "Liquid Glass";
        LiquidGlassToggle.Content = cn ? "液态玻璃" : "Liquid Glass";
        LiquidGlassSettingsBtn.Content = cn ? "💧 液态玻璃设置" : "💧 Liquid Glass Settings";
        LabelBgImage.Text = cn ? "背景图片" : "Background Image";
        LabelBgStretch.Text = cn ? "图片裁剪" : "Crop";
        LabelOffsetX.Text = cn ? "水平偏移" : "Offset X";
        LabelOffsetY.Text = cn ? "垂直偏移" : "Offset Y";
        LabelZoom.Text = cn ? "缩放" : "Zoom";
        LabelBgOpacity.Text = cn ? "图片透明度" : "Image Opacity";
        ClearBgBtn.Content = cn ? "清除" : "Clear";
        CancelButton.Content = cn ? "取消" : "Cancel";
        ApplyButton.Content = cn ? "应用" : "Apply";
        CropFill.Content = cn ? "拉伸填充" : "Fill";
        CropUniform.Content = cn ? "等比缩放" : "Uniform";
        CropUniformToFill.Content = cn ? "等比填充" : "UniformToFill";
        CropNone.Content = cn ? "原始尺寸" : "None";
        TextColorCustomBtn.Content = "...";
        IconColorCustomBtn.Content = "...";
        BorderCustomBtn.Content = "...";
        TitleCustomBtn.Content = "...";
        FillCustomBtn.Content = "...";
    }

    void UpdateHighlights()
    {
        HP(TextColorPresets, GetSelectedTextColor());
        HP(IconColorPresets, GetSelectedIconColor());
        HP(BorderColorPresets, GetSelectedBorderColor());
        HP(TitleBarPresets, GetSelectedTitleBarColor());
        HP(FillColorPresets, GetSelectedFillColor());
    }

    static void HP(Panel p, string s)
    {
        foreach (var c in p.Children)
        {
            if (c is Border b && b.Tag is string t)
                b.BorderThickness = new Thickness(t == s ? 3 : 1);
        }
    }

    string GetSelectedTextColor() => FindSelectedTag(TextColorPresets) ?? "#A0FFFFFF";
    string GetSelectedIconColor() => FindSelectedTag(IconColorPresets) ?? "#FFFFFF";
    string GetSelectedBorderColor() => FindSelectedTag(BorderColorPresets) ?? "#60FFFFFF";
    string GetSelectedTitleBarColor() => FindSelectedTag(TitleBarPresets) ?? "#60FFFFFF";
    string GetSelectedFillColor() => FindSelectedTag(FillColorPresets) ?? "#60FFFFFF";

    static string? FindSelectedTag(Panel p)
    {
        foreach (var c in p.Children)
        {
            if (c is Border b && b.Tag is string && b.BorderThickness.Left == 3)
                return b.Tag as string;
        }
        return null;
    }

    void HandleColorPresetClick(Panel panel, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is Border b && b.Tag is string)
        {
            foreach (var c in panel.Children)
            {
                if (c is Border pb && pb.Tag is string)
                    pb.BorderThickness = new Thickness(1);
            }
            b.BorderThickness = new Thickness(3);
            PushToZone();
        }
    }

    void OpenCustomColor(Panel panel, string defaultColor)
    {
        var current = FindSelectedTag(panel) ?? defaultColor;
        var hex = current.Length >= 7 ? current[1..] : current;
        var d = new ColorPickerDialog(hex) { Owner = this };
        if (d.ShowDialog() == true)
        {
            var newColor = "#" + d.SelectedColor;
            var border = new Border
            {
                Width = 28, Height = 28, CornerRadius = new CornerRadius(14),
                Margin = new Thickness(0, 0, 6, 0), Cursor = Cursors.Hand,
                Tag = newColor, BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(3)
            };
            try
            {
                border.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(newColor));
            }
            catch { }
            panel.Children.Add(border);
            // ponytail: Fix E — without this call the new chip sits in the panel but the
            // underlying _zone color (e.g., MergedGroupFillColor) is never updated, so the
            // live zone shows no live preview until the user clicks Apply.
            PushToZone();
        }
    }

    void BrowseBgImage_Click(object sender, RoutedEventArgs e)
    {
        var d = new Microsoft.Win32.OpenFileDialog
        {
            Title = _loc["Settings.BrowseBg"],
            Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp;*.gif|All|*.*"
        };
        if (d.ShowDialog() == true)
        {
            BgImagePathBox.Text = d.FileName;
            UpdateCropBtnState();
        }
    }

    void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        // Push final state (already previewed, but ensure consistency)
        PushToZone();

        // Save config
        _zoneManager.SaveConfig();

        DialogResult = true;
    }

    void LoadPreset_Click(object sender, RoutedEventArgs e)
    {
        var snap = _zone.Clone();
        // Mirrors ZoneSettingsDialog.LoadPresetButton_Click:
        //   onCardPicked   — real-time preview: writes preset → _zone + live window
        //   onPicked(OK)   — final commit: writes preset → _zone + dialog UI controls
        //   Cancel         — restores _zone + UI from snapshot, refreshes live window
        var applied = PresetButtonsHelper.OpenLoad(this, PresetKind.MergedGroup, _zone,
            picked => SyncFromZone(),                    // OK: sync UI from _zone
            record =>
            {
                var picked = (Zone)PresetService.GetPayload(record);
                CopyMergedGroupFields(picked, _zone);
                // Skip PushToZone here: it reads from the dialog controls (which still
                // hold the pre-preset values) and would overwrite the preset copy
                // we just laid down. ZoneSettingsDialog's onCardPicked has the same
                // shape for the same reason.
                _zoneManager.GetZoneWindow(_zone.Id)?.RefreshZone(_zone);
            });
        if (applied == true)
        {
            // OK — promote _snapshot to current (post-preset) state so a later outer
            // Cancel reverts to "post-preset" baseline, preserving the preset across
            // outer Cancel. Mirrors ZoneSettingsDialog._snapshot = ResultZone.Clone()
            // (line 238) — the preset commit and the outer Cancel are independent.
            _snapshot = _zone.Clone();
        }
        else
        {
            // Cancel — revert _zone from snapshot, sync UI, refresh live window.
            // Skip PushToZone: it reads UI (still pre-preset) and would overwrite
            // the snapshot values we just laid down. SyncFromZone writes snap → UI
            // and uses _suppressPush so its setters don't loop back through PushToZone.
            CopyMergedGroupFields(snap, _zone);
            SyncFromZone();
        }

        // Defensive chain (ZoneSettingsDialog:253-256) — Refresh + UpdateLayout + Refresh
        // guarantees the live window paints the final state after all in-flight
        // setter→PushToZone→RefreshZone cycles have settled, regardless of which
        // branch (OK/Cancel) ran above.
        var win = _zoneManager.GetZoneWindow(_zone.Id);
        win?.RefreshZone(_zone);
        win?.UpdateLayout();
        win?.RefreshZone(_zone);
    }

    void SavePreset_Click(object sender, RoutedEventArgs e)
    {
        PushToZone();
        PresetButtonsHelper.OpenSave(this, PresetKind.MergedGroup, _zone.Clone());
    }

    private static void CopyMergedGroupFields(Zone src, Zone dst)
    {
        dst.MergedGroupName = src.MergedGroupName;
        dst.MergedGroupIcon = src.MergedGroupIcon;
        dst.MergedGroupBorderColor = src.MergedGroupBorderColor;
        dst.MergedGroupBorderThickness = src.MergedGroupBorderThickness;
        dst.MergedGroupCornerRadius = src.MergedGroupCornerRadius;
        dst.MergedGroupFillColor = src.MergedGroupFillColor;
        dst.MergedGroupTitleBarFillColor = src.MergedGroupTitleBarFillColor;
        dst.MergedGroupTitleTextColor = src.MergedGroupTitleTextColor;
        dst.MergedGroupIconColor = src.MergedGroupIconColor;
        dst.MergedGroupControlOpacity = src.MergedGroupControlOpacity;
        dst.MergedGroupTitleBarOpacity = src.MergedGroupTitleBarOpacity;
        dst.MergedGroupUseUnifiedFill = src.MergedGroupUseUnifiedFill;
        dst.MergedGroupQuickBarMode = src.MergedGroupQuickBarMode;
        dst.MergedGroupBackgroundImagePath = src.MergedGroupBackgroundImagePath;
        dst.MergedGroupBgImageStretch = src.MergedGroupBgImageStretch;
        dst.MergedGroupBgImageOffsetX = src.MergedGroupBgImageOffsetX;
        dst.MergedGroupBgImageOffsetY = src.MergedGroupBgImageOffsetY;
        dst.MergedGroupBgImageZoom = src.MergedGroupBgImageZoom;
        dst.MergedGroupBackgroundImageOpacity = src.MergedGroupBackgroundImageOpacity;
    }

    void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        // Restore zone to snapshot state
        _zone.MergedGroupName = _snapshot.MergedGroupName;
        _zone.MergedGroupIcon = _snapshot.MergedGroupIcon;
        _zone.MergedGroupQuickBarMode = _snapshot.MergedGroupQuickBarMode;
        _zone.Width = _snapshot.Width;
        _zone.Height = _snapshot.Height;
        _zone.MergedGroupTitleTextColor = _snapshot.MergedGroupTitleTextColor;
        _zone.MergedGroupIconColor = _snapshot.MergedGroupIconColor;
        _zone.MergedGroupBorderThickness = _snapshot.MergedGroupBorderThickness;
        _zone.MergedGroupBorderColor = _snapshot.MergedGroupBorderColor;
        _zone.MergedGroupTitleBarFillColor = _snapshot.MergedGroupTitleBarFillColor;
        _zone.MergedGroupTitleBarOpacity = _snapshot.MergedGroupTitleBarOpacity;
        _zone.MergedGroupControlOpacity = _snapshot.MergedGroupControlOpacity;
        _zone.MergedGroupFillColor = _snapshot.MergedGroupFillColor;
        _zone.MergedGroupUseUnifiedFill = _snapshot.MergedGroupUseUnifiedFill;
        _zone.TextColorAdaptive = _snapshot.TextColorAdaptive;
        _zone.MergedGroupTitleBarTextColorAdaptive = _snapshot.MergedGroupTitleBarTextColorAdaptive;
        _zone.GlassBlurAmount = _snapshot.GlassBlurAmount;
        _zone.GlassTintOpacity = _snapshot.GlassTintOpacity;
        _zone.GlassTintLuminosity = _snapshot.GlassTintLuminosity;
        _zone.GlassColorMode = _snapshot.GlassColorMode;
        _zone.EnableLiquidGlass = _snapshot.EnableLiquidGlass;
        _zone.MergedGroupBackgroundImagePath = _snapshot.MergedGroupBackgroundImagePath;
        _zone.MergedGroupBgImageOffsetX = _snapshot.MergedGroupBgImageOffsetX;
        _zone.MergedGroupBgImageOffsetY = _snapshot.MergedGroupBgImageOffsetY;
        _zone.MergedGroupBgImageZoom = _snapshot.MergedGroupBgImageZoom;
        _zone.MergedGroupBackgroundImageOpacity = _snapshot.MergedGroupBackgroundImageOpacity;

        // Refresh window with restored state
        if (_zoneManager.GetZoneWindow(_zone.Id) is { } win)
            win.RefreshZone(_zone);

        Close();
    }

    // Event stubs for XAML
    private void TextColorPreset_Click(object s, MouseButtonEventArgs e) => HandleColorPresetClick(TextColorPresets, e);
    private void IconColorPreset_Click(object s, MouseButtonEventArgs e) => HandleColorPresetClick(IconColorPresets, e);
    private void BorderColorPreset_Click(object s, MouseButtonEventArgs e) => HandleColorPresetClick(BorderColorPresets, e);
    private void TitleBarColorPreset_Click(object s, MouseButtonEventArgs e) => HandleColorPresetClick(TitleBarPresets, e);
    private void FillColorPreset_Click(object s, MouseButtonEventArgs e) => HandleColorPresetClick(FillColorPresets, e);
    private void TextColorCustom_Click(object s, RoutedEventArgs e) => OpenCustomColor(TextColorPresets, "#A0FFFFFF");
    private void IconColorCustom_Click(object s, RoutedEventArgs e) => OpenCustomColor(IconColorPresets, "#FFFFFF");
    private void BorderCustom_Click(object s, RoutedEventArgs e) => OpenCustomColor(BorderColorPresets, "#60FFFFFF");
    private void TitleCustom_Click(object s, RoutedEventArgs e) => OpenCustomColor(TitleBarPresets, "#60FFFFFF");
    private void FillCustom_Click(object s, RoutedEventArgs e) => OpenCustomColor(FillColorPresets, "#60FFFFFF");
    private void IconPreset_Click(object s, RoutedEventArgs e)
    {
        if (s is Button b && b.Tag is string ic)
            IconCharBox.Text = ic;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
