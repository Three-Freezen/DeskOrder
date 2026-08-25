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

    public ZoneItem HostSubItem => _hostSubItem;

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

    public SubfolderFlyoutViewModel(ZoneItem hostSubItem, ShellIconService iconService)
    {
        _hostSubItem = hostSubItem;
        _iconService = iconService;

        _hostSubItem.PropertyChanged += OnHostPropertyChanged;
        ItemVms.CollectionChanged += OnItemVmsCollectionChanged;

        RebuildItemVms();
    }

    private void OnHostPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
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

        // ponytail: rebuild the source list from the live ItemVms via the VM→source map.
        // VMs added through raw ItemVms.Add (without going through AddItem) have no
        // registered source; bailing out keeps the host model consistent rather than
        // writing a half-populated list.
        var newSources = new List<ZoneItem>(ItemVms.Count);
        foreach (var vm in ItemVms)
        {
            if (!_vmToSource.TryGetValue(vm, out var src))
                return;
            newSources.Add(src);
        }
        _hostSubItem.SubItems = newSources;
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
