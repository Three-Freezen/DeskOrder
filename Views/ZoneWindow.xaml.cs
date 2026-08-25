using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
    private readonly ShellIconService _iconService;
    private HwndSource? _src;
    private Canvas? _itemCanvas;
    private Action<string>? _langChanged;
    // Background-image placement transform, refreshed by ApplyBackgroundImage and
    // consumed by the adaptive-color samplers to map window-space sample points
    // back into source-image pixels.
    private double _bgImageScale;
    private double _bgImageOffsetX;
    private double _bgImageOffsetY;

    private bool _dragging;
    private Point _ds, _is;
    private ZoneItemViewModel? _dv;
    private FrameworkElement? _de;
    private System.Windows.Shapes.Rectangle? _dropIndicator;
    private const double BarThickness = 3, BarLength = 56;

    // ── Marquee multi-select (long-press + drag) ──
    // Zone items: hold 350ms → drag draws the marquee (quick drag stays move).
    // Mapping list: hold 350ms on an entry / plain drag on empty list area.
    enum SelectMode { None, Hold, Draw }
    enum SelectTarget { None, ZoneItems, ListItems }
    const double MarqueeHoldMs = 350;
    SelectMode _selectMode;
    SelectTarget _selectTarget;
    Point _selectStart, _selectCurrent;
    bool _selectMoved;
    bool _selectFromEmpty;
    HashSet<Guid>? _selectStartZone;
    HashSet<string>? _selectStartList;
    private System.Windows.Threading.DispatcherTimer? _selectHoldTimer;
    // The item footprint is one grid cell (square); the icon is centered inside it.
    double ItemW => _zone.GridSize;
    double ItemH => _zone.GridSize + ZoneLayout.LabelArea;
    private readonly System.Windows.Threading.DispatcherTimer _saveDebounce = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _savePending;
    private readonly System.Windows.Threading.DispatcherTimer _recycleTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private bool _recycleStateInit;
    private bool _recycleFullLast;
    private HoverExpandBehavior? _hover;
    private SnapDrag? _snapDrag;
    private SnapResize? _snapResize;

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
    private double _dragTabLastX = double.NaN; // previous cursor X — drives the leading-edge probe
    private System.Windows.Threading.DispatcherTimer? _tabDragTimer;
    private readonly Dictionary<FrameworkElement, double> _pendingTabSlide = new();
    // ponytail: extracted from inline lambdas so OnClosed can unsubscribe with the same
    // delegate reference. WPF event -= requires reference equality; lambdas can't be
    // removed once added.
    private readonly EventHandler _itemsHostStatusChangedHandler;
    private readonly System.Collections.Specialized.NotifyCollectionChangedEventHandler _vmItemsChangedHandler;

    // ── Folder mapping ──
    private const int FolderMapMaxEntries = 2000;
    private readonly ObservableCollection<FolderEntryViewModel> _folderEntries = new();
    private CancellationTokenSource? _folderLoadCts;
    private DateTime _lastFolderRefreshUtc = DateTime.MinValue;
    private string _folderLoadedPath = ""; // last successfully loaded path — reload guard

    // ── SubFolder flyout auto-close + drag-hover scale ──
    // ponytail 2026-08-26: 鼠标移出 Flyout 200ms 后自动关闭;移回取消关闭。
    // DragOver 命中 SubFolder 时给容器一个 1.06× 放大反馈(不影响正常换位:
    // 换位路径不调用本 helper,只有命中 SubFolder 的拖拽路径触发)。
    System.Windows.Threading.DispatcherTimer? _flyoutCloseTimer;
    FrameworkElement? _scaledSubfolderContainer;
    double _scaledSubfolderFrom = 1.0;
    bool _flyoutClickOutsideHooked;
    bool _flyoutClosing;
    // 打开世代 token:关动画的 onComplete 只在 token 未变时才真正关 Popup,防止
    // "关闭动画还没放完就又点开了另一个 SubFolder" 时把新开的 Flyout 误关。
    int _flyoutOpenToken;
    // 当前打开的 SubFolder 图标容器 — 供 FlyoutPlacementCallback / 动画原点计算使用。
    FrameworkElement? _flyoutOriginContainer;

    public ZoneWindow(Zone zone, ZoneManager mgr, ShellIconService icons)
    {
        InitializeComponent();
        _zone = zone; _mgr = mgr;
        _iconService = icons;
        // ponytail 2026-08-26: SubfolderItemView's DataContextChanged re-wrap needs
        // an iconService to build ZoneItemViewModel thumbnails; stash it on the
        // view class so the unbound VM can be swapped for a SubfolderItemViewModel
        // when the ItemsControl renders a SubFolder row.
        DesktopZones.Views.Components.SubfolderItemView.IconService = icons;
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
        FolderList.ItemsSource = _folderEntries;
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
            {
                ApplyItemTextColorAdaptive();
                ApplyHideAppName(_zone.HideAppName);
                ApplyCustomIcon(_zone.TileMode && _zone.CustomIcon && _zone.Items.Count == 1);
            }
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
        _snapResize = new SnapResize(this);
        RefreshFolderMapping();
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
        // ponytail 2026-08-25 (Task 6): "New Subfolder" menu entry, mirrored in both
        // ContextMenus. Localization keys Subfolder.New* live in i18n/source.*.json.
        CtxNewSubfolder.Header = _loc["Subfolder.New"];
        CtxNewSubfolder2.Header = _loc["Subfolder.New"];
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
        CtxMapFolder.Header = _loc["FolderMap.MenuMap"];
        CtxPaste.Header = _loc["FolderMap.Paste"];
        FolderMapUpBtn.ToolTip = _loc["FolderMap.Up"];
        FolderMapRefreshBtn.ToolTip = _loc["FolderMap.Refresh"];
        FolderMapCloseBtn.ToolTip = _loc["FolderMap.Disable"];
        EnsureFolderEntryMenu();
        if (_fmMenuOpen != null) _fmMenuOpen.Header = _loc["Item.Open"];
        if (_fmMenuOpenLocation != null) _fmMenuOpenLocation.Header = _loc["Item.OpenLocation"];
        if (_fmMenuOpenExplorer != null) _fmMenuOpenExplorer.Header = _loc["FolderMap.OpenInExplorer"];
        if (_fmMenuRename != null) _fmMenuRename.Header = _loc["FolderMap.Rename"];
        if (_fmMenuDelete != null) _fmMenuDelete.Header = _loc["FolderMap.Delete"];
        FolderMapHintBtn.Content = _loc["FolderMap.ChooseAgain"];
    }

    void OnLoad(object s, RoutedEventArgs e)
    {
        if ((DataContext as ZoneViewModel)?.IsLocked != true) NativeMethods.PinToDesktop(this); NativeMethods.SetToolWindow(this);
        // ponytail 2026-08-26 ghost-ring fix: kill the DWM frame shadow that hugs the
        // collapsed RestoreButton (visible as a dark ring on the wallpaper).
        NativeMethods.DisableDwmFrameShadow(this);
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
    {
        try
        {
            uint n = NativeMethods.DragQueryFile(drop, 0xFFFFFFFF, null, 0);
            var paths = new List<string>();
            for (uint i = 0; i < n; i++)
            {
                var sb = new System.Text.StringBuilder(260);
                NativeMethods.DragQueryFile(drop, i, sb, 260);
                if (!string.IsNullOrEmpty(sb.ToString())) paths.Add(sb.ToString());
            }

            // Mapped-folder mode: dropping files lands them in the mapped folder
            // (copy, never move) and the listing refreshes.
            var (mappingOn, mappingPath) = ResolveFolderMapping();
            if (mappingOn && !string.IsNullOrEmpty(mappingPath) && Directory.Exists(mappingPath))
            {
                var targetDir = mappingPath;
                Task.Run(() =>
                {
                    foreach (var p in paths) CopyInto(p, targetDir);
                    Dispatcher.BeginInvoke(new Action(() => RefreshFolderMapping(forceReload: true)));
                });
                return;
            }

            foreach (var p in paths) { var (sx, sy) = FindFreeSpot(); Add(p, sx, sy); }
            UpdateCanvasSize();
        }
        finally { NativeMethods.DragFinish(drop); }
    }

    /// <summary>Copy a file/directory into the mapped folder with collision-safe
    /// naming ("name (1).ext"). Never overwrites, never deletes.</summary>
    static bool CopyInto(string src, string targetDir)
    {
        try
        {
            if (File.Exists(src))
            {
                File.Copy(src, UniqueDropPath(targetDir, Path.GetFileName(src)), overwrite: false);
                return true;
            }
            if (Directory.Exists(src))
            {
                CopyDirectoryRecursive(src, UniqueDropPath(targetDir, Path.GetFileName(src)));
                return true;
            }
            return false;
        }
        catch { return false; }
    }

    static string UniqueDropPath(string dir, string name)
    {
        var full = Path.Combine(dir, name);
        if (!File.Exists(full) && !Directory.Exists(full)) return full;
        string stem = Path.GetFileNameWithoutExtension(name);
        string ext = Path.GetExtension(name);
        for (int i = 1; ; i++)
        {
            full = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(full) && !Directory.Exists(full)) return full;
        }
    }

    static void CopyDirectoryRecursive(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.EnumerateFiles(src))
            File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), overwrite: false);
        foreach (var d in Directory.EnumerateDirectories(src))
            CopyDirectoryRecursive(d, Path.Combine(dst, Path.GetFileName(d)));
    }

    void Add(string path, double x, double y)
    {
        AddItem(CreateImportedItem(path), x, y);
    }

    /// <summary>Build a ZoneItem from a dropped file/folder path (shortcuts re-associate
    /// to their real target and keep the desktop .lnk's custom icon location — see
    /// ShortcutResolver). Shared by main-zone import and SubFolder import.</summary>
    ZoneItem CreateImportedItem(string path)
    {
        var t = Dir(path) ? ItemType.Folder : Path.GetExtension(path).ToLowerInvariant() switch { ".lnk" => ItemType.Shortcut, ".exe" => ItemType.Application, _ => ItemType.Shortcut };
        (string target, ItemType type, string? iconLoc) = ShortcutResolver.NormalizeItem(path, t);
        return new ZoneItem(Path.GetFileNameWithoutExtension(path), target, type, 0, 0) { IconPath = iconLoc };
    }

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

    // ── External file drops (WPF AllowDrop — shows "not allowed" for non-drags) ──

    void Window_DragEnter(object s, DragEventArgs e)
    {
        // ponytail 2026-08-26: 内部拖拽(ZoneItemViewModel)+ 桌面文件(FileDrop)都先走
        // SubFolder 命中检测,命中则高亮目标方框;未命中再走普通 FileDrop 分支。
        if (TryRouteSubfolderDragOver(e)) return;
        if (e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effects = DragDropEffects.Copy; e.Handled = true; }
    }

    void Window_DragOver(object s, DragEventArgs e)
    {
        if (TryRouteSubfolderDragOver(e)) return;
        if (e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effects = DragDropEffects.Copy; e.Handled = true; }
    }

    void Window_Drop(object s, DragEventArgs e)
    {
        // ponytail 2026-08-26: drop 完成时清除 SubFolder 放大反馈(不管命中与否)。
        ClearSubfolderDragScale();
        var pos = e.GetPosition(ItemsHost);

        // 1) 内部拖拽:ZoneItemViewModel payload。
        if (e.Data.GetData(typeof(ZoneItemViewModel)) is ZoneItemViewModel srcVm)
        {
            // 源当前在某个 SubFolder 内 → 拖出回主分区。
            var fromSub = TryFindOwnerSubfolder(srcVm);
            if (fromSub != null)
            {
                MoveOutOfSubfolder(srcVm, fromSub, pos);
                e.Effects = DragDropEffects.Move;
                e.Handled = true;
                return;
            }

            var target = FindSubfolderTarget(pos);
            if (target != null)
            {
                // 嵌套拒绝:SubFolder 不能拖进另一个 SubFolder。
                if (srcVm.Type == ItemType.SubFolder)
                {
                    e.Effects = DragDropEffects.None;
                    e.Handled = true;
                    return;
                }
                MoveIntoSubfolder(srcVm, target);
                e.Effects = DragDropEffects.Move;
                e.Handled = true;
                return;
            }
            // 落到主分区空白 → 交给 Item_MouseUp 的普通换位路径。
            return;
        }

        // 2) 桌面文件 drop。映射开启 → 复制进映射文件夹；否则导入 SubFolder/主分区。
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files) return;
        var (mappingOn, mappingPath) = ResolveFolderMapping();
        if (mappingOn && !string.IsNullOrEmpty(mappingPath) && Directory.Exists(mappingPath))
        {
            var targetDir = mappingPath;
            Task.Run(() =>
            {
                int done = 0;
                foreach (var f in files) if (CopyInto(f, targetDir)) done++;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    RefreshFolderMapping(forceReload: true);
                    FlashMapStatus(done > 0
                        ? string.Format(_loc["FolderMap.PasteDone"], done)
                        : _loc["FolderMap.PasteNothing"]);
                }));
            });
            e.Handled = true;
            return;
        }
        var fileTarget = FindSubfolderTarget(pos);
        foreach (var f in files)
        {
            if (fileTarget != null) AddFileToSubfolder(f, fileTarget);
            else { var (sx, sy) = FindFreeSpot(); Add(f, sx, sy); }
        }
        UpdateCanvasSize();
        e.Handled = true;
    }

    /// <summary>拖拽悬停路由:命中 SubFolder 时给目标方框放大反馈并设 Effects;
    /// 命中为空/嵌套源时复原反馈并拒绝。内部拖拽(ZoneItemViewModel)直接接管事件,
    /// 桌面文件(FileDrop)只负责高亮、不接管事件(让 DragOver 继续设 Copy)。</summary>
    bool TryRouteSubfolderDragOver(DragEventArgs e)
    {
        if (e.Data.GetData(typeof(ZoneItemViewModel)) is ZoneItemViewModel srcVm)
        {
            // 拖出(源在 SubFolder 内)→ 主分区任意位置都是合法落点。
            if (TryFindOwnerSubfolder(srcVm) != null)
            {
                ClearSubfolderDragScale();
                e.Effects = DragDropEffects.Move;
                e.Handled = true;
                return true;
            }
            var target = FindSubfolderTarget(e.GetPosition(ItemsHost));
            if (target == null)
            {
                ClearSubfolderDragScale();
                e.Effects = DragDropEffects.None; e.Handled = true; return true;
            }
            if (srcVm.Type == ItemType.SubFolder)
            {
                ClearSubfolderDragScale();
                e.Effects = DragDropEffects.None; e.Handled = true; return true;
            }
            SetSubfolderDragScale(FindContainerFor(target));
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            return true;
        }
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var target = FindSubfolderTarget(e.GetPosition(ItemsHost));
            if (target != null) SetSubfolderDragScale(FindContainerFor(target));
            else ClearSubfolderDragScale();
        }
        return false;
    }

    /// <summary>Bounds-based SubFolder 命中检测。不用 InputHitTest,因为鼠标被拖拽图标
    /// 捕获/实时跟随光标时,hit test 会返回被拖的图标本身而不是它底下的 SubFolder。
    /// 直接按 SubFolder 图标在分区里的矩形占位判断,捕获/实时预览期间都稳定。
    /// ponytail: 图标锁死 1×1(取消尺寸自适应),占位永远是一格。</summary>
    ZoneItem? FindSubfolderTarget(Point p)
    {
        if (ItemsHost == null) return null;
        foreach (var vm in _vm.Items)
        {
            if (vm.Type != ItemType.SubFolder) continue;
            var src = vm.Source;
            double w = 56.0;
            double h = 56.0 + ZoneLayout.LabelArea;
            if (p.X >= src.X && p.X <= src.X + w && p.Y >= src.Y && p.Y <= src.Y + h)
                return src;
        }
        return null;
    }

    /// <summary>Move a source ZoneItem (from any zone's Items) into a target SubFolder's
    /// SubItems. Uses the open flyout's AddItem when one is already showing for the
    /// target (so the visual grid updates with the new entry); otherwise writes back
    /// via whole-list replacement of host.SubItems, which fires ZoneItem's INPC.</summary>
    void MoveIntoSubfolder(ZoneItemViewModel srcVm, ZoneItem target)
    {
        var srcItem = ResolveSourceZoneItem(srcVm);
        if (srcItem == null) return;
        if (ReferenceEquals(srcItem, target)) return;

        // 从源分区移除(OnZonesChanged 会顺带刷新 VM 列表)。
        Zone? ownerZone = null;
        foreach (var z in _mgr.Zones)
        {
            if (z.Items.Remove(srcItem)) { ownerZone = z; break; }
        }
        if (ownerZone == null) return;

        AppendToSubfolder(target, srcItem);
        _mgr.ScheduleSaveConfig();
        _mgr.NotifyChanged();
        UpdateCanvasSize();
    }

    /// <summary>Add an already-created ZoneItem to a SubFolder's SubItems (drag-in /
    /// desktop-file-import). Prefers the live flyout VM so the grid refreshes instantly,
    /// else whole-list replacement so INPC still fires.</summary>
    void AppendToSubfolder(ZoneItem target, ZoneItem item)
    {
        var liveVm = SubfolderFlyoutPopup.IsOpen
                     && SubfolderFlyoutView.ViewModel?.HostSubItem.Id == target.Id
                     ? SubfolderFlyoutView.ViewModel
                     : null;
        if (liveVm != null) liveVm.AddItem(item);
        else target.SubItems = new List<ZoneItem>(target.SubItems) { item };
    }

    /// <summary>Import a desktop-dropped file/folder directly into a SubFolder's SubItems.</summary>
    void AddFileToSubfolder(string path, ZoneItem target)
    {
        var item = CreateImportedItem(path);
        AppendToSubfolder(target, item);
        _mgr.ScheduleSaveConfig();
        _mgr.NotifyChanged();
    }

    /// <summary>拖出:把当前位于 <paramref name="fromSub"/>.SubItems 里的项移回主分区
    /// (放到 <paramref name="pos"/> 指定的 grid 位置)。</summary>
    void MoveOutOfSubfolder(ZoneItemViewModel srcVm, ZoneItem fromSub, Point pos)
    {
        var srcItem = fromSub.SubItems.FirstOrDefault(i => i.Id == srcVm.Id);
        if (srcItem == null) return;
        fromSub.SubItems = fromSub.SubItems.Where(i => i.Id != srcVm.Id).ToList();

        // 落点吸附到主分区网格,写入 owner zone 的 Items。
        var owner = OwnerZoneOf(srcVm);
        var targetZone = owner ?? _zone;
        srcItem.X = _zone.SnapToGrid ? ZoneViewModel.SnapToGrid(Math.Max(0, pos.X), targetZone.GridSize) : Math.Max(0, pos.X);
        srcItem.Y = _zone.SnapToGrid ? ZoneViewModel.SnapToGridY(Math.Max(0, pos.Y), targetZone.GridSize) : Math.Max(0, pos.Y);
        targetZone.Items.Add(srcItem);

        _mgr.ScheduleSaveConfig();
        _mgr.NotifyChanged();
        UpdateCanvasSize();
    }

    /// <summary>Find the SubFolder whose SubItems currently contain the item backing
    /// <paramref name="vm"/> (drag-out source), or null when it lives in a zone's Items.</summary>
    ZoneItem? TryFindOwnerSubfolder(ZoneItemViewModel vm)
    {
        foreach (var z in _mgr.Zones)
        {
            foreach (var it in z.Items)
            {
                if (it.Type != ItemType.SubFolder) continue;
                if (it.SubItems.Any(si => si.Id == vm.Id)) return it;
            }
        }
        return null;
    }

    /// <summary>Look up the ZoneItem backing the ZoneItemViewModel by its Id, across
    /// all zones (merged-sub-zone items report the sub-zone id in SourceZoneId).</summary>
    ZoneItem? ResolveSourceZoneItem(ZoneItemViewModel vm)
    {
        foreach (var z in _mgr.Zones)
        {
            var found = z.Items.FirstOrDefault(i => i.Id == vm.Id);
            if (found != null) return found;
        }
        return null;
    }

    // ── Subfolder flyout (Task 6) ──

    /// <summary>PropertyChanged subscription on the host SubFolder ZoneItem. When its
    /// SubItems property fires (drag-in/drag-out, preset apply), SizeInnerGrid must
    /// re-run because the Loaded handler only sizes once. Without this, a 5th item
    /// still renders in a 2×2 grid (Task 5 review).</summary>
    System.ComponentModel.PropertyChangedEventHandler? _subItemsChangedHandler;
    ZoneItem? _subItemsHost;

    void NewSubfolder_Click(object s, RoutedEventArgs e)
    {
        // ponytail: 创建命名弹窗与单个图标重命名同款(RenameDialog),不再用 InputBox。
        var rn = new Views.RenameDialog(_loc["Subfolder.NewDefault"], _loc["Subfolder.NewTitle"]) { Owner = this };
        if (rn.ShowDialog() != true) return;
        string name = rn.NewName.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;

        _mgr.CreateSubfolder(_zone, name);
        UpdateCanvasSize();
    }

    void OpenSubfolderFlyout(ZoneItem sub)
    {
        var token = ++_flyoutOpenToken;
        _flyoutClosing = false;
        var vm = new SubfolderFlyoutViewModel(sub, _iconService);
        SubfolderFlyoutView.ViewModel = vm;

        // SubItems 变化(drag-in/out)后重排内层 UniformGrid。
        if (_subItemsChangedHandler != null && _subItemsHost != null)
            _subItemsHost.PropertyChanged -= _subItemsChangedHandler;
        _subItemsHost = sub;
        _subItemsChangedHandler = (_, args) =>
        {
            if (args.PropertyName == nameof(ZoneItem.SubItems))
                ResizeFlyoutGrid(vm.ItemVms.Count);
        };
        sub.PropertyChanged += _subItemsChangedHandler;

        // 记住图标容器:定位 + 动画原点都要用它的屏幕位置。
        _flyoutOriginContainer = FindContainerFor(sub);

        // 先复位到关闭态(缩放 0 / 不透明 0),避免上次关闭残留的中间态一闪而过。
        ResetFlyoutClosed();
        SubfolderFlyoutPopup.IsOpen = true;

        // 等布局完成后:①按"图标屏幕中心"一次性定死 flyout 的屏幕位置(AbsolutePoint
        // offset,确定函数)②按同一份位置反推 TransformGroup 缩放锚点 ③播打开动画。
        // 展开原点 = 图标中心,每次打开完全一致。click-outside 捕获也延后到这里
        // (Popup 的 HWND/可视树就绪后再 Capture 才可靠)。
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_flyoutOpenToken != token) return; // 已被新的 open/close 取代
            HookFlyoutClickOutside();
            TryOpenFlyoutAnimated(sub);
        }), System.Windows.Threading.DispatcherPriority.Loaded);
        vm.IsOpen = true;
    }

    void TryOpenFlyoutAnimated(ZoneItem sub)
    {
        if (SubfolderFlyoutView.ActualWidth <= 0 || SubfolderFlyoutView.ActualHeight <= 0)
        {
            Dispatcher.BeginInvoke(new Action(() => TryOpenFlyoutAnimated(sub)),
                System.Windows.Threading.DispatcherPriority.ContextIdle);
            return;
        }
        // 位置 + 锚点一次性定死(纯函数:图标位置、flyout 尺寸、工作区):
        //   pos = 图标右上 + 8px,越界翻侧并夹工作区 → AbsolutePoint 的屏幕 offset
        //   c   = 图标中心 - pos → TransformGroup 缩放锚点(以图标为原点)
        var container = _flyoutOriginContainer ?? FindContainerFor(sub);
        var (pos, c) = ComputeFlyoutPosAndAnchor(container, new Size(SubfolderFlyoutView.ActualWidth, SubfolderFlyoutView.ActualHeight));
        SubfolderFlyoutPopup.HorizontalOffset = pos.X;
        SubfolderFlyoutPopup.VerticalOffset = pos.Y;
        SetFlyoutAnchor(c);
        AnimateSubfolderFlyoutOpen();
    }

    /// <summary>确定性展开定位 + 动画原点。返回 (屏幕位置 pos, 缩放锚点 c):
    /// pos 以图标右上角 + 8px 向右下展开,横向放不下翻到图标左侧、纵向放不下翻到图标
    /// 上方,并夹在屏幕工作区内;c = 图标中心 - pos(flyout 局部坐标,允许负值)。
    /// 全程只用图标容器的 PointToScreen(容器在可见分区窗口里,必然连着 PresentationSource),
    /// 不再读 flyout 自身的 PointToScreen — 那会因 popup 重排时序拿到错误位置,
    /// 或在 visual 未连接时抛异常回落到 (0,0),造成"起点有时在左、有时在右"。</summary>
    (Point pos, Point c) ComputeFlyoutPosAndAnchor(FrameworkElement? container, Size flyoutSize)
    {
        const double gap = 8;
        var wa = SystemParameters.WorkArea;
        Point iconTL = new(0, 0);
        double iconW = 0, iconH = 0;
        if (container != null)
        {
            try
            {
                iconTL = container.PointToScreen(new Point(0, 0));
                iconW = container.ActualWidth;
                iconH = container.ActualHeight;
            }
            catch
            {
                // visual 未连接等罕见情况 → 回退到工作区中心,保证仍能弹出。
                var center = new Point(wa.Left + (wa.Width - flyoutSize.Width) / 2,
                                       wa.Top + (wa.Height - flyoutSize.Height) / 2);
                return (center, new Point(flyoutSize.Width / 2, flyoutSize.Height / 2));
            }
        }
        double x = iconTL.X + iconW + gap;
        double y = iconTL.Y + gap;
        if (x + flyoutSize.Width > wa.Right - 8)
            x = Math.Max(wa.Left + 8, iconTL.X - flyoutSize.Width - gap); // 翻到图标左侧
        if (y + flyoutSize.Height > wa.Bottom - 8)
            y = Math.Max(wa.Top + 8, iconTL.Y - flyoutSize.Height - gap); // 翻到图标上方
        var pos = new Point(x, y);
        var iconCenter = new Point(iconTL.X + iconW / 2, iconTL.Y + iconH / 2);
        var c = new Point(iconCenter.X - pos.X, iconCenter.Y - pos.Y);
        return (pos, c);
    }

    /// <summary>把缩放锚点 c 写入 TransformGroup 的 [移至原点(-c), Scale, 移回(+c)] —
    /// 与 HoverExpandBehavior.ApplyOrigin 同款组合,动画以 c(图标中心)为原点缩放。</summary>
    void SetFlyoutAnchor(Point c)
    {
        SubfolderFlyoutView.FlyoutTranslateBack.X = c.X;
        SubfolderFlyoutView.FlyoutTranslateBack.Y = c.Y;
        SubfolderFlyoutView.FlyoutTranslateToOrigin.X = -c.X;
        SubfolderFlyoutView.FlyoutTranslateToOrigin.Y = -c.Y;
    }

    /// <summary>把 Flyout 复位到关闭态(scale 0,不透明 0),供打开前调用,避免残留动画帧。
    /// 不透明度取 0 而非 1:Fade 动效的打开要从 0 淡入(NormalizeFlyoutFor 里非 Fade
    /// kind 会自行把 Opacity 抬回 1,只有 Fade 保留 0 作为 from)。</summary>
    void ResetFlyoutClosed()
    {
        var st = SubfolderFlyoutView.FlyoutScale;
        st.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        st.ScaleX = 0; st.ScaleY = 0;
        SubfolderFlyoutView.BeginAnimation(OpacityProperty, null);
        SubfolderFlyoutView.Opacity = 0;
    }

    void CloseSubfolderFlyout()
    {
        if (!SubfolderFlyoutPopup.IsOpen || _flyoutClosing) return;
        _flyoutClosing = true;
        _flyoutCloseTimer?.Stop();
        _flyoutCloseTimer = null;
        var token = _flyoutOpenToken;
        AnimateSubfolderFlyoutClose(onComplete: () =>
        {
            // 关闭动画期间又点开了另一个 SubFolder(token 已变)→ 不要误关新开的 Flyout。
            if (_flyoutOpenToken != token) { _flyoutClosing = false; return; }
            _flyoutClosing = false;
            SubfolderFlyoutPopup.IsOpen = false;
            SubfolderFlyoutView.ViewModel = null;
        });
    }

    // ── click-outside 关闭(分区空白 / 桌面空白)──
    // 打开时把鼠标捕获到 Flyout 上,再挂 PreviewMouseDownOutsideCapturedElement:
    // 任意一次发生在 Flyout 子树之外的按下(包括桌面)都会先到这里,触发关闭。
    void HookFlyoutClickOutside()
    {
        if (_flyoutClickOutsideHooked) return;
        _flyoutClickOutsideHooked = true;
        try { System.Windows.Input.Mouse.Capture(SubfolderFlyoutView, System.Windows.Input.CaptureMode.SubTree); } catch { }
        System.Windows.Input.Mouse.AddPreviewMouseDownOutsideCapturedElementHandler(SubfolderFlyoutView, OnFlyoutClickOutside);
    }

    void UnhookFlyoutClickOutside()
    {
        if (!_flyoutClickOutsideHooked) return;
        _flyoutClickOutsideHooked = false;
        System.Windows.Input.Mouse.RemovePreviewMouseDownOutsideCapturedElementHandler(SubfolderFlyoutView, OnFlyoutClickOutside);
        if (System.Windows.Input.Mouse.Captured == SubfolderFlyoutView)
        {
            try { SubfolderFlyoutView.ReleaseMouseCapture(); } catch { }
        }
    }

    void OnFlyoutClickOutside(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!SubfolderFlyoutPopup.IsOpen) return;
        CloseSubfolderFlyout();
    }

    // ponytail 2026-08-26: 鼠标进入 Flyout 取消自动关闭 timer,确保点击
    // Style 按钮/拖出等交互不被中断。
    void SubfolderFlyoutView_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _flyoutCloseTimer?.Stop();
        _flyoutCloseTimer = null;
    }

    // ponytail 2026-08-26: 鼠标离开 Flyout 200ms 后自动关闭 — Win11 风格。
    // 给用户 200ms 时间决定要不要移回去(避免在 Flyout 边缘反复进出闪烁)。
    void SubfolderFlyoutView_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!SubfolderFlyoutPopup.IsOpen) return;
        _flyoutCloseTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _flyoutCloseTimer.Tick -= OnFlyoutCloseTick;
        _flyoutCloseTimer.Tick += OnFlyoutCloseTick;
        _flyoutCloseTimer.Stop();
        _flyoutCloseTimer.Start();
    }

    void OnFlyoutCloseTick(object? s, EventArgs e)
    {
        _flyoutCloseTimer?.Stop();
        _flyoutCloseTimer = null;
        CloseSubfolderFlyout();
    }

    // ponytail 2026-08-26: 拖动到 SubFolder 上时给目标容器一个 1.06× 放大反馈。
    // 不影响正常换位 —— 只有 FindSubfolderTarget 命中(内部拖拽/桌面文件拖入)时
    // 才调本 helper,普通 live-X/Y 换位路径不触发 scale。命中变化时清旧 + 设新,
    // 避免残留 scale 在错位容器上。
    void SetSubfolderDragScale(FrameworkElement? container)
    {
        if (ReferenceEquals(container, _scaledSubfolderContainer)) return;
        ClearSubfolderDragScale();
        if (container == null) return;
        _scaledSubfolderContainer = container;
        var st = new ScaleTransform(1.0, 1.0);
        container.RenderTransformOrigin = new Point(0.5, 0.5);
        container.RenderTransform = st;
        var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(120);
        st.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty,
            new System.Windows.Media.Animation.DoubleAnimation(1.0, 1.06, dur) { EasingFunction = ease });
        st.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty,
            new System.Windows.Media.Animation.DoubleAnimation(1.0, 1.06, dur) { EasingFunction = ease });
    }

    void ClearSubfolderDragScale()
    {
        var container = _scaledSubfolderContainer;
        if (container == null) return;
        _scaledSubfolderContainer = null;
        if (container.RenderTransform is System.Windows.Media.ScaleTransform st)
        {
            st.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null);
            st.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, null);
            st.ScaleX = 1.0; st.ScaleY = 1.0;
        }
        container.RenderTransform = null;
        container.RenderTransformOrigin = new Point(0, 0);
    }

    /// <summary>Lookup the FrameworkElement container that hosts <paramref name="sub"/>
    /// inside ItemsHost. Returns null if the container hasn't been generated yet
    /// (which would also mean the item isn't visible).
    /// ponytail 2026-08-26: ItemsHost binds ObservableCollection&lt;ZoneItemViewModel&gt;,
    /// so ContainerFromItem(raw ZoneItem) never matches — resolve the VM first.</summary>
    FrameworkElement? FindContainerFor(ZoneItem sub)
    {
        if (ItemsHost == null) return null;
        foreach (var vm in _vm.Items)
        {
            if (vm.Id == sub.Id)
                return ItemsHost.ItemContainerGenerator.ContainerFromItem(vm) as FrameworkElement;
        }
        return null;
    }

    void SubfolderFlyout_EditStyleRequested(SubfolderFlyout flyout)
    {
        if (flyout.ViewModel == null) return;
        // ponytail 2026-08-26: ensure the management window exists before routing the
        // property editor — PropertyWindowService is a no-op while ManagementWindow is
        // null (startup with StartMinimized + zones shown directly). See App.EnsureManagementWindow.
        (System.Windows.Application.Current as App)?.EnsureManagementWindow();
        PropertyWindowService.OpenOrFocus(flyout.ViewModel.HostSubItem, this);
    }

    /// <summary>拖出:从 Flyout 里把一个内层图标拖回主分区。以 itemVm 为 payload 发起
    /// DragDrop,Window_Drop 里 TryFindOwnerSubfolder 命中 → MoveOutOfSubfolder 移回分区。</summary>
    void SubfolderFlyout_ItemDragOutRequested(ZoneItem hostSub, ZoneItemViewModel itemVm)
    {
        _flyoutCloseTimer?.Stop();
        _flyoutCloseTimer = null;
        try { DragDrop.DoDragDrop(SubfolderFlyoutView, itemVm, DragDropEffects.Move); }
        finally { ClearSubfolderDragScale(); }
    }

    void SubfolderFlyoutPopup_Closed(object? s, EventArgs e)
    {
        // 断开 host SubFolder 的 SubItems 订阅,避免 handler 泄漏到已关闭的 flyout。
        if (_subItemsChangedHandler != null && _subItemsHost != null)
            _subItemsHost.PropertyChanged -= _subItemsChangedHandler;
        _subItemsChangedHandler = null;
        _subItemsHost = null;
        _flyoutOriginContainer = null;
        UnhookFlyoutClickOutside();
    }

    // ── Subfolder flyout animation ──
    // ponytail 2026-08-26: faithful port of HoverExpandBehavior.StartAnimation —
    // per-kind open/close symmetry, from-values read from the CURRENT state,
    // duration 200ms/HoverExpandSpeed, onComplete driven by Completed events
    // (with from==to short-circuits). scale-around-point via the TransformGroup
    // [TranslateToOrigin, Scale, TranslateBack] anchored at the SubFolder icon's
    // screen center (SetFlyoutOriginFromIcon). Close is the frame-exact reverse
    // of open (CubicEase EaseIn vs EaseOut), so the flyout shrinks back into the
    // icon it grew from.
    void AnimateSubfolderFlyoutOpen()
    {
        var vm = SubfolderFlyoutView.ViewModel;
        if (vm == null) return;
        var kind = vm.HostSubItem.HoverAnimation;
        NormalizeFlyoutFor(isExpanded: true, kind);
        var dur = new Duration(TimeSpan.FromMilliseconds(200.0 / Math.Max(0.1, vm.HostSubItem.HoverExpandSpeed)));
        switch (kind)
        {
            case HoverExpandAnimationKind.None:
                ApplyFlyoutFinal(isExpanded: true, kind);
                return;
            case HoverExpandAnimationKind.Fade:
                AnimateFlyoutOpacity(SubfolderFlyoutView.Opacity, 1, dur, EasingMode.EaseOut, null);
                return;
            case HoverExpandAnimationKind.VerticalExpand:
                AnimateFlyoutScaleY(SubfolderFlyoutView.FlyoutScale.ScaleY, 1, dur, EasingMode.EaseOut, null);
                return;
            case HoverExpandAnimationKind.DirectionalExpand:
                AnimateFlyoutScaleX(SubfolderFlyoutView.FlyoutScale.ScaleX, 1, dur, EasingMode.EaseOut, null);
                return;
            case HoverExpandAnimationKind.BounceExpand:
                AnimateFlyoutBounce(isExpand: true, dur, null);
                return;
            default: // ScaleExpand
                AnimateFlyoutScaleXY(SubfolderFlyoutView.FlyoutScale.ScaleX, 1, dur, EasingMode.EaseOut, null);
                return;
        }
    }

    void AnimateSubfolderFlyoutClose(Action onComplete)
    {
        var vm = SubfolderFlyoutView.ViewModel;
        var kind = vm != null ? vm.HostSubItem.HoverAnimation : HoverExpandAnimationKind.ScaleExpand;
        double speed = vm != null ? Math.Max(0.1, vm.HostSubItem.HoverExpandSpeed) : 1.0;
        NormalizeFlyoutFor(isExpanded: false, kind);
        var dur = new Duration(TimeSpan.FromMilliseconds(200.0 / speed));
        switch (kind)
        {
            case HoverExpandAnimationKind.None:
                ApplyFlyoutFinal(isExpanded: false, kind);
                onComplete();
                return;
            case HoverExpandAnimationKind.Fade:
                AnimateFlyoutOpacity(SubfolderFlyoutView.Opacity, 0, dur, EasingMode.EaseIn, onComplete);
                return;
            case HoverExpandAnimationKind.VerticalExpand:
                AnimateFlyoutScaleY(SubfolderFlyoutView.FlyoutScale.ScaleY, 0, dur, EasingMode.EaseIn, onComplete);
                return;
            case HoverExpandAnimationKind.DirectionalExpand:
                AnimateFlyoutScaleX(SubfolderFlyoutView.FlyoutScale.ScaleX, 0, dur, EasingMode.EaseIn, onComplete);
                return;
            case HoverExpandAnimationKind.BounceExpand:
                AnimateFlyoutBounce(isExpand: false, dur, onComplete);
                return;
            default: // ScaleExpand
                AnimateFlyoutScaleXY(SubfolderFlyoutView.FlyoutScale.ScaleX, 0, dur, EasingMode.EaseIn, onComplete);
                return;
        }
    }

    /// <summary>Port of HoverExpandBehavior.NormalizeFor: capture the current animated
    /// values as the new base (stale-base fix), then snap the kind's STABLE axes.
    /// Animated axes keep their current value so the following animation starts from
    /// the real visual state.</summary>
    void NormalizeFlyoutFor(bool isExpanded, HoverExpandAnimationKind kind)
    {
        var st = SubfolderFlyoutView.FlyoutScale;
        double sx = st.ScaleX, sy = st.ScaleY, op = SubfolderFlyoutView.Opacity;
        st.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        SubfolderFlyoutView.BeginAnimation(UIElement.OpacityProperty, null);
        st.ScaleX = sx; st.ScaleY = sy;
        SubfolderFlyoutView.Opacity = op;

        switch (kind)
        {
            case HoverExpandAnimationKind.VerticalExpand:
                st.ScaleX = 1; // stable axis — ScaleY is animated
                break;
            case HoverExpandAnimationKind.DirectionalExpand:
                st.ScaleY = 1; // stable axis — ScaleX is animated
                break;
            case HoverExpandAnimationKind.Fade:
                st.ScaleX = 1; st.ScaleY = 1; // stable — Opacity is animated
                break;
            case HoverExpandAnimationKind.None:
                if (isExpanded) { st.ScaleX = 1; st.ScaleY = 1; SubfolderFlyoutView.Opacity = 1; }
                else { st.ScaleX = 0; st.ScaleY = 0; SubfolderFlyoutView.Opacity = 0; }
                break;
        }
        // ghost-content rule: Opacity is only animated by Fade; other kinds keep 1.
        if (kind != HoverExpandAnimationKind.Fade && kind != HoverExpandAnimationKind.None)
            SubfolderFlyoutView.Opacity = 1;
    }

    /// <summary>Port of HoverExpandBehavior.ApplyFinal for the None kind.</summary>
    void ApplyFlyoutFinal(bool isExpanded, HoverExpandAnimationKind kind)
    {
        var st = SubfolderFlyoutView.FlyoutScale;
        double target = isExpanded ? 1 : 0;
        switch (kind)
        {
            case HoverExpandAnimationKind.VerticalExpand: st.ScaleX = 1; st.ScaleY = target; break;
            case HoverExpandAnimationKind.DirectionalExpand: st.ScaleX = target; st.ScaleY = 1; break;
            default: st.ScaleX = target; st.ScaleY = target; break;
        }
        SubfolderFlyoutView.Opacity = isExpanded ? 1
            : (kind == HoverExpandAnimationKind.Fade ? 0 : 1);
    }

    void AnimateFlyoutScaleXY(double from, double to, Duration dur, EasingMode ease, Action? onComplete)
    {
        var st = SubfolderFlyoutView.FlyoutScale;
        if (Math.Abs(from - to) < 1e-9) { st.ScaleX = to; st.ScaleY = to; onComplete?.Invoke(); return; }
        var ax = new DoubleAnimation(from, to, dur) { EasingFunction = new CubicEase { EasingMode = ease } };
        var ay = new DoubleAnimation(from, to, dur) { EasingFunction = new CubicEase { EasingMode = ease } };
        bool done = false;
        Action fireOnce = () => { if (done) return; done = true; onComplete?.Invoke(); };
        ax.Completed += (_, _) => { st.ScaleX = to; fireOnce(); };
        ay.Completed += (_, _) => { st.ScaleY = to; fireOnce(); };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, ax);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, ay);
    }

    void AnimateFlyoutScaleX(double from, double to, Duration dur, EasingMode ease, Action? onComplete)
    {
        var st = SubfolderFlyoutView.FlyoutScale;
        if (Math.Abs(from - to) < 1e-9) { st.ScaleX = to; onComplete?.Invoke(); return; }
        var ax = new DoubleAnimation(from, to, dur) { EasingFunction = new CubicEase { EasingMode = ease } };
        ax.Completed += (_, _) => { st.ScaleX = to; onComplete?.Invoke(); };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, ax);
    }

    void AnimateFlyoutScaleY(double from, double to, Duration dur, EasingMode ease, Action? onComplete)
    {
        var st = SubfolderFlyoutView.FlyoutScale;
        if (Math.Abs(from - to) < 1e-9) { st.ScaleY = to; onComplete?.Invoke(); return; }
        var ay = new DoubleAnimation(from, to, dur) { EasingFunction = new CubicEase { EasingMode = ease } };
        ay.Completed += (_, _) => { st.ScaleY = to; onComplete?.Invoke(); };
        st.BeginAnimation(ScaleTransform.ScaleYProperty, ay);
    }

    void AnimateFlyoutOpacity(double from, double to, Duration dur, EasingMode ease, Action? onComplete)
    {
        if (Math.Abs(from - to) < 1e-9) { SubfolderFlyoutView.Opacity = to; onComplete?.Invoke(); return; }
        var anim = new DoubleAnimation(from, to, dur) { EasingFunction = new CubicEase { EasingMode = ease } };
        anim.Completed += (_, _) => { SubfolderFlyoutView.Opacity = to; onComplete?.Invoke(); };
        SubfolderFlyoutView.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    void AnimateFlyoutBounce(bool isExpand, Duration dur, Action? onComplete)
    {
        var st = SubfolderFlyoutView.FlyoutScale;
        // degenerate collapse (already at 0) — nothing to bounce, fire synchronously.
        if (!isExpand && Math.Abs(st.ScaleX) < 1e-9)
        {
            st.ScaleX = 0; st.ScaleY = 0; onComplete?.Invoke(); return;
        }
        var bounce = new DoubleAnimationUsingKeyFrames();
        var ease = new BounceEase { Bounces = 2, Bounciness = 2, EasingMode = EasingMode.EaseOut };
        if (isExpand)
        {
            bounce.KeyFrames.Add(new EasingDoubleKeyFrame(st.ScaleX, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            bounce.KeyFrames.Add(new EasingDoubleKeyFrame(1.08, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(dur.TimeSpan.TotalMilliseconds * 0.6)), ease));
            bounce.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(dur.TimeSpan)));
        }
        else
        {
            // 弹性收起:开头快速 squash 1→0.85,再 0.85→0 消失(镜像 HoverExpandBehavior)。
            var squashTime = TimeSpan.FromMilliseconds(dur.TimeSpan.TotalMilliseconds * 0.45);
            bounce.KeyFrames.Add(new EasingDoubleKeyFrame(st.ScaleX, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            bounce.KeyFrames.Add(new EasingDoubleKeyFrame(0.85, KeyTime.FromTimeSpan(squashTime), ease));
            bounce.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(dur.TimeSpan),
                new CubicEase { EasingMode = EasingMode.EaseOut }));
        }
        double final = isExpand ? 1 : 0;
        bool done = false;
        Action fireOnce = () => { if (done) return; done = true; onComplete?.Invoke(); };
        bounce.Completed += (_, _) => { st.ScaleX = final; st.ScaleY = final; fireOnce(); };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, bounce);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, bounce);
    }

    /// <summary>Re-size the SubfolderFlyout's inner UniformGrid when SubItems grows
    /// past 4 or 9 (2×2 → 3×3 → 4×4). Mirrors SubfolderFlyout.SizeInnerGrid without
    /// widening that class's public surface. Walks the flyout's visual tree looking
    /// for the UniformGrid named "InnerGrid".</summary>
    void ResizeFlyoutGrid(int itemCount)
    {
        int cols = itemCount <= 4 ? 2 : itemCount <= 9 ? 3 : 4;
        var grid = FindNamedVisualChild<System.Windows.Controls.Primitives.UniformGrid>(SubfolderFlyoutView, "InnerGrid");
        if (grid != null) { grid.Rows = cols; grid.Columns = cols; }
    }

    static T? FindNamedVisualChild<T>(DependencyObject parent, string name) where T : FrameworkElement
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t && t.Name == name) return t;
            var result = FindNamedVisualChild<T>(child, name);
            if (result != null) return result;
        }
        return null;
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
#if DEBUG
        DzTrace.Log($"[ZoneWindow] ShowZone(wave={waveDelayMs}) ENTRY winVisible={IsVisible} content={MainContent.Visibility} btn={RestoreButton.Visibility} hoverExpanded={_hover?.IsExpanded} modelVisible={_zone.IsVisible} size={Width}x{Height}");
