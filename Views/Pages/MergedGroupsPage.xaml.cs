using System;
using System.Collections.Generic;
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
    Zone? _selected;

    public MergedGroupsPage(ManagementWindow main, ManagementViewModel vm, ZoneManager zoneManager)
    {
        InitializeComponent();
        _main = main;
        _zoneManager = zoneManager;
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
        CountLabel.Text = $"{masters.Count} 个分区 · {_zoneManager.Zones.Count} 项";
        IEnumerable<Zone> sorted = _sortMode switch
        {
            1 => masters.OrderByDescending(m => m.MergedGroupMembership.SubZoneIds.Count).ThenBy(m => m.MergedGroupMembership.DisplayName),
            _ => masters.OrderBy(m => m.MergedGroupMembership.DisplayName, StringComparer.Ordinal),
        };
        ListHost.ItemsSource = sorted.Select(BuildRow).ToList();
        EmptyHint.Visibility = masters.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SetSelection(ListHost, _selected);
        SortBtn.Content = $"⇅ {SortLabels[_sortMode]}";
    }

    void SortBtn_Click(object sender, RoutedEventArgs e) =>
        ShowSortMenu(SortBtn, SortLabels, _sortMode, i => { _sortMode = i; RefreshList(); });

    static readonly string[] SortLabels = { "名称", "子分区数" };
    int _sortMode;

    EditableListRow BuildRow(Zone master)
    {
        var subCount = master.MergedGroupMembership.SubZoneIds.Count + 1;
        var row = new EditableListRow
        {
            Tag = master,
            Title = master.MergedGroupMembership.DisplayName,
            Subtitle = $"{subCount} 个分区 · {master.Items.Count} 项",
            IconKey = "Icon.Merged",
            IconText = master.MergedGroupMembership.Icon ?? "",
            IsLocked = master.IsLocked,
            IsVisible = master.IsVisible,
        };
        ApplyStatusBadge(row, master);

        row.LockCommand = new RelayCommand(_ => { master.IsLocked = !master.IsLocked; _zoneManager.UpdateZone(master); });
        row.VisibilityCommand = new RelayCommand(v =>
        {
            master.IsVisible = row.IsVisible;
            if (master.IsVisible) _zoneManager.ShowZone(master); else _zoneManager.HideZone(master.Id);
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
        row.PreviewMouseRightButtonUp += (_, e) =>
        {
            Select(master);
            ShowGroupContextMenu(master, row);
        };
        return row;
    }

    void Select(Zone master)
    {
        if (!ReferenceEquals(_selected, master))
        {
            _selected = master;
            SetSelection(ListHost, master);
        }
        if (_main.DockedPanel != null) _main.DockedPanel.Target = master;
    }

    static void ApplyStatusBadge(EditableListRow row, Zone master)
    {
        if (master.IsLocked)
        {
            row.HasStatusBadge = true; row.StatusBadge = "已锁定";
            row.StatusBadgeBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xC1, 0x07));
        }
        else if (!master.IsVisible)
        {
            row.HasStatusBadge = true; row.StatusBadge = "已隐藏";
            row.StatusBadgeBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xA0, 0xA0, 0xC0));
        }
        else
        {
            row.HasStatusBadge = true; row.StatusBadge = $"{master.MergedGroupMembership.SubZoneIds.Count + 1} 个子分区";
            row.StatusBadgeBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x7C, 0x3A, 0xED));
        }
    }

    void ShowGroupContextMenu(Zone master, EditableListRow row)
    {
        var items = new List<RowContextMenu.Item>
        {
            new(master.IsVisible ? "隐藏" : "显示", () =>
            {
                if (master.IsVisible) { master.IsVisible = false; _zoneManager.HideZone(master.Id); }
                else { master.IsVisible = true; _zoneManager.ShowZone(master); }
                RefreshList();
            }),
            new(master.IsLocked ? "解锁" : "锁定", () =>
            {
                master.IsLocked = !master.IsLocked; _zoneManager.UpdateZone(master); RefreshList();
            }),
        };
        if (master.MergedGroupMembership.SubZoneIds.Count > 0)
            items.Add(new("分离单个分区", () => _main.DisbandSingleZone(master)));
        items.Add(new("解散组合分区", () => Delete(master), Danger: true));
        RowContextMenu.Show(row, items);
    }

    void NewGroup_Click(object sender, RoutedEventArgs e)
    {
        _main.NewZone();
        RefreshList();
        var last = _zoneManager.Zones.LastOrDefault();
        if (last != null) Select(last);
    }

    void Delete(Zone master)
    {
        if (MessageBox.Show($"解散组合「{master.MergedGroupMembership.DisplayName}」？", "解散组合",
            MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _main.DisbandEntireGroup(master);
        if (ReferenceEquals(_selected, master)) _selected = null;
        RefreshList();
    }
}
