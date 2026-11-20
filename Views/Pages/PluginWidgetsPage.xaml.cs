using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeskOrder.PluginApi;
using DesktopZones.Helpers;
using DesktopZones.Models;
using DesktopZones.Services;
using DesktopZones.Views.Components;
using static DesktopZones.Views.Pages.PageHelpers;

namespace DesktopZones.Views.Pages;

/// <summary>
/// 「插件组件」管理页。所有插件注册的组件实例集中在这里：
/// - 行上不放任何按钮，点行 = 右侧打开原生属性面板（显示/锁定/删除都在面板状态区）；
/// - 右上「新建组件」现场弹菜单，列出所有已加载插件声明的组件（与市场页「已安装」行共用
///   <see cref="PluginWidgetCreateMenu"/>）；
/// - 插件被卸载/停用/描述符缺失留下的孤儿实例也照样列出来 —— 副标题标「插件不可用」，
///   点行仍能打开面板把实例删掉。
/// </summary>
public partial class PluginWidgetsPage : UserControl
{
    readonly ManagementWindow _main;
    readonly PluginService _service = PluginService.Instance;
    readonly LocalizationService _loc = LocalizationService.Instance;
    // ponytail 2026-11-14(性能审计): 批量替换行集合,只发一次 Reset。
    readonly BulkObservableCollection<EditableListRow> _rows = new();
    // 插件图标/显示名按「插件@版本」缓存，避免每次刷新都读一次 manifest 与 icon.png。
    readonly Dictionary<string, ImageSource?> _iconCache = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> _nameCache = new(StringComparer.Ordinal);
    PluginWidget? _selected;

    public PluginWidgetsPage(ManagementWindow main)
    {
        InitializeComponent();
        _main = main;
        ListHost.ItemsSource = _rows;
        Loaded += (_, _) =>
        {
            _service.PluginsChanged += OnPluginsChanged;
            _service.WidgetsChanged += OnWidgetsChanged;
            RefreshList();
        };
        Unloaded += (_, _) =>
        {
            _service.PluginsChanged -= OnPluginsChanged;
            _service.WidgetsChanged -= OnWidgetsChanged;
        };
    }

    public void ApplyLoc() => RefreshList();

    void OnPluginsChanged()
    {
        // 插件清单变了：manifest 缓存（名字/图标）可能已经过期。
        _iconCache.Clear();
        _nameCache.Clear();
        if (IsLoaded) RefreshList();
    }

    void OnWidgetsChanged(PluginWidget _)
    {
        if (IsLoaded) RefreshList();
    }

    public void RefreshList()
    {
        var widgets = _service.Widgets.ToList();
        CountText.Text = _loc.Get("PluginWidget.Count", widgets.Count);
        RefreshNewButton();

        _rows.ReplaceAll(widgets.Select(BuildRow));
        bool empty = _rows.Count == 0;
        EmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        RestartHint.Visibility = empty && PluginWidgetCreateMenu.NeedsRestart
            ? Visibility.Visible : Visibility.Collapsed;
        SetSelection(ListHost, _selected);
    }

    void RefreshNewButton()
    {
        bool available = PluginWidgetCreateMenu.HasAvailable;
        NewBtn.IsEnabled = available;
        NewBtn.Opacity = available ? 1.0 : 0.45;
        NewBtn.ToolTip = available
            ? null
            : _loc[PluginWidgetCreateMenu.NeedsRestart
                ? "PluginWidget.NeedsRestart" : "PluginWidget.NoPlugins"];
    }

    EditableListRow BuildRow(PluginWidget widget)
    {
        var descriptor = _service.FindDescriptor(widget.PluginId, widget.WidgetTypeId);
        // 副标题：「插件名 · 显示中/已隐藏 · 宽×高」；孤儿实例追加「插件不可用」，便于一眼识别。
        var subtitle = string.Format("{0} · {1} · {2}×{3}",
            PluginName(widget.PluginId),
            _loc[widget.IsVisible ? "ZoneBay.Status.Visible" : "ZoneBay.Status.Hidden"],
            (int)widget.Width, (int)widget.Height);
        if (descriptor == null) subtitle += " · " + _loc["PluginWidget.Unavailable"];

        var row = new EditableListRow
        {
            Tag = widget,
            // 描述符缺失（插件已卸载/停用/换了类型 id）时用类型 id 兜底，至少能定位到是哪条。
            Title = descriptor?.DisplayName ?? widget.WidgetTypeId,
            Subtitle = subtitle,
            IconSource = ResolveIcon(widget.PluginId),
        };
        if (row.IconSource == null) row.IconKey = "Icon.Plugin";
        row.PreviewMouseLeftButtonUp += (_, _) => Select(widget);
        return row;
    }

