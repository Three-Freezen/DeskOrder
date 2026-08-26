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
/// Calendar list page. Renders one EditableListRow per DesktopCalendar instance
/// from the WidgetService. Click → opens floating PropertyWindow via OpenOrFocus.
/// ponytail: the row's visibility toggle closes/opens the calendar window;
/// service keeps CalendarWindows so we don't recreate on every show.
/// </summary>
public partial class CalendarPage : UserControl
{
    readonly ManagementWindow _main;
    readonly WidgetService? _widgetService;
    readonly LocalizationService _loc = LocalizationService.Instance;
    DesktopCalendar? _selected;
    // ponytail: live row collection bound to ListHost — drag reorder moves rows
    // through this OC (mirroring PropertyTabStrip's Tabs OC) so the ItemsControl
    // shifts live while the model collection moves in parallel for persistence.
    readonly ObservableCollection<EditableListRow> _rows = new();

    public CalendarPage(ManagementWindow main, ManagementViewModel vm, WidgetService? widgetService)
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
                _widgetService.CalendarsChanged += RefreshList;
            RefreshList();
        };
        Unloaded += (_, _) =>
        {
            if (_widgetService != null)
                _widgetService.CalendarsChanged -= RefreshList;
        };
    }

    public void ApplyLoc() => RefreshList();

    public void RefreshList()
    {
        if (_widgetService == null) return;
        var cals = _widgetService.Calendars;
        CountLabel.Text = _loc.Get("CalendarPage.ItemCount", cals.Count);
        // 拖动排序即列表顺序：不再重排，Rows 直接镜像 Calendars 的持久化顺序。
        _rows.Clear();
        foreach (var c in cals) _rows.Add(BuildRow(c));
        EmptyHint.Visibility = cals.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SetSelection(ListHost, _selected);
    }

    EditableListRow BuildRow(DesktopCalendar cal)
    {
        var row = new EditableListRow
        {
            Tag = cal,
            Title = $"Calendar {cal.DisplayYear}-{cal.DisplayMonth:D2}",
            Subtitle = $"{(int)cal.Width}×{(int)cal.Height} · {_loc.Get("CalendarPage.Subtitle", cal.Notes.Count)}",
            IconKey = "Icon.Calendar",
            IsLocked = cal.IsLocked,
            IsVisible = cal.IsVisible,
        };
        row.ReorderRequested += (src, targetIdx) =>
        {
            if (src.Tag is not DesktopCalendar c || _widgetService == null) return;
            _widgetService.MoveCalendar(c.Id, targetIdx); // 模型 + 持久化
            MoveRow(_rows, src, targetIdx);               // 列表实时换位（镜像标签栏）
        };
        // ponytail: status badge removed (lock/hidden chips no longer shown).
        row.LockCommand = new RelayCommand(_ =>
        {
            cal.IsLocked = !cal.IsLocked;
            _widgetService?.UpdateCalendar(cal);
            RefreshList();
        });
        row.VisibilityCommand = new RelayCommand(_ =>
        {
            // ponytail: 2026-08-26 — route straight through ToggleCalendarWindow (see
            // ClockPage). Pre-flipping the model fired CalendarsChanged → ghost-stamp
            // snap + SetEnabled, which reversed the toggle and left a transparent ghost.
            _main.ToggleCalendarWindow(cal);
        });
        row.DeleteCommand = new RelayCommand(_ => Delete(cal));
        row.RenameCommand = new RelayCommand(_ => { /* calendars don't have a free-form name */ });
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
            // floating editor while the docked panel already showed this calendar.
            Select(cal);
        };
        // 右键菜单已取消：显示/隐藏与删除移到属性面板顶部状态区。
        return row;
    }

    void Select(DesktopCalendar cal)
    {
        // ponytail: route through DockTarget so any pre-existing floating editor
        // for this calendar is closed before the docked panel takes over.
        PropertyWindowManager.Instance.DockTarget(cal, _main);
        if (!ReferenceEquals(_selected, cal))
        {
            _selected = cal;
            SetSelection(ListHost, cal);
        }
    }

    void NewCalendar_Click(object sender, RoutedEventArgs e)
    {
        _main.NewCalendar();
        RefreshList();
    }

    void Delete(DesktopCalendar cal)
    {
        if (MessageBox.Show(_loc.Get("CalendarPage.DeleteConfirm", $"Calendar {cal.DisplayYear}-{cal.DisplayMonth:D2}"),
            _loc["CalendarPage.DeleteTitle"], MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _main.DeleteCalendar(cal);
        if (ReferenceEquals(_selected, cal)) _selected = null;
        RefreshList();
    }
}
