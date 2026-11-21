using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopZones.Helpers;
using DesktopZones.Models;
using DesktopZones.Services;

namespace DesktopZones.ViewModels;

public class ZoneViewModel : INotifyPropertyChanged
{
    private readonly ZoneManager _zoneManager;
    private readonly ShellIconService _iconService;
    private Zone _zone;

    public Zone Zone
    {
        get => _zone;
        set { _zone = value; OnPropertyChanged(); }
    }

    public ObservableCollection<ZoneItemViewModel> Items { get; } = new();

    /// <summary>宿主分区窗口的 DPI 缩放（由 ZoneWindow 按窗口实际 DPI 赋值，默认 100%）。
    /// 图标按「显示 DIP × 这个缩放」解码（见 <see cref="ShellIconService.SnapDecodeSize"/>）：
    /// 不知道窗口 DPI 时用 1.0，与系统 DPI 兜底口径一致。
    /// 注意 ZoneWindow 是「先 new VM、再赋 DPI」，所以 setter 里要把已建条目同步过去并让它们
    /// 重新取图标 —— 否则首屏那批 VM 会停在构造时的 1.0 档（高 DPI 下解码偏小、图标略糊）。</summary>
    private double _dpiScale = 1.0;
    public double DpiScale
    {
        get => _dpiScale;
        set
        {
            if (_dpiScale == value || double.IsNaN(value)) return;
            _dpiScale = value;
            foreach (var vm in Items)
            {
                if (vm.IconDpiScale == value) continue;
                vm.IconDpiScale = value;
                vm.RefreshIcon(); // 只是清掉已解析的图标，重新走一次（还没解析时是空操作）
            }
        }
    }

    private bool _isEditing;
    public bool IsEditing
    {
        get => _isEditing;
        set { _isEditing = value; OnPropertyChanged(); OnPropertyChanged(nameof(ControlPointVisible)); }
    }

    // ── Merge state ──
    private Guid? _selectedSubZoneId;
    public Guid? SelectedSubZoneId
    {
        get => _selectedSubZoneId;
        set { _selectedSubZoneId = value; OnPropertyChanged(); RefreshMergedItems(); }
    }

    public bool IsMergedMaster => _zone.MergedGroupMembership.SubZoneIds.Count > 0;

    public bool ControlPointVisible => IsEditing;

    /// <summary>
    /// 锁定状态。**直接代理模型字段**（不再单独存一份）：
    /// 管理界面的行锁按钮走的是 <c>ZoneManager.UpdateZone</c>（不触发 LockChanged），
    /// 以前只更新模型 → 打开着的窗口里那份 VM 会一直是旧值，锁定就"看起来没生效"
    /// （拖得动、图标还能换位）。代理之后两边永远同一个真源。
    /// </summary>
    public bool IsLocked
    {
        get => _zone.IsLocked;
        set
        {
            if (_zone.IsLocked == value) return;
            _zone.IsLocked = value;
            OnPropertyChanged();
            LockChanged?.Invoke(value);
        }
    }

    public event Action<bool>? LockChanged;

    public ICommand ToggleVisibilityCommand { get; }
    public ICommand StartEditCommand { get; }
    public ICommand DeleteItemCommand { get; }

    public ZoneViewModel(Zone zone, ZoneManager zoneManager, ShellIconService iconService)
    {
        _zone = zone;
        _zoneManager = zoneManager;
        _iconService = iconService;

        ToggleVisibilityCommand = new RelayCommand(_ => zoneManager.ToggleZone(zone.Id));
        StartEditCommand = new RelayCommand(_ => IsEditing = !IsEditing);
        DeleteItemCommand = new RelayCommand<ZoneItemViewModel>(DeleteItem);

        RefreshItems();
    }

    public void RefreshZone(Zone zone)
    {
        Zone = zone;
        if (IsMergedMaster)
        {
            _selectedSubZoneId = zone.Id;
            RefreshMergedItems();
        }
        else
        {
            RefreshItems();
        }
    }

