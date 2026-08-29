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


    public ClockWidget(DesktopClock clock, WidgetService widgetService)
    {
        InitializeComponent();
        // 桌面层策略:失去焦点后回落到壁纸上方(与锁定态一致),不再浮在应用窗口之上。
        // IsVisible 守卫防关窗 teardown 期间 EnsureHandle 抛异常(日历同款)。
        Deactivated += (_, _) => { if (IsVisible) NativeMethods.PinBelowProgman(this); };
        _clock = clock;
        _widgetService = widgetService;
        _vm = new ClockViewModel(clock);
        DataContext = _vm;

        Left = clock.X; Top = clock.Y;
        Opacity = clock.Opacity;
        MinWidth = 140; MinHeight = 80;
        // ponytail: 用当前模式的持久化尺寸初始化，避免 ctor 内 SizeChanged 把加载的尺寸覆盖掉。
        var (cw, ch) = ResolveModeSize();
        Width = cw; Height = ch;

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
        LocationChanged += (_, _) => { _clock.X = Left; _clock.Y = Top; ScheduleSave(); };
        _saveDebounce.Tick += (_, _) => { _saveDebounce.Stop(); if (_savePending) { _savePending = false; _widgetService.Save(); } };
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
        // ponytail 2026-08-28 边框残影修复 — 与 ZoneWindow 同款:展开时恢复圆角,
        // 收起完成时重断言关闭全部 OS 层装饰(玻璃/圆角/DWM 框架阴影)。
        _hover.Expanded += ReapplyAcrylic;
        _hover.Collapsed += OnHoverCollapsed;
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

    // ponytail: 位置防抖保存 — 拖拽移动后持久化 X/Y（与分区 ZoneWindow 一致）。
    private readonly DispatcherTimer _saveDebounce = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _savePending;
    void ScheduleSave() { _savePending = true; _saveDebounce.Stop(); _saveDebounce.Start(); }

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
        DesktopLayer.BringToFront(this);
        NativeMethods.SetToolWindow(this);
        NativeMethods.DisableDwmFrameShadow(this);
        if (_clock.Mode == ClockDisplayMode.Digital)
            NativeMethods.RemoveThickFrame(this);
        ApplyAcrylic();
        GenerateMarkers();
        // ponytail: 用当前模式的持久化尺寸（而非硬编码默认），保证跨重启/模式切换尺寸不缩回。
        var (w, h) = ResolveModeSize();
        Width = w; Height = h;
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
        SaveCurrentSizeForMode(_clock.Mode);
        UpdateDigitalBgClip();
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

                // UniformToFill — fill target area maintaining aspect ratio.
                // 数字背景图全出血铺满整窗（DigitalBgBorder 拉伸填满 ClockBorder 内容区，
                // 圆角由 MainContent 裁剪），因此裁剪/缩放目标 = 当前窗口尺寸。
                double imgW = bi.PixelWidth;
                double imgH = bi.PixelHeight;
                double dw = Width * _clock.DigitalBgImageZoom;
                double dh = Height * _clock.DigitalBgImageZoom;
                double utfScale = Math.Max(dw / imgW, dh / imgH);
                double displayedW = imgW * utfScale;
                double displayedH = imgH * utfScale;

                // 全出血 Border 拉伸填满窗口；Image 按裁剪填充尺寸放大，由 Border 的
                // ClipToBounds 裁掉超出部分，图片边缘紧贴窗框，不再留一圈轮廓。
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
        UpdateDigitalBgClip();
    }

    /// <summary>给全出血数字背景图裁圆角，紧贴窗口圆角（Border 的 CornerRadius 不会裁剪子元素）。</summary>
    void UpdateDigitalBgClip()
    {
        if (DigitalBgBorder == null) return;
        int r = _clock.CornerRadius;
        DigitalBgBorder.Clip = new RectangleGeometry(new Rect(0, 0, Width, Height), r, r);
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
        // ponytail 2026-08-30: 一体化 — 玻璃开时填充并入玻璃 tint,FillRect 透明;
        // 玻璃关/收起时 FillRect 保持纯填充。
        bool glassCarriesFill = _clock.EnableLiquidGlass && (_hover?.IsExpanded ?? false);
        try
        {
            FillRect.Fill = glassCarriesFill
                ? Brushes.Transparent
                : new SolidColorBrush((Color)ColorConverter.ConvertFromString(fillColorStr)!);
        }
        catch { }
        // ponytail: force re-render — FillRect as a child paints more reliably than
        // Border.Background inside a transparent window with AllowsTransparency=True.
        FillRect.InvalidateVisual();
        ApplyDefaultTextColors();
    }

    /// <summary>Pick the fill color for the active widget.
    /// ponytail 2026-08-25: per-mode fills (DigitalFillColor / AnalogFillColor)
    /// are the live fields — 时钟设置 exposes independent fills per mode, so
    /// resolve by the active mode. Previously this always returned the shared
    /// FillColor, which made the per-mode rows in the settings UI dead edits.</summary>
    string ResolveEffectiveFill() =>
        _clock.Mode == ClockDisplayMode.Analog ? _clock.AnalogFillColor : _clock.DigitalFillColor;

    /// <summary>Re-apply the fixed body content colors (digital time uses TextColor; the
    /// remaining chrome uses its hardcoded defaults).</summary>
    public void RefreshTextColorAdaptive()
    {
        ApplyDefaultTextColors();
    }

    /// <summary>Apply the fixed foregrounds split:
    /// 主体内容颜色 → 时间/日期/指针/表盘；按钮颜色 → 锁/隐藏/恢复按钮；
    /// 秒针颜色 → 仅秒针。</summary>
    void ApplyDefaultTextColors()
    {
        SolidColorBrush content, buttons, second;
        try { content = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_clock.TextColor)!); } catch { content = Brushes.White; }
        try { buttons = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_clock.ButtonColor)!); } catch { buttons = Brushes.White; }
        try { second = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_clock.SecondHandColor)!); } catch { second = new SolidColorBrush(Color.FromRgb(0xFF, 0x66, 0x66)); }

        // Digital + analog date — 主体内容颜色.
        if (TimeText != null) TimeText.Foreground = content;
        if (DateText != null) DateText.Foreground = content;
        if (AnalogDateText != null) AnalogDateText.Foreground = content;

        // Hands — 主体内容颜色（秒针除外）.
        if (HourHand != null) HourHand.Stroke = content;
        if (MinuteHand != null) MinuteHand.Stroke = content;
        if (SecondHand != null) SecondHand.Stroke = second;

        // Ticks — 主体内容颜色.
        if (MarkerCanvas != null)
            foreach (var child in MarkerCanvas.Children)
                if (child is Line ln) ln.Stroke = content;

        // Dial ellipse stroke + center dot — 主体内容颜色.
        if (AnalogPanel != null)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(AnalogPanel); i++)
            {
                var ch = VisualTreeHelper.GetChild(AnalogPanel, i);
                if (ch is Ellipse el)
                {
                    if (el.Width == 200 && el.Height == 200) el.Stroke = content;
                    else if (el.Width == 10 && el.Height == 10) el.Fill = content;
                }
            }
        }
        // Face fill + date window tinted to the content color (keeps alpha structure).
        var c = content.Color;
        if (AnalogFaceEllipse != null)
            AnalogFaceEllipse.Fill = new SolidColorBrush(Color.FromArgb(0x18, c.R, c.G, c.B));
        if (DateWindowBorder != null)
        {
            DateWindowBorder.Background = new SolidColorBrush(Color.FromArgb(0x20, c.R, c.G, c.B));
            DateWindowBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, c.R, c.G, c.B));
        }

        // Buttons — 按钮颜色.
        if (HideBtn != null) HideBtn.Foreground = buttons;
        if (LockBtn != null) LockBtn.Foreground = buttons;
        ApplyIconVisual(buttons);
    }

    /// <summary>恢复按钮图标 — 独立 IconColor；空则回退按钮颜色，不随系统深浅色。</summary>
    void ApplyIconVisual(Brush fallback)
    {
        var color = !string.IsNullOrEmpty(_clock.IconColor) ? _clock.IconColor : _clock.ButtonColor;
        Brush ic;
        try { ic = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)!); }
        catch { ic = fallback; }
        var icon = string.IsNullOrEmpty(_clock.IconChar) ? Helpers.IconGlyph.Clock : _clock.IconChar;
        Helpers.IconGlyph.Apply(RestoreIconChar, RestoreIconPath, icon, ic, 18);
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
        if (_clock.EnableLiquidGlass && expanded)
        {
            // ponytail 2026-08-30: 一体化 — 填充并入玻璃 tint(算一层),FillRect 已由
            // SyncFillRect 置透明;填充色与玻璃配色作为两个输入本质上仍是两层。
            var blurResult = AcrylicHelper.EnableBlurComposite(this, _clock.GlassBlurAmount,
                ResolveEffectiveFill(), 1.0, _clock.GlassColorMode, _clock.GlassTintOpacity, _clock.GlassTintLuminosity);
            if (!blurResult.Success)
                System.Diagnostics.Debug.WriteLine($"[ClockWidget] EnableBlur failed: {blurResult.Error}");
            if (ClockGlassBorder != null)
            {
                ClockGlassBorder.BorderBrush = AcrylicHelper.CreateChromaticBorder();
                ClockGlassBorder.BorderThickness = new Thickness(Math.Max(1.0, borderThickness));
                ClockGlassBorder.CornerRadius = new CornerRadius(_clock.CornerRadius);
            }
        }
        else
        {
            AcrylicHelper.DisableBlur(this);
            // ponytail: additive overlay — clear the glass border when the effect is off.
            if (ClockGlassBorder != null)
                ClockGlassBorder.BorderThickness = new Thickness(0);
        }
    }

    /// <summary>
    /// ponytail 2026-08-28 边框残影修复 — 展开(悬停/点击恢复按钮)时把 Win11 圆角
    /// 偏好一并恢复(收起时 OnHoverCollapsed 关掉了它),再走 ApplyAcrylic 恢复玻璃。
    /// </summary>
    void ReapplyAcrylic()
    {
        NativeMethods.SetRoundedCorners(this, _clock.CornerRadius);
        ApplyAcrylic();
    }

    /// <summary>
    /// ponytail 2026-08-28 边框残影修复 — 收起完成时的最终保险(与 ZoneWindow 同款):
    /// 窗口收起后仍保持整窗大小,残留的丙烯酸玻璃 / Win11 圆角 / DWM 框架阴影
    /// 都会以「原窗口轮廓」的形式残留在恢复按钮周围,这里全部重断言关闭。
    /// </summary>
    void OnHoverCollapsed()
    {
        AcrylicHelper.DisableBlur(this);
        NativeMethods.DisableRoundedCorners(this);
        NativeMethods.DisableDwmFrameShadow(this);
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
        // loads the mode's OWN persisted size — calling it on every slider tweak would
        // switch the size back and forth through the mode dimensions.
        if (_lastAppliedMode != _clock.Mode)
        {
            // ponytail: 先把窗口当前显示的那个模式的尺寸存入其持久化字段，再切到新模式
            // （ApplyMode 加载新模式自己的尺寸）— 保证来回切换互不覆盖。
            SaveCurrentSizeForMode(_lastAppliedMode);
            _lastAppliedMode = _clock.Mode;
            ApplyMode();
        }
        else
        {
            // ponytail: 属性面板改 Width/Height 时实时改变窗口大小（仅模式未切换时——
            // 模式切换由 ApplyMode 负责加载模式尺寸，避免这里把模式默认值覆盖回去）。
            // Viewbox 会把内容按新窗口等比放大，文字随之同步放大。
            if (_clock.Width >= MinWidth && _clock.Height >= MinHeight)
            {
                Width = _clock.Width;
                Height = _clock.Height;
            }
        }
        // ponytail: ApplyAcrylic's EnableBlur guards on IntPtr.Zero internally, and
        // ClockBorder.Background is a managed property — safe to run regardless of
        // MainContent visibility. Live preview must reach the widget even when hidden,
        // so when the user later Show()s it the latest colors are already set.
        ApplyAcrylic();
        ApplyBackgroundImage();
        ApplyDigitalBackgroundImage();
        ApplyStyle();
        // ponytail 2026-08-28: 只在开关真正变化时才 SetEnabled，避免外观实时预览
        // 打断正在播放的缩放动画。
        if (_hover != null && _hover.IsEnabled != _clock.EnableRestoreButton)
            _hover.SetEnabled(_clock.EnableRestoreButton);
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
        if (ClockGlassBorder != null)
            ClockGlassBorder.CornerRadius = new CornerRadius(r);
        FillRect.RadiusX = FillRect.RadiusY = r;
        // ponytail 2026-08-28: 收起状态下跳过 DWM 圆角 — 设置面板显示开关 →
        // HideClock → UpdateClock → ClocksChanged → OnClocksChanged → ApplyStyle
        // 这条链会在窗口收起后重新打开整窗大小的圆角描边(边框残影来源)。
        // 展开路径(ShowClock / ReapplyAcrylic)会各自恢复。
        bool collapsed = RestoreButton.Visibility == Visibility.Visible
                         || _hover is { IsExpanded: false };
        if (System.Windows.PresentationSource.FromVisual(this) != null && !collapsed)
            NativeMethods.SetRoundedCorners(this, r);

        ApplyQuickBar();
    }

    // ponytail 2026-08-25: 磁贴模式 + 按钮透明度 (时钟设置 spec). Applies in BOTH
    // digital and analog modes — the lock/hide buttons live on the outer grid
    // shared by both mode panels. Zone-style: TileMode collapses the
    // control buttons, ControlOpacity drives their opacity (5-100).
    void ApplyQuickBar()
    {
        if (LockBtn == null || HideBtn == null) return;
        var vis = _clock.TileMode ? Visibility.Collapsed : Visibility.Visible;
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
        // ponytail: toggle the Viewbox hosts (the mode panels are their children);
        // this keeps the Viewbox that drives proportional scaling attached to the
        // correct content when the mode switches.
        DigitalViewbox.Visibility = isAnalog ? Visibility.Collapsed : Visibility.Visible;
        AnalogViewbox.Visibility = isAnalog ? Visibility.Visible : Visibility.Collapsed;
        // ponytail: 数字背景图全出血铺满整窗，只在数字模式显示。
        if (DigitalBgBorder != null)
            DigitalBgBorder.Visibility = isAnalog ? Visibility.Collapsed : Visibility.Visible;
        if (isAnalog)
        {
            // 加载指针模式自己的持久化尺寸（新时钟/空值回退到默认 240×260）。
            Width = Math.Max(MinWidth, _clock.AnalogWidth ?? 240);
            Height = Math.Max(MinHeight, _clock.AnalogHeight ?? 260);
            // ponytail: 指针模式也用同样的四角等比缩放（Viewbox 会把表盘/指针/刻度按新窗口
            // 等比放大），所以与数字模式一致允许客户端缩放并显示缩放把手。
            ResizeMode = ResizeMode.CanResize;
            // 与数字模式一致：移除 OS 原生边缘缩放边框，四角改用 SnapResize 手动缩放。
            NativeMethods.RemoveThickFrame(this);
            foreach (var grip in FindResizeGrips(this))
                grip.Visibility = Visibility.Visible;
            PreviewMouseLeftButtonDown -= AnalogDrag;
            PreviewMouseLeftButtonDown += AnalogDrag;
            ApplyBackgroundImage();
        }
        else
        {
            // 加载数字模式自己的持久化尺寸（新时钟/空值回退到默认 320×140）。
            Width = Math.Max(MinWidth, _clock.DigitalWidth ?? 320);
            Height = Math.Max(MinHeight, _clock.DigitalHeight ?? 140);
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

    // ── 每模式尺寸持久化 ──

    /// <summary>把当前窗口尺寸写入指定模式自己的持久化字段（内存内），供模式切换/保存使用。</summary>
    void SaveCurrentSizeForMode(ClockDisplayMode mode)
    {
        if (mode == ClockDisplayMode.Analog)
        {
            _clock.AnalogWidth = Width;
            _clock.AnalogHeight = Height;
        }
        else
        {
            _clock.DigitalWidth = Width;
            _clock.DigitalHeight = Height;
        }
    }

    /// <summary>解析当前模式应使用的窗口尺寸：优先用该模式自己的持久化尺寸，空值回退默认。</summary>
    (double Width, double Height) ResolveModeSize()
    {
        if (_clock.Mode == ClockDisplayMode.Analog)
            return (_clock.AnalogWidth ?? 240, _clock.AnalogHeight ?? 260);
        return (_clock.DigitalWidth ?? 320, _clock.DigitalHeight ?? 140);
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
                // ponytail: corner grips — analog now shows resize grips too; this Window-level
                // Preview handler fires before the grip's bubbling MouseLeftButtonDown, so skip
                // the drag and let ResizeGrip_Down resize instead.
                if (src is Border b && b.Tag is string tag
                    && (tag == "TL" || tag == "TR" || tag == "BL" || tag == "BR"))
                    return;
                src = VisualTreeHelper.GetParent(src);
            }
        }
        _snapDrag?.Start(e, () =>
        {
            DesktopLayer.BringToFront(this);
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
        bool isAnalog = _clock.Mode == ClockDisplayMode.Analog;
        CtxSwitchMode.Header = isAnalog ? _loc["Clock.DigitalMode"] : _loc["Clock.AnalogMode"];
        CtxToggleSeconds.Header = _clock.ShowSeconds ? _loc["Clock.HideSeconds"] : _loc["Clock.ShowSeconds"];
        CtxToggle24h.Header = _clock.Use24Hour ? _loc["Clock.Format12h"] : _loc["Clock.Format24h"];
        CtxSettings.Header = _loc["Clock.Settings"];
        CtxMinimize.Header = _loc["Clock.Minimize"];
        CtxLock.Header = _loc[_vm?.IsLocked == true ? "Common.Unlock" : "Common.Lock"];
        CtxDelete.Header = _loc["Clock.Delete"];
    }

    void Window_Drag(object s, MouseButtonEventArgs e)
    {
        if (_vm?.IsLocked == true) return;
        _snapDrag?.Start(e, () =>
        {
            DesktopLayer.BringToFront(this);
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
            // ponytail: corner grips use Tag TL/TR/BL/BR — the Preview handler fires before the
            // grip's own bubbling MouseLeftButtonDown, so if we start a drag here a grip click
            // would MOVE the window instead of resizing. Skip and let ResizeGrip_Down handle it.
            if (src is Border b && b.Tag is string tag
                && (tag == "TL" || tag == "TR" || tag == "BL" || tag == "BR"))
                return;
            src = System.Windows.Media.VisualTreeHelper.GetParent(src);
        }
        // Allow restore button to handle its own clicks
        if (RestoreButton.Visibility == Visibility.Visible) return;
        if (_vm?.IsLocked == true) return;
        // ponytail: OS routes click normally now (drill-through removed).
        _snapDrag?.Start(e, () =>
        {
            DesktopLayer.BringToFront(this);
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
        _snapResize?.Start(e, left, top, !left, !top, 140, 80, () =>
        {
            // ponytail: 缩放结束时把最终尺寸同步回模型 + 落盘（触发 ClocksChanged →
            // 管理界面时钟条的尺寸显示随刷新）。
            _clock.Width = Width; _clock.Height = Height;
            SaveCurrentSizeForMode(_clock.Mode);
            _widgetService?.UpdateClock(_clock);
        });
        e.Handled = true;
    }

    void SwitchMode_Click(object s, RoutedEventArgs e)
    {
        // ponytail: 切模式前先把当前模式的实际窗口尺寸存入该模式的持久化字段，
        // 再加载新模式自己的尺寸 — 这样来回切换时两种模式各自记住大小，不会互相顶掉。
        SaveCurrentSizeForMode(_clock.Mode);
        _clock.Mode = _clock.Mode == ClockDisplayMode.Digital ? ClockDisplayMode.Analog : ClockDisplayMode.Digital;
        _vm.Mode = _clock.Mode;
        ApplyMode();
        GenerateMarkers();
        UpdateContextMenuLabels();
        _widgetService.UpdateClock(_clock);
        // ponytail: re-apply the fixed text colors for the new mode (Digital vs Analog).
        // OnClocksChanged already calls SyncFillRect → ApplyDefaultTextColors, but doing it
        // here as well makes the refresh deterministic regardless of WidgetService ordering.
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

    // ponytail 2026-08-27: 已从右键菜单移除 — 保留方法体以防外部旧代码仍引用。
    void ToggleRestore_Click(object s, RoutedEventArgs e)
    {
        _clock.EnableRestoreButton = !_clock.EnableRestoreButton;
        if (s is MenuItem mi)
            mi.Header = _clock.EnableRestoreButton
                ? _loc["Clock.DisableRestore"]
                : _loc["Clock.EnableRestore"];
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
        // ponytail 2026-08-27: 锁定态变化时同步右键菜单 Header(吸取时钟/日历教训)。
        CtxLock.Header = _vm.IsLocked ? _loc["Common.Unlock"] : _loc["Common.Lock"];
        // ponytail: guard with IsVisible — a re-entrant ClocksChanged during teardown
        // would call PinBelowProgman → EnsureHandle and throw "关闭窗口后，无法设置可见性…".
        if (_vm.IsLocked && IsVisible) NativeMethods.PinBelowProgman(this);
    }

    void Delete_Click(object s, RoutedEventArgs e)
    {
        // ponytail 2026-08-27: 二级确认 — 删除时钟不可恢复。
        var modeKey = _clock.Mode == ClockDisplayMode.Analog ? "Clock.AnalogMode" : "Clock.DigitalMode";
        var confirm = string.Format(_loc["Clock.DeleteConfirm"], _loc[modeKey]);
        if (MessageBox.Show(confirm, _loc["Clock.DeleteTitle"], MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        _timer.Stop();
        _widgetService.DeleteClock(_clock.Id);
        Close();
    }

    // ponytail 2026-08-27: 设置 — 与分区齿轮入口同款 PropertyWindowService 调用,
    // 弹时钟属性浮窗(模式/秒/12-24h/不透明度等)。
    void Settings_Click(object s, RoutedEventArgs e)
    {
        PropertyWindowService.OpenOrFocus(_clock, this);
    }

    // ponytail 2026-08-27: 最小化 = 最小化到任务栏,HideClock 通过托盘图标恢复。
    void Minimize_Click(object s, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
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
            // ponytail 2026-08-28: 从恢复按钮态展开走展开动画(与 CollapseAnimated
            // 对称——关有开也要有);已展开的重复 Show 仍瞬时对齐,不重播。
            bool fromButton = RestoreButton.Visibility == Visibility.Visible;
            MainContent.Visibility = Visibility.Visible; RestoreButton.Visibility = Visibility.Collapsed;
            if (fromButton) _hover?.ExpandAnimated(permanent: true);
            else _hover?.SnapToExpanded();
        }
        // ponytail: ghost-glass fix — re-apply acrylic AFTER SnapToExpanded so the
        // expanded-state gate sees IsExpanded == true and re-enables liquid glass when
        // showing from the collapsed button.
        if (!skipResync)
            ApplyAcrylic();
        MinWidth = 140; MinHeight = 80;
        // ponytail: 用当前模式的持久化尺寸（ApplyMode 已按模式设置，这里用 ResolveModeSize
        // 保持一致），模式切换/图片应用后不再缩回硬编码默认。
        var (showW, showH) = ResolveModeSize();
        Width = showW; Height = showH;
        DesktopLayer.BringToFront(this);
        NativeMethods.SetRoundedCorners(this, 10);
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
            DesktopLayer.BringToFront(this);
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
            // ponytail: 用当前模式的持久化尺寸恢复满尺寸（收起/恢复按钮模式不再缩回默认）。
            var (hiddenW, hiddenH) = ResolveModeSize();
            Width = hiddenW; Height = hiddenH;
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
                DesktopLayer.BringToFront(this);
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

    void Restore_Enter(object s, MouseEventArgs e) { RestoreButton.SetResourceReference(Border.BackgroundProperty, "Menu.Bg.Hover"); }
    void Restore_Leave(object s, MouseEventArgs e) { RestoreButton.SetResourceReference(Border.BackgroundProperty, "Menu.Bg.Surface"); }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        // ponytail: 关闭前落盘未保存的位置/尺寸。
        _saveDebounce.Stop();
        if (_savePending) { _savePending = false; _widgetService.Save(); }
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
