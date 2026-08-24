using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using DesktopZones.Helpers;
using DesktopZones.Models;
using DesktopZones.Services;
using DesktopZones.ViewModels;
using DesktopZones.Views.Components;
using Microsoft.Win32;

namespace DesktopZones.Views;

public partial class ZoneWindow : Window
{
    // Resize
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    const uint WM_NCLBUTTONDOWN = 0x00A1;
    const int HTTOPLEFT = 13, HTTOPRIGHT = 14, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;

    // Sub-zone tab drag (browser-like, mirrors PropertyTabStrip's Win32 polling loop)
    [DllImport("user32.dll")] static extern bool GetCursorPos(out Win32Point lpPoint);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
    [StructLayout(LayoutKind.Sequential)] struct Win32Point { public int X; public int Y; }
    const int VK_LBUTTON = 0x01;

    private Zone _zone;
    private readonly ZoneManager _mgr;

    /// <summary>XAML default foreground of the ControlPoint button labels (LockBtnText /
    /// EditBtnText / ImportBtnText / HideBtnText). Used to restore them when title-bar
    /// adaptive is turned back off.</summary>
    private static readonly SolidColorBrush CtrlLabelDefaultBrush = new(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));
    // ponytail: frozen hover brushes — same color on every mouse-over, no need to
    // reallocate. Per-class so each Window can Freeze independently (freeze is thread-safe).
    private static readonly SolidColorBrush RestoreHoverBrush = Freeze(new(Color.FromArgb(0xFF, 0x2A, 0x2A, 0x4E)));
    private static readonly SolidColorBrush RestoreIdleBrush  = Freeze(new(Color.FromArgb(0xDD, 0x1A, 0x1A, 0x2E)));
    private static readonly SolidColorBrush CtrlHoverBrush    = Freeze(new(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)));
    private static readonly SolidColorBrush CtrlIdleBrush     = Freeze(new(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)));
    private static readonly SolidColorBrush ItemHoverBrush    = Freeze(new(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)));
    static SolidColorBrush Freeze(SolidColorBrush b) { b.Freeze(); return b; }
    public bool IsMinimized => RestoreButton.Visibility == Visibility.Visible;
    private readonly ZoneViewModel _vm;
    private readonly LocalizationService _loc = LocalizationService.Instance;
    private HwndSource? _src;
    private Canvas? _itemCanvas;
    private Action<string>? _langChanged;

    private bool _dragging;
    private Point _ds, _is;
    private ZoneItemViewModel? _dv;
    private FrameworkElement? _de;
    private System.Windows.Shapes.Rectangle? _dropIndicator;
    private const double BarThickness = 3, BarLength = 56;
    // The item footprint is one grid cell (square); the icon is centered inside it.
    double ItemW => _zone.GridSize;
    double ItemH => _zone.GridSize;
    private readonly System.Windows.Threading.DispatcherTimer _saveDebounce = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _savePending;
    private readonly System.Windows.Threading.DispatcherTimer _recycleTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private bool _recycleStateInit;
    private bool _recycleFullLast;
    private HoverExpandBehavior? _hover;
    private SnapDrag? _snapDrag;

    // ── Title-bar drag-to-merge ──
    private ZoneWindow? _mergeTarget;
    private bool _titleDragMoved;

    // ── Sub-zone tab drag (reorder + drag-out detach) ──
    private Border? _dragTab;
    private Guid _dragTabZoneId;
    private Point _dragTabOrigin;
    private int _dragTabFromIndex = -1;
    private int _dragTabInsertIndex = -1;
    private bool _dragTabArmed, _dragTabCompleted, _isDragTabOut;
    private double _dragTabGrabOffset;
    private System.Windows.Threading.DispatcherTimer? _tabDragTimer;
    private readonly Dictionary<FrameworkElement, double> _pendingTabSlide = new();
    // ponytail: extracted from inline lambdas so OnClosed can unsubscribe with the same
    // delegate reference. WPF event -= requires reference equality; lambdas can't be
    // removed once added.
    private readonly EventHandler _itemsHostStatusChangedHandler;
    private readonly System.Collections.Specialized.NotifyCollectionChangedEventHandler _vmItemsChangedHandler;

    public ZoneWindow(Zone zone, ZoneManager mgr, ShellIconService icons)
    {
        InitializeComponent();
        _zone = zone; _mgr = mgr;
        _vm = new ZoneViewModel(zone, mgr, icons);
        _vm.IsLocked = zone.IsLocked;
        DataContext = _vm;
        Left = zone.X; Top = zone.Y;
        Width = SanitizeW(zone.Width); Height = SanitizeW(zone.Height);
        ApplyStyle();
        // Acrylic is applied in OnLoad (needs valid HWND)
        ZoneTitleText.Text = zone.Name;
        SetRestoreIcon();
        ApplyLoc();
        _vmItemsChangedHandler = (_, _) => UpdateCanvasSize();
        _vm.Items.CollectionChanged += _vmItemsChangedHandler;
        Loaded += OnLoad;
        LocationChanged += (_, _) => { _zone.X = Left; _zone.Y = Top; ScheduleSave(); };
        SizeChanged += OnSize;
        _saveDebounce.Tick += (_, _) => { _saveDebounce.Stop(); if (_savePending) { _savePending = false; _mgr.SaveConfig(); } };
        _recycleTimer.Tick += RecycleTimer_Tick;
        _recycleTimer.Start();
        _langChanged = _ => ApplyLoc();
        _loc.LanguageChanged += _langChanged;
        _mgr.ZonesChanged += OnZonesChanged;
        // ponytail: subscribe to LockChanged so management UI (or any other source) flipping
        // this zone's lock state immediately syncs the open window.
        _mgr.LockChanged += OnServiceLockChanged;
        // ponytail: BP-A — container generation is lazy in WPF. ItemsControl doesn't
        // realize containers until layout pass runs, which is AFTER ApplyStyle in the
        // constructor and ShowZone's synchronous Visibility=Visible. Hook the generator's
        // StatusChanged so ApplyItemTextColorAdaptive fires the moment containers exist,
        // covering first-open, hide→show, and any subsequent item changes. Constructor
        // ApplyStyle still runs (it handles fill/border/title-bar which are XAML-static)
        // but its item walk is a no-op until this fires.
        _itemsHostStatusChangedHandler = (_, _) =>
        {
            if (ItemsHost.ItemContainerGenerator.Status
                == System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated)
                ApplyItemTextColorAdaptive();
        };
        ItemsHost.ItemContainerGenerator.StatusChanged += _itemsHostStatusChangedHandler;
        if (!_zone.IsVisible) ApplyHidden();
        // ponytail: ApplyStyle (line 74) now rebuilds sub-zone tabs internally with the
        // resolved adaptive brush. No external RebuildSubZoneTabs or
        // ApplySubZoneTabTextColorAdaptive call needed here.
        if (_zone.MergedGroupMembership.SubZoneIds.Count > 0) _vm.SelectedSubZoneId = _zone.Id;
        UpdateMergedTitle();
        // ponytail: hover-expand (Task 14d). Wire after InitComponent; the behavior
        // picks the right initial state from HoverAutoExpand.
        _hover = new HoverExpandBehavior(this, RestoreButton, MainContent, null,
            () => _zone.HoverExpandAnimation,
            () => _zone.HoverExpandSpeed,
            () => _zone.HoverExpandOrigin,
            () => _zone.HoverAutoExpand)
        { IsEnabled = _zone.EnableRestoreButton };
        // ponytail: 2026-08-21 — pick up live changes from MotionSettingsDialog.
        _zone.HoverExpandSettingsChanged += OnHoverExpandSettingsChanged;
        // ponytail: ghost-glass fix — acrylic follows the expand state: enable glass when
        // content expands (hover preview / click), disable when it collapses, so a collapsed
        // zone shows ONLY the RestoreButton and never a full-window glass rectangle.
        _hover.Expanded += ReapplyAcrylic;
        _hover.Collapsed += () => AcrylicHelper.DisableBlur(this);
        // ponytail: bug fix — ZoneManager.ShowZone new-window branch calls window.Show()
        // but NOT window.ShowZone(), so SnapToExpanded never runs and _isExpanded stays
        // false. Clicking Hide then early-returns inside CollapseAnimated ("!_isExpanded")
        // → no animation, RestoreButton never appears. Mirror the existing
        // `if (!_zone.IsVisible) ApplyHidden()` symmetry: if visible at construction,
        // snap the hover-expand state to expanded so the first Hide actually fires.
        if (_zone.IsVisible) _hover.SnapToExpanded();

        // ponytail: 自适应对齐 — 替换 DragMove 的手动拖拽循环。
        _snapDrag = new SnapDrag(this);
    }

    void OnHoverExpandSettingsChanged()
    {
        // Re-apply origin + snap baseline for the current kind without forcing
        // a state change. The host widget's visibility is the source of truth.
        _hover?.SetEnabled(_zone.EnableRestoreButton);
    }

    static double SanitizeW(double w) => w < 100 ? 400 : w;

    void ApplyLoc()
    {
        var cn = _loc.CurrentLanguage == "zh";
        CtxImport.Header = _loc["Zone.Import"];
        CtxImportFolder.Header = cn ? "导入文件夹..." : "Import Folder...";
        CtxImportFiles.Header = cn ? "导入文件..." : "Import Files...";
        CtxImportFolder2.Header = cn ? "导入文件夹..." : "Import Folder...";
        CtxImportShell.Header = _loc["Zone.ImportShellItems"];
        CtxImportShell2.Header = _loc["Zone.ImportShellItems"];
        CtxNew.Header = _loc["Zone.New"];
        CtxNew2.Header = _loc["Zone.New"];
        CtxNewFolder.Header = cn ? "新建文件夹... / New Folder..." : "New Folder...";
        CtxNewTxt.Header = cn ? "文本文档 (.txt)" : "Text Document (.txt)";
        CtxNewDocx.Header = cn ? "Word 文档 (.docx)" : "Word Document (.docx)";
        CtxNewPptx.Header = cn ? "PowerPoint (.pptx)" : "PowerPoint (.pptx)";
        CtxNewXlsx.Header = cn ? "Excel 工作表 (.xlsx)" : "Excel Workbook (.xlsx)";
        CtxNewFolder2.Header = cn ? "新建文件夹... / New Folder..." : "New Folder...";
        CtxNewTxt2.Header = cn ? "文本文档 (.txt)" : "Text Document (.txt)";
        CtxNewDocx2.Header = cn ? "Word 文档 (.docx)" : "Word Document (.docx)";
        CtxNewPptx2.Header = cn ? "PowerPoint (.pptx)" : "PowerPoint (.pptx)";
        CtxNewXlsx2.Header = cn ? "Excel 工作表 (.xlsx)" : "Excel Workbook (.xlsx)";
        CtxDisbandAll.Header = _loc["Merge.DisbandAll"];
        CtxEdit.Header = _loc["Zone.Edit"];
        CtxHide.Header = _loc["Zone.Hide"];
        CtxDelete.Header = _loc["Zone.Delete"];
    }

    void OnLoad(object s, RoutedEventArgs e)
    {
        if ((DataContext as ZoneViewModel)?.IsLocked != true) NativeMethods.PinToDesktop(this); NativeMethods.SetToolWindow(this);
        NativeMethods.SetRoundedCorners(this, (int)_zone.CornerRadius);
        // Re-apply full style now that HWND is valid (constructor's ApplyStyle ran before
        // HWND existed). ApplyStyle internally calls ApplyAcrylic with the freshly-resolved
        // colors, so no separate "store-then-restore" workaround is needed.
        ApplyStyle();
        var hwnd = new WindowInteropHelper(this).Handle;
        int ex = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, ex & ~NativeMethods.WS_EX_APPWINDOW);
        NativeMethods.DragAcceptFiles(hwnd, true);
        _src = HwndSource.FromHwnd(hwnd); _src?.AddHook(WndProc);

        // Find the Canvas for size updates
        _itemCanvas = FindVisualChild<Canvas>(this);
        UpdateCanvasSize();
        ApplyLockState();
    }

    IntPtr WndProc(IntPtr h, int m, IntPtr w, IntPtr l, ref bool hd)
    { if (m == NativeMethods.WM_DROPFILES) { DoDrop(w); hd = true; } return IntPtr.Zero; }

    void DoDrop(IntPtr drop)
    { try { uint n = NativeMethods.DragQueryFile(drop, 0xFFFFFFFF, null, 0); for (uint i = 0; i < n; i++) { var sb = new System.Text.StringBuilder(260); NativeMethods.DragQueryFile(drop, i, sb, 260); if (!string.IsNullOrEmpty(sb.ToString())) { var (sx, sy) = FindFreeSpot(); Add(sb.ToString(), sx, sy); } } UpdateCanvasSize(); } finally { NativeMethods.DragFinish(drop); } }

    void Add(string path, double x, double y)
    { var t = Dir(path) ? ItemType.Folder : Path.GetExtension(path).ToLowerInvariant() switch { ".lnk" => ItemType.Shortcut, ".exe" => ItemType.Application, _ => ItemType.Shortcut }; AddItem(new ZoneItem(Path.GetFileNameWithoutExtension(path), path, t, 0, 0), x, y); }

    /// <summary>
    /// Clamp import coordinates to the zone without grid-snapping: import flows place
    /// items on a fixed 80×90 cell grid, so snapping to the zone grid could collapse
    /// two cells onto one grid point (overlapping icons) whenever GridSize differs
    /// from the cell size. Y is stored as the item's real top-left (same convention as
    /// manual drag) so collision detection below sees the true bounds.
    /// </summary>
    void AddItem(ZoneItem item, double x, double y)
    {
        item.X = Math.Max(0, Math.Min(x, Math.Max(0, _zone.Width - ItemW)));
        item.Y = Math.Max(0, Math.Min(y, Math.Max(0, _zone.Height - ItemH)));
        _vm.AddItem(item);
    }

    static bool Dir(string p) => Directory.Exists(p);
    double Clamp(double v, double max) => Math.Max(0, Math.Min(Snap(v), max));
    double Snap(double v) => _zone.SnapToGrid ? ZoneViewModel.SnapToGrid(v, _zone.GridSize) : v;

    // ── Virtual shell objects (Recycle Bin, This PC, ...) ──

    void ImportShellItems_Click(object s, RoutedEventArgs e)
    {
        var dlg = new ShellLocationPickerWindow { Owner = this };
        if (dlg.ShowDialog() == true && dlg.SelectedItems.Count > 0)
            AddShellItems(dlg.SelectedItems);
    }

    void AddShellItems(IEnumerable<(string Name, string Spec)> items)
    {
        foreach (var (name, spec) in items)
        {
            var (sx, sy) = FindFreeSpot();
            AddItem(new ZoneItem(name, spec, ItemType.ShellLocation, 0, 0), sx, sy);
        }
        UpdateCanvasSize();
    }

    // ── External file drops (WPF AllowDrop — shows "not allowed" for non-file drags) ──

    void Window_DragEnter(object s, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effects = DragDropEffects.Copy; e.Handled = true; }
    }

    void Window_DragOver(object s, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effects = DragDropEffects.Copy; e.Handled = true; }
    }

    void Window_Drop(object s, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files) return;
        foreach (var f in files)
        {
            var (sx, sy) = FindFreeSpot();
            Add(f, sx, sy);
        }
        UpdateCanvasSize();
        e.Handled = true;
    }

    // ── Recycle Bin icon state (empty ⇄ full) ──

    void RecycleTimer_Tick(object? s, EventArgs e)
    {
        try
        {
            bool hasRecycle = false;
            foreach (var item in _vm.Items)
            {
                if (item.Type == ItemType.ShellLocation && ShellIconService.IsRecycleBin(item.TargetPath))
                { hasRecycle = true; break; }
            }
            if (!hasRecycle) { _recycleStateInit = false; return; }

            bool full = ShellIconService.RecycleBinHasItems();
            if (_recycleStateInit && full == _recycleFullLast) return;
            _recycleStateInit = true;
            _recycleFullLast = full;
            foreach (var item in _vm.Items)
            {
                if (item.Type == ItemType.ShellLocation && ShellIconService.IsRecycleBin(item.TargetPath))
                    item.RefreshIcon();
            }
        }
        catch { }
    }

    // ── Show / Hide ──

    public void ShowZone(double waveDelayMs = 0)
    {
        // ponytail: 2026-08-23 — a window hidden via Hide()/ApplyHidden (full-hide
        // path) stays in the manager's dictionary when the hide came through
        // UpdateZone/RefreshZone; ShowZone never re-showed it, so the zone stayed
        // invisible. Re-show symmetrically with ShowClock/ShowCalendar/ShowNote.
        if (!IsVisible) Show();
        if (_zone.Width < 100) _zone.Width = 400; if (_zone.Height < 100) _zone.Height = 300;
        Width = _zone.Width; Height = _zone.Height; Left = _zone.X; Top = _zone.Y;
        if (waveDelayMs > 0)
        {
            // ponytail: batch "Show All" wave — start collapsed and play the zone's own
            // configured animation after its stagger delay (each window uses its own
            // kind/speed/origin, so the batch opens as a staggered cascade).
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
        _zone.IsVisible = true;
        // ponytail: BP-A — Visibility=Visible is processed in the next layout pass, so a
        // synchronous ApplyStyle would walk the visual tree before WPF has re-attached
        // item containers. Defer to Loaded priority so the brush walk runs after layout,
        // catching the hide→show path that StatusChanged alone wouldn't fire for (when
        // containers were already generated, generator status doesn't transition again).
        Dispatcher.BeginInvoke(new Action(ApplyStyle),
            System.Windows.Threading.DispatcherPriority.Loaded);
        if ((DataContext as ZoneViewModel)?.IsLocked != true) NativeMethods.PinToDesktop(this);
        NativeMethods.SetRoundedCorners(this, (int)_zone.CornerRadius);
        _mgr.FireZoneVisibilityChanged(_zone.Id, true);
    }

    public void HideZone(double waveDelayMs = 0)
    {
        // Save dimensions only if not currently minimized (RestoreButton not visible)
        // If minimized, the original dimensions are already saved in _zone
        if (RestoreButton.Visibility != Visibility.Visible)
        {
            _zone.X = Left; _zone.Y = Top; _zone.Width = Width; _zone.Height = Height;
            _mgr.SaveConfig();
        }
        if (!_zone.EnableRestoreButton)
        {
            if (waveDelayMs > 0)
            {
                // ponytail: batch "Minimize All" wave — play the zone's own collapse
                // animation first (staggered), then finalize the full hide: the window
                // shrinks to 36×36, hides and closes itself once the animation finishes.
                _hover?.CollapseAfterDelay(waveDelayMs, onComplete: () =>
                {
                    AcrylicHelper.DisableBlur(this);
                    _hover?.SnapToFullHidden();
                    MainContent.Visibility = Visibility.Collapsed;
                    Width = 36; Height = 36;
                    NativeMethods.DisableRoundedCorners(this);
                    Hide();
                    Close();
                });
            }
            else
            {
                // ponytail: full hide — window itself goes away, RestoreButton never shown.
                // SnapToFullHidden resets the hover state (IsExpanded=false, scale/opacity 0)
                // so no later ApplyStyle/ApplyAcrylic can re-enable the DWM glass on the
                // hidden window (the "empty liquid glass" ghost).
                AcrylicHelper.DisableBlur(this);
                _hover?.SnapToFullHidden();
                MainContent.Visibility = Visibility.Collapsed;
                Width = 36; Height = 36;
                NativeMethods.DisableRoundedCorners(this);
                Hide();
            }
        }
        else
        {
            // ponytail: minimized — window stays at full size, content collapses
            // with animation, RestoreButton stays visible at top-left for hover/click
            // to expand again.
            NativeMethods.DisableRoundedCorners(this);
            if ((DataContext as ZoneViewModel)?.IsLocked != true) NativeMethods.PinToDesktop(this);
            if (waveDelayMs > 0)
                _hover?.CollapseAfterDelay(waveDelayMs, null);
            else
                _hover?.CollapseAnimated();
        }
        _zone.IsVisible = false;
        _mgr.FireZoneVisibilityChanged(_zone.Id, false);
    }

    /// <summary>
    /// Batch-wave entrance for a freshly created window ("Show All" after the zone
    /// window was closed by a full hide): collapse the just-shown content and play
    /// the zone's own configured expand animation after the stagger delay.
    /// </summary>
    public void PlayEntranceAnimation(double waveDelayMs)
    {
        if (waveDelayMs <= 0) return;
        _hover?.SnapToCollapsed();
        RestoreButton.Visibility = Visibility.Collapsed; // no button flash during the delay
        _hover?.ShowAfterDelay(waveDelayMs);
    }

    void ApplyHidden()
    {
        AcrylicHelper.DisableBlur(this);
        NativeMethods.DisableRoundedCorners(this);
        if (!_zone.EnableRestoreButton)
        {
            // ponytail: full hide — see HideZone for the SnapToFullHidden rationale.
            _hover?.SnapToFullHidden();
            MainContent.Visibility = Visibility.Collapsed;
            Width = 36; Height = 36;
            Hide();
        }
        else
        {
            // ponytail: 2026-08-23 — restore the full window size. If a previous
            // full-hide (EnableRestoreButton was off) shrank the window to 36×36,
            // collapsed-to-button mode needs the full-size window back (spec §7.2:
            // the window keeps its size while collapsed; the anchor math and the
            // hover region depend on it).
            Width = _zone.Width < 100 ? 400 : _zone.Width;
            Height = _zone.Height < 100 ? 300 : _zone.Height;
            // ponytail: keep window at full size; HoverExpandBehavior owns
            // visibility/scale from here.
            _hover?.SnapToCollapsed();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _saveDebounce?.Stop();
        _recycleTimer.Stop();
        _vm.Items.CollectionChanged -= _vmItemsChangedHandler;
        ItemsHost.ItemContainerGenerator.StatusChanged -= _itemsHostStatusChangedHandler;
        _zone.HoverExpandSettingsChanged -= OnHoverExpandSettingsChanged;
        if (_tabDragTimer != null)
        {
            _tabDragTimer.Stop();
            _tabDragTimer.Tick -= OnTabDragTick;
            _tabDragTimer = null;
        }
        _snapDrag?.Detach();
        _hover?.Dispose();
        var h = new WindowInteropHelper(this).Handle;
        _mgr.ZonesChanged -= OnZonesChanged;
        _mgr.LockChanged -= OnServiceLockChanged;
        if (_src != null) { _src.RemoveHook(WndProc); _src = null; }
        if (_langChanged != null) { _loc.LanguageChanged -= _langChanged; _langChanged = null; }
        if (h != IntPtr.Zero) NativeMethods.DragAcceptFiles(h, false);
        base.OnClosed(e);
    }

    void OnZonesChanged()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _vm.RefreshItems();
            UpdateCanvasSize();
            // ponytail: Fix C — re-apply adaptive text color after RefreshItems wipes the
            // brush via the XAML default `#E0FFFFFF` foreground on freshly-generated item
            // containers. Without this, any OnZonesChanged trigger (rename, delete, etc.)
            // would silently revert the previously-applied brush on all items.
            ApplyStyle();
        }), System.Windows.Threading.DispatcherPriority.Normal);
    }

    // ── Drag: DIRECT handler on title bar ──

    void TitleBar_Drag(object s, MouseButtonEventArgs e)
    {
        var vm = DataContext as ZoneViewModel;
        if (vm?.IsLocked == true) return;
        if (_snapDrag == null || _snapDrag.IsActive) return;

        var restOpacity = ControlPoint.Opacity;
        ControlPoint.Opacity = 0.6;
        _mergeTarget = null;
        _titleDragMoved = false;
        _snapDrag.DragMoved += OnTitleBarDragMoved;
        _snapDrag.Start(e, () => OnTitleBarDragCompleted(restOpacity, vm));
    }

    void OnTitleBarDragMoved(Point screenPos)
    {
        _titleDragMoved = true;
        var target = ZoneMergeRouter.FindTitleBarTarget(this, screenPos);
        if (ReferenceEquals(target, _mergeTarget)) return;
        _mergeTarget?.SetMergeHover(false);
        _mergeTarget = target;
        _mergeTarget?.SetMergeHover(true);
        // Ghost the dragged window while over a valid target so the target's title bar
        // (and its enlarge animation) stays visible underneath.
        Opacity = target != null ? 0.55 : 1.0;
    }

    void OnTitleBarDragCompleted(double restOpacity, ZoneViewModel? vm)
    {
        _snapDrag!.DragMoved -= OnTitleBarDragMoved;
        var wasClick = !_titleDragMoved;
        _titleDragMoved = false;
        var target = _mergeTarget;
        _mergeTarget = null;
        if (target != null) target.SetMergeHover(false);
        ControlPoint.Opacity = restOpacity;
        Opacity = 1.0;

        // Dropped on another zone's title bar → create/extend a merged group.
        if (target != null)
        {
            _mgr.MergeZoneInto(target.ZoneId, _zone.Id);
            return;
        }

        // Click (no drag) on a merged master's title bar → switch back to the master view.
        if (wasClick && _zone.MergedGroupMembership.SubZoneIds.Count > 0)
        {
            SelectSubZone(_zone.Id);
            return;
        }

        if (vm?.IsLocked != true) NativeMethods.PinToDesktop(this);
        _zone.X = Left; _zone.Y = Top;
        _mgr.SaveConfig();
    }

    // ── Merge drop-target surface ──

    public Guid ZoneId => _zone.Id;

    public bool CanAcceptMerge =>
        IsVisible && MainContent.Visibility == Visibility.Visible && !_zone.IsLocked;

    public Rect TitleBarHitRect()
    {
        if (TitleBarBg.Visibility != Visibility.Visible || TitleBarBg.ActualWidth <= 0 || TitleBarBg.ActualHeight <= 0)
            return Rect.Empty;
        var topLeft = TitleBarBg.TransformToAncestor(this).Transform(new Point(0, 0));
        return new Rect(topLeft, new Size(TitleBarBg.ActualWidth, TitleBarBg.ActualHeight));
    }

    public void SetMergeHover(bool on)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        TitleBarScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(on ? 1.06 : 1.0, TimeSpan.FromMilliseconds(140)) { EasingFunction = ease });
        TitleBarScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(on ? 1.24 : 1.0, TimeSpan.FromMilliseconds(140)) { EasingFunction = ease });
    }

    // ── Window-level mouse: resize grips only ──

    // ponytail: OS routes click normally now (no drill-through).
    // Kept as a no-op so the XAML handler reference in ZoneWindow.xaml keeps working.
    void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) { }

    // ── Resize ──

    void ResizeGrip_Down(object s, MouseButtonEventArgs e)
    {
        var vm = DataContext as ZoneViewModel;
        if (vm?.IsLocked == true) { e.Handled = true; return; }
        if (s is not Border gr) return;
        int d = gr == GripTL ? HTTOPLEFT : gr == GripTR ? HTTOPRIGHT : gr == GripBL ? HTBOTTOMLEFT : HTBOTTOMRIGHT;
        SendMessage(new WindowInteropHelper(this).Handle, WM_NCLBUTTONDOWN, (IntPtr)d, IntPtr.Zero);
        if (vm?.IsLocked != true) NativeMethods.PinToDesktop(this);
        e.Handled = true;
    }

    // ── Import ──

    void ImportFiles_Click(object s, RoutedEventArgs e)
    { var d = new OpenFileDialog { Title = _loc["Zone.ImportTitle"], Filter = "All|*.lnk;*.exe;*.*|Shortcuts|*.lnk|Apps|*.exe", Multiselect = true }; if (d.ShowDialog() == true) ImportArranged(d.FileNames); }

    void ImportFolder_Click(object s, RoutedEventArgs e)
    {
        var displayBuf = IntPtr.Zero;
        var pidl = IntPtr.Zero;
        try
        {
            var h = new WindowInteropHelper(this); h.EnsureHandle();
            displayBuf = Marshal.AllocHGlobal(520); // MAX_PATH*2 Unicode
            var bi = new NativeMethods.BROWSEINFOW
            {
                hwndOwner = h.Handle,
                pszDisplayName = displayBuf,
                lpszTitle = "Select Folder",
                ulFlags = 0x40
            };
            pidl = NativeMethods.SHBrowseForFolderW(ref bi);
            if (pidl != IntPtr.Zero)
            {
                var sb = new System.Text.StringBuilder(260);
                if (NativeMethods.SHGetPathFromIDListW(pidl, sb) && Directory.Exists(sb.ToString()))
                    ImportArranged(new[] { sb.ToString() });
            }
        }
        catch (Exception ex) { MessageBox.Show($"Import failed: {ex.Message}"); }
        finally
        {
            if (displayBuf != IntPtr.Zero) Marshal.FreeHGlobal(displayBuf);
            if (pidl != IntPtr.Zero) NativeMethods.CoTaskMemFree(pidl);
        }
    }

    void ImportBtn_Click(object s, MouseButtonEventArgs e)
    {
        ImportBtn.ContextMenu.IsOpen = true;
        e.Handled = true;
    }

    void ImportArranged(string[] paths)
    {
        foreach (var f in paths) { var (sx, sy) = FindFreeSpot(); Add(f, sx, sy); }
        UpdateCanvasSize();
    }

    (double, double) FindFreeSpot() => ZoneLayout.FindFreeSpot(_vm.GetPlacementItems(), _zone.Width, _zone.Height, _zone.GridSize, _zone.GridSize);

    void RearrangeAll()
    {
        if (!_zone.AutoArrange) return;

        // Determine which zone's items to rearrange and which grid size to use
        List<Models.ZoneItem> items;
        int gridSize;

        if (_zone.MergedGroupMembership.SubZoneIds.Count > 0 && _vm.SelectedSubZoneId.HasValue && _vm.SelectedSubZoneId.Value != _zone.Id)
        {
            // Merged mode with a sub-zone tab selected
            var subZone = _mgr.Zones.FirstOrDefault(z => z.Id == _vm.SelectedSubZoneId.Value);
            if (subZone == null) return;
            items = subZone.Items;
            gridSize = subZone.GridSize;
        }
        else
        {
            items = _zone.Items;
            gridSize = _zone.GridSize;
        }

        double x = 10, y = 10;
        foreach (var item in items.OrderBy(i => i.Y).ThenBy(i => i.X))
        {
            item.X = ZoneViewModel.SnapToGrid(x, gridSize);
            item.Y = ZoneViewModel.SnapToGrid(y, gridSize);
            x += gridSize;
            if (x > _zone.Width - gridSize) { x = 10; y += gridSize; }
        }
        _vm.RefreshMergedItems();
    }

    // ── Right-click zone ──

    void Window_PreviewMouseRightButtonDown(object s, MouseButtonEventArgs e)
    { if (IsOnItem(e.OriginalSource) || MainContent.Visibility != Visibility.Visible) return; ZoneBorder.ContextMenu.IsOpen = true; e.Handled = true; }
    void EditZone_Click(object s, RoutedEventArgs e) { _vm.IsEditing = !_vm.IsEditing; EditBtnText.Text = _vm.IsEditing ? "✓" : "⚙"; }
    void HideZone_Click(object s, RoutedEventArgs e) { HideZone(); }
    void DeleteZone_Click(object s, RoutedEventArgs e) { if (MessageBox.Show(_loc.Get("Dialog.DeleteZoneMsg", _zone.Name), _loc["Dialog.DeleteZoneTitle"], MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes) { _mgr.DeleteZone(_zone.Id); Close(); } }
    void DisbandAll_Click(object s, RoutedEventArgs e)
    {
        if (!_zone.MergedGroupMembership.GroupId.HasValue) return;
        if (MessageBox.Show(_loc["Merge.ConfirmDisband"], _loc["Merge.DisbandAll"], MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            _mgr.DisbandMergedGroup(_zone.MergedGroupMembership.GroupId.Value);
        }
    }

    void DisbandThis_Click(object s, RoutedEventArgs e)
    {
        if (!_zone.MergedGroupMembership.GroupId.HasValue) return;
        // If this zone is a sub-zone (not master), remove it from the group
        if (_zone.MergedGroupMembership.SubZoneIds.Count == 0)
        {
            var cn = _loc.CurrentLanguage == "zh";
            if (MessageBox.Show(
                cn ? $"确定要将分区「{_zone.Name}」从组合中分离吗？"
                   : $"Remove zone \"{_zone.Name}\" from the merged group?",
                _loc["Merge.DisbandThis"], MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                _mgr.RemoveFromMergedGroup(_zone.Id);
            }
        }
        else
        {
            // This is the master — disband the whole group
            DisbandAll_Click(s, e);
        }
    }

    // ── New submenu ──

    void NewFolder_Click(object s, RoutedEventArgs e)
    {
        var displayBuf = IntPtr.Zero;
        var pidl = IntPtr.Zero;
        try
        {
            var h = new WindowInteropHelper(this); h.EnsureHandle();
            displayBuf = Marshal.AllocHGlobal(520);
            var bi = new NativeMethods.BROWSEINFOW
            {
                hwndOwner = h.Handle,
                pszDisplayName = displayBuf,
                lpszTitle = "Select Parent Folder",
                ulFlags = 0x40
            };
            pidl = NativeMethods.SHBrowseForFolderW(ref bi);
            if (pidl != IntPtr.Zero)
            {
                var sb = new System.Text.StringBuilder(260);
                if (NativeMethods.SHGetPathFromIDListW(pidl, sb))
                {
                    // Prompt for folder name
                    string parentPath = sb.ToString();
                    string folderName = Microsoft.VisualBasic.Interaction.InputBox(
                        "Folder Name:", "New Folder", "New Folder");
                    if (!string.IsNullOrWhiteSpace(folderName))
                    {
                        string fullPath = Path.Combine(parentPath, folderName);
                        Directory.CreateDirectory(fullPath);
                    }
                }
            }
        }
        catch (Exception ex) { MessageBox.Show($"Failed: {ex.Message}"); }
        finally
        {
            if (displayBuf != IntPtr.Zero) Marshal.FreeHGlobal(displayBuf);
            if (pidl != IntPtr.Zero) NativeMethods.CoTaskMemFree(pidl);
        }
    }

    void CreateNewFile(string defaultExt, string filter)
    {
        var d = new SaveFileDialog
        {
            Title = "Create New File",
            Filter = filter,
            DefaultExt = defaultExt,
            FileName = "NewDocument" + defaultExt
        };
        if (d.ShowDialog() == true)
        {
            try { System.IO.File.Create(d.FileName).Dispose(); }
            catch (Exception ex) { MessageBox.Show($"Failed: {ex.Message}"); }
            Add(d.FileName, 10, 10);
            UpdateCanvasSize();
            _mgr.SaveConfig();
        }
    }

    void NewTxt_Click(object s, RoutedEventArgs e)
    {
        CreateNewFile(".txt", "Text Document|*.txt|All Files|*.*");
    }

    void NewDocx_Click(object s, RoutedEventArgs e)
    {
        CreateNewFile(".docx", "Word Document|*.docx|All Files|*.*");
    }

    void NewPptx_Click(object s, RoutedEventArgs e)
    {
        CreateNewFile(".pptx", "PowerPoint|*.pptx|All Files|*.*");
    }

    void NewXlsx_Click(object s, RoutedEventArgs e)
    {
        CreateNewFile(".xlsx", "Excel Worksheet|*.xlsx|All Files|*.*");
    }

    // Minimized state drag — uses SnapDrag (manual drag loop) like title bar
    private bool _restoreDragging;
    private Point _restoreDown;

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
                if ((DataContext as ZoneViewModel)?.IsLocked != true) NativeMethods.PinToDesktop(this);
                _zone.X = Left; _zone.Y = Top; _mgr.SaveConfig();
            });
        }
    }

    void Restore_MouseUp(object s, MouseButtonEventArgs e)
    {
        RestoreButton.ReleaseMouseCapture();
        // ponytail: click = permanent expand (no auto-collapse); the hover path
        // (1 s on RestoreButton) is the temporary preview with 3 s auto-collapse.
        // Both share the same animation from HoverExpandBehavior's animationGetter.
        if (!_restoreDragging)
        {
            // ponytail: 2026-08-23 — keep the model in sync with the window: expanding
            // from the RestoreButton makes the zone visible again, so persist it before
            // any ZonesChanged/visibility listener can observe the stale hidden state.
            _zone.IsVisible = true;
            _hover?.ExpandAnimated(permanent: true);
            _mgr.SaveConfig();
            _mgr.FireZoneVisibilityChanged(_zone.Id, true);
        }
    }

    void Restore_Enter(object s, MouseEventArgs e) { RestoreButton.Background = RestoreHoverBrush; }
    void Restore_Leave(object s, MouseEventArgs e) { RestoreButton.Background = RestoreIdleBrush; }

    void Ctrl_Enter(object s, MouseEventArgs e) { if (s is Border b) b.Background = CtrlHoverBrush; }
    void Ctrl_Leave(object s, MouseEventArgs e) { if (s is Border b) b.Background = CtrlIdleBrush; }
    void HideButton_Click(object s, MouseButtonEventArgs e) { HideZone(); e.Handled = true; }

    void LockBtn_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var vm = DataContext as ZoneViewModel;
        if (vm == null) return;
        // ponytail: sync from model first — guards against double-click no-op when model and
        // view have drifted (e.g. management card toggled lock state, event arrived out of order).
        vm.IsLocked = vm.Zone.IsLocked;
        vm.IsLocked = !vm.IsLocked;
        ApplyLockState();
        _mgr?.SetLocked(vm.Zone.Id.ToString(), vm.IsLocked);
        _mgr.SaveConfig();
    }

    void OnServiceLockChanged(string id, bool locked)
    {
        var vm = DataContext as ZoneViewModel;
        if (vm == null || id != vm.Zone.Id.ToString()) return;
        if (vm.IsLocked == locked) return;
        vm.IsLocked = locked;
        ApplyLockState();
    }

    void ApplyLockState()
    {
        var vm = DataContext as ZoneViewModel;
        if (vm == null) return;
        LockBtnText.Text = vm.IsLocked ? "🔒" : "🔓";
        TitleBarBg.Cursor = vm.IsLocked ? System.Windows.Input.Cursors.Arrow : System.Windows.Input.Cursors.SizeAll;
        GripTL.Visibility = GripTR.Visibility = GripBL.Visibility = GripBR.Visibility =
            vm.IsLocked ? Visibility.Collapsed : Visibility.Visible;
        if (vm.IsLocked) NativeMethods.PinBelowProgman(this);
    }

    void AlignGrid_Click(object s, MouseButtonEventArgs e)
    {
        _zone.SnapToGrid = true;
        RearrangeAll();
        _mgr.SaveConfig();
        e.Handled = true;
    }
    void EditButton_Click(object s, MouseButtonEventArgs e)
    {
        // ponytail: pass `this` so the popped-out panel anchors at the zone's
        // position (offset 24,24) instead of jumping to a remembered location —
        // see PropertyWindowManager.ResolvePopPosition.
        // ponytail 2026-08-26: a merged window's gear opens the standalone
        // merged-group editor; standalone windows keep the per-zone editor.
        if (_zone.MergedGroupMembership.SubZoneIds.Count > 0)
            PropertyWindowService.OpenOrFocus(MergedGroupTarget.For(_zone), this);
        else
            PropertyWindowService.OpenOrFocus(_zone, this);
        e.Handled = true;
    }

    // ── File drops ──
    // Handled by the WPF AllowDrop handlers above (Window_DragEnter/Over/Drop).

    // ── Item drag ──

    void Item_MouseDown(object s, MouseButtonEventArgs e)
    { if (e.ClickCount == 2) { if (s is FrameworkElement fe && fe.DataContext is ZoneItemViewModel iv) Open(iv); e.Handled = true; return; } if (s is FrameworkElement el && el.DataContext is ZoneItemViewModel vm) { _dv = vm; _de = el; _ds = e.GetPosition(this); _is = new Point(vm.X, vm.Y); _dragging = false; el.CaptureMouse(); e.Handled = true; } }
    void Item_MouseMove(object s, MouseEventArgs e)
    {
        if (_dv == null || _de == null) return;
        var d = e.GetPosition(this) - _ds;
        if (!_dragging)
        {
            if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            _dragging = true;
            _de.Opacity = 0.7;
        }
        _dv.X = Math.Max(0, Math.Min(_is.X + d.X, _zone.Width - ItemW));
        _dv.Y = Math.Max(0, Math.Min(_is.Y + d.Y, _zone.Height - ItemH));
        UpdateDropIndicator(_dv);
    }

    void Item_MouseUp(object s, MouseButtonEventArgs e)
    {
        if (_dv == null) return;
        if (_de != null) { _de.ReleaseMouseCapture(); _de.Opacity = 1.0; }
        if (_dragging)
        {
            if (_zone.SnapToGrid) ReorderItemInto(_dv, _dv.X, _dv.Y);
            else { _vm.MoveItem(_dv.Id, _dv.X, _dv.Y, snapToGrid: false); _vm.RefreshMergedItems(); }
        }
        HideDropIndicator();
        _dv = null; _de = null; _dragging = false;
    }

    // ── Item reorder (SnapToGrid) + drop indicator ──

    Zone? OwnerZoneOf(ZoneItemViewModel vm)
    {
        if (vm.SourceZoneId == Guid.Empty || vm.SourceZoneId == _zone.Id) return _zone;
        return _mgr.Zones.FirstOrDefault(z => z.Id == vm.SourceZoneId);
    }

    static int ComputeInsertIndex(List<ZoneItem> others, double dropX, double dropY, int gs)
    {
        int row = (int)Math.Round((dropY - 10) / gs);
        int col = (int)Math.Round((dropX - 10) / gs);
        int k = 0;
        foreach (var o in others)
        {
            int r = (int)Math.Round((o.Y - 10) / gs);
            int c = (int)Math.Round((o.X - 10) / gs);
            if (r < row || (r == row && c < col)) k++;
        }
        return k;
    }

    void ReorderItemInto(ZoneItemViewModel dragged, double dropX, double dropY)
    {
        var owner = OwnerZoneOf(dragged);
        if (owner == null) return;
        var item = owner.Items.FirstOrDefault(i => i.Id == dragged.Id);
        if (item == null) return;

        var others = owner.Items.Where(i => i.Id != dragged.Id)
                                .OrderBy(i => i.Y).ThenBy(i => i.X).ToList();
        if (others.Count == 0)
        {
            // No neighbours to reorder around — keep the previous free cell snapping.
            _vm.MoveItem(dragged.Id, dropX, dropY, snapToGrid: true);
            _vm.RefreshMergedItems();
            return;
        }

        int gs = owner.GridSize;
        double zw = _zone.Width;
        if (double.IsNaN(zw) || zw < gs + 10) zw = gs + 10;
        int cols = Math.Max(1, (int)Math.Floor((zw - 10) / gs));
        int k = Math.Clamp(ComputeInsertIndex(others, dropX, dropY, gs), 0, others.Count);

        var ordered = new List<ZoneItem>(others.Count + 1);
        ordered.AddRange(others.Take(k));
        ordered.Add(item);
        ordered.AddRange(others.Skip(k));

        // Capture each item's current visual position before rewriting the model.
        var oldPos = new Dictionary<Guid, (double X, double Y)>();
        foreach (var it in ordered)
        {
            var vm = _vm.Items.FirstOrDefault(v => v.Id == it.Id);
            if (vm != null) oldPos[it.Id] = (vm.X, vm.Y);
        }

        double x = 10, y = 10;
        foreach (var it in ordered)
        {
            it.X = ZoneViewModel.SnapToGrid(x, gs);
            it.Y = ZoneViewModel.SnapToGrid(y, gs);
            x += gs;
            if (x > zw - gs) { x = 10; y += gs; }
        }

        owner.Items.Clear();
        owner.Items.AddRange(ordered);
        _mgr.SaveConfig();

        // Animate every affected icon (including the dragged one) sliding into its
        // new cell. No RefreshMergedItems here — rebuilding the VM list would destroy
        // the containers mid-animation; the VM order is irrelevant because positions
        // are absolute Canvas coordinates.
        foreach (var it in ordered)
        {
            var vm = _vm.Items.FirstOrDefault(v => v.Id == it.Id);
            if (vm == null || !oldPos.TryGetValue(it.Id, out var old)) continue;
            vm.X = it.X;
            vm.Y = it.Y;
            AnimateItemTo(vm, old.X, old.Y, it.X, it.Y);
        }
    }

    void AnimateItemTo(ZoneItemViewModel vm, double fromX, double fromY, double toX, double toY)
    {
        if (ItemsHost.ItemContainerGenerator.ContainerFromItem(vm) is not FrameworkElement fe) return;
        var duration = TimeSpan.FromMilliseconds(170);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };

        var ax = new DoubleAnimation(fromX, toX, duration) { EasingFunction = easing };
        ax.Completed += (_, _) => { fe.BeginAnimation(Canvas.LeftProperty, null); vm.X = toX; };
        var ay = new DoubleAnimation(fromY, toY, duration) { EasingFunction = easing };
        ay.Completed += (_, _) => { fe.BeginAnimation(Canvas.TopProperty, null); vm.Y = toY; };

        fe.BeginAnimation(Canvas.LeftProperty, ax);
        fe.BeginAnimation(Canvas.TopProperty, ay);
    }

    void UpdateDropIndicator(ZoneItemViewModel dragged)
    {
        if (!_zone.SnapToGrid) { HideDropIndicator(); return; }
        var owner = OwnerZoneOf(dragged);
        if (owner == null) { HideDropIndicator(); return; }

        var others = owner.Items.Where(i => i.Id != dragged.Id)
                                .OrderBy(i => i.Y).ThenBy(i => i.X).ToList();
        if (others.Count == 0) { HideDropIndicator(); return; }

        int gs = owner.GridSize;
        double zw = _zone.Width;
        if (double.IsNaN(zw) || zw < gs + 10) zw = gs + 10;
        int cols = Math.Max(1, (int)Math.Floor((zw - 10) / gs));
        int k = Math.Clamp(ComputeInsertIndex(others, dragged.X, dragged.Y, gs), 0, others.Count);

        // The bar is centred in the actual visual gap between the two icons that
        // flank the insertion point (icon edge ↔ icon edge), so it keeps the same
        // moderate distance from the icon on every side — left/right and top/bottom
        // stay symmetric, and it follows the dragged icon instead of hugging the
        // neighbour cell edge. Falls back to the cell boundary when the icons
        // overlap/touch (no room left for the bar).
        double iconSize = Math.Max(24, gs - 8);
        double inset = (gs - iconSize) / 2;
        (double L, double T, double R, double B) Bounds(ZoneItem it)
            => (it.X + inset, it.Y + inset, it.X + gs - inset, it.Y + gs - inset);

        double dInset = Math.Max(0, (dragged.ItemSize - dragged.IconSize) / 2);
        double dL = dragged.X + dInset, dT = dragged.Y + dInset;
        double dR = dragged.X + dragged.ItemSize - dInset;
        double dB = dragged.Y + dragged.ItemSize - dInset;

        double GapCenter(double a, double b, double fallback)
            => b - a >= BarThickness ? (a + b) / 2 : fallback;

        // The bar marks the insertion gap in reading order: vertical when the gap
        // sits between two columns, horizontal when it sits between two rows.
        double barX, barY;
        bool vertical;

        if (k == 0)
        {
            // Insert before the first item → caret in the gap between the dragged
            // icon and that item (on its left side).
            var first = others[0];
            var fb = Bounds(first);
            vertical = true;
            barX = GapCenter(dR, fb.L, first.X);
            barY = first.Y;
        }
        else if (k == others.Count)
        {
            // Insert after the last item.
            var prev = others[others.Count - 1];
            int prevCol = (int)Math.Round((prev.X - 10) / gs);
            var pb = Bounds(prev);
            if (prevCol < cols - 1)
            {
                vertical = true;      // same row, to its right
                barX = GapCenter(pb.R, dL, prev.X + gs);
                barY = prev.Y;
            }
            else
            {
                vertical = false;     // wraps to a new row below
                barX = 10;
                barY = GapCenter(pb.B, dT, prev.Y + gs);
            }
        }
        else
        {
            var prev = others[k - 1];
            var next = others[k];
            int prevRow = (int)Math.Round((prev.Y - 10) / gs);
            int nextRow = (int)Math.Round((next.Y - 10) / gs);
            var pb = Bounds(prev);
            var nb = Bounds(next);
            if (prevRow == nextRow)
            {
                vertical = true;      // gap between two columns
                barX = GapCenter(pb.R, nb.L, next.X);
                barY = next.Y;
            }
            else
            {
                vertical = false;     // gap between two rows
                barX = 10;
                barY = GapCenter(pb.B, nb.T, next.Y);
            }
        }

        var bar = EnsureDropIndicator();
        if (vertical)
        {
            bar.Width = BarThickness; bar.Height = BarLength;
            Canvas.SetLeft(bar, barX - BarThickness / 2);
            Canvas.SetTop(bar, barY + (ItemH - BarLength) / 2);
        }
        else
        {
            bar.Width = BarLength; bar.Height = BarThickness;
            Canvas.SetLeft(bar, barX + (ItemW - BarLength) / 2);
            Canvas.SetTop(bar, barY - BarThickness / 2);
        }
        bar.Visibility = Visibility.Visible;
    }

    System.Windows.Shapes.Rectangle EnsureDropIndicator()
    {
        if (_dropIndicator == null)
        {
            _dropIndicator = new System.Windows.Shapes.Rectangle
            {
                RadiusX = 1.5,
                RadiusY = 1.5,
                Opacity = 0.95,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            _dropIndicator.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Brush.Accent");
            DropIndicatorLayer.Children.Add(_dropIndicator);
        }
        return _dropIndicator;
    }

    void HideDropIndicator()
    { if (_dropIndicator != null) _dropIndicator.Visibility = Visibility.Collapsed; }

    void Item_Enter(object s, MouseEventArgs e)
    {
        if (s is Grid g)
            g.Background = ItemHoverBrush;
    }

    void Item_Leave(object s, MouseEventArgs e)
    {
        if (s is Grid g)
            g.Background = Brushes.Transparent;
    }

    // ── Context menu ──

    void ItemOpen_Click(object s, RoutedEventArgs e) { if (VM(s) is ZoneItemViewModel v) Open(v); }

    // ── Recycle Bin item: right-click specialization (Empty Recycle Bin) ──

    void ItemMenu_Opened(object s, RoutedEventArgs e)
    {
        if (s is not ContextMenu cm || cm.PlacementTarget is not FrameworkElement fe
            || fe.DataContext is not ZoneItemViewModel vm) return;
        bool isRecycle = vm.Type == ItemType.ShellLocation && ShellIconService.IsRecycleBin(vm.TargetPath);
        foreach (var entry in cm.Items)
        {
            if (entry is MenuItem { Name: "CtxEmptyRecycle" } mi)
                mi.Visibility = isRecycle ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    void ItemEmptyRecycle_Click(object s, RoutedEventArgs e)
    {
        if (VM(s) is not ZoneItemViewModel v) return;
        try
        {
            NativeMethods.SHEmptyRecycleBinW(new WindowInteropHelper(this).Handle, null,
                NativeMethods.SHERB_NOCONFIRMATION | NativeMethods.SHERB_NOPROGRESSUI | NativeMethods.SHERB_NOSOUND);
        }
        catch { }
        // Refresh the bin icon immediately instead of waiting for the next poll tick.
        ShellIconService.InvalidateRecycleBinState();
        _recycleStateInit = false;
        foreach (var item in _vm.Items)
        {
            if (item.Type == ItemType.ShellLocation && ShellIconService.IsRecycleBin(item.TargetPath))
                item.RefreshIcon();
        }
    }
    void ItemOpenLocation_Click(object s, RoutedEventArgs e) { if (VM(s) is not ZoneItemViewModel v) return; if (v.Type == ItemType.ShellLocation) { ShellLocationResolver.Open(v.TargetPath, v.Type); return; } if (v.Type is ItemType.Shortcut or ItemType.Application) { var d = Path.GetDirectoryName(v.TargetPath); if (!string.IsNullOrEmpty(d)) System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{v.TargetPath}\""); } else System.Diagnostics.Process.Start("explorer.exe", v.TargetPath); }
    void ItemRename_Click(object s, RoutedEventArgs e)
    {
        if (VM(s) is not ZoneItemViewModel v) return;
        var rn = new Views.RenameDialog(v.Name) { Owner = this };
        if (rn.ShowDialog() == true) { v.Name = rn.NewName; _mgr.SaveConfig(); }
    }
    void ItemDelete_Click(object s, RoutedEventArgs e) { if (VM(s) is ZoneItemViewModel v) _vm.DeleteItemCommand.Execute(v); }

    static ZoneItemViewModel? VM(object s) => s is MenuItem mi && mi.Parent is ContextMenu cm && cm.PlacementTarget is FrameworkElement fe && fe.DataContext is ZoneItemViewModel vm ? vm : null;
    static void Open(ZoneItemViewModel v)
    {
        try { ShellLocationResolver.Open(v.TargetPath, v.Type); }
        catch (Exception ex)
        {
            var loc = LocalizationService.Instance;
            MessageBox.Show($"{loc["Item.FailedToOpen"]}\n{ex.Message}", loc["Item.FailedToOpen.Title"], MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ── Style (CRITICAL: updates _zone reference) ──

    /// <summary>
    /// Pure-data result of style resolution: every field needed to render a zone window,
    /// already merged for the current mode (regular / merged-master-unified /
    /// merged-master-keep-original / merged-subzone-standalone). Decouples "what should we
    /// render?" from "how do we render it?" — mode branching lives ONLY in
    /// <see cref="ResolveStyle"/>, UI application lives ONLY in <see cref="ApplyStyle"/>.
    /// </summary>
    public record ResolvedZoneStyle(
        string FillColor,
        string BorderColor,
        double BorderThickness,
        string TitleBarFillColor,
        string TitleTextColor,
        string IconColor,
        double ControlOpacity,
        int CornerRadius,
        bool QuickBarMode,
        bool TitleBarAdaptive,
        string BgImagePath,
        string BgImageStretch,
        double BgImageOffsetX,
        double BgImageOffsetY,
        double BgImageZoom,
        double BgImageOpacity,
        bool TitleBarFillIndependent);

    /// <summary>
    /// Resolve the visual style for the current mode. This is the ONLY place that knows
    /// about merged-group logic — every other method takes the result and renders blindly.
    /// Mode precedence (highest first):
    ///   1. Regular zone                              → _zone.*  (or global when useGlobal)
    ///   2. Merged master + Unified                   → _zone.MergedGroup*
    ///   3. Merged master + Keep Original + sub-zone  → selectedSubZone.*
    ///   4. Merged master + Keep Original + no sub    → _zone.*  (master's own)
    ///   5. Merged sub-zone standalone + Unified      → _zone.MergedGroup*
    ///   6. Merged sub-zone standalone + Keep Original → _zone.*
    /// TitleBarAdaptive MUST follow the same source as the colors it adapts to; otherwise
    /// adaptive would compute a contrasting color for a different background.
    /// </summary>
    ResolvedZoneStyle ResolveStyle()
    {
        // Step 1: regular zone defaults.
        var regular = new ResolvedZoneStyle(
            FillColor:        _zone.FillColor,
            BorderColor:      _zone.BorderColor,
            BorderThickness:  _zone.BorderThickness,
            TitleBarFillColor: _zone.TitleBarFillColor,
            TitleTextColor:   _zone.TitleTextColor,
            IconColor:        _zone.IconColor,
            ControlOpacity:   _zone.ControlOpacity,
            CornerRadius:     _zone.CornerRadius,
            QuickBarMode:     _zone.QuickBarMode,
            TitleBarAdaptive: _zone.TitleBarTextColorAdaptive,
            BgImagePath:      _zone.BackgroundImagePath,
            BgImageStretch:   _zone.BgImageStretch,
            BgImageOffsetX:   _zone.BgImageOffsetX,
            BgImageOffsetY:   _zone.BgImageOffsetY,
            BgImageZoom:      _zone.BgImageZoom,
            BgImageOpacity:   _zone.BackgroundImageOpacity,
            TitleBarFillIndependent: _zone.TitleBarFillIndependent);

        // Step 2: merged-group override.
        bool isMerged = _zone.MergedGroupMembership.SubZoneIds.Count > 0 || _zone.MergedGroupMembership.GroupId.HasValue;
        if (!isMerged) return regular;

        // Merged + Unified (master or sub-zone standalone) → _zone.MergedGroup*
        if (_zone.MergedGroupStyle.UseUnifiedFill)
        {
            return regular with
            {
                FillColor =        _zone.MergedGroupStyle.FillColor,
                BorderColor =      _zone.MergedGroupStyle.BorderColor,
                BorderThickness =  _zone.MergedGroupStyle.BorderThickness,
                TitleBarFillColor = _zone.MergedGroupStyle.TitleBarFillColor,
                TitleTextColor =   _zone.MergedGroupStyle.TitleTextColor,
                IconColor =        _zone.MergedGroupStyle.IconColor,
                ControlOpacity =   _zone.MergedGroupStyle.ControlOpacity,
                CornerRadius =     _zone.MergedGroupStyle.CornerRadius,
                QuickBarMode =     _zone.MergedGroupStyle.QuickBarMode,
                TitleBarAdaptive = _zone.MergedGroupStyle.TitleBarTextColorAdaptive,
                BgImagePath =      _zone.MergedGroupStyle.BackgroundImagePath,
                BgImageStretch =   _zone.MergedGroupStyle.BgImageStretch,
                BgImageOffsetX =   _zone.MergedGroupStyle.BgImageOffsetX,
                BgImageOffsetY =   _zone.MergedGroupStyle.BgImageOffsetY,
                BgImageZoom =      _zone.MergedGroupStyle.BgImageZoom,
                BgImageOpacity =   _zone.MergedGroupStyle.BackgroundImageOpacity,
                TitleBarFillIndependent = _zone.MergedGroupStyle.TitleBarFillIndependent,
            };
        }

        // Merged + Keep Original + master + sub-zone selected → selectedSubZone.*
        bool isMaster = _zone.MergedGroupMembership.SubZoneIds.Count > 0;
        if (isMaster && _vm?.SelectedSubZoneId is Guid selId && selId != _zone.Id)
        {
            var sub = _mgr.Zones.FirstOrDefault(z => z.Id == selId);
            if (sub != null)
            {
                return regular with
                {
                    FillColor =        sub.FillColor,
                    BorderColor =      sub.BorderColor,
                    BorderThickness =  sub.BorderThickness,
                    TitleBarFillColor = sub.TitleBarFillColor,
                    TitleTextColor =   sub.TitleTextColor,
                    IconColor =        sub.IconColor,
                    ControlOpacity =   sub.ControlOpacity,
                    CornerRadius =     sub.CornerRadius,
                    QuickBarMode =     sub.QuickBarMode,
                    TitleBarAdaptive = sub.TitleBarTextColorAdaptive,
                    BgImagePath =      sub.BackgroundImagePath,
                    BgImageStretch =   sub.BgImageStretch,
                    BgImageOffsetX =   sub.BgImageOffsetX,
                    BgImageOffsetY =   sub.BgImageOffsetY,
                    BgImageZoom =      sub.BgImageZoom,
                    BgImageOpacity =   sub.BackgroundImageOpacity,
                    TitleBarFillIndependent = sub.TitleBarFillIndependent,
                };
            }
        }

        // Merged + Keep Original + (master's own items OR sub-zone standalone) → _zone.*
        return regular;
    }

    /// <summary>
    /// Apply the resolved style to the window. Pure UI — no mode branching. All decisions
    /// about which color source to use have already been made by <see cref="ResolveStyle"/>.
    /// </summary>
    public void ApplyStyle()
    {
        var s = ResolveStyle();
        // Acrylic
        ApplyAcrylic(s.FillColor, s.TitleBarFillColor);

        // Borders + corners
        try { ZoneBorder.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(s.BorderColor)!); } catch { }
        ZoneBorder.BorderThickness = new Thickness(s.BorderThickness);
        MainContent.CornerRadius = new CornerRadius(s.CornerRadius);
        ZoneBorder.CornerRadius = new CornerRadius(s.CornerRadius);
        // ponytail 2026-08-26: the title bar and bottom bar carry their own
        // hardcoded corner radii in XAML. They were never updated here, so
        // switching to 尖角 left rounded top/bottom edges behind ("不能完全
        // 转化尖角"). Drive them from the same resolved radius now.
        TitleBarBg.CornerRadius = new CornerRadius(s.CornerRadius, s.CornerRadius, 0, 0);
        if (BottomBarBg != null)
            BottomBarBg.CornerRadius = new CornerRadius(0, 0, s.CornerRadius, s.CornerRadius);

        // ponytail 2026-08-26: keep the OS (DWM) corner preference in lockstep.
        // radius 0 → DONOTROUND so Win11 stops clipping the sharp WPF corners.
        // Guarded: ApplyStyle also runs in the constructor before the HWND
        // exists, where WindowInteropHelper.Handle would throw.
        if (PresentationSource.FromVisual(this) != null)
            NativeMethods.SetRoundedCorners(this, s.CornerRadius);

        // Body fill
        try { FillRect.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(s.FillColor)!); } catch { }
        bool fillIndependent = s.TitleBarFillIndependent && !s.QuickBarMode;
        FillRect.RadiusX = FillRect.RadiusY = fillIndependent ? 0 : s.CornerRadius;
        FillRect.Margin = fillIndependent ? new Thickness(0, 24, 0, 0) : new Thickness(0);

        // Title bar fill
        try { TitleBarBg.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(s.TitleBarFillColor)!); } catch { }

        // Title text — adaptive on → composite + HSL flip; off → resolved TitleTextColor
        if (s.TitleBarAdaptive)
        {
            // ponytail: TitleBarFillColor is a translucent overlay over FillColor. Composite
            // before HSL flip so the algorithm sees the visible title-bar color, not the
            // bare translucent layer.
            var tBrush = AdaptiveTextColor.ResolveBrushOver(s.TitleBarFillColor, s.FillColor);
            ZoneTitleText.Foreground = tBrush;
        }
        else
        {
            try { ZoneTitleText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(s.TitleTextColor)!); } catch { }
        }

        // Icon + ControlPoint button labels — adaptive on → same brush as title; off → icons use
        // the resolved IconColor (falling back to the resolved title text color, always set by
        // ResolveStyle) and the button labels return to their XAML default #80FFFFFF.
        if (s.TitleBarAdaptive)
        {
            var iBrush = AdaptiveTextColor.ResolveBrushOver(s.TitleBarFillColor, s.FillColor);
            TitleIconChar.Foreground = iBrush;
            RestoreIconChar.Foreground = iBrush;
            // ponytail: Border has no Foreground property — only the inner TextBlocks can carry
            // the adaptive brush. Border.Background stays at its hardcoded #30FFFFFF.
            LockBtnText.Foreground = iBrush;
            EditBtnText.Foreground = iBrush;
            ImportBtnText.Foreground = iBrush;
            HideBtnText.Foreground = iBrush;
        }
        else
        {
            var iconColor = !string.IsNullOrEmpty(s.IconColor) ? s.IconColor : s.TitleTextColor;
            try
            {
                var ic = new SolidColorBrush((Color)ColorConverter.ConvertFromString(iconColor)!);
                TitleIconChar.Foreground = ic;
                RestoreIconChar.Foreground = ic;
            }
            catch
            {
                TitleIconChar.Foreground = Brushes.Transparent;
                RestoreIconChar.Foreground = Brushes.Transparent;
            }
            // ponytail: the adaptive branch above already overwrote these once, so the XAML
            // default can't come back on its own when the toggle flips off (live preview calls
            // ApplyStyle again) — restore the hardcoded #80FFFFFF explicitly.
            LockBtnText.Foreground = CtrlLabelDefaultBrush;
            EditBtnText.Foreground = CtrlLabelDefaultBrush;
            ImportBtnText.Foreground = CtrlLabelDefaultBrush;
            HideBtnText.Foreground = CtrlLabelDefaultBrush;
        }

        // Control-point opacity + QuickBar visibility
        ControlPoint.Opacity = Math.Max(0.05, s.ControlOpacity / 100.0);
        var vis = s.QuickBarMode ? Visibility.Collapsed : Visibility.Visible;
        TitleBarBg.Visibility = vis;
        ControlPoint.Visibility = vis;

        // Background image
        ApplyBackgroundImage(s);

        // Sub-zone tabs + items — both driven by the resolved style so adaptive decision
        // is in lockstep with the resolved colors above (no separate "MergedGroup*" flag
        // check that could fall out of sync).
        SolidColorBrush? tabAdaptiveBrush = s.TitleBarAdaptive
            ? AdaptiveTextColor.ResolveBrushOver(s.TitleBarFillColor, s.FillColor)
            : null;
        RebuildSubZoneTabs(tabAdaptiveBrush, s.TitleTextColor);
        ApplyItemTextColorAdaptive(s.FillColor);
    }

    void ApplyBackgroundImage(ResolvedZoneStyle s)
    {
        // 标题栏独立填充：背景图与 FillRect 一样不铺到标题栏下方（顶部裁剪）。
        double clipTop = s.TitleBarFillIndependent && !s.QuickBarMode ? 24 : 0;
        BgImageBorder.Margin = new Thickness(0, clipTop, 0, 0);
        if (!string.IsNullOrEmpty(s.BgImagePath) && File.Exists(s.BgImagePath))
        {
            try
            {
                var bi = new System.Windows.Media.Imaging.BitmapImage();
                bi.BeginInit();
                bi.UriSource = new Uri(s.BgImagePath);
                bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bi.DecodePixelWidth = 1920;
                bi.EndInit();
                bi.Freeze();
                BgImage.Source = bi;
                BgImage.Stretch = Stretch.UniformToFill;

                var bw = ActualWidth > 0 ? ActualWidth : _zone.Width;
                var bh = ActualHeight > 0 ? ActualHeight : _zone.Height;

                double imgW = bi.PixelWidth;
                double imgH = bi.PixelHeight;
                double utfScale = Math.Max((bw * s.BgImageZoom) / imgW, (bh * s.BgImageZoom) / imgH);
                double displayedW = imgW * utfScale;
                double displayedH = imgH * utfScale;

                BgImage.Width = displayedW;
                BgImage.Height = displayedH;

                double zoneCenterX = bw / 2;
                double zoneCenterY = bh / 2;
                double imgCenterX = displayedW / 2;
                double imgCenterY = displayedH / 2;
                double zox = s.BgImageOffsetX;
                double zoy = s.BgImageOffsetY;

                BgImage.Margin = new Thickness(
                    zoneCenterX - imgCenterX + zox,
                    zoneCenterY - imgCenterY + zoy - clipTop, 0, 0);
                BgImage.HorizontalAlignment = HorizontalAlignment.Left;
                BgImage.VerticalAlignment = VerticalAlignment.Top;
                BgImage.Opacity = Math.Max(0.01, s.BgImageOpacity / 100.0);
            }
            catch { BgImage.Opacity = 0; }
        }
        else { BgImage.Source = null; BgImage.Opacity = 0; }
    }

    /// <summary>Walk the item template subtree under <see cref="MainContent"/> and apply the
    /// adaptive text brush. Uses the same <see cref="AdaptiveTextColor.ApplyBrushToTree"/>
    /// helper PanelWindow does, so behavior is identical across widgets — no special-case
    /// ItemContainerGenerator timing races. The title bar is brushed separately by
    /// <see cref="ApplyStyle"/> before this call, so we scope the walk to the ScrollViewer
    /// subtree that hosts the items to avoid clobbering title bar brushes.
    /// No-op when <see cref="Zone.TextColorAdaptive"/> is false.
    /// When the zone has a background image, samples 5 points from it instead of using FillColor.
    /// Pass <paramref name="effectiveFill"/> when the caller has already resolved it (e.g. merged-group
    /// unified fill); otherwise we resolve from <see cref="Zone.FillColor"/> or global.</summary>
    public void ApplyItemTextColorAdaptive(string? effectiveFill = null)
    {
#if DEBUG
        System.Diagnostics.Debug.WriteLine(
            $"[adaptive] ZoneWindow ({_zone.Name}): bg={effectiveFill ?? ResolveEffectiveBodyFill()} adaptive={_zone.TextColorAdaptive}");
#endif
        if (!_zone.TextColorAdaptive) return;
        string fillColor = effectiveFill ?? ResolveEffectiveBodyFill();
        SolidColorBrush brush;
        if (BgImage?.Source is BitmapSource bmp && !string.IsNullOrEmpty(_zone.BackgroundImagePath))
        {
            brush = AdaptiveTextColor.ResolveBrush(AdaptiveTextColor.ResolveTextColorForImage(bmp));
        }
        else
        {
            brush = AdaptiveTextColor.ResolveBrush(fillColor);
        }
        // ponytail: walk ItemsHost subtree directly via visual tree, mirroring PanelWindow's
        // pattern over ContentStack.Children. The previous ContainerFromIndex approach raced
        // with ItemContainerGenerator status — containers would be null right after RefreshItems
        // wiped and re-added items, silently dropping every brush assignment. Visual tree walk
        // picks up whatever containers WPF has realized so far, and ItemsHost never collapses
        // in zone/MG modes, so no MainContent-visibility guard is needed.
        if (ItemsHost != null)
            AdaptiveTextColor.ApplyBrushToTree(ItemsHost, brush);
    }

    /// <summary>Resolve the effective body fill, mirroring ApplyStyle's merged-group branch:
    /// Unified mode → MergedGroupStyle.FillColor; Keep Original + sub-zone selected → that
    /// sub-zone's FillColor; otherwise zone.FillColor or global.</summary>
    string ResolveEffectiveBodyFill()
    {
        if (_zone.MergedGroupMembership.SubZoneIds.Count > 0 || _zone.MergedGroupMembership.GroupId.HasValue)
        {
            if (_zone.MergedGroupStyle.UseUnifiedFill)
                return _zone.MergedGroupStyle.FillColor;
            // ponytail: Keep Original + sub-zone selected — the visible body fill is the
            // sub-zone's FillColor (ApplyStyle sets FillRect.Fill from it), not the master's.
            // Returning master here made the StatusChanged hook brush items against the
            // wrong color after any path that re-fires the generator.
            if (_vm.SelectedSubZoneId.HasValue && _vm.SelectedSubZoneId.Value != _zone.Id)
            {
                var subZone = _mgr.Zones.FirstOrDefault(z => z.Id == _vm.SelectedSubZoneId.Value);
                if (subZone != null)
                    return subZone.FillColor;
            }
        }
        return _zone.FillColor;
    }

    /// <summary>Re-apply both body and title bar adaptive text colors. Called from
    /// settings dialog live preview when toggles change.</summary>
    public void RefreshTextColorAdaptive()
    {
        ApplyStyle();
    }

    void SetRestoreIcon()
    {
        // For merged groups, prefer MergedGroupMembership.Icon; otherwise use IconChar
        string iconChar = _zone.MergedGroupMembership.SubZoneIds.Count > 0 && !string.IsNullOrEmpty(_zone.MergedGroupMembership.Icon)
            ? _zone.MergedGroupMembership.Icon : _zone.IconChar;
        var icon = string.IsNullOrEmpty(iconChar) ? (string.IsNullOrEmpty(_zone.Name) ? "⊞" : _zone.Name[..1]) : iconChar;
        RestoreIconChar.Text = icon;
        TitleIconChar.Text = string.IsNullOrEmpty(iconChar) ? icon : iconChar;
    }
    void OnSize(object s, SizeChangedEventArgs e) { if (!IsLoaded || MainContent.Visibility != Visibility.Visible) return; _zone.Width = Width; _zone.Height = Height; ScheduleSave(); RearrangeAll(); UpdateCanvasSize(); NativeMethods.UpdateRoundedCorners(this, (int)_zone.CornerRadius); }

    void ScheduleSave() { _savePending = true; _saveDebounce.Stop(); _saveDebounce.Start(); }

    void UpdateCanvasSize()
    {
        if (_itemCanvas == null) return;

        // Use the actually displayed items list (sub-zone's items when a sub-zone tab is selected)
        List<Models.ZoneItem> displayItems;
        if (_zone.MergedGroupMembership.SubZoneIds.Count > 0 && _vm.SelectedSubZoneId.HasValue && _vm.SelectedSubZoneId.Value != _zone.Id)
        {
            var subZone = _mgr.Zones.FirstOrDefault(z => z.Id == _vm.SelectedSubZoneId.Value);
            displayItems = subZone?.Items ?? _zone.Items;
        }
        else
        {
            displayItems = _zone.Items;
        }

        if (displayItems.Count == 0) { _itemCanvas.Width = Math.Max(0, _zone.Width - 2); _itemCanvas.Height = Math.Max(0, _zone.Height - 50); return; }
        double maxX = 0, maxY = 0;
        foreach (var i in displayItems) { if (i.X + 80 > maxX) maxX = i.X + 80; if (i.Y + 96 > maxY) maxY = i.Y + 96; }
        _itemCanvas.Width = Math.Max(_zone.Width - 20, maxX + 20);
        _itemCanvas.Height = Math.Max(_zone.Height - 50, maxY + 20);
    }

    // ── Acrylic / frosted glass ──
    /// <summary>
    /// ponytail: ghost-glass fix — re-enable liquid glass when the zone expands from the
    /// RestoreButton (hover preview or click). Routes through ApplyAcrylic so the
    /// expanded-state gate stays the single source of truth for EnableBlur.
    /// </summary>
    void ReapplyAcrylic()
    {
        var s = ResolveStyle();
        ApplyAcrylic(s.FillColor, s.TitleBarFillColor);
    }

    void ApplyAcrylic(string fillColor, string titleBarFillColor)
    {
        // ponytail: ghost-glass fix — a collapsed zone keeps its full-size window (only the
        // RestoreButton is visible), so enabling acrylic here would tint the WHOLE window
        // bounds with a ghost glass rectangle. Only enable blur while the content is
        // expanded; whenever collapsed (or mid-collapse), disable it instead.
        bool expanded = _hover?.IsExpanded ?? false;
        if (_zone.EnableAcrylic && expanded)
        {
            var blurResult = AcrylicHelper.EnableBlur(this, _zone.GlassBlurAmount, _zone.GlassTintOpacity, _zone.GlassTintLuminosity, _zone.GlassColorMode);
            if (!blurResult.Success)
                System.Diagnostics.Debug.WriteLine($"[ZoneWindow] EnableBlur failed: {blurResult.Error}");
            try
            {
                var tint = (Color)ColorConverter.ConvertFromString(fillColor)!;
                FillRect.Fill = new SolidColorBrush(tint);
                FillRect.Opacity = 1.0; // Brush alpha from FillColor controls transparency
                if (TitleBarBg != null && !string.IsNullOrEmpty(titleBarFillColor))
                {
                    try
                    {
                        TitleBarBg.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(titleBarFillColor)!);
                    }
                    catch
                    {
                        TitleBarBg.Background = new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF));
                    }
                }
            }
            catch
            {
                if (FillRect != null) FillRect.Fill = new SolidColorBrush(Color.FromArgb(0x04, 0x00, 0x00, 0x00));
            }
        }
        else
        {
            AcrylicHelper.DisableBlur(this);
            try
            {
                FillRect.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fillColor)!);
                FillRect.Opacity = 1.0;
                TitleBarBg.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(titleBarFillColor)!);
            }
            catch { }
        }
    }

    static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            var result = FindVisualChild<T>(child);
            if (result != null) return result;
        }
        return null;
    }
    static bool IsOnItem(object s) { var c = s as DependencyObject; while (c != null) { if (c is Grid g && g.DataContext is ZoneItemViewModel) return true; c = VisualTreeHelper.GetParent(c); } return false; }

    public void RefreshZone(Zone zone)
    {
        _zone = zone; // ← KEY FIX: update the reference
        // ponytail: skip _vm.RefreshZone (Items.Clear/Add). Items don't actually change
        // in this path — PushToZone/preset-apply only touch style fields, CopyZoneFields
        // doesn't copy Items. The Clear/Add race with ApplyStyle's ApplyBrushToTree is
        // the reason item names "stuck on previous color" — WPF defers container
        // generation to the next layout pass, so the walk runs before new TextBlocks
        // exist. Actual item add/remove/rename goes through OnZonesChanged which uses
        // Dispatcher.BeginInvoke (Fix C). Updating VM.Zone keeps its binding consumers
        // (SourceZoneId et al.) happy without touching the Items collection.
        _vm.Zone = zone;
        ZoneTitleText.Text = zone.Name;
        SetRestoreIcon();
        // ponytail: ApplyStyle rebuilds sub-zone tabs internally with the resolved adaptive
        // brush — no separate RebuildSubZoneTabs call needed here.
        ApplyStyle();
        UpdateMergedTitle();
        if (zone.IsVisible) ShowZone(); else ApplyHidden();
        // ponytail: run last so it overrides ShowZone/ApplyHidden when HoverAutoExpand=true.
        // Otherwise the post-refresh Width/Height would restore the full-size window and
        // hide the RestoreButton the user is supposed to hover.
        _hover?.SetEnabled(zone.EnableRestoreButton);
    }

    // ── Inline title editing ──

    void ZoneTitle_PreviewMouseLeftButtonDown(object s, MouseButtonEventArgs e)
    {
        // Merged: the title text is the master label (above the sub-zone tabs), so a
        // click switches back to the master view. Not merged: leave the click alone so
        // the TextBox can start an inline rename.
        if (_zone.MergedGroupMembership.SubZoneIds.Count > 0)
        {
            SelectSubZone(_zone.Id);
            e.Handled = true;
        }
    }

    void ZoneTitle_LostFocus(object s, RoutedEventArgs e)
    {
        var text = ZoneTitleText.Text?.Trim() ?? "";
        if (!string.IsNullOrEmpty(text) && text != _zone.Name)
        {
            _zone.Name = text;
            _mgr.SaveConfig();
        }
        ZoneTitleText.Text = _zone.Name;
    }

    void ZoneTitle_KeyDown(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            var text = ZoneTitleText.Text?.Trim() ?? "";
            if (!string.IsNullOrEmpty(text) && text != _zone.Name)
            {
                _zone.Name = text;
                _mgr.SaveConfig();
            }
            ZoneTitleText.Text = _zone.Name;
            // Move focus away
            FocusManager.SetFocusedElement(FocusManager.GetFocusScope(ZoneTitleText), this);
            e.Handled = true;
        }
    }

    protected override void OnActivated(EventArgs e) { base.OnActivated(e); }

    // ── Merge support ──

    void UpdateMergedTitle()
    {
        if (_zone.MergedGroupMembership.SubZoneIds.Count > 0)
        {
            if (!string.IsNullOrEmpty(_zone.MergedGroupMembership.DisplayName))
                ZoneTitleText.Text = _zone.MergedGroupMembership.DisplayName;
            ZoneTitleText.IsReadOnly = true;
            ZoneTitleText.Cursor = Cursors.Arrow;
        }
        else
        {
            ZoneTitleText.IsReadOnly = false;
            ZoneTitleText.Cursor = Cursors.IBeam;
        }
    }

    void RebuildSubZoneTabs(SolidColorBrush? adaptiveBrush = null, string? titleTextColor = null)
    {
        SubZoneTabs.Children.Clear();
        if (_zone.MergedGroupMembership.SubZoneIds.Count == 0)
        {
            SubZoneTabsRow.Visibility = Visibility.Collapsed;
            CtxDisbandAll.Visibility = Visibility.Collapsed;
            CtxDisbandThis.Visibility = Visibility.Collapsed;
            if (CtxMergeSep != null) CtxMergeSep.Visibility = Visibility.Collapsed;
            return;
        }

        SubZoneTabsRow.Visibility = Visibility.Visible;
        CtxDisbandAll.Visibility = Visibility.Visible;
        // Show "Disband This" for sub-zones (not master)
        bool isMaster = _zone.MergedGroupMembership.SubZoneIds.Count > 0;
        CtxDisbandThis.Visibility = isMaster ? Visibility.Collapsed : Visibility.Visible;
        if (CtxMergeSep != null) CtxMergeSep.Visibility = Visibility.Visible;

        // Display order over ALL members (master + subs). Every tab is draggable
        // for reordering; the master tab is just another label (its window role is
        // unchanged wherever it sits). Legacy configs get normalized to master-first.
        var order = _zone.MergedGroupMembership.TabOrder;
        if (order.Count != _zone.MergedGroupMembership.SubZoneIds.Count + 1
            || !order.Contains(_zone.Id)
            || _zone.MergedGroupMembership.SubZoneIds.Any(id => !order.Contains(id)))
        {
            order.Clear();
            order.Add(_zone.Id);
            order.AddRange(_zone.MergedGroupMembership.SubZoneIds);
        }

        foreach (var id in order)
        {
            var z = id == _zone.Id ? _zone : _mgr.Zones.FirstOrDefault(x => x.Id == id);
            if (z != null)
                AddSubZoneTab(z.Id, z.Name, z.IconChar, adaptiveBrush, titleTextColor);
        }
    }

    void AddSubZoneTab(Guid zoneId, string name, string iconChar, SolidColorBrush? adaptiveBrush, string? titleTextColorOverride)
    {
        var cn = _loc.CurrentLanguage == "zh";
        bool isSelected = _vm.SelectedSubZoneId == zoneId;

        // ponytail: mirror ZoneTitleText resolution exactly — adaptive on → adaptive brush,
        // adaptive off → resolved titleTextColor (master's MergedGroupStyle.TitleTextColor in merged
        // mode). No hardcoded hex fallback; if override is empty/malformed, fall through to
        // Transparent so WPF inherits instead of snapping to white.
        Brush textBrush;
        if (adaptiveBrush != null)
        {
            textBrush = adaptiveBrush;
        }
        else if (!string.IsNullOrEmpty(titleTextColorOverride))
        {
            try { textBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(titleTextColorOverride)!); }
            catch { textBrush = Brushes.Transparent; }
        }
        else
        {
            textBrush = Brushes.Transparent;
        }

        var tab = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(1, 0, 1, 0),
            Cursor = Cursors.Hand,
            Tag = zoneId,
            RenderTransform = new TranslateTransform(),
            // All tabs are identical — click to switch, drag to reorder, drag out to detach.
            ToolTip = cn ? "点击切换到此分区；拖拽可调序，拖出标签条可分离" : "Click to switch; drag to reorder, drag out to detach"
        };

        var sp = new StackPanel { Orientation = Orientation.Horizontal };

        if (!string.IsNullOrEmpty(iconChar))
        {
            sp.Children.Add(new TextBlock
            {
                Text = iconChar,
                FontSize = 10,
                Foreground = textBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 3, 0)
            });
        }

        sp.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = 10,
            Foreground = textBrush,
            VerticalAlignment = VerticalAlignment.Center
        });

        if (isSelected)
        {
            tab.Background = new SolidColorBrush(Color.FromArgb(0x30, 0x00, 0x00, 0x00));
        }
        else
        {
            tab.Background = new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF));
        }

        tab.MouseLeftButtonDown += SubZoneTab_MouseDown;
        tab.Child = sp;
        SubZoneTabs.Children.Add(tab);
    }

    void SubZoneTab_MouseDown(object s, MouseButtonEventArgs e)
    {
        var vm = DataContext as ZoneViewModel;
        if (vm?.IsLocked == true) return;
        if (s is not Border tab || tab.Tag is not Guid zoneId) return;
        if (_zone.MergedGroupMembership.SubZoneIds.Count == 0) return;

        _dragTab = tab;
        _dragTabZoneId = zoneId;
        _dragTabOrigin = e.GetPosition(SubZoneTabsRow);
        _dragTabFromIndex = SubZoneTabs.Children.IndexOf(tab);
        _dragTabInsertIndex = _dragTabFromIndex;
        _dragTabArmed = false;
        _dragTabCompleted = false;
        _isDragTabOut = false;

        // Visible drag: the tab follows the cursor. Reset any leftover slide-transform
        // first so the layout origin below is the pure layout position.
        if (tab.RenderTransform is TranslateTransform tt)
        {
            tt.BeginAnimation(TranslateTransform.XProperty, null);
            tt.X = 0;
        }
        int childIndex = SubZoneTabs.Children.IndexOf(tab);
        _dragTabGrabOffset = _dragTabOrigin.X - SubZoneTabs.Margin.Left - TabLayoutOriginInStack(childIndex);
        Panel.SetZIndex(tab, 10); // render the dragged tab above its neighbours

        StartTabDragTimer();
        e.Handled = true;
    }

    void SelectSubZone(Guid zoneId)
    {
        // Re-clicking the visible tab is a no-op (also avoids a redundant re-animate).
        if (_vm.SelectedSubZoneId == zoneId) return;

        int oldIndex = GetTabOrderIndex(_vm.SelectedSubZoneId);
        int newIndex = GetTabOrderIndex(zoneId);
        if (oldIndex < 0 || newIndex < 0 || oldIndex == newIndex)
        {
            ApplySubZoneSwitch(zoneId);
            return;
        }

        // Directional two-phase switch (mirrors PropertyPanel.AnimateSwitch's fade +
        // slide with Motion resources). Clicking a left tab slides the current content
        // out to the right and the new content in from the left; a right tab → the
        // opposite. dir = out direction (+1 right / -1 left); the in-phase enters from
        // the opposite side.
        int dir = newIndex < oldIndex ? 1 : -1;
        AnimateSubZoneOut(dir, () =>
        {
            ApplySubZoneSwitch(zoneId);
            AnimateSubZoneIn(dir);
        });
    }

    void ApplySubZoneSwitch(Guid zoneId)
    {
        _vm.SelectedSubZoneId = zoneId;
        // ponytail: ApplyStyle rebuilds sub-zone tabs internally with the resolved adaptive
        // brush — no separate RebuildSubZoneTabs / ApplySubZoneTabTextColorAdaptive needed.
        ApplyStyle(); // Apply style based on selected sub-zone (also rebuilds tabs)
        RearrangeAll(); // Rearrange items for the newly selected sub-zone
        UpdateCanvasSize();
    }

    /// <summary>Position of a zone in the merged tab strip (0..n, master included
    /// wherever it sits in the display order). -1 when not part of this group.</summary>
    int GetTabOrderIndex(Guid? zoneId)
    {
        if (zoneId == null) return -1;
        int i = _zone.MergedGroupMembership.TabOrder.IndexOf(zoneId.Value);
        if (i >= 0) return i;
        // Legacy fallback before TabOrder is normalized on first render.
        if (zoneId.Value == _zone.Id) return 0;
        int j = _zone.MergedGroupMembership.SubZoneIds.IndexOf(zoneId.Value);
        return j < 0 ? -1 : j + 1;
    }

    // ── Sub-zone content switch animation ──

    const double SwitchSlideOffset = 16;

    void AnimateSubZoneOut(int dir, Action onCompleted)
    {
        var duration = (Duration)FindResource("Motion.Fast");
        var easing = (IEasingFunction)FindResource("Motion.StandardSpline");
        // Pin the start pose so a rapid second click can't re-animate from a stale
        // hold-end value (same stale-base pattern as HoverExpandBehavior).
        ItemsViewport.BeginAnimation(OpacityProperty, null);
        ItemsViewportTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        ItemsViewport.Opacity = 1;
        ItemsViewportTranslate.X = 0;

        var fade = new DoubleAnimation(1, 0, duration) { EasingFunction = easing };
        var slide = new DoubleAnimation(0, dir * SwitchSlideOffset, duration) { EasingFunction = easing };
        fade.Completed += (_, _) => onCompleted();
        ItemsViewport.BeginAnimation(OpacityProperty, fade);
        ItemsViewportTranslate.BeginAnimation(TranslateTransform.XProperty, slide);
    }

    void AnimateSubZoneIn(int dir)
    {
        var duration = (Duration)FindResource("Motion.Normal");
        var easing = (IEasingFunction)FindResource("Motion.StandardSpline");
        ItemsViewport.BeginAnimation(OpacityProperty, null);
        ItemsViewportTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        ItemsViewport.Opacity = 0;
        ItemsViewportTranslate.X = -dir * SwitchSlideOffset;

        var fade = new DoubleAnimation(0, 1, duration) { EasingFunction = easing };
        var slide = new DoubleAnimation(-dir * SwitchSlideOffset, 0, duration) { EasingFunction = easing };
        ItemsViewport.BeginAnimation(OpacityProperty, fade);
        ItemsViewportTranslate.BeginAnimation(TranslateTransform.XProperty, slide);
    }

    // ── Sub-zone tab drag loop (reorder + drag-out detach, browser-like) ──

    void StartTabDragTimer()
    {
        if (_tabDragTimer != null) return;
        _tabDragTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _tabDragTimer.Tick += OnTabDragTick;
        _tabDragTimer.Start();
    }

    void ResetTabDrag()
    {
        if (_tabDragTimer != null)
        {
            _tabDragTimer.Stop();
            _tabDragTimer.Tick -= OnTabDragTick;
            _tabDragTimer = null;
        }
        if (_dragTab != null)
        {
            _dragTab.Opacity = 1.0;
            Panel.SetZIndex(_dragTab, 0);
        }
        _dragTab = null;
        _dragTabZoneId = Guid.Empty;
        _dragTabOrigin = default;
        _dragTabFromIndex = -1;
        _dragTabInsertIndex = -1;
        _dragTabArmed = false;
        _dragTabCompleted = false;
        _isDragTabOut = false;
    }

    void OnTabDragTick(object? s, EventArgs e)
    {
        var dragTab = _dragTab;
        if (dragTab == null || _dragTabCompleted) return;

        GetCursorPos(out Win32Point pt);
        var screen = new Point(pt.X, pt.Y);
        // PointFromScreen converts physical pixels → DIP row coords, keeping the
        // follow math consistent with GetPosition/layout widths (all DIPs). Mixing
        // raw Win32 pixels with DIPs made the tab's head anchor at the cursor on
        // scaled displays instead of the grab point.
        var pos = SubZoneTabsRow.PointFromScreen(screen);
        var rowBounds = new Rect(0, 0, SubZoneTabsRow.ActualWidth, SubZoneTabsRow.ActualHeight);
        bool outsideRow = !rowBounds.Contains(pos);

        var originScreen = SubZoneTabsRow.PointToScreen(_dragTabOrigin);
        var dx = screen.X - originScreen.X;
        var dy = screen.Y - originScreen.Y;
        if (!_dragTabArmed && (dx * dx + dy * dy) > 25)
            _dragTabArmed = true;

        // Ghost the dragged tab while it's outside the strip (signals "will detach").
        // All tabs behave identically — including the group's host zone.
        if (outsideRow && !_isDragTabOut)
        {
            _isDragTabOut = true;
            dragTab.Opacity = 0.35;
        }
        else if (!outsideRow && _isDragTabOut)
        {
            _isDragTabOut = false;
            dragTab.Opacity = 1.0;
        }

        // Visible drag: the dragged tab tracks the cursor while inside the strip;
        // it snaps back to its slot once the cursor leaves.
        if (dragTab.RenderTransform is TranslateTransform followTt)
        {
            if (_dragTabArmed && !outsideRow)
            {
                int childIndex = SubZoneTabs.Children.IndexOf(dragTab);
                double layoutX = TabLayoutOriginInStack(childIndex);
                followTt.X = pos.X - SubZoneTabs.Margin.Left - _dragTabGrabOffset - layoutX;
            }
            else
            {
                followTt.X = 0;
            }
        }

        // Live reorder while the cursor stays inside the strip.
        if (_dragTabArmed && !outsideRow)
        {
            // Convert row coords → StackPanel coords so boundaries align exactly
            // with the accumulated tab widths below.
            int newIndex = ComputeTabDropIndex(pos.X - SubZoneTabs.Margin.Left);
            if (newIndex >= 0 && newIndex != _dragTabInsertIndex
                && newIndex != _dragTabFromIndex && newIndex != _dragTabFromIndex + 1)
            {
                int target = newIndex;
                if (target > _dragTabFromIndex) target--;
                CaptureTabSlidePositions(Math.Min(_dragTabFromIndex, target), Math.Max(_dragTabFromIndex, target));
                MoveTab(_dragTabFromIndex, target);
                _dragTabFromIndex = target;
                _dragTabInsertIndex = newIndex;
                Dispatcher.BeginInvoke(new Action(PlayTabSlideAnimations),
                    System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }

        // LButton release is detected via Win32 so it works even outside this window.
        if ((GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0) return;

        _dragTabCompleted = true;
        try
        {
            if (_dragTabArmed && outsideRow)
            {
                DetachDraggedTabAt(screen);
                return;
            }

            if (_dragTabArmed)
            {
                // Reorder drop inside the strip — persist the new order.
                _mgr.SaveSubZoneOrder(_zone.Id);
            }
            else
            {
                // Plain click → switch to that sub-zone.
                SelectSubZone(_dragTabZoneId);
            }
        }
        finally
        {
            ResetTabDrag();
        }
    }

    int ComputeTabDropIndex(double x)
    {
        double acc = 0;
        // All tabs (master included) share one uniform index space — no fixed first
        // slot, so midpoint boundaries align with the tabs' real positions and both
        // drag directions behave symmetrically.
        for (int c = 0; c < SubZoneTabs.Children.Count; c++)
        {
            if (SubZoneTabs.Children[c] is not FrameworkElement el) continue;
            var w = el.ActualWidth + el.Margin.Left + el.Margin.Right;
            if (x < acc + w / 2) return c;
            acc += w;
        }
        return SubZoneTabs.Children.Count;
    }

    /// <summary>Layout origin (x) of a tab child inside the SubZoneTabs StackPanel,
    /// computed analytically from siblings' widths so it stays valid regardless of any
    /// slide/follow RenderTransform on the tabs.</summary>
    double TabLayoutOriginInStack(int childrenIndex)
    {
        double x = 0;
        for (int i = 0; i < childrenIndex && i < SubZoneTabs.Children.Count; i++)
        {
            if (SubZoneTabs.Children[i] is FrameworkElement el)
                x += el.ActualWidth + el.Margin.Left + el.Margin.Right;
        }
        return x;
    }

    void MoveTab(int from, int to)
    {
        if (from < 0 || from >= SubZoneTabs.Children.Count) return;
        if (to < 0 || to >= SubZoneTabs.Children.Count) return;
        if (from == to) return;

        var el = SubZoneTabs.Children[from];
        SubZoneTabs.Children.RemoveAt(from);
        SubZoneTabs.Children.Insert(to, el);

        var order = _zone.MergedGroupMembership.TabOrder;
        if (from < order.Count && to < order.Count)
        {
            var id = order[from];
            order.RemoveAt(from);
            order.Insert(to, id);
        }
    }

    void CaptureTabSlidePositions(int from, int to)
    {
        _pendingTabSlide.Clear();
        for (int i = from; i <= to; i++)
        {
            if (i < 0 || i >= SubZoneTabs.Children.Count) continue;
            if (SubZoneTabs.Children[i] is not FrameworkElement el) continue;
            if (ReferenceEquals(el, _dragTab)) continue; // dragged tab follows the cursor instead
            _pendingTabSlide[el] = el.TranslatePoint(new Point(0, 0), SubZoneTabs).X;
        }
    }

    void PlayTabSlideAnimations()
    {
        foreach (var kv in _pendingTabSlide)
        {
            var el = kv.Key;
            if (el.RenderTransform is not TranslateTransform transform) continue;
            var newX = el.TranslatePoint(new Point(0, 0), SubZoneTabs).X;
            var delta = kv.Value - newX;
            if (Math.Abs(delta) < 0.5) continue;
            var anim = new DoubleAnimation(delta, 0, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            transform.BeginAnimation(TranslateTransform.XProperty, anim);
        }
        _pendingTabSlide.Clear();
    }

    void DetachDraggedTabAt(Point screenPos)
    {
        var zoneId = _dragTabZoneId;
        var zone = _mgr.Zones.FirstOrDefault(z => z.Id == zoneId);
        if (zone == null) return;

        // If the detached zone was the currently selected view, fall back to the
        // master view before the group refresh rebuilds the tab strip.
        if (_vm.SelectedSubZoneId == zoneId) _vm.SelectedSubZoneId = _zone.Id;

        // Detach (auto-dissolves when only one member remains). Detaching the group's
        // host zone promotes the first remaining member and re-keys this window to it,
        // so the group window stays on screen while the detached zone pops out below.
        _mgr.DetachZoneAt(zoneId);

        // Show the detached zone at the drop point, title bar centred under the cursor.
        var dpi = VisualTreeHelper.GetDpi(this);
        zone.X = screenPos.X / dpi.DpiScaleX - zone.Width / 2;
        zone.Y = screenPos.Y / dpi.DpiScaleY - 12;
        _mgr.ShowZone(zone);
    }
}