    /// <summary>图标随网格大小缩放(ZoneProp.GridSizeHint:"调整网格大小即可调整图标大小")。
    /// 名字行(FontSize 10 ≈13px 行高 + 图标底部 4px 间距)画在格子内部,须预留;
    /// 隐藏应用名时无名字行,图标几乎占满整格。不能封顶 — 封顶 40 会让改网格后图标不再跟随缩放。</summary>
    double CurrentIconSize()
    {
        var reserve = _zone.HideAppName ? 6 : 18;
        return Math.Max(8, _zone.GridSize - reserve);
    }

    public void RefreshItems()
    {
        // ponytail 2026-08-26: merged masters must reload the SELECTED tab's
        // items. Loading _zone.Items unconditionally yanked the view back to
        // the master's icons on every ZonesChanged (icon add/delete, group
        // settings save) — "删除/增添图标后自动跳到标题栏的第一个分区".
        if (IsMergedMaster)
        {
            RefreshMergedItems();
            return;
        }
        SyncItems(_zone.Items, _zone.Id);
    }

    /// <summary>Load items from all merged sub-zones (filtered by selected tab).</summary>
    public void RefreshMergedItems()
    {
        Guid targetId = _selectedSubZoneId ?? _zone.Id;

        if (targetId == _zone.Id)
        {
            SyncItems(_zone.Items, _zone.Id);
        }
        else
        {
            var subZone = _zoneManager.Zones.FirstOrDefault(z => z.Id == targetId);
            SyncItems(subZone != null ? subZone.Items : (IReadOnlyList<ZoneItem>)Array.Empty<ZoneItem>(), targetId);
        }

        OnPropertyChanged(nameof(IsMergedMaster));
    }

    /// <summary>
    /// 增量同步 <see cref="Items"/>：按 <see cref="ZoneItem.Id"/> 复用已有 VM，只对真正
    /// 变化的条目做 Remove/Insert/Move。
    ///
    /// ponytail 2026-11-14(审计修订 P0-3): 原实现是 `Items.Clear()` + 逐条 new ——
    /// 每次 ZonesChanged（任意图标增删/改名/样式应用/自动整理导入都会触发）都让分区窗口的
    /// 非虚拟化 ItemsControl 把所有容器销毁重建，代价 O(图标数)（见 OnZonesChanged 调用链）。
    /// 现在只有新增/删除/顺序变化才动集合，未变化的条目连 PropertyChanged 都不发。
    /// 语义保持：复用 VM 也重新解析一次图标（等价于旧实现"重建 VM → 图标重新走 GetIcon"），
    /// 选中态按旧行为清空（旧实现重建 VM 相当于清空）。
    /// </summary>
    void SyncItems(IReadOnlyList<ZoneItem> source, Guid sourceZoneId)
    {
        double isize = CurrentIconSize();
        var byId = new Dictionary<Guid, ZoneItemViewModel>(Items.Count);
        foreach (var vm in Items) byId[vm.Id] = vm;

        var desired = new List<ZoneItemViewModel>(source.Count);
        foreach (var item in source)
        {
            ZoneItemViewModel? vm = null;
            if (byId.TryGetValue(item.Id, out var existing))
            {
                byId.Remove(item.Id);
                if (ReferenceEquals(existing.Source, item)) vm = existing;
            }
            if (vm == null)
            {
                vm = new ZoneItemViewModel(item, _iconService);
                Items.Add(vm);   // 先占位，下面统一按顺序 Move
            }
            if (vm.IconSize != isize) vm.IconSize = isize;
            if (vm.ItemSize != _zone.GridSize) vm.ItemSize = _zone.GridSize;
            if (vm.ItemHeight != _zone.GridSize) vm.ItemHeight = _zone.GridSize;
            if (vm.IconDpiScale != DpiScale) vm.IconDpiScale = DpiScale;
            if (vm.SourceZoneId != sourceZoneId) vm.SourceZoneId = sourceZoneId;

            // 旧实现重建 VM = 选中态清零 + 图标缓存失效；复用路径显式补上这两点。
            if (vm.IsSelected) vm.IsSelected = false;
            vm.RefreshIcon();

            desired.Add(vm);
        }

        // 删除已不存在的条目（倒序，索引稳定）
        for (int i = Items.Count - 1; i >= 0; i--)
        {
            if (!desired.Contains(Items[i]))
            {
                Items[i].CancelIconRetryForRemoval();   // ponytail 2026-11-14: 别让被丢弃的 VM 继续挂着图标重试计时器
                Items.RemoveAt(i);
            }
        }

        // 校正顺序（只有顺序真变了才 Move；bubble 式比较下界）
        for (int i = 0; i < desired.Count; i++)
        {
            if (!ReferenceEquals(Items[i], desired[i]))
            {
                int cur = Items.IndexOf(desired[i]);
                if (cur > i) Items.Move(cur, i);
            }
        }
    }

