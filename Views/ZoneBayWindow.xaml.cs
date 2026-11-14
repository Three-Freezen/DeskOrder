using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DeskOrder.PluginApi;
using DesktopZones.Helpers;
using DesktopZones.Models;
using DesktopZones.Services;

namespace DesktopZones.Views;

/// <summary>
/// 分区坞窗口：贴边胶囊、芯片条、拖入高亮、自动隐藏、最小化成恢复按钮。
/// 插件只提供芯片条内容；窗口、几何、分区交互都由宿主掌握。
/// </summary>
public partial class ZoneBayWindow : Window
{
    // 与原生快捷栏同一把尺子：一格 48 + 上下内边距 6 × 2 = 60 DIP。
    // 芯片 42 是击中区，图标 28；窗口 == 可见胶囊，不再靠 Margin 收边。
    const double BarThickness = 60;
    const double ChipSize = 42;
    const double ChipGap = 4;
    const double EdgeMargin = 8;
    const double ExpansionGap = 8;
    const double AutoHidePeek = 3;

    readonly PluginZoneBar _bar;
    readonly ZoneBayService _service;
    readonly PluginZoneBayDescriptor _descriptor;
    readonly LocalizationService _loc = LocalizationService.Instance;

    PluginZoneBayContext? _pluginContext;
    PluginZoneBarSettings? _settings;
    JsonPluginStorage? _storage;
    FrameworkElement? _pluginContent;
    WallpaperBackdropLayer? _glassLayer;
    bool _loaded;

    Rect _shownRect;
    Rect _hiddenRect;
    Rect _collapsedRect;
    bool _autoHidden;
    bool _collapsed;
    bool _dragPeek;
    bool _suppressAutoHide;
    DateTime _hideAt = DateTime.MaxValue;

    string? _expandedZoneId;
    ZoneWindow? _expandedWindow;
    bool _expandedPermanent;
    FrameworkElement? _lastAnchor;

    DispatcherTimer? _pollTimer;
    DispatcherTimer? _outsideTimer;
    DispatcherTimer? _hoverTimer;
    DispatcherTimer? _hoverLeaveTimer;
    string? _hoverZoneId;
    FrameworkElement? _hoverAnchor;

    ContextMenu? _menu;
    MenuItem? _menuSettings;
    MenuItem? _menuCollapseAll;
    MenuItem? _menuMinimize;
    MenuItem? _menuHide;
    MenuItem? _menuManage;
    MenuItem? _menuDelete;
    Action<string>? _langChanged;

    // ── 芯片拖出 / 坞内换位（P2）──
    // 拖动期间用 Win32 光标轮询移动影子（和标题栏拖动同一套思路），松手时：
    // 仍在坞内 → 换位；出坞 → 在落点变回桌面的 36×36 恢复按钮。
    string? _chipDragZoneId;
    int _chipDragFrom = -1;
    bool _chipDragArmed;
    bool _chipDragMoving;
    Point _chipDragDown;
    DispatcherTimer? _chipDragTimer;

    public PluginZoneBar Bar => _bar;

    /// <summary>分区菜单的兜底锚点（坞主体）。芯片右键没给锚点时代替使用。</summary>
    internal FrameworkElement MenuAnchor => Root;

    internal string DebugVisualState() =>
        $"win={ActualWidth:F0}x{ActualHeight:F0} back={Backplate.Visibility}/{Backplate.ActualWidth:F0}x{Backplate.ActualHeight:F0} " +
        $"content={ContentHost.Visibility}/{ContentHost.ActualWidth:F0}x{ContentHost.ActualHeight:F0} " +
        $"plugin={(_pluginContent == null ? "null" : _pluginContent.GetType().Name)}/{_pluginContent?.ActualWidth:F0}x{_pluginContent?.ActualHeight:F0} " +
        $"fallback={FallbackChips.Children.Count} zones={_bar.ZoneIds.Count} glass={GlassHost.Visibility} collapsed={_collapsed} " +
        $"align={FallbackChips.HorizontalAlignment}/{FallbackChips.VerticalAlignment} " +
        $"chip0={(FallbackChips.Children.Count > 0 && FallbackChips.Children[0] is FrameworkElement fe ? $"{fe.ActualWidth:F0}x{fe.ActualHeight:F0}" : "-")}";

