using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using DeskOrder.PluginApi;
using DesktopZones.Helpers;
using DesktopZones.Models;
using DesktopZones.Services;

namespace DesktopZones.Views.Pages;

/// <summary>
/// 「插件工具」页：工具插件（API v3）注册的全部命令都列在这里，右侧给每条命令分配/清除
/// 全局热键。命令执行入口是托盘/面板/分区菜单或分配好的热键，本页只做键位管理 ——
/// 行上没有任何「运行」按钮。数据完全来自 <see cref="PluginToolService.Commands"/>
/// （插件加载顺序 + 插件内注册顺序）。
/// </summary>
public partial class PluginToolsPage : UserControl
{
    readonly ConfigService _configService;
    readonly LocalizationService _loc = LocalizationService.Instance;

    // 图标按「插件@版本」缓存，避免语言切换/每次刷新都重读一次 icon.png。
    readonly Dictionary<string, ImageSource?> _iconCache = new(StringComparer.Ordinal);
    // 录制期间要禁用其它行的按钮；行是自绘的，刷新列表时整体重建。
    readonly List<Button> _recordButtons = new();
    readonly List<Button> _clearButtons = new();

    // HotkeyRecorder 是进程级单例键盘钩子：同一时刻只能录一条命令。
    bool _recording;
    Window? _hostWindow;

