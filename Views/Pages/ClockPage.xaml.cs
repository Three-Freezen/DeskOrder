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
    DesktopClock? _selected;

    public ClockPage(ManagementWindow main, ManagementViewModel vm, WidgetService? widgetService)
    {
        InitializeComponent();
        _main = main;
        _widgetService = widgetService;
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
        CountLabel.Text = $"{clocks.Count} 项";
        IEnumerable<DesktopClock> sorted = _sortMode switch
        {
            1 => clocks.OrderByDescending(c => c.Width * c.Height),
            _ => clocks.OrderBy(c => c.Mode.ToString(), StringComparer.Ordinal),
        };
        ListHost.ItemsSource = sorted.Select(BuildRow).ToList();
        EmptyHint.Visibility = clocks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SetSelection(ListHost, _selected);
        SortBtn.Content = $"⇅ {SortLabels[_sortMode]}";
    }

    void SortBtn_Click(object sender, RoutedEventArgs e) =>
        ShowSortMenu(SortBtn, SortLabels, _sortMode, i => { _sortMode = i; RefreshList(); });

    static readonly string[] SortLabels = { "名称", "尺寸" };
    int _sortMode;

    EditableListRow BuildRow(DesktopClock clock)
    {
        var mode = clock.Mode == ClockDisplayMode.Digital ? "数字" : "钟表";
        var format = clock.Use24Hour ? "24 小时" : "12 小时";
        var row = new EditableListRow
        {
            Tag = clock,
            Title = $"Clock ({mode})",
            Subtitle = $"{format} · {(int)clock.Width}×{(int)clock.Height}",
            IconKey = "Icon.Clock",
            IsLocked = clock.IsLocked,
            IsVisible = clock.IsVisible,
        };
        ApplyStatusBadge(row, clock);

        row.LockCommand = new RelayCommand(_ =>
        {
            clock.IsLocked = !clock.IsLocked;
            _widgetService?.UpdateClock(clock);
            RefreshList();
        });
        row.VisibilityCommand = new RelayCommand(_ =>
        {
            clock.IsVisible = row.IsVisible;
            _widgetService?.UpdateClock(clock);
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
            Select(clock);
            PropertyWindowService.OpenOrFocus(clock);
        };
        row.PreviewMouseRightButtonUp += (_, e) =>
        {
            Select(clock);
            ShowClockContextMenu(clock, row);
        };
        return row;
    }

    void Select(DesktopClock clock)
    {
        if (!ReferenceEquals(_selected, clock))
        {
            _selected = clock;
            SetSelection(ListHost, clock);
        }
    }

    static void ApplyStatusBadge(EditableListRow row, DesktopClock clock)
    {
        if (clock.IsLocked)
        {
            row.HasStatusBadge = true; row.StatusBadge = "已锁定";
            row.StatusBadgeBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xC1, 0x07));
        }
        else if (!clock.IsVisible)
        {
            row.HasStatusBadge = true; row.StatusBadge = "已隐藏";
            row.StatusBadgeBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xA0, 0xA0, 0xC0));
        }
        else row.HasStatusBadge = false;
    }

    void ShowClockContextMenu(DesktopClock clock, EditableListRow row)
    {
        var items = new List<RowContextMenu.Item>
        {
            new(clock.IsVisible ? "隐藏时钟" : "显示时钟", () => _main.ToggleClockWindow(clock)),
        };
        items.Add(new("删除时钟", () => Delete(clock), Danger: true));
        RowContextMenu.Show(row, items);
    }

    void NewClock_Click(object sender, RoutedEventArgs e)
    {
        _main.NewClock();
        RefreshList();
    }

    void Delete(DesktopClock clock)
    {
        if (MessageBox.Show($"删除时钟「Clock」？", "删除时钟",
            MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _main.DeleteClock(clock);
        if (ReferenceEquals(_selected, clock)) _selected = null;
        RefreshList();
    }
}