    void Select(PluginWidget widget)
    {
        _selected = widget;
        SetSelection(ListHost, widget);
        // 原生路径：点行 = 停靠到右侧属性面板（与分区/快捷栏/分区坞页一致）。
        PropertyWindowManager.Instance.DockTarget(widget, _main);
    }

    void New_Click(object sender, RoutedEventArgs e)
        => PluginWidgetCreateMenu.Open(NewBtn, Window.GetWindow(this), OnWidgetCreatedFromMenu);

    /// <summary>菜单创建成功后的统一收尾：刷新列表并选中新行（测试入口也走这里）。</summary>
    void OnWidgetCreatedFromMenu(PluginWidget? widget)
    {
        if (widget == null) return;
        RefreshList();
        Select(widget);
    }

    // ── 自测台入口（App.PluginLab.cs）──

    /// <summary>当前可新建的组件描述符总数（无插件时 0）。</summary>
    internal int AvailableDescriptorCount()
        => _service.GetWidgetPlugins().Sum(p => p.Descriptors.Count);

    /// <summary>当前列表行数（截图/增删断言用）。</summary>
    internal int RowCount => _rows.Count;

    /// <summary>「新建组件」按钮是否可点（没有可用描述符时应为 false）。</summary>
    internal bool NewButtonEnabled => NewBtn.IsEnabled;

    /// <summary>自测台：走与「新建组件」菜单相同的创建收尾路径，建出第一个可用组件。</summary>
    internal PluginWidget? CreateFirstWidgetForTest()
    {
        var plugins = _service.GetWidgetPlugins();
        if (plugins.Count == 0) return null;
        var (record, descriptors) = plugins[0];
        var widget = _service.CreateWidget(record.Id, descriptors[0].Id);
        OnWidgetCreatedFromMenu(widget);
        return widget;
    }

    // ── 插件元数据（名字/图标）──

    string PluginName(string pluginId)
    {
        var key = pluginId + "|" + _loc.CurrentLanguage;
        if (_nameCache.TryGetValue(key, out var cached)) return cached;
        var name = ReadManifest(pluginId)?.Name.Get(_loc.CurrentLanguage);
        if (string.IsNullOrWhiteSpace(name)) name = pluginId;
        _nameCache[key] = name!;
        return name!;
    }

    ImageSource? ResolveIcon(string pluginId)
    {
        var loaded = _service.GetLoaded(pluginId);
        var record = loaded?.Record ?? _service.GetRecord(pluginId);
        var key = pluginId + "@" + (record?.Version ?? "");
        if (_iconCache.TryGetValue(key, out var cached)) return cached;

        ImageSource? image = null;
        try
        {
            var iconPath = loaded != null
                ? PluginPaths.ResolveInside(loaded.Directory, loaded.Manifest.Icon)
                : (record == null ? null : ResolveDiskIcon(record));
            if (iconPath != null && File.Exists(iconPath)) image = LoadBitmap(iconPath);
        }
        catch { /* 图标只是装饰，读不到就走 Icon.Plugin 兜底 */ }
        _iconCache[key] = image;
        return image;
    }

    string? ResolveDiskIcon(PluginRecord record)
    {
        var dir = PluginPaths.VersionDir(record.Id, record.Version);
        var manifest = PluginPackages.ReadManifest(dir, out _);
        return manifest == null ? null : PluginPaths.ResolveInside(dir, manifest.Icon);
    }

    PluginManifest? ReadManifest(string pluginId)
    {
        var loaded = _service.GetLoaded(pluginId);
        if (loaded != null) return loaded.Manifest;
        var record = _service.GetRecord(pluginId);
        return record == null
            ? null
            : PluginPackages.ReadManifest(PluginPaths.VersionDir(record.Id, record.Version), out _);
    }

    static ImageSource? LoadBitmap(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }
}