    public PluginToolsPage(ManagementWindow main, ConfigService configService)
    {
        // 本页不选行、不弹窗，main 只为与其它页共用 ManagementWindow.ShowSection 的构造签名。
        _ = main;
        InitializeComponent();
        _configService = configService;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    // ── 生命周期 ──

    void OnLoaded(object sender, RoutedEventArgs e)
    {
        PluginToolService.Instance.CommandsChanged += OnCommandsChanged;
        _loc.LanguageChanged += OnLanguageChanged;
        _hostWindow = Window.GetWindow(this);
        if (_hostWindow != null) _hostWindow.Closed += OnHostWindowClosed;
        RefreshList();
    }

    void OnUnloaded(object sender, RoutedEventArgs e)
    {
        PluginToolService.Instance.CommandsChanged -= OnCommandsChanged;
        _loc.LanguageChanged -= OnLanguageChanged;
        if (_hostWindow != null)
        {
            _hostWindow.Closed -= OnHostWindowClosed;
            _hostWindow = null;
        }
        // 页面离开了但低层键盘钩子还挂着的话，用户按什么键都会被吞掉 —— 必须摘钩。
        StopRecording();
    }

    void OnHostWindowClosed(object? sender, EventArgs e) => StopRecording();

    void OnCommandsChanged()
    {
        // 与 PluginsPage 同一套防御：事件可能来自插件加载/卸载的任意时机，投递到后台队列后
        // 页面可能已经卸载（ShowSection 每次 new 一页），这时再画就是画给死页面。
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!IsLoaded || AppLifetime.IsShuttingDown) return;
            _iconCache.Clear();
            RefreshList();
        }), DispatcherPriority.Background);
    }

    void OnLanguageChanged(string _)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!IsLoaded || AppLifetime.IsShuttingDown) return;
            RefreshList();
        }), DispatcherPriority.Background);
    }

    /// <summary>管理窗口 ApplyLoc / 插件命令变化后重绘本页（也供自测台直接调用）。</summary>
    public void RefreshList()
    {
        // 关闭期不要再重建视觉树（Application.LoadComponent 在关闭中会抛异常，见 PluginsPage.Render）。
        if (Dispatcher.HasShutdownStarted || AppLifetime.IsShuttingDown) return;

        var commands = PluginToolService.Instance.Commands;
        CountLabel.Text = _loc.Plural("PluginTool.Count", commands.Count);

        _recordButtons.Clear();
        _clearButtons.Clear();
        ListHost.Children.Clear();
        foreach (var entry in commands) ListHost.Children.Add(BuildRow(entry));

        bool empty = commands.Count == 0;
        EmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        // 「已安装且启用的 tool 插件，但命令一条都没注册」= 装了/启用了还没重启（或加载失败）。
        RestartHint.Visibility = empty && HasEnabledToolPluginWithoutCommands()
            ? Visibility.Visible : Visibility.Collapsed;
    }

    static bool HasEnabledToolPluginWithoutCommands()
    {
        try
        {
            return PluginService.Instance.Records.Any(r => r.Enabled && r.KindValues.Contains(PluginKind.Tool));
        }
        catch
        {
            return false;
        }
    }

    // ── 行 ──

    FrameworkElement BuildRow(PluginToolEntry entry)
    {
        var hotkey = AssignedHotkey(entry);

        var border = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = ThemeBrushes.BgSurfaceModern,
            BorderBrush = ThemeBrushes.BorderSubtleModern,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 0, 8, 8)
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = BuildIcon(entry);
        icon.Margin = new Thickness(0, 0, 14, 0);
        icon.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = new TextBlock
        {
            Text = entry.Command.DisplayName,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeBrushes.TextPrimaryModern,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        // 命令说明本来就只是菜单 ToolTip；这里顺手复用，不额外占一行高度。
        if (!string.IsNullOrWhiteSpace(entry.Command.Description)) title.ToolTip = entry.Command.Description;
        info.Children.Add(title);
        info.Children.Add(new TextBlock
        {
            Text = BuildSubtitle(entry),
            FontSize = 11,
            Margin = new Thickness(0, 4, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = ThemeBrushes.TextTertiaryModern
        });
        Grid.SetColumn(info, 1);
        grid.Children.Add(info);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };

        bool assigned = hotkey != null && hotkey.Key != 0;
        var recordButton = new Button
        {
            Content = assigned
                ? HotkeyRecorder.ComboToDisplay(hotkey!.Modifiers, hotkey.Key)
                : _loc["PluginTool.Hotkey.Set"],
            Height = 30,
            MinWidth = 118,
            FontSize = 11.5,
            Cursor = Cursors.Hand,
            Style = (Style)FindResource("OutlineBtn"),
            // 未分配时把「未设置」放进 ToolTip：按钮本身写的是动作（设置热键），状态则要能查。
            ToolTip = assigned ? null : _loc["PluginTool.Hotkey.None"]
        };
        recordButton.Click += (_, _) => StartRecording(entry, recordButton);
        // 录制中重绘（插件命令变化）时全部禁用，避免开第二个钩子；正在录的那个按钮由
        // StartRecording 重新启用。
        recordButton.IsEnabled = !_recording;
        _recordButtons.Add(recordButton);
        actions.Children.Add(recordButton);

        if (assigned)
        {
            var clearButton = new Button
            {
                Content = _loc["PluginTool.Hotkey.Clear"],
                Height = 30,
                MinWidth = 52,
                Margin = new Thickness(8, 0, 0, 0),
                FontSize = 11.5,
                Cursor = Cursors.Hand,
                Style = (Style)FindResource("OutlineBtn")
            };
            clearButton.Click += (_, _) => ClearHotkey(entry);
            clearButton.IsEnabled = !_recording;
            _clearButtons.Add(clearButton);
            actions.Children.Add(clearButton);
        }

        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);

        border.Child = grid;
        return border;
    }

    /// <summary>副标题：「插件显示名 · 托盘 / 面板 / 分区」；命令没进任何菜单时写「仅热键」。</summary>
    string BuildSubtitle(PluginToolEntry entry)
    {
        var pluginName = entry.Manifest.Name?.Get(_loc.CurrentLanguage);
        if (string.IsNullOrWhiteSpace(pluginName)) pluginName = entry.PluginDisplayName;

        var surfaces = new List<string>();
        if ((entry.Command.Menus & PluginToolSurface.Tray) != 0) surfaces.Add(_loc["PluginTool.Surface.Tray"]);
        if ((entry.Command.Menus & PluginToolSurface.Panel) != 0) surfaces.Add(_loc["PluginTool.Surface.Panel"]);
        if ((entry.Command.Menus & PluginToolSurface.Zone) != 0) surfaces.Add(_loc["PluginTool.Surface.Zone"]);
        var menus = surfaces.Count == 0
            ? _loc["PluginTool.Surface.HotkeyOnly"]
            : string.Join(" / ", surfaces);
        return $"{pluginName} · {menus}";
    }

    FrameworkElement BuildIcon(PluginToolEntry entry)
    {
        var border = new Border
        {
            Width = 42,
            Height = 42,
            CornerRadius = new CornerRadius(9),
            Background = ThemeBrushes.BgHoverModern,
            ClipToBounds = true
        };
        var image = ResolveIcon(entry);
        if (image != null)
        {
            border.Child = new Image { Source = image, Stretch = Stretch.Uniform };
            return border;
        }
        border.Child = new Path
        {
            Data = (Geometry)FindResource("Icon.Plugin"),
            Stretch = Stretch.Uniform,
            Width = 18,
            Height = 18,
            Fill = ThemeBrushes.TextTertiaryModern
        };
        return border;
    }

    ImageSource? ResolveIcon(PluginToolEntry entry)
    {
        var key = entry.PluginId + "@" + entry.Record.Version;
        if (_iconCache.TryGetValue(key, out var cached)) return cached;

        ImageSource? image = null;
        try
        {
            var dir = PluginPaths.VersionDir(entry.Record.Id, entry.Record.Version);
            var iconPath = PluginPaths.ResolveInside(dir, entry.Manifest.Icon);
            if (iconPath != null && System.IO.File.Exists(iconPath)) image = LoadBitmap(iconPath);
        }
        catch
        {
            // 图标只是装饰：读不到（插件没落盘 / 包被删）就走 Icon.Plugin 兜底。
        }
        _iconCache[key] = image;
        return image;
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

    // ── 热键 ──

    PluginToolHotkey? AssignedHotkey(PluginToolEntry entry)
    {
        var config = _configService.Load();
        return config.PluginToolHotkeys.FirstOrDefault(h =>
            h != null && h.Enabled && h.Key != 0 && SameCommand(h, entry));
    }

    static bool SameCommand(PluginToolHotkey hotkey, PluginToolEntry entry)
        => string.Equals(hotkey.PluginId, entry.PluginId, StringComparison.OrdinalIgnoreCase)
           && string.Equals(hotkey.CommandId, entry.Command.Id, StringComparison.Ordinal);

    /// <summary>点击热键按钮：进录制态。Esc 取消，按下组合键后写配置并重注册。</summary>
    void StartRecording(PluginToolEntry entry, Button button)
    {
        if (_recording) return;
        _recording = true;
        button.Content = _loc["PluginTool.Hotkey.Recording"];
        button.ToolTip = null;
        foreach (var other in _recordButtons) other.IsEnabled = ReferenceEquals(other, button);
        // 清除按钮也要禁用：录制中改配置会让回调随后把旧选择写回去。
        foreach (var clear in _clearButtons) clear.IsEnabled = false;

        HotkeyRecorder.StartRecording(
            (mods, key) => CaptureHotkey(entry, mods, key),
            CancelRecording);
    }

    void CancelRecording()
    {
        _recording = false;
        RefreshList();
    }

    void CaptureHotkey(PluginToolEntry entry, uint mods, uint key)
    {
        _recording = false;
        try
        {
            var config = _configService.Load();
            // 同一个组合键全局只能属于一条命令：后分配的赢，先从别的命令上摘掉。
            // （null 保护：配置可能被手改成带 null 的数组，App.RefreshPluginToolHotkeys 也这么防。）
            config.PluginToolHotkeys.RemoveAll(h =>
                h != null && h.Modifiers == mods && h.Key == key && !SameCommand(h, entry));
            var hotkey = config.PluginToolHotkeys.FirstOrDefault(h => h != null && SameCommand(h, entry));
            if (hotkey == null)
            {
                hotkey = new PluginToolHotkey { PluginId = entry.PluginId, CommandId = entry.Command.Id };
                config.PluginToolHotkeys.Add(hotkey);
            }
            hotkey.Modifiers = mods;
            hotkey.Key = key;
            hotkey.Enabled = true;
            _configService.Save(config);
            if (Application.Current is App app) app.RefreshPluginToolHotkeys();
        }
        finally
        {
            // 无论写配置是否成功都要重绘：按钮上的「按下组合键…」必须变回键位/清除按钮。
            RefreshList();
        }
    }

    void ClearHotkey(PluginToolEntry entry)
    {
        if (_recording) return;
        try
        {
            var config = _configService.Load();
            // RemoveAll 顺手清掉同命令的历史遗留条目（含 Enabled=false），避免占着键却看不见。
            if (config.PluginToolHotkeys.RemoveAll(h => h != null && SameCommand(h, entry)) > 0)
            {
                _configService.Save(config);
                if (Application.Current is App app) app.RefreshPluginToolHotkeys();
            }
        }
        finally
        {
            RefreshList();
        }
    }

    void StopRecording()
    {
        if (!_recording) return;
        _recording = false;
        HotkeyRecorder.StopRecording();
    }

    // ── 自测台入口（App.PluginLab.cs）──

    /// <summary>当前列表行数（= 已发布命令数）。</summary>
    internal int RowCount => ListHost.Children.Count;

    /// <summary>空态是否可见（自测台断言「命令清空后提示出现」用）。</summary>
    internal Visibility EmptyStateVisibility => EmptyHint.Visibility;

    /// <summary>「重启后生效」提示是否可见（自测台/将来的诊断用）。</summary>
    internal Visibility RestartHintVisibility => RestartHint.Visibility;
}
