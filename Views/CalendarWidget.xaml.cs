using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using DesktopZones.Helpers;
using DesktopZones.Models;
using DesktopZones.Services;
using DesktopZones.ViewModels;
using DesktopZones.Views.Components;

namespace DesktopZones.Views;

public partial class CalendarWidget : Window
{
    private bool _restoreDragging;
    private Point _restoreDown;

    private DesktopCalendar _calendar;
    private readonly WidgetService _widgetService;
    private readonly CalendarViewModel _vm;
    private readonly LocalizationService _loc = LocalizationService.Instance;
    private HoverExpandBehavior? _hover;
    private SnapDrag? _snapDrag;
    private SnapResize? _snapResize;
    // ponytail: track generators we subscribed to in SubscribeDayCellStatusChanged so
    // OnClosed can detach without keeping a parallel list. IDisposable pattern: WPF event
    // -= requires the same delegate reference, but DayCellStatus_Changed is a shared
    // method — so we just remember the generator object and detach the method.
    private readonly List<ItemContainerGenerator> _subscribedDayCellGenerators = new();

    public CalendarWidget(DesktopCalendar calendar, WidgetService widgetService)
    {
        InitializeComponent();
        _calendar = calendar;
        _widgetService = widgetService;
        _vm = new CalendarViewModel(calendar);
        DataContext = _vm;

        Left = calendar.X; Top = calendar.Y;
        Opacity = calendar.Opacity;
        MinWidth = 260; MinHeight = 460;

        _vm.DisplayYear = DateTime.Now.Year;
        _vm.DisplayMonth = DateTime.Now.Month;
        _lastStartOnMonday = calendar.StartOnMonday;
        RebuildDisplay();

        ApplyStyle();

        Loaded += OnLoad;
        SizeChanged += OnSizeChanged;
        LocationChanged += (_, _) => { _calendar.X = Left; _calendar.Y = Top; ScheduleSave(); };
        _saveDebounce.Tick += (_, _) => { _saveDebounce.Stop(); if (_savePending) { _savePending = false; _widgetService.Save(); } };
        _langChanged = _ => ApplyLoc();
        _loc.LanguageChanged += _langChanged;
        _widgetService.CalendarsChanged += OnCalendarsChanged;
        // ponytail: subscribe to LockChanged so management UI (or any other source) flipping
        // this widget's lock state immediately syncs the open window.
        _widgetService.LockChanged += OnServiceLockChanged;
        ApplyLoc();
        // ponytail: hover-expand (Task 14d). Wired after InitializeComponent and
        // before any user interaction can occur.
        _hover = new HoverExpandBehavior(this, RestoreButton, MainContent, null,
            () => _calendar.HoverExpandAnimation,
            () => _calendar.HoverExpandSpeed,
            () => _calendar.HoverExpandOrigin,
            () => _calendar.HoverAutoExpand)
        { IsEnabled = _calendar.EnableRestoreButton };
        // ponytail 2026-08-25: pick up live changes from the 动效设置 dialog
        // (property panel) — mirrors ZoneWindow's subscription.
        _calendar.HoverExpandSettingsChanged += OnHoverExpandSettingsChanged;
        // ponytail: ghost-glass fix — see ZoneWindow. Acrylic follows the expand state so a
        // collapsed calendar shows ONLY the RestoreButton (no full-window glass rectangle).
        // ponytail 2026-08-28 边框残影修复 — 与 ZoneWindow 同款:展开时恢复圆角,
        // 收起完成时重断言关闭全部 OS 层装饰(玻璃/圆角/DWM 框架阴影)。
        _hover.Expanded += ReapplyAcrylic;
        _hover.Collapsed += OnHoverCollapsed;
        // ponytail: bug fix — see ZoneWindow ctor. Window.Show() (OpenCalendarWindow /
        // --spawn-widget) bypasses ShowCalendar, so SnapToExpanded never runs.
        if (_calendar.IsVisible) _hover.SnapToExpanded();

        // ponytail: 自适应对齐 — 替换 DragMove 的手动拖拽循环。
        _snapDrag = new SnapDrag(this);
        _snapResize = new SnapResize(this);
    }
    private Action<string>? _langChanged;

    // ponytail: 位置防抖保存 — 拖拽移动后持久化 X/Y（与分区 ZoneWindow 一致）。
    private readonly System.Windows.Threading.DispatcherTimer _saveDebounce = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _savePending;
    void ScheduleSave() { _savePending = true; _saveDebounce.Stop(); _saveDebounce.Start(); }

    void OnHoverExpandSettingsChanged()
    {
        // Re-apply origin + snap baseline for the current kind without forcing
        // a state change (mirrors ZoneWindow.OnHoverExpandSettingsChanged).
        _hover?.SetEnabled(_calendar.EnableRestoreButton);
    }

    // ponytail 2026-08-25: last applied StartOnMonday — OnCalendarsChanged uses
    // it to detect a first-of-week change and rebuild the day grid (the weekday
    // header + cells must re-arrange together).
    private bool _lastStartOnMonday;

    void OnCalendarsChanged()
    {
        if (!IsLoaded) return;
        var latest = _widgetService.Calendars.FirstOrDefault(c => c.Id == _calendar.Id);
        if (latest != null) _calendar = latest;
        // ponytail: ghost-stamp lock — see ZoneWindow.OnZonesChanged for full rationale.
        // 2026-08-23: only stamp when the behavior thinks it is still EXPANDED — during
        // a legitimate animated collapse this used to snap the animation away instantly
        // (see ClockWidget.OnClocksChanged); let the animation finish instead.
        if (!_calendar.IsVisible && _hover != null && _hover.IsExpanded
            && !_hover.IsCollapsePending
            && MainContent.Visibility == Visibility.Visible)
            _hover.SnapToCollapsed();
        // ponytail 2026-08-25: 周一开头 flip changes the whole day-grid layout —
        // rebuild cells + weekday header so the toggle applies immediately
        // instead of waiting for the next month navigation.
        if (_lastStartOnMonday != _calendar.StartOnMonday)
        {
            _lastStartOnMonday = _calendar.StartOnMonday;
            _vm.RebuildCells();
        }
        ApplyWeekdayHeader();
        // ponytail: always sync FillRect, even when hidden — closes the
        // "model blue, screen yellow" desync that ShowCalendar used to reveal.
        SyncFillRect();
        if (MainContent.Visibility == Visibility.Visible)
            ApplyAcrylic();
        ApplyBackgroundImage();
        ApplyStyle();
        // ponytail 2026-08-28: 模型同步路径只在 EnableRestoreButton 真正变化时才
        // SetEnabled；否则每次 UpdateCalendar(收起/展开)都会打断进行中的缩放动画。
        if (_hover != null && _hover.IsEnabled != _calendar.EnableRestoreButton)
            _hover.SetEnabled(_calendar.EnableRestoreButton);
    }

