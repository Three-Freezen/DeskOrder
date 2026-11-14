using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DesktopZones.Helpers;
using DesktopZones.Models;
using DesktopZones.Services;
using DesktopZones.Views.Components;
using static DesktopZones.Views.Pages.PageHelpers;
using RelayCommand = DesktopZones.ViewModels.RelayCommand;

namespace DesktopZones.Views.Pages;

/// <summary>
/// 「分区坞」管理页。和「分区 / 快捷栏」页同一套原生行（<see cref="EditableListRow"/>）：
/// 行上不放任何按钮，点行 = 右侧打开原生属性面板（<see cref="PropertyPanel"/>），
/// 编辑走面板里的「应用/取消」；显示/隐藏与删除走行右键菜单（坞右键菜单同款）。
/// </summary>
public partial class ZoneBaysPage : UserControl
{
    readonly ManagementWindow _main;
    readonly ZoneBayService _service = ZoneBayService.Instance;
    readonly LocalizationService _loc = LocalizationService.Instance;
    // ponytail 2026-11-14(性能审计): 批量替换行集合,只发一次 Reset。
    readonly BulkObservableCollection<EditableListRow> _rows = new();
    PluginZoneBar? _selected;

    public ZoneBaysPage(ManagementWindow main)
    {
        InitializeComponent();
        _main = main;
        ListHost.ItemsSource = _rows;
        Loaded += (_, _) =>
        {
            _service.BarsChanged += Refresh;
            Refresh();
        };
        Unloaded += (_, _) => _service.BarsChanged -= Refresh;
    }

    public void ApplyLoc() => Refresh();

    void Refresh()
    {
        TitleText.Text = _loc["ZoneBay.Title"];
        CountText.Text = _loc.Get("ZoneBay.Count", _service.Bars.Count);
        NewBtn.Visibility = _service.HasAnyZoneBayPlugin ? Visibility.Visible : Visibility.Collapsed;
        NewBtn.IsEnabled = _service.HasAnyZoneBayPlugin;

        _rows.ReplaceAll(_service.Bars.Select(BuildRow));
        EmptyHint.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SetSelection(ListHost, _selected);
    }

    EditableListRow BuildRow(PluginZoneBar bar)
    {
        // 副标题只留「边 · 展开方式 · N 项」——状态字样（显示中/已隐藏）按修复要求去掉，
        // 隐藏态改用原生行自带的徽章表达，不混进这行小字。
        string subtitle = string.Format("{0} · {1} · {2} {3}",
            _loc[ZoneBayEdges.I18nKey(bar.Edge)],
            _loc[bar.NormalizedExpandMode == ZoneBayExpandModes.Original
                ? "ZoneBay.Prop.Mode.Original" : "ZoneBay.Prop.Mode.Strip"],
            bar.ZoneIds.Count,
            _loc["Manage.Count.Unit"]);

        var row = new EditableListRow
        {
            Tag = bar,
            Title = string.IsNullOrWhiteSpace(bar.Name) ? _loc["ZoneBay.Title"] : bar.Name,
            Subtitle = subtitle,
            IconKey = "Icon.ZoneBay",
            HasStatusBadge = !bar.IsVisible,
            StatusBadge = _loc["ZoneBay.Status.Hidden"],
            StatusBadgeBrush = TryFindResource("Brush.Bg.Input") as Brush,
        };

        row.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject src)
            {
                var parent = src;
                while (parent != null && parent is not Button)
                    parent = LogicalTreeHelper.GetParent(parent);
                if (parent is Button) return;
            }
            Select(bar);
        };
        row.ContextMenu = BuildRowMenu(bar);
        return row;
    }

    ContextMenu BuildRowMenu(PluginZoneBar bar)
    {
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem
        {
            Header = bar.IsVisible ? _loc["ZoneBay.Hide"] : _loc["ZoneBay.Show"],
            Command = new RelayCommand(_ => _service.SetVisible(bar, !bar.IsVisible)),
        });
        menu.Items.Add(new MenuItem
        {
            Header = _loc["ZoneBay.Menu.Settings"],
            Command = new RelayCommand(_ => Select(bar)),
        });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem
        {
            Header = _loc["ZoneBay.Menu.Delete"],
            Command = new RelayCommand(_ => Delete(bar)),
        });
        return menu;
    }

    void Select(PluginZoneBar bar)
    {
        _selected = bar;
        SetSelection(ListHost, bar);
        // 原生路径：点行 = 停靠到右侧属性面板（与分区/快捷栏页一致）。
        PropertyWindowManager.Instance.DockTarget(bar, _main);
    }

    void New_Click(object sender, RoutedEventArgs e)
    {
        var bar = _service.CreateBarForFirstPlugin();
        if (bar == null)
        {
            MessageBox.Show(_loc["ZoneBay.Error.PluginMissing"], _loc["ZoneBay.Title"],
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Refresh();
        Select(bar);
    }

    void Delete(PluginZoneBar bar)
    {
        var msg = _loc.Get("ZoneBay.Menu.DeleteConfirm", bar.Name, bar.ZoneIds.Count);
        if (MessageBox.Show(msg, _loc["ZoneBay.Menu.Delete"], MessageBoxButton.YesNo, MessageBoxImage.Warning)
            != MessageBoxResult.Yes) return;
        if (ReferenceEquals(_selected, bar)) _selected = null;
        _service.DeleteBar(bar);
        Refresh();
    }
}
