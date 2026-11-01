using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using DesktopZones.Models;
using DesktopZones.Services;
using DesktopZones.ViewModels;
using DesktopZones.Views.Components;
using static DesktopZones.Views.Pages.PageHelpers;
using RelayCommand = DesktopZones.ViewModels.RelayCommand;

namespace DesktopZones.Views.Pages;

/// <summary>
/// 「快捷栏」列表页。和 <see cref="ZonesPage"/> 同一套行（<see cref="EditableListRow"/>），
/// 但只列 <c>Zone.DockMode == true</c> 的那些分区，并且：
/// - 副标题标出条体尺寸 + 图标数，形态一眼可见；
/// - 「新建」直接建一条快捷栏（形态创建时定死，列表里不给改）；
/// - 锁定/显示隐藏与分区列表同一套按钮（快捷栏右键菜单里也有「锁定」）。
/// </summary>
public partial class DocksPage : UserControl
{
    readonly ManagementWindow _main;
    readonly ZoneManager _zoneManager;
    Zone? _selected;
    readonly ObservableCollection<EditableListRow> _rows = new();

    public DocksPage(ManagementWindow main, ManagementViewModel vm, ZoneManager zoneManager)
    {
        InitializeComponent();
        _main = main;
        _zoneManager = zoneManager;
        ListHost.ItemsSource = _rows;
        Loaded += (_, _) =>
        {
            _zoneManager.ZonesChanged += RefreshList;
            RefreshList();
        };
        Unloaded += (_, _) => _zoneManager.ZonesChanged -= RefreshList;
    }

    public void ApplyLoc() => RefreshList();

    static bool IsDock(Zone z) => z.DockMode;

    public void RefreshList()
    {
        var loc = LocalizationService.Instance;
        var docks = _zoneManager.Zones.Where(IsDock).ToList();
        CountLabel.Text = $"{docks.Count} {loc["Manage.Count.Unit"]}";
        _rows.Clear();
        foreach (var z in docks) _rows.Add(BuildRow(z));
        EmptyHint.Visibility = docks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SetSelection(ListHost, _selected);
    }

    EditableListRow BuildRow(Zone z)
    {
        var loc = LocalizationService.Instance;
        var row = new EditableListRow
        {
            Tag = z,
            Title = z.Name,
            // 副标题：「快捷栏 · 322×80 · 6 项」——形态 + 尺寸 + 图标数
            Subtitle = $"{loc["Dock.ManageSubtitle"]} · {(int)z.Width}×{(int)z.Height} · {z.Items.Count} {loc["Manage.Count.Unit"]}",
            IsLocked = z.IsLocked,
            IsVisible = z.IsVisible,
        };
        ApplyIcon(row, z.IconChar, "Icon.DockBar");
        // 锁定：与分区列表同一个语义（锁定后条体不再跟手、窗口压到桌面层）。
        row.LockCommand = new RelayCommand(_ => { z.IsLocked = !z.IsLocked; _zoneManager.UpdateZone(z); });
        row.ReorderRequested += (src, targetIdx) =>
        {
            if (src.Tag is not Zone z2) return;
            _zoneManager.MoveZone(z2.Id, ToZoneIndex(targetIdx));
            MoveRow(_rows, src, targetIdx);
        };
        row.VisibilityCommand = new RelayCommand(v =>
        {
            // 与分区的眼睛按钮同一路径：显示 = ShowZone，隐藏 = HideZone（快捷栏有恢复按钮
            // 时收成 36×36 的小方块，没有时直接关窗）；「完全隐藏」只用右键菜单。
            if (z.IsVisible) _zoneManager.HideZone(z.Id); else _zoneManager.ShowZone(z);
        });
        row.DeleteCommand = new RelayCommand(_ => Delete(z));
        row.RenameCommand = new RelayCommand(p =>
        {
            var n = p?.ToString();
            if (!string.IsNullOrEmpty(n))
            {
                z.Name = n;
                _zoneManager.UpdateZone(z);
                RefreshList();
            }
        });
        row.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject src)
            {
                var parent = src;
                while (parent != null && parent is not Button)
                    parent = LogicalTreeHelper.GetParent(parent);
                if (parent is Button) return;
            }
            Select(z);
        };
        return row;
    }

    /// <summary>列表里只有快捷栏，但 <c>Zones</c> 里还混着普通分区 —— 拖动排序要把
    /// 「快捷栏列表里的第 N 位」换算回 <c>Zones</c> 集合里的实际下标。</summary>
    int ToZoneIndex(int dockIndex)
    {
        var docks = _zoneManager.Zones.Where(IsDock).ToList();
        if (docks.Count == 0) return 0;
        dockIndex = Math.Clamp(dockIndex, 0, docks.Count - 1);
        int seen = -1;
        for (int i = 0; i < _zoneManager.Zones.Count; i++)
        {
            if (!IsDock(_zoneManager.Zones[i])) continue;
            seen++;
            if (seen == dockIndex) return i;
        }
        return _zoneManager.Zones.Count - 1;
    }

    void Select(Zone z)
    {
        PropertyWindowManager.Instance.DockTarget(z, _main);
        _selected = z;
        SetSelection(ListHost, z);
    }

    void NewDock_Click(object sender, RoutedEventArgs e)
    {
        var dock = _main.NewDock();
        RefreshList();
        if (dock != null) Select(dock);
    }

    void Delete(Zone z)
    {
        var loc = LocalizationService.Instance;
        var msg = string.Format(loc["Dock.DeleteConfirm"], z.Name);
        if (MessageBox.Show(msg, loc["Manage.Zone.DeleteTitle"],
            MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _zoneManager.DeleteZone(z.Id);
        if (ReferenceEquals(_selected, z)) _selected = null;
        RefreshList();
    }
}
