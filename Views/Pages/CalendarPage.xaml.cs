using System;
using System.Collections.Generic;
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
    DesktopCalendar? _selected;

    public CalendarPage(ManagementWindow main, ManagementViewModel vm, WidgetService? widgetService)
    {
        InitializeComponent();
        _main = main;
        _widgetService = widgetService;
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
        CountLabel.Text = $"{cals.Count} 项";
        IEnumerable<DesktopCalendar> sorted = _sortMode switch
        {
            1 => cals.OrderByDescending(c => c.Notes.Count).ThenBy(c => c.Id),
            _ => cals.OrderBy(c => c.Id),
        };
        ListHost.ItemsSource = sorted.Select(BuildRow).ToList();
        EmptyHint.Visibility = cals.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SetSelection(ListHost, _selected);
        SortBtn.Content = $"⇅ {SortLabels[_sortMode]}";
    }

    void SortBtn_Click(object sender, RoutedEventArgs e) =>
        ShowSortMenu(SortBtn, SortLabels, _sortMode, i => { _sortMode = i; RefreshList(); });

    static readonly string[] SortLabels = { "名称", "备注数" };
    int _sortMode;

    EditableListRow BuildRow(DesktopCalendar cal)
    {
        var row = new EditableListRow
        {
            Tag = cal,
            Title = $"Calendar {cal.DisplayYear}-{cal.DisplayMonth:D2}",
            Subtitle = $"{(int)cal.Width}×{(int)cal.Height} · {cal.Notes.Count} 备注",
            IconKey = "Icon.Calendar",
            IsLocked = cal.IsLocked,
            IsVisible = cal.IsVisible,
        };
        ApplyStatusBadge(row, cal);

        row.LockCommand = new RelayCommand(_ =>
        {
            cal.IsLocked = !cal.IsLocked;
            _widgetService?.UpdateCalendar(cal);
            RefreshList();
        });
        row.VisibilityCommand = new RelayCommand(_ =>
        {
            cal.IsVisible = row.IsVisible;
            _widgetService?.UpdateCalendar(cal);
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
            Select(cal);
            PropertyWindowService.OpenOrFocus(cal);
        };
        row.PreviewMouseRightButtonUp += (_, e) =>
        {
            Select(cal);
            ShowCalendarContextMenu(cal, row);
        };
        return row;
    }

    void Select(DesktopCalendar cal)
    {
        if (!ReferenceEquals(_selected, cal))
        {
            _selected = cal;
            SetSelection(ListHost, cal);
        }
    }

    static void ApplyStatusBadge(EditableListRow row, DesktopCalendar cal)
    {
        if (cal.IsLocked)
        {
            row.HasStatusBadge = true; row.StatusBadge = "已锁定";
            row.StatusBadgeBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xC1, 0x07));
        }
        else if (!cal.IsVisible)
        {
            row.HasStatusBadge = true; row.StatusBadge = "已隐藏";
            row.StatusBadgeBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xA0, 0xA0, 0xC0));
        }
        else row.HasStatusBadge = false;
    }

    void ShowCalendarContextMenu(DesktopCalendar cal, EditableListRow row)
    {
        var items = new List<RowContextMenu.Item>
        {
            new(cal.IsVisible ? "隐藏日历" : "显示日历", () => _main.ToggleCalendarWindow(cal)),
        };
        items.Add(new("删除日历", () => Delete(cal), Danger: true));
        RowContextMenu.Show(row, items);
    }

    void NewCalendar_Click(object sender, RoutedEventArgs e)
    {
        _main.NewCalendar();
        RefreshList();
    }

    void Delete(DesktopCalendar cal)
    {
        if (MessageBox.Show($"删除日历「Calendar {cal.DisplayYear}-{cal.DisplayMonth:D2}」？",
            "删除日历", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _main.DeleteCalendar(cal);
        if (ReferenceEquals(_selected, cal)) _selected = null;
        RefreshList();
    }
}