    /// <summary>自测用：吐出插件内容前几层视觉树（类型/尺寸/对齐），排查纵向坞芯片异常。</summary>
    internal string DebugContentTree()
    {
        var sb = new System.Text.StringBuilder();
        void Walk(DependencyObject d, int depth)
        {
            if (depth > 6) return;
            if (d is not FrameworkElement fe) return;
            sb.Append(new string(' ', depth * 2)).Append(fe.GetType().Name);
            if (!string.IsNullOrEmpty(fe.Name)) sb.Append('#').Append(fe.Name);
            sb.Append($" {fe.ActualWidth:F0}x{fe.ActualHeight:F0} vis={fe.Visibility} align={fe.HorizontalAlignment}/{fe.VerticalAlignment}");
            try
            {
                var pos = fe.TransformToAncestor(this).Transform(new Point(0, 0));
                sb.Append($" @({pos.X:F0},{pos.Y:F0})");
            }
            catch { }
            if (fe is System.Windows.Controls.Image img)
                sb.Append($" src={img.Source?.Width:F0}x{img.Source?.Height:F0}");
            sb.AppendLine();
            int n = VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n; i++) Walk(VisualTreeHelper.GetChild(d, i), depth + 1);
        }
        try { if (_pluginContent != null) Walk(_pluginContent, 0); } catch (Exception ex) { sb.Append("tree-error: ").Append(ex.Message); }
        return sb.ToString();
    }

    /// <summary>自测用：把坞菜单按当前状态打开一次，返回可见项文字（分隔线记作 |），然后关掉。</summary>
    internal string DebugOpenBarMenu()
    {
        if (_menu == null) return "no-menu";
        var sb = new System.Text.StringBuilder();
        try
        {
            _menu.PlacementTarget = Root;
            _menu.IsOpen = true;
            foreach (var entry in _menu.Items)
            {
                switch (entry)
                {
                    case Separator sep when sep.Visibility == Visibility.Visible:
                        sb.Append("| ");
                        break;
                    case MenuItem mi when mi.Visibility == Visibility.Visible:
                        sb.Append(mi.Header?.ToString() ?? "?").Append(" · ");
                        break;
                }
            }
            _menu.IsOpen = false;
        }
        catch (Exception ex)
        {
            return "error: " + ex.Message;
        }
        return sb.ToString();
    }

    public ZoneBayWindow(PluginZoneBar bar, ZoneBayService service, PluginZoneBayDescriptor descriptor)
    {
        InitializeComponent();
        _bar = bar;
        _service = service;
        _descriptor = descriptor;
        Title = string.IsNullOrWhiteSpace(bar.Name) ? "Zone Bay" : bar.Name;

        Icon = Application.Current?.TryFindResource("AppIcon") as ImageSource;
        BuildMenu();
        ApplyLoc();
        _langChanged = _ => ApplyLoc();
        _loc.LanguageChanged += _langChanged;

        Loaded += OnLoaded;
        Closed += OnClosed;

        MouseEnter += (_, _) => OnBarMouseEnter();
        MouseLeave += (_, _) => OnBarMouseLeave();
        SizeChanged += (_, _) => UpdateGlassClip();
        // 坞主体右键 = 坞菜单；芯片自己的右键在各自的处理器里打开「分区菜单」并 Handled，
        // 因此这里用冒泡事件（不用 Preview），芯片的菜单不会被坞菜单抢掉。
        Root.MouseRightButtonUp += OnBarRightButtonUp;
        RestoreIcon.Data = Application.Current?.TryFindResource("Icon.ZoneBay") as Geometry;
    }

    // ── 生命周期 ──

    void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loaded = true;
        NativeMethods.SetToolWindow(this);
        NativeMethods.RemoveWindowBorder(this);
        NativeMethods.DisableDwmFrameShadow(this);
        DesktopLayer.BringToFront(this);
        ApplyEverything();
        if (_bar.IsVisible) ShowBar();

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _pollTimer.Tick += (_, _) => PollAutoHide();
        _pollTimer.Start();
    }

    void OnClosed(object? sender, EventArgs e)
    {
        _pollTimer?.Stop();
        _outsideTimer?.Stop();
        _hoverTimer?.Stop();
        _hoverLeaveTimer?.Stop();
        if (_langChanged != null) _loc.LanguageChanged -= _langChanged;
        try { AcrylicHelper.DisableBlur(this); } catch { }
        try { _glassLayer?.Detach(); } catch { }
        _glassLayer = null;
        _pluginContent = null;
        _pluginContext = null;
        Collapse(null, save: false, animate: false);
    }

    // ── 几何 / 外观 ──

    /// <summary>坞所在显示器的工作区（DIP）。多屏 / 每屏 DPI 下不再用只覆盖主屏的
    /// <c>SystemParameters.WorkArea</c>；<see cref="PluginZoneBar.MonitorIndex"/> 越界 = 主屏。</summary>
    Rect WorkArea => MonitorHelper.WorkAreaDipAt(_bar.MonitorIndex);

    /// <summary>同边多坞（P2 多实例）时向内叠开的一层厚度。第一层（lane 0）贴边。</summary>
    const double LaneGap = 6;

    void ComputeRects()
    {
        var wa = WorkArea;
        // 同边 + 同显示器上，排在前面（SortOrder 更小）的坞各占一层；本坞往里让 lane 层。
        double lane = _service.LaneOf(_bar) * (BarThickness + LaneGap);
        switch (_bar.NormalizedEdge)
        {
            case ZoneBayEdges.Top:
                _shownRect = new Rect(wa.Left, wa.Top + lane, wa.Width, BarThickness);
                _hiddenRect = new Rect(wa.Left, wa.Top + lane - (BarThickness - AutoHidePeek), wa.Width, BarThickness);
                _collapsedRect = new Rect(wa.Left + EdgeMargin + lane, wa.Top + EdgeMargin, 44, 44);
                break;
            case ZoneBayEdges.Left:
                _shownRect = new Rect(wa.Left + lane, wa.Top, BarThickness, wa.Height);
                _hiddenRect = new Rect(wa.Left + lane - (BarThickness - AutoHidePeek), wa.Top, BarThickness, wa.Height);
                _collapsedRect = new Rect(wa.Left + EdgeMargin, wa.Top + EdgeMargin + lane, 44, 44);
                break;
            case ZoneBayEdges.Right:
                _shownRect = new Rect(wa.Right - BarThickness - lane, wa.Top, BarThickness, wa.Height);
                _hiddenRect = new Rect(wa.Right - lane - AutoHidePeek, wa.Top, BarThickness, wa.Height);
                _collapsedRect = new Rect(wa.Right - 44 - EdgeMargin, wa.Top + EdgeMargin + lane, 44, 44);
                break;
            default:
                _shownRect = new Rect(wa.Left, wa.Bottom - BarThickness - lane, wa.Width, BarThickness);
                _hiddenRect = new Rect(wa.Left, wa.Bottom - lane - AutoHidePeek, wa.Width, BarThickness);
                _collapsedRect = new Rect(wa.Left + EdgeMargin + lane, wa.Bottom - 44 - EdgeMargin, 44, 44);
                break;
        }
    }

    void ApplyEdgeGeometry()
    {
        ComputeRects();
        if (!_bar.AutoHide && _autoHidden)
        {
            _autoHidden = false;
            TriggerStrip.Visibility = Visibility.Collapsed;
            if (_bar.EnableLiquidGlass) ApplyGlass();
        }
        if (_bar.AutoHide && !_autoHidden && _hideAt == DateTime.MaxValue)
            _hideAt = DateTime.UtcNow.AddMilliseconds(600);
        bool vertical = _bar.IsVertical;
        // 窗口就是胶囊：不再留 8 DIP 的透明边，否则 60 高的窗口里只剩 44 可见。
        Backplate.Margin = new Thickness(0);
        // 长边留 12（快捷栏 DockPadH），短边由芯片自身在 60 里居中（42 + 9×2）。
        ContentHost.Margin = vertical ? new Thickness(0, 12, 0, 12) : new Thickness(12, 0, 12, 0);
        FallbackChips.Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal;
        // 芯片居中（修复：之前横排左对齐，短条上整排贴左）。
        FallbackChips.HorizontalAlignment = HorizontalAlignment.Center;
        FallbackChips.VerticalAlignment = VerticalAlignment.Center;
        ChipScroll.HorizontalContentAlignment = HorizontalAlignment.Center;
        ChipScroll.VerticalContentAlignment = VerticalAlignment.Center;
        ChipScroll.PanningMode = vertical ? PanningMode.VerticalOnly : PanningMode.HorizontalOnly;
        ChipScroll.HorizontalScrollBarVisibility = vertical ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Hidden;
        ChipScroll.VerticalScrollBarVisibility = vertical ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Disabled;

        RestoreButton.HorizontalAlignment = _bar.NormalizedEdge == ZoneBayEdges.Right
            ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        RestoreButton.VerticalAlignment = _bar.NormalizedEdge == ZoneBayEdges.Bottom
            ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        RestoreButton.Margin = new Thickness(
            _bar.NormalizedEdge == ZoneBayEdges.Right ? 0 : 4,
            _bar.NormalizedEdge == ZoneBayEdges.Bottom ? 0 : 4,
            _bar.NormalizedEdge == ZoneBayEdges.Right ? 4 : 0,
            _bar.NormalizedEdge == ZoneBayEdges.Bottom ? 4 : 0);

        TriggerStrip.Height = vertical ? double.NaN : AutoHidePeek;
        TriggerStrip.Width = vertical ? AutoHidePeek : double.NaN;
        TriggerStrip.HorizontalAlignment = _bar.NormalizedEdge == ZoneBayEdges.Right
            ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        TriggerStrip.VerticalAlignment = _bar.NormalizedEdge == ZoneBayEdges.Top
            ? VerticalAlignment.Bottom : VerticalAlignment.Stretch;
        TriggerStrip.Visibility = _autoHidden && !_collapsed ? Visibility.Visible : Visibility.Collapsed;
        if (_autoHidden && !_collapsed) PositionTrigger();

        if (_collapsed)
            SetRect(_collapsedRect, animate: false);
        else
            SetRect(_autoHidden ? _hiddenRect : _shownRect, animate: false);
    }

    double VisibleCornerRadius()
    {
        double radius = Math.Max(0, _bar.CornerRadius);
        // DWM accent 只能把窗口切成系统固定的 8 DIP；此时描边必须跟着 8，
        // 否则四角会从玻璃弧外面露出一小条。自绘背板/关玻璃时 WPF 说多少就是多少。
        bool dwmGlass = _bar.EnableLiquidGlass && !_collapsed && _bar.IsVisible
                        && !AcrylicHelper.ResolveSelfDrawn();
        return dwmGlass ? Math.Min(radius, AcrylicHelper.DwmWindowCornerRadiusDip) : radius;
    }

    void ApplyStyle()
    {
        var fill = WithOpacity(_bar.FillColor, _bar.FillOpacity, "#08000000");
        double radius = VisibleCornerRadius();
        Backplate.Background = fill;
        Backplate.BorderBrush = BrushFrom(_bar.BorderColor, Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF));
        Backplate.BorderThickness = new Thickness(Math.Max(0, _bar.BorderThickness));
        Backplate.CornerRadius = new CornerRadius(radius);
        DropHighlight.CornerRadius = new CornerRadius(radius);
        UpdateGlassClip();

        var restoreFill = WithOpacity(_bar.ChipFillColor, _bar.ChipOpacity, "#CC1E1E1E");
        if (restoreFill is SolidColorBrush rsc && rsc.Color.A < 8)
            restoreFill = Frozen(Color.FromArgb(0xCC, 0x1E, 0x1E, 0x1E));
        RestoreButton.Background = restoreFill;
        RestoreButton.BorderBrush = BrushFrom(_bar.BorderColor, Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
        RestoreButton.CornerRadius = new CornerRadius(Math.Min(10, Math.Max(0, radius - 2)));

        if (_bar.EnableLiquidGlass && !_collapsed && _bar.IsVisible && !AcrylicHelper.ResolveSelfDrawn())
            NativeMethods.SetRoundedCorners(this, (int)Math.Round(radius));
        else
            NativeMethods.DisableRoundedCorners(this);

        ApplyGlass();
        RefreshSettings();
    }

    /// <summary>
    /// 液态玻璃完全走宿主原生三套配方：
    /// ① 渲染方案 = 壁纸采样 → 与分区/快捷栏同一个 WallpaperBackdropLayer，只铺胶囊；
    /// ② 否则 → DWM 的 EnableBlurComposite（填充并入 tint，材质决定 accent state）；
    /// ③ 失败 → 至少铺合成后的纯色，绝不出现“玻璃关掉又没背板”的空窗。
    /// </summary>
    void ApplyGlass()
    {
        try
        {
            if (!_bar.EnableLiquidGlass || _collapsed || !_bar.IsVisible)
            {
                AcrylicHelper.DisableBlur(this);
                HideGlassLayer();
                Backplate.Background = WithOpacity(_bar.FillColor, _bar.FillOpacity, "#08000000");
                return;
            }

            if (AcrylicHelper.ResolveSelfDrawn())
            {
                AcrylicHelper.DisableBlur(this);
                try
                {
                    var layer = EnsureGlassLayer();
                    if (layer == null) { HideGlassLayer(); ApplyTintOnlyGlass(); return; }

                    var tint = AcrylicHelper.ResolveBackdropTint(_bar.FillColor, _bar.FillOpacity,
                        _bar.GlassColorMode, _bar.GlassTintOpacity, _bar.GlassTintLuminosity);
                    var noise = AcrylicHelper.ResolveNoiseOpacity(_bar.GlassMaterial);
                    layer.Update(this, AcrylicHelper.ResolveSelfDrawnRadiusPx(_bar.GlassBlurAmount), tint, noise);
                    layer.SetVisible(true);
                    GlassHost.Visibility = Visibility.Visible;
                    UpdateGlassClip();
                    Backplate.Background = Brushes.Transparent;
                }
                catch (Exception ex)
                {
                    DzTrace.Always("[ZoneBay] wallpaper glass failed: " + ex.Message);
                    HideGlassLayer();
                    ApplyTintOnlyGlass();
                }
                return;
            }

            HideGlassLayer();
            var result = AcrylicHelper.EnableBlurComposite(this, _bar.GlassBlurAmount,
                _bar.FillColor, _bar.FillOpacity, _bar.GlassColorMode,
                _bar.GlassTintOpacity, _bar.GlassTintLuminosity,
                AcrylicHelper.ResolveAccentState(_bar.GlassMaterial),
                AcrylicHelper.ResolveUseClassicBlur(_bar.GlassMaterial));
            if (result.Success)
            {
                Backplate.Background = Brushes.Transparent;
#if DEBUG
                DzTrace.Always($"[ZoneBay] glass=dwm success material='{_bar.GlassMaterial}' blur={_bar.GlassBlurAmount} r={VisibleCornerRadius():F0}");
#endif
                return;
            }

#if DEBUG
            DzTrace.Always("[ZoneBay] glass=dwm failed: " + result.Error + " → fallback blur");
#endif
            // 组合玻璃失败（部分显卡/旧系统）：退回普通 blur + WPF 填充；
            // 连 blur 都上不去时给一层能看清图标的深色，别让整条坞变透明。
            var blur = AcrylicHelper.EnableBlur(this, _bar.GlassBlurAmount, _bar.GlassTintOpacity,
                _bar.GlassTintLuminosity, _bar.GlassColorMode,
                AcrylicHelper.ResolveAccentState(_bar.GlassMaterial),
                AcrylicHelper.ResolveUseClassicBlur(_bar.GlassMaterial));
            Backplate.Background = blur.Success
                ? WithOpacity(_bar.FillColor, _bar.FillOpacity, "#08000000")
                : Frozen(Color.FromArgb(0xB8, 0x12, 0x17, 0x1F));
        }
        catch (Exception ex)
        {
            DzTrace.Always("[ZoneBay] glass failed: " + ex.Message);
        }
    }

    void ApplyTintOnlyGlass()
    {
        var c = AcrylicHelper.ResolveBackdropTint(_bar.FillColor, _bar.FillOpacity,
            _bar.GlassColorMode, _bar.GlassTintOpacity, _bar.GlassTintLuminosity);
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        Backplate.Background = brush;
    }

    WallpaperBackdropLayer? EnsureGlassLayer()
    {
        if (GlassHost == null) return null;
        if (_glassLayer != null)
        {
            _glassLayer.AttachTo(GlassHost);
            return _glassLayer;
        }
        var layer = WallpaperBackdropLayer.TryCreate();
        if (layer == null) return null;
        layer.AttachTo(GlassHost);
        _glassLayer = layer;
        return layer;
    }

    void HideGlassLayer()
    {
        _glassLayer?.SetVisible(false);
        if (GlassHost != null) GlassHost.Visibility = Visibility.Collapsed;
    }

    void UpdateGlassClip()
    {
        if (GlassHost == null) return;
        double w = Math.Max(1, ActualWidth);
        double h = Math.Max(1, ActualHeight);
        double r = Math.Max(0, Backplate.CornerRadius.TopLeft);
        GlassHost.Clip = new RectangleGeometry(new Rect(0, 0, w, h), r, r);
    }

    public void ApplyEverything()
    {
        ApplyEdgeGeometry();
        ApplyStyle();
        EnsurePluginContent();
        ApplyCollapsed();
        RefreshChips();
        SettingsChanged();
    }

    public void ApplyCollapsed()
    {
        if (_bar.IsCollapsed && _bar.EnableRestoreButton)
        {
            if (!_collapsed)
            {
                _collapsed = true;
                Backplate.Visibility = Visibility.Collapsed;
                ContentHost.Visibility = Visibility.Collapsed;
                RestoreButton.Visibility = Visibility.Visible;
                VerticalAlignment = VerticalAlignment.Top;
                HorizontalAlignment = HorizontalAlignment.Left;
                SetRect(_collapsedRect, animate: false);
                AcrylicHelper.DisableBlur(this);
                HideGlassLayer();
                NativeMethods.DisableRoundedCorners(this);
            }
        }
        else
        {
            if (_collapsed)
            {
                _collapsed = false;
                Backplate.Visibility = Visibility.Visible;
                ContentHost.Visibility = Visibility.Visible;
                RestoreButton.Visibility = Visibility.Collapsed;
                ApplyStyle();
                SetRect(_autoHidden ? _hiddenRect : _shownRect, animate: false);
            }
            else
            {
                Backplate.Visibility = Visibility.Visible;
                ContentHost.Visibility = Visibility.Visible;
                RestoreButton.Visibility = Visibility.Collapsed;
            }
            if (!_bar.EnableRestoreButton) _bar.IsCollapsed = false;
        }
    }

    public void ShowBar()
    {
        _bar.IsVisible = true;
        _autoHidden = false;
        if (!IsVisible) Show();
        if (!_loaded) return;
        ApplyEverything();
        DesktopLayer.BringToFront(this);
        if (!_bar.IsCollapsed) SetRect(_shownRect, animate: false);
        _hideAt = _bar.AutoHide ? DateTime.UtcNow.AddMilliseconds(1200) : DateTime.MaxValue;
        _service.Save();
    }

    public void HideBar()
    {
        _bar.IsVisible = false;
        _autoHidden = false;
        try { Hide(); } catch { }
    }

    void SetRect(Rect rect, bool animate)
    {
        if (!_loaded || !IsVisible)
        {
            Left = rect.Left; Top = rect.Top; Width = rect.Width; Height = rect.Height;
            return;
        }
        if (Math.Abs(Width - rect.Width) > 0.5 || Math.Abs(Height - rect.Height) > 0.5)
        {
            Width = rect.Width;
            Height = rect.Height;
        }
        if (!animate)
        {
            BeginAnimation(LeftProperty, null);
            BeginAnimation(TopProperty, null);
            Left = rect.Left;
            Top = rect.Top;
            return;
        }

        var dur = TimeSpan.FromMilliseconds(160);
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(LeftProperty, new DoubleAnimation(rect.Left, dur) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
        BeginAnimation(TopProperty, new DoubleAnimation(rect.Top, dur) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
        Left = rect.Left;
        Top = rect.Top;
    }

    // ── 自动隐藏 ──

    void OnBarMouseEnter()
    {
        if (_bar.AutoHide && !_bar.IsCollapsed) SetAutoHidden(false);
    }

    void OnBarMouseLeave()
    {
        if (_bar.AutoHide && !_bar.IsCollapsed) _hideAt = DateTime.UtcNow.AddMilliseconds(600);
    }

    void TriggerStrip_MouseEnter(object sender, MouseEventArgs e)
    {
        if (_bar.AutoHide && !_bar.IsCollapsed) SetAutoHidden(false);
    }

    void PollAutoHide()
    {
        if (!_loaded || !_bar.IsVisible || _collapsed || _dragPeek || _suppressAutoHide) return;

        if (!_bar.AutoHide)
        {
            if (_autoHidden) SetAutoHidden(false);
            return;
        }
        if (NativeMethods.GetCursorPos(out var pt))
        {
            var cursor = ScreenDip(new Point(pt.x, pt.y));
            // 用「收起后的矩形」判定贴边热区：多屏、同边多层坞都自动成立，
            // 不用再按四边分别写一套 WorkArea 判断。
            bool near = cursor.X >= _hiddenRect.Left - 4 && cursor.X <= _hiddenRect.Right + 4
                        && cursor.Y >= _hiddenRect.Top - 4 && cursor.Y <= _hiddenRect.Bottom + 4;
            bool inside = cursor.X >= _shownRect.Left - 4 && cursor.X <= _shownRect.Right + 4
                          && cursor.Y >= _shownRect.Top - 4 && cursor.Y <= _shownRect.Bottom + 4;
            if (near || inside)
            {
                SetAutoHidden(false);
                _hideAt = DateTime.UtcNow.AddMilliseconds(600);
                return;
            }
        }
        if (!_autoHidden && DateTime.UtcNow >= _hideAt && !IsExpanded)
            SetAutoHidden(true);
    }

    void SetAutoHidden(bool hidden)
    {
        if (_autoHidden == hidden) return;
        _autoHidden = hidden;
        TriggerStrip.Visibility = hidden ? Visibility.Visible : Visibility.Collapsed;
        if (hidden) PositionTrigger();
        SetRect(hidden ? _hiddenRect : _shownRect, animate: _loaded);
        if (hidden) { AcrylicHelper.DisableBlur(this); HideGlassLayer(); }
        else ApplyGlass();
    }

    void PositionTrigger()
    {
        TriggerStrip.HorizontalAlignment = HorizontalAlignment.Stretch;
        TriggerStrip.VerticalAlignment = VerticalAlignment.Stretch;
        switch (_bar.NormalizedEdge)
        {
            case ZoneBayEdges.Top:
                TriggerStrip.VerticalAlignment = VerticalAlignment.Bottom;
                break;
            case ZoneBayEdges.Left:
                TriggerStrip.HorizontalAlignment = HorizontalAlignment.Right;
                break;
            case ZoneBayEdges.Right:
                TriggerStrip.HorizontalAlignment = HorizontalAlignment.Left;
                break;
            default:
                TriggerStrip.VerticalAlignment = VerticalAlignment.Top;
                break;
        }
    }

    bool IsExpanded => _expandedZoneId != null;

    public void PeekForDrag(bool on)
    {
        _dragPeek = on;
        if (on)
        {
            SetAutoHidden(false);
            _suppressAutoHide = true;
        }
        else
        {
            _suppressAutoHide = false;
            _hideAt = DateTime.UtcNow.AddMilliseconds(600);
        }
    }

    public void SetDropHighlight(bool on)
    {
        DropHighlight.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── 芯片 ──

    public void RefreshChips()
    {
        if (_pluginContext != null)
        {
            try { _pluginContext.RaiseChipsChanged(); } catch (Exception ex) { DzTrace.Always("[ZoneBay] plugin chips: " + ex.Message); }
        }
        if (_pluginContent == null) BuildFallbackChips();
        UpdateEmptyHint();
    }

    void BuildFallbackChips()
    {
        var chips = _service.BuildChips(_bar);
        FallbackChips.Children.Clear();
        double size = _settings?.ChipSize ?? ChipSize;
        double gap = _settings?.ChipGap ?? ChipGap;
        double iconSize = _settings?.IconSize ?? 28;
        foreach (var chip in chips)
        {
            var border = new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(Math.Min(10, Math.Max(0, _bar.CornerRadius - 2))),
                Background = _settings?.ChipFill ?? BrushFrom(_bar.ChipFillColor, Colors.Transparent),
                BorderBrush = _settings?.ChipBorder ?? Brushes.Transparent,
                BorderThickness = new Thickness(_settings?.ChipBorderThickness ?? 0),
                Margin = new Thickness(0, 0, gap, 0),
                Cursor = Cursors.Hand,
                ToolTip = chip.Name,
                Tag = chip.ZoneId,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(1, 1)
            };
            if (_bar.IsVertical) border.Margin = new Thickness(0, 0, 0, gap);

            var image = new Image
            {
                Source = chip.Icon,
                Width = iconSize,
                Height = iconSize,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                SnapsToDevicePixels = true
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            border.Child = image;

            border.MouseEnter += (_, _) =>
            {
                border.Background = _settings?.ChipHoverFill ?? border.Background;
                AnimateChip(border, 1.08);
                HoverEnter(chip.ZoneId, border);
            };
            border.MouseLeave += (_, _) =>
            {
                border.Background = _settings?.ChipFill ?? border.Background;
                AnimateChip(border, 1.0);
                HoverLeave(chip.ZoneId);
            };
            // 按下只记录起点；移动超过 6px 才算拖动（拖出恢复 / 换位），否则仍是点击展开。
            border.PreviewMouseLeftButtonDown += (_, e) =>
            {
                if (e.ChangedButton != MouseButton.Left) return;
                _chipDragArmed = true;
                _chipDragMoving = false;
                _chipDragDown = e.GetPosition(this);
            };
            border.MouseMove += (_, e) =>
            {
                if (!_chipDragArmed || e.LeftButton != MouseButtonState.Pressed) return;
                var p = e.GetPosition(this);
                if (Math.Abs(p.X - _chipDragDown.X) <= 6 && Math.Abs(p.Y - _chipDragDown.Y) <= 6) return;
                _chipDragArmed = false;
                _chipDragMoving = true;
                border.CaptureMouse();
                BeginChipDrag(chip.ZoneId, border);
            };
            border.MouseLeftButtonUp += (_, e) =>
            {
                bool dragged = _chipDragMoving;
                _chipDragArmed = false;
                _chipDragMoving = false;
                if (dragged)
                {
                    if (border.IsMouseCaptured) border.ReleaseMouseCapture();
                    EndChipDrag();
                    e.Handled = true;
                    return;
                }
                Toggle(chip.ZoneId, border);
            };
            border.MouseRightButtonUp += (_, e) =>
            {
                e.Handled = true;
                // 芯片右键 = 这个分区自己的菜单（坞主体的右键才是坞菜单）。
                OpenMenu(chip.ZoneId, border);
            };
            FallbackChips.Children.Add(border);
        }
    }

    void UpdateEmptyHint()
    {
        bool empty = _bar.ZoneIds.Count == 0;
        EmptyHint.Visibility = empty && _pluginContent == null && !_bar.IsCollapsed
            ? Visibility.Visible : Visibility.Collapsed;
        EmptyHint.Text = _loc["ZoneBay.Hint.Drag"];
    }

    // ── 插件内容 ──

    void EnsurePluginContent()
    {
        if (_pluginContent != null) return;
        if (_descriptor.CreateContent == null)
        {
            ChipScroll.Visibility = Visibility.Visible;
            PluginContent.Visibility = Visibility.Collapsed;
            return;
        }

        _storage = new JsonPluginStorage(null, _bar.SettingsJson, () =>
        {
            _bar.SettingsJson = _storage?.ToJson() ?? "{}";
            _service.Save();
        });
        _pluginContext = new PluginZoneBayContext
        {
            BarId = _bar.Id.ToString(),
            Bar = new BayHostFacade(this),
            Zones = new ZonesFacade(this),
            Storage = _storage,
            Ui = new PluginUiFacade(null),
            Loc = PluginLocalizer.Instance,
            Log = new PluginLogger(_bar.PluginId),
            OpenSettingsCallback = () => _service.OpenSettings(_bar)
        };
        try
        {
            var content = _descriptor.CreateContent(_pluginContext);
            if (content == null) throw new InvalidOperationException("CreateContent returned null");
            _pluginContent = content;
            PluginContent.Content = content;
            PluginContent.Visibility = Visibility.Visible;
            ChipScroll.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            DzTrace.Always("[ZoneBay] CreateContent failed: " + ex);
            _pluginContent = null;
            PluginContent.Content = null;
            PluginContent.Visibility = Visibility.Collapsed;
            ChipScroll.Visibility = Visibility.Visible;
        }
    }

    void RefreshSettings()
    {
        _settings = BuildSettings();
        try { _pluginContext?.RaiseSettingsChanged(); } catch { }
        if (_pluginContent == null) BuildFallbackChips();
    }

    void SettingsChanged() => RefreshSettings();

    PluginZoneBarSettings BuildSettings()
    {
        var fill = WithOpacity(_bar.ChipFillColor, _bar.ChipOpacity, "#00FFFFFF");
        // 原生快捷栏的图标没有底板；只有用户真的给了芯片底色/边框时才画框。
        bool chipChrome = fill is SolidColorBrush scb && scb.Color.A > 8;
        var hover = chipChrome ? Lighten(fill, 0.16) : Frozen(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
        return new PluginZoneBarSettings
        {
            Edge = _bar.NormalizedEdge,
            AutoHide = _bar.AutoHide,
            ExpandOnHover = _bar.ExpandOnHover,
            ExpandMode = _bar.NormalizedExpandMode,
            EnableRestoreButton = _bar.EnableRestoreButton,
            ChipSize = ChipSize,
            ChipGap = ChipGap,
            IconSize = 30,
            ChipFill = fill,
            ChipHoverFill = hover,
            ChipBorder = chipChrome
                ? BrushFrom(_bar.BorderColor, Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF))
                : Brushes.Transparent,
            ChipBorderThickness = chipChrome ? Math.Max(0, _bar.BorderThickness) : 0,
            ChipCornerRadius = Math.Min(10, Math.Max(0, _bar.CornerRadius - 2)),
            IconFill = string.IsNullOrWhiteSpace(_bar.IconColor)
                ? null
                : BrushFrom(_bar.IconColor, Colors.White)
        };
    }

    static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    static void AnimateChip(Border chip, double scale)
    {
        if (chip.RenderTransform is not ScaleTransform st) return;
        var dur = TimeSpan.FromMilliseconds(140);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(scale, dur) { EasingFunction = ease });
        st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(scale, dur) { EasingFunction = ease });
    }

    // ── 展开 / 收起 ──

    public void Toggle(string zoneId, FrameworkElement? anchor = null)
    {
        if (_expandedZoneId == zoneId) Collapse(zoneId);
        else Expand(zoneId, anchor, permanent: true);
    }

    public void Expand(string zoneId, FrameworkElement? anchor = null)
    {
        Expand(zoneId, anchor, permanent: true);
    }

    public void Expand(string zoneId, FrameworkElement? anchor, bool permanent)
    {
        if (!Guid.TryParse(zoneId, out var id)) return;
        var zone = _service.FindZone(zoneId);
        if (zone == null) return;
        if (_expandedZoneId == zoneId) return;

        Collapse(_expandedZoneId, save: false, animate: false);
        var win = _service.GetOrCreateZoneWindow(zone);
        if (win == null) return;

        bool strip = _bar.NormalizedExpandMode == ZoneBayExpandModes.Strip;
        bool vertical = _bar.IsVertical;
        var anchorDip = anchor != null ? ScreenDip(anchor.PointToScreen(
            new Point(Math.Max(0, anchor.ActualWidth) / 2, Math.Max(0, anchor.ActualHeight) / 2))) : EstimateAnchor(zoneId);
        var rect = ComputeExpansionRect(zone, strip, vertical, anchorDip);

        win.ShowForBay(rect, strip, vertical, _bar.NormalizedEdge);
        _expandedZoneId = zoneId;
        _expandedWindow = win;
        _expandedPermanent = permanent;
        _lastAnchor = anchor;
        _suppressAutoHide = true;
        DesktopLayer.BringToFront(win);
        StartOutsideWatch(permanent);
    }

    public void Collapse(string? zoneId, bool save = false, bool animate = true)
    {
        if (zoneId != null && _expandedZoneId != zoneId) return;
        if (_expandedZoneId == null && _expandedWindow == null) return;
        var win = _expandedWindow;
        try
        {
            if (win != null)
            {
                if (animate) win.BeginBayCloseAnimation(_bar.NormalizedEdge);
                else win.ExitBay();
            }
        }
        catch { }
        _expandedWindow = null;
        _expandedZoneId = null;
        _expandedPermanent = false;
        _suppressAutoHide = false;
        StopOutsideWatch();
        _hideAt = DateTime.UtcNow.AddMilliseconds(600);
        if (save) _service.Save();
    }

    public void CollapseAll() => Collapse(null);

    Rect ComputeExpansionRect(Zone zone, bool strip, bool vertical, Point anchor)
    {
        double w, h;
        if (strip)
        {
            // 与快捷栏同一套：长边 DockPadH=12、短边 DockPadV=6、格间距 DockGap=2，
            // 所以长条正好是“一格高”（GridSize + 12）。
            int n = Math.Max(1, zone.Items.Count);
            double cell = Math.Max(40, zone.GridSize);
            const double padH = 12;
            const double padV = 6;
            const double gap = 2;
            double tabs = zone.MergedGroupMembership.SubZoneIds.Count > 0 ? 24 : 0;
            if (vertical)
            {
                w = cell + padH * 2;
                h = n * cell + Math.Max(0, n - 1) * gap + padV * 2 + tabs;
            }
            else
            {
                w = n * cell + Math.Max(0, n - 1) * gap + padH * 2;
                h = cell + padV * 2 + tabs;
                // 组合分区：上面那层标签栏也得放得下两三个标签名，否则条体只有一格宽时
                // 第二个标签会被直接裁掉（用户点不到）。
                if (zone.MergedGroupMembership.SubZoneIds.Count > 0)
                    w = Math.Max(w, 16 + 76.0 * (zone.MergedGroupMembership.SubZoneIds.Count + 1));
            }
        }
        else
        {
            w = Math.Max(220, zone.Width);
            h = Math.Max(140, zone.Height);
        }

        var wa = WorkArea;
        const double margin = 8;
        double x, y;
        switch (_bar.NormalizedEdge)
        {
            case ZoneBayEdges.Top:
                x = anchor.X - w / 2;
                y = anchor.Y + ExpansionGap;
                break;
            case ZoneBayEdges.Left:
                x = anchor.X + ExpansionGap;
                y = anchor.Y - h / 2;
                break;
            case ZoneBayEdges.Right:
                x = anchor.X - w - ExpansionGap;
                y = anchor.Y - h / 2;
                break;
            default:
                x = anchor.X - w / 2;
                y = anchor.Y - h - ExpansionGap;
                break;
        }

        x = Math.Clamp(x, wa.Left + margin, Math.Max(wa.Left + margin, wa.Right - margin - w));
        y = Math.Clamp(y, wa.Top + margin, Math.Max(wa.Top + margin, wa.Bottom - margin - h));
        return new Rect(x, y, w, h);
    }

    Point EstimateAnchor(string zoneId)
    {
        var chip = FallbackChips.Children.OfType<FrameworkElement>()
            .FirstOrDefault(c => string.Equals(c.Tag as string, zoneId, StringComparison.OrdinalIgnoreCase));
        if (chip != null && chip.ActualWidth > 0)
            return ScreenDip(chip.PointToScreen(new Point(chip.ActualWidth / 2, chip.ActualHeight / 2)));
        if (_lastAnchor != null && _lastAnchor.ActualWidth > 0)
            return ScreenDip(_lastAnchor.PointToScreen(new Point(_lastAnchor.ActualWidth / 2, _lastAnchor.ActualHeight / 2)));

        int index = _bar.ZoneIds.FindIndex(id => string.Equals(id.ToString(), zoneId, StringComparison.OrdinalIgnoreCase));
        if (index < 0) index = 0;
        if (_bar.IsVertical)
            return new Point(Left + Width / 2, Top + 12 + index * (ChipSize + ChipGap) + ChipSize / 2);
        return new Point(Left + 12 + index * (ChipSize + ChipGap) + ChipSize / 2, Top + Height / 2);
    }

    // ── 悬停 ──

    public void HoverEnter(string zoneId, FrameworkElement? anchor = null)
    {
        if (!_bar.ExpandOnHover) return;
        _hoverZoneId = zoneId;
        _hoverAnchor = anchor;
        _hoverLeaveTimer?.Stop();
        _hoverTimer?.Stop();
        _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _hoverTimer.Tick += (_, _) =>
        {
            _hoverTimer?.Stop();
            if (_hoverZoneId == zoneId) Expand(zoneId, _hoverAnchor, permanent: false);
        };
        _hoverTimer.Start();
    }

    public void HoverLeave(string zoneId)
    {
        if (_hoverZoneId == zoneId)
        {
            _hoverTimer?.Stop();
            _hoverZoneId = null;
        }
        if (_expandedZoneId == zoneId && !_expandedPermanent)
        {
            _hoverLeaveTimer?.Stop();
            _hoverLeaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2000) };
            _hoverLeaveTimer.Tick += (_, _) =>
            {
                _hoverLeaveTimer?.Stop();
                if (_expandedZoneId != zoneId || _expandedPermanent) return;
                // 光标已经移进展开窗口：不要收，继续等它离开。
                if (CursorInExpandedWindow()) { _hoverLeaveTimer?.Start(); return; }
                Collapse(zoneId);
            };
            _hoverLeaveTimer.Start();
        }
    }

    bool CursorInExpandedWindow()
    {
        if (_expandedWindow == null) return false;
        if (!NativeMethods.GetCursorPos(out var pt)) return false;
        try
        {
            var q = _expandedWindow.PointFromScreen(new Point(pt.x, pt.y));
            return q.X >= -4 && q.Y >= -4
                   && q.X <= _expandedWindow.ActualWidth + 4
                   && q.Y <= _expandedWindow.ActualHeight + 4;
        }
        catch { return false; }
    }

    void StartOutsideWatch(bool permanent)
    {
        if (!permanent) return;
        _outsideTimer?.Stop();
        _outsideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _outsideTimer.Tick += (_, _) =>
        {
            if (!_expandedPermanent || _expandedZoneId == null) { StopOutsideWatch(); return; }
            if (!NativeMethods.GetCursorPos(out var pt)) return;
            var p = ScreenDip(new Point(pt.x, pt.y));
            bool inBar = p.X >= _shownRect.Left - 4 && p.X <= _shownRect.Right + 4
                         && p.Y >= _shownRect.Top - 4 && p.Y <= _shownRect.Bottom + 4;
            bool inExpanded = false;
            if (_expandedWindow != null)
            {
                try
                {
                    var q = _expandedWindow.PointFromScreen(new Point(pt.x, pt.y));
                    inExpanded = q.X >= -4 && q.Y >= -4
                                 && q.X <= _expandedWindow.ActualWidth + 4
                                 && q.Y <= _expandedWindow.ActualHeight + 4;
                }
                catch { }
            }
            bool down = (NativeMethods.GetAsyncKeyState(0x01) & 0x8000) != 0;
            if (down && !inBar && !inExpanded) Collapse(null);
        };
        _outsideTimer.Start();
    }

    void StopOutsideWatch()
    {
        _outsideTimer?.Stop();
        _outsideTimer = null;
    }

    // ── 菜单 ──

    void BuildMenu()
    {
        _menu = new ContextMenu();
        _menuSettings = AddMenu("ZoneBay.Menu.Settings", (_, _) => _service.OpenSettings(_bar));
        _menuCollapseAll = AddMenu("ZoneBay.Menu.CollapseAll", (_, _) => CollapseAll());
        _menuMinimize = AddMenu("ZoneBay.Menu.Minimize", (_, _) =>
        {
            if (_bar.EnableRestoreButton) _service.CollapseBar(_bar);
            else _service.SetVisible(_bar, false);
        });
        _menuHide = AddMenu("ZoneBay.Menu.Hide", (_, _) => _service.SetVisible(_bar, false));
        _menuManage = AddMenu("ZoneBay.Menu.Manage", (_, _) => _service.OpenManagement(_bar));
        _menu.Items.Add(new Separator());
        _menuDelete = AddMenu("ZoneBay.Menu.Delete", (_, _) => DeleteWithConfirm());
    }

    MenuItem AddMenu(string key, RoutedEventHandler handler)
    {
        var item = new MenuItem { Header = _loc[key] };
        item.Click += handler;
        _menu!.Items.Add(item);
        return item;
    }

    void ApplyLoc()
    {
        if (_menuSettings != null) _menuSettings.Header = _loc["ZoneBay.Menu.Settings"];
        if (_menuCollapseAll != null) _menuCollapseAll.Header = _loc["ZoneBay.Menu.CollapseAll"];
        if (_menuMinimize != null)
            _menuMinimize.Header = _loc[_bar.EnableRestoreButton ? "ZoneBay.Menu.Minimize" : "ZoneBay.Menu.Hide"];
        if (_menuHide != null) _menuHide.Header = _loc["ZoneBay.Menu.Hide"];
        if (_menuManage != null) _menuManage.Header = _loc["ZoneBay.Menu.Manage"];
        if (_menuDelete != null) _menuDelete.Header = _loc["ZoneBay.Menu.Delete"];
        if (EmptyHint != null) EmptyHint.Text = _loc["ZoneBay.Hint.Drag"];
    }

    void DeleteWithConfirm()
    {
        var msg = _loc.Get("ZoneBay.Menu.DeleteConfirm", _bar.Name, _bar.ZoneIds.Count);
        if (MessageBox.Show(msg, _loc["ZoneBay.Menu.Delete"], MessageBoxButton.YesNo, MessageBoxImage.Warning)
            != MessageBoxResult.Yes) return;
        _service.DeleteBar(_bar);
    }

    /// <summary>坞主体右键 → 坞菜单（设置 / 全部收回 / 最小化 / 隐藏 / 管理 / 删除）。
    /// 芯片的右键在芯片自己的处理器里打开分区菜单并 <c>Handled</c>，冒泡到这里已被吃掉。</summary>
    void OnBarRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Right) return;
        if (_collapsed || _menu == null) return;
        _menu.PlacementTarget = Root;
        _menu.Placement = PlacementMode.MousePoint;
        _menu.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>芯片右键（插件 <see cref="IPluginZones.OpenMenu"/> 或宿主兜底条调用）：
    /// 打开**这个分区自己的**右键菜单，而不是坞菜单。</summary>
    public void OpenMenu(string zoneId, FrameworkElement? anchor)
    {
        _lastAnchor = anchor;
        _service.OpenZoneMenu(zoneId, anchor);
    }

    // ── 芯片拖动：拖出恢复 / 坞内换位 ──

    public void BeginChipDrag(string zoneId, FrameworkElement? anchor)
    {
        if (_chipDragZoneId != null) return;
        var zone = _service.FindZone(zoneId);
        if (zone == null) return;

        _chipDragZoneId = zoneId;
        _chipDragFrom = _bar.ZoneIds.FindIndex(id =>
            string.Equals(id.ToString(), zoneId, StringComparison.OrdinalIgnoreCase));
        _suppressAutoHide = true;

        ChipGhostImage.Source = _service.BuildIcon(_bar, zone);
        ChipGhost.PlacementTarget = Root;
        ChipGhost.Placement = PlacementMode.AbsolutePoint;
        UpdateChipGhost();
        ChipGhost.IsOpen = true;

        _chipDragTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _chipDragTimer.Tick -= OnChipDragTick;
        _chipDragTimer.Tick += OnChipDragTick;
        _chipDragTimer.Start();
    }

    void OnChipDragTick(object? sender, EventArgs e) => UpdateChipGhost();

    void UpdateChipGhost()
    {
        if (!NativeMethods.GetCursorPos(out var pt)) return;
        var dip = MonitorHelper.PhysicalToDip(new Point(pt.x, pt.y));
        ChipGhost.HorizontalOffset = dip.X - 20;
        ChipGhost.VerticalOffset = dip.Y - 20;
    }

    public void EndChipDrag()
    {
        if (_chipDragZoneId == null) return;
        _chipDragTimer?.Stop();
        ChipGhost.IsOpen = false;

        var zoneId = _chipDragZoneId;
        int from = _chipDragFrom;
        _chipDragZoneId = null;
        _chipDragFrom = -1;
        _suppressAutoHide = false;
        _hideAt = DateTime.UtcNow.AddMilliseconds(600);

        if (!NativeMethods.GetCursorPos(out var pt)) return;
        var screenDip = MonitorHelper.PhysicalToDip(new Point(pt.x, pt.y));
        var local = new Point(screenDip.X - Left, screenDip.Y - Top);
        bool inside = local.X >= -8 && local.Y >= -8
                      && local.X <= ActualWidth + 8 && local.Y <= ActualHeight + 8;
        if (inside)
        {
            if (from < 0) return;
            int to = ChipIndexAt(local);
            if (to >= 0 && to != from) _service.MoveChip(_bar, from, to);
            return;
        }

        // 拖出坞：在落点恢复成桌面的 36×36 恢复按钮（分区不会丢）。
        _service.ReleaseToDesktop(zoneId, screenDip);
    }

    /// <summary>光标（窗口内 DIP）落在第几颗芯片上。芯片尺寸/间距由宿主统一下发，
    /// 内容区居中，所以按「总宽居中」反算，插件自绘和兜底条都适用。</summary>
    int ChipIndexAt(Point local)
    {
        int count = _bar.ZoneIds.Count;
        if (count == 0) return -1;
        var origin = ContentHost.TranslatePoint(new Point(0, 0), this);
        double step = ChipSize + ChipGap;
        double total = count * ChipSize + Math.Max(0, count - 1) * ChipGap;
        if (_bar.IsVertical)
        {
            double start = origin.Y + (ContentHost.ActualHeight - total) / 2;
            return Math.Clamp((int)Math.Floor((local.Y - start) / step), 0, count - 1);
        }
        double startX = origin.X + (ContentHost.ActualWidth - total) / 2;
        return Math.Clamp((int)Math.Floor((local.X - startX) / step), 0, count - 1);
    }

    void RestoreButton_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _bar.IsCollapsed = false;
        ApplyCollapsed();
        ShowBar();
        _service.Save();
    }

    // ── 命中 / 坐标 ──

    public bool HitTestScreen(Point screenPx)
    {
        if (!IsVisible) return false;
        Point p;
        try { p = PointFromScreen(screenPx); } catch { return false; }
        if (p.X >= -4 && p.Y >= -4 && p.X <= ActualWidth + 4 && p.Y <= ActualHeight + 4) return true;

        if (!_bar.AutoHide) return false;
        var sd = ScreenDip(screenPx);
        return sd.X >= _hiddenRect.Left - 32 && sd.X <= _hiddenRect.Right + 32
               && sd.Y >= _hiddenRect.Top - 32 && sd.Y <= _hiddenRect.Bottom + 32;
    }

    Point ScreenDip(Point screenPx)
    {
        try
        {
            var p = PointFromScreen(screenPx);
            return new Point(Left + p.X, Top + p.Y);
        }
        catch { return screenPx; }
    }

    // ── 外观工具 ──

    static Brush WithOpacity(string? hex, double opacity, string fallback)
    {
        var c = ParseColor(hex, fallback);
        byte a = (byte)Math.Clamp(c.A * Math.Clamp(opacity, 0, 1), 0, 255);
        var brush = new SolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B));
        brush.Freeze();
        return brush;
    }

    static Brush BrushFrom(string? hex, Color fallback)
    {
        var c = ParseColor(hex, null);
        if (c.A == 0 && string.IsNullOrWhiteSpace(hex)) c = fallback;
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        return brush;
    }

    static Color ParseColor(string? hex, string? fallback)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(hex) && ColorConverter.ConvertFromString(hex) is Color c) return c;
            if (!string.IsNullOrWhiteSpace(fallback) && ColorConverter.ConvertFromString(fallback) is Color f) return f;
        }
        catch { }
        return Colors.Transparent;
    }

    static Brush Lighten(Brush brush, double amount)
    {
        if (brush is not SolidColorBrush scb) return brush;
        var c = scb.Color;
        byte r = (byte)Math.Min(255, c.R + 255 * amount);
        byte g = (byte)Math.Min(255, c.G + 255 * amount);
        byte b = (byte)Math.Min(255, c.B + 255 * amount);
        var res = new SolidColorBrush(Color.FromArgb(c.A, r, g, b));
        res.Freeze();
        return res;
    }

    // ── 插件门面 ──

    sealed class BayHostFacade : IPluginZoneBayHost
    {
        readonly ZoneBayWindow _owner;
        public BayHostFacade(ZoneBayWindow owner) => _owner = owner;
        public PluginZoneBarSettings Settings => _owner._settings ?? _owner.BuildSettings();
        public bool IsVertical => _owner._bar.IsVertical;
    }

    sealed class ZonesFacade : IPluginZones
    {
        readonly ZoneBayWindow _owner;
        public ZonesFacade(ZoneBayWindow owner) => _owner = owner;
        public IReadOnlyList<PluginZoneChip> Chips => _owner._service.BuildChips(_owner._bar);
        public void Toggle(string zoneId, FrameworkElement? anchor = null) => _owner.Toggle(zoneId, anchor);
        public void Expand(string zoneId, FrameworkElement? anchor = null) => _owner.Expand(zoneId, anchor);
        public void Collapse(string zoneId) => _owner.Collapse(zoneId);
        public void HoverEnter(string zoneId, FrameworkElement? anchor = null) => _owner.HoverEnter(zoneId, anchor);
        public void HoverLeave(string zoneId) => _owner.HoverLeave(zoneId);
        public void OpenMenu(string zoneId, FrameworkElement anchor) => _owner.OpenMenu(zoneId, anchor);
        public void BeginChipDrag(string zoneId, FrameworkElement? anchor = null) => _owner.BeginChipDrag(zoneId, anchor);
        public void EndChipDrag() => _owner.EndChipDrag();
    }
}
