using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DesktopZones.Models;
using DesktopZones.Services;
using DesktopZones.ViewModels;
using DesktopZones.Views.Components;
using static DesktopZones.Views.Pages.PageHelpers;
using RelayCommand = DesktopZones.ViewModels.RelayCommand;

namespace DesktopZones.Views.Pages;

/// <summary>
/// Merged-groups list page. Renders one EditableListRow per merged-group master
/// (a Zone whose MergedSubZoneIds is non-empty). Click → pushes the master to
/// PropertyPanel.Target. Right-click → group-specific menu (disband single /
/// disband all / add zone).
/// ponytail: master = zone with sub-zone ids; sub-zones are hidden behind the master.
/// </summary>
public partial class MergedGroupsPage : UserControl
{
    readonly ManagementWindow _main;
    readonly ZoneManager _zoneManager;
    readonly LocalizationService _loc = LocalizationService.Instance;
    Zone? _selected;
    // ponytail: live row collection bound to ListHost — drag reorder moves rows
    // through this OC (mirroring PropertyTabStrip's Tabs OC) so the ItemsControl
    // shifts live while ZoneManager.MoveMergedGroupMaster persists the model order.
    readonly ObservableCollection<EditableListRow> _rows = new();

    public MergedGroupsPage(ManagementWindow main, ManagementViewModel vm, ZoneManager zoneManager)
    {
        InitializeComponent();
        _main = main;
        _zoneManager = zoneManager;
        ListHost.ItemsSource = _rows;
        // ponytail 2026-08-25: Persist is wired centrally in ManagementWindow
        // (WirePropertyPanelPersist) — one dispatcher for all target types.
        // Pages no longer overwrite it.
        Loaded += (_, _) =>
        {
            _zoneManager.ZonesChanged += RefreshList;
            RefreshList();
        };
        Unloaded += (_, _) => _zoneManager.ZonesChanged -= RefreshList;
    }

    public void ApplyLoc() => RefreshList();

    public void RefreshList()
    {
        var masters = _zoneManager.Zones.Where(z => z.MergedGroupMembership.SubZoneIds.Count > 0).ToList();
        CountLabel.Text = _loc.Get("MergedGroupsPage.Subtitle", masters.Count, _zoneManager.Zones.Count);
        // 拖动排序即列表顺序：主分区按 Zones 集合中的持久化相对顺序展示。
        _rows.Clear();
        foreach (var m in masters) _rows.Add(BuildRow(m));
        EmptyHint.Visibility = masters.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SetSelection(ListHost, _selected);
    }

    EditableListRow BuildRow(Zone master)
    {
        var subCount = master.MergedGroupMembership.SubZoneIds.Count + 1;
        var row = new EditableListRow
        {
            Tag = master,
            Title = master.MergedGroupMembership.DisplayName,
            Subtitle = _loc.Get("MergedGroupsPage.Subtitle", subCount, master.Items.Count),
            IsLocked = master.IsLocked,
            IsVisible = master.IsVisible,
        };
        ApplyIcon(row, master.MergedGroupMembership.Icon, "Icon.Merged");
        // ponytail: no status badge on merged-group rows (lock/hidden/merged chips
        // removed; folder-mapping badge intentionally NOT shown here).

        // ponytail: long-press drag reorder — masters are a FILTERED view of
        // Zones, so the model move goes through MoveMergedGroupMaster (reorders
        // the full collection so the masters' relative order persists), while
        // MoveRow shifts the bound row OC for the live visual reorder.
        row.ReorderRequested += (src, targetIdx) =>
        {
            if (src.Tag is not Zone m) return;
            _zoneManager.MoveMergedGroupMaster(m.Id, targetIdx);
            MoveRow(_rows, src, targetIdx);
        };

        row.LockCommand = new RelayCommand(_ => { master.IsLocked = !master.IsLocked; _zoneManager.UpdateZone(master); });
        row.VisibilityCommand = new RelayCommand(v =>
        {
            // ponytail 2026-08-26: no model pre-flip — same code as the zone's own
            // hide button (see ZonesPage).
            if (master.IsVisible) _zoneManager.HideZone(master.Id); else _zoneManager.ShowZone(master);
        });
        row.DeleteCommand = new RelayCommand(_ => Delete(master));
        row.RenameCommand = new RelayCommand(p =>
        {
            var n = p?.ToString();
            if (!string.IsNullOrEmpty(n))
            {
                master.MergedGroupMembership.DisplayName = n;
                _zoneManager.UpdateZone(master);
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
            Select(master);
        };
        // 右键菜单已取消：显示/锁定/分离单个/解散全部移到属性面板顶部状态区。
        return row;
    }

    void Select(Zone master)
    {
        if (!ReferenceEquals(_selected, master))
        {
            _selected = master;
            SetSelection(ListHost, master);
        }
        // ponytail 2026-08-26: row click opens the standalone merged-group
        // editor (group style + membership), not the per-zone editor. Route
        // through DockTarget so a collapsed right workspace auto-opens the
        // style panel (EnsurePropertyPanelVisible) and the tab strip stays in
        // sync — direct DockedPanel.Target assignment skipped both.
        var target = MergedGroupTarget.For(master);
        PropertyWindowManager.Instance.DockTarget(target, _main);
    }

    void NewGroup_Click(object sender, RoutedEventArgs e)
    {
        // ponytail: 右上角「新建」改为打开「选择要合并的分区」二级窗口，勾选后创建新组合。
        var master = _main.ShowCreateMergedGroupDialog();
        RefreshList();
        if (master != null) Select(master);
    }

    void Delete(Zone master)
    {
        if (MessageBox.Show(_loc.Get("MergePage.DisbandConfirm", master.MergedGroupMembership.DisplayName), _loc["MergePage.DisbandTitle"],
            MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _main.DisbandEntireGroup(master);
        if (ReferenceEquals(_selected, master)) _selected = null;
        RefreshList();
    }
}