    public void AddItem(ZoneItem item)
    {
        // If merged and a sub-zone tab is selected, add to that sub-zone
        if (_selectedSubZoneId.HasValue && _selectedSubZoneId.Value != _zone.Id)
        {
            var targetZone = _zoneManager.Zones.FirstOrDefault(z => z.Id == _selectedSubZoneId.Value);
            if (targetZone != null)
            {
                targetZone.Items.Add(item);
                _zoneManager.SaveConfig();
                _zoneManager.NotifyChanged();
                RefreshMergedItems();
                return;
            }
        }

        _zone.Items.Add(item);
        var vm = new ZoneItemViewModel(item, _iconService) { IconSize = CurrentIconSize(), ItemSize = _zone.GridSize, ItemHeight = _zone.GridSize, IconDpiScale = DpiScale, SourceZoneId = _zone.Id };
        Items.Add(vm);
        _zoneManager.SaveConfig();
        _zoneManager.NotifyChanged();
    }

    /// <summary>The item list AddItem actually writes to (sub-zone when a tab is selected in merged mode).</summary>
    public IReadOnlyList<ZoneItem> GetPlacementItems()
    {
        if (_selectedSubZoneId.HasValue && _selectedSubZoneId.Value != _zone.Id)
        {
            var targetZone = _zoneManager.Zones.FirstOrDefault(z => z.Id == _selectedSubZoneId.Value);
            if (targetZone != null) return targetZone.Items;
        }
        return _zone.Items;
    }

    public void RemoveItem(ZoneItem item)
    {
        _zone.Items.Remove(item);
        var vm = FindItemVm(item.Id);
        if (vm != null)
            Items.Remove(vm);
        // 删除内层组件(含次级文件夹)后同步关闭其样式设置界面,避免残留编辑器。
        PropertyWindowService.CloseEditorsFor(item);
        _zoneManager.SaveConfig();
        _zoneManager.NotifyChanged();
    }

    public void MoveItem(Guid itemId, double newX, double newY, bool snapToGrid = true)
    {
        // Search across all merged zones
        ZoneItem? item = _zone.Items.Find(i => i.Id == itemId);
        if (item == null && _zone.MergedGroupMembership.SubZoneIds.Count > 0)
        {
            foreach (var subId in _zone.MergedGroupMembership.SubZoneIds)
            {
                var sub = _zoneManager.Zones.FirstOrDefault(z => z.Id == subId);
                if (sub != null)
                {
                    item = sub.Items.Find(i => i.Id == itemId);
                    if (item != null) break;
                }
            }
        }
        if (item == null) return;

        if (snapToGrid && _zone.SnapToGrid)
        {
            item.X = SnapToGrid(newX, _zone.GridSize);
            item.Y = SnapToGridY(newY, _zone.GridSize);
        }
        else
        {
            item.X = newX;
            item.Y = newY;
        }

        var vm = FindItemVm(itemId);
        if (vm != null)
        {
            vm.X = item.X;
            vm.Y = item.Y;
        }
        _zoneManager.SaveConfig();
    }

    private ZoneItemViewModel? FindItemVm(Guid itemId)
    {
        foreach (var vm in Items)
        {
            if (vm.Id == itemId) return vm;
        }
        return null;
    }

    public static double SnapToGrid(double value, int gridSize) => ZoneLayout.Snap(value, gridSize);

