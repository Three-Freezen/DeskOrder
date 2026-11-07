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

    private static readonly SolidColorBrush ItemHoverBrush    = Freeze(new(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)));
    static SolidColorBrush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    // ── 内容视图：网格(画布) / 列表 ──
    // ponytail 2026-10: 与面板标题栏那对 ▦/≡ 同款。网格 = 历史行为（画布自由/自动排列），
    // 列表 = 单列「图标 + 名称」（见 ZoneWindow.xaml 的 ZoneItemListTemplate）。
    // 只切显示：不写 item.X/Y，列表里也不挂拖拽（没有落点概念）。
    private bool _isGridView = true;
    /// <summary>▦/≡ 选中态的底盘方块（与面板同色）。</summary>
    private static readonly SolidColorBrush ViewToggleIdleBrush = Freeze(new(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF)));
    /// <summary>当前「按钮颜色」原色 —— 两枚切换图标按选中/未选中在此之上加/减亮度。</summary>
    private Brush _viewToggleBrush = Brushes.White;
    /// <summary>列表视图的排序视图（见 EnsureListViewSource）。</summary>
    private System.Windows.Data.ListCollectionView? _listView;

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

    // ── 快捷栏（Dock）──
    /// <summary>当前窗口是否为快捷栏形态。形态由 <see cref="Zone.DockMode"/> 定死，
    /// 关窗重开也不变；与分区的所有分支用这一个判据。</summary>
    bool IsDock => _zone.DockMode;

    /// <summary>正在按内容重算宽高 —— 期间 <see cref="OnSize"/> 不回写模型、不重排。</summary>
    bool _applyingDockGeometry;

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
    // ponytail 2026-09-26: 标题栏改名的"停手即提交"防抖。见 ZoneTitle_TextChanged 的说明 ——
    // 只在 Enter / 失焦提交是不够的(点窗口里的空白处不会把焦点移出 TextBox)。
    private readonly System.Windows.Threading.DispatcherTimer _titleCommitDebounce = new() { Interval = TimeSpan.FromMilliseconds(700) };
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
    /// <summary>映射区上一次的显示状态(null = 还没跑过)。只有它翻转时才重跑 ApplyStyle,
    /// 见 <see cref="RefreshFolderMapping"/> 的注释。</summary>
    private bool? _folderMappingShown;

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
    // ponytail 2026-10: 点击时记住被点的那个元素（网格里的图标格 / 列表里的图标），
    // 打开浮层时优先拿它当锚点 —— 面板那边就是直接把被点的卡片传给
    // SubfolderFlyout.ComputePosAndAnchor。列表视图里容器在 ItemsListHost 而不是
    // 网格画布，只按网格容器找会拿到 null，浮层就落到屏幕左上角 (8,8)。
    FrameworkElement? _flyoutAnchorOverride;

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
        // ponytail 2026-08-31: flyout 内层回收站图标的「清空回收站」— 与主分区右键
        // ItemEmptyRecycle_Click 共用同一实现(EmptyRecycleBin)。
        SubfolderFlyoutView.ItemEmptyRecycleBinRequested += OnFlyoutItemEmptyRecycleBin;
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
        // 视图模式跟随分区模型（标题栏 ▦/≡ 会写回 Zone.IsGridView 并落盘）。
        _isGridView = zone.IsGridView;
        _vm.IsLocked = zone.IsLocked;
        DataContext = _vm;
        Left = zone.X; Top = zone.Y;
        // 快捷栏比分区的兜底小得多（高 60~100），不能走 SanitizeW（<100 会被撑成 400/300）。
        if (zone.DockMode)
        {
            Width = Math.Max(40, zone.Width);
            Height = Math.Max(40, zone.Height);
        }
        else
        {
            Width = SanitizeW(zone.Width); Height = SanitizeW(zone.Height);
        }
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
        // 标题栏改名:停手 700ms 自动提交 + 窗口失去激活时提交 —— 用户"改完点别处"
        // (点窗口内不可聚焦的空白 / 点别的窗口)都必须落盘,不能只有 Enter 才算数。
        _titleCommitDebounce.Tick += (_, _) => CommitTitleRename();
        ZoneTitleText.TextChanged += ZoneTitle_TextChanged;
        Deactivated += (_, _) =>
        {
            CommitTitleRename();
            // 窗口失去激活 = 刚启动的程序把前台抢走了：这时候的拖动状态（半透明 + 跟手位置）
            // 没人来收尾，必须在这里结束掉（见 CancelItemDrag 的说明）。
            if (_dv != null) CancelItemDrag("window deactivated");
        };
        _recycleTimer.Tick += RecycleTimer_Tick;
        _recycleTimer.Start();
        _langChanged = _ => ApplyLoc();
        _loc.LanguageChanged += _langChanged;
        _mgr.ZonesChanged += OnZonesChanged;
        // 快捷栏的玻璃层是**独立窗口**：内容层一变成不可见（最小化 / Hide / 关窗前）
        // 就必须跟着收，否则屏幕上会留下一块悬空的胶囊 —— 那也是一种「两层玻璃」。
        IsVisibleChanged += (_, _) => { if (!IsVisible) HideDockGlassWindow(); };
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
                // 容器重建后短线是新实例（宽度 0、画刷透明）→ 按当前状态补一次
                if (IsDock) RefreshDockRunBars(animate: false);
                // 倒影要读图标的**实际渲染矩形**（TransformToAncestor），容器/布局没好时
                // 量不到 → 容器生成后再补一次（ApplyDockGeometry 那次多半还没排好版）。
                if (IsDock) RefreshDockReflection();
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
            // 快捷栏用自己的开关：收起后窗口里只剩那颗 36×36 的方块，悬停即展开比"必须点"
            // 更符合直觉（没有热键、没有托盘，它是唯一入口）；默认开。
            () => IsDock ? _zone.DockHoverRestore : _zone.HoverAutoExpand)
        { IsEnabled = _zone.EnableRestoreButton };
        // ponytail: 2026-08-21 — pick up live changes from MotionSettingsDialog.
        _zone.HoverExpandSettingsChanged += OnHoverExpandSettingsChanged;
        // ponytail 2026-11-01「拖动卡住」修复 — 拖动途中被别的窗口抢走鼠标捕获时，
        // 系统不会再派发 WM_LBUTTONUP，Item_MouseUp 永远等不到 → 在这里兜住收尾。
        AddHandler(Mouse.LostMouseCaptureEvent,
            new MouseEventHandler(OnAnyLostMouseCapture), handledEventsToo: true);
        // ponytail 2026-11-01 「显示名称时悬停缩放抽搐」修复 — 指针脱离对账（见 DockHoverBegin）。
        _dockHoverWatch = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _dockHoverWatch.Tick += (_, _) => ReconcileDockHover();
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

        // ponytail: 快捷栏形态在构造末尾统一落到窗口上（隐藏标题栏/按钮/底条、
        // 换图标模板、按图标数算宽高、画倒影）。放在 _hover 之后，收起/展开逻辑已就位。
        if (IsDock) ApplyDockMode();

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
        DesktopLayer.BringToFront(this); if (IsDock) SyncDockGlassWindow(); NativeMethods.SetToolWindow(this);
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

    /// <summary>点击前那一瞬的前台窗口（只在 WM_MOUSEACTIVATE 里抓得到，见下面的说明）。</summary>
    IntPtr _foregroundBeforeClick = IntPtr.Zero;

    IntPtr WndProc(IntPtr h, int m, IntPtr w, IntPtr l, ref bool hd)
    {
        if (m == NativeMethods.WM_DROPFILES) { DoDrop(w); hd = true; }
        else if (m == NativeMethods.WM_WINDOWPOSCHANGED) { OnDockWindowPosChanged(); }
        else if (m == NativeMethods.WM_MOUSEACTIVATE)
        {
            // 这一条**在窗口被激活之前**到达，所以 GetForegroundWindow() 还是"点之前那个
            // 前台窗口"。点快捷栏图标要区分「切前台」还是「最小化」全靠它：等真正进到
            // 点击处理时，前台已经是我们自己的条体了，判不出"刚才是不是它"。
            try { _foregroundBeforeClick = NativeMethods.GetForegroundWindow(); } catch { }
        }
        return IntPtr.Zero;
    }

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
        // 快捷栏：一律追加到末尾（没有"找空格子"这回事），X/Y 交给几何重算写死，
        // 跨分区拖入 / 导入文件 / 导入系统项目 / 新建文件 都走这一条路。
        if (IsDock)
        {
            item.X = ZoneLayout.DockX(_zone, _zone.Items.Count);
            item.Y = ZoneLayout.DockBandTop(_zone);
            _vm.AddItem(item);
            ZoneLayout.ApplyDockSize(_zone);
            ApplyDockGeometry();
            return;
        }
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

    /// <summary>锁定的快捷栏不收外部拖入（否则拖个文件进来就会把条体撑长 —— 那是"改尺寸"，
    /// 锁定的语义就是布局不许被误操作改动）。分区保持原有行为。</summary>
    bool DockDropBlocked => IsDock && _zone.IsLocked;

    void Window_DragEnter(object s, DragEventArgs e)
    {
#if DEBUG
        if (IsDock) DzTrace.Log($"[WinPreview] dragEnter locked={_zone.IsLocked}");
#endif
        if (DockDropBlocked) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
        // ponytail 2026-08-26: 内部拖拽(ZoneItemViewModel)+ 桌面文件(FileDrop)都先走
        // SubFolder 命中检测,命中则高亮目标方框;未命中再走普通 FileDrop 分支。
        if (TryRouteSubfolderDragOver(e)) return;
        if (e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effects = DragDropEffects.Copy; e.Handled = true; }
    }

    void Window_DragOver(object s, DragEventArgs e)
    {
        if (DockDropBlocked) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
        if (TryRouteSubfolderDragOver(e)) return;
        if (e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effects = DragDropEffects.Copy; e.Handled = true; }
    }

    void Window_Drop(object s, DragEventArgs e)
    {
        if (DockDropBlocked) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
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
        if (IsDock)
        {
            // 快捷栏：外部文件一律追加到末尾（没有方格可找），然后把宽高算一遍。
            ImportDockApps(files);
            e.Handled = true;
            return;
        }
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
        // 快捷栏：新条目要按单行排到末尾，条体宽度也要跟着长一格。
        if (IsDock) { ZoneLayout.ArrangeDock(_zone); ZoneLayout.ApplyDockSize(_zone); ApplyDockGeometry(); }
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
            s.EnableLiquidGlass ? _zone.GlassColorMode : null,
            _zone.GlassBlurAmount, _zone.GlassTintOpacity, _zone.GlassTintLuminosity,
            _zone.GlassMaterial);
    }

    void OpenSubfolderFlyout(ZoneItem sub)
    {
        // 先把「点击时抓到的锚点」取走并清掉：后面任何一条 early-return 都不会留下脏锚点。
        var clickAnchor = _flyoutAnchorOverride;
        _flyoutAnchorOverride = null;
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
        // 优先用「点击时抓到的那个图标元素」;另外两条路径(网格视图点击、悬停自动
        // 展开)拿到的容器是图标格/列表行,必须再往下一层取模板里那颗图标 —— 容器
        // 的宽度会被当成"图标宽度"参与定位,格子越大浮层离图标就越远。
        _flyoutOriginContainer = clickAnchor ?? FlyoutAnchorOf(FindContainerFor(sub));

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
        // 快捷栏：这个次级分区"打开着"→ 图标下面补一条横条（等浮层真开起来再刷）
        if (IsDock) RefreshDockRunStates();
    }

    void TryOpenFlyoutAnimated(ZoneItem sub)
    {
        // ponytail 2026-10: 先让布局把「这一次」的尺寸算完再定位。浮层是复用的单个控件,
        // 尺寸随内容(图标数量 → 2×2/3×3…、次级分区的 GridSize、名字长度)变化;旧代码在
        // Loaded 优先级直接读 ActualWidth/Height,读到的可能还是上一次打开时留下的旧尺寸 ——
        // 翻侧判定用的是假尺寸,浮层就落到"不该翻"的那一侧,而且偏得很远(次级分区越大、
        // 两次打开的尺寸差越大越明显)。UpdateLayout 强制按新内容完成一次测量/排布。
        try { SubfolderFlyoutView.UpdateLayout(); } catch { }
        if (SubfolderFlyoutView.ActualWidth <= 0 || SubfolderFlyoutView.ActualHeight <= 0)
        {
            Dispatcher.BeginInvoke(new Action(() => TryOpenFlyoutAnimated(sub)),
                System.Windows.Threading.DispatcherPriority.ContextIdle);
            return;
        }
        // 位置 + 锚点一次性定死(纯函数:图标位置、flyout 尺寸、工作区):
        //   pos = 贴着图标展在设置里的那个方位(默认左下),放不下时先左右翻、再上下翻、
        //         最后斜对角,间距恒为 FlyoutGap
        //   c   = 图标中心 - pos → TransformGroup 缩放锚点(以图标为原点)
        // 列表视图沿用先前观感:永远先朝图标右侧、浮层顶边与图标顶边齐平。
        var container = _flyoutOriginContainer ?? FlyoutAnchorOf(FindContainerFor(sub));
        var flyoutSize = new Size(SubfolderFlyoutView.ActualWidth, SubfolderFlyoutView.ActualHeight);
        var corner = _isGridView ? SubfolderFlyout.PreferredCorner : SubfolderFlyout.FlyoutCorner.BottomRight;
        var (pos, c) = SubfolderFlyout.ComputePosAndAnchor(
            container, flyoutSize, corner, SubfolderFlyout.FlyoutGap, alignToIconTop: !_isGridView);
        DzTrace.Log($"[SubFlyout] 定位: anchor={(container?.GetType().Name ?? "null")}#{container?.Name} " +
                    $"flyout={flyoutSize.Width:F0}x{flyoutSize.Height:F0} pos=({pos.X:F1},{pos.Y:F1}) " +
                    $"grid={_isGridView} corner={corner} anchorCenter={c}");
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
        // 快捷栏：浮层一关，那颗图标下面的横条也该立刻消失（别等 700ms 那次轮询）
        if (IsDock) RefreshDockRunStates();
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

    /// <summary>Lookup the FrameworkElement container that hosts <paramref name="sub"/>.
    /// 网格视图的容器在 ItemsHost 画布里；列表视图的容器在 ItemsListHost 里（此时画布
    /// 是 Collapsed 的，容器可能压根没生成）。两个宿主都查一遍，谁在当时可见就用谁。
    /// 返回 null = 容器还没生成（调用方应改用点击时抓到的元素）。
    /// ponytail 2026-08-26: ItemsHost binds ObservableCollection&lt;ZoneItemViewModel&gt;,
    /// so ContainerFromItem(raw ZoneItem) never matches — resolve the VM first.</summary>
    FrameworkElement? FindContainerFor(ZoneItem sub)
    {
        var vm = _vm.Items.FirstOrDefault(v => v.Id == sub.Id);
        if (vm == null) return null;
        if (ItemsListHost != null && ItemsListHost.Visibility == Visibility.Visible
            && ItemsListHost.ItemContainerGenerator.ContainerFromItem(vm) is FrameworkElement listRow)
            return listRow;
        return ItemsHost?.ItemContainerGenerator.ContainerFromItem(vm) as FrameworkElement;
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
            // ponytail 2026-09-26: 原来只 SaveConfig,设置界面收不到通知 —— 次级分区(SubFolder)
            // 自己的名字就显示在属性面板里,内层图标改名同理;走同一条「落盘 + 通知」收口。
            OnFlyoutItemsChanged();
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
            // ponytail 2026-08-31: 回收站图标在次级分区里时外层只是 SubFolder，
            // 上面的平铺扫描看不见它 → 轮询直接短路(hasRecycle=false)，浮层里的
            // 空桶/满桶图标永不刷新。这里递归进 SubItems 再判一次。
            if (!hasRecycle) hasRecycle = ZoneContainsRecycleBin(_zone.Items);
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

    /// <summary>ponytail 2026-08-31: 分区里(含次级分区内部，递归)是否存在回收站图标 —
    /// 轮询要先确认"有没有回收站要刷",否则次级分区里的回收站永远等不到刷新。</summary>
    static bool ZoneContainsRecycleBin(IEnumerable<ZoneItem> items)
    {
        foreach (var item in items)
        {
            if (item.Type == ItemType.ShellLocation && ShellIconService.IsRecycleBin(item.TargetPath))
                return true;
            if (item.Type == ItemType.SubFolder && ZoneContainsRecycleBin(item.SubItems))
                return true;
        }
        return false;
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
        if (IsDock)
        {
            // 快捷栏的宽高由内容算出来，不能走分区那套 <100 的兜底。
            ZoneLayout.ApplyDockSize(_zone);
            _zone.Width = Math.Max(40, _zone.Width);
            _zone.Height = Math.Max(40, _zone.Height);
        }
        else
        {
            if (_zone.Width < 100) _zone.Width = 400; if (_zone.Height < 100) _zone.Height = 300;
        }
        Width = _zone.Width; ApplyProgrammaticHeight(TileWindowHeight()); Left = _zone.X; Top = _zone.Y;
        // 已经展开显示的窗口不再重复播放 wave 动画(再次「全部显示」时跳过)，
        // 但仍走 SnapToExpanded 以维持「永久展开」(不会因鼠标离开而自动收起)。
        bool alreadyExpanded = _hover?.IsExpanded == true
            && MainContent.Visibility == Visibility.Visible
            && RestoreButton.Visibility != Visibility.Visible;
        // ponytail 2026-09-26「先出一个整窗框再展开」修复:窗口尺寸必须在**播展开动画之前**
        // 就位(上面那句 Width/ApplyProgrammaticHeight)。原先收缩到 36×36 再 Hide() 的整窗
        // 隐藏态,Show() 会先把那个 36×36 的空窗口显示出来,而恢复尺寸落在动画启动之后 ——
        // 于是动画第一帧先出现一个全尺寸空窗轮廓,内容随后才在框里缩放展开。关闭路径没有
        // 这个问题,因为带恢复按钮的收起本来就不改窗口尺寸。改动时务必保持「先定尺寸再动画」。
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
        DesktopLayer.BringToFront(this); if (IsDock) SyncDockGlassWindow();
        // 快捷栏不做整窗圆角/边框（见 ReapplyAcrylic 里「一圈鬼影」的说明）。
        if (IsDock) ApplyDockWindowFrame(_zone.TileMode);
        else NativeMethods.SetRoundedCorners(this, (int)_zone.CornerRadius);
        _mgr.FireZoneVisibilityChanged(_zone.Id, true);
    }

    public void HideZone(double waveDelayMs = 0)
    {
#if DEBUG
        DzTrace.Log($"[ZoneWindow] HideZone(wave={waveDelayMs}) ENTRY winVisible={IsVisible} content={MainContent.Visibility} btn={RestoreButton.Visibility} hoverExpanded={_hover?.IsExpanded} modelVisible={_zone.IsVisible} restoreEnabled={_zone.EnableRestoreButton} size={Width}x{Height}");
#endif
        // Save dimensions only if not currently minimized (RestoreButton not visible)
        // If minimized, the original dimensions are already saved in _zone
        HideDockGlassWindow();      // 快捷栏的玻璃层是独立窗口，隐藏分区时一起收走
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
            DesktopLayer.BringToFront(this); if (IsDock) SyncDockGlassWindow();
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
            if (IsDock)
            {
                ZoneLayout.ApplyDockSize(_zone);
                Width = Math.Max(ZoneLayout.DockEmptyWidth, _zone.Width);
                ApplyProgrammaticHeight(Math.Max(40, _zone.Height));
            }
            else
            {
                Width = _zone.Width < 100 ? 400 : _zone.Width;
                ApplyProgrammaticHeight(_zone.Height < 100 ? 300 : TileWindowHeight());
            }
            // ponytail: keep window at full size; HoverExpandBehavior owns
            // visibility/scale from here.
            _hover?.SnapToCollapsed();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        // ponytail 2026-09-26: 先摘订阅再做任何会抛 ZonesChanged 的事 —— 下面的标题提交会
        // 走 ZoneManager.RenameZone → NotifyChanged,若此时自己还订阅着,OnZonesChanged 会
        // 排一个 BeginInvoke 到关窗之后再跑(RefreshItems/ApplyStyle/RefreshFolderMapping
        // 在已关闭的窗口上跑)。顺序与 StickyNoteWindow.OnClosed 同策。
        _mgr.ZonesChanged -= OnZonesChanged;
        _mgr.LockChanged -= OnServiceLockChanged;
        // 玻璃层是独立窗口，必须跟着关掉，否则会剩下一个胶囊形状的毛玻璃残影。
        CloseDockGlassWindow();
        // 关窗前把标题栏里还没提交的改名落盘(用户敲完直接关/隐藏窗口).
        CommitTitleRename();
        _saveDebounce?.Stop();
        _titleCommitDebounce.Stop();
        _recycleTimer.Stop();
        _dockRunTimer?.Stop();
        _dockHoverWatch?.Stop();
        _dockHoverEndTimer?.Stop();
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
        if (_src != null) { _src.RemoveHook(WndProc); _src = null; }
        if (_langChanged != null) { _loc.LanguageChanged -= _langChanged; _langChanged = null; }
        if (h != IntPtr.Zero) NativeMethods.DragAcceptFiles(h, false);
        if (h != IntPtr.Zero) DockStartButtonHook.Unregister(h);
        base.OnClosed(e);
    }

    void OnZonesChanged()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _vm.RefreshItems();
            UpdateCanvasSize();
            // 快捷栏：图标被别处增删（跨分区拖入、管理器加图标）也要重排 + 重算宽高。
            if (IsDock) ApplyDockGeometry();
            // ponytail: keep the title NAME in sync on every ZonesChanged, not just on the
            // style-panel's direct RefreshZone preview. Rename/merge paths that only fire
            // ZonesChanged used to leave the title bar showing the old name (ApplyStyle
            // below refreshes the icon/colors but never the title text).
            ZoneTitleText.Text = _zone.Name;
            // ponytail: Fix C — re-apply body content color after RefreshItems wipes the
            // brush via the XAML default `#E0FFFFFF` foreground on freshly-generated item
            // containers. Without this, any OnZonesChanged trigger (rename, delete, etc.)
            // would silently revert the previously-applied brush on all items.
            ApplyStyle();
            UpdateMergedTitle();
            RefreshFolderMapping();
        }), System.Windows.Threading.DispatcherPriority.Normal);
    }

    // ── Drag: DIRECT handler on title bar ──

    void TitleBar_Drag(object s, MouseButtonEventArgs e)
    {
        // ponytail 2026-09-26: 在标题栏空白处按下 = "改完点别处"最常见的一种 —— 但标题栏
        // 不可聚焦,TextBox 的 LostFocus 不会触发,所以这里主动提交一次再开始拖动。
        CommitTitleRename();
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

        DesktopLayer.BringToFront(this); if (IsDock) SyncDockGlassWindow();
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
#if DEBUG
        if (IsDock) DzTrace.Log($"[WinPreview] src={e.OriginalSource?.GetType().Name} main={MainContent.Visibility} pos={e.GetPosition(this)}");
#endif
        if (e.OriginalSource is TextBox) return; // title inline editing
        // Items / list / chrome presses are owned by their own handlers.
        if (IsOnFolderEntry(e.OriginalSource) || IsOnItem(e.OriginalSource)) return;
        if (IsWithinZoneChrome(e.OriginalSource)) return;
        if (MainContent.Visibility != Visibility.Visible) return;
        if (FolderMappingView.Visibility == Visibility.Visible) return;

        // 快捷栏：没有标题栏，条体空白处按下就是拖动整窗（也不做框选/多选）。
        // 开始按钮例外：按下即弹开始菜单，不能被条体拖动吃掉这一下。
        if (IsDock)
        {
            if (DockStartBtn != null && DockStartBtn.Visibility == Visibility.Visible && DockStartBtn.IsMouseOver)
            {
                DockStartClick();
                e.Handled = true;
                return;
            }
            StartBodyDrag(e);
            return;
        }

        // 自定义图标模式下第二次按下（ClickCount==2）不启动框选，交给双击打开。
        if (_customIconOpenFirst && e.ClickCount == 2) return;

        // Ctrl+拖拽 = 拖动整窗。
        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            StartBodyDrag(e);
            return;
        }

        // 直接拖拽空白 = 框选（原 Ctrl+拖拽行为，用户要求"反一下"）。
        // 列表视图里没有可框选的画布（图标不按 X/Y 摆），空白处按下就不起框选。
        if (!_isGridView) return;
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
        DesktopLayer.BringToFront(this); if (IsDock) SyncDockGlassWindow();
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
        // ponytail 2026-09-26(审计修订): 只有「映射区显示与否」**真的翻转**时才需要重跑样式。
        // 那条 ApplyStyle 存在的唯一理由就是"映射表头是标题栏带子的一部分,填充/裁剪要跟着
        // 开关走"(见下面的原注释);而它每次都被无条件调用,于是每次面板编辑都要跑两遍
        // ApplyStyle(RefreshZone 里一遍 + 这里一遍)—— 每遍含背景图解码、遍历所有图标容器
        // 改画刷、重建子分区页签,拖一次滑块就是几十遍。
        bool visibilityChanged = _folderMappingShown != show;
        _folderMappingShown = show;
        FolderMappingView.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ItemsViewport.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        // The mapping header row is part of the title-bar band — re-run the style
        // pass so the independent-fill clip / header fill follow the toggle.
        if (visibilityChanged) ApplyStyle();
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

    // 快捷栏没有方格网，导入一律追加到末尾（X 由序号算，Y 是那条单行的固定值）。
    (double, double) FindFreeSpot() => IsDock
        ? (ZoneLayout.DockX(_zone, _zone.Items.Count), ZoneLayout.DockBandTop(_zone))
        : ZoneLayout.FindFreeSpot(_vm.GetPlacementItems(), _zone.Width, _zone.Height, _zone.GridSize, _zone.GridSize);

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
        if (IsDock) { ArrangeDockItems(); return; }
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

        // 快捷栏：和分区菜单对齐（缺的功能补齐），只去掉两条快捷栏里没有意义的东西：
        // ①「文件夹映射」以及依赖它的「粘贴」（快捷栏不做映射/监听）
        // ② 组合分区的「解散」（快捷栏不参与合并组）
        // 保留：添加应用… / 导入 ▶ / 新建次级分区 / 新建 ▶ / 设置 / 锁定 / 最小化 /
        //       完全隐藏 / 删除快捷栏。
        if (IsDock)
        {
            CtxDockAddApp.Visibility = Visibility.Visible;
            CtxDockSep.Visibility = Visibility.Visible;
            CtxImport.Visibility = Visibility.Visible;
            CtxImportSep.Visibility = Visibility.Visible;
            CtxNewSubZone.Visibility = Visibility.Visible;
            CtxNew.Visibility = Visibility.Visible;
            CtxSettings.Visibility = Visibility.Visible;
            CtxLock.Visibility = Visibility.Visible;
            CtxMinimize.Visibility = Visibility.Visible;
            CtxFullHide.Visibility = Visibility.Visible;
            CtxMapFolderNew.Visibility = Visibility.Collapsed;
            CtxDisbandAll.Visibility = Visibility.Collapsed;
            CtxDisbandThis.Visibility = Visibility.Collapsed;
            CtxMergeSep.Visibility = Visibility.Collapsed;
            CtxPaste.Visibility = Visibility.Collapsed;
            CtxPasteSep.Visibility = Visibility.Collapsed;
            CtxDelete.Header = _loc["DockMenu.DeleteDock"];
            NormalizeMenuSeparators((ContextMenu)s);
            return;
        }

        // 分区：把快捷栏专用的两项收回去（同一个 ContextMenu 只在一种形态下用，
        // 这里仍显式复位，避免将来两条路径复用同一个菜单时串味）。
        CtxDockAddApp.Visibility = Visibility.Collapsed;
        CtxDockSep.Visibility = Visibility.Collapsed;
        CtxFullHide.Visibility = Visibility.Collapsed;
    }

    /// <summary>收拾右键菜单里的空段：开头/结尾的分隔线与相邻重复的分隔线都藏起来。
    /// 快捷栏隐藏掉一半菜单项之后，不收拾会看到「菜单第一项就是一条横线」。</summary>
    static void NormalizeMenuSeparators(ContextMenu cm)
    {
        bool lastWasSeparator = true;   // 开头视作已有分隔线 → 首个分隔线会被隐藏
        foreach (var entry in cm.Items)
        {
            if (entry is Separator sep)
            {
                sep.Visibility = lastWasSeparator ? Visibility.Collapsed : Visibility.Visible;
                lastWasSeparator = sep.Visibility == Visibility.Visible;
            }
            else if (entry is UIElement ui && ui.Visibility == Visibility.Visible)
            {
                lastWasSeparator = false;
            }
        }
        // 结尾的分隔线也藏掉
        for (int i = cm.Items.Count - 1; i >= 0; i--)
        {
            if (cm.Items[i] is Separator tail) { tail.Visibility = Visibility.Collapsed; break; }
            if (cm.Items[i] is UIElement t && t.Visibility == Visibility.Visible) break;
        }
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
#if DEBUG
        DzTrace.Log($"[Restore] down pos={e.GetPosition(this)} main={MainContent.Visibility} hover={_hover?.IsExpanded}");
#endif
        _restoreDragging = false;
        _restoreDown = e.GetPosition(this);
        RestoreButton.CaptureMouse();
        e.Handled = true;
    }

    void Restore_MouseMove(object s, MouseEventArgs e)
    {
        if (!RestoreButton.IsMouseCaptured) return;
        // 锁定的窗口不许通过恢复按钮拖动整窗（锁 = 位置别动）。
        if (_zone.IsLocked) return;
        var d = e.GetPosition(this) - _restoreDown;
        if (Math.Abs(d.X) > 3 || Math.Abs(d.Y) > 3)
        {
            _restoreDragging = true;
            RestoreButton.ReleaseMouseCapture();
            _snapDrag?.Start(e, () =>
            {
                DesktopLayer.BringToFront(this); if (IsDock) SyncDockGlassWindow();
                _zone.X = Left; _zone.Y = Top; _mgr.SaveConfig();
            });
        }
    }

    void Restore_MouseUp(object s, MouseButtonEventArgs e)
    {
#if DEBUG
        DzTrace.Log($"[Restore] up dragging={_restoreDragging}");
#endif
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

    // ponytail: 小按钮悬停高光已收口到 Helpers/HoverHighlight.cs（全应用唯一配方 #30FFFFFF，
    // 平时透明、悬停铺高光、不改字色）。原来的 Ctrl_Enter/Ctrl_Leave + CtrlHoverBrush/
    // CtrlIdleBrush（常驻 #30FFFFFF 底 + 悬停变 #60FFFFFF）已删除。
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

    /// <summary>上一次真正画到界面上的锁定态（用于 RefreshZone 时按需重刷，不重复 Pin）。</summary>
    bool? _appliedLock;

    void ApplyLockState()
    {
        var vm = DataContext as ZoneViewModel;
        if (vm == null) return;
        _appliedLock = _zone.IsLocked;
        LockBtnText.Text = vm.IsLocked ? "🔒" : "🔓";
        TitleBarBg.Cursor = vm.IsLocked ? System.Windows.Input.Cursors.Arrow : System.Windows.Input.Cursors.SizeAll;
        // 快捷栏没有缩放柄（条体尺寸由内容算），锁定切换不许把它们放出来。
        GripTL.Visibility = GripTR.Visibility = GripBL.Visibility = GripBR.Visibility =
            (vm.IsLocked || IsDock) ? Visibility.Collapsed : Visibility.Visible;
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

    // ── 内容视图切换（标题栏 ▦/≡，参照面板 PanelWindow.GridToggle/ListToggle）──

    /// <summary>标题栏一量出尺寸就把标题输入框的宽度上限夹到「栏宽 − 右侧按钮排」——
    /// 那排是横向 StackPanel，子元素按无限宽测量，不夹一下窄分区里标题会压到 ▦/≡ 下面。</summary>
    void TitleBarBg_SizeChanged(object s, SizeChangedEventArgs e) => UpdateTitleMaxWidth();

    void UpdateTitleMaxWidth()
    {
        if (ZoneTitleText == null || TitleBarBg == null) return;
        double bar = TitleBarBg.ActualWidth;
        if (bar <= 0) return;
        double buttons = ControlPoint != null && ControlPoint.Visibility == Visibility.Visible
            ? ControlPoint.ActualWidth + ControlPoint.Margin.Right : 0;
        // 8 左距 + 图标(12) + 图标间距(4) + 一点点余量
        double avail = bar - buttons - 28;
        var capped = Math.Clamp(Math.Round(avail), 28, 160);
        if (Math.Abs(ZoneTitleText.MaxWidth - capped) > 0.5) ZoneTitleText.MaxWidth = capped;
    }

    void GridToggle_Click(object s, MouseButtonEventArgs e)
    {
        SetGridView(true, save: true);
        e.Handled = true;
    }

    void ListToggle_Click(object s, MouseButtonEventArgs e)
    {
        SetGridView(false, save: true);
        e.Handled = true;
    }

    /// <summary>切到网格(<paramref name="grid"/>=true)或列表视图。只切显示，不动任何
    /// 图标的 X/Y —— 切回网格时还是原来那个摆法。</summary>
    void SetGridView(bool grid, bool save)
    {
        if (_isGridView == grid)
        {
            // 同一态再点一次也要把底盘刷对（防止悬停高光残留）。
            ApplyViewToggleBackgrounds();
            return;
        }
        _isGridView = grid;
        _zone.IsGridView = grid;
        ApplyItemHostsVisibility();
        ApplyViewToggleBackgrounds();
        if (grid)
        {
            // 切回网格：重找一次画布实例（宿主显隐后容器可能被重建），再补一次画布尺寸，
            // 否则 UpdateCanvasSize 可能还在给旧 Canvas 设 Width/Height。
            _itemCanvas = FindVisualChild<Canvas>(this);
            UpdateCanvasSize();
        }
        else
        {
            // 进列表前按当前 X/Y 重排一次（网格里拖过图标 → 顺序变了，VM 不发通知）。
            _listView?.Refresh();
        }
        // 刚显隐的宿主要下一次布局才生成容器 —— 名称隐藏状态在布局完成后补一遍。
        Dispatcher.BeginInvoke(new Action(() => ApplyHideAppName(_zone.HideAppName)),
            System.Windows.Threading.DispatcherPriority.Loaded);
        if (save) ScheduleSave();
    }

    /// <summary>网格画布 / 列表宿主二选一（自定义图标模式两个都藏）。</summary>
    void ApplyItemHostsVisibility()
    {
        if (IsDock)
        {
            // 快捷栏永远是「网格画布」那一套：没有列表视图，也没有单图标模式。
            if (ItemsHost != null) ItemsHost.Visibility = Visibility.Visible;
            if (ItemsListHost != null) ItemsListHost.Visibility = Visibility.Collapsed;
            return;
        }
        EnsureListViewSource();
        if (ItemsHost != null)
            ItemsHost.Visibility = (!_customIconOpenFirst && _isGridView) ? Visibility.Visible : Visibility.Collapsed;
        if (ItemsListHost != null)
            ItemsListHost.Visibility = (!_customIconOpenFirst && !_isGridView) ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>列表视图的数据源 = Items 的排序视图（按 Y 再按 X，等价于网格里的「从上到下、
    /// 从左到右」阅读顺序），否则列表顺序会和画布对不上。
    ///
    /// 只在首次建视图（或集合被换掉）时设一次 ItemsSource；排序在被切进列表时由
    /// <c>SetGridView</c> 里的 Refresh 触发 —— 不能每次 ApplyStyle 都刷，否则拖动
    /// 外观滑块（每帧一次 ApplyStyle）会不停重建整列行容器。</summary>
    void EnsureListViewSource()
    {
        if (ItemsListHost == null) return;
        if (_listView != null && ReferenceEquals(_listView.SourceCollection, _vm.Items)) return;
        var view = new System.Windows.Data.ListCollectionView(_vm.Items);
        view.SortDescriptions.Add(new System.ComponentModel.SortDescription(
            nameof(ZoneItemViewModel.Y), System.ComponentModel.ListSortDirection.Ascending));
        view.SortDescriptions.Add(new System.ComponentModel.SortDescription(
            nameof(ZoneItemViewModel.X), System.ComponentModel.ListSortDirection.Ascending));
        _listView = view;
        ItemsListHost.ItemsSource = view;
    }

    /// <summary>▦/≡ 的选中态底盘 + 悬停结束复位色（HoverHighlight.BaseBackground）。</summary>
    void ApplyViewToggleBackgrounds()
    {
        if (GridToggleBtn != null)
        {
            GridToggleBtn.Background = _isGridView ? ViewToggleIdleBrush : Brushes.Transparent;
            Helpers.HoverHighlight.SetBaseBackground(GridToggleBtn, GridToggleBtn.Background);
        }
        if (ListToggleBtn != null)
        {
            ListToggleBtn.Background = _isGridView ? Brushes.Transparent : ViewToggleIdleBrush;
            Helpers.HoverHighlight.SetBaseBackground(ListToggleBtn, ListToggleBtn.Background);
        }
        ApplyViewToggleGlyphColor();
    }

    /// <summary>选中那枚用「按钮颜色」原色，另一枚压到 45% —— 与面板 ▦/≡ 的亮/暗区分一致。
    /// 只动字色，不动 Border.Opacity（那口子归 HoverHighlight 的按钮透明度管）。</summary>
    void ApplyViewToggleGlyphColor()
    {
        var full = _viewToggleBrush ?? Brushes.White;
        var dim = DimBrush(full);
        if (GridToggleText != null) GridToggleText.Foreground = _isGridView ? full : dim;
        if (ListToggleText != null) ListToggleText.Foreground = _isGridView ? dim : full;
    }

    static Brush DimBrush(Brush b)
    {
        if (b is SolidColorBrush sb)
        {
            var c = sb.Color;
            return Freeze(new SolidColorBrush(Color.FromArgb((byte)(c.A * 0.45), c.R, c.G, c.B)));
        }
        return b;
    }

    // ── 列表视图的行交互（列表模板里挂的是这几个，网格模板挂 Item_MouseDown 等）──

    void ListRow_MouseDown(object s, MouseButtonEventArgs e)
    {
        if (s is not FrameworkElement fe || fe.DataContext is not ZoneItemViewModel vm) return;
        // 浮层锚点 = 这一行里的图标（与面板把「被点的卡片」里的图标传给
        // ComputePosAndAnchor 同一思路）。列表行整行都很宽，拿整行当锚点会把浮层
        // 甩到窗口外面去。
        if (vm.Type == ItemType.SubFolder)
            _flyoutAnchorOverride = FlyoutAnchorOf(fe);
        // 双击 = 打开（次级分区没有可启动路径 → 弹浮层），与网格视图一致。
        if (e.ClickCount == 2)
        {
            if (vm.Type == ItemType.SubFolder)
            {
                var sub = ResolveSourceZoneItem(vm);
                if (sub != null) { OpenSubfolderFlyout(sub); e.Handled = true; return; }
            }
            Open(vm);
            e.Handled = true;
            return;
        }
        // Ctrl+点击 = 多选开关（与网格视图同规则）。
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            vm.IsSelected = !vm.IsSelected;
            e.Handled = true;
            return;
        }
        // 普通点击 = 单选；已选中的保持多选（Explorer 习惯）。
        if (!vm.IsSelected)
        {
            foreach (var o in _vm.Items) o.IsSelected = false;
            vm.IsSelected = true;
        }
        // 次级分区：单击即弹浮层（网格视图是「按下-抬起没拖动」才弹，列表里没有拖拽）。
        if (vm.Type == ItemType.SubFolder)
        {
            var sub = ResolveSourceZoneItem(vm);
            if (sub != null) OpenSubfolderFlyout(sub);
        }
        e.Handled = true;
    }

    /// <summary>浮层锚点 = 模板里那颗图标(网格 = Image / 次级分区的 2×2 预览;
    /// 列表 = 行内图标),名字统一叫 ItemIcon(与面板卡片同名)。
    /// **不能**把图标格/列表行的容器当锚点 —— 容器宽度会被当作"图标宽度"参与定位,
    /// 格子越大浮层离图标越远(见 SubfolderFlyout.ComputePosAndAnchor)。
    /// 找不到就退回容器本身。</summary>
    FrameworkElement? FlyoutAnchorOf(FrameworkElement? container)
        => container == null
            ? null
            : FindVisualChild<FrameworkElement>(container, f => f.Name == "ItemIcon") ?? container;

    void ListRow_Enter(object s, MouseEventArgs e)
    {
        if (s is Grid g && FindVisualChild<Border>(g, b => b.Name == "RowHoverBox") is { } box)
            box.Background = ItemHoverBrush;
    }

    void ListRow_Leave(object s, MouseEventArgs e)
    {
        if (s is Grid g && FindVisualChild<Border>(g, b => b.Name == "RowHoverBox") is { } box)
            box.Background = Brushes.Transparent;
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
        // 快捷栏：单击打开（默认）时，双击不再额外触发一次打开 —— 交给 MouseUp 判定。
        bool dockSingleClick = IsDock && _zone.DockClickOnce;
        if (e.ClickCount == 2 && !dockSingleClick)
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
        // 快捷栏：不做多选/框选，只记录拖拽起点（拖=换位，松开时若没拖就打开）。
        if (IsDock)
        {
            if (s is FrameworkElement del && del.DataContext is ZoneItemViewModel dvm)
            {
                _dv = dvm; _de = del; _ds = e.GetPosition(this);
                _is = new Point(dvm.X, dvm.Y); _dragging = false; del.CaptureMouse();
                e.Handled = true;
            }
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
        // ponytail 2026-11-01「拖动卡住」修复 —— 拖动途中左键已经抬起却没收到 MouseUp
        // （最常见：点了图标启动程序 → 前台被那个程序抢走 → 系统把我们的鼠标捕获收走，
        //  WM_LBUTTONUP 从此派发不到这个元素）。老代码不检查按键状态，`_dragging` 会永远是
        // true，于是之后每次移动光标都把图标拖过去 —— 用户看到的就是"图标卡在动画里、
        // 要再拖一次才恢复"。这里一旦发现左键不在按下态就立刻收尾。
        if (_dv != null && e.LeftButton != MouseButtonState.Pressed)
        {
            CancelItemDrag("left button no longer pressed");
            return;
        }
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
            // 锁定的快捷栏：图标不跟手（拖了就只是"划了一下"），透明/位置都不动。
            _de.Opacity = (IsDock && _zone.IsLocked) ? 1.0 : 0.7;
        }

        // 锁定的快捷栏：整排布局不许改 —— 不跟手、不换位、不做跨分区幽灵。
        // （_dragging 已经置 true，所以松手时不会把这次拖动当成一次单击去启动程序。）
        if (IsDock && _zone.IsLocked) return;

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
        if (IsDock)
        {
            DockDragFollow(d, e.GetPosition(ItemsHost));
            return;
        }
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

    /// <summary>
    /// 快捷栏的单行跟手：只允许左右换位，垂直方向固定（图标不许飞出条体）；
    /// 压到同条快捷栏里的次级分区上时放大它（松手移入）。
    /// 锁定状态下**整段不执行** —— 布局不许被拖动改掉（见 Item_MouseMove 的说明）。
    /// </summary>
    void DockDragFollow(Vector d, Point posInItems)
    {
        if (_dv == null) return;
        if (_zone.IsLocked) return;
        _dv.X = Math.Max(0, Math.Min(_is.X + d.X, Math.Max(0, _zone.Width - ItemW)));
        if (_dv.Type != ItemType.SubFolder)
        {
            var overSubDock = FindSubfolderTarget(posInItems);
            if (overSubDock != null) SetSubfolderDragScale(FindContainerFor(overSubDock));
            else ClearSubfolderDragScale();
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
        var dv = _dv;
        var de = _de;
        bool dragged = _dragging;
        if (dv == null) return;
        // 先把状态清空、再放捕获 —— 否则 ReleaseMouseCapture 会同步触发 LostMouseCapture，
        // 收尾逻辑看到 _de 还在就会把这次"正常松手"当成丢捕获取消掉。
        _dv = null; _de = null; _dragging = false;
        if (de != null)
        {
            try { de.ReleaseMouseCapture(); } catch { }
            de.Opacity = 1.0;
        }
        if (dragged)
        {
            // 跨分区提交优先:悬停在其它分区上释放 → 移动到该分区;拒收/未悬停
            // 则回退本地换位/移动(次级文件夹图标与普通图标同规则)。
            var ext = _externalTarget;
            _externalTarget = null;
            ext?.HideExternalDropGhost();
            if (ext == null || !CommitCrossZoneMove(ext, PointToScreen(e.GetPosition(this)), dv))
            {
                if (IsDock)
                {
                    // 快捷栏：压到次级分区上 → 移入；否则按落点算目标序号换位
                    // （单行只有 X 有意义，不走分区的方格吸附）。
                    var overSubDock = dv.Type == ItemType.SubFolder
                        ? null : FindSubfolderTarget(e.GetPosition(ItemsHost));
                    if (overSubDock != null) MoveIntoSubfolder(dv, overSubDock);
                    else CommitDockReorder(dv);
                }
                else
                {
                    // 命中 SubFolder → 移入;否则普通换位/移动(次级文件夹图标与普通图标同规则)。
                    var overSub = FindSubfolderTarget(e.GetPosition(ItemsHost));
                    if (overSub != null && dv.Type != ItemType.SubFolder)
                        MoveIntoSubfolder(dv, overSub);
                    else if (_zone.SnapToGrid) ReorderItemInto(dv, dv.X, dv.Y);
                    else { _vm.MoveItem(dv.Id, dv.X, dv.Y, snapToGrid: false); _vm.RefreshMergedItems(); }
                }
            }
        }
        else if (IsDock && _zone.DockClickOnce && dv.Type != ItemType.SubFolder)
        {
            // 快捷栏：松开时没拖动 = 单击 → 打开（与任务栏一致）。
            Open(dv);
        }
        else if (dv.Type == ItemType.SubFolder)
        {
            // 单击未拖动 → 打开 flyout(点击 vs 拖拽消歧;Ctrl+click 已在 MouseDown 拦下)。
            var sub = ResolveSourceZoneItem(dv);
            if (sub != null) OpenSubfolderFlyout(sub);
        }
        HideDropIndicator();
        ClearSubfolderDragScale();
    }

    /// <summary>
    /// 取消一次还没收尾的图标拖动（幂等）。什么时候会用到：拖动途中丢了鼠标捕获
    /// （`LostMouseCapture`）、左键已经不在按下态（`Item_MouseMove` 的前置检查）、
    /// 窗口失去激活（点了图标启动程序，前台被那个程序抢走 —— 就是用户报的
    /// 「打开软件的同时拖动图标，图标卡在动画里」）。
    ///
    /// 只回滚快捷栏的落点：快捷栏的位置由顺序算出来（`ArrangeDock`），拖到一半的
    /// X 是"跟手"的临时值，不回滚图标就会停在半路。分区仍然保持"拖动即落点"的老行为，
    /// 免得改变桌面分区的既有手感。
    /// </summary>
    void CancelItemDrag(string why)
    {
        var de = _de;
        _dv = null; _de = null; _dragging = false;
        if (de != null)
        {
            try { de.ReleaseMouseCapture(); } catch { }
            de.Opacity = 1.0;
        }
        HideDropIndicator();
        ClearSubfolderDragScale();
        if (IsDock)
        {
            ZoneLayout.ArrangeDock(_zone);
            ArrangeDockItems();
            ResetDockRowTransforms();
            RefreshDockReflection();
        }
        DzTrace.Log($"[Dock drag] cancel: {why}");
    }

    /// <summary>鼠标捕获在拖动途中被系统收走（别的窗口被激活时会这样）→ 取消拖动。</summary>
    void OnAnyLostMouseCapture(object s, MouseEventArgs e)
    {
        if (_dv == null || _de == null) return;                  // 正常松手已经先清过了
        if (!ReferenceEquals(e.OriginalSource, _de)) return;     // 不是我们拖的那一格
        CancelItemDrag("lost mouse capture");
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
    /// 返回 false = 目标拒收,调用方回退本地换位。
    /// ponytail 2026-11-01: 被拖的条目改成**参数**传进来 —— 调用点（Item_MouseUp）现在会
    /// 先清 `_dv` 再提交（为了不让 ReleaseMouseCapture 触发的 LostMouseCapture 把这次正常
    /// 松手当成丢捕获取消），继续读 `_dv` 会永远是 null、跨分区拖拽全部失效。</summary>
    bool CommitCrossZoneMove(ZoneWindow target, Point screenPt, ZoneItemViewModel dragged)
    {
        if (dragged == null) return false;
        var raw = ResolveSourceZoneItem(dragged);
        if (raw == null) return false;
        var owner = OwnerZoneOf(dragged) ?? _zone;
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
        // 锁定的快捷栏拒收（拖进来的图标会把条体撑长 = 改布局）。
        if (recv.DockMode && recv.IsLocked) return false;
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
        if (recv.DockMode)
        {
            // 拖进快捷栏 = 追加到末尾（不做方格落位），宽高跟着重算。
            ZoneLayout.ArrangeDock(recv);
            ZoneLayout.ApplyDockSize(recv);
            if (ReferenceEquals(recv, _zone)) ApplyDockGeometry();
        }
        return true;
    }

    void Item_Enter(object s, MouseEventArgs e)
    {
#if DEBUG
        // 自测用：悬停抽搐 = Enter/Leave 在同一个图标上来回打。记时间 + 序号 + 光标位置，
        // 复现时看两件事：① 同一格短时间反复 enter/leave；② 每次都在光标没动的时候发生。
        if (IsDock) HoverNote($"enter [{DockIndexOf(s)}] over={ItemIsOver(s)} pos={Mouse.GetPosition(this)}");
#endif
        // 快捷栏开了「悬停动效」（放大）就不铺高光：放大 + 相邻推让本身已经是反馈，
        // 再叠一层高光会和被推开的图标底色打架（属性面板里这两个也不再同时生效）。
        bool highlight = !(IsDock && _zone.DockAnimate);
        if (highlight && s is Grid g)
        {
            var hover = FindVisualChild<Border>(g, b => b.Name is "HoverBox" or "SubHoverBox");
            if (hover != null) hover.Background = ItemHoverBrush;
        }
        if (IsDock)
        {
            DockHoverBegin(s);
            // 只有真实鼠标事件才启动对账轮询（离屏自测传 null，不该被它复原放大态）。
            if (e != null && _dockHoverIndex >= 0) StartDockHoverWatch();
        }
    }

    void Item_Leave(object s, MouseEventArgs e)
    {
#if DEBUG
        if (IsDock) HoverNote($"leave [{DockIndexOf(s)}] over={ItemIsOver(s)} pos={Mouse.GetPosition(this)}");
#endif
        if (s is Grid g)
        {
            var hover = FindVisualChild<Border>(g, b => b.Name is "HoverBox" or "SubHoverBox");
            if (hover != null) hover.Background = Brushes.Transparent;
        }
        if (IsDock) DockHoverEnd();
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
        EmptyRecycleBin();
        // Refresh the bin icon immediately instead of waiting for the next poll tick.
        _recycleStateInit = false;
        foreach (var item in _vm.Items)
        {
            if (item.Type == ItemType.ShellLocation && ShellIconService.IsRecycleBin(item.TargetPath))
                item.RefreshIcon();
        }
    }

    /// <summary>ponytail 2026-08-31: 清空回收站 — 主分区右键与次级分区浮层内层图标
    /// 共用(SHEmptyRecycleBinW 的 hwnd 只是 UIPI 父窗口，用本窗口句柄即可)。</summary>
    void EmptyRecycleBin()
    {
        try
        {
            NativeMethods.SHEmptyRecycleBinW(new WindowInteropHelper(this).Handle, null,
                NativeMethods.SHERB_NOCONFIRMATION | NativeMethods.SHERB_NOPROGRESSUI | NativeMethods.SHERB_NOSOUND);
        }
        catch { }
        ShellIconService.InvalidateRecycleBinState();
    }

    /// <summary>ponytail 2026-08-31: 浮层内层图标「清空回收站」— 与主分区右键同款，
    /// 清空后本分区主图标 + 浮层内层图标一起刷新，浮层开着时立即变空桶(浮层内层图标
    /// 有自己的 VM 与 Icon 缓存，主分区那份循环刷不到)。</summary>
    void OnFlyoutItemEmptyRecycleBin(ZoneItemViewModel vm)
    {
        EmptyRecycleBin();
        _recycleStateInit = false;
        foreach (var item in _vm.Items)
        {
            if (item.Type == ItemType.ShellLocation && ShellIconService.IsRecycleBin(item.TargetPath))
                item.RefreshIcon();
        }
        if (SubfolderFlyoutView.ViewModel is { } fvm)
        {
            foreach (var item in fvm.ItemVms)
            {
                if (item.Type == ItemType.ShellLocation && ShellIconService.IsRecycleBin(item.TargetPath))
                    item.RefreshIcon();
            }
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

    void Open(ZoneItemViewModel v)
    {
        // 快捷栏 + 运行状态指示开着：程序已经在跑 → 和 Windows 任务栏同一套语义：
        //   · 它已经是前台 → **最小化**
        //   · 否则 → 切到前台
        // 功能关掉就退回普通「启动」语义。
        if (IsDock && _zone.DockRunIndicator && TryToggleRunningApp(v)) return;
        try { ShellLocationResolver.Open(v.TargetPath, v.Type); }
        catch (Exception ex)
        {
            var loc = LocalizationService.Instance;
            MessageBox.Show($"{loc["Item.FailedToOpen"]}\n{ex.Message}", loc["Item.FailedToOpen.Title"], MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// 条目已经在运行 → 切前台 / 再点最小化。返回 true 表示已处理（不用再启动）。
    ///
    /// 「刚才是不是它」不能现问 GetForegroundWindow()：点击已经把我们的窗口激活了。
    /// 优先用 WM_MOUSEACTIVATE 抓到的那个窗口，其次用轮询记下的「最近一个别人的前台窗口」。
    /// </summary>
    bool TryToggleRunningApp(ZoneItemViewModel v)
    {
        if (v.Type is ItemType.SubFolder or ItemType.Folder or ItemType.ShellLocation) return false;
        var exe = AppActivityService.ExeNameForPath(v.TargetPath);
        if (exe.Length == 0) return false;
        try
        {
            // "点击前的前台窗口"只在两种情况下可信：
            //   ① WM_MOUSEACTIVATE 抓到的那个（最准，激活之前）；
            //   ② 我们的窗口本来就已经是活动窗口（比如刚点开一个文件夹、焦点还在条体上）
            //      —— 这时系统不会再发 MOUSEACTIVATE，退一步用轮询记下的"最近一个别人的
            //      前台窗口"。如果两条都不是（我们本来不是活动窗口却没收到消息），宁可不猜：
            //      猜错会把别的程序收起来（"显示桌面"后点图标结果被最小化）。
            var hint = _foregroundBeforeClick;
            if (hint == IntPtr.Zero && IsActive) hint = AppActivityService.LastOtherForeground;
            bool handled = AppActivityService.TryToggle(exe, hint, out bool minimized);
            if (handled)
            {
                // 这一下已经用掉了"点击前的前台窗口"。清掉它 —— 下一次点击如果我们的窗口
                // 还是活动窗口（例如刚点开一个文件夹），系统不会再发 WM_MOUSEACTIVATE，
                // 留着这个旧值会拿上一次的前台去判"要不要最小化"（会误最小化）。
                // 清掉后走兜底：轮询记下的"最近一个别人的前台窗口"，那个是持续的、不会过期太久。
                _foregroundBeforeClick = IntPtr.Zero;
                DzTrace.Log($"[Dock click] exe={exe} hint=0x{hint.ToInt64():X} → {(minimized ? "minimize" : "activate")}");
            }
            return handled;
        }
        catch { return false; }
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
        double TitleBarOpacity,
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
        bool TitleBarFillIndependent,
        bool EnableLiquidGlass);

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
            TitleBarOpacity:  _zone.TitleBarOpacity,
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
            TitleBarFillIndependent: _zone.TitleBarFillIndependent,
            EnableLiquidGlass: _zone.EnableLiquidGlass);

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
                TitleBarOpacity =  _zone.MergedGroupStyle.TitleBarOpacity,
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
            TitleBarOpacity =  _zone.MergedGroupStyle.TitleBarOpacity,
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
            // 保留原有填充语义上必须让主体填充独立裁剪到标题栏之下，否则统一标题栏
            // 会盖在保留的原填充上；不依赖配置里是否显式存了 TitleBarFillIndependent。
            TitleBarFillIndependent = true,
            // 玻璃属于统一填充表现的一部分 — 保留原有填充时关闭液态玻璃，只保留各子分区
            // 自己的纯填充，避免统一玻璃层盖在保留的原填充上。
            EnableLiquidGlass = false,
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
        ApplyAcrylic(s);

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
        // 快捷栏不做整窗圆角（胶囊的圆角由 WPF 自己画，见 ApplyDockWindowFrame）。
        if (PresentationSource.FromVisual(this) != null && !collapsed && !IsDock)
            NativeMethods.SetRoundedCorners(this, s.CornerRadius);

        // Body fill — 分区本体一体化:玻璃开且填充铺满整窗时,填充并入玻璃 tint;
        // 玻璃关(或收起)时保持纯填充照旧。标题栏独立填充(组合分区「保留原有填充」
        // 会强制开启)时填充必须留在独立的 WPF 层,才能被裁剪到标题栏之下 — 此时
        // 只把纯玻璃着色交给 DWM accent,填充仍走 FillRect。
        bool fillIndependent = s.TitleBarFillIndependent && !s.TileMode && !IsDock;
        bool glassCarriesFill = s.EnableLiquidGlass && (_hover?.IsExpanded ?? false) && !fillIndependent;
        // ponytail 2026-09-26: 非亚克力材质(毛玻璃/清透/液态/深色玻璃)的 DWM 背板不认着色
        // (实测 state3 完全不着色),合成着色改由填充层承担;亚克力系仍返回 null → 填充层保持
        // 透明,着色交给 DWM(与历史行为一致)。
        var wpfGlassTint = glassCarriesFill
            ? AcrylicHelper.ResolveWpfGlassTintBrush(_zone.GlassMaterial, s.FillColor, 1.0,
                _zone.GlassColorMode, _zone.GlassTintOpacity, _zone.GlassTintLuminosity)
            : null;
        try { FillRect.Fill = wpfGlassTint ?? (glassCarriesFill ? AcrylicHelper.HitTestFill : new SolidColorBrush((Color)ColorConverter.ConvertFromString(s.FillColor)!)); } catch { }
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

        // 快捷栏「磁贴模式」= 全透明（与律动条同一个语义）：只留图标本身，
        // 填充 / 边框 / 背景图 / 液态玻璃全部撤掉，条体看起来是"浮在壁纸上的一排图标"。
        // 放在填充与背景图之后覆盖它们（顺序即优先级）。
        if (IsDock && s.TileMode)
        {
            AcrylicHelper.DisableBlur(this);
            _wallpaperBackdrop?.SetVisible(false);
            FillRect.Fill = Brushes.Transparent;
            FillRect.Opacity = 1.0;
            BgImageBorder.Visibility = Visibility.Collapsed;
            ZoneBorder.BorderThickness = new Thickness(0);
        }

        // 快捷栏：填充 / 背景图 / 边框全部收进「可见胶囊」那一条带，窗口上方那条
        // 透明带专门给放大溢出用（图标可以长到胶囊外面而不会被窗口边界裁掉）。
        if (IsDock) ApplyDockBarVisuals(s);

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
        // ▦/≡ 的字色跟同一个「按钮颜色」，选中/未选中在 ApplyViewToggleGlyphColor 里分亮暗。
        _viewToggleBrush = btnBrush;
        ApplyViewToggleBackgrounds();

        // Control-point opacity + TileMode visibility
        // ponytail: 不透明度改成逐按钮下放(HoverHighlight.ApplyControlOpacity) —— 原来整排
        // ControlPoint 设 0.4 会把悬停高光一起压到 ≈7.5% 白, 与组件(18.8%)差 7 倍,
        // 表现为「分区的高光几乎看不见」。容器保持不透明, 只让按钮自己跟随 ControlOpacity；
        // 合并拖拽时的整排变淡(0.6)仍然由容器负责, 两者相乘不冲突。
        HoverHighlight.ApplyControlOpacity(s.ControlOpacity,
            new UIElement[] { GridToggleBtn, ListToggleBtn, LockBtn, ImportBtn, EditBtn, HideBtn, FolderMapUpBtn, FolderMapRefreshBtn, FolderMapCloseBtn });
        // 容器本身恢复全不透明：明暗已下放到按钮，容器只承担「合并拖拽时整排变淡 0.6」。
        ControlPoint.Opacity = 1.0;
        var vis = (s.TileMode || IsDock) ? Visibility.Collapsed : Visibility.Visible;
        TitleBarBg.Visibility = vis;
        ControlPoint.Visibility = vis;
        if (IsDock && SubZoneTabsRow != null) SubZoneTabsRow.Visibility = Visibility.Collapsed;
        // ponytail 2026-10: 标题栏透明度 = 整条标题栏(含拖拽区与右侧按钮)的 Opacity,
        // 与「按钮透明度」同一套语义 —— 0% = 整条淡出到底 (0)，而不是只把填充色变透明。
        // 之前这行滑块去改 TitleBarFillColor 的 alpha，调 0 后标题栏背景没了、按钮和
        // 命中区还在，所以"看起来没变透明"。上限 1、下限 0（真正的 0）。
        TitleBarBg.Opacity = Math.Clamp(s.TitleBarOpacity / 100.0, 0.0, 1.0);
        // 磁贴模式把右侧按钮排藏了 → 标题可用宽度变大，重新夹一次上限。
        UpdateTitleMaxWidth();
        _tileVisual = s.TileMode && !IsDock;

        // 磁贴模式 / 快捷栏 = 隐藏底部 8px 分割条。
        if (BottomBarBg != null)
            BottomBarBg.Visibility = (s.TileMode || IsDock) ? Visibility.Collapsed : Visibility.Visible;

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

    /// <summary>遍历 ItemsHost / ItemsListHost 内的容器，根据 hide 切换名称 TextBlock
    /// （x:Name="ItemNameText"）可见性。两个宿主各走一遍：模板里的 DataTrigger 只在
    /// DataContext 变化时求值，勾选/取消「隐藏应用名」是同实例改属性，必须显式刷
    /// （局部值优先级高于模板触发器，所以来回切都能覆盖）。容器未生成时无操作 —
    /// StatusChanged / SetGridView 会在容器就绪后补一次。</summary>
    void ApplyHideAppName(bool hide)
    {
        var target = hide ? Visibility.Collapsed : Visibility.Visible;
        ApplyHideAppNameTo(ItemsHost, target);
        ApplyHideAppNameTo(ItemsListHost, target);
    }

    void ApplyHideAppNameTo(ItemsControl? host, Visibility target)
    {
        if (host == null) return;
        for (int i = 0; i < host.Items.Count; i++)
        {
            if (host.ItemContainerGenerator.ContainerFromIndex(i) is not DependencyObject container) continue;
            var tb = FindVisualChild<TextBlock>(container, t => t.Name == "ItemNameText");
            if (tb != null) tb.Visibility = target;
        }
    }

    bool _customIconOpenFirst;

    /// <summary>自定义图标模式：ItemsHost 隐藏，整窗双击（Window_MouseDoubleClick）
    /// 打开 Items[0]。与列表视图互斥，两个宿主的可见性统一由
    /// <see cref="ApplyItemHostsVisibility"/> 算。</summary>
    void ApplyCustomIcon(bool on)
    {
        if (ItemsHost == null) return;
        _customIconOpenFirst = on;
        ApplyItemHostsVisibility();
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
        double clipTop = s.TitleBarFillIndependent && !s.TileMode && !IsDock ? TitleBarLayerHeight() : 0;
        BgImageBorder.Margin = new Thickness(0, clipTop, 0, 0);
        var bi = GetBackgroundImage(s.BgImagePath);
        if (bi != null)
        {
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
        else { BgImage.Source = null; BgImage.Opacity = 0; }
    }

    // 背景图解码缓存。ponytail 2026-09-26(审计修订): 原来每次 ApplyStyle 都
    // `new BitmapImage + UriSource + DecodePixelWidth=1920 + OnLoad` —— 那是**同步磁盘读取 +
    // 1920px 解码**,而 ApplyStyle 是每次面板编辑(拖滑块逐帧)都会跑的,而且 RefreshZone
    // 还会跑两遍。路径与文件时间戳没变时直接用上次的解码结果。
    string _bgImageCacheKey = "";
    System.Windows.Media.Imaging.BitmapSource? _bgImageCache;

    /// <summary>取(并缓存)背景图解码结果。路径为空/文件不存在/解码失败 → null。
    /// 缓存键含文件最后写入时间,替换成同名文件也能立刻生效。</summary>
    System.Windows.Media.Imaging.BitmapSource? GetBackgroundImage(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            _bgImageCacheKey = "";      // 顺手放掉上一张,别让"不再用背景图"的分区还占着一张位图
            _bgImageCache = null;
            return null;
        }
        DateTime stamp;
        try { stamp = File.GetLastWriteTimeUtc(path); } catch { stamp = default; }
        string key = path + "|" + stamp.Ticks;
        if (key == _bgImageCacheKey && _bgImageCache != null) return _bgImageCache;
        try
        {
            var bi = new System.Windows.Media.Imaging.BitmapImage();
            bi.BeginInit();
            bi.UriSource = new Uri(path);
            bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bi.DecodePixelWidth = 1920;
            bi.EndInit();
            bi.Freeze();
            _bgImageCacheKey = key;
            _bgImageCache = bi;
            return bi;
        }
        catch
        {
            _bgImageCacheKey = "";
            _bgImageCache = null;
            return null;
        }
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
        if (IsDock)
        {
            // 快捷栏：宽高是算出来的，不能反着写回模型（否则下一次算出来的尺寸会被它覆盖），
            // 只有外部（用户手动改图标大小/倒影开关）真的改了才走 ApplyDockGeometry。
            if (!_applyingDockGeometry)
            {
                _zone.Width = Width;
                _zone.Height = Height;
            }
            ScheduleSave();
            UpdateCanvasSize();
            RefreshDockReflection();
            RefreshDockStartReflection();
            RefreshDockGlass();          // 胶囊高度/位置变了 → 壁纸裁剪要重铺
            NativeMethods.UpdateRoundedCorners(this, (int)_zone.CornerRadius);
            return;
        }
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

        // 快捷栏：画布就是整窗（没有标题栏/底条可减），单行不折行。
        if (IsDock)
        {
            _itemCanvas.Width = Math.Max(0, Width > 0 ? Width : _zone.Width);
            _itemCanvas.Height = Math.Max(0, Height > 0 ? Height : _zone.Height);
            return;
        }

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

    // ── Acrylic / frosted glass ──    /// <summary>
    /// ponytail: ghost-glass fix — re-enable liquid glass when the zone expands from the
    /// RestoreButton (hover preview or click). Routes through ApplyAcrylic so the
    /// expanded-state gate stays the single source of truth for EnableBlur.
    /// </summary>
    void ReapplyAcrylic()
    {
        var s = ResolveStyle();
        if (IsDock)
        {
            // ponytail 2026-11-01「展开后一圈鬼影」修复 — 绝对不要给快捷栏套 Win11 的整窗圆角。
            //
            // 原来的写法对分区/快捷栏一视同仁地 SetRoundedCorners(radius)：分区的窗口 == 可见内容，
            // 圆角看不出来；快捷栏的窗口比可见胶囊大（上方放大带 + 下方倒影带 + 左右放大余量），
            // 于是 DWM 会给**整个窗口**描一圈 1px 边框 + 铺一层窗口背板，屏幕上就是胶囊外面多出
            // 一个透明的圆角面板（用户报的「从最小化展开后周围一圈鬼影，就是透明边框」）。
            // 实测：最小化状态下 DWMWA_WINDOW_CORNER_PREFERENCE=1(DONOTROUND) 干净；
            // 点恢复按钮展开后（走的就是这里）变成 2(ROUND)，窗口外多出 2642 个差异像素、
            // 差异图里能直接看到那个圆角面板；窗口 Hide/Show 或走 ShowZone（后面会 ApplyStyle）
            // 都不会出现 —— 因为 ApplyStyle 里的 ApplyDockWindowFrame 会把它关回去。
            ApplyDockWindowFrame(_zone.TileMode);
        }
        else
        {
            // ponytail 2026-08-28: 展开时把 Win11 圆角偏好一并恢复(收起时
            // OnHoverCollapsed 关掉了它),否则恢复按钮点开的分区会一直保持尖角。
            NativeMethods.SetRoundedCorners(this, (int)_zone.CornerRadius);
        }
        ApplyAcrylic(s);
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
        HideDockGlassWindow();      // 玻璃层是独立 HWND，跟不上收起动画 → 直接藏掉
    }

    // ── 自绘背板(方案①:壁纸采样 + 可变半径模糊) ──

    WallpaperBackdropLayer? _wallpaperBackdrop;

    /// <summary>材质 = 「自绘背板」通道时,把壁纸采样层插到填充层之下并关掉 DWM 玻璃。
    /// 返回 true = 背板已接管(调用方不要再走 DWM);壁纸不可用/材质=自定义/开关关闭 → false(回退 DWM)。</summary>
    bool TryApplyWallpaperBackdrop(ResolvedZoneStyle s)
    {
        // ponytail 2026-09-26(二期修订): 一旦不走自绘背板(开关关掉 / 材质=自定义 / 壁纸不可用),
        // **必须先把已经挂上的背板层藏起来**。它是一张**不透明**的壁纸裁剪图,留在可见状态就会
        // 整块盖住下面的 DWM 玻璃与它的着色 —— 用户从新方案切回 DWM 之后,改颜色/不透明度/亮度
        // 全都"没有反应"(实测反馈,查明就是这个原因:切换只调了 TryApply… 的返回值,没人去藏它)。
        // 收口在本方法里,调用点就不会漏。
        if (!AcrylicHelper.ResolveSelfDrawn())
        {
            _wallpaperBackdrop?.SetVisible(false);
            return false;
        }
        _wallpaperBackdrop ??= WallpaperBackdropLayer.TryCreate();
        if (_wallpaperBackdrop == null) return false;   // 从没建起来过 → 没有层需要藏

        _wallpaperBackdrop.Attach(MainContent, FillRect);
        _wallpaperBackdrop.SetVisible(true);
        _wallpaperBackdrop.BindWindow(this);
        _wallpaperBackdrop.SetAppearance(this,
            AcrylicHelper.ResolveSelfDrawnRadiusPx(_zone.GlassBlurAmount),
            AcrylicHelper.ResolveBackdropTint(s.FillColor, 1.0, _zone.GlassColorMode,
                _zone.GlassTintOpacity, _zone.GlassTintLuminosity),
            AcrylicHelper.ResolveNoiseOpacity(_zone.GlassMaterial));
        AcrylicHelper.DisableBlur(this); // 自绘背板与 DWM 背板不能叠加
        return true;
    }

    void ApplyAcrylic(ResolvedZoneStyle s)
    {
        // 快捷栏磁贴模式（全透明）：DWM 玻璃/背板都不能上 —— 窗口比可见胶囊高，
        // 整窗模糊会把上方那条透明带变成一块悬空毛玻璃。**但胶囊自己的玻璃照旧要铺**
        // （磁贴模式只撤填充/边框/背景图，不撤用户显式打开的液态玻璃），由 ApplyDockGlass 决定；
        // 以前这里直接把 FillRect 设成透明就返回了 —— 而"点恢复按钮展开"只走
        // ReapplyAcrylic（不会再走 ApplyStyle 里的 ApplyDockBarVisuals），于是玻璃被擦掉
        // 之后没人重画，用户看到的就是"从最小化展开后玻璃又没了"。
        if (IsDock && _zone.TileMode)
        {
            AcrylicHelper.DisableBlur(this);
            _wallpaperBackdrop?.SetVisible(false);
            FillRect.Opacity = 1.0;
            ApplyDockGlass(s);
            return;
        }

        // 快捷栏的窗口比可见胶囊高（上方是放大余量、下方是倒影带）时，同样不能上 DWM 模糊 ——
        // 那是整窗矩形的，两条带会变成悬空的毛玻璃。玻璃观感改由**胶囊区的壁纸采样背板**
        // 承担（ApplyDockGlass）：只铺胶囊、不碰两条带。
        if (IsDock && (ZoneLayout.DockOverflowTop(_zone) > 0 || ZoneLayout.DockReflectBand(_zone) > 0))
        {
            AcrylicHelper.DisableBlur(this);
            _wallpaperBackdrop?.SetVisible(false);
            FillRect.Opacity = 1.0;
            ApplyDockGlass(s);
            return;
        }
        // ponytail: ghost-glass fix — a collapsed zone keeps its full-size window (only the
        // RestoreButton is visible), so enabling acrylic here would tint the WHOLE window
        // bounds with a ghost glass rectangle. Only enable blur while the content is
        // expanded; whenever collapsed (or mid-collapse), disable it instead.
        bool expanded = _hover?.IsExpanded ?? false;
        // 标题栏独立填充(组合分区「保留原有填充」会强制开启)时,填充需要被裁剪到
        // 标题栏之下,DWM accent 无法按区域裁剪 — 所以只把纯玻璃着色并入 DWM,
        // 填充保持独立 FillRect 层(见 ApplyStyle 的 FillRect.Margin)。
        bool fillIndependent = s.TitleBarFillIndependent && !s.TileMode;
        if (s.EnableLiquidGlass && expanded)
        {
            // ponytail 2026-09-26(二期修订 2): 「保留原有填充」必须排在自绘背板**之前**判断。
            // 这种分区要求填充独立成层、并被裁到标题栏之下(FillRect.Margin),而自绘背板是
            // **整窗一层**、又会把填充并进自己的着色里 —— 天然做不到这种裁剪。所以这条路固定
            // 走 DWM(与一期一致),否则组合分区的「保留原有填充」会被背板吃掉。
            if (fillIndependent)
            {
                _wallpaperBackdrop?.SetVisible(false);   // 老背板让位,别盖住 DWM 玻璃
                var blurResult = AcrylicHelper.EnableBlur(this, _zone.GlassBlurAmount,
                    _zone.GlassTintOpacity, _zone.GlassTintLuminosity, _zone.GlassColorMode,
                    AcrylicHelper.ResolveAccentState(_zone.GlassMaterial),
                    AcrylicHelper.ResolveUseClassicBlur(_zone.GlassMaterial));
                if (!blurResult.Success)
                    System.Diagnostics.Debug.WriteLine($"[ZoneWindow] EnableBlur failed: {blurResult.Error}");
                try { FillRect.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(s.FillColor)!); } catch { }
                FillRect.Opacity = 1.0;
            }
            // ponytail 2026-09-26 方案①: 新方案 → 壁纸采样自绘背板(模糊半径真正生效)。
            // 背板层插在填充层之下、又在 MainContent 之内,所以收起/展开动画自动带着它走;
            // 开关关掉 / 壁纸读不到 → 返回 false(并让老背板让位),走下面的 DWM 老路径。
            else if (TryApplyWallpaperBackdrop(s))
            {
                // 着色已并入背板层 → 填充层保持透明(与 DWM 路径的"填充并入玻璃"同语义)
                FillRect.Fill = AcrylicHelper.HitTestFill;
                FillRect.Opacity = 1.0;
            }
            else
            {
                // ponytail 2026-08-30: 分区本体一体化 — 内部填充并入玻璃 tint(算一层),
                // FillRect 透明;填充色与玻璃配色作为两个输入本质上仍是两层。
                var blurResult = AcrylicHelper.EnableBlurComposite(this, _zone.GlassBlurAmount,
                    s.FillColor, 1.0, _zone.GlassColorMode, _zone.GlassTintOpacity, _zone.GlassTintLuminosity,
                    AcrylicHelper.ResolveAccentState(_zone.GlassMaterial),
                    AcrylicHelper.ResolveUseClassicBlur(_zone.GlassMaterial));
                if (!blurResult.Success)
                    System.Diagnostics.Debug.WriteLine($"[ZoneWindow] EnableBlur failed: {blurResult.Error}");
                FillRect.Fill = AcrylicHelper.HitTestFill;
                FillRect.Opacity = 1.0;
            }
            if (TitleBarBg != null && !string.IsNullOrEmpty(s.TitleBarFillColor))
                ApplyTitleBarBandFill(s.TitleBarFillColor);
        }
        else
        {
            AcrylicHelper.DisableBlur(this);
            _wallpaperBackdrop?.SetVisible(false);
            try
            {
                FillRect.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(s.FillColor)!);
                FillRect.Opacity = 1.0;
                ApplyTitleBarBandFill(s.TitleBarFillColor);
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
        // 锁定态可能被管理界面（行锁按钮走 UpdateZone，不触发 LockChanged）改掉 →
        // 这里按需补一次，保证 🔒 图标、右键菜单文字、桌面层归属都跟上。
        if (_appliedLock != zone.IsLocked) ApplyLockState();
        // 视图模式也从模型回读（应用预设/重新加载配置可能改了 IsGridView）。
        _isGridView = zone.IsGridView;
        SetRestoreIcon();
        // ponytail: ApplyStyle rebuilds sub-zone tabs internally with the resolved title
        // text color — no separate RebuildSubZoneTabs call needed here.
        ApplyStyle();
        UpdateMergedTitle();
        RefreshFolderMapping();
        if (zone.IsVisible) ShowZone(); else ApplyHidden();
        // 快捷栏：属性面板改「图标大小 / 倒影 / 动画」后，宽高与排列要跟着重算
        // （分区那条路靠 ApplyStyle + ShowZone 就够了，快捷栏还得重新排一行）。
        if (zone.DockMode) { ApplyDockMode(); }
        // ponytail: run last so it overrides ShowZone/ApplyHidden when HoverAutoExpand=true.
        // Otherwise the post-refresh Width/Height would restore the full-size window and
        // hide the RestoreButton the user is supposed to hover.
        _hover?.SetEnabled(zone.EnableRestoreButton);
    }

    // ── Inline title editing ──

    /// <summary>组合分区(组长窗口)的标题 = 组显示名 <c>MergedGroupMembership.DisplayName</c>,
    /// 不是主分区自己的 <c>Zone.Name</c> —— 子分区没有自己的窗口(ShowZone 会重定向到组长),
    /// 所以"组合分区的名字"在这条标题栏上改的就是 DisplayName。</summary>
    bool TitleIsMergedGroup => _zone.MergedGroupMembership.SubZoneIds.Count > 0;

    /// <summary>标题栏当前应该显示的名字(组合分区:DisplayName,空则回落主分区名)。</summary>
    string CurrentTitleText() => TitleIsMergedGroup
        ? (string.IsNullOrEmpty(_zone.MergedGroupMembership.DisplayName)
            ? _zone.Name : _zone.MergedGroupMembership.DisplayName)
        : _zone.Name;

    /// <summary>
    /// 标题栏内联改名的唯一提交口(Enter / 失焦 / 点标题栏 / 窗口失去激活 / 关窗 /
    /// 停手 700ms 都走这里)。改名走 <see cref="ZoneManager.RenameZone"/>:写模型 +
    /// 立即落盘 + ZonesChanged,设置界面实时跟上,不做"二次应用"。
    /// 提交后把显示文本还原为已保存的值(空名 / 未改动时同样回填,避免标题框停在
    /// 用户敲了一半的文本上)。
    /// </summary>
    void CommitTitleRename()
    {
        _titleCommitDebounce.Stop();
        var text = ZoneTitleText.Text?.Trim() ?? "";
        if (_mgr.RenameZone(_zone, text))
        {
            // 设置界面里显示这个名字的地方(停靠标签页 / 该目标的浮动编辑器标签页)跟着改。
            PropertyWindowManager.Instance.RefreshEditorTitle(
                TitleIsMergedGroup ? MergedGroupTarget.For(_zone) : _zone);
        }
        ZoneTitleText.Text = CurrentTitleText();
    }

    /// <summary>
    /// 停手 700ms 自动提交。
    ///
    /// ponytail 2026-09-26(外部端到端实测):只在 Enter / LostFocus 提交是不够的 ——
    /// 用户改完名字最自然的动作是"点别处",而在窗口内部点标题栏 / 图标区这类**不可聚焦**
    /// 的元素,WPF 根本不会把键盘焦点移出 TextBox,LostFocus 永远不触发:于是标题栏里
    /// 显示着新名字(用户以为改好了),模型 / 磁盘 / 设置界面全是旧名 ——
    /// 实机复现:UIA 写真名 + 点标题栏空白处 → 磁盘仍是旧名。
    /// 这里按"停手即提交"补齐:文本与已保存值不同才起表,提交后回填会让 TextChanged
    /// 再进一次本函数(那时文本 == 已保存值 → 不再起表),不会自激。
    /// </summary>
    void ZoneTitle_TextChanged(object s, TextChangedEventArgs e)
    {
        var text = ZoneTitleText.Text?.Trim() ?? "";
        if (text.Length == 0 || text == CurrentTitleText())
        {
            _titleCommitDebounce.Stop();
            return;
        }
        _titleCommitDebounce.Stop();
        _titleCommitDebounce.Start();
    }

    void ZoneTitle_PreviewMouseLeftButtonDown(object s, MouseButtonEventArgs e)
    {
        // Merged: the title text is the master label (above the sub-zone tabs), so a
        // click switches back to the master view.
        // ponytail 2026-09-26: 但只在「当前不在主分区视图」时拦截 —— 已经在主视图了,
        // 这一下点击就该进内联改名(组合分区的名字现在允许在标题栏直接改;旧实现
        // 无条件拦截 + TextBox 置只读,用户根本改不了,只能去设置界面改)。
        if (TitleIsMergedGroup && _vm.SelectedSubZoneId != _zone.Id)
        {
            SelectSubZone(_zone.Id);
            e.Handled = true;
        }
    }

    void ZoneTitle_LostFocus(object s, RoutedEventArgs e) => CommitTitleRename();

    void ZoneTitle_KeyDown(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitTitleRename();
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
        // ponytail 2026-09-26: 标题栏现在**始终可编辑**。组合分区改的是组显示名
        // (DisplayName),普通分区改的是 Zone.Name —— 都由 CommitTitleRename →
        // ZoneManager.RenameZone 统一处理(写模型 + 立即落盘 + ZonesChanged),
        // 所以不再需要"组合分区标题置只读、只能去设置界面改"的老办法。
        ZoneTitleText.IsReadOnly = false;
        ZoneTitleText.Cursor = Cursors.IBeam;
        // 非组合分区不动文本:调用方(RefreshZone / OnZonesChanged)已经写过 _zone.Name。
        if (TitleIsMergedGroup) ZoneTitleText.Text = CurrentTitleText();
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

    // ═══════════════════════════════════════════════════════════════════════
    //  快捷栏（Dock）形态
    //
    //  形态判据只有一个：Zone.DockMode。窗口本身仍是一个 ZoneWindow —— 图标、拖放、
    //  右键菜单、液态玻璃、预设卡、桌面层全部复用分区那套；这里只加三样东西：
    //  ① 单行布局 + 尺寸由内容决定（窗口 = 条体）
    //  ② 悬停放大 + 相邻推让
    //  ③ 条体内的倒影
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>切到快捷栏形态：没有标题栏/按钮/缩放柄/底条/页面映射，图标换成 Dock 模板。</summary>
    void ApplyDockMode()
    {
        TitleBarBg.Visibility = Visibility.Collapsed;
        ControlPoint.Visibility = Visibility.Collapsed;
        if (BottomBarBg != null) BottomBarBg.Visibility = Visibility.Collapsed;
        if (SubZoneTabsRow != null) SubZoneTabsRow.Visibility = Visibility.Collapsed;
        if (FolderMappingView != null) FolderMappingView.Visibility = Visibility.Collapsed;
        GripTL.Visibility = GripTR.Visibility = GripBL.Visibility = GripBR.Visibility = Visibility.Collapsed;

        // 快捷栏的尺寸完全由内容决定（图标数 × 格），没有可拉的地方；而且
        // ResizeMode=CanResize 会让窗口带上 WS_THICKFRAME —— Win11 会沿着这条
        // 不可见边框描一圈 1px 亮线（磁贴模式下那圈"还在的边框"就是它 + DWM 边框）。
        ResizeMode = ResizeMode.NoResize;

        // 快捷栏专用图标模板（图标+可选名称、ToolTip 显示名字；单击打开在 Item_MouseUp 里判）。
        // 走选择器而不是单一模板：次级分区（SubFolder）在快捷栏里同样是合法条目，
        // 要和分区里的 2×2 缩略图外观一致（DockItemSelector 见 ZoneWindow.xaml）。
        ItemsHost.ItemTemplate = null;
        ItemsHost.ItemTemplateSelector = (DataTemplateSelector)Resources["DockItemSelector"];
        _customIconOpenFirst = false;
        ApplyItemHostsVisibility();

        // 胶囊的壁纸采样玻璃要跟着窗口位置走（窗口一移动，壁纸裁剪就得重算）。
        LocationChanged -= OnDockLocationChanged;
        LocationChanged += OnDockLocationChanged;

        ApplyDockGeometry();
    }

    /// <summary>快捷栏被拖动 / 被程序挪位后重铺胶囊玻璃（位置没变时 ApplyDockGlass 自己会跳过）。</summary>
    void OnDockLocationChanged(object? s, EventArgs e) => RefreshDockGlass();

    /// <summary>按模型重算宽高 + 单行排列 + 重画倒影。图标增删、换序、改图标大小、
    /// 开关倒影之后都走这里（ZoneManager.RecomputeDockGeometry / 属性面板调用）。</summary>
    public void ApplyDockGeometry()
    {
        if (!IsDock) return;
        ZoneLayout.ApplyDockSize(_zone);
        double w = Math.Max(40, _zone.Width);
        double h = Math.Max(40, _zone.Height);
        _applyingDockGeometry = true;
        try
        {
            if (Math.Abs(Width - w) > 0.5) Width = w;
            if (Math.Abs(Height - h) > 0.5) ApplyProgrammaticHeight(h);
            ResetDockRowTransforms();
            ArrangeDockItems();
            UpdateCanvasSize();
            RefreshDockReflection();
            ApplyItemTextColor();
            ApplyHideAppName(_zone.HideAppName);
            UpdateDockChrome();
            EnsureDockRunPolling();
            RefreshDockRunBars(animate: false);
            RefreshDockGlass();
            ApplyDockWindowFrame(_zone.TileMode);
            RefreshDockStartHook();
        }
        finally { _applyingDockGeometry = false; }
        ScheduleSave();
        NativeMethods.UpdateRoundedCorners(this, (int)_zone.CornerRadius);
    }

    /// <summary>
    /// 快捷栏的胶囊外观：填充 / 背景图 / 边框都收进「可见胶囊」那一条带。
    ///
    /// 为什么不像分区那样整窗画：窗口比胶囊高（上方那条透明带是给悬停放大溢出的），
    /// 整窗画的话胶囊会跟着变高、图标看起来又"偏下"了 —— 上一轮就是这么绕回去的。
    /// 磁贴模式（全透明）时三样全部撤掉，只剩图标。
    /// </summary>
    void ApplyDockBarVisuals(ResolvedZoneStyle s)
    {
        if (FillRect == null) return;
        // 胶囊几何一律走 DockPill*Px：**先落到整像素，再按左/上 + 显式宽高摆放**。
        //
        // 两件事都不能用"四边 Margin"那条老写法：
        //   ① 上边距 × DPI 常常带小数（用户的快捷栏 11 DIP × 1.25 = 13.75px），不取整的话
        //      玻璃层（HWND，只能整数坐标）跟描边差 0.25px；
        //   ② 窗口自己也会被取整 —— 131 DIP × 1.25 = 163.75px，HWND 是 164px，
        //      内容层于是有 131.2 DIP，"右/下边距"按模型 131 算就短 0.25px。
        // 所以：左/上边距按整像素换算，宽高**直接给死**（不依赖父容器实际尺寸），
        // 四个边就都落在同一组整数设备像素上，跟玻璃窗逐个像素对齐。
        var dpi = VisualTreeHelper.GetDpi(this);
        double sx = dpi.DpiScaleX, sy = dpi.DpiScaleY;
        int lPx = DockPillLeftPx(), tPx = DockPillTopPx();
        int wPx = DockPillWidthPx(), hPx = DockPillHeightPx();
        double leftDip = lPx / sx, topDip = tPx / sy;
        double widthDip = wPx / sx, heightDip = hPx / sy;
        bool clear = _zone.TileMode;
        // 可见圆角：走 DWM 玻璃窗时压到系统给窗口切的圆角（见 DockVisibleCornerRadius），
        // 描边和玻璃层必须是同一个半径，否则四个角上两层对不齐。
        double rVisible = DockVisibleCornerRadius(s);

        FillRect.Margin = new Thickness(leftDip, topDip, 0, 0);
        FillRect.HorizontalAlignment = HorizontalAlignment.Left;
        FillRect.VerticalAlignment = VerticalAlignment.Top;
        FillRect.Width = widthDip;
        FillRect.Height = heightDip;
        FillRect.RadiusX = FillRect.RadiusY = rVisible;

        // 胶囊玻璃图层也只用胶囊这一块：位置/尺寸与 FillRect 完全一致，圆角用 Clip 切
        // （图层内部是三块无圆角的矩形）。半径同样用 rVisible：万一 DWM 玻璃窗上不去、
        // 退到这条自绘通道，胶囊的圆角也得跟描边一致，否则角上又会露出一个壁纸楔子。
        if (DockGlassHost != null)
        {
            DockGlassHost.Margin = new Thickness(leftDip, topDip, 0, 0);
            DockGlassHost.HorizontalAlignment = HorizontalAlignment.Left;
            DockGlassHost.VerticalAlignment = VerticalAlignment.Top;
            DockGlassHost.Width = widthDip;
            DockGlassHost.Height = heightDip;
            double r = Math.Max(0, rVisible);
            DockGlassHost.Clip = new RectangleGeometry(new Rect(0, 0, widthDip, heightDip), r, r);
        }
        BgImageBorder.Margin = new Thickness(leftDip, topDip, 0, 0);
        BgImageBorder.HorizontalAlignment = HorizontalAlignment.Left;
        BgImageBorder.VerticalAlignment = VerticalAlignment.Top;
        BgImageBorder.Width = widthDip;
        BgImageBorder.Height = heightDip;
        BgImageBorder.Visibility = clear ? Visibility.Collapsed : Visibility.Visible;

        // 描边单独一条：往里收半个线宽，免得被窗口/胶囊边缘切掉一半。
        if (DockBarStroke != null)
        {
            double bt = clear ? 0 : Math.Max(0, s.BorderThickness);
            DockBarStroke.StrokeThickness = bt;
            DockBarStroke.RadiusX = DockBarStroke.RadiusY = rVisible;
            // **这条必须关掉整像素布局对齐**（Window 上是 `UseLayoutRounding=True`）：
            // 描边要往里收半个线宽，`left + 0.5` 在 125% 下是 15.625px，布局对齐会把它圆成
            // 16px —— 外沿就变成"左边缩进 0.375px、右边探出 0.625px"（上下同理），四个角
            // 左右/上下不对称。关掉之后 15.625 − 0.625 = 15px，正好压在玻璃的边上。
            DockBarStroke.UseLayoutRounding = false;
            DockBarStroke.SnapsToDevicePixels = false;
            try
            {
                DockBarStroke.Stroke = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString(s.BorderColor)!);
            }
            catch { }
            double half = bt / 2;
            DockBarStroke.Margin = new Thickness(leftDip + half, topDip + half, 0, 0);
            DockBarStroke.HorizontalAlignment = HorizontalAlignment.Left;
            DockBarStroke.VerticalAlignment = VerticalAlignment.Top;
            DockBarStroke.Width = Math.Max(0, widthDip - bt);
            DockBarStroke.Height = Math.Max(0, heightDip - bt);
            DockBarStroke.Visibility = bt > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // 整窗那条边框在快捷栏里不用（会画到透明带上）。
        ZoneBorder.BorderThickness = new Thickness(0);

        // 磁贴模式（全透明）：整窗都不该有 Windows 给窗口本身画的 1px 边框/圆角
        // （窗口比胶囊高，描在窗口四周的那条线会浮在胶囊上方/下方）。
        ApplyDockWindowFrame(clear);

        // 胶囊的玻璃放在最后：它要读胶囊矩形（上面刚更新过的边距/半径）。
        ApplyDockGlass(s);
    }

    // ── 快捷栏的液态玻璃（胶囊区壁纸采样） ─────────────────────────────────

    /// <summary>上一次铺玻璃用的胶囊屏幕矩形（物理像素）。只用于状态摘录/日志。</summary>
    Rect _dockGlassRect = Rect.Empty;

    /// <summary>
    /// 快捷栏胶囊的液态玻璃。
    ///
    /// **为什么不能直接上 DWM**：窗口比胶囊高（上方放大余量 + 下方倒影带），而 DWM 的
    /// 模糊/亚克力**只能整窗**。第 7 轮用 `--dock-lab-case=dwmscope` 实测过：把分区那套
    /// `EnableBlurComposite` 原样打上来，整窗 353×131 变成一块毛玻璃，两条带一起被糊，
    /// 而 `SetWindowRgn` 对 `AllowsTransparency` 的分层窗口无效（裁不动）。所以只要窗口
    /// 比胶囊大，胶囊的玻璃只能**自绘**：壁纸按胶囊屏幕矩形裁出来，铺在 DockGlassHost 里。
    /// 窗口正好等于胶囊时（动效关 + 倒影关）才把 DWM 交给窗口本身。
    ///
    /// **载体**（第 7 轮 A）：自绘通道用与分区/时钟/日历/便签/面板**同一个**
    /// <see cref="WallpaperBackdropLayer"/>，只把它挂进胶囊那一块 `DockGlassHost`
    /// （`SetSourceInset` 让壁纸按胶囊的屏幕矩形裁）。换成图层带来两个实在的差别：
    ///   ① 认 `WallpaperBackdrop.Generation` —— 换壁纸 / 点「重新采样」/ 显示器拓扑变化后
    ///      自己重裁，不必等到窗口被拖动（老写法是一次性 `CreateStaticBrush` + "位置没变
    ///      就 return"，玻璃会一直用旧图）；
    ///   ② 窗口移动只改 ImageBrush.Viewbox（老写法每动一下都重建一张裁剪位图）。
    ///
    /// **跟设置走**（第 6 轮用户要求）：配方全部走分区那套解析器 ——
    /// ① 渲染方案开关 <see cref="AcrylicHelper.ResolveSelfDrawn"/>：关掉时材质不参与
    ///    （<see cref="AcrylicHelper.EffectiveMaterial"/> 恒 null → 无噪点、亚克力默认档），
    ///    与分区的 DWM 方案同一个观感配方；打开时按材质（AcrylicThin 等）出噪点。
    /// ② 模糊半径仍是 `GlassBlurAmount` 像素、着色仍是「填充 over 玻璃着色」的合成色。
    /// ③ **磁贴模式不再吞掉玻璃**：磁贴模式只撤填充/边框/背景图（外观），用户显式打开的
    ///    液态玻璃照旧铺 —— 老写法里 `s.TileMode` 直接 return，而用户那条快捷栏恰好是
    ///    "磁贴 + 玻璃"，于是玻璃永远是空的（这就是"玻璃失效"的直接原因）。
    /// </summary>
    void ApplyDockGlass(ResolvedZoneStyle s)
    {
        if (FillRect == null || DockGlassHost == null) return;
#if DEBUG
        DzTrace.Log($"[Dock glass] enter glass={s.EnableLiquidGlass} tile={s.TileMode} " +
                    $"selfDrawn={AcrylicHelper.ResolveSelfDrawn()} enabled={WallpaperSource.Enabled} " +
                    $"equalsPill={DockWindowEqualsPill()} mode={_dockGlassMode}");
#endif

        // 玻璃关掉：磁贴模式什么都不铺（全透明），否则铺纯填充色；采样层/玻璃窗一并藏掉。
        if (!s.EnableLiquidGlass)
        {
            HideDockGlassLayer();
            HideDockGlassWindow();
            if (s.TileMode) FillRect.Fill = Brushes.Transparent;
            else
            {
                try { FillRect.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(s.FillColor)!); }
                catch { }
            }
            FillRect.Opacity = 1.0;
            _dockGlassMode = "off";
            _dockGlassRect = Rect.Empty;
            return;
        }

        // 渲染方案 = DWM 时走**真**玻璃（实景模糊，动态壁纸也跟着动）：
        //   · 窗口 == 胶囊（动效/倒影都关）→ 直接把 DWM 交给窗口本身，不必多一个窗口；
        //   · 否则 → 第 7 轮 B：胶囊大小的独立玻璃窗口垫在下面（见 DockGlassWindow）。
        if (!AcrylicHelper.ResolveSelfDrawn())
        {
            if (DockWindowEqualsPill())
            {
                var r = AcrylicHelper.EnableBlurComposite(this, _zone.GlassBlurAmount,
                    s.FillColor, 1.0, _zone.GlassColorMode, _zone.GlassTintOpacity, _zone.GlassTintLuminosity,
                    AcrylicHelper.ResolveAccentState(_zone.GlassMaterial),
                    AcrylicHelper.ResolveUseClassicBlur(_zone.GlassMaterial));
                if (r.Success)
                {
                    HideDockGlassWindow();
                    HideDockGlassLayer();
                    FillRect.Fill = AcrylicHelper.HitTestFill;
                    FillRect.Opacity = 1.0;
                    _dockGlassMode = "dwm(window==pill)";
                    _dockGlassRect = Rect.Empty;
                    DzTrace.Log("[Dock glass] scheme=dwm window==pill blur ok");
                    return;
                }
                DzTrace.Log($"[Dock glass] scheme=dwm EnableBlurComposite failed: {r.Error} → 采样兜底");
            }
            else if (TryApplyDwmGlassWindow(s)) return;
        }
        HideDockGlassWindow();

        // ── 采样通道（第 7 轮 A：与分区/时钟/日历/便签/面板**同一个** WallpaperBackdropLayer）
        //    以前这里是 CreateStaticBrush 的一次性静态画刷，而且"位置没变就 return"，
        //    于是换壁纸 / 点「重新采样」之后玻璃一直用旧图。现在换成图层：
        //    · 认 WallpaperBackdrop.Generation → 换壁纸后自己重裁（不必等窗口移动）；
        //    · 窗口移动只改 Viewbox（不重建画刷、不重解码）；
        //    · 只铺胶囊那块（SetSourceInset 让壁纸按胶囊屏幕矩形裁），两条透明带完全不参与。
        AcrylicHelper.DisableBlur(this);

        var pill = DockPillScreenPx();
        if (pill.IsEmpty) return;      // 窗口还没接到 PresentationSource → 等 OnLoad/LocationChanged 再补

        var tint = AcrylicHelper.ResolveBackdropTint(s.FillColor, 1.0, _zone.GlassColorMode,
            _zone.GlassTintOpacity, _zone.GlassTintLuminosity);
        // 噪点/材质同样受渲染方案开关约束（DWM 方案下 EffectiveMaterial=null → 0）。
        var noise = AcrylicHelper.ResolveNoiseOpacity(_zone.GlassMaterial);

        var layer = EnsureDockGlassLayer();
        if (layer == null)
        {
            // 壁纸读不到（纯色壁纸 / 抓不到）→ 至少把玻璃着色铺上，别让胶囊变透明。
            var solid = new SolidColorBrush(tint);
            solid.Freeze();
            FillRect.Fill = solid;
            FillRect.Opacity = 1.0;
            _dockGlassMode = "tint-only";
            _dockGlassRect = pill;
            DzTrace.Log("[Dock glass] 壁纸不可用 → 纯着色");
            return;
        }

        layer.SetSourceInset(DockPillInsetPx());          // 壁纸只裁胶囊那一块
        layer.Update(this, AcrylicHelper.ResolveSelfDrawnRadiusPx(_zone.GlassBlurAmount), tint, noise);
        layer.SetVisible(true);
        DockGlassHost.Visibility = Visibility.Visible;
        FillRect.Fill = AcrylicHelper.HitTestFill;        // 命中留着（拖拽/右键都靠它）
        FillRect.Opacity = 1.0;
        _dockGlassMode = "layer";
        _dockGlassRect = pill;
        DzTrace.Log($"[Dock glass] layer rect={pill.X:F0},{pill.Y:F0} {pill.Width:F0}x{pill.Height:F0} " +
                    $"r={_zone.GlassBlurAmount} noise={noise:F2} " +
                    $"scheme={(AcrylicHelper.ResolveSelfDrawn() ? "self-drawn" : "dwm→sample")} " +
                    $"host={DockGlassHost.ActualWidth:F0}x{DockGlassHost.ActualHeight:F0}");
    }

    // ── 胶囊玻璃图层（与分区同一套 WallpaperBackdropLayer，只铺胶囊） ──

    WallpaperBackdropLayer? _dockGlassLayer;
    string _dockGlassMode = "off";

    // ── B：DWM 真玻璃用的独立「玻璃层」窗口（正好胶囊大小、非分层、可被区域裁圆角） ──

    DockGlassWindow? _dockGlassWin;

    /// <summary>玻璃层圆角的**物理像素**半径（与 WPF 胶囊那边的 `CornerRadius × DPI` 同一口径）。
    /// 层级被顶掉后重摆时要复用它，保证形状和内容层一模一样。</summary>
    int _dockGlassRadiusPx = 12;

    /// <summary>已经排了一次层级修正（防抖，避免一帧里排好几次）。</summary>
    bool _dockGlassZPending;

    /// <summary>
    /// 快捷栏胶囊的**可见圆角**（DIP）—— 描边、填充、玻璃层都用这一个值。
    ///
    /// 为什么不是直接用 `CornerRadius`：走 DWM 玻璃（独立 HWND 那一层）时，玻璃层的圆角
    /// 由 Windows 定死（<see cref="AcrylicHelper.DwmWindowCornerRadiusDip"/> ≈ 8 DIP），
    /// 而且 `SetWindowRgn` 对它完全无效（实测：把玻璃窗区域设成半窗矩形，画面一个像素都不变）。
    /// 如果描边还按用户设的 12 画，四个角上玻璃就从描边的弧**外面**露出一小条 ——
    /// 用户报的「玻璃层和内容层没有完美契合」里，圆角这一半就是这个。
    ///
    /// 剩下两个通道不受这个限制，用户设多少就是多少：
    ///   · 自绘背板（渲染方案 = 壁纸采样）—— 玻璃是 WPF 图层，任意半径；
    ///   · 玻璃关闭 / 整窗就是胶囊 —— 可见形状由 WPF 自己画。
    /// </summary>
    double DockVisibleCornerRadius(ResolvedZoneStyle s)
    {
        if (!IsDock || !_zone.EnableLiquidGlass) return s.CornerRadius;
        if (AcrylicHelper.ResolveSelfDrawn()) return s.CornerRadius;
        if (DockWindowEqualsPill()) return s.CornerRadius;
        return Math.Min(s.CornerRadius, AcrylicHelper.DwmWindowCornerRadiusDip);
    }

    /// <summary>DWM 方案下的胶囊玻璃：把玻璃单独做成一个窗口垫在内容窗口正下方。
    /// 成功返回 true（此时内容层的 FillRect 只留一个 HitTestFill 抢命中）。</summary>
    bool TryApplyDwmGlassWindow(ResolvedZoneStyle s)
    {
        try
        {
            return TryApplyDwmGlassWindowCore(s);
        }
        catch (Exception ex)
        {
            DzTrace.Log($"[Dock glass] glasswin EXC {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    bool TryApplyDwmGlassWindowCore(ResolvedZoneStyle s)
    {
        var pill = DockPillScreenPx();
        if (pill.IsEmpty) { DzTrace.Log("[Dock glass] glasswin: pill empty → skip"); return false; }
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) { DzTrace.Log("[Dock glass] glasswin: no hwnd → skip"); return false; }
        if (!DockGlassWindowShouldShow())
        {
            DzTrace.Log($"[Dock glass] glasswin: shouldShow=false (vis={IsVisible} content={MainContent.Visibility} " +
                        $"btn={RestoreButton.Visibility} expanded={_hover?.IsExpanded}) → skip");
            HideDockGlassWindow();
            return false;
        }

        var win = _dockGlassWin ??= new DockGlassWindow();
        win.ShowNoActivate();                       // 先确保句柄
        if (win.Handle == IntPtr.Zero)
        {
            DzTrace.Log("[Dock glass] glasswin: CreateWindowEx 失败 → skip");
            return false;
        }

        // 配方必须和分区**逐项一致**（这是用户说的"调成和分区一样的"）：
        //   accentState 走同一套材质解析器。
        //
        // **经典 blurbehind 固定关掉**（`useClassicBlur: false`）：这个玻璃层是**非分层**
        // 裸 HWND，`DwmEnableBlurBehindWindow` 在这里不是"模糊"，而是给整窗加一层灰黑
        // （实测同一块壁纸：开着 Δluma≈−24、蓝通道 −43，看起来就是"比分区深一截"）；
        // 分组那种**分层** WPF 窗口上它反而是空操作 —— 同一套配方两条通道观感不同，
        // 根因就在这。分区那边保留材质解析器给的取值，不动。
        var r = win.ApplyGlass(_zone.GlassBlurAmount, s.FillColor, 1.0, _zone.GlassColorMode,
            _zone.GlassTintOpacity, _zone.GlassTintLuminosity,
            AcrylicHelper.ResolveAccentState(_zone.GlassMaterial),
            useClassicBlur: false);
        if (!r.Success)
        {
            DzTrace.Log($"[Dock glass] DWM 玻璃窗失败: {r.Error} → 采样兜底");
            HideDockGlassWindow();
            return false;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        // 顺序要紧：ShowWindow 会把窗口提到最前 → 位置/层级必须放在**最后**一步，
        // 否则玻璃层会盖在图标上面（用户反馈"有时候玻璃层还会盖住内容"）。
        // 圆角半径存一份：之后层级被别人顶掉时（见 OnDockWindowPosChanged）还要按
        // 同一个半径重新摆一次，不能跟 WPF 胶囊那边算出两个半径来。
        //
        // **玻璃窗矩形 = 胶囊矩形，一个像素都不收**（第九轮曾经按 (CornerRadius−8)×0.3
        // 往里收过 2px，结果两侧多出一条壁纸缝，用户看到的就是"WPF 层比玻璃大一圈"）。
        // 圆角不需要收：可见圆角已经在 DockVisibleCornerRadius 里压到玻璃层同一个半径，
        // 描边的弧和玻璃的弧重合，贴着放反而严丝合缝。
        _dockGlassRadiusPx = (int)Math.Round(Math.Max(0, DockVisibleCornerRadius(s)) * dpi.DpiScaleX);
        bool rounded = _dockGlassRadiusPx > 0;
        win.SetCornerRounding(rounded);
        win.SyncTo(hwnd,
            (int)Math.Round(pill.X), (int)Math.Round(pill.Y),
            (int)Math.Round(pill.Width), (int)Math.Round(pill.Height),
            _dockGlassRadiusPx);
        win.ShowNoActivate();
        win.SyncTo(hwnd,
            (int)Math.Round(pill.X), (int)Math.Round(pill.Y),
            (int)Math.Round(pill.Width), (int)Math.Round(pill.Height),
            _dockGlassRadiusPx);

        AcrylicHelper.DisableBlur(this);            // 内容窗口自己不能再上 accent
        HideDockGlassLayer();
        FillRect.Fill = AcrylicHelper.HitTestFill;  // 命中仍归内容层（alpha=1，肉眼不可见）
        FillRect.Opacity = 1.0;
        _dockGlassMode = "dwm-window";
        _dockGlassRect = pill;
        DzTrace.Log($"[Dock glass] dwm-window pill={pill.X:F0},{pill.Y:F0} {pill.Width:F0}x{pill.Height:F0} " +
                    $"r={_zone.GlassBlurAmount}");
        return true;
    }

    /// <summary>玻璃层该不该露出来：内容层可见、没被最小化成 36×36、悬停展开着。
    /// 收起/最小化时内容层在缩放动画里，玻璃层是独立 HWND 跟不上动画 —— 直接藏掉，
    /// 展开完成（`_hover.Expanded → ReapplyAcrylic`）时会重新摆一遍。</summary>
    bool DockGlassWindowShouldShow()
        => IsVisible
           && MainContent.Visibility == Visibility.Visible
           && RestoreButton.Visibility != Visibility.Visible
           && (_hover?.IsExpanded ?? true);

    void HideDockGlassWindow()
    {
        _dockGlassWin?.HideGlass();
        if (_dockGlassMode == "dwm-window") _dockGlassMode = "none";
    }

    /// <summary>只重排位置/层级（窗口移动、贴桌面层之后调用）。</summary>
    void SyncDockGlassWindow()
    {
        if (_dockGlassWin == null || !_dockGlassWin.IsVisibleGlass) return;
        try { ApplyDockGlass(ResolveStyle()); } catch { }
    }

    // ── 玻璃层的层级守卫（第 7 轮补充 2）──────────────────────────────────

    /// <summary>
    /// 内容层的**位置/层级变了之后**，把玻璃层重新压回它正下方。
    ///
    /// 背景（用户报的「DWM 玻璃明显有两层，点一下边缘下面那层就盖到上面」）：
    /// 玻璃层是独立 HWND，系统不会把它跟内容层当成一个整体。内容层被
    /// `PinBelowProgman`（失焦 / 点桌面 / 启动程序抢前台）压到桌面层最底部、
    /// 或被 `BringToFront` 提到别处时，玻璃层留在原地 —— 于是它从「内容层下面」
    /// 翻到「上面」，糊住图标；再点一下又翻回来。所以每次 `WM_WINDOWPOSCHANGED`
    /// 都检查一下，破了就在下一轮消息里修回去（不能在本条消息里直接 SetWindowPos，
    /// 那会在系统处理 z 序的过程中重新排 z 序）。
    /// </summary>
    void OnDockWindowPosChanged()
    {
        if (!IsDock) return;
        if (_dockGlassWin is not { } gw || !gw.IsVisibleGlass) return;
        var content = new WindowInteropHelper(this).Handle;
        if (content == IntPtr.Zero) return;
        if (gw.IsBelow(content)) return;        // 还在下面 → 拖拽/移动时这条几乎零成本
        if (_dockGlassZPending) return;
        _dockGlassZPending = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, new Action(() =>
        {
            _dockGlassZPending = false;
            try { RestoreDockGlassZOrder(); } catch { }
        }));
    }

    /// <summary>把玻璃层压回内容层下面（只动 z 序和胶囊矩形，不重上玻璃配方）。</summary>
    void RestoreDockGlassZOrder()
    {
        if (_dockGlassMode != "dwm-window") return;
        if (_dockGlassWin is not { } gw || !gw.IsVisibleGlass) return;
        var content = new WindowInteropHelper(this).Handle;
        if (content == IntPtr.Zero) return;
        if (gw.IsBelow(content)) return;
        var pill = DockPillScreenPx();
        if (pill.IsEmpty) return;
        bool ok = gw.SyncTo(content,
            (int)Math.Round(pill.X), (int)Math.Round(pill.Y),
            (int)Math.Round(pill.Width), (int)Math.Round(pill.Height),
            _dockGlassRadiusPx);
        DzTrace.Log($"[Dock glass] zorder 修正: ok={ok} pill={pill.X:F0},{pill.Y:F0} " +
                    $"{pill.Width:F0}x{pill.Height:F0} | {DockGlassWindow.LastError}");
        if (!ok) _dockGlassMode = "none";   // 兜底：这一帧先别露玻璃，下一次 ApplyDockGlass 再来
    }

    void CloseDockGlassWindow()
    {
        try { _dockGlassWin?.Dispose(); } catch { }
        _dockGlassWin = null;
    }

    /// <summary>懒创建胶囊玻璃图层。壁纸不可用 → null（调用方退成纯着色）。</summary>
    WallpaperBackdropLayer? EnsureDockGlassLayer()
    {
        if (DockGlassHost == null) return null;
        if (_dockGlassLayer != null)
        {
            _dockGlassLayer.AttachTo(DockGlassHost);      // 幂等
            return _dockGlassLayer;
        }
        var layer = WallpaperBackdropLayer.TryCreate();
        if (layer == null) return null;
        layer.AttachTo(DockGlassHost);
        _dockGlassLayer = layer;
        return layer;
    }

    void HideDockGlassLayer()
    {
        _dockGlassLayer?.SetVisible(false);
        if (DockGlassHost != null) DockGlassHost.Visibility = Visibility.Collapsed;
    }

    // ── 胶囊几何：**先在设备像素里取整，再换回 DIP** ────────────────────────
    //
    // 快捷栏的可见胶囊有两个"画家"：内容层（WPF，能把描边画在半像素上）和玻璃层
    // （裸 HWND，坐标只能是整数）。只要有一边落在 .25/.5 上，两层就差一个亚像素：
    // 描边那侧看不出来，玻璃那侧就变成"多吃 0.25px"或"少 0.25px"（一条发丝缝）。
    //
    // 实测（125% 缩放，用户那条「快捷栏 1」）：`DockBarTop = 11 DIP × 1.25 = 13.75px`、
    // 胶囊宽 `181 × 1.25 = 226.25px`。玻璃窗按 round 摆到 14 / 226，描边还画在 13.75 / 226.25
    // 上 → 玻璃"上边多出 0.25px、下边少 0.25px"；再叠上后面那条布局取整（见
    // ApplyDockBarVisuals 里描边那段的注释），两个弧的圆心就斜了，两条弧在**左下**交叉 ——
    // 用户报的"左下角还稍微有点没对齐"。
    //
    // 解决办法：四条边先用 round() 落到整数像素，再把整数换算成 DIP 交给 WPF
    // （整数 ÷ 1.25 × 1.25 正好还是那个整数），玻璃层直接用同一组整数 —— 两层重合。

    /// <summary>胶囊左边距（**整设备像素**，相对内容层左上角）。</summary>
    int DockPillLeftPx() => (int)Math.Round(ZoneLayout.DockSideOverflow(_zone) * VisualTreeHelper.GetDpi(this).DpiScaleX);

    /// <summary>胶囊上边距（**整设备像素**，相对内容层左上角）。</summary>
    int DockPillTopPx() => (int)Math.Round(ZoneLayout.DockBarTop(_zone) * VisualTreeHelper.GetDpi(this).DpiScaleY);

    /// <summary>胶囊宽度（**整设备像素**）。</summary>
    int DockPillWidthPx() => Math.Max(1,
        (int)Math.Round(ZoneLayout.DockPillWidth(_zone, _zone.Items.Count) * VisualTreeHelper.GetDpi(this).DpiScaleX));

    /// <summary>胶囊高度（**整设备像素**）。</summary>
    int DockPillHeightPx() => Math.Max(1,
        (int)Math.Round(ZoneLayout.DockBarHeight(_zone) * VisualTreeHelper.GetDpi(this).DpiScaleY));

    /// <summary>胶囊在**窗口内**的物理像素矩形（= 壁纸裁剪源偏移）。
    /// 存窗口内偏移而不是屏幕矩形：窗口一移动，图层自己按新位置重裁。</summary>
    Rect DockPillInsetPx()
        => new(DockPillLeftPx(), DockPillTopPx(), DockPillWidthPx(), DockPillHeightPx());


    /// <summary>窗口矩形是否正好等于可见胶囊（此时 DWM 整窗模糊 == 胶囊模糊，可以用 DWM）。</summary>
    bool DockWindowEqualsPill()
        => ZoneLayout.DockSideOverflow(_zone) <= 0
           && ZoneLayout.DockOverflowTop(_zone) <= 0
           && ZoneLayout.DockReflectBand(_zone) <= 0;

    /// <summary>胶囊在屏幕上的**物理像素**矩形（玻璃层窗口 / 壁纸裁剪用）。
    /// 四条边都是整数（见上面那段注释），窗口没就位时返回 Empty。</summary>
    Rect DockPillScreenPx()
    {
        try
        {
            if (PresentationSource.FromVisual(this) == null) return Rect.Empty;
            int w = DockPillWidthPx(), h = DockPillHeightPx();
            if (w < 2 || h < 2) return Rect.Empty;
            var origin = PointToScreen(new Point(0, 0));       // 内容层左上角（设备像素）
            return new Rect(Math.Round(origin.X) + DockPillLeftPx(),
                            Math.Round(origin.Y) + DockPillTopPx(), w, h);
        }
        catch { return Rect.Empty; }
    }

    /// <summary>窗口移动/尺寸变化后重铺玻璃（胶囊的屏幕矩形变了，壁纸裁剪要跟着走）。</summary>
    void RefreshDockGlass()
    {
        if (!IsDock) return;
        try { ApplyDockGlass(ResolveStyle()); } catch { }
    }

    /// <summary>
    /// 快捷栏的窗口外框：一律不要。
    ///
    /// 逐像素查过：磁贴模式下 WPF 一个字都没画（除图标外全透明），屏幕上却仍有
    /// 一圈 1px 亮线 → 那是 Windows 给顶层窗口描的边框（DWM 的
    /// visibleBorderThickness=1）。窗口比可见胶囊高（上方放大余量 + 下方倒影带），
    /// 这条边框就露在胶囊外面，看起来像"胶囊上方多了一条横线""磁贴模式边框还在"。
    /// 去掉办法：① DWMWA_BORDER_COLOR = DWMWA_COLOR_NONE（Win11 21H2+ 支持）；
    /// ② 窗口不做 DWM 圆角 —— 胶囊的圆角由 WPF 自己画，整窗圆角只会切到倒影；
    /// ③ 顺带把窗口设成不可调整大小（见 ApplyDockMode），去掉 WS_THICKFRAME 那条
    ///    不可见边框带。
    /// </summary>
    void ApplyDockWindowFrame(bool tileMode)
    {
        NativeMethods.RemoveWindowBorder(this);
        if (tileMode || ZoneLayout.DockOverflowTop(_zone) > 0 || ZoneLayout.DockBarTop(_zone) > 0
            || ZoneLayout.DockReflectBand(_zone) > 0)
            NativeMethods.DisableRoundedCorners(this);
    }

    /// <summary>单行排列：Y 固定在「图标带顶边」（= 条体里垂直居中），X 按序号递推；
    /// 顺带把格子/图标尺寸同步到 VM（改「图标大小 / 显示名称」后不用重建容器）。</summary>
    void ArrangeDockItems()
    {
        if (!IsDock) return;
        double y = ZoneLayout.DockBandTop(_zone);
        double cell = _zone.GridSize;
        double icon = ZoneLayout.DockIconSize(_zone);
        for (int i = 0; i < _vm.Items.Count; i++)
        {
            var vm = _vm.Items[i];
            vm.ItemSize = cell;
            vm.ItemHeight = cell;
            vm.IconSize = icon;
            vm.X = ZoneLayout.DockX(_zone, i);
            vm.Y = y;
        }
    }

    // ── 倒影（每个图标自己在格子里挂一份镜像） ──────────────────────────────

    /// <summary>倒影节点的标记 —— 重画时按这个 Tag 把上一批摘掉。</summary>
    const string DockReflectTag = "dock-reflect";

    /// <summary>
    /// 倒影：每个图标下方挂一份「垂直翻转 + 压扁 + 向下渐隐」的镜像。
    ///
    /// ① 挂在图标容器里（而不是一条独立图层）：镜像要跟着悬停放大一起动 ——
    ///    容器的 Scale/Translate 会自动带着它走，不用第二套动画。
    /// ② 位置按**图标本体的实际矩形**算（TransformToAncestor 取真实坐标），不再靠
    ///    "图标在格子里居中"的假设：显示名称时图标会被名字顶上去，凭假设推会隔一截空白。
    /// ③ 次级分区是自绘的 2×2 方框（SubfolderItemView，不是 Image），早先那段
    ///    `vm.Icon is ImageSource` 直接把它跳过了 —— 现在给它**拍一张快照**再镜像
    ///    （为什么不直接用 VisualBrush：实测那样一个像素都画不出来，见下面的注释）。
    /// </summary>
    void RefreshDockReflection() => RefreshDockReflection(0);

    /// <param name="retry">内部用：容器/布局还没就绪时延后重试的次数（最多 5 次，防止死循环）。</param>
    void RefreshDockReflection(int retry)
    {
        if (!IsDock || ItemsHost == null) return;
        double rh = ZoneLayout.DockReflectHeight(_zone);
        bool on = _zone.DockReflection && rh > 2;
        double alpha = Math.Clamp(_zone.DockReflectionOpacity, 0, 100) / 100.0;
        double boxSize = ZoneLayout.DockIconSize(_zone);
        bool notReady = false;

        for (int i = 0; i < ItemsHost.Items.Count; i++)
        {
            if (ItemsHost.ItemContainerGenerator.ContainerFromIndex(i) is not DependencyObject container)
            { ReflNote($"{i}:noContainer"); notReady = true; continue; }
            var root = FindVisualChild<Grid>(container, g => g.Name == "DockItemRoot");
            if (root == null) { ReflNote($"{i}:noRoot"); notReady = true; continue; }

            // 先摘掉上一批镜像（图标/尺寸/开关一改就整批重来）
            for (int c = root.Children.Count - 1; c >= 0; c--)
                if (root.Children[c] is FrameworkElement old && (old.Tag as string) == DockReflectTag)
                    root.Children.RemoveAt(c);
            if (!on) continue;

            // 取图标本体。普通图标就是 `ItemIcon`；**次级分区取不到这个名字** —— 它的
            // SubfolderItemView 自己的 XAML 里 `x:Name="Root"` 会把这个实例的 Name 先占掉，
            // 模板上的 x:Name="ItemIcon" 只进了名字域、没改 Name 属性（实测 kids=…#Root）。
            // 所以按类型兜底找那颗自绘控件，而不是只认名字。
            var iconEl = FindVisualChild<FrameworkElement>(container, e => e.Name == "ItemIcon")
                         ?? FindVisualChild<FrameworkElement>(container, e => e is SubfolderItemView);
            if (iconEl == null)
            {
                var kids = new List<string>();
                foreach (var ch in root.Children)
                    kids.Add($"{ch.GetType().Name}#{(ch as FrameworkElement)?.Name}");
                ReflNote($"{i}:noIcon kids={string.Join(",", kids)}");
                notReady = true;
                continue;
            }
            if (iconEl.ActualWidth < 2 || iconEl.ActualHeight < 2)
            { ReflNote($"{i}:{iconEl.GetType().Name} size={iconEl.ActualWidth:F0}x{iconEl.ActualHeight:F0}"); notReady = true; continue; }

            Rect iconRect;
            try
            {
                iconRect = iconEl.TransformToAncestor(root)
                    .TransformBounds(new Rect(0, 0, iconEl.ActualWidth, iconEl.ActualHeight));
            }
            catch { notReady = true; continue; }   // 还没接进可视树

            // 镜像源有两条路：
            //  ① 普通图标就是一张图 → 直接复用那张位图（清晰、零开销）；
            //  ② 次级分区的 2×2 方框是**自绘控件**（不是 Image），只能拍快照。
            //     早先这里用 VisualBrush 把控件"照"过去 —— 实测一个像素都画不出来
            //     （Mirror 节点存在、屏幕上什么都没有），换 VisualBrush 的几种
            //     Viewbox/Viewport 组合也没救；RenderTargetBitmap 快照是确定性的。
            ImageSource? src;
            double boxW, boxH, glyphBottomLocal;   // glyphBottom 在镜像框内的局部 y
            if (iconEl is System.Windows.Controls.Image img && img.Source is ImageSource bmp)
            {
                var (_, gBottom) = GlyphBoundsOf(bmp);
                src = bmp;
                boxW = iconRect.Width;
                boxH = iconRect.Height;
                glyphBottomLocal = gBottom * iconRect.Height;
            }
            else
            {
                // 次级分区：只要控件第 0 行那块方框（方框边长 = 图标边长），名字行不进倒影。
                double box = Math.Min(Math.Min(iconRect.Width, iconRect.Height), boxSize);
                var snap = SnapshotVisual(iconEl, new Rect(0, 0, iconEl.ActualWidth, box));
                if (snap == null) { notReady = true; continue; }
                src = snap;
                boxW = box;
                boxH = box;
                glyphBottomLocal = box;             // 方框画满整块 → 底边就是字形底
                iconRect = new Rect(iconRect.Left, iconRect.Top, box, box);
            }

            var layer = BuildDockMirror(src, boxW, boxH, glyphBottomLocal, rh, alpha);
            layer.Margin = new Thickness(iconRect.Left, iconRect.Top + glyphBottomLocal, 0, 0);
            root.Children.Add(layer);
            ReflNote($"{i}:{iconEl.GetType().Name} box={boxW:F0}x{boxH:F0} OK");
        }

        // 容器/布局/图标都还没就绪（首帧常见）→ 等布局过一轮再来一次。
        // 不重试就永远没有倒影：ApplyDockGeometry 发生在容器生成之前。
        if (notReady && retry < 5)
        {
            Dispatcher.BeginInvoke(new Action(() => RefreshDockReflection(retry + 1)),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    /// <summary>
    /// 造一份倒影层（外部拿走后自己设 Margin 丢进容器）。
    ///
    /// 几何：把图片按「左上角为轴、垂直翻转 squash 倍」再往下平移 visible，
    /// 然后把这个框的顶边放在「镜中字形顶正好贴住原字形底边」的位置
    /// （原字形底边在框内的局部 y = glyphBottom → 框顶 = squash×glyphBottom − visible）。
    /// 外层 Canvas 承担渐隐遮罩 —— 遮罩要画在**视觉坐标**里（内层已经翻转过了，
    /// 直接罩在翻转元素上会把渐隐画反：亮的一端跑到最下面）。
    /// 用 Canvas 而不是 Grid 装镜像：Grid 会按格子给子元素"可用高度"，
    /// 42 高的镜像放进 23 高的格子会被夹扁（实测只剩顶上几像素、看着像没画）。
    /// </summary>
    static Canvas BuildDockMirror(ImageSource src, double w, double h,
                                  double glyphBottom, double rh, double alpha)
    {
        double squash = Math.Min(1.0, rh / Math.Max(1, h));
        double visible = h * squash;
        var mi = new System.Windows.Controls.Image
        {
            Source = src,
            Width = w,
            Height = h,
            Stretch = Stretch.Fill,
            IsHitTestVisible = false,
            RenderTransformOrigin = new Point(0, 0),
        };
        RenderOptions.SetBitmapScalingMode(mi, BitmapScalingMode.HighQuality);
        var tg = new TransformGroup();
        tg.Children.Add(new ScaleTransform(1, -squash));      // 以左上角为轴翻转
        tg.Children.Add(new TranslateTransform(0, visible));  // 再压回倒影带顶部
        mi.RenderTransform = tg;

        var layer = new Canvas
        {
            Width = w,
            Height = rh,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Opacity = alpha,
            IsHitTestVisible = false,
            Tag = DockReflectTag,
            OpacityMask = BuildDockReflectMask(),
        };
        Canvas.SetLeft(mi, 0);
        Canvas.SetTop(mi, squash * glyphBottom - visible);
        layer.Children.Add(mi);
        return layer;
    }

    /// <summary>同上的通用版：任意元素（比如用图元重画的开始徽标）当镜像内容。</summary>
    static Canvas BuildDockMirror(FrameworkElement content, double w, double h,
                                  double glyphBottom, double rh, double alpha)
    {
        double squash = Math.Min(1.0, rh / Math.Max(1, h));
        double visible = h * squash;
        content.IsHitTestVisible = false;
        content.RenderTransformOrigin = new Point(0, 0);
        var tg = new TransformGroup();
        tg.Children.Add(new ScaleTransform(1, -squash));      // 以左上角为轴翻转
        tg.Children.Add(new TranslateTransform(0, visible));  // 再压回倒影带顶部
        content.RenderTransform = tg;

        var layer = new Canvas
        {
            Width = w,
            Height = rh,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Opacity = alpha,
            IsHitTestVisible = false,
            Tag = DockReflectTag,
            OpacityMask = BuildDockReflectMask(),
        };
        Canvas.SetLeft(content, 0);
        Canvas.SetTop(content, squash * glyphBottom - visible);
        layer.Children.Add(content);
        return layer;
    }

    /// <summary>开始徽标的颜色：Windows 经典蓝（和 XAML 里 DockStartGlyph 同一色）。</summary>
    static readonly SolidColorBrush DockStartBlueBrush = MakeFrozen(Color.FromRgb(0x00, 0x78, 0xD4));

    static SolidColorBrush MakeFrozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    /// <summary>
    /// 用图元重画一份「四格开始徽标」—— 和 XAML 里 DockStartGlyph 的 Canvas 同一套常量
    /// （16 单位方格、四块 7.5、缝隙 1、圆角 0.6），只是边长按 <paramref name="side"/> 缩放。
    ///
    /// 倒影为什么不用快照：字形是 Viewbox 里的一个小 Canvas，`RenderTargetBitmap.Render`
    /// 会带上它在按钮里的**布局偏移** —— 实测拍出来只剩右下角一块（倒影变成一个小方块）。
    /// 直接按几何重画是确定性的，而且任意尺寸都清晰。
    /// </summary>
    static Canvas MakeStartGlyphCanvas(double side, Brush fill)
    {
        var c = new Canvas { Width = side, Height = side };
        double u = side / 16.0;
        foreach (var (lx, ly) in new[] { (0.0, 0.0), (8.5, 0.0), (0.0, 8.5), (8.5, 8.5) })
        {
            var r = new System.Windows.Shapes.Rectangle
            {
                Width = 7.5 * u,
                Height = 7.5 * u,
                RadiusX = 0.6 * u,
                RadiusY = 0.6 * u,
                Fill = fill,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(r, lx * u);
            Canvas.SetTop(r, ly * u);
            c.Children.Add(r);
        }
        return c;
    }

    /// <summary>
    /// 把一个可视元素当前的外观拍成位图（可只取其中一块矩形），给倒影当镜像源。
    ///
    /// 为什么不用 VisualBrush：实测把它罩在 Rectangle.Fill 上一个像素都没渲染出来
    /// （节点在、屏幕上空的，用户看到的就是"次级分区没有倒影"），换 Viewbox/Viewport
    /// 组合也无解。RTB 快照在离屏渲染和真机上表现一致，是确定性的。
    /// 2× 像素密度：倒影本身会被压扁/缩小，留一倍余量不会糊。
    /// </summary>
    static BitmapSource? SnapshotVisual(FrameworkElement el, Rect region)
    {
        try
        {
            double w = el.ActualWidth, h = el.ActualHeight;
            if (w < 2 || h < 2) { _snapDiag = $"{el.GetType().Name} size={w:F0}x{h:F0}"; return null; }
            const double scale = 2.0;
            int pw = Math.Max(1, (int)Math.Ceiling(w * scale));
            int ph = Math.Max(1, (int)Math.Ceiling(h * scale));
            var rtb = new RenderTargetBitmap(pw, ph, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            rtb.Render(el);
            rtb.Freeze();

            int x = (int)Math.Round(region.X * scale);
            int y = (int)Math.Round(region.Y * scale);
            int rw = (int)Math.Round(region.Width * scale);
            int rh2 = (int)Math.Round(region.Height * scale);
            x = Math.Clamp(x, 0, Math.Max(0, pw - 1));
            y = Math.Clamp(y, 0, Math.Max(0, ph - 1));
            rw = Math.Clamp(rw, 1, pw - x);
            rh2 = Math.Clamp(rh2, 1, ph - y);
            var crop = new CroppedBitmap(rtb, new Int32Rect(x, y, rw, rh2));
            crop.Freeze();
            _snapDiag = $"{el.GetType().Name} size={w:F0}x{h:F0} → {pw}x{ph} crop={rw}x{rh2}";
            return crop;
        }
        catch (Exception ex) { _snapDiag = $"{el.GetType().Name} EXC {ex.GetType().Name}: {ex.Message}"; return null; }
    }

    /// <summary>最近几次快照的结果（自测用；只保留最后 8 条）。</summary>
    static readonly List<string> _snapLog = new();
    static string _snapDiag
    {
        get => _snapLog.Count == 0 ? "" : _snapLog[^1];
        set { _snapLog.Add(value); if (_snapLog.Count > 8) _snapLog.RemoveAt(0); }
    }

    /// <summary>倒影重画的过程日志（自测用；只保留最后 14 条）。</summary>
    static readonly List<string> _reflLog = new();
    static void ReflNote(string s)
    {
        _reflLog.Add(s);
        if (_reflLog.Count > 14) _reflLog.RemoveAt(0);
    }

    /// <summary>倒影的渐隐遮罩：贴条体那头亮、往下淡出（1 → 0.47 → 0）。</summary>
    static LinearGradientBrush BuildDockReflectMask()
    {
        var b = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
            MappingMode = BrushMappingMode.RelativeToBoundingBox,
        };
        b.GradientStops.Add(new GradientStop(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), 0.0));
        b.GradientStops.Add(new GradientStop(Color.FromArgb(0x78, 0xFF, 0xFF, 0xFF), 0.55));
        b.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1.0));
        return b;
    }

    // 图标位图「真正有像素」的上下边界（0–1），按 ImageSource 缓存。
    static readonly Dictionary<System.Windows.Media.ImageSource, (double Top, double Bottom)> _glyphBounds = new();

    /// <summary>取图标位图的 alpha 上下边界（归一化 0–1）。取不到就按整张图算。
    /// 阈值 96（＜阈值算空白）：Shell 图标底部那一圈很淡的投影要忽略掉，否则镜像
    /// 会从投影下沿开始、看起来离图标本体还差一截（用户反馈"文件夹的倒影有点远"）。</summary>
    static (double Top, double Bottom) GlyphBoundsOf(System.Windows.Media.ImageSource src)
    {
        if (_glyphBounds.TryGetValue(src, out var cached)) return cached;
        var result = (Top: 0.0, Bottom: 1.0);
        try
        {
            if (src is BitmapSource bs && bs.PixelWidth > 0 && bs.PixelHeight > 0)
            {
                var conv = bs.Format == PixelFormats.Bgra32
                    ? bs
                    : new FormatConvertedBitmap(bs, PixelFormats.Bgra32, null, 0);
                int w = conv.PixelWidth, h = conv.PixelHeight;
                int stride = w * 4;
                var buf = new byte[stride * h];
                conv.CopyPixels(buf, stride, 0);
                const int AlphaMin = 96;
                int first = -1, last = -1;
                for (int y = 0; y < h; y++)
                {
                    bool any = false;
                    int row = y * stride;
                    for (int x = 0; x < w; x++)
                    {
                        if (buf[row + x * 4 + 3] > AlphaMin) { any = true; break; }
                    }
                    if (any) { if (first < 0) first = y; last = y; }
                }
                if (first >= 0)
                    result = ((double)first / h, (double)(last + 1) / h);
            }
        }
        catch { }
        if (_glyphBounds.Count > 512) _glyphBounds.Clear();   // 尺寸/图标变形会造新 key，别无限长
        _glyphBounds[src] = result;
        return result;
    }

    // ── 开始菜单按钮 + 分隔线 ─────────────────────────────────────────────

    /// <summary>把「开始按钮 + 分隔竖线」摆到几何算出来的位置上（关掉开关就整层收起）。</summary>
    void UpdateDockChrome()
    {
        if (!IsDock || DockChromeLayer == null) return;
        bool on = _zone.DockStartButton;
        DockChromeLayer.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (!on) return;

        double cell = _zone.GridSize;
        double bandTop = ZoneLayout.DockBandTop(_zone);
        double barTop = ZoneLayout.DockBarTop(_zone);
        double barH = ZoneLayout.DockBarHeight(_zone);

        DockStartBtn.Width = cell;
        DockStartBtn.Height = cell;
        DockStartBtn.CornerRadius = new CornerRadius(Math.Min(10, Math.Round(cell * 0.2)));
        // 四格标按原生占比取格边长的 46%（Win11 任务栏那颗开始按钮里标就这么大）。
        if (DockStartGlyph != null)
        {
            double g = Math.Round(cell * 0.46);
            DockStartGlyph.Width = g;
            DockStartGlyph.Height = g;
        }
        Canvas.SetLeft(DockStartBtn, ZoneLayout.DockStartX(_zone));
        Canvas.SetTop(DockStartBtn, bandTop);

        double sepH = Math.Round(cell * 0.62);
        DockStartSep.Height = sepH;
        Canvas.SetLeft(DockStartSep, ZoneLayout.DockStartSepX(_zone));
        Canvas.SetTop(DockStartSep, barTop + (barH - sepH) / 2);

        // 开始按钮也要有倒影（用户反馈：图标都有，就它没有）
        RefreshDockStartReflection();
    }

    // ── 开始按钮的倒影 ────────────────────────────────────────────────────

    /// <summary>开始按钮倒影的重试计数（布局/快照没好时会延后重来）。</summary>
    int _dockStartReflRetry;

    void RefreshDockStartReflection() { _dockStartReflRetry = 0; RefreshDockStartReflection(0); }

    /// <param name="retry">内部用：字形还没排好版（拍出来是空图）时延后重试，最多 5 次。</param>
    void RefreshDockStartReflection(int retry)
    {
        if (!IsDock || DockChromeLayer == null || DockStartBtn == null) return;

        // 先摘掉上一批（开关/尺寸/强度一改就整批重来）
        for (int i = DockChromeLayer.Children.Count - 1; i >= 0; i--)
            if (DockChromeLayer.Children[i] is FrameworkElement old && (old.Tag as string) == DockReflectTag)
                DockChromeLayer.Children.RemoveAt(i);

        double rh = ZoneLayout.DockReflectHeight(_zone);
        if (!_zone.DockStartButton || !_zone.DockReflection || rh <= 2 || DockStartGlyph == null) return;

        double cell = _zone.GridSize;
        double g = DockStartGlyph.ActualWidth;
        if (g < 2 || DockStartGlyph.ActualHeight < 2)
        {
            // 还没量出来就先不画（字形大小 = 格边长的 46%，量出来之前不知道画多大）。
            if (retry < 5) ScheduleStartReflection(retry);
            return;
        }

        // 位置按**字形**算，不是按钮框：按钮框是悬停热区（一格见方），字形只有它的 46%。
        double bandTop = ZoneLayout.DockBandTop(_zone);
        double left = ZoneLayout.DockStartX(_zone) + (cell - g) / 2;
        double bottom = bandTop + (cell - g) / 2 + g;
        double rhStart = Math.Min(rh, Math.Round(g * 0.55));
        double alpha = Math.Clamp(_zone.DockReflectionOpacity, 0, 100) / 100.0;

        var layer = BuildDockMirror(MakeStartGlyphCanvas(g, DockStartBlueBrush), g, g, g, rhStart, alpha);
        layer.Margin = new Thickness(left, bottom, 0, 0);
        DockChromeLayer.Children.Add(layer);
    }

    void ScheduleStartReflection(int retry)
        => Dispatcher.BeginInvoke(new Action(() => RefreshDockStartReflection(retry + 1)),
            System.Windows.Threading.DispatcherPriority.Loaded);

    void DockStart_Enter(object s, MouseEventArgs e) => DockStartBtn.Background = ItemHoverBrush;

    void DockStart_Leave(object s, MouseEventArgs e) => DockStartBtn.Background = Brushes.Transparent;

    /// <summary>点开始按钮（钩子路径和 WPF 点击路径共用；<see cref="DockStartButtonHook.TryConsume"/>
    /// 保证同一次物理点击只触发一次，见那边的说明）。</summary>
    void DockStartClick()
    {
        bool pass = DockStartButtonHook.TryConsume();
        DzTrace.Log($"[Dock start] click pass={pass}");
        if (!pass) return;
        AppActivityService.OpenStartMenu();
    }

    /// <summary>登记/撤销「开始按钮」的全局钩子兜底（只在开按钮 + 窗口可见时登记）。
    /// 屏幕矩形由 DockStartBtn 自己算，钩子回调里只做比较，不碰可视树。</summary>
    void RefreshDockStartHook()
    {
        if (!IsDock) return;
        IntPtr hwnd;
        try { hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle; }
        catch { return; }
        if (hwnd == IntPtr.Zero) return;

        if (!_zone.DockStartButton)
        {
            DockStartButtonHook.Unregister(hwnd);
            return;
        }
        DockStartButtonHook.Register(hwnd, DockStartScreenRect, DockStartClick);
    }

    /// <summary>开始按钮当前的屏幕矩形（不可见时返回 <see cref="Rect.Empty"/>）。</summary>
    Rect DockStartScreenRect()
    {
        if (!IsDock || !_zone.DockStartButton) return Rect.Empty;
        if (!IsVisible || MainContent.Visibility != Visibility.Visible) return Rect.Empty;
        if (DockStartBtn == null || DockStartBtn.ActualWidth < 2 || DockStartBtn.ActualHeight < 2) return Rect.Empty;
        try
        {
            var tl = DockStartBtn.PointToScreen(new Point(0, 0));
            var br = DockStartBtn.PointToScreen(new Point(DockStartBtn.ActualWidth, DockStartBtn.ActualHeight));
            return new Rect(tl, br);
        }
        catch { return Rect.Empty; }
    }

    // ── 运行状态指示（Windows 任务栏那套：后台灰短线 / 前台蓝长线） ──────────

    System.Windows.Threading.DispatcherTimer? _dockRunTimer;

    static readonly Color DockRunIdleColor = Color.FromArgb(0x8C, 0xC9, 0xCE, 0xD6);
    Color? _dockRunActiveColor;

    /// <summary>蓝线取「位置指示蓝条」同一个颜色（分区拖动时的插入指示用 Brush.Accent）。</summary>
    Color DockRunActiveColor()
    {
        if (_dockRunActiveColor.HasValue) return _dockRunActiveColor.Value;
        var c = Color.FromRgb(0x3B, 0x82, 0xF6);
        try
        {
            if (TryFindResource("Brush.Accent") is SolidColorBrush sb) c = sb.Color;
        }
        catch { }
        _dockRunActiveColor = c;
        return c;
    }

    /// <summary>按开关起停轮询：只有「快捷栏 + 指示开」才轮询，关掉不花任何代价。</summary>
    void EnsureDockRunPolling()
    {
        if (!IsDock) return;
        if (!_zone.DockRunIndicator)
        {
            _dockRunTimer?.Stop();
            foreach (var vm in _vm.Items) vm.RunState = DockRunState.None;
            RefreshDockRunBars(animate: false);
            return;
        }
        if (_dockRunTimer == null)
        {
            _dockRunTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            _dockRunTimer.Tick += (_, _) => RefreshDockRunStates();
        }
        if (!_dockRunTimer.IsEnabled) _dockRunTimer.Start();
        // 属性面板拖滑块时 ApplyDockGeometry 会连发；枚举窗口别每帧都跑一遍，
        // 400ms 内只让定时器去刷。
        if ((DateTime.UtcNow - _dockRunLastPoll).TotalMilliseconds > 400) RefreshDockRunStates();
    }

    DateTime _dockRunLastPoll = DateTime.MinValue;

    /// <summary>抓一次「谁在跑 / 谁在前台 / 哪些文件夹开着」，写到条目 VM 上，再刷短线。</summary>
    void RefreshDockRunStates()
    {
        if (!IsDock || !_zone.DockRunIndicator) return;
        _dockRunLastPoll = DateTime.UtcNow;
        System.Collections.Generic.Dictionary<string, AppActivityService.RunningApp> snap;
        try { snap = AppActivityService.Snapshot(); }
        catch { return; }

        IntPtr fg = ForegroundRoot();
        foreach (var vm in _vm.Items) vm.RunState = ResolveDockRunState(vm, snap, fg);
        RefreshDockRunBars(animate: _zone.DockRunAnim);
    }

    /// <summary>前台窗口的顶层句柄（子系统窗口常常是子窗口在前台）。</summary>
    static IntPtr ForegroundRoot()
    {
        try
        {
            var fg = NativeMethods.GetForegroundWindow();
            return fg == IntPtr.Zero ? IntPtr.Zero : NativeMethods.GetAncestor(fg, NativeMethods.GA_ROOT);
        }
        catch { return IntPtr.Zero; }
    }

    /// <summary>
    /// 一个条目现在该亮什么线。三类来源：
    ///  ① 普通应用 → exe 有可见窗口（前台蓝 / 后台灰）；
    ///  ② 文件夹（含 .zip）快捷方式 → 资源管理器正开着那个文件夹（或它的子目录）；
    ///  ③ 次级分区 → 它自己的浮层正开着。
    /// </summary>
    DockRunState ResolveDockRunState(ZoneItemViewModel vm,
        System.Collections.Generic.Dictionary<string, AppActivityService.RunningApp> snap, IntPtr fg)
    {
        // ③ 次级分区：浮层开着 = "这一个正在被展开"（用户反馈：点开文件夹下面什么都没有）
        if (vm.Type == ItemType.SubFolder)
        {
            var sub = ResolveSourceZoneItem(vm);
            if (sub != null && SubfolderFlyoutPopup.IsOpen && !_flyoutClosing
                && SubfolderFlyoutView.ViewModel?.HostSubItem.Id == sub.Id)
                return DockRunState.Foreground;
            return DockRunState.None;
        }

        // ② 文件夹 / 压缩包：资源管理器在浏览它（含子目录）
        if (AppActivityService.IsBrowsableFolder(vm.TargetPath))
        {
            var hwnd = AppActivityService.FolderOpenWindow(vm.TargetPath);
            if (hwnd == IntPtr.Zero) return DockRunState.None;
            return hwnd == fg ? DockRunState.Foreground : DockRunState.Background;
        }
        if (vm.Type is ItemType.Folder or ItemType.ShellLocation) return DockRunState.None;

        // ① 普通应用
        var exe = AppActivityService.ExeNameForPath(vm.TargetPath);
        if (exe.Length > 0 && snap.TryGetValue(exe, out var app))
            return app.IsForeground ? DockRunState.Foreground : DockRunState.Background;
        return DockRunState.None;
    }

    /// <summary>把每条短线的宽度/颜色推到当前状态（动画开则过渡，关则瞬切）。</summary>
    void RefreshDockRunBars(bool animate)
    {
        if (!IsDock || ItemsHost == null) return;
        double cell = _zone.GridSize;
        double idleW = Math.Max(6, Math.Round(cell * 0.18));
        double activeW = Math.Max(idleW + 4, Math.Round(cell * 0.34));
        double durMs = Math.Clamp(_zone.DockAnimMs, 80, 300) + 40;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var accent = DockRunActiveColor();

        for (int i = 0; i < ItemsHost.Items.Count; i++)
        {
            if (ItemsHost.ItemContainerGenerator.ContainerFromIndex(i) is not DependencyObject container) continue;
            var bar = FindVisualChild<Border>(container, b => b.Name == "RunBar");
            if (bar == null) continue;
            var vm = ItemsHost.Items[i] as ZoneItemViewModel;
            var state = vm?.RunState ?? DockRunState.None;
            double targetW = state switch
            {
                DockRunState.Foreground => activeW,
                DockRunState.Background => idleW,
                _ => 0,
            };
            var targetColor = state == DockRunState.Foreground ? accent : DockRunIdleColor;
            var brush = EnsureRunBarBrush(bar);

            if (animate)
            {
                bar.BeginAnimation(FrameworkElement.WidthProperty,
                    new DoubleAnimation(targetW, TimeSpan.FromMilliseconds(durMs)) { EasingFunction = ease });
                brush.BeginAnimation(SolidColorBrush.ColorProperty,
                    new ColorAnimation(targetColor, TimeSpan.FromMilliseconds(durMs)) { EasingFunction = ease });
            }
            else
            {
                bar.BeginAnimation(FrameworkElement.WidthProperty, null);
                brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
                bar.Width = targetW;
                brush.Color = targetColor;
            }
        }
    }

    /// <summary>每条短线用自己的可写画刷（模板里的画刷会被冻住，动画写不进去）。</summary>
    static SolidColorBrush EnsureRunBarBrush(Border bar)
    {
        if (bar.Background is SolidColorBrush sb && !sb.IsFrozen) return sb;
        var nb = new SolidColorBrush(Colors.Transparent);
        bar.Background = nb;
        return nb;
    }

    // ── 悬停放大 + 相邻推让 ─────────────────────────────────────────────

    /// <summary>当前被放大的是第几格（-1 = 没有）。**状态机收口**：所有放大/复原都经它比对，
    /// 同一格重复 Enter 不会重播动画（用户报的「显示名称时悬停图标、缩放一直抽搐/反复播放」）。</summary>
    int _dockHoverIndex = -1;

    /// <summary>指针脱离对账（200ms）：真实鼠标进过条体才启动，离屏自测不会启动。</summary>
    System.Windows.Threading.DispatcherTimer? _dockHoverWatch;

    /// <summary>Leave 后延迟复核用（Enter(邻居) 常常晚于 Leave(上一个) 到达）。</summary>
    System.Windows.Threading.DispatcherTimer? _dockHoverEndTimer;

    /// <summary>
    /// 悬停放大（方案 A：只放大悬停那一格，两侧整体平移让位）。
    ///
    /// 早先邻居也跟着放大（权重 1 / .55 / .22），位移却受胶囊内边距限制被夹死 ——
    /// 倍数一开大，放大后的格子互相压在一起（用户反馈"重合在一起"）。
    /// 现在：悬停格 ×m，同侧其余格平移 grow = 格×(m−1)/2，格间距恒定 → 永远不重合；
    /// 整排往外撑开的那部分留在窗口两侧的透明余量里（ZoneLayout.DockSideOverflow）。
    ///
    /// ponytail 2026-11-01 「缩放抽搐」修复 — 以前每收到一次 MouseEnter 就重新
    /// BeginAnimation 一遍整个条体。只要 Enter 因为任何原因重复（光标停在两格交界、
    /// 名字/图标换命中元素、动画把邻居推到位后又弹回来…），动画就从当前值重新往目标值
    /// 跑一遍 → 屏幕上看就是"缩放一直在抽搐、反复播放"。现在先把目标序号记下来，
    /// 同一格重复 Enter 直接返回；换了格子才动一次；离开不立刻复原（见
    /// <see cref="DockHoverEnd"/>），由 <see cref="ReconcileDockHover"/> 用真实命中元素对账。
    /// </summary>
    void DockHoverBegin(object sender)
    {
        if (!IsDock || !_zone.DockAnimate) return;
        int idx = sender is FrameworkElement fe && fe.DataContext is ZoneItemViewModel hvm
            ? _vm.Items.IndexOf(hvm) : -1;
        if (idx < 0 || _vm.Items.Count == 0) return;
        if (idx == _dockHoverIndex) return;      // ← 重复 Enter：不重播动画
        SetDockHoverIndex(idx, animate: true);
    }

    /// <summary>放大到第 <paramref name="idx"/> 格（-1 = 全部复原）。
    /// 只改 RenderTransform，不触发布局。</summary>
    void SetDockHoverIndex(int idx, bool animate)
    {
        _dockHoverIndex = idx;
        _dockHoverMiss = 0;
        int n = _vm.Items.Count;
        double m = Math.Clamp(_zone.DockMagnify, 1.0, 2.0);
        double grow = ZoneLayout.DockHoverShift(_zone);
        var scale = new double[n];
        var off = new double[n];
        for (int i = 0; i < n; i++)
        {
            bool hot = i == idx;
            scale[i] = hot ? m : 1.0;
            off[i] = idx < 0 ? 0.0 : (i < idx ? -grow : (i > idx ? grow : 0.0));
        }
        if (!_zone.DockAnimate || !animate)
        {
            ResetDockRowTransforms();
            if (idx < 0) return;
            // 无动画（关掉"悬停动效"或首次落位）：直接写值
            for (int i = 0; i < n; i++)
            {
                var tg = DockItemTransform(i);
                if (tg == null || tg.Children.Count < 2) continue;
                if (tg.Children[0] is ScaleTransform sc) sc.ScaleX = sc.ScaleY = scale[i];
                if (tg.Children[1] is TranslateTransform tr) tr.X = off[i];
            }
        }
        else
        {
            AnimateDockRow(scale, off);
        }
        // 被放大那格要压在邻居上面（放大后它会盖住让位后的邻居）。
        for (int i = 0; i < n; i++)
            if (ItemsHost.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement c)
                Panel.SetZIndex(c, i == idx ? 10 : 0);
    }

    /// <summary>
    /// 悬停结束。**不立刻复原**：MouseLeave 与下一个 MouseEnter 的到达顺序没有保证，
    /// 立刻收起会变成"放大→收起→再放大"的一次抽搐。这里只排一个 70ms 后的复核，
    /// 由真实命中元素（<see cref="DockHoveredIndex"/>）决定到底还该不该放大。
    /// </summary>
    void DockHoverEnd()
    {
        if (!IsDock) return;
        if (_dockHoverIndex < 0 && _dockHoverEndTimer == null) return;
#if DEBUG
        HoverNote($"magnify leave (keep={_dockHoverIndex})");
#endif
        _dockHoverEndTimer ??= new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(70)
        };
        _dockHoverEndTimer.Tick -= DockHoverEndTick;
        _dockHoverEndTimer.Tick += DockHoverEndTick;
        _dockHoverEndTimer.Stop();
        _dockHoverEndTimer.Start();
    }

    void DockHoverEndTick(object? s, EventArgs e)
    {
        _dockHoverEndTimer?.Stop();
        ReconcileDockHover();
    }

    /// <summary>
    /// 把「该放大的格子」对齐到**真实命中元素**。只在"真的被鼠标进过"之后启动
    /// （离屏自测直接调 Item_Enter，没有真实指针，不该被它复原）。
    /// 这样即使 Leave/Enter 漏了一条、或容器被重建，状态也会在 200ms 内自动收敛。
    ///
    /// **连续两次**量到"不在任何一格上"才复原：实测光标归属会在 5ms 内打一次
    /// enter/leave 的假信号（悬浮窗/缩放动画把邻居推走都会这样），一次就复原的话
    /// 屏幕上是"放大→缩回→再放大"的抽搐 —— 正是用户报的那条。
    /// </summary>
    void ReconcileDockHover()
    {
        if (!IsDock || !_zone.DockAnimate) return;
        int over = DockHoveredIndex();
        if (over >= 0)
        {
            _dockHoverMiss = 0;
            if (over != _dockHoverIndex) SetDockHoverIndex(over, animate: true);
            return;
        }
        if (_dockHoverIndex < 0) { _dockHoverWatch?.Stop(); return; }   // 本来就没放大
        if (++_dockHoverMiss < 2) return;                               // 疑似瞬断，再等等
        _dockHoverMiss = 0;
        SetDockHoverIndex(-1, animate: true);
        _dockHoverWatch?.Stop();
    }

    /// <summary>连续几次对账都没量到"悬停在某格上"（用于滤掉瞬时假 leave）。</summary>
    int _dockHoverMiss;

    /// <summary>真实指针现在停在第几格（-1 = 不在任何一格上）。基于命中元素往上找
    /// DockItemRoot，再换回容器序号 —— 比"哪个格子收到过 Enter"可靠。</summary>
    int DockHoveredIndex()
    {
        try
        {
            var d = Mouse.DirectlyOver as DependencyObject;
            while (d != null)
            {
                if (d is FrameworkElement fe && fe.Name == "DockItemRoot")
                {
                    var p = VisualTreeHelper.GetParent(fe);
                    while (p != null && p is not ContentPresenter)
                        p = VisualTreeHelper.GetParent(p);
                    if (p is ContentPresenter cp)
                        return ItemsHost.ItemContainerGenerator.IndexFromContainer(cp);
                    return -1;
                }
                d = VisualTreeHelper.GetParent(d);
            }
        }
        catch { }
        return -1;
    }

    void StartDockHoverWatch()
    {
        _dockHoverWatch ??= new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _dockHoverWatch.Tick -= DockHoverWatchTick;
        _dockHoverWatch.Tick += DockHoverWatchTick;
        if (!_dockHoverWatch.IsEnabled) _dockHoverWatch.Start();
    }

    void DockHoverWatchTick(object? s, EventArgs e) => ReconcileDockHover();

    /// <summary>把所有格的放大/位移直接归零（不走动画）。关掉动画开关、改图标大小、
    /// 重排之后调用 —— 否则会卡在上一次的放大态上。</summary>
    void ResetDockRowTransforms()
    {
        _dockHoverIndex = -1;
        int n = _vm.Items.Count;
        for (int i = 0; i < n; i++)
        {
            var tg = DockItemTransform(i);
            if (tg == null || tg.Children.Count < 2) continue;
            if (tg.Children[0] is not ScaleTransform sc || tg.Children[1] is not TranslateTransform tr) continue;
            sc.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            sc.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            tr.BeginAnimation(TranslateTransform.XProperty, null);
            sc.ScaleX = sc.ScaleY = 1;
            tr.X = 0;
        }
    }

    /// <summary>把每一格的 Scale/Translate 一起推过去。只改 RenderTransform，
    /// 不触发布局（和分区里 SetMergeHover / PlayTabSlideAnimations 同一个套路）。
    /// 注意：容器（ContentPresenter）不是那个带变换的 Grid —— 变换在
    /// DockItemTemplate 的根 Grid（x:Name="DockItemRoot"）上，必须往里找一层。</summary>
    void AnimateDockRow(double[] scale, double[] off)
    {
#if DEBUG
        _dockRowAnimCount++;   // 自测用：统计悬停动画重启次数（Release 编译掉）
#endif
        var dur = TimeSpan.FromMilliseconds(Math.Clamp(_zone.DockAnimMs, 80, 300));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        int n = Math.Min(scale.Length, _vm.Items.Count);
        for (int i = 0; i < n; i++)
        {
            var tg = DockItemTransform(i);
            if (tg == null || tg.Children.Count < 2) continue;
            if (tg.Children[0] is not ScaleTransform sc || tg.Children[1] is not TranslateTransform tr) continue;
            sc.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(scale[i], dur) { EasingFunction = ease });
            sc.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(scale[i], dur) { EasingFunction = ease });
            tr.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(off[i], dur) { EasingFunction = ease });
        }
    }

    /// <summary>第 i 格的变换组（模板根 Grid 上的 Scale+Translate）。模板里的变换会被
    /// 冻住（DataTemplate 封存后 TransformGroup 不可动画），所以这里在代码里挂一份可写的；
    /// 幂等，每次拿都确保存在。</summary>
    TransformGroup? DockItemTransform(int i)
    {
        if (ItemsHost.ItemContainerGenerator.ContainerFromIndex(i) is not DependencyObject container) return null;
        var root = FindVisualChild<Grid>(container, g => g.Name == "DockItemRoot");
        if (root == null) return null;
        if (root.RenderTransform is TransformGroup tg0 && !tg0.IsFrozen && tg0.Children.Count >= 2) return tg0;
        var tg = new TransformGroup();
        tg.Children.Add(new ScaleTransform(1, 1));
        tg.Children.Add(new TranslateTransform(0, 0));
        root.RenderTransform = tg;
        return tg;
    }

    // ── 拖拽排序 ─────────────────────────────────────────────────────────

    /// <summary>按落点 X 算目标序号，把图标挪到那个位置，然后重排 + 重算宽高。</summary>
    void CommitDockReorder(ZoneItemViewModel vm)
    {
        var raw = _zone.Items.FirstOrDefault(i => i.Id == vm.Id);
        if (raw == null) return;
        double pitch = _zone.GridSize + ZoneLayout.DockGap;
        int target = (int)Math.Round((vm.X - ZoneLayout.DockPadH) / pitch);
        target = Math.Clamp(target, 0, _zone.Items.Count - 1);
        int from = _zone.Items.IndexOf(raw);
        if (from == target) { ApplyDockGeometry(); return; }
        _zone.Items.RemoveAt(from);
        _zone.Items.Insert(target, raw);
        _vm.RefreshItems();
        ApplyDockGeometry();
        _mgr.SaveConfig();
    }

    // ── 添加应用 / 完全隐藏 ───────────────────────────────────────────────

    void DockAddApp_Click(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = _loc["DockMenu.AddApp"],
            Filter = $"{_loc["Filter.Exe"]}|*.exe|{_loc["Filter.Lnk"]}|*.lnk|{_loc["Filter.All"]}|*.*",
            Multiselect = true,
        };
        if (dlg.ShowDialog() == true) ImportDockApps(dlg.FileNames);
    }

    /// <summary>追加到末尾（快捷栏没有方格可找），保留选择顺序，然后重算几何。</summary>
    void ImportDockApps(string[] paths)
    {
        if (!IsDock || paths.Length == 0) return;
        int added = 0;
        foreach (var p in paths)
        {
            var type = Path.GetExtension(p).ToLowerInvariant() switch
            {
                ".lnk" => ItemType.Shortcut,
                ".exe" => ItemType.Application,
                _ => ItemType.Shortcut,
            };
            var (target, ntype, iconLoc) = ShortcutResolver.NormalizeItem(p, type);
            if (_zone.Items.Any(i => string.Equals(i.TargetPath, target, StringComparison.OrdinalIgnoreCase)))
                continue;
            var item = new ZoneItem(Path.GetFileNameWithoutExtension(p), target, ntype, 0, 0)
            {
                IconPath = iconLoc,
            };
            ShellIconService.Instance.GetIcon(item.TargetPath, item.Type, item.IconPath);
            _vm.AddItem(item);
            added++;
        }
        if (added == 0) return;
        ApplyDockGeometry();
        _mgr.SaveConfig();
    }

    void DockFullHide_Click(object s, RoutedEventArgs e) => _mgr.FullHideZone(_zone.Id);

#if DEBUG
    /// <summary>自测入口（--render-dock-preview）：把第 index 格摆成悬停态，
    /// 用来在离屏渲染里检查放大与邻位移是否算对。走的是真实的 Item_Enter
    /// （悬停高光也在里面，动效开着时它会按规则被跳过）。</summary>
    public void PreviewDockHover(int index)
    {
        if (!IsDock) return;
        if (index < 0 || index >= ItemsHost.Items.Count) return;
        if (ItemsHost.ItemContainerGenerator.ContainerFromIndex(index) is not DependencyObject container) return;
        // 真实路径里 MouseEnter 挂在模板根 Grid 上（sender 就是那个 Grid），
        // 这里必须同样取到 DockItemRoot，否则高光那一段（s is Grid）不会执行。
        var root = FindVisualChild<Grid>(container, g => g.Name == "DockItemRoot");
        Item_Enter(root ?? (object)container, null!);
    }

    /// <summary>自测用：把条体右键菜单按当前形态打开一次，返回可见项文字串
    /// （分隔线记作 |），用来核对快捷栏菜单是否与分区对齐。</summary>
    public string DebugOpenZoneMenu()
    {
        var cm = ZoneBorder.ContextMenu;
        if (cm == null) return "no-menu";
        var sb = new System.Text.StringBuilder();
        try
        {
            cm.PlacementTarget = ZoneBorder;
            cm.IsOpen = true;
            foreach (var entry in cm.Items)
            {
                switch (entry)
                {
                    case Separator sep when sep.Visibility == Visibility.Visible:
                        sb.Append(" | ");
                        break;
                    case MenuItem mi when mi.Visibility == Visibility.Visible:
                        sb.Append(mi.Header?.ToString() ?? "?");
                        if (mi.Items.Count > 0) sb.Append(" ▶");
                        sb.Append(" · ");
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            sb.Append("menu-error: ").Append(ex.Message);
        }
        finally
        {
            cm.IsOpen = false;
        }
        return sb.ToString();
    }

    /// <summary>自测用：手动摆两个运行状态（不依赖真的开程序），离屏渲染检查灰/蓝短线。</summary>
    public void PreviewDockRunStates(int focusedIndex, int backgroundIndex)
    {
        if (!IsDock) return;
        for (int i = 0; i < _vm.Items.Count; i++)
        {
            _vm.Items[i].RunState = i == focusedIndex ? DockRunState.Foreground
                : (i == backgroundIndex ? DockRunState.Background : DockRunState.None);
        }
        RefreshDockRunBars(animate: false);
    }

    /// <summary>自测用：真跑一轮「谁在运行 / 谁在前台」，把每个条目的 exe 名与判定结果打出来
    /// （验证运行状态指示不是只靠手摆的假数据能渲染，而是真识别到了 exe）。</summary>
    public string PreviewDockLiveRun()
    {
        if (!IsDock) return "not-dock";
        RefreshDockRunStates();
        var sb = new System.Text.StringBuilder();
        sb.Append("live: ");
        foreach (var vm in _vm.Items)
            sb.Append($"{vm.Name}='{AppActivityService.ExeNameForPath(vm.TargetPath)}'→{vm.RunState}; ");
        try
        {
            var snap = AppActivityService.Snapshot();
            sb.Append($" snapshot={snap.Count}");
            if (snap.TryGetValue(AppActivityService.ExeNameForPath(_vm.Items.FirstOrDefault()?.TargetPath), out var app))
                sb.Append($" firstIsForeground={app.IsForeground}");
        }
        catch (Exception ex) { sb.Append(" snapshot-failed:" + ex.Message); }
        return sb.ToString();
    }

    /// <summary>自测用：玻璃/开始按钮倒影的状态摘录（验证背板不是空的、镜像节点数对）。</summary>
    public string DebugDockGlass()
    {
        string fill = FillRect?.Fill?.GetType().Name ?? "null";
        string brush = FillRect?.Fill is SolidColorBrush sb2 ? $"solid#{sb2.Color.A:X2}{sb2.Color.R:X2}{sb2.Color.G:X2}{sb2.Color.B:X2}" : fill;
        var pill = DockPillScreenPx();
        int startRefl = 0;
        if (DockChromeLayer != null)
            foreach (var ch in DockChromeLayer.Children)
                if (ch is FrameworkElement f && (f.Tag as string) == DockReflectTag) startRefl++;
        var inset = DockPillInsetPx();
        return $"glass={_dockGlassMode} fill={brush} " +
               $"glaswin={(_dockGlassWin == null ? "none" : (_dockGlassWin.IsVisibleGlass ? $"visible#0x{_dockGlassWin.Handle.ToInt64():X}" : "hidden"))} " +
               $"host={(DockGlassHost == null ? "null" : $"{DockGlassHost.Visibility} {DockGlassHost.ActualWidth:F0}x{DockGlassHost.ActualHeight:F0}")} " +
               $"inset={inset.X:F0},{inset.Y:F0} {inset.Width:F0}x{inset.Height:F0} " +
               $"rVis={(IsDock ? DockVisibleCornerRadius(ResolveStyle()) : _zone.CornerRadius):F1} " +
               $"rGlassPx={_dockGlassRadiusPx} " +
               $"pill={(pill.IsEmpty ? "empty" : $"{pill.X:F0},{pill.Y:F0} {pill.Width:F0}x{pill.Height:F0}")} " +
               $"gen={WallpaperBackdrop.Generation} " +
               $"startGlyph={DockStartGlyph?.ActualWidth:F0} startRefl={startRefl} " +
               $"bands=top{ZoneLayout.DockOverflowTop(_zone):F0}/refl{ZoneLayout.DockReflectBand(_zone):F0} " +
               $"snap=[{string.Join(" | ", _snapLog)}] refl=[{string.Join(" ", _reflLog)}]";
    }

    /// <summary>自测用：胶囊的**三方几何**（全部换成设备像素）—— 胶囊矩形 / 描边外沿 /
    /// 玻璃窗矩形。三个必须落在同一组整数边界上：差 0 才叫"严丝合缝"，只要有一个是 .25
    /// 就是一条发丝缝（第 10 轮的左下角问题）。</summary>
    public string DebugDockPillGeom()
    {
        try
        {
            var pill = DockPillScreenPx();
            var origin = PointToScreen(new Point(0, 0));
            var dpi = VisualTreeHelper.GetDpi(this);
            string stroke = "n/a";
            if (DockBarStroke != null && DockBarStroke.Visibility == Visibility.Visible
                && DockBarStroke.ActualWidth > 0)
            {
                var tl = DockBarStroke.TransformToAncestor(this).Transform(new Point(0, 0));
                var br = DockBarStroke.TransformToAncestor(this).Transform(
                    new Point(DockBarStroke.ActualWidth, DockBarStroke.ActualHeight));
                double half = DockBarStroke.StrokeThickness / 2;   // 描边居中：外沿再向外半个线宽
                double x0 = origin.X + (tl.X - half) * dpi.DpiScaleX;
                double y0 = origin.Y + (tl.Y - half) * dpi.DpiScaleY;
                double x1 = origin.X + (br.X + half) * dpi.DpiScaleX;
                double y1 = origin.Y + (br.Y + half) * dpi.DpiScaleY;
                stroke = $"({x0:F2},{y0:F2}) {x1 - x0:F2}x{y1 - y0:F2}";
            }
            string glass = "none";
            if (_dockGlassWin is { } gw && gw.Handle != IntPtr.Zero)
            {
                NativeMethods.GetWindowRect(gw.Handle, out var r);
                glass = $"({r.Left},{r.Top}) {r.Right - r.Left}x{r.Bottom - r.Top}";
            }
            return $"pillPx=({pill.X},{pill.Y}) {pill.Width}x{pill.Height} | " +
                   $"strokeOuter={stroke} | glassRect={glass}";
        }
        catch (Exception ex) { return "geom-exc " + ex.Message; }
    }

    /// <summary>自测用：胶囊玻璃图层的内部状态（半径/世代/位图/裁剪框）。</summary>
    public string DebugDockGlassLayer() => _dockGlassLayer?.DebugState() ?? "(no layer)";

    /// <summary>自测用：强制显示/藏起玻璃层窗口（抓"纯桌面"参照帧时要两个窗口都收走）。</summary>
    public void DebugSetDockGlassVisible(bool on)
    {
        if (_dockGlassWin == null) return;
        if (on) _dockGlassWin.ShowNoActivate(); else _dockGlassWin.HideGlass();
    }

    /// <summary>自测用：玻璃层窗口句柄（0 = 还没创建）。</summary>
    public IntPtr DebugDockGlassHwnd() => _dockGlassWin?.Handle ?? IntPtr.Zero;

    // ── 自测用：DWM 整窗模糊 vs 窗口裁成胶囊（第 7 轮回答「为什么做不了 DWM」） ──

    [DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);
    [DllImport("user32.dll")] static extern int SetWindowRgn(IntPtr hwnd, IntPtr hRgn, bool redraw);

    /// <summary>
    /// 绕过 <see cref="ApplyDockGlass"/> 的兜底，直接给快捷栏窗口上**真正那套 DWM 配方**
    /// （和分区 / 律动条同一个 <see cref="AcrylicHelper.EnableBlurComposite"/>），用于实测
    /// 「DWM 的模糊到底只盖胶囊、还是把胶囊外那两条透明带也糊上」。
    /// mode 0 = 复原到正常路径；1 = DWM 整窗；2 = DWM + 把窗口 SetWindowRgn 裁成胶囊。
    /// </summary>
    public string DebugForceDwmGlass(int mode)
    {
        var s = ResolveStyle();
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return "no-hwnd";
        if (FillRect == null) return "no-fillrect";

        SetWindowRgn(hwnd, IntPtr.Zero, true);          // 先撤掉可能存在的区域

        if (mode == 0)
        {
            AcrylicHelper.DisableBlur(this);
            ApplyDockGlass(s);
            return "restored | " + DebugDockGlass();
        }

        var r = AcrylicHelper.EnableBlurComposite(this, _zone.GlassBlurAmount,
            s.FillColor, 1.0, _zone.GlassColorMode, _zone.GlassTintOpacity, _zone.GlassTintLuminosity,
            AcrylicHelper.ResolveAccentState(_zone.GlassMaterial),
            AcrylicHelper.ResolveUseClassicBlur(_zone.GlassMaterial));
        FillRect.Fill = AcrylicHelper.HitTestFill;      // 与分区走 DWM 时完全一致
        FillRect.Opacity = 1.0;

        string regionTxt = "full-window";
        if (mode == 2)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            double side = ZoneLayout.DockSideOverflow(_zone);
            double top = ZoneLayout.DockBarTop(_zone);
            double w = ZoneLayout.DockPillWidth(_zone, _zone.Items.Count);
            double h = ZoneLayout.DockBarHeight(_zone);
            int x = (int)Math.Round(side * dpi.DpiScaleX);
            int y = (int)Math.Round(top * dpi.DpiScaleY);
            int ww = (int)Math.Round(w * dpi.DpiScaleX);
            int hh = (int)Math.Round(h * dpi.DpiScaleY);
            int rad = (int)Math.Round(s.CornerRadius * dpi.DpiScaleX * 2);
            SetWindowRgn(hwnd, CreateRoundRectRgn(x, y, x + ww, y + hh, rad, rad), true);
            regionTxt = $"pill {x},{y} {ww}x{hh} r={rad}";
        }
        return $"dwm={(r.Success ? "ok" : r.Error)} region={regionTxt} | " + DebugDockGlass();
    }

    /// <summary>
    /// 自测用：验证「最小化后鼠标悬停恢复按钮自动展开」真的接上了 —— 收起成 36×36 方块，
    /// 合成一次 MouseEnter 打进恢复按钮，等 500ms 展开计时器跑完，看有没有自动展开。
    /// </summary>
    public async Task<string> PreviewHoverRestoreAsync()
    {
        if (!IsDock) return "not-dock";
        bool enabled = _hover?.IsEnabled == true;
        string result;
        try
        {
            _hover?.SnapToCollapsed();
            await Task.Delay(120);
            bool before = _hover?.IsExpanded == true;
            bool btnVisible = RestoreButton.Visibility == Visibility.Visible;
            RestoreButton.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
            { RoutedEvent = Mouse.MouseEnterEvent });
            await Task.Delay(900);
            bool after = _hover?.IsExpanded == true;
            result = $"hoverRestore={_zone.DockHoverRestore} enabled={enabled} collapsed={!before} " +
                     $"btnVisible={btnVisible} afterHoverEnter={after}";
        }
        catch (Exception ex) { result = "EXC " + ex.GetType().Name + ": " + ex.Message; }
        _hover?.SnapToExpanded();
        return result;
    }

    /// <summary>自测用：打印「点这个图标会切前台还是最小化」的判定，不真的动别人的窗口。</summary>
    public string PreviewDockToggle(int index)
    {
        if (!IsDock || index < 0 || index >= _vm.Items.Count) return "bad-index";
        var vm = _vm.Items[index];
        var exe = AppActivityService.ExeNameForPath(vm.TargetPath);
        var wins = AppActivityService.WindowsOf(exe);
        var hint = _foregroundBeforeClick != IntPtr.Zero ? _foregroundBeforeClick : AppActivityService.LastOtherForeground;
        string Decide(IntPtr h) => wins.Count == 0 ? "launch"
            : AppActivityService.WindowBelongsTo(h, exe) ? "minimize" : "activate";
        return $"toggle[{index}] name={vm.Name} exe='{exe}' wins={wins.Count} " +
               $"hint=0x{hint.ToInt64():X} → {Decide(hint)} | " +
               $"假设刚才前台是它自己的窗口(0x{(wins.Count > 0 ? wins[0].ToInt64() : 0):X}) → " +
               $"{(wins.Count > 0 ? Decide(wins[0]) : "launch")}";
    }

    /// <summary>自测用：立刻跑一轮运行状态轮询（不等 700ms 定时器）。</summary>
    public void RefreshDockRunStatesForTest() => RefreshDockRunStates();

    /// <summary>自测用：资源管理器打开着的文件夹（文件夹快捷方式的横条靠它判定）。</summary>
    public string PreviewDockFolders()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var vm in _vm.Items)
        {
            if (!AppActivityService.IsBrowsableFolder(vm.TargetPath)) continue;
            var hwnd = AppActivityService.FolderOpenWindow(vm.TargetPath);
            sb.Append($"folder '{vm.Name}': open={hwnd != IntPtr.Zero} state={vm.RunState}; ");
        }
        var locs = AppActivityService.ExplorerLocations();
        sb.Append($"explorerWindows={locs.Count}");
        foreach (var (p, h) in locs) sb.Append($" [{p} h=0x{h.ToInt64():X}]");
        sb.Append(" titles=[");
        foreach (var (t, h) in AppActivityService.ExplorerWindowTitles()) sb.Append($"'{t}' h=0x{h.ToInt64():X} ");
        sb.Append($"] diag={AppActivityService.ExplorerDiag}");
        return sb.ToString();
    }

    /// <summary>自测用：把当前每格的缩放/位移读出来，确认动画真的落到容器上。</summary>
    public string DebugDockState()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"items={_vm.Items.Count}; containers={ItemsHost.Items.Count}; ");
        int refl = 0;
        for (int i = 0; i < ItemsHost.Items.Count; i++)
        {
            if (ItemsHost.ItemContainerGenerator.ContainerFromIndex(i) is not DependencyObject cont) continue;
            var rt = FindVisualChild<Grid>(cont, g => g.Name == "DockItemRoot");
            if (rt == null) continue;
            foreach (var ch in rt.Children)
                if (ch is FrameworkElement f && (f.Tag as string) == DockReflectTag) refl++;
        }
        sb.Append($"reflectNodes={refl}; ");
        try
        {
            for (int i = 0; i < ItemsHost.Items.Count; i++)
            {
                if (ItemsHost.ItemContainerGenerator.ContainerFromIndex(i) is not DependencyObject cont) continue;
                var rt = FindVisualChild<Grid>(cont, g => g.Name == "DockItemRoot");
                if (rt == null) continue;
                FrameworkElement? w0 = null;
                foreach (var ch in rt.Children)
                    if (ch is FrameworkElement f && (f.Tag as string) == DockReflectTag) { w0 = f; break; }
                if (w0 is not Grid g0) continue;
                var pos = g0.TransformToAncestor(this).Transform(new Point(0, 0));
                var kid = g0.Children.Count > 0 ? g0.Children[0] as FrameworkElement : null;
                var kidPos = kid?.TransformToAncestor(this).Transform(new Point(0, 0)) ?? new Point(-1, -1);
                sb.Append($"refl0 root={((FrameworkElement)rt).ActualWidth:F0}x{((FrameworkElement)rt).ActualHeight:F0} " +
                          $"size={g0.ActualWidth:F0}x{g0.ActualHeight:F0} pos=({pos.X:F1},{pos.Y:F1}) op={g0.Opacity:F2} " +
                          $"kid={kid?.GetType().Name ?? "null"} {kid?.ActualWidth:F0}x{kid?.ActualHeight:F0} kidPos=({kidPos.X:F1},{kidPos.Y:F1}); ");
                break;
            }
        }
        catch (Exception ex) { sb.Append("reflERR=" + ex.Message + "; "); }
        for (int i = 0; i < ItemsHost.Items.Count; i++)
        {
            var c = ItemsHost.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement;
            if (c == null) { sb.Append($"[{i}]=null; "); continue; }
            var tg = DockItemTransform(i);
            var sc = tg?.Children.Count > 0 ? tg.Children[0] as ScaleTransform : null;
            var tr = tg?.Children.Count > 1 ? tg.Children[1] as TranslateTransform : null;
            var hover = FindVisualChild<Border>(c, b => b.Name is "HoverBox");
            sb.Append($"[{i}] rt={c.RenderTransform?.GetType().Name ?? "null"} " +
                      $"sx={(sc?.ScaleX ?? -1):F2} tx={(tr?.X ?? -999):F1} " +
                      $"hover={(hover?.Background is SolidColorBrush b2 ? b2.Color.ToString() : "none")} " +
                      $"pos={c.TransformToAncestor(this).Transform(new Point(0, 0)):F1}; ");
        }
        return sb.ToString();
    }
    // ── 自测台（--dock-lab）用的探针 ───────────────────────────────────────
    // 悬停事件环形缓冲：Enter/Leave/放大开始结束都记时间戳，用来抓"同一格来回抖"。
    readonly List<string> _hoverLog = new();

    void HoverNote(string s)
    {
        _hoverLog.Add($"{DateTime.Now:HH:mm:ss.fff} {s}");
        if (_hoverLog.Count > 60) _hoverLog.RemoveAt(0);
    }

    int DockIndexOf(object s)
        => s is FrameworkElement fe && fe.DataContext is ZoneItemViewModel v ? _vm.Items.IndexOf(v) : -1;

    bool ItemIsOver(object s) => s is UIElement u && u.IsMouseOver;

    /// <summary>最近 60 条悬停事件（复现"抽搐"时看 Enter/Leave 有没有来回打）。</summary>
    public string DebugHoverLog() => _hoverLog.Count == 0 ? "(empty)" : string.Join(" | ", _hoverLog);

    /// <summary>每格的缩放/位移（悬停放大是否稳定在这里看）。</summary>
    public string DebugDockScales()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < _vm.Items.Count; i++)
        {
            var tg = DockItemTransform(i);
            double sx = 1, tx = 0;
            if (tg != null && tg.Children.Count > 1)
            {
                if (tg.Children[0] is ScaleTransform sc) sx = sc.ScaleX;
                if (tg.Children[1] is TranslateTransform tr) tx = tr.X;
            }
            sb.Append($"[{i}]{sx:F2}/{tx:F1} ");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>拖拽状态机快照：拖动标志、被拖条目、不透明度、鼠标捕获、每格 X。</summary>
    public string DebugDockInteraction()
    {
        string cap = Mouse.Captured is FrameworkElement cf && !string.IsNullOrEmpty(cf.Name) ? cf.Name
            : Mouse.Captured?.GetType().Name ?? "null";
        var sb = new System.Text.StringBuilder();
        sb.Append($"dragging={_dragging} dv={_dv?.Name ?? "-"} deOp={(_de?.Opacity ?? 1):F2} capture={cap} ");
        for (int i = 0; i < _vm.Items.Count; i++)
        {
            var vm = _vm.Items[i];
            sb.Append($"[{i}]{vm.Name}:X={vm.X:F0} ");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>主体内容的可见性 + 不透明度（收起/展开后对账用）。</summary>
    public string DebugContentState()
        => $"main={MainContent.Visibility} op={MainContent.Opacity:F2} " +
           $"hasTransform={(MainContent.RenderTransform is Transform ? "T" : "-")} " +
           $"btn={RestoreButton.Visibility} hoverExpanded={_hover?.IsExpanded}";

    /// <summary>当前是否处于「内容已展开」状态。</summary>
    public bool DebugHoverExpanded() => _hover?.IsExpanded == true;

    /// <summary>
    /// 自测用：验证「拖动途中丢捕获」两条收尾路径真的接上了 ——
    /// ① 真给图标格设鼠标捕获，再 <c>Mouse.Capture(null)</c>（= 前台窗口被激活时系统的行为）
    ///    → LostMouseCapture 处理器应当把拖动状态清干净；
    /// ② 直接把状态摆成"正在拖"，再喂一条左键已抬起的 MouseMove（丢捕获后最常见的残留输入）
    ///    → Item_MouseMove 的前置检查应当收尾。
    /// </summary>
    public string PreviewDragWiring()
    {
        if (!IsDock || ItemsHost.Items.Count == 0) return "not-dock";
        if (ItemsHost.ItemContainerGenerator.ContainerFromIndex(0) is not DependencyObject c) return "no-container";
        var root = FindVisualChild<Grid>(c, g => g.Name == "DockItemRoot");
        if (root == null) return "no-root";
        var vm = _vm.Items[0];
        double homeX = ZoneLayout.DockX(_zone, 0);
        var sb = new System.Text.StringBuilder();

        // ① 真捕获 → 夺捕获
        bool captured = root.CaptureMouse();
        _dv = vm; _de = root; _dragging = true; root.Opacity = 0.7;
        vm.X = homeX + 40;
        sb.Append($"[1] captured={captured} before: drag={_dragging} X={vm.X:F0} op={root.Opacity:F2}; ");
        System.Windows.Input.Mouse.Capture(null);
        sb.Append($"after: drag={_dragging} dv={(_dv?.Name ?? "-")} X={vm.X:F0} op={root.Opacity:F2}; ");

        // ② 残留输入：左键已抬起的 MouseMove
        _dv = vm; _de = root; _dragging = true; root.Opacity = 0.7;
        vm.X = homeX + 40;
        Item_MouseMove(root, new MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount)
        { RoutedEvent = UIElement.MouseMoveEvent, Source = root });
        sb.Append($"[2] after-move: drag={_dragging} dv={(_dv?.Name ?? "-")} X={vm.X:F0} op={root.Opacity:F2}");
        return sb.ToString();
    }

    /// <summary>
    /// 自测用：验证悬停放大不会「同一格重复 Enter 就重播动画」——
    /// 连续 20 次 Item_Enter 同一格，动画启动次数应当只有 1，最终缩放应当停在倍数上；
    /// 再喂一条 Leave，70ms 后由"真实命中元素"对账复原。
    /// </summary>
    public string PreviewHoverIdempotence()
    {
        if (!IsDock || ItemsHost.Items.Count < 2) return "not-dock";
        var roots = new List<Grid>();
        for (int i = 0; i < 2; i++)
        {
            if (ItemsHost.ItemContainerGenerator.ContainerFromIndex(i) is not DependencyObject c) continue;
            var r = FindVisualChild<Grid>(c, g => g.Name == "DockItemRoot");
            if (r != null) roots.Add(r);
        }
        if (roots.Count < 2) return "no-root";
        int before = _dockRowAnimCount;
        for (int i = 0; i < 20; i++) DockHoverBegin(roots[1]);   // 同一格 20 次 Enter
        int same = _dockRowAnimCount - before;
        DockHoverBegin(roots[0]);                                 // 换到另一格
        int switched = _dockRowAnimCount - before;
        string mid = DebugDockScales();
        DockHoverEnd();
        ReconcileDockHover();   // 第一次：疑似瞬断（不复原）
        ReconcileDockHover();   // 第二次：确认离开 → 复原
        int afterEnd = _dockRowAnimCount - before;
        return $"sameIdxRestarts={same}(expect 1) afterSwitch={switched}(expect 2) afterEnd={afterEnd}(expect 3) " +
               $"mid={mid} end={DebugDockScales()}";
    }

    /// <summary>AnimateDockRow 被调用次数（自测：悬停动画重播计数）。</summary>
    int _dockRowAnimCount;

    /// <summary>自测用：把「悬停放大 / 悬停提示」清干净（自测里窗口被挪过位置时 WPF 的
    /// 命中缓存会停在旧位置，截图上会留一颗放大的图标 + 一个 tooltip）。</summary>
    public void PreviewResetHover()
    {
        _dockHoverWatch?.Stop();
        _dockHoverEndTimer?.Stop();
        _dockHoverIndex = -1;
        _dockHoverMiss = 0;
        ResetDockRowTransforms();
        for (int i = 0; i < ItemsHost.Items.Count; i++)
        {
            if (ItemsHost.ItemContainerGenerator.ContainerFromIndex(i) is not DependencyObject c) continue;
            var root = FindVisualChild<Grid>(c, g => g.Name == "DockItemRoot");
            if (root == null) continue;
            ToolTipService.SetIsEnabled(root, false);
            ToolTipService.SetIsEnabled(root, true);
        }
    }

    /// <summary>
    /// 自测用：快捷栏锁定的三道闸 —— ① 条体不能拖 ② 图标不跟手/不换位 ③ 不收外部拖入。
    /// </summary>
    public string PreviewLockGuards()
    {
        if (!IsDock || ItemsHost.Items.Count == 0) return "not-dock";
        if (ItemsHost.ItemContainerGenerator.ContainerFromIndex(0) is not DependencyObject c) return "no-container";
        var root = FindVisualChild<Grid>(c, g => g.Name == "DockItemRoot");
        if (root == null) return "no-root";
        var vm = _vm.Items[0];
        var sb = new System.Text.StringBuilder();
        double homeX = ZoneLayout.DockX(_zone, 0);
        var d = new Vector(40, 0);

        void DragProbe(string tag)
        {
            _dv = vm; _de = root; _dragging = true; _is = new Point(homeX, vm.Y); _ds = new Point(0, 0);
            DockDragFollow(d, new Point(0, 0));
            sb.Append($"{tag}: X={vm.X:F0}(home={homeX:F0}) op={root.Opacity:F2}; ");
            _dv = null; _de = null; _dragging = false; root.Opacity = 1.0;
            ZoneLayout.ArrangeDock(_zone); ArrangeDockItems();
        }

        bool saved = _zone.IsLocked;
        try
        {
            _zone.IsLocked = false; _vm.IsLocked = false; ApplyLockState();
            DragProbe("unlocked");
            _zone.IsLocked = true; _vm.IsLocked = true; ApplyLockState();
            DragProbe("locked");
            sb.Append($"dropBlocked={DockDropBlocked}; ");
            var probe = new ZoneItem("probe", @"C:\Windows\System32\notepad.exe", ItemType.Application, 0, 0);
            sb.Append($"receiveLocked={ReceiveExternalItem(probe, new Point(0, 0))}(expect False)");
        }
        finally
        {
            _zone.IsLocked = saved; _vm.IsLocked = saved; ApplyLockState();
        }
        return sb.ToString();
    }

    /// <summary>第 <paramref name="index"/> 格图标的中心点（**设备像素**，给 SetCursorPos 用）。</summary>
    public (int X, int Y) ItemIconCenterDevice(int index)
    {
        try
        {
            if (index < 0 || index >= ItemsHost.Items.Count) return (0, 0);
            if (ItemsHost.ItemContainerGenerator.ContainerFromIndex(index) is not DependencyObject c) return (0, 0);
            var fe = FindVisualChild<FrameworkElement>(c, e => e.Name == "ItemIcon")
                     ?? FindVisualChild<Grid>(c, g => g.Name == "DockItemRoot");
            if (fe == null || fe.ActualWidth < 1) return (0, 0);
            var sp = fe.PointToScreen(new Point(fe.ActualWidth / 2, fe.ActualHeight / 2));
            return ((int)Math.Round(sp.X), (int)Math.Round(sp.Y));
        }
        catch { return (0, 0); }
    }

    /// <summary>恢复按钮（36×36 方块）的中心点（**设备像素**）。</summary>
    public (int X, int Y) RestoreButtonCenterDevice()
    {
        try
        {
            if (RestoreButton == null || RestoreButton.ActualWidth < 1) return (0, 0);
            var sp = RestoreButton.PointToScreen(
                new Point(RestoreButton.ActualWidth / 2, RestoreButton.ActualHeight / 2));
            return ((int)Math.Round(sp.X), (int)Math.Round(sp.Y));
        }
        catch { return (0, 0); }
    }

    /// <summary>自测用：等价于「点恢复按钮」那一击（Restore_MouseUp 的完整效果）。
    /// 沙箱里 SetCursorPos/mouse_event 收不到真实鼠标事件，用它复现同一状态。</summary>
    public void PreviewRestoreClick()
    {
        _zone.IsVisible = true;
        _hover?.ExpandAnimated(permanent: true);
        _mgr.FireZoneVisibilityChanged(_zone.Id, true);
    }

    /// <summary>自测用：屏幕点上 WPF 命中到的元素链（诊断"点击没落到按钮上"）。</summary>
    public string DebugProbeAt(int devX, int devY)
    {
        var sb = new System.Text.StringBuilder();
        try
        {
            var local = PointFromScreen(new Point(devX, devY));
            sb.Append($"dev=({devX},{devY}) local=({local.X:F1},{local.Y:F1}) ");
            var chain = new List<string>();
            var hit = InputHitTest(local) as DependencyObject;
            for (var d = hit; d != null && chain.Count < 10; d = VisualTreeHelper.GetParent(d))
            {
                chain.Add($"{d.GetType().Name}#{(d as FrameworkElement)?.Name}");
                if (d is Window) break;
            }
            sb.Append("hit=" + (chain.Count == 0 ? "null" : string.Join(" < ", chain)));
        }
        catch (Exception ex) { sb.Append("hitEXC " + ex.Message); }
        sb.Append($" | btn={RestoreButton.Visibility} {RestoreButton.ActualWidth:F0}x{RestoreButton.ActualHeight:F0} " +
                  $"btnOver={RestoreButton.IsMouseOver} directlyOver={Mouse.DirectlyOver?.GetType().Name ?? "null"}");
        return sb.ToString();
    }
#endif
}
