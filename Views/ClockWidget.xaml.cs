using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopZones.Helpers;
using DesktopZones.Models;
using DesktopZones.Services;
using DesktopZones.ViewModels;
using DesktopZones.Views.Components;

namespace DesktopZones.Views;

public partial class ClockWidget : Window
{
    private DesktopClock _clock;
    private readonly WidgetService _widgetService;
    private readonly ClockViewModel _vm;
    private readonly DispatcherTimer _timer;
    private readonly LocalizationService _loc = LocalizationService.Instance;
    private HoverExpandBehavior? _hover;
    private SnapDrag? _snapDrag;
    private SnapResize? _snapResize;

    private bool _restoreDragging;
    private Point _restoreDown;

    // ponytail: frozen hover brushes — same color on every mouse-over.
    private static readonly SolidColorBrush RestoreHoverBrush = Freeze(new(Color.FromArgb(0xFF, 0x2A, 0x2A, 0x4E)));
    private static readonly SolidColorBrush RestoreIdleBrush  = Freeze(new(Color.FromArgb(0xDD, 0x1A, 0x1A, 0x2E)));
    static SolidColorBrush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    public ClockWidget(DesktopClock clock, WidgetService widgetService)
    {
        InitializeComponent();
        _clock = clock;
        _widgetService = widgetService;
        _vm = new ClockViewModel(clock);
        DataContext = _vm;

        Left = clock.X; Top = clock.Y;
        Opacity = clock.Opacity;
        MinWidth = 140; MinHeight = 80;
        Width = 320; Height = 140;

        ApplyMode();
        ApplyStyle();
        // Seed the RefreshAppearance gate with the clock's actual mode so the very
        // first RefreshAppearance (e.g. style dialog Cancel / Apply round-trip)
        // doesn't see "Mode differs from default Digital" and re-run ApplyMode —
        // which would reset Width/Height to the mode's hardcoded defaults and
        // clobber any user-resized dimensions on Analog clocks.
        _lastAppliedMode = _clock.Mode;
        UpdateContextMenuLabels();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += Tick;
        _timer.Start();
        Tick(null, EventArgs.Empty);

        Loaded += OnLoad;
        SizeChanged += OnSizeChanged;
        LocationChanged += (_, _) => { _clock.X = Left; _clock.Y = Top; };
        _langChanged = _ => UpdateContextMenuLabels();
        _loc.LanguageChanged += _langChanged;
        _widgetService.ClocksChanged += OnClocksChanged;
        // ponytail: subscribe to LockChanged so management UI (or any other source) flipping
        // this widget's lock state immediately syncs the open window — without this the
        // open clock stays 🔓 while the model (and management card) shows 🔒.
        _widgetService.LockChanged += OnServiceLockChanged;
        // ponytail: hover-expand (Task 14d). Wired after InitializeComponent and
        // before any user interaction can occur.
        _hover = new HoverExpandBehavior(this, RestoreButton, MainContent, null,
            () => _clock.HoverExpandAnimation,
            () => _clock.HoverExpandSpeed,
            () => _clock.HoverExpandOrigin,
            () => _clock.HoverAutoExpand)
        { IsEnabled = _clock.EnableRestoreButton };
        // ponytail 2026-08-25: pick up live changes from the 动效设置 dialog
        // (property panel) — mirrors ZoneWindow's subscription.
        _clock.HoverExpandSettingsChanged += OnHoverExpandSettingsChanged;
        // ponytail: ghost-glass fix — see ZoneWindow. Acrylic follows the expand state so a
        // collapsed clock shows ONLY the RestoreButton (no full-window glass rectangle).
        _hover.Expanded += ApplyAcrylic;
        _hover.Collapsed += () => AcrylicHelper.DisableBlur(this);
        // ponytail: bug fix — see ZoneWindow ctor for full rationale. Window.Show()
        // (called by OpenClockWindow / --spawn-widget) doesn't route through
        // ShowClock, so SnapToExpanded never fires and HideClock → CollapseAnimated
        // early-returns. Mirror the existing "if !IsVisible ApplyHidden()" symmetry
        // by snapping expanded when visible at construction.
        if (_clock.IsVisible) _hover.SnapToExpanded();

        // ponytail: 自适应对齐 — 替换 DragMove 的手动拖拽循环。
        _snapDrag = new SnapDrag(this);
        _snapResize = new SnapResize(this);
    }
    private Action<string>? _langChanged;

    void OnHoverExpandSettingsChanged()
    {
        // Re-apply origin + snap baseline for the current kind without forcing
        // a state change (mirrors ZoneWindow.OnHoverExpandSettingsChanged).
        _hover?.SetEnabled(_clock.EnableRestoreButton);
    }

    void OnClocksChanged()
    {
        if (!IsLoaded) return;
        // Re-fetch latest clock from collection (UpdateClock replaces the object)
        var latest = _widgetService.Clocks.FirstOrDefault(c => c.Id == _clock.Id);
        if (latest != null) _clock = latest;
        // ponytail: ghost-stamp lock — see ZoneWindow.OnZonesChanged for full rationale.
        // 2026-08-23: only stamp the window collapsed when the behavior still thinks it
        // is EXPANDED. During a legitimate animated collapse (CollapseAnimated already
        // set _isExpanded=false while the content visibility flips at the animation's
        // end), HideClock's own UpdateClock fires ClocksChanged and this check used to
        // snap the animation away instantly — the widget's collapse animation never
        // played. Let the animation's completion handler finish the job instead.
        if (!_clock.IsVisible && _hover != null && _hover.IsExpanded
            && !_hover.IsCollapsePending
            && MainContent.Visibility == Visibility.Visible)
            _hover.SnapToCollapsed();
        // ponytail: always sync FillRect, even when hidden — closes the
        // "model blue, screen yellow" desync that ShowClock used to reveal.
        // Acrylic blur stays gated on visibility (needs valid HWND).
        SyncFillRect();
        if (MainContent.Visibility == Visibility.Visible)
            ApplyAcrylic();
        ApplyBackgroundImage();
        ApplyDigitalBackgroundImage();
        ApplyStyle();
    }