    /// <summary>Vertical grid snap — the vertical pitch includes the name area below the icon.</summary>
    public static double SnapToGridY(double value, int gridSize) => ZoneLayout.SnapY(value, gridSize);

    private void DeleteItem(ZoneItemViewModel? itemVm)
    {
        if (itemVm == null) return;

        // Find which zone owns this item
        Zone? ownerZone = _zone;
        if (itemVm.SourceZoneId != Guid.Empty && itemVm.SourceZoneId != _zone.Id)
            ownerZone = _zoneManager.Zones.FirstOrDefault(z => z.Id == itemVm.SourceZoneId);

        if (ownerZone == null) return;

        var item = ownerZone.Items.Find(i => i.Id == itemVm.Id);
        if (item != null)
        {
            ownerZone.Items.Remove(item);
            var vm = FindItemVm(item.Id);
            if (vm != null) Items.Remove(vm);
            // 删除内层组件(含次级文件夹)后同步关闭其样式设置界面,避免残留编辑器。
            PropertyWindowService.CloseEditorsFor(item);
            _zoneManager.SaveConfig();
            _zoneManager.NotifyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>快捷栏条目的运行状态（Windows 任务栏那套）：没在跑 / 在后台 / 是当前前台。</summary>
public enum DockRunState
{
    None = 0,
    Background = 1,
    Foreground = 2,
}

public class ZoneItemViewModel : INotifyPropertyChanged
{
    private readonly ZoneItem _item;
    private readonly ShellIconService _iconService;

    private DockRunState _runState = DockRunState.None;
    /// <summary>由 <see cref="Views.ZoneWindow"/> 的轮询写入；模板/代码据此画灰/蓝短线。</summary>
    public DockRunState RunState
    {
        get => _runState;
        set
        {
            if (_runState == value) return;
            _runState = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(IsFocusedApp));
        }
    }
    public bool IsRunning => _runState != DockRunState.None;
    public bool IsFocusedApp => _runState == DockRunState.Foreground;

    /// <summary>ponytail 2026-08-26: expose the underlying ZoneItem so the SubFolder
    /// template selector + SubfolderItemView wiring can unwrap the VM (ItemsControl
    /// binds to ObservableCollection&lt;ZoneItemViewModel&gt;, but the SubFolder
    /// rendering needs the raw model for SubItems / CellLayout derivation).</summary>
    public ZoneItem Source => _item;

    public Guid Id => _item.Id;
    public string Name
    {
        get => _item.Name;
        set { _item.Name = value; OnPropertyChanged(); }
    }
    public string TargetPath => _item.TargetPath;
    public ItemType Type => _item.Type;

    /// <summary>Custom shortcut icon location ("file,index") when the desktop shortcut had one.</summary>
    public string? IconPath => _item.IconPath;

    public double X
    {
        get => _item.X;
        set { _item.X = value; OnPropertyChanged(); }
    }
    public double Y
    {
        get => _item.Y;
        set { _item.Y = value; OnPropertyChanged(); }
    }

    private double _iconSize = 40;
    public double IconSize
    {
        get => _iconSize;
        set { _iconSize = value; OnPropertyChanged(); }
    }

    private double _itemSize = 56;
    public double ItemSize
    {
        get => _itemSize;
        set { _itemSize = value; OnPropertyChanged(); }
    }

    /// <summary>Name label max width — mirrors the panel card ratio (panel name is
    /// 72px in an 80px card = 90% of the cell width).</summary>
    public double NameMaxWidth => Math.Max(32, ItemSize * 0.9);

    private double _itemHeight = 80;
    /// <summary>Cell height = grid size (square 80×80 cell; name sits inside, like the panel card).</summary>
    public double ItemHeight
    {
        get => _itemHeight;
        set { _itemHeight = value; OnPropertyChanged(); }
    }

    /// <summary>图标解码用的 DPI 缩放（容器 <see cref="ZoneViewModel.DpiScale"/> 在创建/同步条目时带过来）。
    /// ItemSize 是 DIP，解码边长 = ItemSize × 这个缩放，再取 <see cref="ShellIconService.SnapDecodeSize"/> 的档位。</summary>
    public double IconDpiScale { get; set; } = 1.0;

    private ImageSource? _icon;
    private DispatcherTimer? _iconRetryTimer;
    private int _iconRetryCount;
    private const int IconRetryMax = 10;
    private static readonly TimeSpan IconRetryInterval = TimeSpan.FromMilliseconds(600);

    public ImageSource? Icon
    {
        get
        {
            if (_icon == null && _iconRetryTimer == null)
            {
                // 按实际显示尺寸解码：ItemSize（= 分区网格边长）× DPI 向上取到 48/64/96/128/192/256 档。
                // 不再一律解 256px —— 一个 65px 的格子存一张 256 的图纯属浪费（内存 ~7 倍、解码也慢）。
                int decodeSize = ShellIconService.SnapDecodeSize(ItemSize, IconDpiScale);
                // 分区图片预览：图片文件显示内容缩略图而非默认文件图标。
                if (ShellIconService.IsImageFile(TargetPath) && ShellIconService.ImagePreviewEnabled)
                {
                    _icon = _iconService.GetImageThumbnail(TargetPath, decodeSize);
                }
                _icon ??= _iconService.GetIcon(TargetPath, Type, IconPath, decodeSize);
                // 文件刚创建仍在写入/被占用时 shell 解析失败：定时重试直到成功
                //（监听导入时文件尚未写完 → 图标空白的修复）。重试进行中不再重复解析，
                // 由定时器统一重试。
                if (_icon == null) ScheduleIconRetry();
            }
            return _icon;
        }
        set
        {
            _icon = value;
            CancelIconRetry();
            OnPropertyChanged();
        }
    }

    /// <summary>Drop the cached icon so the next Icon read re-resolves it (recycle-bin state changes).</summary>
    public void RefreshIcon()
    {
        _iconRetryCount = 0;
        CancelIconRetry();
        _icon = null;
        OnPropertyChanged(nameof(Icon));
    }

    void ScheduleIconRetry()
    {
        if (_iconRetryTimer != null || _iconRetryCount >= IconRetryMax) return;
        // 目标已不存在（shell 虚拟项除外）时重试无意义。
        if (Type != ItemType.ShellLocation
            && !File.Exists(TargetPath) && !Directory.Exists(TargetPath))
            return;

        _iconRetryTimer = new DispatcherTimer { Interval = IconRetryInterval };
        _iconRetryTimer.Tick += (_, _) =>
        {
            _iconRetryTimer.Stop();
            _iconRetryTimer = null;
            _iconRetryCount++;
            int decodeSize = ShellIconService.SnapDecodeSize(ItemSize, IconDpiScale);
            var src = _iconService.GetIcon(TargetPath, Type, IconPath, decodeSize);
            if (src != null)
            {
                _icon = src;
                OnPropertyChanged(nameof(Icon));
            }
            else if (_iconRetryCount < IconRetryMax)
            {
                ScheduleIconRetry();
            }
        };
        _iconRetryTimer.Start();
    }

    void CancelIconRetry()
    {
        _iconRetryTimer?.Stop();
        _iconRetryTimer = null;
    }

    /// <summary>VM 被 <see cref="ZoneViewModel"/> 丢弃前调用：停掉图标重试计时器。
    /// ponytail 2026-11-14(审计修订 P2): 原来 RefreshItems 直接 Clear，旧 VM 的
    /// 600ms×10 重试计时器会继续跑（每个都是一次 shell 图标解析）。</summary>
    internal void CancelIconRetryForRemoval() => CancelIconRetry();

    /// <summary>Which zone this item belongs to (for merged views).</summary>
    public Guid SourceZoneId { get; set; }

    private bool _isSelected;
    /// <summary>Marquee / Ctrl+click multi-selection state (visual only — the
    /// underlying model is untouched; cleared when items refresh).</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public ZoneItemViewModel(ZoneItem item, ShellIconService iconService)
    {
        _item = item;
        _iconService = iconService;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
