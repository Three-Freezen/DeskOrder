using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media;
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

    private bool _isLocked;
    public bool IsLocked
    {
        get => _isLocked;
        set
        {
            if (_isLocked != value)
            {
                _isLocked = value;
                LockChanged?.Invoke(value);
            }
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
        Items.Clear();
        double isize = Math.Max(24, _zone.GridSize - 4);
        foreach (var item in _zone.Items)
        {
            var vm = new ZoneItemViewModel(item, _iconService) { IconSize = isize, ItemSize = _zone.GridSize, ItemHeight = _zone.GridSize + ZoneLayout.LabelArea, SourceZoneId = _zone.Id };
            Items.Add(vm);
        }
    }

    /// <summary>Load items from all merged sub-zones (filtered by selected tab).</summary>
    public void RefreshMergedItems()
    {
        Items.Clear();
        double isize = Math.Max(24, _zone.GridSize - 4);

        // Determine which zone's items to show
        Guid targetId = _selectedSubZoneId ?? _zone.Id;

        if (targetId == _zone.Id)
        {
            foreach (var item in _zone.Items)
                Items.Add(new ZoneItemViewModel(item, _iconService) { IconSize = isize, ItemSize = _zone.GridSize, ItemHeight = _zone.GridSize + ZoneLayout.LabelArea, SourceZoneId = _zone.Id });
        }
        else
        {
            var subZone = _zoneManager.Zones.FirstOrDefault(z => z.Id == targetId);
            if (subZone != null)
            {
                foreach (var item in subZone.Items)
                    Items.Add(new ZoneItemViewModel(item, _iconService) { IconSize = isize, ItemSize = _zone.GridSize, ItemHeight = _zone.GridSize + ZoneLayout.LabelArea, SourceZoneId = targetId });
            }
        }

        OnPropertyChanged(nameof(IsMergedMaster));
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
        var vm = new ZoneItemViewModel(item, _iconService) { IconSize = Math.Max(24, _zone.GridSize - 4), ItemSize = _zone.GridSize, ItemHeight = _zone.GridSize + ZoneLayout.LabelArea, SourceZoneId = _zone.Id };
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

public class ZoneItemViewModel : INotifyPropertyChanged
{
    private readonly ZoneItem _item;
    private readonly ShellIconService _iconService;

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

    private double _iconSize = 52;
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

    private double _itemHeight = 72;
    /// <summary>Cell height = grid size + the name area below the icon (Windows-native icon style).</summary>
    public double ItemHeight
    {
        get => _itemHeight;
        set { _itemHeight = value; OnPropertyChanged(); }
    }

    private ImageSource? _icon;
    public ImageSource? Icon
    {
        get => _icon ??= _iconService.GetIcon(TargetPath, Type, IconPath);
        set { _icon = value; OnPropertyChanged(); }
    }

    /// <summary>Drop the cached icon so the next Icon read re-resolves it (recycle-bin state changes).</summary>
    public void RefreshIcon()
    {
        _icon = null;
        OnPropertyChanged(nameof(Icon));
    }

    /// <summary>Which zone this item belongs to (for merged views).</summary>
    public Guid SourceZoneId { get; set; }

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