#endif
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
        RefreshFolderMapping();
        if ((DataContext as ZoneViewModel)?.IsLocked != true) NativeMethods.PinToDesktop(this);
        NativeMethods.SetRoundedCorners(this, (int)_zone.CornerRadius);
        _mgr.FireZoneVisibilityChanged(_zone.Id, true);
    }

    public void HideZone(double waveDelayMs = 0)
    {
#if DEBUG
        DzTrace.Log($"[ZoneWindow] HideZone(wave={waveDelayMs}) ENTRY winVisible={IsVisible} content={MainContent.Visibility} btn={RestoreButton.Visibility} hoverExpanded={_hover?.IsExpanded} modelVisible={_zone.IsVisible} restoreEnabled={_zone.EnableRestoreButton} size={Width}x{Height}");
#endif
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
            {
                _hover?.CollapseAnimated();
#if DEBUG
                // ponytail 2026-08-25 ghost-ring diagnosis — 1.5 s after the collapse
                // finishes, save a pure-WPF render + a real screen grab of the
                // collapsed window (see SaveCollapsedDiag).
                var diag = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(1500)
                };
                diag.Tick += (_, _) => { diag.Stop(); SaveCollapsedDiag(); };
                diag.Start();
#endif
            }
        }
        _zone.IsVisible = false;
        _mgr.FireZoneVisibilityChanged(_zone.Id, false);
    }

