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

    private static readonly SolidColorBrush CtrlHoverBrush    = Freeze(new(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)));
    private static readonly SolidColorBrush CtrlIdleBrush     = Freeze(new(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)));
    private static readonly SolidColorBrush ItemHoverBrush    = Freeze(new(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)));
    static SolidColorBrush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    // 图标文字（ItemNameText / 次级文件夹名称）固定颜色。声明式绑定到该 DP：
    // 容器实例化瞬间即取到正确颜色，避免先渲染默认 #E0FFFFFF 再在 Loaded 之后
    // 补刷导致的「跳颜色」闪烁。
    private static readonly SolidColorBrush DefaultItemTextBrush = Freeze(new(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF)));
    public static readonly DependencyProperty ItemTextBrushProperty =
        DependencyProperty.Register(nameof(ItemTextBrush), typeof(Brush), typeof(ZoneWindow),
            new PropertyMetadata(DefaultItemTextBrush));
    public Brush ItemTextBrush
    {
        get => (Brush)GetValue(ItemTextBrushProperty);
        set => SetValue(ItemTextBrushProperty, value);
    }

    public bool IsMinimized => RestoreButton.Visibility == Visibility.Visible;
    private readonly ZoneViewModel _vm;
    private readonly LocalizationService _loc = LocalizationService.Instance;
    private readonly ShellIconService _iconService;
    private HwndSource? _src;
    private Canvas? _itemCanvas;
    private Action<string>? _langChanged;

    private bool _dragging;
    private Point _ds, _is;
    private ZoneItemViewModel? _dv;
    private FrameworkElement? _de;
    private System.Windows.Shapes.Rectangle? _dropIndicator;
    private const double BarThickness = 3, BarLength = 80;

    // ── 跨分区拖动图标 ──
    // 源窗口在 Item_MouseMove 里用 WindowFromPoint 命中其它 ZoneWindow 并驱动
    // 其幽灵预览;Item_MouseUp 提交跨分区移动。_externalTarget 只在源窗口上有意义。
    ZoneWindow? _externalTarget;
    System.Windows.Controls.StackPanel? _extGhost;

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
    // The item footprint is one square grid cell (80×80, panel-aligned); the icon is centered inside it.
    double ItemW => _zone.GridSize;
    double ItemH => _zone.GridSize;

    // ── Tile-mode title-bar cut ──
    // 磁贴模式砍掉最上面一层 24px 标题栏:窗口高度同步缩小 24px,内容区高度不变。
    // 组合分区/文件夹映射只砍最上面一层,下面一层(子标签栏/映射头部行)保留。
    const double TileTitleBarCut = 24;
    bool _tileVisual;

    double TileWindowHeight() => _tileVisual ? Math.Max(36, _zone.Height - TileTitleBarCut) : _zone.Height;
    double FullHeightFromWindowHeight() => _tileVisual ? Height + TileTitleBarCut : Height;

    /// <summary>回写模型用的完整高度：磁贴模式下窗口高度不含被砍掉的标题栏，必须加回
    /// TileTitleBarCut，否则 FullHide/解散等路径会把它当成完整高度保存，重新打开时
    /// 每次再砍一刀 → 磁贴窗口被裁剪/位移。</summary>
    public double FullModelHeight => FullHeightFromWindowHeight();

    // 程序化改高度(切磁贴/恢复)时记录期望值 — OnSize 据此跳过自动重排,避免
    // 切换磁贴或恢复普通模式时把图标重新居中。
    double _expectedTileHeight = double.NaN;
    void ApplyProgrammaticHeight(double h)
    {
        _expectedTileHeight = h;
        Height = h;
    }
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
        // ponytail 2026-08-26: 内层图标与主分区同款操作 — 打开/重命名/删除由
        // SubfolderFlyout 事件委托回 ZoneWindow(主分区命令都住在这里);
        // ItemsChanged = flyout 内部换位/删除后保存。
        SubfolderFlyoutView.ItemOpenRequested += OnFlyoutItemOpen;
        SubfolderFlyoutView.ItemOpenLocationRequested += OnFlyoutItemOpenLocation;
        SubfolderFlyoutView.ItemRenameRequested += OnFlyoutItemRename;
        SubfolderFlyoutView.ItemDeleteRequested += OnFlyoutItemDelete;
        SubfolderFlyoutView.ItemsChanged += OnFlyoutItemsChanged;
        SubfolderFlyoutView.ClickOutsideRequested += OnFlyoutClickOutside;
        // ponytail 2026-08-28: 这 4 个事件原先挂在 XAML 属性上 — 但标记编译器对
        // 本程序集 UserControl 元素的 XAML 事件属性会整组静默丢弃(g.cs 里不生成
        // 接线、也无警告),导致 ⚙ 编辑样式/鼠标移出 200ms 自动关闭/内层图标拖出
        // 回分区从功能加入起就一直没生效。必须在代码里订阅。
        SubfolderFlyoutView.MouseEnter += SubfolderFlyoutView_MouseEnter;
        SubfolderFlyoutView.MouseLeave += SubfolderFlyoutView_MouseLeave;
        SubfolderFlyoutView.EditStyleRequested += SubfolderFlyout_EditStyleRequested;
        SubfolderFlyoutView.ItemDragOutRequested += SubfolderFlyout_ItemDragOutRequested;
        // ponytail 2026-08-26: 键盘焦点可能落在 Popup 内(主窗口收不到 Ctrl+A/Delete),
        // flyout 侧再挂一份同样的快捷键处理。
        SubfolderFlyoutView.PreviewKeyDown += (_, e) =>
        {
            if (TryHandleFlyoutKeys(e)) e.Handled = true;
        };
        _vm = new ZoneViewModel(zone, mgr, icons);
        _vm.IsLocked = zone.IsLocked;
        DataContext = _vm;
        Left = zone.X; Top = zone.Y;
        Width = SanitizeW(zone.Width); Height = SanitizeW(zone.Height);
        ApplyStyle();
        // 磁贴模式:构造期即砍掉最上面一层标题栏高度(ApplyStyle 已设 _tileVisual)。
        ApplyProgrammaticHeight(TileWindowHeight());
        // Acrylic is applied in OnLoad (needs valid HWND)
        ZoneTitleText.Text = zone.Name;
        SetRestoreIcon();
        ApplyLoc();
        FolderList.ItemsSource = _folderEntries;
        _vmItemsChangedHandler = (_, _) =>
        {
            UpdateCanvasSize();
            // 自定义图标按图标数立即同步（只切换 ItemsHost 可见性，不依赖容器）。
            // 应用名隐藏由模板 DataTrigger 声明式处理（容器实例化瞬间即生效），
            // 不在此延迟遍历 — 否则缩窗重排容器时会出现「名字先显示再隐藏」的闪烁。
            ApplyCustomIcon(_zone.TileMode && _zone.CustomIcon && _zone.Items.Count == 1);
        };
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
        // StatusChanged so ApplyItemTextColor fires the moment containers exist,
        // covering first-open, hide→show, and any subsequent item changes. Constructor
        // ApplyStyle still runs (it handles fill/border/title-bar which are XAML-static)
        // but its item walk is a no-op until this fires.
        _itemsHostStatusChangedHandler = (_, _) =>
        {
            if (ItemsHost.ItemContainerGenerator.Status
                == System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated)
            {
                ApplyItemTextColor();
                ReapplyTileItemVisuals();
                // ponytail 2026-08-29: 次级分区图标悬停自动展开 — 容器每次生成都
                // 重挂(先摘后挂,幂等):悬停 350ms 且 HostSubItem.HoverAutoExpand=true
                // 时自动打开浮层,移出取消。
                for (int i = 0; i < ItemsHost.Items.Count; i++)
                {
                    if (ItemsHost.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement fe)
                    {
                        fe.MouseEnter -= ItemContainer_MouseEnter;
                        fe.MouseEnter += ItemContainer_MouseEnter;
                        fe.MouseLeave -= ItemContainer_MouseLeave;
                        fe.MouseLeave += ItemContainer_MouseLeave;
                    }
                }
            }
        };
        ItemsHost.ItemContainerGenerator.StatusChanged += _itemsHostStatusChangedHandler;
        if (!_zone.IsVisible) ApplyHidden();
        // ponytail: ApplyStyle rebuilds sub-zone tabs internally with the resolved title
        // text color. No external RebuildSubZoneTabs call needed here.
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
        // ponytail 2026-08-28 边框残影修复 — 收起完成时把「OS 层装饰」全套重断言一遍:
        // 玻璃、Win11 圆角、DWM 框架阴影。收起后窗口仍保持整窗大小,这三样任何一样
        // 残留都会显示成「原窗口轮廓的边框残影」(玻璃染色、圆角描边、深色阴影环)。
        // 仅靠动画开始前的 DisableBlur 不够:窗口失去焦点时经设置面板/热键触发的
        // 收起可能被后续 ApplyStyle/RefreshItems 打断,完成回调就是最后的保险。
        _hover.Collapsed += OnHoverCollapsed;
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
        CtxImport.Header = _loc["ZoneMenu.Import"];
        CtxImport2.Header = _loc["ZoneMenu.Import"];
        CtxImportFolder.Header = _loc["ZoneMenu.ImportFolder"];
        CtxImportFiles.Header = _loc["ZoneMenu.ImportFiles"];
        CtxImportFolder2.Header = _loc["ZoneMenu.ImportFolder"];
        CtxImportShell.Header = _loc["Zone.ImportShellItems"];
        CtxImportShell2.Header = _loc["Zone.ImportShellItems"];
        CtxNew.Header = _loc["ZoneMenu.New"];
        CtxNew2.Header = _loc["ZoneMenu.New"];
        CtxNewFolder.Header = _loc["ZoneMenu.NewFolder"];
        // ponytail 2026-08-27: "新建次级分区"(原"新建次级文件夹")— Zone 与 SubZone 概念合并,
        // 命名统一;i18n 键 Subfolder.* → SubZone.*。
        CtxNewSubZone.Header = _loc["SubZone.New"];
        CtxNewSubZone2.Header = _loc["SubZone.New"];
        CtxNewTxt.Header = _loc["ZoneMenu.NewTxt"];
        CtxNewDocx.Header = _loc["ZoneMenu.NewDocx"];
        CtxNewPptx.Header = _loc["ZoneMenu.NewPptx"];
        CtxNewXlsx.Header = _loc["ZoneMenu.NewXlsx"];
        CtxNewFolder2.Header = _loc["ZoneMenu.NewFolder"];
        CtxNewTxt2.Header = _loc["ZoneMenu.NewTxt"];
        CtxNewDocx2.Header = _loc["ZoneMenu.NewDocx"];
        CtxNewPptx2.Header = _loc["ZoneMenu.NewPptx"];
        CtxNewXlsx2.Header = _loc["ZoneMenu.NewXlsx"];
        CtxMapFolderNew.Header = _loc["FolderMap.MenuMap"];
        CtxMapFolder2.Header = _loc["FolderMap.MenuMap"];
        CtxDisbandAll.Header = _loc["Merge.DisbandAll"];
        CtxSettings.Header = _loc["ZoneMenu.Settings"];
        // ponytail 2026-08-27: 切语言时同步刷新 CtxLock(吸取教训 — ApplyLockState 不在
        // LanguageChanged 路径上,XAML 静态绑定只读一次,需手动同步)。
        CtxLock.Header = _loc[_zone.IsLocked ? "Common.Unlock" : "Common.Lock"];
        CtxMinimize.Header = _loc["ZoneMenu.Minimize"];
        CtxDelete.Header = _loc["ZoneMenu.DeleteZone"];
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

    void Window_Deactivated(object? s, EventArgs e)
    {
        // 桌面层策略:与锁定态一致,失去焦点后回落到壁纸上方,不再浮在应用窗口之上。
        // IsVisible 守卫防关窗 teardown 期间 EnsureHandle 抛异常(时钟/日历同款)。
        if (IsVisible) NativeMethods.PinBelowProgman(this);
        // 拖拽中失焦(如 Alt+Tab):清掉目标窗口上的幽灵,避免残留。
        _externalTarget?.HideExternalDropGhost();
        _externalTarget = null;
        // ponytail 2026-08-28: 失活 + 光标在浮层之外 → 关浮层(点击桌面/其他分区/其他
        // 应用时它们夺走激活)。光标守卫防误伤:Popup 已设 WS_EX_NOACTIVATE,层内点击
        // 不应再失活,但即便残余路径触发,只要光标还在层内就不关(否则又回到
        // "点层内任意位置浮层自动回收"的回归)。
        if (SubfolderFlyoutPopup.IsOpen && !_flyoutClosing && !SubfolderFlyoutView.ContainsScreenCursor())
        {
            DzTrace.Log($"[SubFlyout] ZoneWindow.Window_Deactivated: 光标在浮层外 → CloseSubfolderFlyout (popupOpen={SubfolderFlyoutPopup.IsOpen})");
            CloseSubfolderFlyout();
        }
    }

    void OnLoad(object s, RoutedEventArgs e)
    {
        DesktopLayer.BringToFront(this); NativeMethods.SetToolWindow(this);
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
            // 已知文件夹(文档/图片/音乐/视频等)直接关联真实路径 — "::{GUID}" 壳
            // 无法被 shell 解析(打不开/空壳),转成真实 Folder 项。
            var folderPath = ShellLocationResolver.ResolveKnownFolderPath(spec);
            if (folderPath != null)
                AddItem(new ZoneItem(name, folderPath, ItemType.Folder, 0, 0), sx, sy);
            else
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
            double w = ItemW;
            double h = ItemH;
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

    /// <summary>ponytail 2026-08-26: 解析 SubFolder 打开的填充 — 跟随主分区时取
    /// ResolveStyle() 的主分区主体填充(填充色/背景图/液态玻璃,不含边框);不跟随时取
    /// SubFolder 自身的 override 字段。边框固定不同步(设计如此)。跟随模式下自身
    /// 的液态玻璃/背景图被禁用(面板里灰显),渲染完全取自主分区。</summary>
    SubfolderFill ResolveSubfolderFill(ZoneItem sub)
    {
        if (!sub.FillFollowsZone)
        {
            var f = SubfolderFill.FromOverride(sub);
            // ponytail 2026-08-29: 未设置 override 填充色时沿用主分区解析填充 — 纯默认
            // 3% 透明会让浮层"隐形"(圆角/内容都看不清),用户体验为"设置不生效"。
            if (string.IsNullOrEmpty(sub.FillColorOverride))
                return f with { FillHex = ResolveStyle().FillColor };
            return f;
        }
        var s = ResolveStyle();
        return new SubfolderFill(
            s.FillColor, 100,
            s.BgImagePath, s.BgImageOpacity,
            _zone.EnableLiquidGlass ? _zone.GlassColorMode : null,
            _zone.GlassBlurAmount, _zone.GlassTintOpacity, _zone.GlassTintLuminosity);
    }

    void OpenSubfolderFlyout(ZoneItem sub)
    {
        DzTrace.Log($"[SubEdit] OpenSubfolderFlyout: id={sub.Id.ToString("N")[..8]} FillFollows={sub.FillFollowsZone} Corner={sub.CornerRounded} Hover={sub.HoverAutoExpand} Fill={sub.FillColorOverride}");
        // 默认按"点击/菜单打开"处理 — 不参与自动收回;悬停打开在 OnFlyoutHoverTick
        // 打开后置 true 并启动轮询。
        _flyoutOpenedByHover = false;
        // ponytail 2026-08-26: 左键/双击/菜单"打开"已开启的同一个 SubFolder → 直接播
        // 关闭动画,绝不重播打开动画。注意:点击图标时,鼠标按下已触发"点击外部"的
        // 关闭动画(_flyoutClosing=true),此时松手的再次打开必须直接 return —
        // 否则会把正在关闭的 flyout Reset 重开,视觉上卡一帧。
        if (SubfolderFlyoutPopup.IsOpen && SubfolderFlyoutView.ViewModel?.HostSubItem.Id == sub.Id)
        {
            if (!_flyoutClosing) CloseSubfolderFlyout();
            return;
        }
        var token = ++_flyoutOpenToken;
        _flyoutClosing = false;
        var vm = new SubfolderFlyoutViewModel(sub, _iconService, ResolveSubfolderFill(sub));
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
        SubfolderFlyoutView.ResetToClosed();
        SubfolderFlyoutPopup.IsOpen = true;

        // 等布局完成后:①按"图标屏幕中心"一次性定死 flyout 的屏幕位置(AbsolutePoint
        // offset,确定函数)②按同一份位置反推 TransformGroup 缩放锚点 ③播打开动画。
        // 展开原点 = 图标中心,每次打开完全一致。click-outside 捕获也延后到这里
        // (Popup 的 HWND/可视树就绪后再 Capture 才可靠)。
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_flyoutOpenToken != token) return; // 已被新的 open/close 取代
            SubfolderFlyoutView.HookClickOutside();
            TryOpenFlyoutAnimated(sub);
        }), System.Windows.Threading.DispatcherPriority.Loaded);
        vm.IsOpen = true;
        // ponytail 2026-08-29: 悬停自动展开开关打开时启动"自动收回"轮询(光标离开
        // 浮层 450ms 收回,不受浮层鼠标捕获饿死容器事件的影响)。
        StartFlyoutAutoClosePoll();
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
        var (pos, c) = SubfolderFlyout.ComputePosAndAnchor(container, new Size(SubfolderFlyoutView.ActualWidth, SubfolderFlyoutView.ActualHeight));
        SubfolderFlyoutPopup.HorizontalOffset = pos.X;
        SubfolderFlyoutPopup.VerticalOffset = pos.Y;
        SubfolderFlyoutView.SetAnchor(c);
        // ponytail 2026-08-26: 真玻璃 — 与主分区同配方给 Popup HWND 开 DWM 模糊,
        // 视觉上与主分区玻璃一致;失败才显示渐变兜底(ShowGlassFallback)。
        var flyoutVm = SubfolderFlyoutView.ViewModel;
        if (flyoutVm != null && flyoutVm.Fill != null)
            flyoutVm.ShowGlassFallback = !SubfolderFlyoutView.TryApplyRealGlass(flyoutVm.Fill);
        // ponytail 2026-08-29: Popup HWND 的 Win11 DWM 圆角偏好跟随宿主 SubFolder
        // 的圆角/尖角设置(内容 CornerRadius 之外的第二道保险)。
        SubfolderFlyoutView.ApplyCornerPref();
        SubfolderFlyoutView.AnimateOpen();
    }

    void CloseSubfolderFlyout()
    {
        if (!SubfolderFlyoutPopup.IsOpen || _flyoutClosing) return;
        StopFlyoutAutoClosePoll();
        _flyoutClosing = true;
        _flyoutCloseTimer?.Stop();
        _flyoutCloseTimer = null;
        var token = _flyoutOpenToken;
        SubfolderFlyoutView.AnimateClose(onComplete: () =>
        {
            // 关闭动画期间又点开了另一个 SubFolder(token 已变)→ 不要误关新开的 Flyout。
            if (_flyoutOpenToken != token) { _flyoutClosing = false; return; }
            _flyoutClosing = false;
            SubfolderFlyoutPopup.IsOpen = false;
            SubfolderFlyoutView.ViewModel = null;
        });
    }

    // ── click-outside 关闭(分区空白 / 桌面空白)──
    // 捕获/卸载与 WPF handler 注册已下沉到 SubfolderFlyout.HookClickOutside / UnhookClickOutside;
    // 这里只订阅 SubfolderFlyoutView.ClickOutsideRequested 拿到事件做 popup 状态判定。
    void OnFlyoutClickOutside(System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!SubfolderFlyoutPopup.IsOpen) return;
        // ponytail 2026-08-27: 右键菜单打开期间,任何按下一律不关闭 — 菜单 Popup 的
        // 按下/菜单项点击是"点击外部"判定最容易误伤的场景,直接整段豁免。
        if (SubfolderFlyoutView.IsContextMenuOpen) return;
        // ponytail 2026-08-26: 右键菜单(ContextMenu)里的点击不算"点击外部"。右键打开
        // 菜单后点菜单项时,按下点属于菜单自己的 Popup 源 — 旧实现用 GetPosition
        // 跨源换算,抛异常被 catch 吞掉后反而走了关闭分支,这就是"右键之后还触发
        // 关闭动画"的根源。现在:按下点属于其它 Popup 源(右键菜单等)→ 直接豁免;
        // 属于分区窗口等非 Popup 源 → 照常关闭;异常一律保守不关闭。
        var flyoutSrc = System.Windows.PresentationSource.FromVisual(SubfolderFlyoutView);
        if (e.OriginalSource is System.Windows.Media.Visual d
            && System.Windows.PresentationSource.FromVisual(d) is { } pressSrc
            && !ReferenceEquals(pressSrc, flyoutSrc))
        {
            if (pressSrc.RootVisual?.GetType().Name == "PopupRoot") return; // 右键菜单等 Popup
            CloseSubfolderFlyout(); // 分区窗口/其它窗口
            return;
        }
        // 同一源(Flyout 自己的 Popup 内)→ 坐标判定:落在 Flyout 范围内不算外部。
        try
        {
            var p = e.GetPosition(SubfolderFlyoutView);
            if (p.X >= 0 && p.Y >= 0
                && p.X <= SubfolderFlyoutView.ActualWidth && p.Y <= SubfolderFlyoutView.ActualHeight)
                return;
        }
        catch
        {
            // 跨源/未连接等异常 → 保守处理:不关闭(别再把异常误判成"点击外部")。
            return;
        }
        CloseSubfolderFlyout();
    }

    // ponytail 2026-08-26: 鼠标进入 Flyout 取消自动关闭 timer,确保点击
    // Style 按钮/拖出等交互不被中断。
    void SubfolderFlyoutView_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _flyoutCloseTimer?.Stop();
        _flyoutCloseTimer = null;
    }

    // ponytail 2026-08-26: 鼠标离开 Flyout 后自动关闭。
    // ponytail 2026-08-29: 自动收回跟随"鼠标悬停自动展开"开关(开关关闭时移出不自动
    // 收回,点击外部仍可关);收回时长与分区 HoverExpandBehavior 的 2s 一致。
    void SubfolderFlyoutView_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!SubfolderFlyoutPopup.IsOpen) return;
        // 框选拖拽中(可能把鼠标拖出 Flyout 范围)不自动关闭。
        if (SubfolderFlyoutView.IsMarqueeActive) return;
        // 右键菜单打开中(菜单 Popup 抢走鼠标 → Flyout 收到 MouseLeave)不自动关闭。
        if (SubfolderFlyoutView.IsContextMenuOpen) return;
        if (SubfolderFlyoutView.ViewModel?.HostSubItem.HoverAutoExpand != true) return;
        _flyoutCloseTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2000) };
        _flyoutCloseTimer.Tick -= OnFlyoutCloseTick;
        _flyoutCloseTimer.Tick += OnFlyoutCloseTick;
        _flyoutCloseTimer.Stop();
        _flyoutCloseTimer.Start();
    }

    void OnFlyoutCloseTick(object? s, EventArgs e)
    {
        _flyoutCloseTimer?.Stop();
        _flyoutCloseTimer = null;
        if (!SubfolderFlyoutPopup.IsOpen) return;
        // ponytail 2026-08-27: 右键菜单 Popup 抢走鼠标 → Flyout 收 MouseLeave → 200ms
        // timer 启动;若 ContextMenuOpening 恰好晚于 MouseLeave 生效,这里必须复查,
        // 否则右键打开菜单后 flyout 会被误关(用户反馈"右键触发关闭动画")。
        if (SubfolderFlyoutView.IsContextMenuOpen || SubfolderFlyoutView.IsMarqueeActive) return;
        // 鼠标已回到 Flyout 内(菜单在附近开关等瞬态)→ 不关。
        if (IsMouseInsideFlyout()) return;
        CloseSubfolderFlyout();
    }

    /// <summary>鼠标当前是否落在 SubFolder Flyout 范围内(跨窗口也有效,用于
    /// timer 关闭前的最终复查)。</summary>
    bool IsMouseInsideFlyout()
    {
        try
        {
            var p = System.Windows.Input.Mouse.GetPosition(SubfolderFlyoutView);
            return p.X >= 0 && p.Y >= 0
                && p.X <= SubfolderFlyoutView.ActualWidth && p.Y <= SubfolderFlyoutView.ActualHeight;
        }
        catch { return false; }
    }

    // ── 次级分区图标悬停自动展开(ponytail 2026-08-29) ──
    // "鼠标悬停自动展开"开关打开时:悬停次级分区图标 350ms → 自动打开浮层;移出图标
    // 取消。与点击打开/点击再关、移出浮层 200ms 自动收回共用同一套开合路径。

    System.Windows.Threading.DispatcherTimer? _flyoutHoverTimer;
    ZoneItem? _flyoutHoverTarget;

    // ── 悬停自动展开语义的「自动收回」轮询(ponytail 2026-08-29) ──
    // 浮层 HookClickOutside 会持有鼠标捕获,分区窗口里图标容器的 MouseEnter/Leave
    // 在捕获期间收不到消息 — "离开图标→收回" 依赖容器事件会静默失效(实测悬停展开
    // 后移出光标浮层不收回)。改为轮询物理光标:打开浮层且 HostSubItem.HoverAutoExpand
    // 时启动 150ms 定时器,光标不在浮层内(GetWindowRect 真实矩形)累计 3 次 ≈450ms
    // 即收回;光标进入浮层清零计数,未进入则持续累计。

    System.Windows.Threading.DispatcherTimer? _flyoutAutoClosePoll;
    int _flyoutAutoCloseOutsideTicks;
    /// <summary>本次浮层是否由悬停自动展开打开 — 只有悬停打开的才参与"离开即收回";
    /// 点击打开的与分区"点击总是展开"语义一致,不被自动收回。</summary>
    bool _flyoutOpenedByHover;

    void StartFlyoutAutoClosePoll()
    {
        StopFlyoutAutoClosePoll();
        if (!_flyoutOpenedByHover) return;
        if (SubfolderFlyoutView.ViewModel?.HostSubItem.HoverAutoExpand != true) return;
        _flyoutAutoCloseOutsideTicks = 0;
        _flyoutAutoClosePoll ??= new System.Windows.Threading.DispatcherTimer
        {
            // ponytail 2026-08-29: 与分区 HoverExpandBehavior._exitTimer 一致的 2 秒收回 —
            // 200ms × 10 次(光标持续在浮层外)。分区用的是"光标出窗 2s 后收起"。
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _flyoutAutoClosePoll.Tick -= OnFlyoutAutoClosePollTick;
        _flyoutAutoClosePoll.Tick += OnFlyoutAutoClosePollTick;
        _flyoutAutoClosePoll.Start();
    }

    void StopFlyoutAutoClosePoll()
    {
        _flyoutAutoClosePoll?.Stop();
        _flyoutAutoCloseOutsideTicks = 0;
    }

    void OnFlyoutAutoClosePollTick(object? s, EventArgs e)
    {
        if (!SubfolderFlyoutPopup.IsOpen || _flyoutClosing)
        {
            StopFlyoutAutoClosePoll();
            return;
        }
        if (SubfolderFlyoutView.ViewModel?.HostSubItem.HoverAutoExpand != true)
        {
            StopFlyoutAutoClosePoll();
            return;
        }
        // 严格 0px 判定:光标真正在浮层矩形内才清零;停在图标上(浮层右 8px 外)算在层外。
        if (SubfolderFlyoutView.ContainsScreenCursor(0))
        {
            _flyoutAutoCloseOutsideTicks = 0;
            return;
        }
        _flyoutAutoCloseOutsideTicks++;
        if (_flyoutAutoCloseOutsideTicks >= 10)
        {
            DzTrace.Log("[SubFlyout] 自动收回轮询: 光标在浮层外 2s → CloseSubfolderFlyout");
            StopFlyoutAutoClosePoll();
            CloseSubfolderFlyout();
        }
    }

    void ItemContainer_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        // ponytail 2026-08-29: 拖动图标/框选进行中禁用悬停自动展开 — 拖动时光标扫过
        // 其他次级分区图标会不断触发浮层打开(Popup 激活 + 鼠标捕获导致拖动卡顿)。
        if (_dragging || _selectMode != SelectMode.None) return;
        if (sender is not FrameworkElement fe) return;
        if (fe.DataContext is not ZoneItemViewModel vm || vm.Type != ItemType.SubFolder) return;
        var sub = ResolveSourceZoneItem(vm);
        if (sub == null || !sub.HoverAutoExpand) return;
        // 已在开合动画中的同一浮层 → 不重复调度。
        if (SubfolderFlyoutPopup.IsOpen && SubfolderFlyoutView.ViewModel?.HostSubItem.Id == sub.Id) return;
        _flyoutHoverTarget = sub;
        _flyoutHoverTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _flyoutHoverTimer.Tick -= OnFlyoutHoverTick;
        _flyoutHoverTimer.Tick += OnFlyoutHoverTick;
        _flyoutHoverTimer.Stop();
        _flyoutHoverTimer.Start();
    }

    void ItemContainer_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        // 只取消悬停展开计时;自动收回交给轮询(浮层持捕获时容器 event 收不到,
        // 这里做收回判定不可靠)。
        _flyoutHoverTimer?.Stop();
        _flyoutHoverTarget = null;
    }

    void OnFlyoutHoverTick(object? s, EventArgs e)
    {
        _flyoutHoverTimer?.Stop();
        var sub = _flyoutHoverTarget;
        _flyoutHoverTarget = null;
        if (sub == null) return;
        // ponytail 2026-08-29: 计时期间开始拖动/框选 → 放弃本次悬停展开。
        if (_dragging || _selectMode != SelectMode.None) return;
        // 浮层已开(点击打开等)→ 交给既有交互,不重复开关。
        if (SubfolderFlyoutPopup.IsOpen && SubfolderFlyoutView.ViewModel?.HostSubItem.Id == sub.Id) return;
        OpenSubfolderFlyout(sub);
        _flyoutOpenedByHover = true;
        StartFlyoutAutoClosePoll();
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
        if (flyout.ViewModel == null)
        {
            DzTrace.Log("[SubFlyout] ZoneWindow.EditStyleRequested: ViewModel 为空,中止");
            return;
        }
        DzTrace.Log($"[SubFlyout] ZoneWindow.EditStyleRequested: host={flyout.ViewModel.HostSubItem.Id} name={flyout.ViewModel.HostSubItem.Name} anchor={flyout.StyleBtnScreenDip?.ToString() ?? "null"} popupOpen={SubfolderFlyoutPopup.IsOpen} flyoutClosing={_flyoutClosing}");
        // ponytail 2026-08-26: ensure the management window exists before routing the
        // property editor — PropertyWindowService is a no-op while ManagementWindow is
        // null (startup with StartMinimized + zones shown directly). See App.EnsureManagementWindow.
        (System.Windows.Application.Current as App)?.EnsureManagementWindow();
        // ponytail 2026-08-28: 贴 ⚙ 点击点弹出 — 历史位置可能罩住 ⚙(✕ 落在光标下,
        // 用户下一次点击把窗口关掉,表现为"打不开")。锚点缺失时走旧路径。
        if (flyout.StyleBtnScreenDip is { } anchor)
            PropertyWindowService.OpenOrFocus(flyout.ViewModel.HostSubItem, this, anchor);
        else
            PropertyWindowService.OpenOrFocus(flyout.ViewModel.HostSubItem, this);
    }

    /// <summary>拖出:从 Flyout 里把一个内层图标拖回主分区。以 itemVm 为 payload 发起
    /// DragDrop,Window_Drop 里 TryFindOwnerSubfolder 命中 → MoveOutOfSubfolder 移回分区。</summary>
    void SubfolderFlyout_ItemDragOutRequested(ZoneItem hostSub, ZoneItemViewModel itemVm)
    {
        _flyoutCloseTimer?.Stop();
        _flyoutCloseTimer = null;
        try { DragDrop.DoDragDrop(SubfolderFlyoutView, itemVm, DragDropEffects.Move); }
        finally
        {
            ClearSubfolderDragScale();
            // DoDragDrop 会顶掉 Flyout 的子树捕获 — 拖放结束后还回,否则后续
            // Flyout 内部点击会被"点击外部"误判关闭。
            if (SubfolderFlyoutPopup.IsOpen)
            {
                try { System.Windows.Input.Mouse.Capture(SubfolderFlyoutView, System.Windows.Input.CaptureMode.SubTree); } catch { }
            }
        }
    }

    // ── 内层图标与主分区同款操作(委托自 SubfolderFlyout) ──

    void OnFlyoutItemOpen(ZoneItemViewModel vm)
    {
        try { ShellLocationResolver.Open(vm.TargetPath, vm.Type); }
        catch (Exception ex)
        {
            MessageBox.Show($"{_loc["Item.FailedToOpen"]}\n{ex.Message}",
                _loc["Item.FailedToOpen.Title"], MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Flyout 内层图标"打开所在位置" — 与主分区图标同款逻辑(单一入口)。</summary>
    void OnFlyoutItemOpenLocation(ZoneItemViewModel vm) => OpenItemLocation(vm);

    /// <summary>打开所在位置:ShellLocation 直接解析打开;快捷方式/应用在资源管理器中
    /// 定位到文件;其余按目录打开。主分区图标与 flyout 内层图标共用。</summary>
    void OpenItemLocation(ZoneItemViewModel v)
    {
        if (v.Type == ItemType.ShellLocation)
        {
            ShellLocationResolver.Open(v.TargetPath, v.Type);
            return;
        }
        if (v.Type is ItemType.Shortcut or ItemType.Application)
        {
            var d = Path.GetDirectoryName(v.TargetPath);
            if (!string.IsNullOrEmpty(d))
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{v.TargetPath}\"");
        }
        else
        {
            System.Diagnostics.Process.Start("explorer.exe", v.TargetPath);
        }
    }

    void OnFlyoutItemRename(ZoneItemViewModel vm)
    {
        // 与主分区单个图标重命名同款弹窗(RenameDialog)。
        var rn = new Views.RenameDialog(vm.Name) { Owner = this };
        if (rn.ShowDialog() == true && !string.IsNullOrWhiteSpace(rn.NewName))
        {
            vm.Name = rn.NewName; // ZoneItemViewModel.Name 直写底层 ZoneItem
            _mgr.SaveConfig();
        }
    }

    void OnFlyoutItemDelete(ZoneItemViewModel vm)
    {
        // 与主分区一致:单个删除直接删(无确认);多选删除弹确认后一次全删。
        // ItemVms.Remove → CollectionChanged 写回 HostSubItem.SubItems →
        // 图标格缩略图 / flyout 网格自动刷新。
        var fvm = SubfolderFlyoutView.ViewModel;
        if (fvm == null) return;
        var sel = fvm.ItemVms.Where(i => i.IsSelected).ToList();
        if (sel.Count > 1 && sel.Contains(vm))
        {
            if (MessageBox.Show(string.Format(_loc["ZoneItem.DeleteMultiConfirm"], sel.Count),
                    _loc["Item.Delete"], MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            foreach (var it in sel) fvm.ItemVms.Remove(it);
        }
        else
        {
            fvm.ItemVms.Remove(vm);
        }
        OnFlyoutItemsChanged();
    }

    /// <summary>flyout 内部换位/删除已写回模型 → 落盘 + 通知。</summary>
    void OnFlyoutItemsChanged()
    {
        _mgr.SaveConfig();
        _mgr.NotifyChanged();
    }

    void SubfolderFlyoutPopup_Closed(object? s, EventArgs e)
    {
        // ponytail 2026-08-26: 关闭 Popup 子窗口上的真玻璃(失败无害)。
        SubfolderFlyoutView.DisableGlass();
        StopFlyoutAutoClosePoll();
        // 断开 host SubFolder 的 SubItems 订阅,避免 handler 泄漏到已关闭的 flyout。
        if (_subItemsChangedHandler != null && _subItemsHost != null)
            _subItemsHost.PropertyChanged -= _subItemsChangedHandler;
        _subItemsChangedHandler = null;
        _subItemsHost = null;
        _flyoutOriginContainer = null;
        SubfolderFlyoutView.UnhookClickOutside();
    }

    /// <summary>Re-size the SubfolderFlyout's inner UniformGrid when SubItems grows
    /// past 4 or 9 (2×2 → 3×3 → 4×4). Mirrors SubfolderFlyout.SizeInnerGrid without
    /// widening that class's public surface. Walks the flyout's visual tree looking
    /// for the UniformGrid named "InnerGrid".</summary>
    void ResizeFlyoutGrid(int itemCount)
    {
        // 图标超出后网格依次扩大:2×2 → 3×3 → 4×4 → … → 9×9 (cols = ⌈√n⌉,最少 2)。
        int cols = Math.Max(2, (int)Math.Ceiling(Math.Sqrt(itemCount)));
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
        Width = _zone.Width; ApplyProgrammaticHeight(TileWindowHeight()); Left = _zone.X; Top = _zone.Y;
        // 已经展开显示的窗口不再重复播放 wave 动画(再次「全部显示」时跳过)，
        // 但仍走 SnapToExpanded 以维持「永久展开」(不会因鼠标离开而自动收起)。
        bool alreadyExpanded = _hover?.IsExpanded == true
            && MainContent.Visibility == Visibility.Visible
            && RestoreButton.Visibility != Visibility.Visible;
        if (waveDelayMs > 0 && !alreadyExpanded)
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
            // ponytail 2026-08-28: 从恢复按钮态展开走展开动画(与 CollapseAnimated
            // 对称——关有开也要有,样式面板开关窗口从此两向都有动画);已展开的
            // 重复 Show(如「全部显示」)仍瞬时对齐,不重播。
            bool fromButton = RestoreButton.Visibility == Visibility.Visible;
            MainContent.Visibility = Visibility.Visible; RestoreButton.Visibility = Visibility.Collapsed;
            if (fromButton) _hover?.ExpandAnimated(permanent: true);
            else _hover?.SnapToExpanded();
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
        DesktopLayer.BringToFront(this);
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
            _zone.X = Left; _zone.Y = Top; _zone.Width = Width; _zone.Height = FullHeightFromWindowHeight();
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
            // ponytail 2026-08-28 边框残影修复 — 与三个小挂件(HideClock/HideCalendar/
            // HideNote)同款:收起分支必须同步关闭 DWM 玻璃。分区此前只依赖 Collapsed
            // 事件在动画结束后关玻璃,收起期间(以及动画被外部打断时)整窗大小的
            // 丙烯酸/边框会残留为「原窗口轮廓残影」;设置面板与全部最小化两条路径
            // 都在窗口失去焦点的情况下触发,残影最明显。
            NativeMethods.DisableRoundedCorners(this);
            AcrylicHelper.DisableBlur(this);
            DesktopLayer.BringToFront(this);
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
            ApplyProgrammaticHeight(_zone.Height < 100 ? 300 : TileWindowHeight());
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
            // ponytail: Fix C — re-apply body content color after RefreshItems wipes the
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

        DesktopLayer.BringToFront(this);
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

    // ── Window-level mouse: body drag + marquee (swapped per user request) ──

    // ponytail: 直接拖拽空白 = 框选；Ctrl+拖拽 = 拖动整窗（复用 _snapDrag 合并检测）。
    // 双击由 Window_MouseDoubleClick 处理（自定义图标打开）。
    void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is TextBox) return; // title inline editing
        // Items / list / chrome presses are owned by their own handlers.
        if (IsOnFolderEntry(e.OriginalSource) || IsOnItem(e.OriginalSource)) return;
        if (IsWithinZoneChrome(e.OriginalSource)) return;
        if (MainContent.Visibility != Visibility.Visible) return;
        if (FolderMappingView.Visibility == Visibility.Visible) return;

        // 自定义图标模式下第二次按下（ClickCount==2）不启动框选，交给双击打开。
        if (_customIconOpenFirst && e.ClickCount == 2) return;

        // Ctrl+拖拽 = 拖动整窗。
        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            StartBodyDrag(e);
            return;
        }

        // 直接拖拽空白 = 框选（原 Ctrl+拖拽行为，用户要求"反一下"）。
        _selectMode = SelectMode.Draw;
        _selectTarget = SelectTarget.ZoneItems;
        _selectStart = e.GetPosition(this);
        _selectCurrent = _selectStart;
        _selectMoved = false;
        _selectFromEmpty = true;
        _selectStartZone = null;
        _selectStartList = null;
        try { Mouse.Capture(this); } catch { }
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
        _snapResize?.Start(e, left, top, !left, !top, 120, 80, OnResizeCompleted);
        DesktopLayer.BringToFront(this);
        e.Handled = true;
    }

    // ── Import ──

    void ImportFiles_Click(object s, RoutedEventArgs e)
    { var d = new OpenFileDialog { Title = _loc["Zone.ImportTitle"], Filter = $"{_loc["Filter.All"]}|*.lnk;*.exe;*.*|{_loc["Filter.Lnk"]}|*.lnk|{_loc["Filter.Exe"]}|*.exe", Multiselect = true }; if (d.ShowDialog() == true) ImportArranged(d.FileNames); }

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
                lpszTitle = _loc["Dialog.SelectFolder"],
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
        catch (Exception ex) { MessageBox.Show(string.Format(_loc["Dialog.ImportFailed"], ex.Message)); }
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
                // 分区图片预览:图片文件显示内容缩略图(与分区 ZoneItemViewModel.Icon 同逻辑),
                // 其余文件/文件夹照旧解析 shell 图标。解析失败回退到默认图标。
                ImageSource? icon;
                if (e.IsFolder)
                {
                    icon = _iconService.GetIcon(e.FullPath, Models.ItemType.Folder);
                }
                else if (ShellIconService.ImagePreviewEnabled && ShellIconService.IsImageFile(e.FullPath))
                {
                    icon = _iconService.GetImageThumbnail(e.FullPath)
                        ?? _iconService.GetIcon(e.FullPath, Models.ItemType.Shortcut);
                }
                else
                {
                    icon = _iconService.GetIcon(e.FullPath, Models.ItemType.Shortcut);
                }
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
                if (image != null && image.CanFreeze) image.Freeze();
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
    /// <summary>flyout 打开时的键盘操作:Ctrl+A 全选内层图标 / Delete 删除选中项
    /// (与主分区多选删除同款确认)。返回 true = 已处理。</summary>
    bool TryHandleFlyoutKeys(KeyEventArgs e)
    {
        if (!SubfolderFlyoutPopup.IsOpen || SubfolderFlyoutView.ViewModel is not { } fvm) return false;
        if (e.Key == Key.A && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            foreach (var it in fvm.ItemVms) it.IsSelected = true;
            return true;
        }
        if (e.Key == Key.Delete)
        {
            var sel = fvm.ItemVms.Where(i => i.IsSelected).ToList();
            if (sel.Count > 0)
            {
                if (sel.Count == 1
                    || MessageBox.Show(string.Format(_loc["ZoneItem.DeleteMultiConfirm"], sel.Count),
                        _loc["Item.Delete"], MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    foreach (var it in sel) fvm.ItemVms.Remove(it);
                    OnFlyoutItemsChanged();
                }
                return true;
            }
        }
        return false;
    }

    void Window_PreviewKeyDown(object s, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox) return; // inline title editing keeps its own keys
        // ponytail 2026-08-26: flyout 打开时,Ctrl+A 全选 / Delete 删除选中项。
        if (TryHandleFlyoutKeys(e)) { e.Handled = true; return; }
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

    (double, double) FindFreeSpot() => ZoneLayout.FindFreeSpot(_vm.GetPlacementItems(), _zone.Width, _zone.Height, _zone.GridSize, _zone.GridSize);

    /// <summary>解析重排目标：合并分区选中子分区标签时用该子分区的图标与网格；否则用主分区。</summary>
    (List<Models.ZoneItem>? Items, int GridSize) ResolveRearrangeTarget()
    {
        if (_zone.MergedGroupMembership.SubZoneIds.Count > 0 && _vm.SelectedSubZoneId.HasValue && _vm.SelectedSubZoneId.Value != _zone.Id)
        {
            var subZone = _mgr.Zones.FirstOrDefault(z => z.Id == _vm.SelectedSubZoneId.Value);
            if (subZone != null) return (subZone.Items, subZone.GridSize);
        }
        return (_zone.Items, _zone.GridSize);
    }

    void RearrangeAll(bool center = true)
    {
        if (!_zone.AutoArrange) return;
        var (items, gridSize) = ResolveRearrangeTarget();
        if (items == null || items.Count == 0) return;

        double pitch = ZoneLayout.Pitch(gridSize);
        double vpitch = ZoneLayout.VPitch(gridSize);
        double pad = ZoneLayout.Pad;
        // 按窗口宽度计算列数。center=true(显式「对齐」/加载归一化):整块水平居中;
        // center=false(拖拽缩放):左对齐 — 放大窗口时图标不往中间漂,只有缩到放不下才换行。
        double avail = Math.Max(0, _zone.Width - 2 * pad);
        int fitCols = Math.Max(1, (int)Math.Floor((avail - gridSize) / pitch) + 1);
        int cols = Math.Min(fitCols, items.Count);
        double blockWidth = (cols - 1) * pitch + gridSize;
        double offsetX = center ? Math.Max(pad, (_zone.Width - blockWidth) / 2) : pad;
        // 直接写 VM 的 X/Y(VM setter 回写模型 + 触发 Canvas.Left/Top 绑定即时更新),
        // 不再 RefreshMergedItems 重建 Items 容器 — 否则拖拽缩放每帧都整树销毁重建,
        // 正是「一拉窗口图标就偏移/闪烁」的根源。
        var vmById = _vm.Items.ToDictionary(v => v.Id);
        int idx = 0;
        foreach (var item in items.OrderBy(i => i.Y).ThenBy(i => i.X))
        {
            int col = idx % cols;
            int row = idx / cols;
            double nx = offsetX + col * pitch;
            double ny = ZoneViewModel.SnapToGridY(pad + row * vpitch, gridSize);
            if (vmById.TryGetValue(item.Id, out var vm))
            {
                vm.X = nx;
                vm.Y = ny;
            }
            else
            {
                item.X = nx;
                item.Y = ny;
            }
            idx++;
        }
    }

    /// <summary>当前图标是否已越过右边界 — 只在「缩小到放不下」时为 true,触发换行重排;
    /// 放大窗口时为 false,图标原地不动(修复「放大分区后图标向中间偏移」)。</summary>
    bool ShouldReflowForResize()
    {
        var (items, gridSize) = ResolveRearrangeTarget();
        if (items == null || items.Count == 0) return false;
        double right = 0;
        foreach (var i in items) right = Math.Max(right, i.X + gridSize);
        return right > _zone.Width - ZoneLayout.Pad;
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
        // 粘贴项上方的分割线跟粘贴项同显隐:无映射时保持菜单整洁(避免双分割线)。
        CtxPasteSep.Visibility = CtxPaste.Visibility;
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
    // ponytail 2026-08-27: 右键 → 设置 = 弹 PropertyPanel 样式浮窗(与齿轮按钮同入口)。
    // ponytail 2026-08-27: 对齐分区右上角齿轮 — 走 PropertyWindowService 而不是
    // ManagementWindow.OpenFloatingProperty,合并组目标走 MergedGroupTarget.For,
    // 浮窗位置锚定在 ZoneWindow。
    void SettingsZone_Click(object s, RoutedEventArgs e)
    {
        if (_zone.MergedGroupMembership.SubZoneIds.Count > 0)
            PropertyWindowService.OpenOrFocus(MergedGroupTarget.For(_zone), this);
        else
            PropertyWindowService.OpenOrFocus(_zone, this);
    }
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
            if (MessageBox.Show(
                string.Format(_loc["Merge.DisbandSingleConfirm"], _zone.Name),
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
                lpszTitle = _loc["Dialog.SelectFolder"],
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
                        _loc["Dialog.FolderName"], _loc["Dialog.NewFolder"], _loc["Dialog.NewFolder"]);
                    if (!string.IsNullOrWhiteSpace(folderName))
                    {
                        string fullPath = Path.Combine(parentPath, folderName);
                        Directory.CreateDirectory(fullPath);
                    }
                }
            }
        }
        catch (Exception ex) { MessageBox.Show(string.Format(_loc["Dialog.ImportFailed"], ex.Message)); }
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
            Title = _loc["Dialog.CreateFile"],
            Filter = filter,
            DefaultExt = defaultExt,
            FileName = "NewDocument" + defaultExt
        };
        if (d.ShowDialog() == true)
        {
            try { System.IO.File.Create(d.FileName).Dispose(); }
            catch (Exception ex) { MessageBox.Show(string.Format(_loc["Dialog.ImportFailed"], ex.Message)); }
            Add(d.FileName, 10, 10);
            UpdateCanvasSize();
            _mgr.SaveConfig();
        }
    }

    void NewTxt_Click(object s, RoutedEventArgs e)
    {
        CreateNewFile(".txt", $"{_loc["Filter.Txt"]}|*.txt|{_loc["Filter.All"]}|*.*");
    }

    void NewDocx_Click(object s, RoutedEventArgs e)
    {
        CreateNewFile(".docx", $"{_loc["Filter.Docx"]}|*.docx|{_loc["Filter.All"]}|*.*");
    }

    void NewPptx_Click(object s, RoutedEventArgs e)
    {
        CreateNewFile(".pptx", $"{_loc["Filter.Pptx"]}|*.pptx|{_loc["Filter.All"]}|*.*");
    }

    void NewXlsx_Click(object s, RoutedEventArgs e)
    {
        CreateNewFile(".xlsx", $"{_loc["Filter.Xlsx"]}|*.xlsx|{_loc["Filter.All"]}|*.*");
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
                DesktopLayer.BringToFront(this);
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

    void Restore_Enter(object s, MouseEventArgs e) { RestoreButton.SetResourceReference(Border.BackgroundProperty, "Menu.Bg.Hover"); }
    void Restore_Leave(object s, MouseEventArgs e) { RestoreButton.SetResourceReference(Border.BackgroundProperty, "Menu.Bg.Surface"); }

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
        _mgr?.SaveConfig();
    }

    // ponytail 2026-08-27: 右键菜单专用 — RoutedEventHandler 签名(不能用 MouseButtonEventArgs 版)。
    void CtxLock_Click(object sender, RoutedEventArgs e)
    {
        LockBtn_Click(sender, new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Button.ClickEvent });
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
        // ponytail 2026-08-27: 锁定态变化时同步右键菜单 Header,切语言后下次打开也会刷新。
        CtxLock.Header = vm.IsLocked ? _loc["Common.Unlock"] : _loc["Common.Lock"];
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
        // ponytail 2026-08-28: 有 ⚙ 点击点坐标时贴点弹出(同次级文件夹,避免历史
        // rect 罩住 ⚙ 导致 ✕ 被下一次点击误关)。
        object target = _zone.MergedGroupMembership.SubZoneIds.Count > 0
            ? MergedGroupTarget.For(_zone) : _zone;
        if (s is System.Windows.FrameworkElement fe)
        {
            try
            {
                var screenPx = fe.PointToScreen(e.GetPosition(fe));
                var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(fe);
                var anchor = new Point(screenPx.X / dpi.DpiScaleX, screenPx.Y / dpi.DpiScaleY);
                PropertyWindowService.OpenOrFocus(target, this, anchor);
                e.Handled = true;
                return;
            }
            catch { /* 未连接等 — 落回旧路径 */ }
        }
        PropertyWindowService.OpenOrFocus(target, this);
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

        // 跨分区拖动:光标悬停到其它分区窗口 → 幽灵预览落到目标格位,源内指示器
        // 全部隐藏;移回源分区(或悬停在应用窗口上)则恢复正常预览路径。
        var extScreen = PointToScreen(e.GetPosition(this));
        var extTarget = FindZoneWindowAt(extScreen);
        if (extTarget != null)
        {
            if (!ReferenceEquals(extTarget, _externalTarget))
            {
                _externalTarget?.HideExternalDropGhost();
                _externalTarget = extTarget;
            }
            HideDropIndicator();
            ClearSubfolderDragScale();
            extTarget.ShowExternalDropGhost(_dv, extScreen);
            return;
        }
        if (_externalTarget != null)
        {
            _externalTarget.HideExternalDropGhost();
            _externalTarget = null;
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
            // 跨分区提交优先:悬停在其它分区上释放 → 移动到该分区;拒收/未悬停
            // 则回退本地换位/移动(次级文件夹图标与普通图标同规则)。
            var ext = _externalTarget;
            _externalTarget = null;
            ext?.HideExternalDropGhost();
            if (ext == null || !CommitCrossZoneMove(ext, PointToScreen(e.GetPosition(this))))
            {
                // 命中 SubFolder → 移入;否则普通换位/移动(次级文件夹图标与普通图标同规则)。
                var overSub = FindSubfolderTarget(e.GetPosition(ItemsHost));
                if (overSub != null && _dv.Type != ItemType.SubFolder)
                    MoveIntoSubfolder(_dv, overSub);
                else if (_zone.SnapToGrid) ReorderItemInto(_dv, _dv.X, _dv.Y);
                else { _vm.MoveItem(_dv.Id, _dv.X, _dv.Y, snapToGrid: false); _vm.RefreshMergedItems(); }
            }
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

    /// <summary>屏幕点下的可接收分区窗口(排除自身;最小化成恢复按钮的分区不可接收)。
    /// WindowFromPoint 尊重真实 z 序与分层窗口透明像素——覆盖在目标分区上的应用
    /// 窗口会自然挡住投放。</summary>
    ZoneWindow? FindZoneWindowAt(Point screenPt)
    {
        var hwnd = NativeMethods.WindowFromPoint(new NativeMethods.POINT
        {
            x = (int)Math.Round(screenPt.X),
            y = (int)Math.Round(screenPt.Y)
        });
        if (hwnd != IntPtr.Zero) hwnd = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
        if (hwnd == IntPtr.Zero) return null;
        foreach (var z in _mgr.Zones)
        {
            if (z.Id == _zone.Id) continue;
            if (_mgr.GetZoneWindow(z.Id) is not ZoneWindow zw || !zw.AcceptsExternalDrop) continue;
            if (new WindowInteropHelper(zw).Handle == hwnd) return zw;
        }
        return null;
    }

    /// <summary>跨分区移动提交:把拖拽图标从其所属分区(合并时可能是子分区)移除,
    /// 加入目标窗口当前显示的分区并统一落盘刷新(两个窗口都经 ZonesChanged 重建)。
    /// 返回 false = 目标拒收,调用方回退本地换位。</summary>
    bool CommitCrossZoneMove(ZoneWindow target, Point screenPt)
    {
        if (_dv == null) return false;
        var raw = ResolveSourceZoneItem(_dv);
        if (raw == null) return false;
        var owner = OwnerZoneOf(_dv) ?? _zone;
        if (!target.ReceiveExternalItem(raw, screenPt)) return false;
        owner.Items.Remove(raw);
        _mgr.SaveConfig();
        _mgr.NotifyChanged();
        return true;
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
        // 双击 = 进入文件夹 / 打开文件。必须在 Preview 阶段处理:下方捕获鼠标 +
        // 长按框选计时会吞掉 ListBox 自身的 MouseDoubleClick(第二击 ClickCount==2
        // 时 ListBoxItem 已选中,ListBox 不再按普通点击路径派发双击事件)。
        if (e.ClickCount == 2 && e.ChangedButton == MouseButton.Left && IsOnFolderEntry(e.OriginalSource))
        {
            SelectFolderEntryAtCursor();
            OpenFolderMapSelected();
            e.Handled = true;
            return;
        }

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
                barY = prev.Y + gs + ZoneLayout.CellGap / 2;
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

    // ── 跨分区拖动图标:目标侧 API(由源窗口在拖拽悬停时驱动) ──

    /// <summary>是否可接收来自其它分区的拖放(最小化成恢复按钮时不可)。</summary>
    public bool AcceptsExternalDrop => IsVisible && MainContent.Visibility == Visibility.Visible;

    /// <summary>当前显示的分区:合并主区 = 选中的子分区页签;普通分区 = 自身。</summary>
    Zone? CurrentReceiveZone()
    {
        var id = _vm.SelectedSubZoneId ?? _zone.Id;
        return id == _zone.Id ? _zone : _mgr.Zones.FirstOrDefault(z => z.Id == id);
    }

    /// <summary>拖放幽灵:半透明图标+名称预览,落在悬停点吸附后的格位。
    /// 每次鼠标移动都会重调以跟随光标。</summary>
    public void ShowExternalDropGhost(ZoneItemViewModel sourceVm, Point screenPt)
    {
        if (!AcceptsExternalDrop) return;
        var recv = CurrentReceiveZone();
        if (recv == null) return;

        int gs = recv.GridSize;
        var local = PointFromScreen(screenPt);
        double x, y;
        if (recv.SnapToGrid)
        {
            x = ZoneViewModel.SnapToGrid(local.X - ItemW / 2, gs);
            y = ZoneViewModel.SnapToGridY(local.Y - ItemH / 2, gs);
        }
        else { x = local.X - ItemW / 2; y = local.Y - ItemH / 2; }
        x = Math.Clamp(x, 0, Math.Max(0, _zone.Width - ItemW));
        y = Math.Clamp(y, 0, Math.Max(0, _zone.Height - ItemH));

        // 幽灵按目标分区的图标尺寸公式预览(与 CurrentIconSize 同式,目标分区私有)
        double iconSize = Math.Max(8, _zone.GridSize - (_zone.HideAppName ? 6 : 18));
        if (_extGhost == null)
        {
            _extGhost = new StackPanel
            {
                IsHitTestVisible = false,
                Opacity = 0.65,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top
            };
            var img = new Image
            {
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 4)
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            var tb = new TextBlock
            {
                FontSize = 10,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = ItemTextBrush
            };
            _extGhost.Children.Add(img);
            _extGhost.Children.Add(tb);
            DropIndicatorLayer.Children.Add(_extGhost);
        }
        var gImg = (Image)_extGhost.Children[0];
        var gName = (TextBlock)_extGhost.Children[1];
        gImg.Source = sourceVm.Icon;
        gImg.Width = gImg.Height = iconSize;
        gName.Text = sourceVm.Name;
        gName.MaxWidth = Math.Max(32, _zone.GridSize * 0.9);
        Canvas.SetLeft(_extGhost, x);
        Canvas.SetTop(_extGhost, y);
    }

    public void HideExternalDropGhost()
    {
        if (_extGhost == null) return;
        DropIndicatorLayer.Children.Remove(_extGhost);
        _extGhost = null;
    }

    /// <summary>接收来自其它分区拖来的图标:落到悬停点吸附后的格位(被占时与导入
    /// 图标同规则找最近空格),加入当前显示的分区。返回 false = 无法接收,源窗口
    /// 继续走本地换位逻辑。</summary>
    public bool ReceiveExternalItem(ZoneItem raw, Point screenPt)
    {
        var recv = CurrentReceiveZone();
        if (recv == null || !AcceptsExternalDrop) return false;
        HideExternalDropGhost();

        var local = PointFromScreen(screenPt);
        int gs = recv.GridSize;
        double x, y;
        if (recv.SnapToGrid)
        {
            x = ZoneViewModel.SnapToGrid(local.X - ItemW / 2, gs);
            y = ZoneViewModel.SnapToGridY(local.Y - ItemH / 2, gs);
            if (recv.Items.Any(i => i.Id != raw.Id && Math.Abs(i.X - x) < 1 && Math.Abs(i.Y - y) < 1))
                (x, y) = ZoneLayout.FindFreeSpot(recv.Items.Where(i => i.Id != raw.Id), _zone.Width, _zone.Height, ItemW, ItemH);
        }
        else { x = local.X - ItemW / 2; y = local.Y - ItemH / 2; }
        x = Math.Clamp(x, 0, Math.Max(0, _zone.Width - ItemW));
        y = Math.Clamp(y, 0, Math.Max(0, _zone.Height - ItemH));

        raw.X = x; raw.Y = y;
        recv.Items.Add(raw);
        return true;
    }

    void Item_Enter(object s, MouseEventArgs e)
    {
        if (s is Grid g)
        {
            var hover = FindVisualChild<Border>(g, b => b.Name is "HoverBox" or "SubHoverBox");
            if (hover != null) hover.Background = ItemHoverBrush;
        }
    }

    void Item_Leave(object s, MouseEventArgs e)
    {
        if (s is Grid g)
        {
            var hover = FindVisualChild<Border>(g, b => b.Name is "HoverBox" or "SubHoverBox");
            if (hover != null) hover.Background = Brushes.Transparent;
        }
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
    void ItemOpenLocation_Click(object s, RoutedEventArgs e)
    {
        if (VM(s) is not ZoneItemViewModel v) return;
        OpenItemLocation(v);
    }
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
        string ButtonColor,
        string TextColor,
        double ControlOpacity,
        int CornerRadius,
        bool TileMode,
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
    /// ButtonColor stays unified from MergedGroupStyle in both merged modes; TextColor follows
    /// the same source as FillColor (group fill in unified mode, displayed sub-zone's own
    /// in Keep Original) so body content color stays coherent with its background.
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
            ButtonColor:      _zone.ButtonColor,
            TextColor:        _zone.TextColor,
            ControlOpacity:   _zone.ControlOpacity,
            CornerRadius:     _zone.CornerRadius,
            TileMode:     _zone.TileMode,
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
                ButtonColor =      _zone.MergedGroupStyle.ButtonColor,
                TextColor =        _zone.MergedGroupStyle.TextColor,
                ControlOpacity =   _zone.MergedGroupStyle.ControlOpacity,
                CornerRadius =     _zone.MergedGroupStyle.CornerRadius,
                TileMode =     _zone.MergedGroupStyle.TileMode,
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
        string keepTextColor = _zone.TextColor;
        if (_zone.MergedGroupMembership.SubZoneIds.Count > 0
            && _vm?.SelectedSubZoneId is Guid selId && selId != _zone.Id)
        {
            var sub = _mgr.Zones.FirstOrDefault(z => z.Id == selId);
            if (sub != null)
            {
                keepFill = sub.FillColor;
                keepTextColor = sub.TextColor;
            }
        }

        return regular with
        {
            FillColor =        keepFill,
            BorderColor =      _zone.MergedGroupStyle.BorderColor,
            BorderThickness =  _zone.MergedGroupStyle.BorderThickness,
            TitleBarFillColor = _zone.MergedGroupStyle.TitleBarFillColor,
            TitleTextColor =   _zone.MergedGroupStyle.TitleTextColor,
            IconColor =        _zone.MergedGroupStyle.IconColor,
            ButtonColor =      _zone.MergedGroupStyle.ButtonColor,
            TextColor =        keepTextColor,
            ControlOpacity =   _zone.MergedGroupStyle.ControlOpacity,
            CornerRadius =     _zone.MergedGroupStyle.CornerRadius,
            TileMode =         _zone.MergedGroupStyle.TileMode,
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
    /// 多段标题栏的自带透明度区分：主标题栏保持解析后的填充色，副标题栏
    /// （组合分区子标签栏）取 50% 透明度，文件夹映射头部行取 2× 透明度——
    /// 还原最初 XAML 默认值 #10 / #08 / #22 的相对关系。开启「标题栏独立填充」
    /// 也不会取消这一区分（该开关只影响主体填充与背景图的顶部裁剪）。
    /// </summary>
    void ApplyTitleBarBandFill(string titleBarFillColor)
    {
        SolidColorBrush main;
        try { main = new SolidColorBrush((Color)ColorConverter.ConvertFromString(titleBarFillColor)!); }
        catch { main = new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)); }

        var c = main.Color;
        TitleBarBg.Background = main;
        SubZoneTabsRow.Background = new SolidColorBrush(Color.FromArgb((byte)(c.A / 2), c.R, c.G, c.B));
        FolderMapHeaderBg.Background = new SolidColorBrush(Color.FromArgb((byte)Math.Min(255, c.A * 2), c.R, c.G, c.B));
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
        if (ClickPulseBg != null)
            ClickPulseBg.CornerRadius = new CornerRadius(s.CornerRadius);

        // ponytail 2026-08-26: keep the OS (DWM) corner preference in lockstep.
        // radius 0 → DONOTROUND so Win11 stops clipping the sharp WPF corners.
        // Guarded: ApplyStyle also runs in the constructor before the HWND
        // exists, where WindowInteropHelper.Handle would throw.
        // ponytail 2026-08-28: 收起状态下跳过 — 设置面板显示开关 → HideZone →
        // ZonesChanged → OnZonesChanged → ApplyStyle 这条链会在窗口收起后重新
        // 打开 Win11 圆角,整窗大小的圆角描边正是「原窗口轮廓残影」来源之一。
        // 展开路径(ShowZone / ReapplyAcrylic)会各自恢复圆角。
        bool collapsed = RestoreButton.Visibility == Visibility.Visible
                         || _hover is { IsExpanded: false };
        if (PresentationSource.FromVisual(this) != null && !collapsed)
            NativeMethods.SetRoundedCorners(this, s.CornerRadius);

        // Body fill — 分区本体一体化:玻璃开时填充已并入玻璃 tint,此处透明;
        // 玻璃关(或收起)时保持纯填充照旧。
        bool glassCarriesFill = _zone.EnableLiquidGlass && (_hover?.IsExpanded ?? false);
        try { FillRect.Fill = glassCarriesFill ? AcrylicHelper.HitTestFill : new SolidColorBrush((Color)ColorConverter.ConvertFromString(s.FillColor)!); } catch { }
        bool fillIndependent = s.TitleBarFillIndependent && !s.TileMode;
        FillRect.RadiusX = FillRect.RadiusY = fillIndependent ? 0 : s.CornerRadius;
        // ponytail 2026-08-26: the merged master's title bar is TWO layers — the
        // 24px top bar + the 24px sub-zone tab row — so the body fill starts below
        // both (48px), not just below the top bar.
        FillRect.Margin = fillIndependent ? new Thickness(0, TitleBarLayerHeight(), 0, 0) : new Thickness(0);

        // Title bar fill — 3-band built-in transparency distinction (top bar / merged
        // sub-zone tab row / folder-mapping header), preserved regardless of the
        // title-bar independent-fill toggle.
        ApplyTitleBarBandFill(s.TitleBarFillColor);

        // Background image.
        ApplyBackgroundImage(s);

        // Title text — always the resolved TitleTextColor.
        try { ZoneTitleText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(s.TitleTextColor)!); } catch { }

        // Title icon — resolved IconColor (falling back to the resolved title text color).
        ApplyIconVisuals();

        // ControlPoint button labels — fixed 按钮颜色 (replaces the old title-bar adaptive).
        SolidColorBrush btnBrush;
        try { btnBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(s.ButtonColor)!); }
        catch { btnBrush = Brushes.White; }
        LockBtnText.Foreground = btnBrush;
        EditBtnText.Foreground = btnBrush;
        ImportBtnText.Foreground = btnBrush;
        HideBtnText.Foreground = btnBrush;

        // Control-point opacity + TileMode visibility
        ControlPoint.Opacity = Math.Max(0.05, s.ControlOpacity / 100.0);
        var vis = s.TileMode ? Visibility.Collapsed : Visibility.Visible;
        TitleBarBg.Visibility = vis;
        ControlPoint.Visibility = vis;
        _tileVisual = s.TileMode;

        // 磁贴模式 = 隐藏底部 8px 分割条。
        if (BottomBarBg != null)
            BottomBarBg.Visibility = s.TileMode ? Visibility.Collapsed : Visibility.Visible;

        // 隐藏应用名 — 遍历 item 容器切换名称 TextBlock 可见性。
        ApplyHideAppName(_zone.HideAppName);

        // 自定义图标（单图标模式）：TileMode + CustomIcon + Items.Count==1 时
        // 隐藏 ItemsHost，双击整个分区打开唯一图标。
        ApplyCustomIcon(s.TileMode && _zone.CustomIcon && _zone.Items.Count == 1);

        // Sub-zone tabs reuse the resolved title text color (merged groups share one
        // title-bar band); items use the resolved body content color.
        RebuildSubZoneTabs(s.TitleTextColor);
        ApplyItemTextColor(s.TextColor);
    }

    /// <summary>遍历 ItemsHost 内的 ContentPresenter，根据 hide 切换名称 TextBlock
    /// （x:Name="ItemNameText"）可见性。次级分区名称走 SubfolderItemView.HideName
    /// 声明式绑定,不在此重复处理。容器未生成时无操作 — StatusChanged 处理器会在
    /// 容器生成后补一次。</summary>
    void ApplyHideAppName(bool hide)
    {
        if (ItemsHost == null) return;
        var target = hide ? Visibility.Collapsed : Visibility.Visible;
        for (int i = 0; i < ItemsHost.Items.Count; i++)
        {
            if (ItemsHost.ItemContainerGenerator.ContainerFromIndex(i) is not DependencyObject container) continue;
            var tb = FindVisualChild<TextBlock>(container, tb => tb.Name == "ItemNameText");
            if (tb != null) tb.Visibility = target;
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

    /// <summary>图标列表变化后重应用磁贴相关的 item 视觉（隐藏应用名 + 自定义图标）。</summary>
    void ReapplyTileItemVisuals()
    {
        ApplyHideAppName(_zone.HideAppName);
        ApplyCustomIcon(_zone.TileMode && _zone.CustomIcon && _zone.Items.Count == 1);
    }

    /// <summary>整窗双击：CustomIcon 开启时打开当前列表的第一项；否则忽略。
    /// 打开的同时播放一次点击脉冲动画 — 仅在该模式下存在双击行为。</summary>
    void Window_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!_customIconOpenFirst) return;
        // 标题栏 / 控件 / 文件夹映射区域的双击不触发打开。
        if (IsWithinZoneChrome(e.OriginalSource)) return;
        var item = _vm.Items.FirstOrDefault();
        if (item == null) return;
        PlayClickPulse();
        Open(item);
        e.Handled = true;
    }

    /// <summary>自定义图标双击反馈：整窗白色脉冲（0 → 0.30 → 0，约 230ms）。</summary>
    void PlayClickPulse()
    {
        if (ClickPulseBg == null) return;
        var anim = new DoubleAnimationUsingKeyFrames();
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(0.30, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(70)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(230)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        ClickPulseBg.BeginAnimation(OpacityProperty, anim);
    }

    /// <summary>Combined title-bar height: 24px top bar + 24px merged sub-zone tab
    /// row + 26px folder-mapping header row (when mapping is enabled).</summary>
    double TitleBarLayerHeight() =>
        24 + (_zone.MergedGroupMembership.SubZoneIds.Count > 0 ? 24 : 0)
           + (ResolveFolderMapping().Enabled ? 26 : 0);

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
            }
            catch { BgImage.Opacity = 0; }
        }
        else { BgImage.Source = null; BgImage.Opacity = 0; }
    }

    /// <summary>Apply the resolved 主体内容颜色 to item labels. The brush is exposed as
    /// <see cref="ItemTextBrush"/> and bound declaratively in the item templates, so newly
    /// generated containers pick the right color at instantiation — no post-render walk,
    /// no default-color flash.</summary>
    public void ApplyItemTextColor(string? effectiveColor = null)
    {
        string color = effectiveColor ?? ResolveStyle().TextColor;
        SolidColorBrush brush;
        try { brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)!); }
        catch { brush = Brushes.White; }
        brush.Freeze();
        ItemTextBrush = brush;
    }

    /// <summary>Re-apply the full style (fixed title/body content colors included). Called
    /// from live preview when a color setting changes.</summary>
    public void RefreshTextColorAdaptive()
    {
        ApplyStyle();
    }

    void SetRestoreIcon()
    {
        // 组合分区优先用 MergedGroupMembership.Icon，否则用 IconChar；为空时回退到
        // 软件原生默认图标（组合 → @merged，分区 → @zones），不再用名称首字母兜底。
        ApplyIconVisuals();
    }

    /// <summary>解析当前应显示的图标字符串（含原生默认兜底）。</summary>
    string ResolveIconText()
    {
        bool isMergedMaster = _zone.MergedGroupMembership.SubZoneIds.Count > 0;
        string iconChar = isMergedMaster && !string.IsNullOrEmpty(_zone.MergedGroupMembership.Icon)
            ? _zone.MergedGroupMembership.Icon : _zone.IconChar;
        return string.IsNullOrEmpty(iconChar)
            ? (isMergedMaster ? Helpers.IconGlyph.Merged : Helpers.IconGlyph.Zones)
            : iconChar;
    }

    /// <summary>把解析后的图标（emoji 或原生矢量）刷到标题栏 + 恢复按钮。
    /// 两处图标都走「设置的颜色」（IconColor ?? 名称/文字颜色），不随系统深浅色变化。</summary>
    void ApplyIconVisuals()
    {
        var s = ResolveStyle();
        Brush titleBrush;
        var titleColor = !string.IsNullOrEmpty(s.IconColor) ? s.IconColor : s.TitleTextColor;
        try { titleBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(titleColor)!); }
        catch { titleBrush = Brushes.Transparent; }

        string icon = ResolveIconText();
        Helpers.IconGlyph.Apply(TitleIconChar, TitleIconPath, icon, titleBrush, 12);
        Helpers.IconGlyph.Apply(RestoreIconChar, RestoreIconPath, icon, titleBrush, 18);
    }
    void OnSize(object s, SizeChangedEventArgs e)
    {
        if (!IsLoaded || MainContent.Visibility != Visibility.Visible) return;
        _zone.Width = Width;
        _zone.Height = FullHeightFromWindowHeight();
        ScheduleSave();
        bool tileSync = !double.IsNaN(_expectedTileHeight) && Math.Abs(Height - _expectedTileHeight) < 0.5;
        if (tileSync) _expectedTileHeight = double.NaN;
        // 「尺寸变化时自动重排」:只在边框拖拽缩放期间、且图标确实放不下(缩小)时
        // 左对齐换行重排;放大窗口时不动图标(不再向中间漂)。磁贴切换等程序化尺寸
        // 同步(_expectedTileHeight 命中)不重排。
        if (!tileSync && _snapResize?.IsActive == true && ShouldReflowForResize())
            RearrangeAll(center: false);
        UpdateCanvasSize();
        NativeMethods.UpdateRoundedCorners(this, (int)_zone.CornerRadius);
    }

    // 拖拽缩放结束的收尾:补齐最后一次(可能晚于 IsActive=false 到达的)尺寸重排,
    // 保证缩小时按最终宽度落位 + 保存;放大不重排。
    void OnResizeCompleted()
    {
        if (!IsLoaded || MainContent.Visibility != Visibility.Visible) return;
        _zone.Width = Width;
        _zone.Height = FullHeightFromWindowHeight();
        if (ShouldReflowForResize()) RearrangeAll(center: false);
        UpdateCanvasSize();
        ScheduleSave();
    }

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
        foreach (var i in displayItems) { if (i.X + gs + 20 > maxX) maxX = i.X + gs + 20; if (i.Y + gs + 20 > maxY) maxY = i.Y + gs + 20; }
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
        // ponytail 2026-08-28: 展开时把 Win11 圆角偏好一并恢复(收起时
        // OnHoverCollapsed 关掉了它),否则恢复按钮点开的分区会一直保持尖角。
        NativeMethods.SetRoundedCorners(this, (int)_zone.CornerRadius);
        ApplyAcrylic(s.FillColor, s.TitleBarFillColor);
    }

    /// <summary>
    /// ponytail 2026-08-28 边框残影修复 — 收起完成时的最终保险。窗口收起后仍保持
    /// 整窗大小,任何残留的 OS 层装饰(丙烯酸玻璃 / Win11 圆角 / DWM 框架阴影)
    /// 都会以「原窗口轮廓」的形式残留在恢复按钮周围。这里把三样全部重断言关闭,
    /// 与三个小挂件的收起分支行为对齐。
    /// </summary>
    void OnHoverCollapsed()
    {
        AcrylicHelper.DisableBlur(this);
        NativeMethods.DisableRoundedCorners(this);
        NativeMethods.DisableDwmFrameShadow(this);
    }

    void ApplyAcrylic(string fillColor, string titleBarFillColor)
    {
        // ponytail: ghost-glass fix — a collapsed zone keeps its full-size window (only the
        // RestoreButton is visible), so enabling acrylic here would tint the WHOLE window
        // bounds with a ghost glass rectangle. Only enable blur while the content is
        // expanded; whenever collapsed (or mid-collapse), disable it instead.
        bool expanded = _hover?.IsExpanded ?? false;
        if (_zone.EnableLiquidGlass && expanded)
        {
            // ponytail 2026-08-30: 分区本体一体化 — 内部填充并入玻璃 tint(算一层),
            // FillRect 透明;填充色与玻璃配色作为两个输入本质上仍是两层。
            var blurResult = AcrylicHelper.EnableBlurComposite(this, _zone.GlassBlurAmount,
                fillColor, 1.0, _zone.GlassColorMode, _zone.GlassTintOpacity, _zone.GlassTintLuminosity);
            if (!blurResult.Success)
                System.Diagnostics.Debug.WriteLine($"[ZoneWindow] EnableBlur failed: {blurResult.Error}");
            FillRect.Fill = AcrylicHelper.HitTestFill;
            FillRect.Opacity = 1.0;
            if (TitleBarBg != null && !string.IsNullOrEmpty(titleBarFillColor))
                ApplyTitleBarBandFill(titleBarFillColor);
        }
        else
        {
            AcrylicHelper.DisableBlur(this);
            try
            {
                FillRect.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fillColor)!);
                FillRect.Opacity = 1.0;
                ApplyTitleBarBandFill(titleBarFillColor);
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
        // doesn't copy Items. Clearing/re-adding would force container regeneration, and
        // the item-label color now rides a declarative binding on ItemTextBrush, so the
        // color simply re-evaluates without any walk/regeneration race. Actual item
        // add/remove/rename goes through OnZonesChanged which uses Dispatcher.BeginInvoke
        // (Fix C). Updating VM.Zone keeps its binding consumers (SourceZoneId et al.)
        // happy without touching the Items collection.
        _vm.Zone = zone;
        ZoneTitleText.Text = zone.Name;
        SetRestoreIcon();
        // ponytail: ApplyStyle rebuilds sub-zone tabs internally with the resolved title
        // text color — no separate RebuildSubZoneTabs call needed here.
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

    void RebuildSubZoneTabs(string? titleTextColor = null)
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
                AddSubZoneTab(z.Id, z.Name, z.IconChar, titleTextColor);
        }
    }

    void AddSubZoneTab(Guid zoneId, string name, string iconChar, string? titleTextColorOverride)
    {
        bool isSelected = _vm.SelectedSubZoneId == zoneId;

        // Sub-zone tabs reuse the resolved title text color (master's MergedGroupStyle.
        // TitleTextColor in merged mode). No hardcoded hex fallback; if the override is
        // empty/malformed, fall through to Transparent so WPF inherits instead of snapping white.
        Brush textBrush;
        if (!string.IsNullOrEmpty(titleTextColorOverride))
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
            ToolTip = _loc["ZoneMenu.TabTooltip"]
        };

        var sp = new StackPanel { Orientation = Orientation.Horizontal };

        if (!string.IsNullOrEmpty(iconChar))
        {
            var iconEl = Helpers.IconGlyph.CreateIcon(iconChar, textBrush, fontSize: 10, pathSize: 10);
            if (iconEl != null)
            {
                iconEl.Margin = new Thickness(0, 0, 3, 0);
                sp.Children.Add(iconEl);
            }
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
        // ponytail: ApplyStyle rebuilds sub-zone tabs internally with the resolved title
        // text color — no separate RebuildSubZoneTabs call needed.
        ApplyStyle(); // Apply style based on selected sub-zone (also rebuilds tabs)
        // The selected tab owns the visible folder mapping (sub-zone keeps its own
        // mapping after joining the group) — re-resolve + reload for the new tab.
        RefreshFolderMapping();
        // ponytail: 切到子分区 tab 时不再自动重排/居中图标 — 保持子分区自己的网格位置，
        // 避免「分区加入组合分区后图标偏移」（与窗口缩放/启动重排的修复一致）。
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
