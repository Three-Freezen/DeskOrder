using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using DesktopZones.Helpers;
using DesktopZones.Models;
using DesktopZones.Services;

namespace DesktopZones.ViewModels;

/// <summary>
/// ponytail: hosts the open SubFolder ZoneItem + its inner ZoneItemViewModels.
/// Each SubItem is wrapped via <see cref="ZoneItemViewModel"/> so the ItemTemplate
/// can bind .Icon — <see cref="ZoneItem"/> does NOT expose Icon directly (only the
/// ShellIconService-backed wrapper does). Mutations to <see cref="ItemVms"/> are
/// written back to <see cref="HostSubItem"/>.SubItems via whole-list replacement so
/// ZoneItem's INPC fires exactly once for the change.
/// </summary>
public class SubfolderFlyoutViewModel : INotifyPropertyChanged
{
    private readonly ZoneItem _hostSubItem;
    private readonly ShellIconService _iconService;

    /// <summary>Per-VM source map. Cleared + rebuilt on every ItemVms rebuild so
    /// external mutations to ItemVms can be written back via the original ZoneItem
    /// reference. VMs that were never registered (added via the raw OC) are
    /// dropped from the sync — use <see cref="AddItem"/> for new entries.</summary>
    private readonly Dictionary<ZoneItemViewModel, ZoneItem> _vmToSource = new();

    /// <summary>True while we are rebuilding ItemVms in response to HostSubItem's
    /// SubItems INPC — skips the writeback handler so we don't echo the change
    /// back as another INPC and loop forever.</summary>
    private bool _suppressWriteback;

    // ponytail 2026-08-26: flyout 内部拖拽换位的暂存区。拖拽过程中 ItemVms.Move
    // 只改集合顺序、不写回 HostSubItem.SubItems — 否则每次 Move 都触发 SubItems
    // INPC → RebuildItemVms 清空重建 → 拖拽中的容器被销毁、鼠标捕获丢失、被拖 VM
    // 变陈旧。松手时 Commit 一次性写回,Cancel 则按模型顺序重建还原。
    private bool _transientReorder;
    private bool _orderDirty;

    public ZoneItem HostSubItem => _hostSubItem;

    /// <summary>打开时由 ZoneWindow 解析好的填充(跟随主分区 → 主分区风格;否则
    /// SubFolder 自身 override)。为空时回落到自身 override / 默认暗色。</summary>
    public SubfolderFill Fill { get; }

    /// <summary>填充色画刷(alpha 已乘透明度)。</summary>
    public System.Windows.Media.Brush? FillBrush => Fill.FillBrush;

    /// <summary>背景图(路径无效时 null)。</summary>
    public System.Windows.Media.ImageSource? BgImage => Fill.BgImage;

    /// <summary>背景图 ImageBrush — 自动裁剪适应面板,不参与布局测量(防撑大)。</summary>
    public System.Windows.Media.Brush? BgImageBrush => Fill.BgImageBrush;

    /// <summary>背景图不透明度(0..1)。</summary>
    public double BgOpacity => Fill.BgOpacity01;

