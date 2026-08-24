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
    const uint WM_NCLBUTTONDOWN = 0x00A1;
    const int HTTOPLEFT = 13, HTTOPRIGHT = 14, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
    private bool _restoreDragging;
    private Point _restoreDown;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    private DesktopCalendar _calendar;
    private readonly WidgetService _widgetService;
    private readonly CalendarViewModel _vm;
    private readonly LocalizationService _loc = LocalizationService.Instance;
    private HoverExpandBehavior? _hover;
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
        LocationChanged += (_, _) => { _calendar.X = Left; _calendar.Y = Top; };
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
        _hover.Expanded += ApplyAcrylic;
        _hover.Collapsed += () => AcrylicHelper.DisableBlur(this);
        // ponytail: bug fix — see ZoneWindow ctor. Window.Show() (OpenCalendarWindow /
        // --spawn-widget) bypasses ShowCalendar, so SnapToExpanded never runs.
        if (_calendar.IsVisible) _hover.SnapToExpanded();
    }
    private Action<string>? _langChanged;

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
        _hover?.SetEnabled(_calendar.EnableRestoreButton);
    }

    void OnLoad(object s, RoutedEventArgs e)
    {
        if (_vm?.IsLocked != true) NativeMethods.PinToDesktop(this);
        NativeMethods.SetToolWindow(this);
        ApplyAcrylic();
        ApplyBackgroundImage();
        // ponytail: subscribe to day-cell ItemsControl's StatusChanged so adaptive text
        // auto-recolors whenever WPF finishes generating containers (first show, month
        // switch, preset load, etc.). Hook must be after Loaded so the visual tree is up.
        SubscribeDayCellStatusChanged();
        // Set rounded corners LAST after all sizing is complete
        NativeMethods.SetRoundedCorners(this, 10);
        NativeMethods.UpdateRoundedCorners(this, 10);
        ApplyLockState();
        if (!_calendar.IsVisible) ApplyHidden();
    }

    void OnSizeChanged(object s, SizeChangedEventArgs e)
    {
        if (MainContent.Visibility != Visibility.Visible) return;
        _calendar.Width = Width; _calendar.Height = Height;
        NativeMethods.UpdateRoundedCorners(this, 10);
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
        ApplyBodyTextColorAdaptive(fillColorStr);
    }

    /// <summary>Adaptive text/icon color based on the widget's effective fill. When the
    /// calendar has a background image, samples 5 points from it instead of using FillColor.</summary>
    void ApplyBodyTextColorAdaptive(string effectiveFill)
    {
#if DEBUG
        System.Diagnostics.Debug.WriteLine(
            $"[adaptive] CalendarWidget: bg={effectiveFill} adaptive={_calendar.TextColorAdaptive}");
#endif
        if (!_calendar.TextColorAdaptive) return;
        SolidColorBrush brush;
        if (CalBgImage?.Source is BitmapSource bmp && !string.IsNullOrEmpty(_calendar.BackgroundImagePath))
        {
            brush = AdaptiveTextColor.ResolveBrush(AdaptiveTextColor.ResolveTextColorForImage(bmp));
        }
        else
        {
            brush = AdaptiveTextColor.ResolveBrush(effectiveFill);
        }
        // ponytail: month nav arrows + LockBtn + NotesDateLabel now ride the same adaptive
        // brush as HideBtn (calendar has no separate title-bar adaptive, body toggle covers all)
        if (LockBtn != null) LockBtn.Foreground = brush;
        if (PrevMonthBtn != null) PrevMonthBtn.Foreground = brush;
        if (NextMonthBtn != null) NextMonthBtn.Foreground = brush;
        if (NotesDateLabel != null) NotesDateLabel.Foreground = brush;
        if (MonthTitleText != null) MonthTitleText.Foreground = brush;
        if (TodayBtn != null) TodayBtn.Foreground = brush;
        if (HideBtn != null) HideBtn.Foreground = brush;
        if (RestoreIconChar != null) RestoreIconChar.Foreground = brush;
        // DOW headers
        for (int i = 0; i <= 6; i++)
        {
            var tb = FindName($"Dow{i}") as TextBlock;
            if (tb != null) tb.Foreground = brush;
        }
        // Day cells (dynamic items inside ItemsControl)
        ApplyToDayCells(brush);
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
        // NotesDateLabel + AddNoteBtn live at calendar-widget level — set directly.
        if (NotesDateLabel != null) NotesDateLabel.Foreground = brush;
        if (AddNoteBtn != null) AddNoteBtn.Foreground = brush;
        return anyApplied;
    }

    /// <summary>Subscribe to the day-cells ItemsControl's ItemContainerGenerator.StatusChanged
    /// so that whenever WPF finishes generating containers (after RebuildCells, month-switch,
    /// preset load, etc.) we re-apply the adaptive brush automatically. Idempotent — safe to
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
        // Containers ready — re-run adaptive to color any cells that were added/regenerated.
        string fillColorStr = _calendar.FillColor;
        ApplyBodyTextColorAdaptive(fillColorStr);
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
            if (child is TextBlock tb) tb.Foreground = brush;
            else if (child is Control c) c.Foreground = brush;
            ApplyBrushRecursive(child, brush);
        }
    }

    /// <summary>Re-apply body text adaptive using the current model+config. Call when the
    /// adaptive toggle changes (e.g. settings dialog live preview).</summary>
    public void RefreshTextColorAdaptive()
    {
        string fillColorStr = _calendar.FillColor;
        if (_calendar.TextColorAdaptive) ApplyBodyTextColorAdaptive(fillColorStr);
        else ApplyDefaultTextColors();
    }

    /// <summary>Restore hard-coded / user-configured foregrounds when adaptive is off.</summary>
    void ApplyDefaultTextColors()
    {
        if (MonthTitleText != null) MonthTitleText.Foreground = new SolidColorBrush(Color.FromArgb(0xEE, 0xFF, 0xFF, 0xFF));
        if (TodayBtn != null) TodayBtn.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#6C63FF")!);
        if (HideBtn != null) HideBtn.Foreground = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
        // ponytail: LockBtn + month nav + NotesDateLabel — restore XAML defaults when adaptive is off
        if (LockBtn != null) LockBtn.Foreground = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
        if (PrevMonthBtn != null) PrevMonthBtn.Foreground = new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF));
        if (NextMonthBtn != null) NextMonthBtn.Foreground = new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF));
        // NotesDateLabel XAML default is "#AAA0C0" — 3-char hex = RGB-only, alpha=0xFF
        if (NotesDateLabel != null) NotesDateLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xA0, 0xC0));
        if (RestoreIconChar != null) RestoreIconChar.Foreground = new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF));
        for (int i = 0; i <= 6; i++)
        {
            var tb = FindName($"Dow{i}") as TextBlock;
            if (tb != null) tb.Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x80, 0x80, 0xA0));
        }
        ApplyToDayCells(new SolidColorBrush((Color)ColorConverter.ConvertFromString(_calendar.TextColor)!));
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
        if (_calendar.EnableAcrylic && expanded)
        {
            var blurResult = AcrylicHelper.EnableBlur(this, _calendar.GlassBlurAmount, _calendar.GlassTintOpacity,
                _calendar.GlassTintLuminosity, _calendar.GlassColorMode);
            if (!blurResult.Success)
                System.Diagnostics.Debug.WriteLine($"[CalendarWidget] EnableBlur failed: {blurResult.Error}");
            // ponytail 2026-08-25: liquid-glass chromatic border branch — mirrors
            // ClockWidget.ApplyAcrylic (the only component that had it).
            if (_calendar.EnableLiquidGlass)
            {
                CalendarBorder.BorderBrush = AcrylicHelper.CreateChromaticBorder();
                CalendarBorder.BorderThickness = new Thickness(Math.Max(1.0, borderThickness));
            }
        }
        else
        {
            AcrylicHelper.DisableBlur(this);
        }
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

        ApplyQuickBar();
    }

    // ponytail 2026-08-25: 极简模式 + 按钮透明度 (日历设置 spec). Zone-style:
    // QuickBarMode collapses the minimize/lock buttons, ControlOpacity drives
    // their opacity.
    void ApplyQuickBar()
    {
        if (LockBtn == null || HideBtn == null) return;
        var vis = _calendar.QuickBarMode ? Visibility.Collapsed : Visibility.Visible;
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
    /// TextColorAdaptive toggle) to avoid the ItemContainerGenerator race window.</summary>
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
        _hover?.SetEnabled(_calendar.EnableRestoreButton);
    }

    void ApplyLoc()
    {
        TodayBtn.Content = _loc["Calendar.Today"];
        AddNoteBtn.ToolTip = _loc["Calendar.AddNote"];
        NotesDateLabel.Text = _loc["Common.Notes"];
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

    void ToggleRestore_Click(object s, RoutedEventArgs e)
    {
        _calendar.EnableRestoreButton = !_calendar.EnableRestoreButton;
        var cn = _loc.CurrentLanguage == "zh";
        if (s is MenuItem mi)
            mi.Header = _calendar.EnableRestoreButton
                ? (cn ? "关闭恢复按钮" : "Disable Restore")
                : (cn ? "启用恢复按钮" : "Enable Restore");
    }

    void RebuildDisplay()
    {
        _vm.RebuildCells();
        MonthTitleText.Text = _loc.CurrentLanguage == "zh"
            ? $"{_vm.DisplayYear}年{_vm.DisplayMonth}月"
            : $"{_vm.DisplayMonth}/{_vm.DisplayYear}";
    }

    void Window_Drag(object s, MouseButtonEventArgs e)
    {
        if (_vm?.IsLocked == true) return;
        try { DragMove(); if (_vm?.IsLocked != true) NativeMethods.PinToDesktop(this); } catch { }
    }

    // ponytail: OS routes click normally now (no drill-through).
    void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) { }

    void ResizeGrip_Down(object s, MouseButtonEventArgs e)
    {
        if (s is not Border g || g.Tag is not string tag) return;
        if (_vm?.IsLocked == true) { e.Handled = true; return; }
        int d = tag switch
        {
            "TL" => HTTOPLEFT,
            "TR" => HTTOPRIGHT,
            "BL" => HTBOTTOMLEFT,
            _ => HTBOTTOMRIGHT
        };
        SendMessage(new WindowInteropHelper(this).Handle, WM_NCLBUTTONDOWN, (IntPtr)d, IntPtr.Zero);
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
        var cn = _loc.CurrentLanguage == "zh";
        bool isEdit = existingNote != null;
        var title = isEdit ? (cn ? "编辑备注" : "Edit Note") : _loc["Calendar.AddNote"];
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
        outerBorder.Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 0, Color = Color.FromArgb(0xAA, 0x00, 0x00, 0x00), Opacity = 0.6 };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });    // 0: title
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 1: content
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });    // 2: priority
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });    // 3: reminder
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });    // 4: buttons

        // Title
        grid.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 14, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xF0)),
            Margin = new Thickness(0, 0, 0, 8)
        });
        Grid.SetRow(grid.Children[^1], 0);

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
            FontSize = 13, Padding = new Thickness(8, 6, 8, 6)
        };
        Grid.SetRow(textBox, 1);
        grid.Children.Add(textBox);

        // Priority
        var priorityPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        Grid.SetRow(priorityPanel, 2);
        var cmb = ComboBoxHelper.Create(width: 120);
        cmb.Items.Add(cn ? "无" : "None");
        cmb.Items.Add(cn ? "低" : "Low");
        cmb.Items.Add(cn ? "中" : "Normal");
        cmb.Items.Add(cn ? "高" : "High");
        cmb.SelectedIndex = existingPriority switch
        {
            NotePriority.Low => 1,
            NotePriority.Normal => 2,
            NotePriority.High => 3,
            _ => 0
        };
        priorityPanel.Children.Add(new TextBlock
        {
            Text = cn ? "优先级:" : "Priority:",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0xA0)),
            FontSize = 11, Margin = new Thickness(0, 0, 6, 0)
        });
        priorityPanel.Children.Add(cmb);
        grid.Children.Add(priorityPanel);

        // Reminder row
        var reminderPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        Grid.SetRow(reminderPanel, 3);
        var reminderCheck = new CheckBox
        {
            Content = cn ? "提醒" : "Reminder",
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
        Grid.SetRow(btnPanel, 4);
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
        noteWindow.Content = outerBorder;

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
        if (_vm.IsLocked) NativeMethods.PinBelowProgman(this);
    }

    void DeleteCalendar_Click(object s, RoutedEventArgs e)
    {
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
        if (!_vm.IsLocked) Topmost = true;
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
            try { DragMove(); } catch { }
            _calendar.X = Left; _calendar.Y = Top;
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

    void Restore_Enter(object s, MouseEventArgs e) { RestoreButton.Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x2A, 0x2A, 0x4E)); }
    void Restore_Leave(object s, MouseEventArgs e) { RestoreButton.Background = new SolidColorBrush(Color.FromArgb(0xDD, 0x1A, 0x1A, 0x2E)); }

    protected override void OnClosed(EventArgs e)
    {
        UnsubscribeDayCellStatusChanged();
        if (_langChanged != null) _loc.LanguageChanged -= _langChanged;
        _langChanged = null;
        _widgetService.LockChanged -= OnServiceLockChanged;
        _calendar.HoverExpandSettingsChanged -= OnHoverExpandSettingsChanged;
        _widgetService.UpdateCalendar(_calendar);
        _hover?.Dispose();
        base.OnClosed(e);
    }
}