#if DEBUG
    /// <summary>
    /// ponytail 2026-08-25 ghost-ring diagnosis: writes two PNGs of the collapsed window —
    ///   • D:\BS\dz_render.png — pure WPF render (RenderTargetBitmap, no DWM compositing);
    ///   • D:\BS\dz_screen.png — real screen grab from THIS session around the window
    ///     center (the RestoreButton position for ButtonCenter origin).
    /// Comparing them pins the reported "透明边框" to either WPF-internal rendering or
    /// the OS layer (DWM shadow / acrylic / corner-rounding compositing).
    /// </summary>
    void SaveCollapsedDiag()
    {
        try
        {
            int w = Math.Max(1, (int)Math.Ceiling(ActualWidth));
            int h = Math.Max(1, (int)Math.Ceiling(ActualHeight));
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(this);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = File.Create(@"D:\BS\dz_render.png")) enc.Save(fs);

            var btn = RestoreButton;
            var btnLocal = btn.TransformToAncestor(this).Transform(new Point(btn.ActualWidth / 2, btn.ActualHeight / 2));
            var center = PointToScreen(btnLocal);
            if (PresentationSource.FromVisual(this)?.CompositionTarget is System.Windows.Media.CompositionTarget ct)
            {
                var px = ct.TransformToDevice.Transform(center);
                int cx = (int)Math.Round(px.X), cy = (int)Math.Round(px.Y);
                using var bmp = new System.Drawing.Bitmap(560, 560);
                using var g = System.Drawing.Graphics.FromImage(bmp);
                g.CopyFromScreen(cx - 280, cy - 280, 0, 0, bmp.Size);
                bmp.Save(@"D:\BS\dz_screen.png", System.Drawing.Imaging.ImageFormat.Png);
            }
            DzTrace.Log($"[ZoneWindow] SaveCollapsedDiag ok size={w}x{h} btn=({btnLocal.X:0},{btnLocal.Y:0})");
        }
        catch (Exception ex)
        {
            DzTrace.Log($"[ZoneWindow] SaveCollapsedDiag FAILED: {ex.Message}");
        }
    }