    /// <summary>ponytail 2026-08-26: Flyout 打开时优先给 Popup HWND 开真玻璃(DWM,
    /// 与主分区同配方)。成功时隐藏渐变兜底,失败时才显示渐变。由 ZoneWindow 在
    /// 打开流程里设置。</summary>
    private bool _showGlassFallback;
    public bool ShowGlassFallback
    {
        get => _showGlassFallback;
        set
        {
            if (_showGlassFallback == value) return;
            _showGlassFallback = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(GlassBrush));
        }
    }

    /// <summary>液态玻璃渐变画刷(真玻璃成功时 null,失败时渐变兜底)。</summary>
    public System.Windows.Media.Brush? GlassBrush => _showGlassFallback ? Fill.GlassBrush : null;

    /// <summary>ZoneItemViewModels for each entry in HostSubItem.SubItems.
    /// ponytail 2026-08-25: exposed as ItemVms (ZoneItemViewModel OC) instead of
    /// the original brief's SubItems (ZoneItem OC) so the template can bind
    /// .Icon directly — see class summary.</summary>
    public ObservableCollection<ZoneItemViewModel> ItemVms { get; } = new();

    private bool _isOpen;
    public bool IsOpen
    {
        get => _isOpen;
        set { _isOpen = value; OnPropertyChanged(); }
    }

    /// <summary>Flyout inner-grid cell size (from the host SubFolder's own GridSize).</summary>
    public int GridSize => _hostSubItem.GridSize;

    /// <summary>Cell width in the flyout inner grid (one grid cell).</summary>
    public double CellWidth => Math.Max(28, GridSize);

    /// <summary>Cell height — 与 CellWidth 相等的正方形格(图标在上、名称压格底),
    /// 2×2 四图标 = 2·GridSize 的正方形内容区,符合"网格大小的正方形"要求。</summary>
    public double CellHeight => Math.Max(28, GridSize);

    /// <summary>Icon render size inside a flyout cell (grid cell minus label + padding).</summary>
    public double IconSize => Math.Max(20, GridSize - 16);

    public SubfolderFlyoutViewModel(ZoneItem hostSubItem, ShellIconService iconService, SubfolderFill? fill = null)
    {
        _hostSubItem = hostSubItem;
        _iconService = iconService;
        // ponytail 2026-08-26: 填充由 ZoneWindow 按"跟随主分区 / 自身 override"解析后
        // 传入(主分区风格只有 ZoneWindow 能拿到 ResolveStyle());为空时兜底自身 override。
        Fill = fill ?? (hostSubItem.FillFollowsZone
            ? new SubfolderFill("#08000000", 100, null, 0, null)
            : SubfolderFill.FromOverride(hostSubItem));

        _hostSubItem.PropertyChanged += OnHostPropertyChanged;
        ItemVms.CollectionChanged += OnItemVmsCollectionChanged;

        RebuildItemVms();
    }

    private void OnHostPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        DzTrace.Log($"[SubEdit] FlyoutVM.HostChanged: prop={e.PropertyName} FillFollows={_hostSubItem.FillFollowsZone} Corner={_hostSubItem.CornerRounded} Hover={_hostSubItem.HoverAutoExpand}");
        if (e.PropertyName == nameof(ZoneItem.SubItems))
            RebuildItemVms();
        else if (e.PropertyName == nameof(ZoneItem.GridSize))
        {
            OnPropertyChanged(nameof(GridSize));
            OnPropertyChanged(nameof(CellWidth));
            OnPropertyChanged(nameof(CellHeight));
            OnPropertyChanged(nameof(IconSize));
        }
    }

    private void RebuildItemVms()
    {
        _suppressWriteback = true;
        try
        {
            _vmToSource.Clear();
            ItemVms.Clear();
            foreach (var item in _hostSubItem.SubItems)
            {
                var vm = new ZoneItemViewModel(item, _iconService);
                _vmToSource[vm] = item;
                ItemVms.Add(vm);
            }
        }
        finally { _suppressWriteback = false; }
    }

    private void OnItemVmsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_suppressWriteback) return;
        // 拖拽换位暂存中:只记录"顺序已变",不写回 — 写回会触发 SubItems INPC 重建。
        if (_transientReorder) { _orderDirty = true; return; }
        WriteBackOrder();
    }

    /// <summary>rebuild the source list from the live ItemVms via the VM→source map.
    /// VMs added through raw ItemVms.Add (without going through AddItem) have no
    /// registered source; bailing out keeps the host model consistent rather than
    /// writing a half-populated list.</summary>
    void WriteBackOrder()
    {
        var newSources = new List<ZoneItem>(ItemVms.Count);
        foreach (var vm in ItemVms)
        {
            if (!_vmToSource.TryGetValue(vm, out var src))
                return;
            newSources.Add(src);
        }
        _hostSubItem.SubItems = newSources;
    }

    /// <summary>开始拖拽换位暂存(拖拽开始时调用)。</summary>
    public void BeginTransientReorder() { _transientReorder = true; _orderDirty = false; }

    /// <summary>提交暂存顺序:一次性写回 HostSubItem.SubItems(松手且换位过时调用)。</summary>
    public void CommitTransientReorder()
    {
        _transientReorder = false;
        if (_orderDirty) { _orderDirty = false; WriteBackOrder(); }
    }

    /// <summary>取消暂存:按模型当前顺序重建 ItemVms,还原拖拽期间的临时换位
    /// (拖出主分区/取消拖拽时调用)。</summary>
    public void CancelTransientReorder()
    {
        _transientReorder = false;
        bool dirty = _orderDirty;
        _orderDirty = false;
        if (dirty) RebuildItemVms();
    }

    /// <summary>Public add path used by ZoneWindow drag-drop (Task 6). Registers
    /// the source in <see cref="_vmToSource"/> so the writeback picks it up.</summary>
    public void AddItem(ZoneItem source)
    {
        var vm = new ZoneItemViewModel(source, _iconService);
        _vmToSource[vm] = source;
        ItemVms.Add(vm);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
