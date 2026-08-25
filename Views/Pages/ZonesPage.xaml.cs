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
/// Zones list page. Renders one <see cref="EditableListRow"/> per zone; clicking a
/// row sets the docked PropertyPanel.Target to that zone so the field tree rebuilds,
/// and the row itself lights up via IsSelected (set via PageHelpers.SetSelection).
/// Right-click opens a contextual menu (show/hide/lock/group/delete) so the three
/// hover-ops buttons no longer carry the whole interaction.
/// ponytail: row rendering rebuilds the ItemsControl each refresh — list is small,
/// no virtualization needed; upgrade to ObservableCollection&lt;T&gt; + Diff when
/// count crosses ~50.
/// </summary>
public partial class ZonesPage : UserControl
{
    readonly ManagementWindow _main;
    readonly ZoneManager _zoneManager;
    Zone? _selected; // ponytail: row-level selection mirror (also pushed to DockedPanel.Target).
    // ponytail: live row collection bound to ListHost — drag reorder moves rows
    // through this OC (mirroring PropertyTabStrip's Tabs OC) so the ItemsControl
    // shifts live while the model collection moves in parallel for persistence.
    readonly ObservableCollection<EditableListRow> _rows = new();

    public ZonesPage(ManagementWindow main, ManagementViewModel vm, ZoneManager zoneManager)
    {
        InitializeComponent();
        _main = main;
        _zoneManager = zoneManager;
        ListHost.ItemsSource = _rows;
        // ponytail 2026-08-25: Persist is wired centrally in ManagementWindow
        // (WirePropertyPanelPersist, which also pins the docked tab on edit).
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
        var loc = LocalizationService.Instance;
        var zones = _zoneManager.Zones;
        CountLabel.Text = $"{zones.Count} {loc["Manage.Count.Unit"]}";
        // 拖动排序即列表顺序：不再按名称/数量重排，Rows 直接镜像 Zones 的持久化顺序。
        _rows.Clear();
        foreach (var z in zones) _rows.Add(BuildRow(z));
        EmptyHint.Visibility = zones.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SetSelection(ListHost, _selected); // re-apply after rebuild (rename / lock toggle).
    }

    EditableListRow BuildRow(Zone z)
    {
        var loc = LocalizationService.Instance;
        var row = new EditableListRow
        {
            Tag = z,
            Title = z.Name,
            Subtitle = $"{(int)z.Width}×{(int)z.Height} · {z.Items.Count} {loc["Manage.Count.Unit"]}",
            IconKey = "Icon.Zones",
            IconText = z.IconChar ?? "",
            IsLocked = z.IsLocked,
            IsVisible = z.IsVisible,
        };
        // ponytail 2026-08-25: long-press drag reorder — model Move persists the
        // collection order; MoveRow shifts the bound row OC so the ItemsControl
        // reorders live mid-drag (same live-shift shape as PropertyTabStrip).
        row.ReorderRequested += (src, targetIdx) =>
        {
            if (src.Tag is not Zone z2) return;
            _zoneManager.MoveZone(z2.Id, targetIdx);
            MoveRow(_rows, src, targetIdx);
        };
        ApplyStatusBadge(row, z);

        row.LockCommand = new RelayCommand(_ => { z.IsLocked = !z.IsLocked; _zoneManager.UpdateZone(z); });
        row.VisibilityCommand = new RelayCommand(v =>
        {
            z.IsVisible = row.IsVisible;
            if (z.IsVisible) _zoneManager.ShowZone(z); else _zoneManager.HideZone(z.Id);
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
            // Skip clicks that land on a row action button (lock/eye/trash).
            if (e.OriginalSource is DependencyObject src)
            {
                var parent = src;
                while (parent != null && parent is not Button)
                    parent = LogicalTreeHelper.GetParent(parent);
                if (parent is Button) return;
            }
            Select(z);
        };
        row.PreviewMouseRightButtonUp += (_, e) =>
        {
            Select(z);
            ShowZoneContextMenu(z, row);
        };
        return row;
    }

    void Select(Zone z)
    {
        // ponytail: workspace direction — dock only. DockTarget closes any floating
        // editor for the same target so we never have both editors showing the
        // same zone at once (see PropertyWindowManager.DockTarget). Old code did
        // _main.DockedPanel.Target = z unconditionally and let the row-click
        // also call PropertyWindowService.OpenOrFocus separately, which produced
        // the duplicate-editor bug.
        PropertyWindowManager.Instance.DockTarget(z, _main);
        _selected = z;
        SetSelection(ListHost, z);
    }

    static void ApplyStatusBadge(EditableListRow row, Zone z)
    {
        var loc = LocalizationService.Instance;
        // ponytail: one badge per row; locked beats hidden, hidden beats merged (most actionable first).
        if (z.IsLocked)
        {
            row.HasStatusBadge = true; row.StatusBadge = loc["Manage.Status.Locked"];
            row.StatusBadgeBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xC1, 0x07));
        }
        else if (!z.IsVisible)
        {
            row.HasStatusBadge = true; row.StatusBadge = loc["Manage.Status.Hidden"];
            row.StatusBadgeBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xA0, 0xA0, 0xC0));
        }
        else if (z.MergedGroupMembership.SubZoneIds.Count > 0)
        {
            row.HasStatusBadge = true;
            row.StatusBadge = string.Format(loc["Manage.Status.Merged"], z.MergedGroupMembership.SubZoneIds.Count + 1);
            row.StatusBadgeBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x7C, 0x3A, 0xED));
        }
        else
        {
            row.HasStatusBadge = false;
        }
    }

    void ShowZoneContextMenu(Zone z, EditableListRow row)
    {
        var loc = LocalizationService.Instance;
        var items = new List<RowContextMenu.Item>
        {
            new(z.IsVisible ? loc["Manage.Zone.Hide"] : loc["Manage.Zone.Show"], () =>
            {
                if (z.IsVisible) { z.IsVisible = false; _zoneManager.HideZone(z.Id); }
                else { z.IsVisible = true; _zoneManager.ShowZone(z); }
                RefreshList();
            }),
            new(z.IsLocked ? loc["Manage.Zone.Unlock"] : loc["Manage.Zone.Lock"], () =>
            {
                z.IsLocked = !z.IsLocked; _zoneManager.UpdateZone(z); RefreshList();
            }),
        };
        var otherGroups = _zoneManager.Zones.Any(o => o.MergedGroupMembership.SubZoneIds.Count > 0 && o.Id != z.Id);
        if (otherGroups) items.Add(new(loc["Manage.Zone.AddToMerge"], () => _main.ShowMergeDialog(z)));
        items.Add(new(loc["Manage.Zone.Delete"], () => Delete(z), Danger: true));
        RowContextMenu.Show(row, items);
    }

    void NewZone_Click(object sender, RoutedEventArgs e)
    {
        _main.NewZone();
        RefreshList();
        var last = _zoneManager.Zones.LastOrDefault();
        if (last != null) Select(last);
    }

    void Delete(Zone z)
    {
        var loc = LocalizationService.Instance;
        var msg = string.Format(loc["Manage.Zone.DeleteConfirm"], z.Name);
        if (MessageBox.Show(msg, loc["Manage.Zone.DeleteTitle"],
            MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _zoneManager.DeleteZone(z.Id);
        if (ReferenceEquals(_selected, z)) _selected = null;
        RefreshList();
    }
}
