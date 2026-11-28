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

    // ── 指针磁贴纯圆 ─────────────────────────────────────────────────────
    // 窗口仍是 240×260，圆外像素透明；SetWindowRgn 负责系统级命中裁剪，
    // WM_NCHITTEST 作为跨进程穿透的兜底（部分系统上分层窗口的 region 只影响画面）。
    private HwndSource? _hwndSource;
    private bool _windowRegionActive;
    private Rect _lastRegionBounds = Rect.Empty;
    private const int WM_NCHITTEST = 0x0084;
    private const int WM_DPICHANGED = 0x02E0;
    private const int HTTRANSPARENT = -1;
    private const double AnalogCardInset = 10.0;
    private const double TileHitMarginDip = 2.0;


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
        SourceInitialized += (_, _) => AttachHitTestHook();
        SizeChanged += OnSizeChanged;
        DpiChanged += (_, _) => { _lastRegionBounds = Rect.Empty; UpdateAnalogFaceGeometry(); UpdateInputRegion(); };
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
        ApplyBackgroundImage();
        ApplyDigitalBackgroundImage();
        ApplyChromeState();
    }

    void OnLoad(object s, RoutedEventArgs e)
    {
        DesktopLayer.BringToFront(this);
        NativeMethods.SetToolWindow(this);
        NativeMethods.DisableDwmFrameShadow(this);
        // Win11 会给每个顶层窗口描一圈 1px 边框（visible frame border = 1）。小组件的窗口
        // 正好等于可见内容，这条线就压在自己画的那条边框**外面** —— 看起来是"双层边框"、
        // 角上还从描边弧外面绕过去。快捷栏早就关掉了它（见 ZoneWindow.ApplyDockWindowFrame），
        // 四个小组件是同一个问题。
        NativeMethods.RemoveWindowBorder(this);
        if (_clock.Mode == ClockDisplayMode.Digital)
            NativeMethods.RemoveThickFrame(this);
        GenerateMarkers();
        // ponytail: 用当前模式的持久化尺寸（而非硬编码默认），保证跨重启/模式切换尺寸不缩回。
        var (w, h) = ResolveModeSize();
        Width = w; Height = h;
        ApplyBackgroundImage();
        ApplyDigitalBackgroundImage();
        ApplyChromeState();
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
        if (IsAnalogTileActive())
        {
            // 磁贴下不能用 UpdateRoundedCorners：它会 SetWindowRgn(IntPtr.Zero) 把圆清掉。
            UpdateAnalogFaceGeometry();
        }
        else
        {
            NativeMethods.UpdateRoundedCorners(this, _clock.CornerRadius);
            _windowRegionActive = false;
            _lastRegionBounds = Rect.Empty;
            UpdateAnalogFaceGeometry();
        }
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
                double targetSize = 220;
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
                AnalogBgClipGeo.RadiusX = 110;
                AnalogBgClipGeo.RadiusY = 110;
            }
            else
            {
                ClockBgImage.Source = null;
                ClockBgImage.Opacity = 0;
                AnalogBgClip.Width = 220;
                AnalogBgClip.Height = 220;
                AnalogBgClipGeo.Center = new System.Windows.Point(110, 110);
                AnalogBgClipGeo.RadiusX = 110;
                AnalogBgClipGeo.RadiusY = 110;
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

    // ── Meridian dial (01 经纬) ───────────────────────────────────────────

    static Point OnCircle(Point center, double radius, double deg)
    {
        double a = deg * Math.PI / 180.0;
        return new Point(center.X + radius * Math.Sin(a), center.Y - radius * Math.Cos(a));
    }

    static StreamGeometry RadialTicks(Point center, double inner, double outer, int count,
        int step = 1, Func<int, bool>? include = null)
    {
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            for (int i = 0; i < count; i += step)
            {
                if (include != null && !include(i)) continue;
                double deg = i * (360.0 / count);
                ctx.BeginFigure(OnCircle(center, inner, deg), false, false);
                ctx.LineTo(OnCircle(center, outer, deg), true, false);
            }
        }
        geo.Freeze();
        return geo;
    }

    void GenerateMarkers()
    {
        if (MarkerCanvas == null) return;
        var c = new Point(110, 110);

        // 60 minute ticks (all positions), 12 hour ticks, 4 cardinal ticks.
        MinuteTickPath.Data = RadialTicks(c, 101.6, 106.2, 60);
        HourTickPath.Data = RadialTicks(c, 96.4, 106.6, 12);
        CardinalTickPath.Data = RadialTicks(c, 94.2, 106.6, 4);

        // The only accent on the dial: a 2px north index just outside the rim.
        var north = new StreamGeometry();
        using (var ctx = north.Open())
        {
            ctx.BeginFigure(OnCircle(c, 108.0, 0), false, false);
            ctx.LineTo(OnCircle(c, 110.0, 0), true, false);
        }
        north.Freeze();
        NorthIndexPath.Data = north;
    }

    // ── Acrylic / frosted glass ──

    // ponytail: FillRect sync extracted from ApplyAcrylic so OnClocksChanged can
    // refresh it without requiring a valid HWND (AcrylicHelper.* needs HWND).
    // Closes the "model blue, screen yellow" desync window when the widget is hidden.
    void SyncFillRect()
    {
        string fillColorStr = ResolveEffectiveFill();

        // 指针磁贴：只留圆内填充。DWM 只能整窗模糊且不认窗口 region，圆内真实玻璃
        // 只能走壁纸采样背板（TryApplyTileBackdrop）；采样不可用时用同一套 tint 兜底。
        if (IsAnalogTileActive())
        {
            Brush tileFill;
            try
            {
                var fill = (Color)ColorConverter.ConvertFromString(fillColorStr)!;
                // 兜底圆底最低 60% 不透明：没有模糊时默认 3% 黑底在浅色/亮壁纸上
                // 会让白刻度完全消失（本机实测）。保留用户色相，只抬 alpha。
                byte floor = 0x99;
                if (fill.A < floor) fill.A = floor;

                var tint = _clock.EnableLiquidGlass
                    ? AcrylicHelper.ResolveWpfGlassTintBrush(_clock.GlassMaterial, fillColorStr,
                        1.0, _clock.GlassColorMode, _clock.GlassTintOpacity, _clock.GlassTintLuminosity)
                    : null;

                if (tint is SolidColorBrush scb)
                {
                    var c = scb.Color;
                    if (c.A < floor) c.A = floor;
                    tileFill = new SolidColorBrush(c);
                }
                else if (tint != null)
                {
                    tileFill = tint;
                }
                else
                {
                    tileFill = new SolidColorBrush(fill);
                }
            }
            catch { tileFill = new SolidColorBrush(Color.FromArgb(0x99, 0x08, 0x0B, 0x11)); }
            // 采样层自己带 tint；FillRect 只留命中用的近透明层，否则 60% 实色会把
            // 背板盖死（视觉上又退回实心圆）。
            FillRect.Fill = _tileBackdropActive ? AcrylicHelper.HitTestFill : tileFill;
            UpdateFaceFillClip();
            FillRect.InvalidateVisual();
            ApplyDefaultTextColors();
            return;
        }

        // ponytail 2026-08-30: 一体化 — 玻璃开时填充并入玻璃 tint,FillRect 透明;
        // 玻璃关/收起时 FillRect 保持纯填充。
        bool glassCarriesFill = _clock.EnableLiquidGlass && (_hover?.IsExpanded ?? false);
        // 非亚克力材质 DWM 不认着色 → 合成着色改由填充层承担(见 ResolveWpfGlassTintBrush)。
        var wpfGlassTint = glassCarriesFill
            ? AcrylicHelper.ResolveWpfGlassTintBrush(_clock.GlassMaterial, fillColorStr, 1.0,
                _clock.GlassColorMode, _clock.GlassTintOpacity, _clock.GlassTintLuminosity)
            : null;
        try
        {
            FillRect.Fill = wpfGlassTint ?? (glassCarriesFill
                ? AcrylicHelper.HitTestFill
                : new SolidColorBrush((Color)ColorConverter.ConvertFromString(fillColorStr)!));
        }
        catch { }
        // 普通模式不能残留磁贴圆裁剪（切回卡片时这里是唯一兜底）。
        FillRect.Clip = null;
        _wallpaperBackdrop?.SetClip(null);
        // ponytail: force re-render — FillRect as a child paints more reliably than
        // Border.Background inside a transparent window with AllowsTransparency=True.
        FillRect.InvalidateVisual();
        ApplyDefaultTextColors();
    }

    // ── 自绘背板(方案①:壁纸采样 + 可变半径模糊) ──

    WallpaperBackdropLayer? _wallpaperBackdrop;

    /// <summary>本次 ApplyAcrylic 里磁贴是否由采样背板接管（决定 FillRect 用近透明还是实色兜底）。</summary>
    bool _tileBackdropActive;

    /// <summary>材质 = 「自绘背板」通道时,把壁纸采样层插到填充层之下并关掉 DWM 玻璃。
    /// 返回 true = 背板已接管;壁纸不可用/材质=自定义/开关关闭 → false(回退 DWM)。</summary>
    bool TryApplyWallpaperBackdrop(string fillColorStr)
    {
        // ponytail 2026-09-26(二期修订): 回退 DWM 前必须先把背板层藏起来 —— 它是不透明的
        // 壁纸裁剪图,留着就会盖住 DWM 玻璃及着色,表现为"改颜色没反应"(见 ZoneWindow 同款注释)。
        if (!AcrylicHelper.ResolveSelfDrawn())
        {
            _wallpaperBackdrop?.SetVisible(false);
            return false;
        }
        _wallpaperBackdrop ??= WallpaperBackdropLayer.TryCreate();
        if (_wallpaperBackdrop == null) return false;

        _wallpaperBackdrop.Attach(MainContent, FillRect);
        _wallpaperBackdrop.SetVisible(true);
        _wallpaperBackdrop.BindWindow(this);
        _wallpaperBackdrop.SetAppearance(this,
            AcrylicHelper.ResolveSelfDrawnRadiusPx(_clock.GlassBlurAmount),
            AcrylicHelper.ResolveBackdropTint(fillColorStr, 1.0, _clock.GlassColorMode,
                _clock.GlassTintOpacity, _clock.GlassTintLuminosity),
            AcrylicHelper.ResolveNoiseOpacity(_clock.GlassMaterial));
        AcrylicHelper.DisableBlur(this);
        return true;
    }

    /// <summary>磁贴圆形采样背板。DWM 的 accent 背板完全不认窗口 region（改圆角/半窗
    /// region 逐像素不变，见 AcrylicHelper.DwmWindowCornerRadiusDip），所以圆内真实玻璃
    /// 只能自己画：与普通卡片共用 WallpaperBackdropLayer / 预模糊缓存，只把图层 Clip 到
    /// 表盘圆。返回 false = 无采样可用，调用方回退实色圆底。</summary>
    bool TryApplyTileBackdrop(string fillColorStr)
    {
        if (!_clock.EnableLiquidGlass || !AcrylicHelper.ResolveSelfDrawn())
        {
            _wallpaperBackdrop?.SetVisible(false);
            return false;
        }
        _wallpaperBackdrop ??= WallpaperBackdropLayer.TryCreate();
        if (_wallpaperBackdrop == null) return false;

        _wallpaperBackdrop.Attach(MainContent, FillRect);
        _wallpaperBackdrop.BindWindow(this);
        _wallpaperBackdrop.SetVisible(true);
        // 先裁圆再取图：SetAppearance 首帧若直接铺整窗，会闪一下矩形角。
        ApplyTileBackdropClip();

        // 磁贴比普通卡片更需要刻度可读性：Default 色板在 DWM 下由系统补一层
        // 着色，采样通道里它是全透明的，亮壁纸上的白刻度会消失。保留用户色相，
        // 只给合成 tint 抬一个最低暗底（35%）。
        var tint = AcrylicHelper.ResolveBackdropTint(fillColorStr, 1.0, _clock.GlassColorMode,
            _clock.GlassTintOpacity, _clock.GlassTintLuminosity);
        const byte TileTintFloor = 0x5A;
        if (tint.A < TileTintFloor) tint.A = TileTintFloor;

        _wallpaperBackdrop.SetAppearance(this,
            AcrylicHelper.ResolveSelfDrawnRadiusPx(_clock.GlassBlurAmount),
            tint,
            AcrylicHelper.ResolveNoiseOpacity(_clock.GlassMaterial));
        AcrylicHelper.DisableBlur(this);
        return true;
    }

    /// <summary>采样层只显示表盘圆；非磁贴/离磁贴时清掉，避免普通卡片被圆裁。</summary>
    void ApplyTileBackdropClip()
    {
        if (_wallpaperBackdrop == null) return;
        if (!IsAnalogTileActive())
        {
            _wallpaperBackdrop.SetClip(null);
            return;
        }
        var (center, radius) = AnalogFaceGeometryDip();
        var clip = new EllipseGeometry(center, radius, radius);
        clip.Freeze();
        _wallpaperBackdrop.SetClip(clip);
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

    /// <summary>Resolve the dial accent. The model's legacy default is #FFFFFFFF
    /// (AccentColor existed but was never consumed); treat pure white as "unset"
    /// so existing clocks get the Meridian cyan instead of a dead white accent.</summary>
    Color ResolveAccentColor()
    {
        string s = _clock.AccentColor;
        if (string.IsNullOrWhiteSpace(s) || string.Equals(s, "#FFFFFFFF", StringComparison.OrdinalIgnoreCase))
            return Color.FromRgb(0x4C, 0xC9, 0xF0);
        try { return (Color)ColorConverter.ConvertFromString(s)!; }
        catch { return Color.FromRgb(0x4C, 0xC9, 0xF0); }
    }

    /// <summary>Apply the fixed foregrounds split:
    /// 主体内容颜色 → 时间/指针/刻度/表面；按钮颜色 → 锁/隐藏/恢复按钮；
    /// 秒针颜色 → 仅秒针；强调色 → 12 点索引。</summary>
    void ApplyDefaultTextColors()
    {
        SolidColorBrush content, buttons, second;
        try { content = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_clock.TextColor)!); } catch { content = Brushes.White; }
        try { buttons = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_clock.ButtonColor)!); } catch { buttons = Brushes.White; }
        try { second = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_clock.SecondHandColor)!); } catch { second = new SolidColorBrush(Color.FromRgb(0xFF, 0x66, 0x66)); }
        var accent = new SolidColorBrush(ResolveAccentColor());

        // Digital — 主体内容颜色.
        if (TimeText != null) TimeText.Foreground = content;
        if (DateText != null) DateText.Foreground = content;

        // Meridian hands.
        if (HourHandShape != null) { HourHandShape.Fill = content; HourHandShape.Stroke = content; }
        if (MinuteHandShape != null) { MinuteHandShape.Fill = content; MinuteHandShape.Stroke = content; }
        if (SecondHand != null) SecondHand.Stroke = second;
        if (SecondHandDot != null) SecondHandDot.Fill = second;

        // Meridian ticks + north accent.
        if (MinuteTickPath != null) MinuteTickPath.Stroke = content;
        if (HourTickPath != null) HourTickPath.Stroke = content;
        if (CardinalTickPath != null) CardinalTickPath.Stroke = content;
        if (NorthIndexPath != null) NorthIndexPath.Stroke = accent;

        var c = content.Color;
        if (AnalogFaceEllipse != null)
        {
            AnalogFaceEllipse.Fill = new SolidColorBrush(Color.FromArgb(0x18, c.R, c.G, c.B));
            AnalogFaceEllipse.Stroke = new SolidColorBrush(Color.FromArgb(0x2E, c.R, c.G, c.B));
        }
        if (AnalogInnerRing != null)
            AnalogInnerRing.Stroke = new SolidColorBrush(Color.FromArgb(0x0E, c.R, c.G, c.B));
        if (CenterCap != null) CenterCap.Fill = content;
        if (CenterHole != null) CenterHole.Fill = new SolidColorBrush(Color.FromArgb(0xB0, 0x08, 0x0A, 0x0F));

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

    public void ApplyAcrylic()
    {
        // 指针磁贴：DWM 只能整窗模糊且不认 region，玻璃只能走壁纸采样背板（裁成圆）；
        // 采样不可用时回退 SyncFillRect 的实色 tint 圆底。
        if (IsAnalogTileActive())
        {
            _tileBackdropActive = TryApplyTileBackdrop(ResolveEffectiveFill());
            if (!_tileBackdropActive)
            {
                AcrylicHelper.DisableBlur(this);
                _wallpaperBackdrop?.SetVisible(false);
            }
            SyncFillRect();
            if (ClockGlassBorder != null)
            {
                ClockGlassBorder.Visibility = Visibility.Collapsed;
                ClockGlassBorder.BorderThickness = new Thickness(0);
            }
            return;
        }

        _tileBackdropActive = false;
        SyncFillRect();

        // ponytail: ghost-glass fix — acrylic follows the expand state: a collapsed clock
        // keeps its full-size window (only the RestoreButton shows), so enabling blur here
        // would paint the tint across the whole window. Only enable while expanded.
        bool expanded = _hover?.IsExpanded ?? false;
        if (_clock.EnableLiquidGlass && expanded)
        {
            // ponytail 2026-09-26 方案①: 选了材质 → 壁纸采样自绘背板(半径真正生效),
            // 填充层已在 SyncFillRect 里置透明;壁纸不可用/自定义 → 落回 DWM。
            bool backdropActive = TryApplyWallpaperBackdrop(ResolveEffectiveFill());
            if (!backdropActive)
            {
                // ponytail 2026-08-30: 一体化 — 填充并入玻璃 tint(算一层),FillRect 已由
                // SyncFillRect 置透明;填充色与玻璃配色作为两个输入本质上仍是两层。
                var blurResult = AcrylicHelper.EnableBlurComposite(this, _clock.GlassBlurAmount,
                    ResolveEffectiveFill(), 1.0, _clock.GlassColorMode, _clock.GlassTintOpacity, _clock.GlassTintLuminosity,
                    AcrylicHelper.ResolveAccentState(_clock.GlassMaterial),
                    AcrylicHelper.ResolveUseClassicBlur(_clock.GlassMaterial));
                if (!blurResult.Success)
                    System.Diagnostics.Debug.WriteLine($"[ClockWidget] EnableBlur failed: {blurResult.Error}");
            }
            // 边框跟随分区：不再叠那条彩色渐变（AcrylicHelper.CreateChromaticBorder 是液态
            // 玻璃的旧装饰）。玻璃开着时可见边框就是 ApplyStyle 里设的「边框颜色 / 线宽」。
            if (ClockGlassBorder != null)
            {
                ClockGlassBorder.Visibility = Visibility.Visible;
                ClockGlassBorder.BorderThickness = new Thickness(0);
            }
        }
        else
        {
            AcrylicHelper.DisableBlur(this);
            _wallpaperBackdrop?.SetVisible(false);
            // ponytail: additive overlay — clear the glass border when the effect is off.
            if (ClockGlassBorder != null)
            {
                ClockGlassBorder.Visibility = Visibility.Visible;
                ClockGlassBorder.BorderThickness = new Thickness(0);
            }
        }
    }

    /// <summary>
    /// ponytail 2026-08-28 边框残影修复 — 展开(悬停/点击恢复按钮)时把 Win11 圆角
    /// 偏好一并恢复(收起时 OnHoverCollapsed 关掉了它),再走 ApplyAcrylic 恢复玻璃。
    /// 2026-11-28: 展开后也要把磁贴圆与命中区重新对齐。
    /// </summary>
    void ReapplyAcrylic()
    {
        if (!IsAnalogTileActive()) ClearTileWindowRegion();
        NativeMethods.SetRoundedCorners(this, _clock.CornerRadius);
        ApplyStyle();
        ApplyAcrylic();
        ApplyAnalogTileChrome();
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
        UpdateInputRegion();
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
        ApplyBackgroundImage();
        ApplyDigitalBackgroundImage();
        ApplyChromeState();
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
        // 可见半径压到 Windows 给窗口切的那个值（8 DIP）：边框/填充画得比它更圆的话，
        // 四个角上 DWM 切出来的玻璃就会从描边弧外面露出一小条（见 NativeMethods.VisibleCornerRadius）。
        int rv = NativeMethods.VisibleCornerRadius(r, _clock.EnableLiquidGlass && !AcrylicHelper.ResolveSelfDrawn());
        MainContent.CornerRadius = new CornerRadius(rv);
        ClockBorder.CornerRadius = new CornerRadius(rv);
        if (ClockGlassBorder != null)
            ClockGlassBorder.CornerRadius = new CornerRadius(rv);
        FillRect.RadiusX = FillRect.RadiusY = rv;
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

    // ── 指针磁贴：纯圆外观 / 圆形命中区 ─────────────────────────────────

    bool IsAnalogTileActive() =>
        _clock != null && _clock.Mode == ClockDisplayMode.Analog && _clock.TileMode;

    (Point Center, double Radius) AnalogFaceGeometryDip()
    {
        double w = ActualWidth > 0 ? ActualWidth : (double.IsNaN(Width) ? 240 : Width);
        double h = ActualHeight > 0 ? ActualHeight : (double.IsNaN(Height) ? 260 : Height);
        if (w <= 0) w = 240;
        if (h <= 0) h = 260;
        double diameter = Math.Max(0, Math.Min(w - AnalogCardInset * 2, h - AnalogCardInset * 2));
        return (new Point(w / 2, h / 2), diameter / 2);
    }

    void UpdateFaceFillClip()
    {
        if (FillRect == null) return;
        if (!IsAnalogTileActive())
        {
            FillRect.Clip = null;
            _wallpaperBackdrop?.SetClip(null);
            return;
        }
        var (center, radius) = AnalogFaceGeometryDip();
        var clip = new EllipseGeometry(center, radius, radius);
        clip.Freeze();
        FillRect.Clip = clip;
        // 磁贴采样层跟表盘圆同一几何（同一局部坐标系：壁纸层挂在 MainContent 根面板）。
        _wallpaperBackdrop?.SetClip(clip);
    }

    /// <summary>统一入口：普通样式 → 玻璃/填充 → 磁贴覆盖 → 命中区。
    /// 不做反向调用，避免 ApplyStyle/ApplyAcrylic 互相递归。</summary>
    void ApplyChromeState()
    {
        if (_clock == null) return;
        // 退出磁贴先撤命中区，再开 DWM 玻璃；否则玻璃会被旧圆区域裁一帧。
        if (!IsAnalogTileActive()) ClearTileWindowRegion();
        ApplyStyle();
        ApplyAcrylic();
        ApplyAnalogTileChrome();
    }

    /// <summary>磁贴覆盖层。窗口尺寸不变，只把卡片收成圆并把命中裁圆；
    /// 关磁贴时把填充裁剪/手柄/圆角/玻璃恢复。</summary>
    void ApplyAnalogTileChrome()
    {
        if (_clock == null) return;

        if (IsAnalogTileActive())
        {
            var (center, radius) = AnalogFaceGeometryDip();
            var clip = new EllipseGeometry(center, radius, radius);
            clip.Freeze();
            FillRect.Clip = clip;
            FillRect.RadiusX = FillRect.RadiusY = 0;

            ClockBorder.BorderThickness = new Thickness(0);
            ClockBorder.BorderBrush = Brushes.Transparent;
            ClockBorder.CornerRadius = new CornerRadius(0);
            MainContent.CornerRadius = new CornerRadius(0);

            if (ClockGlassBorder != null)
            {
                ClockGlassBorder.Visibility = Visibility.Collapsed;
                ClockGlassBorder.BorderThickness = new Thickness(0);
            }

            NativeMethods.DisableRoundedCorners(this);
            NativeMethods.DisableDwmFrameShadow(this);
            ResizeMode = ResizeMode.NoResize;
            foreach (var grip in FindResizeGrips(this))
                grip.Visibility = Visibility.Collapsed;
        }
        else
        {
            ClearTileWindowRegion();
            if (FillRect != null) FillRect.Clip = null;

            int rv = NativeMethods.VisibleCornerRadius(_clock.CornerRadius,
                _clock.EnableLiquidGlass && !AcrylicHelper.ResolveSelfDrawn());
            try
            {
                ClockBorder.BorderBrush = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString(_clock.BorderColor)!);
            }
            catch { }
            ClockBorder.BorderThickness = new Thickness(_clock.BorderThickness);
            ClockBorder.CornerRadius = new CornerRadius(rv);
            MainContent.CornerRadius = new CornerRadius(rv);
            if (FillRect != null) FillRect.RadiusX = FillRect.RadiusY = rv;
            if (ClockGlassBorder != null)
            {
                ClockGlassBorder.Visibility = Visibility.Visible;
                ClockGlassBorder.CornerRadius = new CornerRadius(rv);
            }

            ResizeMode = ResizeMode.CanResize;
            foreach (var grip in FindResizeGrips(this))
                grip.Visibility = Visibility.Visible;

            bool collapsed = RestoreButton.Visibility == Visibility.Visible
                             || _hover is { IsExpanded: false };
            if (System.Windows.PresentationSource.FromVisual(this) != null && !collapsed)
                NativeMethods.SetRoundedCorners(this, _clock.CornerRadius);
        }

        UpdateFaceFillClip();
        UpdateInputRegion();
    }

    void UpdateAnalogFaceGeometry()
    {
        UpdateFaceFillClip();
        UpdateInputRegion();
        // 尺寸/DPI 变了：采样层仍按整窗物理矩形裁壁纸，只刷 Viewbox，不重取图。
        _wallpaperBackdrop?.RefreshViewbox();
    }

    void ClearTileWindowRegion()
    {
        if (!_windowRegionActive) return;
        NativeMethods.ClearWindowRegion(this);
        _windowRegionActive = false;
        _lastRegionBounds = Rect.Empty;
    }

    void SetTileWindowRegion(Rect boundsDip)
    {
        // OnMouseWheel/主题色这类高频 ClocksChanged 不改变圆尺寸；避免反复 SetWindowRgn。
        if (_windowRegionActive && _lastRegionBounds == boundsDip) return;
        if (NativeMethods.SetEllipticalWindowRegion(this, boundsDip, TileHitMarginDip))
        {
            _windowRegionActive = true;
            _lastRegionBounds = boundsDip;
        }
        else
        {
            _windowRegionActive = false;
            _lastRegionBounds = Rect.Empty;
        }
    }

    void UpdateInputRegion()
    {
        if (_clock == null) return;
        if (!IsAnalogTileActive() || !IsVisible)
        {
            ClearTileWindowRegion();
            return;
        }

        if (MainContent.Visibility == Visibility.Visible)
        {
            var (center, radius) = AnalogFaceGeometryDip();
            SetTileWindowRegion(new Rect(center.X - radius, center.Y - radius, radius * 2, radius * 2));
            return;
        }

        if (RestoreButton.Visibility == Visibility.Visible)
        {
            double w = RestoreButton.ActualWidth > 0 ? RestoreButton.ActualWidth : RestoreButton.Width;
            double h = RestoreButton.ActualHeight > 0 ? RestoreButton.ActualHeight : RestoreButton.Height;
            var bounds = RestoreButton.TransformToAncestor(this)
                .TransformBounds(new Rect(0, 0, w, h));
            SetTileWindowRegion(bounds);
            return;
        }

        ClearTileWindowRegion();
    }

    bool IsPointInsideTileInput(Point p)
    {
        if (MainContent.Visibility == Visibility.Visible)
        {
            var (center, radius) = AnalogFaceGeometryDip();
            double dx = p.X - center.X;
            double dy = p.Y - center.Y;
            double r = radius + TileHitMarginDip;
            return dx * dx + dy * dy <= r * r;
        }

        if (RestoreButton.Visibility == Visibility.Visible)
        {
            double w = RestoreButton.ActualWidth > 0 ? RestoreButton.ActualWidth : RestoreButton.Width;
            double h = RestoreButton.ActualHeight > 0 ? RestoreButton.ActualHeight : RestoreButton.Height;
            var bounds = RestoreButton.TransformToAncestor(this)
                .TransformBounds(new Rect(0, 0, w, h));
            bounds.Inflate(TileHitMarginDip, TileHitMarginDip);
            return bounds.Contains(p);
        }
        return false;
    }

    void AttachHitTestHook()
    {
        if (_hwndSource != null) return;
        try
        {
            var helper = new WindowInteropHelper(this);
            var hwnd = helper.EnsureHandle();
            _hwndSource = HwndSource.FromHwnd(hwnd);
            _hwndSource?.AddHook(WndProc);
        }
        catch { }
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_DPICHANGED)
        {
            // DIP 尺寸不变但物理 region 必须重设；清掉 bounds 缓存让 UpdateInputRegion 重新 SetWindowRgn。
            _lastRegionBounds = Rect.Empty;
            Dispatcher.BeginInvoke(new Action(UpdateAnalogFaceGeometry), DispatcherPriority.Loaded);
        }
        else if (msg == WM_NCHITTEST && IsAnalogTileActive() && IsVisible)
        {
            long lp = lParam.ToInt64();
            int sx = unchecked((short)(lp & 0xFFFF));
            int sy = unchecked((short)((lp >> 16) & 0xFFFF));
            try
            {
                var p = PointFromScreen(new Point(sx, sy));
                if (!IsPointInsideTileInput(p))
                {
                    handled = true;
                    return new IntPtr(HTTRANSPARENT);
                }
            }
            catch { }
        }
        return IntPtr.Zero;
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
        // ponytail: 小按钮的不透明度统一走 HoverHighlight（全应用同一个值）。
        HoverHighlight.ApplyControlOpacity(_clock.ControlOpacity, new UIElement[] { LockBtn, HideBtn });
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
                if (HourHandShape?.RenderTransform is RotateTransform hrt) hrt.Angle = _vm.HourAngle;
                if (MinuteHandShape?.RenderTransform is RotateTransform mrt) mrt.Angle = _vm.MinuteAngle;
                if (SecondHandGroup?.RenderTransform is RotateTransform srt) srt.Angle = _vm.SecondAngle;
                if (SecondHandGroup != null)
                    SecondHandGroup.Visibility = _vm.ShowSeconds ? Visibility.Visible : Visibility.Collapsed;
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
        if (IsAnalogTileActive()) { e.Handled = true; return; }
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
        ApplyChromeState();
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
    public System.Windows.Media.Brush? CaptureFillBrush() => IsAnalogTileActive() ? null : FillRect?.Fill;

    /// <summary>Restore a previously captured FillRect brush and force a redraw.</summary>
    public void RestoreFillBrush(System.Windows.Media.Brush? brush)
    {
        if (brush == null || FillRect == null || IsAnalogTileActive()) return;
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
        // ponytail 2026-09-26「先出整窗框再展开」修复:窗口尺寸必须落在展开动画之前
        // (详见 ZoneWindow.ShowZone 同款注释)。整窗隐藏的挂件被缩到 36×36,若尺寸恢复
        // 排在动画之后,第一帧会先出现一个全尺寸空窗轮廓。ResolveModeSize 只读模型,
        // 提前调用无副作用。
        MinWidth = 140; MinHeight = 80;
        var (showW, showH) = ResolveModeSize();
        Width = showW; Height = showH;
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
        if (!IsAnalogTileActive()) ClearTileWindowRegion();
        if (!skipResync)
            ApplyAcrylic();
        // 尺寸已在本方法开头(动画之前)设好 —— 这里不再重复设置,保持「先定尺寸再动画」。
        DesktopLayer.BringToFront(this);
        ApplyStyle();
        ApplyAnalogTileChrome();
        if (!IsAnalogTileActive())
            NativeMethods.SetRoundedCorners(this, _clock.CornerRadius);
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
        UpdateInputRegion();
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
        UpdateInputRegion();
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
            ClearTileWindowRegion();
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
        if (!_clock.EnableRestoreButton) ClearTileWindowRegion(); else UpdateInputRegion();
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
            ClearTileWindowRegion();
        }
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
        UpdateInputRegion();
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
            UpdateInputRegion();
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
        // ponytail 2026-11-14: 审计修复 —— ctor 里订阅了 ClocksChanged(单例 WidgetService),
        // 之前 OnClosed 漏退 → 每个关掉的时钟窗口被单例事件永久持有(窗口+可视树+图标位图)。
        _widgetService.ClocksChanged -= OnClocksChanged;
        _widgetService.LockChanged -= OnServiceLockChanged;
        _clock.HoverExpandSettingsChanged -= OnHoverExpandSettingsChanged;
        _snapDrag?.Detach();
        _snapResize?.Detach();
        ClearTileWindowRegion();
        if (_hwndSource != null)
        {
            try { _hwndSource.RemoveHook(WndProc); } catch { }
            _hwndSource = null;
        }
        _hover?.Dispose();
        base.OnClosed(e);
    }
}