    void OnLoad(object s, RoutedEventArgs e)
    {
        if (_vm?.IsLocked != true) NativeMethods.PinToDesktop(this);
        NativeMethods.SetToolWindow(this);
        NativeMethods.DisableDwmFrameShadow(this);
        if (_clock.Mode == ClockDisplayMode.Digital)
            NativeMethods.RemoveThickFrame(this);
        ApplyAcrylic();
        GenerateMarkers();
        if (_clock.Mode == ClockDisplayMode.Digital)
        {
            Width = 320; Height = 140;
        }
        ApplyBackgroundImage();
        ApplyDigitalBackgroundImage();
        // Set rounded corners LAST after all sizing is complete
        NativeMethods.SetRoundedCorners(this, _clock.CornerRadius);
        ApplyLockState();
        if (!_clock.IsVisible) ApplyHidden();
    }

    // Keep OS-level rounded corners in sync on resize
    void OnSizeChanged(object s, SizeChangedEventArgs e)
    {
        if (MainContent.Visibility != Visibility.Visible) return;
        _clock.Width = Width; _clock.Height = Height;
        NativeMethods.UpdateRoundedCorners(this, _clock.CornerRadius);
    }

    // ── Background image (analog clock face) ──

    void ApplyBackgroundImage()
    {
        try
        {
            if (!string.IsNullOrEmpty(_clock.BackgroundImagePath) && System.IO.File.Exists(_clock.BackgroundImagePath))
            {
                var bi = new System.Windows.Media.Imaging.BitmapImage();
                bi.BeginInit();
                bi.UriSource = new Uri(_clock.BackgroundImagePath);
                bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bi.DecodePixelWidth = 1920;
                bi.EndInit();
                bi.Freeze();
                ClockBgImage.Source = bi;
                ClockBgImage.Stretch = Stretch.UniformToFill;

                // UniformToFill — fill target area maintaining aspect ratio
                double imgW = bi.PixelWidth;
                double imgH = bi.PixelHeight;
                double targetSize = 200;
                double utfScale = Math.Max((targetSize * _clock.BgImageZoom) / imgW, (targetSize * _clock.BgImageZoom) / imgH);
                double displayedW = imgW * utfScale;
                double displayedH = imgH * utfScale;

                ClockBgImage.Width = displayedW;
                ClockBgImage.Height = displayedH;

                // Offset: Canvas is resized to displayed size and centered by Grid,
                // so Image position within Canvas is simply the raw offset
                double ox = _clock.BgImageOffsetX;
                double oy = _clock.BgImageOffsetY;

                ClockBgImage.Margin = new Thickness(ox, oy, 0, 0);
                ClockBgImage.HorizontalAlignment = HorizontalAlignment.Left;
                ClockBgImage.VerticalAlignment = VerticalAlignment.Top;
                ClockBgImage.Opacity = Math.Max(0.01, _clock.BackgroundImageOpacity / 100.0);

                // Expand Canvas to match Image size so visual clipping works correctly
                AnalogBgClip.Width = displayedW;
                AnalogBgClip.Height = displayedH;
                AnalogBgClipGeo.Center = new System.Windows.Point(displayedW / 2, displayedH / 2);
                AnalogBgClipGeo.RadiusX = 100;
                AnalogBgClipGeo.RadiusY = 100;
            }
            else
            {
                ClockBgImage.Source = null;
                ClockBgImage.Opacity = 0;
                AnalogBgClip.Width = 200;
                AnalogBgClip.Height = 200;
                AnalogBgClipGeo.Center = new System.Windows.Point(100, 100);
                AnalogBgClipGeo.RadiusX = 100;
                AnalogBgClipGeo.RadiusY = 100;
            }
        }
        catch { if (ClockBgImage != null) { ClockBgImage.Source = null; ClockBgImage.Opacity = 0; } }
    }

    // ── Digital clock background image ──