    void OnLoad(object s, RoutedEventArgs e)
    {
        if (_vm?.IsLocked != true) NativeMethods.PinToDesktop(this);
        NativeMethods.SetToolWindow(this);
        NativeMethods.DisableDwmFrameShadow(this);
        ApplyAcrylic();
        ApplyBackgroundImage();
        // ponytail: subscribe to day-cell ItemsControl's StatusChanged so the fixed text
        // colors auto-reapply whenever WPF finishes generating containers (first show, month
        // switch, preset load, etc.). Hook must be after Loaded so the visual tree is up.
        SubscribeDayCellStatusChanged();
        // Set rounded corners LAST after all sizing is complete
        NativeMethods.SetRoundedCorners(this, _calendar.CornerRadius);
        NativeMethods.UpdateRoundedCorners(this, _calendar.CornerRadius);
        ApplyLockState();
        if (!_calendar.IsVisible) ApplyHidden();
    }

    void OnSizeChanged(object s, SizeChangedEventArgs e)
    {
        if (MainContent.Visibility != Visibility.Visible) return;
        _calendar.Width = Width; _calendar.Height = Height;
        NativeMethods.UpdateRoundedCorners(this, _calendar.CornerRadius);
    }

    // ── Acrylic / frosted glass ──

    // ponytail: FillRect sync extracted from ApplyAcrylic so OnCalendarsChanged can
    // refresh it without requiring a valid HWND (AcrylicHelper.* needs HWND).
    // Closes the "model blue, screen yellow" desync window when the widget is hidden.
    void SyncFillRect()
    {
        string fillColorStr = _calendar.FillColor;
        try
        {
            FillRect.Fill = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(fillColorStr)!);
        }
        catch { }
        // ponytail: force re-render — FillRect paints more reliably than
        // Border.Background inside a transparent window.
        FillRect.InvalidateVisual();
        ApplyDefaultTextColors();
    }

    void ApplyToDayCells(System.Windows.Media.Brush brush)
    {
        // ponytail: timing race — after RefreshAppearance's RebuildCells, the
        // ItemContainerGenerator hasn't created containers yet so ContainerFromIndex
        // returns null and the brush is silently dropped. First try synchronously;
        // if no containers exist yet, defer to Loaded priority and retry once more.
        // Subscribes to StatusChanged for ongoing safety (see SubscribeDayCellStatusChanged).
        bool anyApplied = TryApplyToDayCells(brush);
        if (!anyApplied)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!TryApplyToDayCells(brush))
                {
                    Dispatcher.BeginInvoke(new Action(() => TryApplyToDayCells(brush)),
                        System.Windows.Threading.DispatcherPriority.Loaded);
                }
            }), System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    /// <summary>Walk every ItemsControl in MainContent and re-brush its containers. Returns
    /// true if at least one container was found and brushed; false if all ItemsControls were
    /// still in GeneratingContainers state.</summary>
    bool TryApplyToDayCells(System.Windows.Media.Brush brush)
    {
        bool anyApplied = false;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(MainContent); i++)
        {
            var ch = VisualTreeHelper.GetChild(MainContent, i);
            foreach (var ic in EnumerateItemsControls(ch))
            {
                if (ic.ItemsSource == null) continue;
                for (int k = 0; k < ic.Items.Count; k++)
                {
                    if (ic.ItemContainerGenerator.ContainerFromIndex(k) is DependencyObject container)
                    {
                        ApplyBrushRecursive(container, brush);
                        anyApplied = true;
                    }
                }
            }
        }
        // NotesDateLabel + AddNoteBtn live at calendar-widget level — set in
        // ApplyDefaultTextColors (label = content color, button = button color).
        return anyApplied;
    }

    /// <summary>Subscribe to the day-cells ItemsControl's ItemContainerGenerator.StatusChanged
    /// so that whenever WPF finishes generating containers (after RebuildCells, month-switch,
    /// preset load, etc.) we re-apply the fixed text colors automatically. Idempotent — safe to
    /// call from the constructor once the visual tree is up.</summary>
    void SubscribeDayCellStatusChanged()
    {
        foreach (var ic in EnumerateItemsControls(MainContent))
        {
            if (ic.ItemsSource == null) continue;
            ic.ItemContainerGenerator.StatusChanged += DayCellStatus_Changed;
            _subscribedDayCellGenerators.Add(ic.ItemContainerGenerator);
        }
    }

    void UnsubscribeDayCellStatusChanged()
    {
        foreach (var gen in _subscribedDayCellGenerators)
        {
            gen.StatusChanged -= DayCellStatus_Changed;
        }
        _subscribedDayCellGenerators.Clear();
    }

    void DayCellStatus_Changed(object? sender, EventArgs e)
    {
        if (sender is not ItemContainerGenerator gen) return;
        if (gen.Status != System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated) return;
        // Containers ready — re-run the fixed text colors on any cells that were added/regenerated.
        ApplyDefaultTextColors();
    }

    static IEnumerable<ItemsControl> EnumerateItemsControls(DependencyObject parent)
    {
        if (parent is ItemsControl ic) yield return ic;
        int n = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            foreach (var inner in EnumerateItemsControls(child)) yield return inner;
        }
    }

    static void ApplyBrushRecursive(DependencyObject node, System.Windows.Media.Brush brush)
    {
        int count = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            // Content only — buttons are colored separately by 按钮颜色.
            if (child is TextBlock tb) tb.Foreground = brush;
            ApplyBrushRecursive(child, brush);
        }
    }

    /// <summary>Re-apply the fixed foregrounds (day cells use TextColor; chrome uses its
    /// hardcoded defaults).</summary>
    public void RefreshTextColorAdaptive()
    {
        ApplyDefaultTextColors();
    }

    /// <summary>Apply the fixed foregrounds split:
    /// 主体内容颜色 → 月份标题/星期表头/日期格/备注标签/备注内容；按钮颜色 → 所有按钮。</summary>
    void ApplyDefaultTextColors()
    {
        SolidColorBrush content, buttons;
        try { content = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_calendar.TextColor)!); } catch { content = Brushes.White; }
        try { buttons = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_calendar.ButtonColor)!); } catch { buttons = Brushes.White; }

        // Content — 主体内容颜色.
        if (MonthTitleText != null) MonthTitleText.Foreground = content;
        if (NotesDateLabel != null) NotesDateLabel.Foreground = content;
        for (int i = 0; i <= 6; i++)
        {
            var tb = FindName($"Dow{i}") as TextBlock;
            if (tb != null) tb.Foreground = content;
        }
        ApplyToDayCells(content);

        // Buttons — 按钮颜色.
        if (TodayBtn != null) TodayBtn.Foreground = buttons;
        if (HideBtn != null) HideBtn.Foreground = buttons;
        if (LockBtn != null) LockBtn.Foreground = buttons;
        if (PrevMonthBtn != null) PrevMonthBtn.Foreground = buttons;
        if (NextMonthBtn != null) NextMonthBtn.Foreground = buttons;
        if (AddNoteBtn != null) AddNoteBtn.Foreground = buttons;
        ApplyIconVisual(buttons);
    }

    /// <summary>恢复按钮图标 — 独立 IconColor；空则回退按钮颜色，不随系统深浅色。</summary>
    void ApplyIconVisual(Brush fallback)
    {
        var color = !string.IsNullOrEmpty(_calendar.IconColor) ? _calendar.IconColor : _calendar.ButtonColor;
        Brush ic;
        try { ic = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)!); }
        catch { ic = fallback; }
        var icon = string.IsNullOrEmpty(_calendar.IconChar) ? Helpers.IconGlyph.Calendar : _calendar.IconChar;
        Helpers.IconGlyph.Apply(RestoreIconChar, RestoreIconPath, icon, ic, 18);
    }

    void ApplyAcrylic()
    {
        SyncFillRect();

        string borderColorStr = _calendar.BorderColor;
        double borderThickness = _calendar.BorderThickness;

        // ponytail: ghost-glass fix — see ZoneWindow/ClockWidget. A collapsed calendar keeps
        // its full-size window, so enabling blur here would paint the tint across the whole
        // window. Only enable while content is expanded.
        bool expanded = _hover?.IsExpanded ?? false;
        if (_calendar.EnableLiquidGlass && expanded)
        {
            var blurResult = AcrylicHelper.EnableBlur(this, _calendar.GlassBlurAmount, _calendar.GlassTintOpacity,
                _calendar.GlassTintLuminosity, _calendar.GlassColorMode);
            if (!blurResult.Success)
                System.Diagnostics.Debug.WriteLine($"[CalendarWidget] EnableBlur failed: {blurResult.Error}");
            // ponytail: additive liquid-glass overlay — the chromatic border rides a
            // separate overlay Border so it never replaces the user's base CalendarBorder.
            if (CalendarGlassBorder != null)
            {
                CalendarGlassBorder.BorderBrush = AcrylicHelper.CreateChromaticBorder();
                CalendarGlassBorder.BorderThickness = new Thickness(Math.Max(1.0, borderThickness));
                CalendarGlassBorder.CornerRadius = new CornerRadius(_calendar.CornerRadius);
            }
        }
        else
        {
            AcrylicHelper.DisableBlur(this);
            // ponytail: additive overlay — clear the glass border when the effect is off.
            if (CalendarGlassBorder != null)
                CalendarGlassBorder.BorderThickness = new Thickness(0);
        }
    }

    /// <summary>
    /// ponytail 2026-08-28 边框残影修复 — 展开(悬停/点击恢复按钮)时把 Win11 圆角
    /// 偏好一并恢复(收起时 OnHoverCollapsed 关掉了它),再走 ApplyAcrylic 恢复玻璃。
    /// </summary>
    void ReapplyAcrylic()
    {
        NativeMethods.SetRoundedCorners(this, _calendar.CornerRadius);
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

    // ── Background image ──

    void ApplyBackgroundImage()
    {
        try
        {
            if (!string.IsNullOrEmpty(_calendar.BackgroundImagePath) && System.IO.File.Exists(_calendar.BackgroundImagePath))
            {
                var bi = new System.Windows.Media.Imaging.BitmapImage();
                bi.BeginInit();
                bi.UriSource = new Uri(_calendar.BackgroundImagePath);
                bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bi.DecodePixelWidth = 1920;
                bi.EndInit();
                bi.Freeze();
                CalBgImage.Source = bi;
                CalBgImage.Stretch = Stretch.UniformToFill;
                double cw = CalBgBorder.ActualWidth > 0 ? CalBgBorder.ActualWidth : 300;
                double ch = CalBgBorder.ActualHeight > 0 ? CalBgBorder.ActualHeight : 420;

                // UniformToFill — fill target area maintaining aspect ratio
                double imgW = bi.PixelWidth;
                double imgH = bi.PixelHeight;
                double utfScale = Math.Max((cw * _calendar.BgImageZoom) / imgW, (ch * _calendar.BgImageZoom) / imgH);
                double displayedW = imgW * utfScale;
                double displayedH = imgH * utfScale;

                CalBgImage.Width = displayedW;
                CalBgImage.Height = displayedH;

                // Position image: center at container center + offset (matches preview positioning)
                double zoneCenterX = cw / 2;
                double zoneCenterY = ch / 2;
                double imgCenterX = displayedW / 2;
                double imgCenterY = displayedH / 2;
                double ox = _calendar.BgImageOffsetX;
                double oy = _calendar.BgImageOffsetY;

                CalBgImage.Margin = new Thickness(
                    zoneCenterX - imgCenterX + ox,
                    zoneCenterY - imgCenterY + oy, 0, 0);
                CalBgImage.HorizontalAlignment = HorizontalAlignment.Left;
                CalBgImage.VerticalAlignment = VerticalAlignment.Top;
                CalBgImage.Opacity = Math.Max(0.01, _calendar.BackgroundImageOpacity / 100.0);
            }
            else
            {
                CalBgImage.Source = null;
                CalBgImage.Opacity = 0;
            }
        }
        catch { if (CalBgImage != null) { CalBgImage.Source = null; CalBgImage.Opacity = 0; } }
    }

    // ── Style (border / fill) ──

    void ApplyStyle()
    {
        // Always apply user's border color (overrides chromatic border from LiquidGlass if needed)
        try
        {
            CalendarBorder.BorderBrush = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(_calendar.BorderColor)!);
        }
        catch { }
        CalendarBorder.BorderThickness = new Thickness(_calendar.BorderThickness);
        // ponytail: force the Border element itself to re-render. In transparent windows
        // (AllowsTransparency=True), setting BorderBrush on a Border inside a MainContent
        // Border subtree sometimes caches the previous Brush until the Border is told to
        // invalidate directly — InvalidateMeasure+InvalidateVisual clears the cache.
        CalendarBorder.InvalidateMeasure();
        CalendarBorder.InvalidateVisual();

        // ponytail 2026-08-26: 圆角/尖角 switch — corner elements + DWM lockstep.
        int r = _calendar.CornerRadius;
        MainContent.CornerRadius = new CornerRadius(r);
        CalendarBorder.CornerRadius = new CornerRadius(r);
        if (CalendarGlassBorder != null)
            CalendarGlassBorder.CornerRadius = new CornerRadius(r);
        FillRect.RadiusX = FillRect.RadiusY = r;
        // ponytail 2026-08-28: 收起状态下跳过 DWM 圆角 — 设置面板显示开关 →
        // HideCalendar → UpdateCalendar → CalendarsChanged → OnCalendarsChanged →
        // ApplyStyle 这条链会在窗口收起后重新打开整窗大小的圆角描边(边框残影来源)。
        // 展开路径(ShowCalendar / ReapplyAcrylic)会各自恢复。
        bool collapsed = RestoreButton.Visibility == Visibility.Visible
                         || _hover is { IsExpanded: false };
        if (System.Windows.PresentationSource.FromVisual(this) != null && !collapsed)
            NativeMethods.SetRoundedCorners(this, r);

        ApplyQuickBar();
    }

    // ponytail 2026-08-25: 磁贴模式 + 按钮透明度 (日历设置 spec). Zone-style:
    // TileMode collapses the minimize/lock buttons, ControlOpacity drives
    // their opacity.
    void ApplyQuickBar()
    {
        if (LockBtn == null || HideBtn == null) return;
        var vis = _calendar.TileMode ? Visibility.Collapsed : Visibility.Visible;
        LockBtn.Visibility = vis;
        HideBtn.Visibility = vis;
        var op = Math.Max(0.05, _calendar.ControlOpacity / 100.0);
        LockBtn.Opacity = op;
        HideBtn.Opacity = op;
    }

    /// <summary>Refresh all visual styles from the current _calendar model (for live preview).
    /// Accepts an optional <paramref name="calendar"/> to refresh the local reference, mirroring
    /// ZoneWindow.RefreshZone's "KEY FIX" pattern — see ClockWidget.RefreshAppearance for rationale.
    /// <paramref name="rebuildCells"/> defaults true for backwards compat with callers that don't
    /// know about the parameter; pass false for pure cosmetic tweaks (FillColor, BorderColor,
    /// TextColor) to avoid the ItemContainerGenerator race window.</summary>
    public void RefreshAppearance(DesktopCalendar? calendar = null, bool rebuildCells = true)
    {
        if (calendar != null) _calendar = calendar;
        // RebuildCells first: a preset load may change StartOnMonday (Sun-first vs Mon-first
        // arrangement) or Notes (dot indicators). Without this the day grid keeps its
        // previous layout even though _calendar has changed.
        if (rebuildCells)
        {
            _lastStartOnMonday = _calendar.StartOnMonday;
            _vm?.RebuildCells();
            ApplyWeekdayHeader();
        }
        // ponytail: ApplyAcrylic's EnableBlur guards on IntPtr.Zero internally —
        // safe to run regardless of MainContent visibility so live preview reaches
        // the widget even when hidden.
        ApplyAcrylic();
        ApplyBackgroundImage();
        ApplyStyle();
        // ponytail 2026-08-28: 只在开关真正变化时才 SetEnabled，避免外观实时预览
        // 打断正在播放的缩放动画。
        if (_hover != null && _hover.IsEnabled != _calendar.EnableRestoreButton)
            _hover.SetEnabled(_calendar.EnableRestoreButton);
    }

    void ApplyLoc()
    {
        TodayBtn.Content = _loc["Calendar.Today"];
        AddNoteBtn.ToolTip = _loc["Calendar.AddNote"];
        NotesDateLabel.Text = _loc["Common.Notes"];
        // ponytail 2026-08-27: 切语言时刷新右键菜单 — XAML 静态绑定只读一次 i18n,
        // 菜单项 Header 必须手动同步,否则切语言后保留旧键值。
        CtxStartOnMonday.Header = _calendar.StartOnMonday
            ? _loc["Calendar.StartOnMonday"]
            : _loc["Calendar.EndOnSunday"];
        CtxSettings.Header = _loc["Calendar.Settings"];
        CtxMinimize.Header = _loc["Calendar.Minimize"];
        // ponytail 2026-08-27: 切语言时同步刷新 CtxLock。
        CtxLock.Header = _loc[_calendar.IsLocked ? "Common.Unlock" : "Common.Lock"];
        CtxDelete.Header = _loc["Calendar.Delete"];
        ApplyWeekdayHeader();
        MonthTitleText.Text = _loc.Get("Calendar.MonthYear", _vm.DisplayYear, _vm.DisplayMonth);
    }

    // ponytail 2026-08-25: weekday header follows StartOnMonday — Mon-first
    // (Weekday.1..6,0) or Sun-first (Weekday.0..6). Previously the header was
    // hardcoded Mon-first while the cells re-arranged Sun-first, misaligning
    // the whole grid when 周一开头 was off.
    void ApplyWeekdayHeader()
    {
        if (_calendar.StartOnMonday)
        {
            Dow0.Text = _loc["Calendar.Weekday.1"];
            Dow1.Text = _loc["Calendar.Weekday.2"];
            Dow2.Text = _loc["Calendar.Weekday.3"];
            Dow3.Text = _loc["Calendar.Weekday.4"];
            Dow4.Text = _loc["Calendar.Weekday.5"];
            Dow5.Text = _loc["Calendar.Weekday.6"];
            Dow6.Text = _loc["Calendar.Weekday.0"];
        }
        else
        {
            Dow0.Text = _loc["Calendar.Weekday.0"];
            Dow1.Text = _loc["Calendar.Weekday.1"];
            Dow2.Text = _loc["Calendar.Weekday.2"];
            Dow3.Text = _loc["Calendar.Weekday.3"];
            Dow4.Text = _loc["Calendar.Weekday.4"];
            Dow5.Text = _loc["Calendar.Weekday.5"];
            Dow6.Text = _loc["Calendar.Weekday.6"];
        }
    }

    // ponytail 2026-08-27: 已从右键菜单移除 — 保留方法体以防外部旧代码仍引用。
    void ToggleRestore_Click(object s, RoutedEventArgs e)
    {
        _calendar.EnableRestoreButton = !_calendar.EnableRestoreButton;
        if (s is MenuItem mi)
            mi.Header = _calendar.EnableRestoreButton
                ? _loc["Calendar.DisableRestore"]
                : _loc["Calendar.EnableRestore"];
    }

    // ponytail 2026-08-27: 右键点击切换"周一开头"。RebuildCells 已支持 StartOnMonday。
    void StartOnMonday_Click(object s, RoutedEventArgs e)
    {
        _calendar.StartOnMonday = !_calendar.StartOnMonday;
        _vm.RebuildCells();
        if (s is MenuItem mi)
            mi.Header = _calendar.StartOnMonday
                ? _loc["Calendar.StartOnMonday"]
                : _loc["Calendar.EndOnSunday"];
        _widgetService.UpdateCalendar(_calendar);
    }

    // ponytail 2026-08-27: 设置 — 与分区齿轮入口同款 PropertyWindowService 调用。
    void Settings_Click(object s, RoutedEventArgs e)
    {
        PropertyWindowService.OpenOrFocus(_calendar, this);
    }

    // ponytail 2026-08-27: 最小化 = 最小化到任务栏。
    void Minimize_Click(object s, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    void RebuildDisplay()
    {
        _vm.RebuildCells();
        MonthTitleText.Text = _loc.Get("Calendar.MonthYear", _vm.DisplayYear, _vm.DisplayMonth);
    }

    void Window_Drag(object s, MouseButtonEventArgs e)
    {
        if (_vm?.IsLocked == true) return;
        _snapDrag?.Start(e, () =>
        {
            if (_vm?.IsLocked != true) NativeMethods.PinToDesktop(this);
            _calendar.X = Left; _calendar.Y = Top;
        });
    }

    // ponytail: OS routes click normally now (no drill-through).
    void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) { }

    void ResizeGrip_Down(object s, MouseButtonEventArgs e)
    {
        if (s is not Border g || g.Tag is not string tag) return;
        if (_vm?.IsLocked == true) { e.Handled = true; return; }
        bool left = tag == "TL" || tag == "BL";
        bool top = tag == "TL" || tag == "TR";
        _snapResize?.Start(e, left, top, !left, !top, 260, 460);
        e.Handled = true;
    }

    void PrevMonth_Click(object s, RoutedEventArgs e)
    {
        if (_vm.DisplayMonth == 1) { _vm.DisplayMonth = 12; _vm.DisplayYear--; }
        else _vm.DisplayMonth--;
        RebuildDisplay();
    }

    void NextMonth_Click(object s, RoutedEventArgs e)
    {
        if (_vm.DisplayMonth == 12) { _vm.DisplayMonth = 1; _vm.DisplayYear++; }
        else _vm.DisplayMonth++;
        RebuildDisplay();
    }

    void Today_Click(object s, RoutedEventArgs e)
    {
        var now = DateTime.Now;
        _vm.DisplayYear = now.Year;
        _vm.DisplayMonth = now.Month;
        RebuildDisplay();
    }

    void DayCell_Click(object s, MouseButtonEventArgs e)
    {
        if (s is Border b && b.Tag is string dateKey)
        {
            if (!DateTime.TryParse(dateKey, out var dt)) return;
            // ponytail: cross-month click jumps to that month (e.g. row-1 "31" → July view).
            // RebuildDisplay handles title + cell rebuild; SelectDate then targets the day.
            if (dt.Year != _vm.DisplayYear || dt.Month != _vm.DisplayMonth)
            {
                _vm.DisplayYear = dt.Year;
                _vm.DisplayMonth = dt.Month;
                RebuildDisplay();
            }
            _vm.SelectDate(dateKey);
            NotesDateLabel.Text = (_loc["Common.Notes"] + " - ") + dateKey;
        }
        e.Handled = true;
    }

    void AddNote_Click(object s, RoutedEventArgs e)
    {
        var selectedDate = _vm.SelectedDate;
        if (string.IsNullOrEmpty(selectedDate)) return;
        ShowNoteDialog(selectedDate, null);
    }

    void NoteItem_Click(object s, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && s is FrameworkElement fe && fe.DataContext is CalendarNoteViewModel nvm)
        {
            ShowNoteDialog(nvm.Date, nvm);
        }
    }

    void ShowNoteDialog(string dateKey, CalendarNoteViewModel? existingNote)
    {
        bool isEdit = existingNote != null;
        var title = isEdit ? _loc["Calendar.EditNote"] : _loc["Calendar.AddNote"];
        var existingPriority = existingNote?.Priority ?? NotePriority.None;
        var existingContent = existingNote?.Content ?? "";
        var existingReminder = existingNote != null &&
            _calendar.Notes.TryGetValue(existingNote.Date, out var notes) &&
            notes.FirstOrDefault(n => n.Id == existingNote.Id) is { ReminderEnabled: true, ReminderTime: not null } realNote
            ? realNote.ReminderTime : null;

        var noteWindow = new Window
        {
            Title = title,
            Width = 320, Height = 280,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent
        };
        var outerBorder = new System.Windows.Controls.Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x10, 0x11, 0x1A)),
            CornerRadius = new CornerRadius(12),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16)
        };
        // ponytail 2026-08-28: 阴影拆到独立兄弟 Border(ManagementWindow 同款处理) —
        // Effect 挂在含 TextBox 的根 Border 上，光标闪烁也会反复触发整窗 CPU 位图卷积。
        var shadowBorder = new System.Windows.Controls.Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x10, 0x11, 0x1A)),
            CornerRadius = new CornerRadius(12),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 0, Color = Color.FromArgb(0xAA, 0x00, 0x00, 0x00), Opacity = 0.6 }
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });    // 0: title
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });    // 1: divider
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 2: content
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });    // 3: priority
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });    // 4: reminder
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });    // 5: buttons

        // Title
        grid.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 14, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xF0)),
            Margin = new Thickness(0, 0, 0, 8)
        });
        Grid.SetRow(grid.Children[^1], 0);

        // Title/content divider (与液态玻璃二级窗口同款,用本窗口自己的半透明白配色)
        var noteDivider = new Border
        {
            Height = 1,
            Margin = new Thickness(0, 0, 0, 10)
        };
        noteDivider.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "Menu.Separator");
        Grid.SetRow(noteDivider, 1);
        grid.Children.Add(noteDivider);

        // Content textbox
        var textBox = new TextBox
        {
            Text = existingContent,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = new SolidColorBrush(Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xF0)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            FontSize = 13, Padding = new Thickness(8, 6, 8, 6),
            // ponytail 2026-08-27: 自定义右键菜单 — 默认 WPF ContextMenu 的"剪贴/复制/粘贴"
            // 是 PresentationFramework 内置字符串,切语言不变。改用自定义菜单挂 i18n。
            ContextMenu = BuildTextBoxContextMenu()
        };
        Grid.SetRow(textBox, 2);
        grid.Children.Add(textBox);

        // Priority
        var priorityPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        Grid.SetRow(priorityPanel, 3);
        var cmb = ComboBoxHelper.Create(width: 120);
        cmb.Items.Add(_loc["Calendar.Priority.None"]);
        cmb.Items.Add(_loc["Calendar.Priority.Low"]);
        cmb.Items.Add(_loc["Calendar.Priority.Normal"]);
        cmb.Items.Add(_loc["Calendar.Priority.High"]);
        cmb.SelectedIndex = existingPriority switch
        {
            NotePriority.Low => 1,
            NotePriority.Normal => 2,
            NotePriority.High => 3,
            _ => 0
        };
        priorityPanel.Children.Add(new TextBlock
        {
            Text = _loc["Calendar.Priority"],
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0xA0)),
            FontSize = 11, Margin = new Thickness(0, 0, 6, 0)
        });
        priorityPanel.Children.Add(cmb);
        grid.Children.Add(priorityPanel);

        // Reminder row
        var reminderPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        Grid.SetRow(reminderPanel, 4);
        var reminderCheck = new CheckBox
        {
            Content = _loc["Calendar.Reminder"],
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0xA0)),
            FontSize = 11,
            IsChecked = existingReminder.HasValue,
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };
        var dateHint = "yyyy-MM-dd";
        var timeHint = "HH:mm";
        var reminderDateBox = new TextBox
        {
            Width = 85, Height = 24, FontSize = 11,
            Text = existingReminder.HasValue
                ? FuzzyDateTimeParser.FormatDate(existingReminder.Value)
                : dateHint,
            IsEnabled = existingReminder.HasValue,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xF0)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1)
        };
        var reminderTimeBox = new TextBox
        {
            Width = 50, Height = 24, FontSize = 11,
            Text = existingReminder.HasValue
                ? FuzzyDateTimeParser.FormatTime(existingReminder.Value.TimeOfDay)
                : timeHint,
            IsEnabled = existingReminder.HasValue,
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
            Background = new SolidColorBrush(Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xF0)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1)
        };

        // Watermark behavior for date box
        var hintBrush = new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x80));
        var textBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xF0));
        reminderDateBox.Foreground = hintBrush;
        reminderDateBox.GotFocus += (_, _) =>
        {
            if (reminderDateBox.Text == dateHint) { reminderDateBox.Text = ""; reminderDateBox.Foreground = textBrush; }
        };
        reminderDateBox.LostFocus += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(reminderDateBox.Text)) { reminderDateBox.Text = dateHint; reminderDateBox.Foreground = hintBrush; }
        };

        // Watermark behavior for time box
        reminderTimeBox.Foreground = hintBrush;
        reminderTimeBox.GotFocus += (_, _) =>
        {
            if (reminderTimeBox.Text == timeHint) { reminderTimeBox.Text = ""; reminderTimeBox.Foreground = textBrush; }
        };
        reminderTimeBox.LostFocus += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(reminderTimeBox.Text)) { reminderTimeBox.Text = timeHint; reminderTimeBox.Foreground = hintBrush; }
        };
        reminderCheck.Checked += (_, _) => { reminderDateBox.IsEnabled = true; reminderTimeBox.IsEnabled = true; };
        reminderCheck.Unchecked += (_, _) => { reminderDateBox.IsEnabled = false; reminderTimeBox.IsEnabled = false; };
        reminderPanel.Children.Add(reminderCheck);
        reminderPanel.Children.Add(reminderDateBox);
        reminderPanel.Children.Add(reminderTimeBox);
        grid.Children.Add(reminderPanel);

        // Buttons
        var btnPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        Grid.SetRow(btnPanel, 5);
        var cancelBtn = new Button
        {
            Content = _loc["Settings.Cancel"], Width = 70, Height = 30,
            Background = new SolidColorBrush(Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xF0)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1), FontSize = 12, Cursor = Cursors.Hand,
            Margin = new Thickness(0, 0, 8, 0)
        };
        var okBtn = new Button
        {
            Content = _loc["Rename.Ok"], Width = 70, Height = 30,
            Background = new SolidColorBrush(Color.FromRgb(0x7C, 0x3A, 0xED)),
            Foreground = System.Windows.Media.Brushes.White,
            BorderThickness = new Thickness(0), FontSize = 12,
            FontWeight = FontWeights.SemiBold, Cursor = Cursors.Hand
        };
        btnPanel.Children.Add(cancelBtn);
        btnPanel.Children.Add(okBtn);
        grid.Children.Add(btnPanel);

        outerBorder.Child = grid;
        noteWindow.Content = new Grid { Children = { shadowBorder, outerBorder } };

        okBtn.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(textBox.Text)) return;
            var priority = cmb.SelectedIndex switch
            {
                1 => NotePriority.Low,
                2 => NotePriority.Normal,
                3 => NotePriority.High,
                _ => NotePriority.None
            };
            bool reminderEnabled = reminderCheck.IsChecked == true;
            DateTime? reminderTime = null;
            string? parsedDate = null;
            if (reminderEnabled)
            {
                var dateVal = FuzzyDateTimeParser.ParseDate(reminderDateBox.Text);
                var tsVal = FuzzyDateTimeParser.ParseTime(reminderTimeBox.Text);
                if (dateVal.HasValue)
                {
                    reminderTime = dateVal.Value + (tsVal ?? TimeSpan.FromHours(9));
                    parsedDate = FuzzyDateTimeParser.FormatDate(dateVal.Value);
                }
            }

            if (isEdit)
            {
                // If reminder date changed, use that as the new note date
                string? newDate = parsedDate;
                _vm.UpdateNote(existingNote!, textBox.Text.Trim(), priority, reminderEnabled, reminderTime, newDate);
            }
            else
            {
                _vm.AddNote(dateKey, textBox.Text.Trim(), priority, reminderEnabled, reminderTime);
            }
            _vm.RebuildCells();
            _widgetService.UpdateCalendar(_calendar);
            noteWindow.Close();
        };
        cancelBtn.Click += (_, _) => noteWindow.Close();
        noteWindow.ShowDialog();
    }

    void NoteCheck_Click(object s, MouseButtonEventArgs e)
    {
        if (s is Border b && b.DataContext is CalendarNoteViewModel nvm)
        {
            _vm.ToggleNoteComplete(nvm);
            _vm.RebuildCells(); // Recalculate dot indicators
            _widgetService.UpdateCalendar(_calendar);
        }
    }

    void DeleteNote_Click(object s, RoutedEventArgs e)
    {
        if (s is Button btn && btn.Tag is CalendarNoteViewModel nvm)
        {
            _vm.DeleteNote(nvm);
            _vm.RebuildCells();
            _widgetService.UpdateCalendar(_calendar);
        }
    }

    void HideBtn_Click(object s, RoutedEventArgs e)
    {
        HideCalendar();
    }

    void LockBtn_Click(object s, RoutedEventArgs e)
    {
        if (_vm == null) return;
        // ponytail: sync from model first — guards against double-click no-op when model and
        // view have drifted (e.g. management card toggled lock state, event arrived out of order).
        _vm.IsLocked = _calendar.IsLocked;
        _vm.IsLocked = !_vm.IsLocked;
        ApplyLockState();
        _widgetService?.SetLocked(_calendar.Id.ToString(), _vm.IsLocked);
        _widgetService?.Save();
    }

    void OnServiceLockChanged(string id, bool locked)
    {
        if (id != _calendar.Id.ToString()) return;
        if (_vm.IsLocked == locked) return;
        _vm.IsLocked = locked;
        ApplyLockState();
    }

    void ApplyLockState()
    {
        if (_vm == null) return;
        LockBtn.Content = _vm.IsLocked ? "🔒" : "🔓";
        var gripVis = _vm.IsLocked ? Visibility.Collapsed : Visibility.Visible;
        if (GripTL != null) GripTL.Visibility = gripVis;
        if (GripTR != null) GripTR.Visibility = gripVis;
        if (GripBL != null) GripBL.Visibility = gripVis;
        if (GripBR != null) GripBR.Visibility = gripVis;
        // ponytail 2026-08-27: 锁定态变化时同步右键菜单 Header(吸取教训)。
        CtxLock.Header = _vm.IsLocked ? _loc["Common.Unlock"] : _loc["Common.Lock"];
        // ponytail: guard with IsVisible — OnClosed → UpdateCalendar → CalendarsChanged
        // re-enters this handler during teardown, and PinBelowProgman → EnsureHandle
        // would throw "关闭窗口后，无法设置可见性…".
        if (_vm.IsLocked && IsVisible) NativeMethods.PinBelowProgman(this);
    }

    // ponytail 2026-08-27: 调用 helper 替换默认 TextBox 右键菜单 — 默认 WPF
    // ContextMenu 是 PresentationFramework 内置字符串,切语言不变。
    System.Windows.Controls.ContextMenu BuildTextBoxContextMenu()
        => TextBoxContextMenuBuilder.Build(_loc);

    void DeleteCalendar_Click(object s, RoutedEventArgs e)
    {
        // ponytail 2026-08-27: 二级确认 — 删除日历不可恢复。
        var title = $"{_calendar.DisplayYear}-{_calendar.DisplayMonth:D2}";
        var confirm = string.Format(_loc["Calendar.DeleteConfirm"], title);
        if (MessageBox.Show(confirm, _loc["Calendar.DeleteTitle"], MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        _widgetService.DeleteCalendar(_calendar.Id);
        Close();
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

    public void ShowCalendar(bool skipResync = false, double waveDelayMs = 0)
    {
#if DEBUG
        DzTrace.Log($"[CalendarWidget] ShowCalendar(skip={skipResync}, wave={waveDelayMs}) ENTRY winVisible={IsVisible} content={MainContent.Visibility} btn={RestoreButton.Visibility} hoverExpanded={_hover?.IsExpanded} modelVisible={_calendar.IsVisible} size={Width}x{Height}");
#endif
        if (!IsVisible) Show();
        // ponytail: skipResync=true when called from the property window (was the style dialog).
        // Skip BOTH ApplyAcrylic() and UpdateCalendar(_calendar):
        //   - ApplyAcrylic would read model and write FillRect directly.
        //   - UpdateCalendar would fire CalendarsChanged → OnCalendarsChanged → SyncFillRect → same result.
        // Without this, the FillRect "snaps" to model the moment the property window opens, even
        // though the user hasn't touched anything.
        Left = _calendar.X; Top = _calendar.Y;
        if (waveDelayMs > 0)
        {
            // ponytail: batch "Show All" wave — start collapsed and play the calendar's
            // own configured animation at its stagger slot (see ZoneWindow.ShowZone).
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
        MinWidth = 260; MinHeight = 460;
        Width = _calendar.Width > 260 ? _calendar.Width : 320;
        Height = _calendar.Height > 340 ? _calendar.Height : 440;
        if (_vm?.IsLocked != true) NativeMethods.PinToDesktop(this);
        NativeMethods.SetRoundedCorners(this, 10);
        if (_vm?.IsLocked != true) Topmost = true;
        // ponytail: 2026-08-23 — persist LAST so a failure in the model/event path can
        // no longer abort the visual expansion (see ShowClock for the full rationale).
        if (!skipResync)
        {
            _calendar.IsVisible = true;
            _widgetService.UpdateCalendar(_calendar);
        }
        else
        {
            _calendar.IsVisible = true;
        }
        System.Diagnostics.Debug.WriteLine(
            $"[ShowCalendar] done: winVisible={IsVisible} content={MainContent.Visibility} restore={RestoreButton.Visibility}");
        Activate();
    }

    /// <summary>
    /// Batch-wave entrance for a freshly created window: collapse the just-shown
    /// content and play the calendar's own expand animation at the stagger slot.
    /// </summary>
    public void PlayEntranceAnimation(double waveDelayMs)
    {
        if (waveDelayMs <= 0) return;
        _hover?.SnapToCollapsed();
        RestoreButton.Visibility = Visibility.Collapsed;
        _hover?.ShowAfterDelay(waveDelayMs);
    }

    public void HideCalendar(double waveDelayMs = 0)
    {
#if DEBUG
        DzTrace.Log($"[CalendarWidget] HideCalendar(wave={waveDelayMs}) ENTRY winVisible={IsVisible} content={MainContent.Visibility} btn={RestoreButton.Visibility} hoverExpanded={_hover?.IsExpanded} modelVisible={_calendar.IsVisible} restoreEnabled={_calendar.EnableRestoreButton} size={Width}x{Height}");
#endif
        _calendar.X = Left; _calendar.Y = Top; _calendar.Width = Width; _calendar.Height = Height;
        NativeMethods.DisableRoundedCorners(this);
        if (!_calendar.EnableRestoreButton)
        {
            if (waveDelayMs > 0)
            {
                // ponytail: batch "Minimize All" wave — play the calendar's own collapse
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
                // ponytail: 2026-08-23 — SnapToFullHidden resets the hover state so no
                // later ApplyAcrylic call can re-enable the DWM glass on the hidden
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
            // ponytail: minimized — let HoverExpandBehavior handle visibility/scale
            AcrylicHelper.DisableBlur(this);
            if (_vm?.IsLocked != true) NativeMethods.PinToDesktop(this);
            if (waveDelayMs > 0)
                _hover?.CollapseAfterDelay(waveDelayMs, null);
            else
                _hover?.CollapseAnimated();
        }
        _calendar.IsVisible = false;
        _widgetService.UpdateCalendar(_calendar);
#if DEBUG
        DzTrace.Log($"[CalendarWidget] HideCalendar DONE winVisible={IsVisible} content={MainContent.Visibility} btn={RestoreButton.Visibility} hoverExpanded={_hover?.IsExpanded} size={Width}x{Height}");
#endif
    }

    void ApplyHidden()
    {
        AcrylicHelper.DisableBlur(this);
        NativeMethods.DisableRoundedCorners(this);
        if (!_calendar.EnableRestoreButton)
        {
            // ponytail: 2026-08-23 — see HideCalendar for the SnapToFullHidden rationale.
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
            // size, matching ShowCalendar's sizing).
            MinWidth = 260; MinHeight = 460;
            Width = _calendar.Width > 260 ? _calendar.Width : 320;
            Height = _calendar.Height > 340 ? _calendar.Height : 440;
            // ponytail: minimized — window stays at full size, content collapses
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
                _calendar.X = Left; _calendar.Y = Top;
            });
        }
    }

    void Restore_MouseUp(object s, MouseButtonEventArgs e)
    {
        RestoreButton.ReleaseMouseCapture();
        if (!_restoreDragging)
        {
            // ponytail: 2026-08-23 — flip the model to visible BEFORE UpdateCalendar fires
            // CalendarsChanged; see ClockWidget.Restore_MouseUp for the ghost-stamp
            // rationale ("button in the middle + liquid glass around" after expand).
            _calendar.IsVisible = true;
            _hover?.ExpandAnimated(permanent: true);
            _widgetService.UpdateCalendar(_calendar);
        }
    }

    void Restore_Enter(object s, MouseEventArgs e) { RestoreButton.SetResourceReference(Border.BackgroundProperty, "Menu.Bg.Hover"); }
    void Restore_Leave(object s, MouseEventArgs e) { RestoreButton.SetResourceReference(Border.BackgroundProperty, "Menu.Bg.Surface"); }

    protected override void OnClosed(EventArgs e)
    {
        UnsubscribeDayCellStatusChanged();
        // ponytail: 关闭前落盘未保存的位置/尺寸。
        _saveDebounce.Stop();
        if (_savePending) { _savePending = false; _widgetService.Save(); }
        if (_langChanged != null) _loc.LanguageChanged -= _langChanged;
        _langChanged = null;
        _widgetService.LockChanged -= OnServiceLockChanged;
        // ponytail: unsubscribe BEFORE UpdateCalendar so this closing window doesn't
        // re-enter its own CalendarsChanged handler while WmDestroy tears it down
        // (re-entrant ApplyLockState → PinBelowProgman → EnsureHandle crash on exit).
        _widgetService.CalendarsChanged -= OnCalendarsChanged;
        _calendar.HoverExpandSettingsChanged -= OnHoverExpandSettingsChanged;
        _snapDrag?.Detach();
        _snapResize?.Detach();
        _widgetService.UpdateCalendar(_calendar);
        _hover?.Dispose();
        base.OnClosed(e);
    }
}
