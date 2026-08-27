using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DesktopZones.Helpers;
using DesktopZones.Models;
using DesktopZones.Services;
using DesktopZones.ViewModels;
using DesktopZones.Views.Components;
using static DesktopZones.Views.Pages.PageHelpers;
using RelayCommand = DesktopZones.ViewModels.RelayCommand;

namespace DesktopZones.Views.Pages;

/// <summary>
/// Clock list page. Renders one EditableListRow per DesktopClock.
/// Subtitle shows mode (Digital/Analog) and 12/24-hour setting.
/// ponytail: live HH:mm:ss in subtitle isn't propagated — rows rebuild on
/// ClocksChanged, which fires on user edits only. A live time read requires
/// a timer; acceptable trade-off, the row preview is informational only.
/// </summary>
public partial class ClockPage : UserControl
{
    readonly ManagementWindow _main;
    readonly WidgetService? _widgetService;
    readonly LocalizationService _loc = LocalizationService.Instance;
    DesktopClock? _selected;
    // ponytail: live row collection bound to ListHost — drag reorder moves rows
    // through this OC (mirroring PropertyTabStrip's Tabs OC) so the ItemsControl
    // shifts live while the model collection moves in parallel for persistence.
    readonly ObservableCollection<EditableListRow> _rows = new();

    public ClockPage(ManagementWindow main, ManagementViewModel vm, WidgetService? widgetService)
    {
        InitializeComponent();
        _main = main;
        _widgetService = widgetService;
        ListHost.ItemsSource = _rows;
        // ponytail 2026-08-25: Persist is wired centrally in ManagementWindow
        // (WirePropertyPanelPersist) — one dispatcher for all target types,
        // docked and floating. Pages no longer overwrite it.
        Loaded += (_, _) =>
        {
            if (_widgetService != null)
                _widgetService.ClocksChanged += RefreshList;
            RefreshList();
        };
        Unloaded += (_, _) =>
        {
            if (_widgetService != null)
                _widgetService.ClocksChanged -= RefreshList;
        };
    }

    public void ApplyLoc() => RefreshList();

    public void RefreshList()
    {
        if (_widgetService == null) return;
        var clocks = _widgetService.Clocks;
        CountLabel.Text = _loc.Get("ClockPage.ItemCount", clocks.Count);
        // 拖动排序即列表顺序：不再重排，Rows 直接镜像 Clocks 的持久化顺序。
        _rows.Clear();
        foreach (var c in clocks) _rows.Add(BuildRow(c));
        EmptyHint.Visibility = clocks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SetSelection(ListHost, _selected);
    }

    EditableListRow BuildRow(DesktopClock clock)
    {
        var mode = clock.Mode == ClockDisplayMode.Digital ? _loc["ClockPage.Digital"] : _loc["ClockPage.Analog"];
        var format = clock.Use24Hour ? _loc["ClockPage.Format24"] : _loc["ClockPage.Format12"];
        var row = new EditableListRow
        {
            Tag = clock,
            Title = $"Clock ({mode})",
            Subtitle = $"{format} · {(int)clock.Width}×{(int)clock.Height}",
            IsLocked = clock.IsLocked,
            IsVisible = clock.IsVisible,
        };
        ApplyIcon(row, clock.IconChar, "Icon.Clock");
        row.ReorderRequested += (src, targetIdx) =>
        {
            if (src.Tag is not DesktopClock c || _widgetService == null) return;
            _widgetService.MoveClock(c.Id, targetIdx);   // 模型 + 持久化
            MoveRow(_rows, src, targetIdx);              // 列表实时换位（镜像标签栏）
        };
        // ponytail: status badge removed (lock/hidden chips no longer shown).
        row.LockCommand = new RelayCommand(_ =>
        {
            clock.IsLocked = !clock.IsLocked;
            _widgetService?.UpdateClock(clock);
            RefreshList();
        });
        row.VisibilityCommand = new RelayCommand(_ =>
        {
            // ponytail: 2026-08-26 — do NOT pre-flip the model here. clock.IsVisible =
            // row.IsVisible + UpdateClock fired ClocksChanged → the ghost-stamp in
            // OnClocksChanged snapped the window to the RestoreButton, so the following
            // toggle saw MainContent.Collapsed and re-SHOWED it — the eye toggle reversed
            // itself. Route straight through ToggleClockWindow; ShowClock/HideClock update
            // the model and refresh the list themselves.
            _main.ToggleClockWindow(clock);
        });
        row.DeleteCommand = new RelayCommand(_ => Delete(clock));
        row.RenameCommand = new RelayCommand(_ => { /* clocks don't have a free-form name */ });
        row.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject src)
            {
                var parent = src;
                while (parent != null && parent is not Button)
                    parent = LogicalTreeHelper.GetParent(parent);
                if (parent is Button) return;
            }
            // ponytail: workspace direction (dock only). Old code also called
            // PropertyWindowService.OpenOrFocus which created a duplicate
            // floating editor while the docked panel already showed this clock.
            Select(clock);
        };
        // 右键菜单已取消：显示/隐藏与删除移到属性面板顶部状态区。
        return row;
    }

    void Select(DesktopClock clock)
    {
        // ponytail: route through DockTarget so any pre-existing floating editor
        // for this clock is closed before the docked panel takes over. Without
        // this, clicking a different clock row would leave the previous
        // floating window open while the docked panel switched targets —
        // duplicate editor for the previously-selected clock.
        PropertyWindowManager.Instance.DockTarget(clock, _main);
        if (!ReferenceEquals(_selected, clock))
        {
            _selected = clock;
            SetSelection(ListHost, clock);
        }
    }

    void NewClock_Click(object sender, RoutedEventArgs e)
    {
        _main.NewClock();
        RefreshList();
    }

    void Delete(DesktopClock clock)
    {
        if (MessageBox.Show(_loc.Get("ClockPage.DeleteConfirm", "Clock"), _loc["ClockPage.DeleteTitle"],
            MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _main.DeleteClock(clock);
        if (ReferenceEquals(_selected, clock)) _selected = null;
        RefreshList();
    }
}