    void ApplyDigitalBackgroundImage()
    {
        try
        {
            if (!string.IsNullOrEmpty(_clock.DigitalBackgroundImagePath) && System.IO.File.Exists(_clock.DigitalBackgroundImagePath))
            {
                var bi = new System.Windows.Media.Imaging.BitmapImage();
                bi.BeginInit();
                bi.UriSource = new Uri(_clock.DigitalBackgroundImagePath);
                bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bi.DecodePixelWidth = 1920;
                bi.EndInit();
                bi.Freeze();
                DigitalBgImage.Source = bi;
                DigitalBgImage.Stretch = Stretch.UniformToFill;

                // UniformToFill — fill target area maintaining aspect ratio
                double imgW = bi.PixelWidth;
                double imgH = bi.PixelHeight;
                double dw = 320 * _clock.DigitalBgImageZoom;
                double dh = 140 * _clock.DigitalBgImageZoom;
                double utfScale = Math.Max(dw / imgW, dh / imgH);
                double displayedW = imgW * utfScale;
                double displayedH = imgH * utfScale;

                // ponytail: Match the analog pattern (AnalogBgClip.Width/Height = image size).
                // The previous "center in zone (dw x dh)" formula assumed the Border parent was
                // 320x140 — but DigitalBgBorder uses HorizontalAlignment=Stretch inside an
                // auto-sized parent chain (Margin=10 Grid → DigitalPanel). In that chain Stretch
                // doesn't propagate a deterministic size, so the centering math computed a Margin
                // against a phantom zone, and the Border often ended up 0x0 → image clipped to
                // nothing by ClipToBounds. Setting Border = image size directly makes the parent
                // dimension explicit, mirroring AnalogBgClip (Canvas Width=200 Height=200 + code
                // resizes to displayedW/displayedH at ClockWidget.xaml.cs:148-149).
                DigitalBgBorder.Width = displayedW;
                DigitalBgBorder.Height = displayedH;
                DigitalBgImage.Width = displayedW;
                DigitalBgImage.Height = displayedH;

                double ox = _clock.DigitalBgImageOffsetX;
                double oy = _clock.DigitalBgImageOffsetY;

                DigitalBgImage.Margin = new Thickness(ox, oy, 0, 0);
                DigitalBgImage.HorizontalAlignment = HorizontalAlignment.Left;
                DigitalBgImage.VerticalAlignment = VerticalAlignment.Top;
                DigitalBgImage.Opacity = Math.Max(0.01, _clock.DigitalBackgroundImageOpacity / 100.0);
            }
            else
            {
                DigitalBgImage.Source = null;
                DigitalBgImage.Opacity = 0;
            }
        }
        catch { if (DigitalBgImage != null) { DigitalBgImage.Source = null; DigitalBgImage.Opacity = 0; } }
    }

    // ── Generate proper clock tick marks using trigonometry ──

    void GenerateMarkers()
    {
        MarkerCanvas.Children.Clear();
        double cx = 100, cy = 100; // center of 200×200 face

        for (int i = 0; i < 60; i++)
        {
            double angleDeg = i * 6.0; // degrees clockwise from 12 o'clock
            double rad = angleDeg * Math.PI / 180.0;

            bool isHour = i % 5 == 0;
            bool isMajor = i % 15 == 0;

            double innerR = isHour ? 84 : 92;
            double outerR = 98;

            double x1 = cx + innerR * Math.Sin(rad);
            double y1 = cy - innerR * Math.Cos(rad);
            double x2 = cx + outerR * Math.Sin(rad);
            double y2 = cy - outerR * Math.Cos(rad);

            var line = new Line
            {
                X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
                Stroke = new SolidColorBrush(Color.FromArgb(
                    (byte)(isMajor ? 0xCC : (isHour ? 0x90 : 0x40)),
                    0xFF, 0xFF, 0xFF)),
                StrokeThickness = isMajor ? 3.5 : (isHour ? 2.5 : 1.2),
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };
            MarkerCanvas.Children.Add(line);
        }
    }

    // ── Acrylic / frosted glass ──