#endif

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
        _folderLoadCts?.Cancel();
        _folderLoadCts = null;
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
        _snapResize?.Detach();
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
            RefreshFolderMapping();
        }), System.Windows.Threading.DispatcherPriority.Normal);
    }

    // ── Drag: DIRECT handler on title bar ──

    void TitleBar_Drag(object s, MouseButtonEventArgs e)
    {
        StartBodyDrag(e);
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
        double h = TitleBarBg.ActualHeight;
        // ponytail 2026-08-26: the merged master's title bar is two layers — the top
        // bar plus the sub-zone tab row — so drag-to-merge drop targeting covers both.
        if (SubZoneTabsRow.Visibility == Visibility.Visible && SubZoneTabsRow.ActualHeight > 0)
            h += SubZoneTabsRow.ActualHeight;
        return new Rect(topLeft, new Size(TitleBarBg.ActualWidth, h));
    }

    public void SetMergeHover(bool on)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        TitleBarScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(on ? 1.06 : 1.0, TimeSpan.FromMilliseconds(140)) { EasingFunction = ease });
        TitleBarScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(on ? 1.24 : 1.0, TimeSpan.FromMilliseconds(140)) { EasingFunction = ease });
    }

    // ── Window-level mouse: body drag (Tile mode) + Ctrl marquee ──

    // ponytail 2026-08-26 (磁贴模式): 主体空白按下 = 拖动整窗（复用 _snapDrag 合并检测），
    // Ctrl+按下 = 保留原有框选。双击由 Window_MouseDoubleClick 处理（自定义图标打开）。
    void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is TextBox) return; // title inline editing
        // Items / list / chrome presses are owned by their own handlers.
        if (IsOnFolderEntry(e.OriginalSource) || IsOnItem(e.OriginalSource)) return;
        if (IsWithinZoneChrome(e.OriginalSource)) return;
        if (MainContent.Visibility != Visibility.Visible) return;
        if (FolderMappingView.Visibility == Visibility.Visible) return;

        // 自定义图标模式下第二次按下（ClickCount==2）不启动拖动，交给双击打开。
        if (_customIconOpenFirst && e.ClickCount == 2) return;

        // Ctrl+drag 走框选（保留旧行为）。
        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            _selectMode = SelectMode.Draw;
            _selectTarget = SelectTarget.ZoneItems;
            _selectStart = e.GetPosition(this);
            _selectCurrent = _selectStart;
            _selectMoved = false;
            _selectFromEmpty = true;
            _selectStartZone = null;
            _selectStartList = null;
            try { Mouse.Capture(this); } catch { }
            return;
        }

        // 主体空白 = 拖动整窗（复用 _snapDrag + 合并检测）。
        StartBodyDrag(e);
    }

    /// <summary>从主体空白触发的窗口拖动，复用 TitleBar_Drag 同款 _snapDrag + 合并检测。</summary>
    void StartBodyDrag(MouseButtonEventArgs e)
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

    // ── Resize ──

    void ResizeGrip_Down(object s, MouseButtonEventArgs e)
    {
        var vm = DataContext as ZoneViewModel;
        if (vm?.IsLocked == true) { e.Handled = true; return; }
        if (s is not Border gr) return;
        bool left = gr == GripTL || gr == GripBL;
        bool top = gr == GripTL || gr == GripTR;
        _snapResize?.Start(e, left, top, !left, !top, 120, 80);
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

    // ── Folder mapping ──
    //
    // 把电脑上的文件夹/磁盘映射到分区内容区：启用后 ItemsViewport 隐藏，内容区
    // 变成可滚动图标网格（虚拟化换行面板），双击文件夹进入、双击文件用关联程序
    // 打开，右上角 + 菜单与样式设置面板都能选择文件夹或磁盘。

    (bool Enabled, string Path) ResolveFolderMapping()
    {
        // Merged master window: the visible mapping follows the selected tab —
        // a sub-zone tab resolves that sub-zone's OWN zone-level mapping (kept
        // when the zone joined the group), the master tab resolves the
        // group-level mapping and falls back to the master's own mapping when
        // the group never set one.
        if (_zone.MergedGroupMembership.SubZoneIds.Count > 0)
        {
            if (_vm.SelectedSubZoneId is Guid sel && sel != _zone.Id
                && _zone.MergedGroupMembership.SubZoneIds.Contains(sel))
            {
                var sub = _mgr.Zones.FirstOrDefault(z => z.Id == sel);
                if (sub != null)
                    return (sub.FolderMappingEnabled, sub.FolderMappingPath ?? "");
            }
            var gs = _zone.MergedGroupStyle;
            if (gs.FolderMappingEnabled || !string.IsNullOrEmpty(gs.FolderMappingPath))
                return (gs.FolderMappingEnabled, gs.FolderMappingPath ?? "");
            return (_zone.FolderMappingEnabled, _zone.FolderMappingPath ?? "");
        }
        return (_zone.FolderMappingEnabled, _zone.FolderMappingPath ?? "");
    }

    /// <summary>Dual-write (zone + merged style) so the mapping survives group
    /// disband / master promotion, then persist + repaint. When a sub-zone tab is
    /// selected inside the merged master, the write targets that sub-zone's own
    /// zone-level mapping instead (it keeps its mapping when joining the group).</summary>
    void SetFolderMapping(bool enabled, string path)
    {
        path = NormalizeFolderMappingPath(path);
        if (_zone.MergedGroupMembership.SubZoneIds.Count > 0
            && _vm.SelectedSubZoneId is Guid sel && sel != _zone.Id
            && _zone.MergedGroupMembership.SubZoneIds.Contains(sel))
        {
            var sub = _mgr.Zones.FirstOrDefault(z => z.Id == sel);
            if (sub != null)
            {
                sub.FolderMappingEnabled = enabled;
                sub.FolderMappingPath = path;
                ScheduleSave();
                // Notify the manager so the open style panel syncs its checkbox/path.
                _mgr.NotifyChanged();
                RefreshFolderMapping();
                return;
            }
        }
        _zone.FolderMappingEnabled = enabled;
        _zone.FolderMappingPath = path;
        _zone.MergedGroupStyle.FolderMappingEnabled = enabled;
        _zone.MergedGroupStyle.FolderMappingPath = path;
        ScheduleSave();
        // Notify the manager so the open style panel syncs its checkbox/path
        // (e.g. mapping turned off from the zone's ✕ button).
        _mgr.NotifyChanged();
        RefreshFolderMapping();
    }

    static string NormalizeFolderMappingPath(string? path)
    {
        path = path?.Trim() ?? "";
        // "C:" → "C:\" so drive roots behave like directories.
        if (path.Length == 2 && char.IsLetter(path[0]) && path[1] == ':')
            return path + "\\";
        return path;
    }

    static bool IsDriveRoot(string path) =>
        path.Length == 3 && char.IsLetter(path[0]) && path[1] == ':' && path[2] == '\\';

    /// <summary>Toggle the mapping view vs the item view and (re)load the listing.
    /// Reloads are skipped when the path is unchanged (cheap on every ZonesChanged);
    /// pass forceReload for explicit refreshes.</summary>
    void RefreshFolderMapping(bool forceReload = false)
    {
        var (enabled, path) = ResolveFolderMapping();
        bool show = enabled;
        FolderMappingView.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ItemsViewport.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        // The mapping header row is part of the title-bar band — re-run the style
        // pass so the independent-fill clip / header fill follow the toggle.
        ApplyStyle();
        if (!show)
        {
            _folderLoadCts?.Cancel();
            _folderLoadCts = null;
            _folderLoadedPath = "";
            _folderEntries.Clear();
            UpdateCanvasSize();
            return;
        }

        _lastFolderRefreshUtc = DateTime.UtcNow;
        FolderMapPathText.Text = path;
        bool validDir = Directory.Exists(path);
        bool upEnabled = validDir && !IsDriveRoot(path);
        FolderMapUpBtn.IsEnabled = upEnabled;
        FolderMapUpBtn.Opacity = upEnabled ? 1.0 : 0.35;

        if (string.IsNullOrEmpty(path) || !validDir)
        {
            ShowFolderHint(_loc["FolderMap.Invalid"], withChoose: true);
            return;
        }
        if (!forceReload && _folderLoadedPath == path && FolderList.Visibility == Visibility.Visible)
            return; // unchanged — keep the current listing
        StartFolderLoad(path);
    }

    void ShowFolderHint(string text, bool withChoose)
    {
        _folderLoadCts?.Cancel();
        _folderLoadCts = null;
        _folderLoadedPath = "";
        _folderEntries.Clear();
        FolderMapHintText.Text = text;
        FolderMapHintBtn.Visibility = withChoose ? Visibility.Visible : Visibility.Collapsed;
        FolderMapHint.Visibility = Visibility.Visible;
        FolderList.Visibility = Visibility.Collapsed;
    }

    void StartFolderLoad(string path)
    {
        _folderLoadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _folderLoadCts = cts;
        _folderLoadedPath = path;
        _folderEntries.Clear();
        FolderMapHint.Visibility = Visibility.Collapsed;
        FolderList.Visibility = Visibility.Visible;

        Task.Run(() => EnumerateAndLoad(path, cts));
    }

    void EnumerateAndLoad(string path, CancellationTokenSource cts)
    {
        var entries = new List<FolderEntryViewModel>();
        bool error = false;
        bool truncated = false;
        try
        {
            var opts = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = false,
                ReturnSpecialDirectories = false,
            };
            foreach (var d in Directory.EnumerateDirectories(path, "*", opts))
            {
                if (cts.IsCancellationRequested) return;
                if (entries.Count >= FolderMapMaxEntries) { truncated = true; break; }
                entries.Add(new FolderEntryViewModel(Path.GetFileName(d), d, true));
            }
            if (!truncated)
            {
                foreach (var f in Directory.EnumerateFiles(path, "*", opts))
                {
                    if (cts.IsCancellationRequested) return;
                    if (entries.Count >= FolderMapMaxEntries) { truncated = true; break; }
                    entries.Add(new FolderEntryViewModel(Path.GetFileName(f), f, false));
                }
            }
        }
        catch (Exception) { error = true; }

        entries.Sort((a, b) => a.IsFolder != b.IsFolder
            ? (a.IsFolder ? -1 : 1)
            : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));

        if (cts.IsCancellationRequested) return;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (cts.IsCancellationRequested) return;
            if (error) { ShowFolderHint(_loc["FolderMap.Invalid"], withChoose: true); return; }
            _folderEntries.Clear();
            foreach (var e in entries) _folderEntries.Add(e);
            ShowEmptyHintIfNone();
        }));

        // Icon pass: resolve shell icons on the background thread (cache-backed),
        // then hand each frozen source to the UI thread in small batches.
        const int batch = 12;
        for (int i = 0; i < entries.Count; i += batch)
        {
            if (cts.IsCancellationRequested) return;
            var batchItems = new List<(FolderEntryViewModel vm, System.Windows.Media.ImageSource icon)>();
            for (int j = i; j < Math.Min(i + batch, entries.Count); j++)
            {
                var e = entries[j];
                var icon = _iconService.GetIcon(e.FullPath,
                    e.IsFolder ? Models.ItemType.Folder : Models.ItemType.Shortcut);
                if (icon == null) continue;
                try { (icon as Freezable)?.Freeze(); } catch { }
                batchItems.Add((e, icon));
            }
            if (batchItems.Count > 0)
            {
                var chunk = batchItems;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (cts.IsCancellationRequested) return;
                    foreach (var (vm, icon) in chunk) vm.Icon = icon;
                }));
            }
        }
    }

    void ShowEmptyHintIfNone()
    {
        if (_folderEntries.Count == 0)
        {
            FolderMapHintText.Text = _loc["FolderMap.Empty"];
            FolderMapHintBtn.Visibility = Visibility.Visible;
            FolderMapHint.Visibility = Visibility.Visible;
            FolderList.Visibility = Visibility.Collapsed;
        }
        else
        {
            FolderMapHint.Visibility = Visibility.Collapsed;
            FolderList.Visibility = Visibility.Visible;
        }
    }

    /// <summary>Throttled re-scan when the window regains focus (2 s minimum spacing).</summary>
    void RefreshFolderMappingIfStale()
    {
        if (!ResolveFolderMapping().Enabled) return;
        if (DateTime.UtcNow - _lastFolderRefreshUtc < TimeSpan.FromSeconds(2)) return;
        RefreshFolderMapping(forceReload: true);
    }

    void FolderMapChoose_Click(object s, RoutedEventArgs e)
    {
        var (_, current) = ResolveFolderMapping();
        var dlg = new OpenFolderDialog
        {
            Title = _loc["FolderMap.ChooseTitle"],
            Multiselect = false,
        };
        if (!string.IsNullOrEmpty(current) && Directory.Exists(current))
            dlg.InitialDirectory = current;
        bool? ok;
        try { ok = dlg.ShowDialog(this); }
        catch { ok = null; }
        if (ok == true && !string.IsNullOrEmpty(dlg.FolderName))
            SetFolderMapping(true, dlg.FolderName);
    }

    void FolderMapUp_Click(object s, MouseButtonEventArgs e)
    {
        var (_, path) = ResolveFolderMapping();
        string? parent = FolderMapParent(path);
        if (parent != null) SetFolderMapping(true, parent);
        e.Handled = true;
    }

    static string? FolderMapParent(string path)
    {
        if (string.IsNullOrEmpty(path) || IsDriveRoot(path)) return null;
        try
        {
            var parent = Directory.GetParent(path);
            return parent?.FullName;
        }
        catch { return null; }
    }

    void FolderMapRefresh_Click(object s, MouseButtonEventArgs e)
    {
        var (enabled, _) = ResolveFolderMapping();
        if (enabled) RefreshFolderMapping(forceReload: true);
        e.Handled = true;
    }

    void FolderMapClose_Click(object s, MouseButtonEventArgs e)
    {
        // Keep the mapped path so re-enabling restores the same folder instantly.
        SetFolderMapping(false, ResolveFolderMapping().Path);
        e.Handled = true;
    }

    void FolderMapMenuOpen_Click(object s, RoutedEventArgs e) => OpenFolderMapSelected();

    void FolderMapMenuOpenLocation_Click(object s, RoutedEventArgs e)
    {
        if (FolderList.SelectedItem is not FolderEntryViewModel { FullPath: { Length: > 0 } } vm) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{vm.FullPath}\"") { UseShellExecute = true });
        }
        catch { }
    }

    void FolderMapMenuOpenInExplorer_Click(object s, RoutedEventArgs e)
    {
        if (FolderList.SelectedItem is not FolderEntryViewModel { FullPath: { Length: > 0 } } vm) return;
        try
        {
            string target = vm.IsFolder ? vm.FullPath : Path.GetDirectoryName(vm.FullPath) ?? "";
            if (!string.IsNullOrEmpty(target))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = true });
        }
        catch { }
    }

    void OpenFolderMapSelected()
    {
        var sel = FolderList.SelectedItems.Cast<FolderEntryViewModel>()
            .Where(x => !string.IsNullOrEmpty(x.FullPath)).ToList();
        if (sel.Count == 0) return;
        if (sel.Count == 1)
        {
            var vm = sel[0];
            if (vm.IsFolder)
            {
                SetFolderMapping(true, vm.FullPath);
                return;
            }
            try { Process.Start(new ProcessStartInfo(vm.FullPath) { UseShellExecute = true }); }
            catch { }
            return;
        }
        // Multi-open: files launch with their associated apps; folders open in Explorer.
        foreach (var vm in sel)
        {
            try
            {
                if (vm.IsFolder)
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{vm.FullPath}\"") { UseShellExecute = true });
                else
                    Process.Start(new ProcessStartInfo(vm.FullPath) { UseShellExecute = true });
            }
            catch { }
        }
    }

    void FolderList_MouseDoubleClick(object s, MouseButtonEventArgs e)
    {
        // Only left double-click navigates/opens — right double-click must not enter a folder.
        if (e.ChangedButton != MouseButton.Left) return;
        OpenFolderMapSelected();
        e.Handled = true;
    }

    // ── Folder-entry context menu (code-built, opened manually on right-down so
    //    exactly one menu opens and no focus steal closes it early) ──

    ContextMenu? _folderEntryMenu;
    MenuItem? _fmMenuOpen, _fmMenuOpenLocation, _fmMenuOpenExplorer, _fmMenuRename, _fmMenuDelete;
    Separator? _fmMenuSep;

    void EnsureFolderEntryMenu()
    {
        if (_folderEntryMenu != null) return;
        _folderEntryMenu = new ContextMenu();
        _fmMenuOpen = new MenuItem(); _fmMenuOpen.Click += FolderMapMenuOpen_Click;
        _fmMenuOpenLocation = new MenuItem(); _fmMenuOpenLocation.Click += FolderMapMenuOpenLocation_Click;
        _fmMenuOpenExplorer = new MenuItem(); _fmMenuOpenExplorer.Click += FolderMapMenuOpenInExplorer_Click;
        _fmMenuRename = new MenuItem(); _fmMenuRename.Click += FolderMapMenuRename_Click;
        _fmMenuSep = new Separator();
        _fmMenuDelete = new MenuItem { Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0x66, 0x66)) };
        _fmMenuDelete.Click += FolderMapMenuDelete_Click;
        _folderEntryMenu.Items.Add(_fmMenuOpen);
        _folderEntryMenu.Items.Add(_fmMenuOpenLocation);
        _folderEntryMenu.Items.Add(_fmMenuOpenExplorer);
        _folderEntryMenu.Items.Add(_fmMenuRename);
        _folderEntryMenu.Items.Add(_fmMenuSep);
        _folderEntryMenu.Items.Add(_fmMenuDelete);
    }

    void ShowFolderEntryMenu()
    {
        EnsureFolderEntryMenu();
        if (_folderEntryMenu == null) return;
        // Multi-selection: 打开/删除/重命名 act on the whole selection;
        // 打开所在位置 is single-only (like Explorer).
        bool multi = FolderList.SelectedItems.Count > 1;
        if (_fmMenuOpenLocation != null) _fmMenuOpenLocation.Visibility = multi ? Visibility.Collapsed : Visibility.Visible;
        _folderEntryMenu.PlacementTarget = FolderList;
        _folderEntryMenu.IsOpen = true;
    }

    /// <summary>Select the entry under the cursor (no focus call — focusing the
    /// ListBoxItem while the popup opens is what made the menu close instantly).
    /// Right-clicking an unselected entry selects it alone first.</summary>
    void SelectFolderEntryAtCursor()
    {
        if (!GetCursorPos(out var pt)) return;
        var p = FolderList.PointFromScreen(new Point(pt.X, pt.Y));
        if (FolderList.InputHitTest(p) is not DependencyObject hit) return;
        while (hit != null && !ReferenceEquals(hit, FolderList))
        {
            if (hit is ListBoxItem item)
            {
                if (!item.IsSelected)
                {
                    FolderList.UnselectAll();
                    item.IsSelected = true;
                }
                return;
            }
            hit = VisualTreeHelper.GetParent(hit);
        }
    }

    List<FolderEntryViewModel> SelectedFolderEntries() =>
        FolderList.SelectedItems.Cast<FolderEntryViewModel>()
            .Where(x => !string.IsNullOrEmpty(x.FullPath)).ToList();

    /// <summary>Rename the entry on disk (same directory as the mapping — Explorer
    /// parity). Multi-selection renames with a base name + sequential suffix.</summary>
    void FolderMapMenuRename_Click(object s, RoutedEventArgs e)
    {
        var sel = SelectedFolderEntries();
        if (sel.Count == 0) return;
        var (_, mapPath) = ResolveFolderMapping();
        if (string.IsNullOrEmpty(mapPath) || !Directory.Exists(mapPath)) return;

        if (sel.Count == 1)
        {
            RenameFolderEntry(sel[0], mapPath);
            return;
        }

        // Batch rename: base + " (n)", extensions preserved — same styled dialog
        // as the single-icon rename.
        var rn = new Views.RenameDialog(sel[0].Name, _loc["Rename.Batch"], _loc["Rename.BatchPrompt"]) { Owner = this };
        if (rn.ShowDialog() != true) return;
        var baseName = rn.NewName.Trim();
        if (string.IsNullOrEmpty(baseName)) return;
        int done = 0, failed = 0;
        int n = 0;
        foreach (var vm in sel)
        {
            n++;
            string newName = n == 1
                ? baseName
                : $"{Path.GetFileNameWithoutExtension(baseName)} ({n}){Path.GetExtension(baseName)}";
            if (RenameFolderEntryCore(vm, mapPath, newName)) done++; else failed++;
        }
        RefreshFolderMapping(forceReload: true);
        FlashMapStatus(failed == 0
            ? string.Format(_loc["FolderMap.RenameDone"], done)
            : _loc["FolderMap.RenameFailedShort"]);
    }

    void RenameFolderEntry(FolderEntryViewModel vm, string mapPath)
    {
        // Same styled dialog as the single-icon rename.
        var rn = new Views.RenameDialog(vm.Name) { Owner = this };
        if (rn.ShowDialog() != true) return;
        var name = rn.NewName.Trim();
        if (string.IsNullOrEmpty(name) || name == vm.Name) return;
        if (RenameFolderEntryCore(vm, mapPath, name))
            RefreshFolderMapping(forceReload: true);
    }

    bool RenameFolderEntryCore(FolderEntryViewModel vm, string mapPath, string newName)
    {
        if (newName == vm.Name) return true;
        if (newName is "." or ".." || newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            MessageBox.Show(_loc["FolderMap.RenameInvalid"], _loc["FolderMap.Rename"],
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        var newPath = Path.Combine(mapPath, newName);
        if (File.Exists(newPath) || Directory.Exists(newPath))
        {
            MessageBox.Show(_loc["FolderMap.NameTaken"], _loc["FolderMap.Rename"],
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        try
        {
            if (vm.IsFolder) Directory.Move(vm.FullPath, newPath);
            else File.Move(vm.FullPath, newPath);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(string.Format(_loc["FolderMap.RenameFailed"], ex.Message), _loc["FolderMap.Rename"],
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    /// <summary>Delete the selected entries from the mapped folder. EVERY delete
    /// asks for confirmation and warns that it syncs to the mapped folder/drive.
    /// Deletions go to the Recycle Bin (recoverable).</summary>
    void FolderMapMenuDelete_Click(object s, RoutedEventArgs e) => DeleteSelectedFolderEntries();

    void DeleteSelectedFolderEntries()
    {
        var sel = SelectedFolderEntries();
        if (sel.Count == 0) return;
        var (enabled, path) = ResolveFolderMapping();
        if (!enabled || string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
        // 二次确认 + 同步警告（操作会同步到对应文件夹或磁盘）。
        if (MessageBox.Show(
                string.Format(_loc["FolderMap.DeleteConfirm"], sel.Count, path),
                _loc["FolderMap.DeleteTitle"], MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        Task.Run(() =>
        {
            int done = 0;
            string? firstError = null;
            foreach (var vm in sel)
            {
                try
                {
                    if (vm.IsFolder)
                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(vm.FullPath,
                            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    else
                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(vm.FullPath,
                            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    done++;
                }
                catch (Exception ex) { firstError ??= ex.Message; }
            }
            Dispatcher.BeginInvoke(new Action(() =>
            {
                RefreshFolderMapping(forceReload: true);
                if (firstError != null && done == 0)
                    FlashMapStatus(string.Format(_loc["FolderMap.DeleteFailed"], firstError));
                else
                    FlashMapStatus(string.Format(_loc["FolderMap.DeleteDone"], done));
            }));
        });
    }

    // ── Paste (zone context menu + Ctrl+V) ──

    void PasteClipboard_Click(object s, RoutedEventArgs e) => PasteClipboardIntoMapping();

    /// <summary>Paste clipboard contents (files / text / image) into the mapped folder.
    /// Returns true when the mapping consumed the gesture.</summary>
    bool PasteClipboardIntoMapping()
    {
        var (enabled, path) = ResolveFolderMapping();
        if (!enabled || string.IsNullOrEmpty(path) || !Directory.Exists(path)) return false;

        string targetDir = path;
        // Snapshot clipboard contents on the UI thread (STA requirement).
        List<string>? fileList = null;
        string? text = null;
        System.Windows.Media.Imaging.BitmapSource? image = null;
        try
        {
            if (Clipboard.ContainsFileDropList()) fileList = Clipboard.GetFileDropList().Cast<string>().ToList();
            else if (Clipboard.ContainsText()) text = Clipboard.GetText();
            else if (Clipboard.ContainsImage())
            {
                image = Clipboard.GetImage();
                if (image.CanFreeze) image.Freeze();
            }
        }
        catch { return false; }

        if (fileList == null && string.IsNullOrEmpty(text) && image == null)
        {
            FlashMapStatus(_loc["FolderMap.PasteNothing"]);
            return true;
        }

        Task.Run(() =>
        {
            int done = 0;
            if (fileList != null)
            {
                foreach (var f in fileList) if (CopyInto(f, targetDir)) done++;
            }
            else if (!string.IsNullOrEmpty(text))
            {
                var dest = UniqueDropPath(targetDir, "粘贴文本.txt");
                try { File.WriteAllText(dest, text, System.Text.Encoding.UTF8); done = 1; } catch { }
            }
            else if (image != null)
            {
                var dest = UniqueDropPath(targetDir, "粘贴图片.png");
                try
                {
                    using var fs = File.Create(dest);
                    var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
                    enc.Save(fs);
                    done = 1;
                }
                catch { }
            }
            Dispatcher.BeginInvoke(new Action(() =>
            {
                RefreshFolderMapping(forceReload: true);
                FlashMapStatus(done > 0
                    ? string.Format(_loc["FolderMap.PasteDone"], done)
                    : _loc["FolderMap.PasteNothing"]);
            }));
        });
        return true;
    }

    /// <summary>Show a transient status in the mapping header path area, then restore
    /// the real path.</summary>
    System.Windows.Threading.DispatcherTimer? _mapStatusTimer;

    void FlashMapStatus(string msg)
    {
        FolderMapPathText.Text = msg;
        _mapStatusTimer?.Stop();
        _mapStatusTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2.2) };
        _mapStatusTimer.Tick += (_, _) =>
        {
            _mapStatusTimer.Stop();
            var (_, path) = ResolveFolderMapping();
            FolderMapPathText.Text = path;
        };
        _mapStatusTimer.Start();
    }

    /// <summary>Ctrl+V pastes clipboard files/text into the mapped folder — the
    /// same gesture as Explorer.</summary>
    void Window_PreviewKeyDown(object s, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox) return; // inline title editing keeps its own keys
        if (e.Key == Key.Delete)
        {
            // Delete key deletes the selected mapped entries (same confirm flow).
            if (ResolveFolderMapping().Enabled && FolderList.SelectedItems.Count > 0)
            {
                DeleteSelectedFolderEntries();
                e.Handled = true;
            }
            return;
        }
        if (e.Key != Key.V || (Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        if (PasteClipboardIntoMapping()) e.Handled = true;
    }

    (double, double) FindFreeSpot() => ZoneLayout.FindFreeSpot(_vm.GetPlacementItems(), _zone.Width, _zone.Height, _zone.GridSize, _zone.GridSize + ZoneLayout.LabelArea);

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

        double pitch = ZoneLayout.Pitch(gridSize);
        double vpitch = ZoneLayout.VPitch(gridSize);
        double x = 10, y = 10;
        foreach (var item in items.OrderBy(i => i.Y).ThenBy(i => i.X))
        {
            item.X = ZoneViewModel.SnapToGrid(x, gridSize);
            item.Y = ZoneViewModel.SnapToGridY(y, gridSize);
            x += pitch;
            if (x > _zone.Width - gridSize) { x = 10; y += vpitch; }
        }
        _vm.RefreshMergedItems();
    }

    // ── Right-click zone ──

    void Window_PreviewMouseRightButtonDown(object s, MouseButtonEventArgs e)
    {
        if (MainContent.Visibility != Visibility.Visible) return;
        // Mapped-folder entries: select + open the entry menu manually — a single
        // deterministic menu open (no ContextMenuService, no focus steal).
        if (IsOnFolderEntry(e.OriginalSource))
        {
            SelectFolderEntryAtCursor();
            ShowFolderEntryMenu();
            e.Handled = true;
            return;
        }
        // Zone items have their own ContextMenu (ContextMenuService opens it);
        // right-clicking an unselected item selects it alone first.
        if (IsOnItem(e.OriginalSource))
        {
            SelectZoneItemUnderCursor(e.OriginalSource);
            return;
        }
        ZoneBorder.ContextMenu.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>Zone context menu opened — show Paste only while a folder mapping is active.</summary>
    void ZoneMenu_Opened(object s, RoutedEventArgs e)
    {
        var (enabled, path) = ResolveFolderMapping();
        bool canPaste = enabled && !string.IsNullOrEmpty(path) && Directory.Exists(path);
        CtxPaste.Visibility = canPaste ? Visibility.Visible : Visibility.Collapsed;
    }

    static bool IsOnFolderEntry(object s)
    {
        var c = s as DependencyObject;
        while (c != null)
        {
            if (c is ListBoxItem) return true;
            c = VisualTreeHelper.GetParent(c);
        }
        return false;
    }
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
    {
        if (e.ClickCount == 2)
        {
            if (s is FrameworkElement fe && fe.DataContext is ZoneItemViewModel iv)
            {
                if (iv.Type == ItemType.SubFolder)
                {
                    // 双击次级文件夹 = 打开 flyout(它没有可启动的 TargetPath)。
                    var sub = ResolveSourceZoneItem(iv);
                    if (sub != null) { OpenSubfolderFlyout(sub); e.Handled = true; return; }
                }
                Open(iv);
            }
            e.Handled = true;
            return;
        }
        // Ctrl+click toggles selection (multi-select) — no drag, no flyout.
        if (s is FrameworkElement feCtrl && feCtrl.DataContext is ZoneItemViewModel ctrlVm
            && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            ctrlVm.IsSelected = !ctrlVm.IsSelected;
            e.Handled = true;
            return;
        }
        // Plain click selects the clicked item alone; clicking an already-selected
        // item keeps the multi-selection (standard Explorer behavior).
        if (s is FrameworkElement feSel && feSel.DataContext is ZoneItemViewModel selVm
            && !selVm.IsSelected)
        {
            foreach (var o in _vm.Items) o.IsSelected = false;
            selVm.IsSelected = true;
        }
        // SubFolder 与普通图标同路径:记录拖拽起点 + 捕获鼠标;未拖动的单击在
        // Item_MouseUp 里打开 flyout(点击 vs 拖拽消歧)。
        if (s is FrameworkElement el && el.DataContext is ZoneItemViewModel vm)
        {
            _dv = vm; _de = el; _ds = e.GetPosition(this); _is = new Point(vm.X, vm.Y); _dragging = false; el.CaptureMouse();
            // Long-press arms the marquee; a quick drag stays the move gesture.
            StartMarqueeHoldTimer(SelectTarget.ZoneItems, _ds);
            e.Handled = true;
        }
    }
    void Item_MouseMove(object s, MouseEventArgs e)
    {
        // A move before the long-press completes hands the gesture back to the
        // move-drag (cancels the pending marquee).
        if (_selectMode == SelectMode.Hold)
        {
            var pd = e.GetPosition(this) - _selectStart;
            if (Math.Abs(pd.X) >= SystemParameters.MinimumHorizontalDragDistance
                || Math.Abs(pd.Y) >= SystemParameters.MinimumVerticalDragDistance)
                CancelMarqueeHold();
        }
        if (_dv == null || _de == null) return;
        var d = e.GetPosition(this) - _ds;
        if (!_dragging)
        {
            if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            _dragging = true;
            _de.Opacity = 0.7;
        }
        // live X/Y preview — icon tracks cursor; Item_MouseUp commits reorder/move.
        _dv.X = Math.Max(0, Math.Min(_is.X + d.X, _zone.Width - ItemW));
        _dv.Y = Math.Max(0, Math.Min(_is.Y + d.Y, _zone.Height - ItemH));

        // SubFolder 命中检测(bounds-based,捕获/实时预览期间都稳定):命中 → 放大目标
        // 方框并隐藏换位指示器;未命中 → 复原放大并显示换位指示器。
        var overSub = FindSubfolderTarget(e.GetPosition(ItemsHost));
        if (overSub != null && _dv.Type != ItemType.SubFolder)
        {
            SetSubfolderDragScale(FindContainerFor(overSub));
            HideDropIndicator();
        }
        else
        {
            ClearSubfolderDragScale();
            UpdateDropIndicator(_dv);
        }
    }

    void Item_MouseUp(object s, MouseButtonEventArgs e)
    {
        // A click (no move) released before the long-press completed → no marquee.
        if (_selectMode == SelectMode.Hold)
        {
            _selectHoldTimer?.Stop();
            _selectMode = SelectMode.None;
            _selectTarget = SelectTarget.None;
        }
        if (_dv == null) return;
        if (_de != null) { _de.ReleaseMouseCapture(); _de.Opacity = 1.0; }
        if (_dragging)
        {
            // 命中 SubFolder → 移入;否则普通换位/移动(次级文件夹图标与普通图标同规则)。
            var overSub = FindSubfolderTarget(e.GetPosition(ItemsHost));
            if (overSub != null && _dv.Type != ItemType.SubFolder)
                MoveIntoSubfolder(_dv, overSub);
            else if (_zone.SnapToGrid) ReorderItemInto(_dv, _dv.X, _dv.Y);
            else { _vm.MoveItem(_dv.Id, _dv.X, _dv.Y, snapToGrid: false); _vm.RefreshMergedItems(); }
        }
        else if (_dv.Type == ItemType.SubFolder)
        {
            // 单击未拖动 → 打开 flyout(点击 vs 拖拽消歧;Ctrl+click 已在 MouseDown 拦下)。
            var sub = ResolveSourceZoneItem(_dv);
            if (sub != null) OpenSubfolderFlyout(sub);
        }
        HideDropIndicator();
        ClearSubfolderDragScale();
        _dv = null; _de = null; _dragging = false;
    }

    // ── SubFolder 图标右键菜单 ──

    void SubfolderOpen_Click(object s, RoutedEventArgs e)
    {
        if (VM(s) is not ZoneItemViewModel v || v.Type != ItemType.SubFolder) return;
        var sub = ResolveSourceZoneItem(v);
        if (sub != null) OpenSubfolderFlyout(sub);
    }

    void SubfolderRename_Click(object s, RoutedEventArgs e)
    {
        if (VM(s) is not ZoneItemViewModel v || v.Type != ItemType.SubFolder) return;
        // 与单个图标重命名同款弹窗(RenameDialog)。
        var rn = new Views.RenameDialog(v.Name) { Owner = this };
        if (rn.ShowDialog() == true && !string.IsNullOrWhiteSpace(rn.NewName))
        {
            v.Name = rn.NewName;
            _mgr.SaveConfig();
        }
    }

    /// <summary>解散次级文件夹:图标本身移除,内部图标自动排列回所属分区。
    /// 支持多选:选中多个次级文件夹时一次全部解散(与普通图标多选删除同模式)。</summary>
    void SubfolderDissolve_Click(object s, RoutedEventArgs e)
    {
        if (VM(s) is not ZoneItemViewModel v || v.Type != ItemType.SubFolder) return;
        var sel = _vm.Items.Where(i => i.IsSelected && i.Type == ItemType.SubFolder).ToList();
        CloseSubfolderFlyout();
        if (sel.Count > 1 && sel.Contains(v))
        {
            foreach (var it in sel) DissolveSubfolder(it);
        }
        else
        {
            DissolveSubfolder(v);
        }
        _mgr.SaveConfig();
        _mgr.NotifyChanged();
        UpdateCanvasSize();
    }

    void DissolveSubfolder(ZoneItemViewModel v)
    {
        var sub = ResolveSourceZoneItem(v);
        if (sub == null) return;
        var owner = OwnerZoneOf(v) ?? _zone;
        owner.Items.Remove(sub);
        foreach (var inner in sub.SubItems)
        {
            var (sx, sy) = FindFreeSpot();
            inner.X = sx; inner.Y = sy;
            owner.Items.Add(inner);
        }
        sub.SubItems.Clear();
    }

    /// <summary>删除次级文件夹:内部图标一并删除(spec Q7-A,无需二次确认)。
    /// 支持多选:与普通图标多选删除完全一致 — 选中多个(含普通图标/次级文件夹混选)
    /// 时一次全部删除,带数量确认。</summary>
    void SubfolderDelete_Click(object s, RoutedEventArgs e)
    {
        if (VM(s) is not ZoneItemViewModel v) return;
        var sel = _vm.Items.Where(i => i.IsSelected).ToList();
        if (sel.Count > 1 && sel.Contains(v))
        {
            if (MessageBox.Show(string.Format(_loc["ZoneItem.DeleteMultiConfirm"], sel.Count),
                    _loc["Item.Delete"], MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            CloseSubfolderFlyout();
            foreach (var it in sel) _vm.DeleteItemCommand.Execute(it);
            return;
        }
        CloseSubfolderFlyout();
        _vm.DeleteItemCommand.Execute(v);
    }

    // ── Marquee multi-select (long-press + drag) ──
    //
    // Press holds 350ms → the drag draws a marquee and selects everything it
    // touches (selection at gesture start is preserved). A quick drag on a zone
    // item stays the move gesture; a drag on empty area marquees immediately.

    void StartMarqueeHoldTimer(SelectTarget target, Point start)
    {
        _selectMode = SelectMode.Hold;
        _selectTarget = target;
        _selectStart = start;
        _selectMoved = false;
        _selectFromEmpty = false;
        _selectHoldTimer?.Stop();
        _selectHoldTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(MarqueeHoldMs)
        };
        _selectHoldTimer.Tick += (_, _) =>
        {
            _selectHoldTimer.Stop();
            if (_selectMode != SelectMode.Hold) return;
            _selectMode = SelectMode.Draw;
            // Zone items: hand the gesture to the marquee — release the move-drag
            // scaffolding so Item_MouseMove no longer moves the grabbed item.
            if (_selectTarget == SelectTarget.ZoneItems)
            {
                _dv = null;
                if (_de != null) { try { _de.ReleaseMouseCapture(); } catch { } _de = null; }
                _dragging = false;
                HideDropIndicator();
                ClearSubfolderDragScale();
            }
        };
        _selectHoldTimer.Start();
    }

    void CancelMarqueeHold()
    {
        if (_selectMode != SelectMode.Hold) return;
        _selectHoldTimer?.Stop();
        _selectMode = SelectMode.None;
        _selectTarget = SelectTarget.None;
        _selectStartZone = null;
        _selectStartList = null;
    }

    /// <summary>Press on the mapping list: hold on an entry arms the marquee
    /// (click-select still happens); empty-area press marquees immediately.</summary>
    void FolderList_PreviewMouseLeftButtonDown(object s, MouseButtonEventArgs e)
    {
        // Capture so the marquee still commits when released outside the window.
        try { Mouse.Capture(FolderList); } catch { }
        if (IsOnFolderEntry(e.OriginalSource))
        {
            StartMarqueeHoldTimer(SelectTarget.ListItems, e.GetPosition(this));
            return; // let the ListBox click-select
        }
        // Empty list area: suppress the ListBox's empty-click clear; a plain click
        // clears selection in the up handler, a drag draws the marquee.
        _selectMode = SelectMode.Draw;
        _selectTarget = SelectTarget.ListItems;
        _selectStart = e.GetPosition(this);
        _selectCurrent = _selectStart;
        _selectMoved = false;
        _selectFromEmpty = true;
        e.Handled = true;
    }

    /// <summary>Window-level move drives the marquee regardless of where the
    /// press started (item / list / empty area) — moves bubble to the window.</summary>
    void Window_MouseMove(object s, MouseEventArgs e)
    {
        if (_selectMode == SelectMode.None) return;
        var p = e.GetPosition(this);
        if (_selectMode == SelectMode.Hold)
        {
            if (Math.Abs(p.X - _selectStart.X) >= SystemParameters.MinimumHorizontalDragDistance
                || Math.Abs(p.Y - _selectStart.Y) >= SystemParameters.MinimumVerticalDragDistance)
                CancelMarqueeHold();
            return;
        }
        if (_selectMode != SelectMode.Draw) return;
        if (!_selectMoved)
        {
            if (Math.Abs(p.X - _selectStart.X) < 4 && Math.Abs(p.Y - _selectStart.Y) < 4) return;
            _selectMoved = true;
            SnapshotSelection();
        }
        _selectCurrent = p;
        UpdateMarquee();
    }

    void Window_MouseLeftButtonUp(object s, MouseButtonEventArgs e)
    {
        if (_selectMode == SelectMode.None)
        {
            // Safety: release any stray marquee capture.
            if (Mouse.Captured == FolderList || Mouse.Captured == this) Mouse.Capture(null);
            return;
        }
        try { Mouse.Capture(null); } catch { }
        _selectHoldTimer?.Stop();
        var mode = _selectMode;
        var target = _selectTarget;
        var moved = _selectMoved;
        var fromEmpty = _selectFromEmpty;
        _selectMode = SelectMode.None;
        _selectTarget = SelectTarget.None;
        _selectMoved = false;
        _selectFromEmpty = false;
        _selectStartZone = null;
        _selectStartList = null;
        MarqueeRect.Visibility = Visibility.Collapsed;
        MarqueeRect.Width = MarqueeRect.Height = 0;
        if (mode == SelectMode.Draw && moved)
        {
            e.Handled = true; // gesture consumed by the marquee
            return;
        }
        // Plain click on empty area clears the selection (Explorer behavior).
        if (mode == SelectMode.Draw && !moved && fromEmpty)
        {
            if (target == SelectTarget.ListItems) FolderList.UnselectAll();
            else ClearZoneItemSelection();
        }
    }

    void SnapshotSelection()
    {
        if (_selectTarget == SelectTarget.ZoneItems)
            _selectStartZone = _vm.Items.Where(i => i.IsSelected).Select(i => i.Id).ToHashSet();
        else
            _selectStartList = FolderList.SelectedItems.Cast<FolderEntryViewModel>()
                .Select(f => f.FullPath).ToHashSet();
    }

    void UpdateMarquee()
    {
        double x1 = Math.Min(_selectStart.X, _selectCurrent.X);
        double y1 = Math.Min(_selectStart.Y, _selectCurrent.Y);
        double w = Math.Abs(_selectCurrent.X - _selectStart.X);
        double h = Math.Abs(_selectCurrent.Y - _selectStart.Y);
        MarqueeRect.Visibility = Visibility.Visible;
        Canvas.SetLeft(MarqueeRect, x1);
        Canvas.SetTop(MarqueeRect, y1);
        MarqueeRect.Width = w;
        MarqueeRect.Height = h;
        var r = new Rect(x1, y1, w, h);
        if (_selectTarget == SelectTarget.ZoneItems) ApplyZoneMarquee(r);
        else ApplyListMarquee(r);
    }

    void ApplyZoneMarquee(Rect r)
    {
        for (int i = 0; i < ItemsHost.Items.Count; i++)
        {
            if (ItemsHost.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement fe) continue;
            if (fe.DataContext is not ZoneItemViewModel vm) continue;
            var p0 = fe.TranslatePoint(new Point(0, 0), this);
            bool inRect = r.IntersectsWith(new Rect(p0.X, p0.Y, Math.Max(1, fe.ActualWidth), Math.Max(1, fe.ActualHeight)));
            vm.IsSelected = inRect || (_selectStartZone?.Contains(vm.Id) ?? false);
        }
    }

    void ApplyListMarquee(Rect r)
    {
        foreach (var item in RealizedListItems())
        {
            if (item.DataContext is not FolderEntryViewModel vm) continue;
            var p0 = item.TranslatePoint(new Point(0, 0), this);
            bool inRect = r.IntersectsWith(new Rect(p0.X, p0.Y, Math.Max(1, item.ActualWidth), Math.Max(1, item.ActualHeight)));
            item.IsSelected = inRect || (_selectStartList?.Contains(vm.FullPath) ?? false);
        }
    }

    List<ListBoxItem> RealizedListItems()
    {
        var list = new List<ListBoxItem>();
        void Walk(DependencyObject d)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            {
                var c = VisualTreeHelper.GetChild(d, i);
                if (c is ListBoxItem lbi) list.Add(lbi);
                Walk(c);
            }
        }
        Walk(FolderList);
        return list;
    }

    void ClearZoneItemSelection()
    {
        foreach (var i in _vm.Items) i.IsSelected = false;
    }

    /// <summary>Right-click on a zone item: select it alone unless it is already
    /// part of the current selection (keeps the multi-selection for the menu).</summary>
    void SelectZoneItemUnderCursor(object s)
    {
        var c = s as DependencyObject;
        while (c != null)
        {
            if (c is FrameworkElement fe && fe.DataContext is ZoneItemViewModel vm)
            {
                if (!vm.IsSelected)
                {
                    foreach (var o in _vm.Items) o.IsSelected = false;
                    vm.IsSelected = true;
                }
                return;
            }
            c = VisualTreeHelper.GetParent(c);
        }
    }

    static bool IsWithinZoneChrome(object s)
    {
        var c = s as DependencyObject;
        while (c != null)
        {
            if (c is FrameworkElement fe && fe.Name is "TitleBarBg" or "ControlPoint" or "SubZoneTabsRow"
                or "FolderMappingView" or "BottomBarBg" or "RestoreButton"
                or "GripTL" or "GripTR" or "GripBL" or "GripBR")
                return true;
            c = VisualTreeHelper.GetParent(c);
        }
        return false;
    }

    // ── Item reorder (SnapToGrid) + drop indicator ──

    Zone? OwnerZoneOf(ZoneItemViewModel vm)
    {
        if (vm.SourceZoneId == Guid.Empty || vm.SourceZoneId == _zone.Id) return _zone;
        return _mgr.Zones.FirstOrDefault(z => z.Id == vm.SourceZoneId);
    }

    static int ComputeInsertIndex(List<ZoneItem> others, double dropX, double dropY, int gs)
    {
        double pitch = ZoneLayout.Pitch(gs);
        double vpitch = ZoneLayout.VPitch(gs);
        int row = (int)Math.Round((dropY - ZoneLayout.Pad) / vpitch);
        int col = (int)Math.Round((dropX - ZoneLayout.Pad) / pitch);
        int k = 0;
        foreach (var o in others)
        {
            int r = (int)Math.Round((o.Y - ZoneLayout.Pad) / vpitch);
            int c = (int)Math.Round((o.X - ZoneLayout.Pad) / pitch);
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

        double pitch = ZoneLayout.Pitch(gs);
        double vpitch = ZoneLayout.VPitch(gs);
        double x = 10, y = 10;
        foreach (var it in ordered)
        {
            it.X = ZoneViewModel.SnapToGrid(x, gs);
            it.Y = ZoneViewModel.SnapToGridY(y, gs);
            x += pitch;
            if (x > zw - gs) { x = 10; y += vpitch; }
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
        double pitch = ZoneLayout.Pitch(gs);
        double zw = _zone.Width;
        if (double.IsNaN(zw) || zw < gs + 10) zw = gs + 10;
        int cols = Math.Max(1, (int)Math.Floor((zw - ZoneLayout.Pad - gs) / pitch) + 1);
        int k = Math.Clamp(ComputeInsertIndex(others, dragged.X, dragged.Y, gs), 0, others.Count);

        // The caret sits at a fixed position in the gap between two grid cells
        // (centred in the inter-cell gap), so it keeps the same moderate distance
        // from the icon on every side — left/right and top/bottom are symmetric,
        // and it never follows the dragged icon away.
        double barX, barY;
        bool vertical;

        if (k == 0)
        {
            // Insert before the first item → gap to the left of that cell.
            vertical = true;
            barX = others[0].X - ZoneLayout.CellGap / 2;
            barY = others[0].Y;
        }
        else if (k == others.Count)
        {
            // Insert after the last item.
            var prev = others[others.Count - 1];
            int prevCol = (int)Math.Round((prev.X - ZoneLayout.Pad) / pitch);
            if (prevCol < cols - 1)
            {
                vertical = true;      // same row, to its right
                barX = prev.X + gs + ZoneLayout.CellGap / 2;
                barY = prev.Y;
            }
            else
            {
                vertical = false;     // wraps to a new row below
                barX = 10;
                barY = prev.Y + gs + ZoneLayout.LabelArea + ZoneLayout.CellGap / 2;
            }
        }
        else
        {
            var prev = others[k - 1];
            var next = others[k];
            int prevRow = (int)Math.Round((prev.Y - ZoneLayout.Pad) / pitch);
            int nextRow = (int)Math.Round((next.Y - ZoneLayout.Pad) / pitch);
            if (prevRow == nextRow)
            {
                vertical = true;      // gap between two columns
                barX = next.X - ZoneLayout.CellGap / 2;
                barY = next.Y;
            }
            else
            {
                vertical = false;     // gap between two rows
                barX = 10;
                barY = next.Y - ZoneLayout.CellGap / 2;
            }
        }

        var bar = EnsureDropIndicator();
        if (vertical)
        {
            bar.Width = BarThickness; bar.Height = BarLength;
            Canvas.SetLeft(bar, barX - BarThickness / 2);
            // Center the caret on the icon block (the cell also carries the name area below).
            Canvas.SetTop(bar, barY + (gs - BarLength) / 2);
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
        // Multi-selection menu: only 删除 + 重命名 (single-item items are hidden).
        bool isMulti = vm.IsSelected && _vm.Items.Count(i => i.IsSelected) > 1;
        foreach (var entry in cm.Items)
        {
            if (entry is not MenuItem mi) continue;
            switch (mi.Name)
            {
                case "CtxEmptyRecycle":
                    mi.Visibility = isRecycle && !isMulti ? Visibility.Visible : Visibility.Collapsed;
                    break;
                case "CtxOpen":
                case "CtxOpenLocation":
                    mi.Visibility = isMulti ? Visibility.Collapsed : Visibility.Visible;
                    break;
                case "CtxRename":
                case "CtxDelete":
                    break; // shown in both modes
            }
        }
        if (cm.Items.OfType<Separator>().FirstOrDefault(x => x.Name == "CtxSep1") is { } sep)
            sep.Visibility = isMulti ? Visibility.Collapsed : Visibility.Visible;
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
        var sel = _vm.Items.Where(i => i.IsSelected).ToList();
        if (sel.Count > 1 && sel.Contains(v))
        {
            // Batch rename: base name + sequential suffix — same styled dialog
            // as single-icon rename (matches the mapping view).
            var rnBatch = new Views.RenameDialog(v.Name, _loc["Rename.Batch"], _loc["Rename.BatchPrompt"]) { Owner = this };
            if (rnBatch.ShowDialog() != true) return;
            var baseName = rnBatch.NewName.Trim();
            if (string.IsNullOrEmpty(baseName)) return;
            int n = 0;
            foreach (var it in sel) { n++; it.Name = n == 1 ? baseName : $"{baseName} ({n})"; }
            _mgr.SaveConfig();
            return;
        }
        var rn = new Views.RenameDialog(v.Name) { Owner = this };
        if (rn.ShowDialog() == true) { v.Name = rn.NewName; _mgr.SaveConfig(); }
    }

    void ItemDelete_Click(object s, RoutedEventArgs e)
    {
        if (VM(s) is not ZoneItemViewModel v) return;
        var sel = _vm.Items.Where(i => i.IsSelected).ToList();
        if (sel.Count > 1 && sel.Contains(v))
        {
            if (MessageBox.Show(string.Format(_loc["ZoneItem.DeleteMultiConfirm"], sel.Count),
                    _loc["Item.Delete"], MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            foreach (var it in sel) _vm.DeleteItemCommand.Execute(it);
            return;
        }
        _vm.DeleteItemCommand.Execute(v);
    }

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
        bool TileMode,
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
    ///   1. Regular zone             → _zone.*  (or global when useGlobal)
    ///   2. Merged + Unified         → _zone.MergedGroup*
    ///   3. Merged + Keep Original   → frame (border / corners / BOTH title-bar layers /
    ///        icons / control opacity / bg image) from _zone.MergedGroup*; ONLY the body
    ///        FillColor keeps the displayed zone's own fill (selected sub-zone's, or the
    ///        master's own when no sub-zone is selected).
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
            TileMode:     _zone.TileMode,
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
                TileMode =     _zone.MergedGroupStyle.TileMode,
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

        // Merged + Keep Original → the frame (border, corners, BOTH title-bar layers,
        // icons, control opacity) stays unified from MergedGroupStyle; the body fill
        // keeps the currently-displayed zone's own fill (selected sub-zone's when one
        // is active, otherwise the master's own). The unified background image is
        // disabled in this mode (背景图片随保留原有填充一起禁掉).
        string keepFill = _zone.FillColor;
        if (_zone.MergedGroupMembership.SubZoneIds.Count > 0
            && _vm?.SelectedSubZoneId is Guid selId && selId != _zone.Id)
        {
            var sub = _mgr.Zones.FirstOrDefault(z => z.Id == selId);
            if (sub != null) keepFill = sub.FillColor;
        }

        return regular with
        {
            FillColor =        keepFill,
            BorderColor =      _zone.MergedGroupStyle.BorderColor,
            BorderThickness =  _zone.MergedGroupStyle.BorderThickness,
            TitleBarFillColor = _zone.MergedGroupStyle.TitleBarFillColor,
            TitleTextColor =   _zone.MergedGroupStyle.TitleTextColor,
            IconColor =        _zone.MergedGroupStyle.IconColor,
            ControlOpacity =   _zone.MergedGroupStyle.ControlOpacity,
            CornerRadius =     _zone.MergedGroupStyle.CornerRadius,
            TileMode =         _zone.MergedGroupStyle.TileMode,
            TitleBarAdaptive = _zone.MergedGroupStyle.TitleBarTextColorAdaptive,
            BgImagePath =      "",
            BgImageStretch =   _zone.MergedGroupStyle.BgImageStretch,
            BgImageOffsetX =   _zone.MergedGroupStyle.BgImageOffsetX,
            BgImageOffsetY =   _zone.MergedGroupStyle.BgImageOffsetY,
            BgImageZoom =      _zone.MergedGroupStyle.BgImageZoom,
            BgImageOpacity =   _zone.MergedGroupStyle.BackgroundImageOpacity,
            TitleBarFillIndependent = _zone.MergedGroupStyle.TitleBarFillIndependent,
        };
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
        bool fillIndependent = s.TitleBarFillIndependent && !s.TileMode;
        FillRect.RadiusX = FillRect.RadiusY = fillIndependent ? 0 : s.CornerRadius;
        // ponytail 2026-08-26: the merged master's title bar is TWO layers — the
        // 24px top bar + the 24px sub-zone tab row — so the body fill starts below
        // both (48px), not just below the top bar.
        FillRect.Margin = fillIndependent ? new Thickness(0, TitleBarLayerHeight(), 0, 0) : new Thickness(0);

        // Title bar fill — all title-bar layers share the resolved fill (top bar,
        // merged sub-zone tab row, and the folder-mapping header row).
        try { TitleBarBg.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(s.TitleBarFillColor)!); } catch { }
        try { SubZoneTabsRow.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(s.TitleBarFillColor)!); } catch { }
        try { FolderMapHeaderBg.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(s.TitleBarFillColor)!); } catch { }

        // Background image — computed before the adaptive brushes below so the
        // sampling transform is fresh for both the title-bar and body regions.
        ApplyBackgroundImage(s);

        // Title text — adaptive on → sample the title-bar strip; off → resolved TitleTextColor.
        SolidColorBrush? titleAdaptiveBrush = null;
        if (s.TitleBarAdaptive)
        {
            titleAdaptiveBrush = ResolveTitleBarAdaptiveBrush(s);
            ZoneTitleText.Foreground = titleAdaptiveBrush;
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
            var iBrush = titleAdaptiveBrush!;
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

        // Control-point opacity + TileMode visibility
        ControlPoint.Opacity = Math.Max(0.05, s.ControlOpacity / 100.0);
        var vis = s.TileMode ? Visibility.Collapsed : Visibility.Visible;
        TitleBarBg.Visibility = vis;
        ControlPoint.Visibility = vis;

        // 磁贴模式 = 隐藏底部 8px 分割条。
        if (BottomBarBg != null)
            BottomBarBg.Visibility = s.TileMode ? Visibility.Collapsed : Visibility.Visible;

        // 隐藏应用名 — 遍历 item 容器切换名称 TextBlock 可见性。
        ApplyHideAppName(_zone.HideAppName);

        // 自定义图标（单图标模式）：TileMode + CustomIcon + Items.Count==1 时
        // 隐藏 ItemsHost，双击整个分区打开唯一图标。
        ApplyCustomIcon(s.TileMode && _zone.CustomIcon && _zone.Items.Count == 1);

        // Sub-zone tabs + items — the tab row sits inside the same title-bar band, so its text
        // reuses the top title bar's adaptive brush (merged groups don't get a separate tab-bar
        // sample; the difference is negligible).
        RebuildSubZoneTabs(titleAdaptiveBrush, s.TitleTextColor);
        ApplyItemTextColorAdaptive(s.FillColor);
    }

    /// <summary>遍历 ItemsHost 内的 ContentPresenter，根据 hide 切换名称 TextBlock
    /// （x:Name="ItemNameText"）可见性。容器未生成时无操作 — StatusChanged
    /// 处理器会在容器生成后补一次。</summary>
    void ApplyHideAppName(bool hide)
    {
        if (ItemsHost == null) return;
        for (int i = 0; i < ItemsHost.Items.Count; i++)
        {
            if (ItemsHost.ItemContainerGenerator.ContainerFromIndex(i) is not DependencyObject container) continue;
            var tb = FindVisualChild<TextBlock>(container, tb => tb.Name == "ItemNameText");
            if (tb != null) tb.Visibility = hide ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    bool _customIconOpenFirst;

    /// <summary>自定义图标模式：ItemsHost 隐藏，整窗双击（Window_MouseDoubleClick）
    /// 打开 Items[0]。</summary>
    void ApplyCustomIcon(bool on)
    {
        if (ItemsHost == null) return;
        ItemsHost.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        _customIconOpenFirst = on;
    }

    /// <summary>整窗双击：CustomIcon 开启时打开当前列表的第一项；否则忽略。</summary>
    void Window_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!_customIconOpenFirst) return;
        // 标题栏 / 控件 / 文件夹映射区域的双击不触发打开。
        if (IsWithinZoneChrome(e.OriginalSource)) return;
        var item = _vm.Items.FirstOrDefault();
        if (item == null) return;
        Open(item);
        e.Handled = true;
    }

    /// <summary>Combined title-bar height: 24px top bar + 24px merged sub-zone tab
    /// row + 26px folder-mapping header row (when mapping is enabled).</summary>
    double TitleBarLayerHeight() =>
        24 + (_zone.MergedGroupMembership.SubZoneIds.Count > 0 ? 24 : 0)
           + (ResolveFolderMapping().Enabled ? 26 : 0);

    /// <summary>Effective window size used by the background-image transform — mirrors
    /// <see cref="ApplyBackgroundImage"/>, which falls back to the model size before the
    /// first layout pass has run (e.g. inside the constructor's ApplyStyle).</summary>
    double EffectiveWidth => ActualWidth > 0 ? ActualWidth : _zone.Width;
    double EffectiveHeight => ActualHeight > 0 ? ActualHeight : _zone.Height;

    /// <summary>Parse a hex color string, falling back to white on malformed input.</summary>
    static Color ParseColor(string hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex)!; }
        catch { return Colors.White; }
    }

    /// <summary>The adaptive title-bar strip is always the 24px <see cref="TitleBarBg"/>;
    /// merged groups sample only this top layer, never the sub-zone tab row.</summary>
    const double TitleBarSampleHeight = 24;

    /// <summary>Top edge of the body region in window space (below title bar + tab row).</summary>
    double BodyRegionTop(ResolvedZoneStyle s) =>
        s.TileMode
            ? (_zone.MergedGroupMembership.SubZoneIds.Count > 0 ? TitleBarSampleHeight : 0)
            : TitleBarLayerHeight();

    /// <summary>Title-bar samples: left endpoint, middle, right endpoint.</summary>
    static (double, double)[] TitleBarSamplePoints(double width) => new[]
    {
        (4.0, TitleBarSampleHeight / 2.0),
        (width / 2.0, TitleBarSampleHeight / 2.0),
        (Math.Max(4.0, width - 4.0), TitleBarSampleHeight / 2.0),
    };

    /// <summary>Body samples: the body region's four corners + its center (5 points).</summary>
    static (double, double)[] BodySamplePoints(double top, double bottom, double width)
    {
        double left = 4.0, right = Math.Max(4.0, width - 4.0);
        return new[]
        {
            (left, top),
            (right, top),
            (width / 2.0, (top + bottom) / 2.0),
            (left, bottom),
            (right, bottom),
        };
    }

    /// <summary>Resolve the title-bar adaptive brush. When a background image is present
    /// (and not clipped away by an independent title bar), sample the title-bar strip;
    /// otherwise fall back to the translucent-title-over-fill composite.</summary>
    SolidColorBrush ResolveTitleBarAdaptiveBrush(ResolvedZoneStyle s)
    {
        var titleFill = ParseColor(s.TitleBarFillColor);

        // Independent title bar: its fill is the only layer we own (the desktop behind it
        // is unknowable), so contrast against the fill color itself.
        if (s.TitleBarFillIndependent && !s.TileMode)
            return AdaptiveTextColor.ResolveBrush(titleFill);

        if (BgImage?.Source is BitmapSource bmp && _bgImageScale > 0 && EffectiveWidth > 0)
        {
            var backdrop = ParseColor(s.FillColor);
            var avg = AdaptiveTextColor.AverageImageOver(
                bmp, _bgImageScale, _bgImageOffsetX, _bgImageOffsetY,
                TitleBarSamplePoints(EffectiveWidth), backdrop);
            if (avg is Color c)
                return AdaptiveTextColor.ResolveBrush(AdaptiveTextColor.CompositeOver(titleFill, c));
        }

        // No image under the title bar: translucent title fill over the body fill.
        return AdaptiveTextColor.ResolveBrushOver(s.TitleBarFillColor, s.FillColor);
    }

    void ApplyBackgroundImage(ResolvedZoneStyle s)
    {
        // 标题栏独立填充：背景图与 FillRect 一样不铺到标题栏下方（顶部裁剪）。
        double clipTop = s.TitleBarFillIndependent && !s.TileMode ? TitleBarLayerHeight() : 0;
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

                // Store the placement transform for the adaptive-color samplers:
                // window → source pixel = ((wx - offsetX) / scale, (wy - offsetY) / scale).
                _bgImageScale = utfScale;
                _bgImageOffsetX = zoneCenterX - imgCenterX + zox;
                _bgImageOffsetY = zoneCenterY - imgCenterY + zoy;
            }
            catch { _bgImageScale = 0; BgImage.Opacity = 0; }
        }
        else { _bgImageScale = 0; BgImage.Source = null; BgImage.Opacity = 0; }
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
        var backdrop = ParseColor(fillColor);
        Color? sampled = null;
        if (BgImage?.Source is BitmapSource bmp && _bgImageScale > 0 && EffectiveWidth > 0 && EffectiveHeight > 0)
        {
            var s = ResolveStyle();
            double top = BodyRegionTop(s) + 4.0;
            double bottom = Math.Max(top, EffectiveHeight - 8.0 - 4.0);
            sampled = AdaptiveTextColor.AverageImageOver(
                bmp, _bgImageScale, _bgImageOffsetX, _bgImageOffsetY,
                BodySamplePoints(top, bottom, EffectiveWidth), backdrop);
        }
        var brush = AdaptiveTextColor.ResolveBrush(sampled ?? backdrop);
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
        int gs = _zone.GridSize;
        if (_zone.MergedGroupMembership.SubZoneIds.Count > 0 && _vm.SelectedSubZoneId.HasValue && _vm.SelectedSubZoneId.Value != _zone.Id)
        {
            var subZone = _mgr.Zones.FirstOrDefault(z => z.Id == _vm.SelectedSubZoneId.Value);
            displayItems = subZone?.Items ?? _zone.Items;
            if (subZone != null) gs = subZone.GridSize;
        }
        else
        {
            displayItems = _zone.Items;
        }

        if (displayItems.Count == 0) { _itemCanvas.Width = Math.Max(0, _zone.Width - 2); _itemCanvas.Height = Math.Max(0, _zone.Height - 50); return; }
        double maxX = 0, maxY = 0;
        foreach (var i in displayItems) { if (i.X + gs + 20 > maxX) maxX = i.X + gs + 20; if (i.Y + gs + ZoneLayout.LabelArea + 20 > maxY) maxY = i.Y + gs + ZoneLayout.LabelArea + 20; }
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
                        SubZoneTabsRow.Background = TitleBarBg.Background;
                    }
                    catch
                    {
                        TitleBarBg.Background = new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF));
                        SubZoneTabsRow.Background = TitleBarBg.Background;
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
                SubZoneTabsRow.Background = TitleBarBg.Background;
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

    /// <summary>在 visual tree 中按谓词查找指定类型后代（磁贴模式 item 名称定位）。</summary>
    static T? FindVisualChild<T>(DependencyObject parent, Func<T, bool> pred) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t && pred(t)) return t;
            var deeper = FindVisualChild(child, pred);
            if (deeper != null) return deeper;
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
        RefreshFolderMapping();
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

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        // Mapped folders can change outside the app — rescan (throttled) on focus.
        RefreshFolderMappingIfStale();
    }

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
        _dragTabLastX = _dragTabOrigin.X;

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
        // The selected tab owns the visible folder mapping (sub-zone keeps its own
        // mapping after joining the group) — re-resolve + reload for the new tab.
        RefreshFolderMapping();
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
        _dragTabLastX = double.NaN;
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
            // Leading-edge probe, direction-aware: the swap fires once the tab's
            // leading edge (right edge when dragging right, left edge when dragging
            // left) crosses a neighbour's midpoint — 拖过一半即换位. Probing the
            // fixed left edge made rightward drags fire a whole tab-width late
            // (the pointer had to reach the neighbour's far end — 拖到底).
            bool movingRight = pos.X >= _dragTabLastX;
            _dragTabLastX = pos.X;
            // Convert row coords → StackPanel coords so boundaries align exactly
            // with the accumulated tab widths below.
            double probeX = pos.X - SubZoneTabs.Margin.Left - _dragTabGrabOffset; // left edge
            if (movingRight)
                probeX += dragTab.ActualWidth + dragTab.Margin.Left + dragTab.Margin.Right; // right edge
            int newIndex = ComputeTabDropIndex(probeX);
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