    // ponytail: FillRect sync extracted from ApplyAcrylic so OnClocksChanged can
    // refresh it without requiring a valid HWND (AcrylicHelper.* needs HWND).
    // Closes the "model blue, screen yellow" desync window when the widget is hidden.
    void SyncFillRect()
    {
        string fillColorStr = ResolveEffectiveFill();
        try
        {
            FillRect.Fill = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(fillColorStr)!);
        }
        catch { }
        // ponytail: force re-render — FillRect as a child paints more reliably than
        // Border.Background inside a transparent window with AllowsTransparency=True.
        FillRect.InvalidateVisual();
        ApplyBodyTextColorAdaptive(fillColorStr);
    }

    /// <summary>Pick the fill color for the active widget.
    /// ponytail 2026-08-25: per-mode fills (DigitalFillColor / AnalogFillColor)
    /// are the live fields — 时钟设置 exposes independent fills per mode, so
    /// resolve by the active mode. Previously this always returned the shared
    /// FillColor, which made the per-mode rows in the settings UI dead edits.</summary>
    string ResolveEffectiveFill() =>
        _clock.Mode == ClockDisplayMode.Analog ? _clock.AnalogFillColor : _clock.DigitalFillColor;

    /// <summary>Adaptive text/icon color based on the widget's effective fill.
    /// When <see cref="DesktopClock.TextColorAdaptive"/> is true, overrides the user-set
    /// TextColor / accent colors so the text stays legible on any background. When the
    /// analog clock has a face image, samples 5 points from it instead of using FillColor.</summary>
    void ApplyBodyTextColorAdaptive(string effectiveFill)
    {
#if DEBUG
        System.Diagnostics.Debug.WriteLine(
            $"[adaptive] ClockWidget: bg={effectiveFill} adaptive={_clock.TextColorAdaptive}");
#endif
        if (!_clock.TextColorAdaptive) return;
        SolidColorBrush brush;
        // Prefer background-image sampling for the analog face when present.
        if (ClockBgImage?.Source is BitmapSource bmp && !string.IsNullOrEmpty(_clock.BackgroundImagePath))
        {
            brush = AdaptiveTextColor.ResolveBrush(AdaptiveTextColor.ResolveTextColorForImage(bmp));
        }
        else
        {
            brush = AdaptiveTextColor.ResolveBrush(effectiveFill);
        }
        if (TimeText != null) TimeText.Foreground = brush;
        if (DateText != null) DateText.Foreground = brush;
        if (AnalogDateText != null) AnalogDateText.Foreground = brush;
        if (HourHand != null) HourHand.Stroke = brush;
        if (MinuteHand != null) MinuteHand.Stroke = brush;
        if (SecondHand != null) SecondHand.Stroke = brush;
        if (HideBtn != null) HideBtn.Foreground = brush;
        if (LockBtn != null) LockBtn.Foreground = brush;
        if (RestoreIconChar != null) RestoreIconChar.Foreground = brush;
        // Refresh MarkerCanvas tick strokes; the analog dial border too.
        if (MarkerCanvas != null)
        {
            foreach (var child in MarkerCanvas.Children)
            {
                if (child is Line ln) ln.Stroke = brush;
            }
        }
        // Outer dial ellipse + center dot
        if (AnalogPanel != null)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(AnalogPanel); i++)
            {
                var ch = VisualTreeHelper.GetChild(AnalogPanel, i);
                if (ch is Ellipse el)
                {
                    if (el.Width == 200 && el.Height == 200) el.Stroke = brush;
                    else if (el.Width == 10 && el.Height == 10) el.Fill = brush;
                }
            }
        }
        // ponytail: analog face background + date-window Border now ride the adaptive brush
        // instead of staying at hardcoded #18000000 / #20FFFFFF / #40FFFFFF. Tinted to the
        // adaptive hue at the original alpha so contrast on the hands/ticks is preserved.
        var c = brush.Color;
        if (AnalogFaceEllipse != null)
            AnalogFaceEllipse.Fill = new SolidColorBrush(Color.FromArgb(0x18, c.R, c.G, c.B));
        if (DateWindowBorder != null)
        {
            DateWindowBorder.Background = new SolidColorBrush(Color.FromArgb(0x20, c.R, c.G, c.B));
            DateWindowBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, c.R, c.G, c.B));
        }
    }

    /// <summary>Re-apply body text adaptive using the current model+config. Call when the
    /// adaptive toggle changes (e.g. settings dialog live preview) or when switching modes
    /// (so digital↔analog picks the right per-mode fill).</summary>
    public void RefreshTextColorAdaptive()
    {
        string fillColorStr = ResolveEffectiveFill();
        if (_clock.TextColorAdaptive) ApplyBodyTextColorAdaptive(fillColorStr);
        else ApplyDefaultTextColors();
    }

    /// <summary>Restore hard-coded / user-configured foregrounds when adaptive is off.</summary>
    void ApplyDefaultTextColors()
    {
        if (TimeText != null) TimeText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_clock.TextColor)!);
        if (DateText != null) DateText.Foreground = new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF));
        if (AnalogDateText != null) AnalogDateText.Foreground = new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF));
        if (HourHand != null) HourHand.Stroke = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
        if (MinuteHand != null) MinuteHand.Stroke = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));
        if (SecondHand != null) SecondHand.Stroke = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0x66, 0x66));
        if (HideBtn != null) HideBtn.Foreground = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF));
        if (LockBtn != null) LockBtn.Foreground = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF));
        if (RestoreIconChar != null) RestoreIconChar.Foreground = new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF));
        // ponytail: reset face bg + date window to XAML defaults so toggling adaptive off
        // doesn't leave the last adaptive tint stuck on these elements.
        if (AnalogFaceEllipse != null)
            AnalogFaceEllipse.Fill = new SolidColorBrush(Color.FromArgb(0x18, 0x00, 0x00, 0x00));
        if (DateWindowBorder != null)
        {
            DateWindowBorder.Background = new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF));
            DateWindowBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
        }
    }

    void ApplyAcrylic()
    {
        SyncFillRect();

        string borderColorStr = _clock.BorderColor;
        double borderThickness = _clock.BorderThickness;

        // ponytail: ghost-glass fix — acrylic follows the expand state: a collapsed clock
        // keeps its full-size window (only the RestoreButton shows), so enabling blur here
        // would paint the tint across the whole window. Only enable while expanded.
        bool expanded = _hover?.IsExpanded ?? false;
        if (_clock.EnableAcrylic && expanded)
        {
            var blurResult = AcrylicHelper.EnableBlur(this, _clock.GlassBlurAmount, _clock.GlassTintOpacity,
                _clock.GlassTintLuminosity, _clock.GlassColorMode);
            if (!blurResult.Success)
                System.Diagnostics.Debug.WriteLine($"[ClockWidget] EnableBlur failed: {blurResult.Error}");
            if (_clock.EnableLiquidGlass)
            {
                ClockBorder.BorderBrush = AcrylicHelper.CreateChromaticBorder();
                ClockBorder.BorderThickness = new Thickness(Math.Max(1.0, borderThickness));
            }
        }
        else
        {
            AcrylicHelper.DisableBlur(this);
        }
    }

    // ── Style (border / fill) ──

    /// <summary>Refresh all visual styles from the current _clock model (for live preview).
    /// Accepts an optional <paramref name="clock"/> to refresh the local reference, mirroring
    /// ZoneWindow.RefreshZone's "KEY FIX" pattern — without it, OnClocksChanged reassigning
    /// _clock leaves the widget looking at a stale object after a save while the dialog
    /// still holds the fresh one.</summary>
    public void RefreshAppearance(DesktopClock? clock = null)
    {
        if (clock != null) _clock = clock;
        // ApplyMode first if the Mode actually changed (preset load can switch Digital↔Analog,
        // changing window size + panel visibility). Gated by previous mode because ApplyMode
        // resets Width/Height to mode defaults — calling it on every slider tweak would
        // clobber any user-resized custom dimensions stored in _clock.Width/Height.
        if (_lastAppliedMode != _clock.Mode)
        {
            _lastAppliedMode = _clock.Mode;
            ApplyMode();
        }
        // ponytail: ApplyAcrylic's EnableBlur guards on IntPtr.Zero internally, and
        // ClockBorder.Background is a managed property — safe to run regardless of
        // MainContent visibility. Live preview must reach the widget even when hidden,
        // so when the user later Show()s it the latest colors are already set.
        ApplyAcrylic();
        ApplyBackgroundImage();
        ApplyDigitalBackgroundImage();
        ApplyStyle();
        _hover?.SetEnabled(_clock.EnableRestoreButton);
    }

    private ClockDisplayMode _lastAppliedMode = ClockDisplayMode.Digital;

    void ApplyStyle()
    {
        // Always apply user's border color (overrides chromatic border from LiquidGlass if needed)
        try
        {
            ClockBorder.BorderBrush = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(_clock.BorderColor)!);
        }
        catch { }
        ClockBorder.BorderThickness = new Thickness(_clock.BorderThickness);
        // ponytail: force the Border element itself to re-render. In transparent windows
        // (AllowsTransparency=True), setting BorderBrush on a Border inside a MainContent
        // Border subtree sometimes caches the previous Brush until the Border is told to
        // invalidate directly — InvalidateMeasure+InvalidateVisual clears the cache.
        ClockBorder.InvalidateMeasure();
        ClockBorder.InvalidateVisual();

        // ponytail 2026-08-26: 圆角/尖角 switch — drive every corner-bearing
        // element + the OS DWM preference from the resolved radius (0 = sharp).
        int r = _clock.CornerRadius;
        MainContent.CornerRadius = new CornerRadius(r);
        ClockBorder.CornerRadius = new CornerRadius(r);
        FillRect.RadiusX = FillRect.RadiusY = r;
        if (System.Windows.PresentationSource.FromVisual(this) != null)
            NativeMethods.SetRoundedCorners(this, r);

        ApplyQuickBar();
    }

    // ponytail 2026-08-25: 极简模式 + 按钮透明度 (时钟设置 spec). Applies in BOTH
    // digital and analog modes — the lock/hide buttons live on the outer grid
    // shared by both mode panels. Zone-style: QuickBarMode collapses the
    // control buttons, ControlOpacity drives their opacity (5-100).
    void ApplyQuickBar()
    {
        if (LockBtn == null || HideBtn == null) return;
        var vis = _clock.QuickBarMode ? Visibility.Collapsed : Visibility.Visible;
        LockBtn.Visibility = vis;
        HideBtn.Visibility = vis;
        var op = Math.Max(0.05, _clock.ControlOpacity / 100.0);
        LockBtn.Opacity = op;
        HideBtn.Opacity = op;
    }

    private string _lastDateText = "";

    void Tick(object? s, EventArgs e)
    {
        try
        {
            var now = DateTime.Now;
            _vm.UpdateTime(now);

            // Update digital display
            if (TimeText != null) TimeText.Text = _vm.DisplayText;

            // Only update DateText when the date actually changes (perf guard)
            if (DateText != null && _vm.DateText != _lastDateText)
            {
                _lastDateText = _vm.DateText;
                DateText.Text = _vm.DateText;
            }

            // Analog mode: only update hands when analog is visible
            if (_clock.Mode == ClockDisplayMode.Analog)
            {
                if (AnalogDateText != null) AnalogDateText.Text = now.Day.ToString();
                if (HourHand?.RenderTransform is RotateTransform hrt) hrt.Angle = _vm.HourAngle;
                if (MinuteHand?.RenderTransform is RotateTransform mrt) mrt.Angle = _vm.MinuteAngle;
                if (SecondHand?.RenderTransform is RotateTransform srt) srt.Angle = _vm.SecondAngle;
                if (SecondHand != null) SecondHand.Visibility = _vm.ShowSeconds ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        catch { /* Ignore timer tick errors during window teardown */ }
    }

    void ApplyMode()
    {
        bool isAnalog = _clock.Mode == ClockDisplayMode.Analog;
        DigitalPanel.Visibility = isAnalog ? Visibility.Collapsed : Visibility.Visible;
        AnalogPanel.Visibility = isAnalog ? Visibility.Visible : Visibility.Collapsed;
        if (isAnalog)
        {
            Width = 240; Height = 260;
            ResizeMode = ResizeMode.NoResize;
            foreach (var grip in FindResizeGrips(this))
                grip.Visibility = Visibility.Collapsed;
            PreviewMouseLeftButtonDown -= AnalogDrag;
            PreviewMouseLeftButtonDown += AnalogDrag;
            ApplyBackgroundImage();
        }
        else
        {
            Width = 320; Height = 140;
            ResizeMode = ResizeMode.CanResize;
            NativeMethods.RemoveThickFrame(this);
            PreviewMouseLeftButtonDown -= AnalogDrag;
            foreach (var grip in FindResizeGrips(this))
                grip.Visibility = Visibility.Visible;
            ApplyDigitalBackgroundImage();
        }
        // ponytail 2026-08-25: re-apply 极简模式/按钮透明度 after the mode
        // switch rebuilds the mode panels.
        ApplyQuickBar();
    }

    void AnalogDrag(object s, MouseButtonEventArgs e)
    {
        // Skip drag when minimized (restore button is showing)
        if (RestoreButton.Visibility == Visibility.Visible) return;
        if (_vm?.IsLocked == true) return;

        if (e.OriginalSource is DependencyObject src)
        {
            while (src != null)
            {
                if (src is System.Windows.Controls.ContextMenu) return;
                if (src is System.Windows.Controls.Button) return;
                src = VisualTreeHelper.GetParent(src);
            }
        }
        _snapDrag?.Start(e, () =>
        {
            if (_vm?.IsLocked != true) NativeMethods.PinToDesktop(this);
            _clock.X = Left; _clock.Y = Top;
        });
    }

    static IEnumerable<Border> FindResizeGrips(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Border b && b.Tag is string tag
                && (tag == "TL" || tag == "TR" || tag == "BL" || tag == "BR"))
                yield return b;
            foreach (var nested in FindResizeGrips(child))
                yield return nested;
        }
    }

    void UpdateContextMenuLabels()
    {
        var cn = _loc.CurrentLanguage == "zh";
        bool isAnalog = _clock.Mode == ClockDisplayMode.Analog;
        CtxSwitchMode.Header = isAnalog ? _loc["Clock.DigitalMode"] : _loc["Clock.AnalogMode"];
        CtxToggleSeconds.Header = _clock.ShowSeconds ? _loc["Clock.HideSeconds"] : _loc["Clock.ShowSeconds"];
        CtxToggle24h.Header = _clock.Use24Hour ? _loc["Clock.Format12h"] : _loc["Clock.Format24h"];
        CtxToggleRestore.Header = _clock.EnableRestoreButton
            ? (cn ? "关闭恢复按钮" : "Disable Restore")
            : (cn ? "启用恢复按钮" : "Enable Restore");
        CtxDelete.Header = _loc["Clock.Delete"];
    }

    void Window_Drag(object s, MouseButtonEventArgs e)
    {
        if (_vm?.IsLocked == true) return;
        _snapDrag?.Start(e, () =>
        {
            if (_vm?.IsLocked != true) NativeMethods.PinToDesktop(this);
            _clock.X = Left; _clock.Y = Top;
        });
    }

    void Window_PreviewMouseLeftButtonDown(object s, MouseButtonEventArgs e)
    {
        // Allow resize grips and buttons to handle their own clicks
        // OriginalSource may be a child (e.g. TextBlock inside Button), so walk up the visual tree
        var src = e.OriginalSource as System.Windows.DependencyObject;
        while (src != null && src != s)
        {
            if (src == HideBtn || src is System.Windows.Controls.Button) return;
            src = System.Windows.Media.VisualTreeHelper.GetParent(src);
        }
        // Allow restore button to handle its own clicks
        if (RestoreButton.Visibility == Visibility.Visible) return;
        if (_vm?.IsLocked == true) return;
        // ponytail: OS routes click normally now (drill-through removed).
        _snapDrag?.Start(e, () =>
        {
            if (_vm?.IsLocked != true) NativeMethods.PinToDesktop(this);
            _clock.X = Left; _clock.Y = Top;
        });
    }

    void Window_PreviewMouseRightButtonDown(object s, MouseButtonEventArgs e)
    {
        // Context menu is on root Grid, no special handling needed
    }

    void ResizeGrip_Down(object s, MouseButtonEventArgs e)
    {
        if (s is not Border g || g.Tag is not string tag) return;
        if (_vm?.IsLocked == true) { e.Handled = true; return; }
        bool left = tag == "TL" || tag == "BL";
        bool top = tag == "TL" || tag == "TR";
        _snapResize?.Start(e, left, top, !left, !top, 140, 80);
        e.Handled = true;
    }

    void SwitchMode_Click(object s, RoutedEventArgs e)
    {
        _clock.Mode = _clock.Mode == ClockDisplayMode.Digital ? ClockDisplayMode.Analog : ClockDisplayMode.Digital;
        _vm.Mode = _clock.Mode;
        ApplyMode();
        GenerateMarkers();
        UpdateContextMenuLabels();
        _widgetService.UpdateClock(_clock);
        // ponytail: re-apply adaptive using the new mode's fill (Digital vs Analog) so the
        // brush computed for the previous mode doesn't bleed into the new one. OnClocksChanged
        // already calls SyncFillRect → ApplyBodyTextColorAdaptive, but doing it here as well
        // makes the refresh deterministic regardless of WidgetService event ordering.
        RefreshTextColorAdaptive();
    }

    void ToggleSeconds_Click(object s, RoutedEventArgs e)
    {
        _clock.ShowSeconds = !_clock.ShowSeconds;
        _vm.ShowSeconds = _clock.ShowSeconds;
        UpdateContextMenuLabels();
        _widgetService.UpdateClock(_clock);
    }

    void Toggle24h_Click(object s, RoutedEventArgs e)
    {
        _clock.Use24Hour = !_clock.Use24Hour;
        _vm.Use24Hour = _clock.Use24Hour;
        UpdateContextMenuLabels();
        _widgetService.UpdateClock(_clock);
    }

    void ToggleRestore_Click(object s, RoutedEventArgs e)
    {
        _clock.EnableRestoreButton = !_clock.EnableRestoreButton;
        var cn = _loc.CurrentLanguage == "zh";
        if (s is MenuItem mi)
            mi.Header = _clock.EnableRestoreButton
                ? (cn ? "关闭恢复按钮" : "Disable Restore")
                : (cn ? "启用恢复按钮" : "Enable Restore");
    }

    void HideBtn_Click(object s, RoutedEventArgs e)
    {
        HideClock();
    }

    void HideBtn_MouseDown(object s, MouseButtonEventArgs e)
    {
        // Mark event as handled to prevent Window_PreviewMouseLeftButtonDown from firing DragMove
        e.Handled = true;
    }

    void LockBtn_Click(object s, RoutedEventArgs e)
    {
        if (_vm == null) return;
        // ponytail: sync from model first — if management UI already flipped this widget's
        // lock state, _vm.IsLocked may be stale (no LockChanged handler fired yet).
        // Without this, double-clicking the lock button can no-op when model and view drift.
        _vm.IsLocked = _clock.IsLocked;
        _vm.IsLocked = !_vm.IsLocked;
        ApplyLockState();
        _widgetService?.SetLocked(_clock.Id.ToString(), _vm.IsLocked);
        _widgetService?.Save();
    }

    void OnServiceLockChanged(string id, bool locked)
    {
        if (id != _clock.Id.ToString()) return;
        if (_vm.IsLocked == locked) return;
        _vm.IsLocked = locked;
        ApplyLockState();
    }

    // ponytail: ClockWidget corners are anonymous Borders (no x:Name), routed by Tag — so find
    // them via the existing FindResizeGrips helper instead of GripTL.Visibility etc.
    void ApplyLockState()
    {
        if (_vm == null) return;
        LockBtn.Content = _vm.IsLocked ? "🔒" : "🔓";
        var gripVis = _vm.IsLocked ? Visibility.Collapsed : Visibility.Visible;
        foreach (var grip in FindResizeGrips(this))
            grip.Visibility = gripVis;
        if (_vm.IsLocked) NativeMethods.PinBelowProgman(this);
    }

    void Delete_Click(object s, RoutedEventArgs e)
    {
        _timer.Stop();
        _widgetService.DeleteClock(_clock.Id);
        Close();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        double delta = e.Delta > 0 ? 0.05 : -0.05;
        _clock.Opacity = Math.Clamp(_clock.Opacity + delta, 0.1, 1.0);
        _vm.Opacity = _clock.Opacity;
        _widgetService.UpdateClock(_clock);
        e.Handled = true;
    }

    /// <summary>Snapshot the currently-displayed fill brush so the style dialog can restore it
    /// after any path that might have re-synced FillRect to model (e.g. OnLoad's ApplyAcrylic
    /// on first show, or PushToWidget during live preview that user later cancels).</summary>
    public System.Windows.Media.Brush? CaptureFillBrush() => FillRect?.Fill;

    /// <summary>Restore a previously captured FillRect brush and force a redraw.</summary>
    public void RestoreFillBrush(System.Windows.Media.Brush? brush)
    {
        if (brush == null || FillRect == null) return;
        FillRect.Fill = brush;
        FillRect.InvalidateVisual();
    }

    public void ShowClock(bool skipResync = false, double waveDelayMs = 0)
    {
#if DEBUG
        DzTrace.Log($"[ClockWidget] ShowClock(skip={skipResync}, wave={waveDelayMs}) ENTRY winVisible={IsVisible} content={MainContent.Visibility} btn={RestoreButton.Visibility} hoverExpanded={_hover?.IsExpanded} modelVisible={_clock.IsVisible} size={Width}x{Height}");
#endif
        if (!IsVisible) Show();
        ApplyMode();
        // ponytail: skipResync=true when called from the property window (was the style dialog).
        // Skip BOTH ApplyAcrylic() and UpdateClock(_clock):
        //   - ApplyAcrylic would read model and write FillRect directly.
        //   - UpdateClock would fire ClocksChanged → OnClocksChanged → SyncFillRect → same result.
        // Without this, the FillRect "snaps" to model the moment the property window opens, even
        // though the user hasn't touched anything.
        Left = _clock.X; Top = _clock.Y;
        if (waveDelayMs > 0)
        {
            // ponytail: batch "Show All" wave — start collapsed and play the clock's own
            // configured animation at its stagger slot (see ZoneWindow.ShowZone).
            MainContent.Visibility = Visibility.Visible; RestoreButton.Visibility = Visibility.Collapsed;
            _hover?.SnapToCollapsed();
            RestoreButton.Visibility = Visibility.Collapsed; // no button flash during the delay
            _hover?.ShowAfterDelay(waveDelayMs);
        }
        else
        {
            MainContent.Visibility = Visibility.Visible; RestoreButton.Visibility = Visibility.Collapsed;
            _hover?.SnapToExpanded();
        }
        // ponytail: ghost-glass fix — re-apply acrylic AFTER SnapToExpanded so the
        // expanded-state gate sees IsExpanded == true and re-enables liquid glass when
        // showing from the collapsed button.
        if (!skipResync)
            ApplyAcrylic();
        MinWidth = 140; MinHeight = 80;
        Width = _clock.Width > 140 ? _clock.Width : 320;
        Height = _clock.Height > 80 ? _clock.Height : 140;
        if (_vm?.IsLocked != true) NativeMethods.PinToDesktop(this);
        NativeMethods.SetRoundedCorners(this, 10);
        if (!_vm.IsLocked) Topmost = true;
        // ponytail: 2026-08-23 — persist LAST so a failure in the model/event path can
        // no longer abort the visual expansion (which used to leave the clock stuck as
        // a RestoreButton while the model already said visible — "Show All needs an
        // extra click"). The window is on screen before any event can observe the model.
        if (!skipResync)
        {
            _clock.IsVisible = true;
            _widgetService.UpdateClock(_clock);
        }
        else
        {
            // In-memory IsVisible flip only — Cancel branch will save it if user backs out,
            // or the dialog's Apply path will save the new state if user commits.
            _clock.IsVisible = true;
        }
        System.Diagnostics.Debug.WriteLine(
            $"[ShowClock] done: winVisible={IsVisible} content={MainContent.Visibility} restore={RestoreButton.Visibility}");
        Activate();
    }

    /// <summary>
    /// Batch-wave entrance for a freshly created window: collapse the just-shown
    /// content and play the clock's own expand animation at the stagger slot.
    /// </summary>
    public void PlayEntranceAnimation(double waveDelayMs)
    {
        if (waveDelayMs <= 0) return;
        _hover?.SnapToCollapsed();
        RestoreButton.Visibility = Visibility.Collapsed;
        _hover?.ShowAfterDelay(waveDelayMs);
    }

    public void HideClock(double waveDelayMs = 0)
    {
#if DEBUG
        // ponytail: verbose HideClock trace for diagnosing widget-minimize regressions.
        // Writes to repo-relative debug_clock.log so reviewers can grep without absolute paths.
        try { System.IO.File.AppendAllText("debug_clock.log",
            $"[{DateTime.Now:HH:mm:ss.fff}] HideClock: EnableRestore={_clock.EnableRestoreButton}, W={Width}, H={Height}\n"); } catch { }
#endif
        _clock.X = Left; _clock.Y = Top; _clock.Width = Width; _clock.Height = Height;
        NativeMethods.DisableRoundedCorners(this);
        if (!_clock.EnableRestoreButton)
        {
            if (waveDelayMs > 0)
            {
                // ponytail: batch "Minimize All" wave — play the clock's own collapse
                // animation first (staggered), then finalize the full hide.
                _hover?.CollapseAfterDelay(waveDelayMs, onComplete: () =>
                {
                    AcrylicHelper.DisableBlur(this);
                    _hover?.SnapToFullHidden();
                    MainContent.Visibility = Visibility.Collapsed;
                    MinWidth = 36; MinHeight = 36;
                    Width = 36; Height = 36;
                    Hide();
                });
            }
            else
            {
                // ponytail: 2026-08-23 — SnapToFullHidden resets the hover state
                // (IsExpanded=false, scale/opacity 0) so no later ApplyAcrylic /
                // RefreshAppearance call can re-enable the DWM glass on the hidden
                // window (ghost "empty liquid glass" bug). See ZoneWindow.HideZone.
                AcrylicHelper.DisableBlur(this);
                _hover?.SnapToFullHidden();
                MainContent.Visibility = Visibility.Collapsed;
                MinWidth = 36; MinHeight = 36;
                Width = 36; Height = 36;
                Hide();
            }
        }
        else
        {
            // ponytail: minimized — window stays at full size, content collapses with animation
            AcrylicHelper.DisableBlur(this);
            if (_vm?.IsLocked != true) NativeMethods.PinToDesktop(this);
            if (waveDelayMs > 0)
                _hover?.CollapseAfterDelay(waveDelayMs, null);
            else
                _hover?.CollapseAnimated();
        }
        _clock.IsVisible = false;
        // Update AFTER Hide() to ensure correct state when event fires
        _widgetService.UpdateClock(_clock);
#if DEBUG
        DzTrace.Log($"[ClockWidget] HideClock DONE winVisible={IsVisible} content={MainContent.Visibility} btn={RestoreButton.Visibility} hoverExpanded={_hover?.IsExpanded} size={Width}x{Height}");
#endif
    }

    void ApplyHidden()
    {
        AcrylicHelper.DisableBlur(this);
        NativeMethods.DisableRoundedCorners(this);
        if (!_clock.EnableRestoreButton)
        {
            // ponytail: 2026-08-23 — see HideClock for the SnapToFullHidden rationale.
            _hover?.SnapToFullHidden();
            MainContent.Visibility = Visibility.Collapsed;
            MinWidth = 36; MinHeight = 36;
            Width = 36; Height = 36;
            Hide();
        }
        else
        {
            // ponytail: 2026-08-23 — restore the full window size after a previous
            // full-hide shrank it to 36×36 (collapsed mode keeps the window at full
            // size, matching ShowClock's sizing).
            MinWidth = 140; MinHeight = 80;
            Width = _clock.Width > 140 ? _clock.Width : 320;
            Height = _clock.Height > 80 ? _clock.Height : 140;
            // ponytail: minimized — window stays at full size, content collapses with animation
            _hover?.SnapToCollapsed();
        }
    }

    void Restore_MouseDown(object s, MouseButtonEventArgs e)
    {
        _restoreDragging = false;
        _restoreDown = e.GetPosition(this);
        RestoreButton.CaptureMouse();
        e.Handled = true;
    }

    void Restore_MouseMove(object s, MouseEventArgs e)
    {
        if (!RestoreButton.IsMouseCaptured) return;
        var d = e.GetPosition(this) - _restoreDown;
        if (Math.Abs(d.X) > 3 || Math.Abs(d.Y) > 3)
        {
            _restoreDragging = true;
            RestoreButton.ReleaseMouseCapture();
            _snapDrag?.Start(e, () =>
            {
                if (_vm?.IsLocked != true) NativeMethods.PinToDesktop(this);
                _clock.X = Left; _clock.Y = Top;
            });
        }
    }

    void Restore_MouseUp(object s, MouseButtonEventArgs e)
    {
        RestoreButton.ReleaseMouseCapture();
        if (!_restoreDragging)
        {
            // ponytail: 2026-08-23 — flip the model to visible BEFORE UpdateClock fires
            // ClocksChanged. OnClocksChanged's ghost-stamp lock ("model hidden but
            // content visible → SnapToCollapsed") used to collapse the window right back
            // mid-expand — and that snap never disables the acrylic — leaving the
            // RestoreButton centered inside the still-on liquid glass. The model must
            // agree with the window before any change event can observe it.
            _clock.IsVisible = true;
            _hover?.ExpandAnimated(permanent: true);
            _widgetService.UpdateClock(_clock);
        }
    }

    void Restore_Enter(object s, MouseEventArgs e) { RestoreButton.Background = RestoreHoverBrush; }
    void Restore_Leave(object s, MouseEventArgs e) { RestoreButton.Background = RestoreIdleBrush; }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        if (_langChanged != null) _loc.LanguageChanged -= _langChanged;
        _langChanged = null;
        _widgetService.LockChanged -= OnServiceLockChanged;
        _clock.HoverExpandSettingsChanged -= OnHoverExpandSettingsChanged;
        _snapDrag?.Detach();
        _snapResize?.Detach();
        _hover?.Dispose();
        base.OnClosed(e);
    }
}
