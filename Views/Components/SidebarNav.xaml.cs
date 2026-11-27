using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using DesktopZones.Helpers;
using DesktopZones.Services;

namespace DesktopZones.Views.Components;

public partial class SidebarNav : UserControl
{
    public event EventHandler<string>? SectionChanged;
    public event EventHandler? ShowAllClicked;
    public event EventHandler? MinimizeAllClicked;
    public event EventHandler? HideAllClicked;

    private string _activeSection = "zones";
    private Button? _activeButton;

    // Cache original section keys. XAML sets Tag on each nav button, but we overwrite
    // it with "active" on highlight, and ClearValue wipes the XAML value too — so
    // we look up the section from this dict instead of reading btn.Tag.
    private readonly Dictionary<Button, string> _sectionKeys = new();

    public string ActiveSection
    {
        get => _activeSection;
        set
        {
            _activeSection = string.IsNullOrEmpty(value) ? "zones" : value;
            HighlightActive();
        }
    }

    public SidebarNav()
    {
        InitializeComponent();
        // 版本跟随程序集（csproj <Version>），与关于页/设置页同一来源，不再硬编码。
        VersionText.Text = "v" + DesktopZones.Helpers.AppVersion.Current;
        _sectionKeys[NavZones]    = "zones";
        _sectionKeys[NavMerged]   = "merged";
        _sectionKeys[NavDocks]    = "docks";
        _sectionKeys[NavPanel]    = "panel";
        _sectionKeys[NavZoneBays] = "zonebays";
        _sectionKeys[NavCalendar] = "calendar";
        _sectionKeys[NavClock]    = "clock";
        _sectionKeys[NavMedia]    = "media";
        _sectionKeys[NavSticky]   = "sticky";
        _sectionKeys[NavPluginWidgets] = "pluginwidgets";
        _sectionKeys[NavPlugins]  = "plugins";
        _sectionKeys[NavPluginTools] = "plugintools";
        _sectionKeys[NavSettings] = "settings";
        _sectionKeys[NavAbout]    = "about";
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        HighlightActive();
        // 「可更新插件数」由 PluginService 启动后台刷新时算好：这里只订阅，窗口关闭/卸载时退订，
        // 避免管理窗口反复开关（每次 new SidebarNav）后旧实例仍被事件表引用。
        var service = PluginService.Instance;
        service.AvailableUpdatesChanged -= OnAvailableUpdatesChanged;
        service.AvailableUpdatesChanged += OnAvailableUpdatesChanged;
        UpdateUpdateBadge(service.AvailableUpdateCount);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
        => PluginService.Instance.AvailableUpdatesChanged -= OnAvailableUpdatesChanged;

    private void OnAvailableUpdatesChanged()
    {
        // 事件在 UI 线程发出（PluginService 后台刷新会切回 Dispatcher）；这里再兜底一次，
        // 防止将来有别的调用方在后台线程直接写计数。
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!IsLoaded || AppLifetime.IsShuttingDown) return;
                UpdateUpdateBadge(PluginService.Instance.AvailableUpdateCount);
            }));
            return;
        }
        if (!IsLoaded || AppLifetime.IsShuttingDown) return;
        UpdateUpdateBadge(PluginService.Instance.AvailableUpdateCount);
    }

    /// <summary>写角标：数字 &gt;0 才显示，&gt;99 显示 99+。</summary>
    private void UpdateUpdateBadge(int count)
    {
        PluginsBadgeText.Text = FormatUpdateBadge(count);
        PluginsBadge.Visibility = ShouldShowUpdateBadge(count) ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>角标文案（纯函数，自测台直接断言）：0 → 空串；1..99 → 数字；&gt;99 → "99+"。</summary>
    internal static string FormatUpdateBadge(int count)
        => count <= 0 ? "" : count > 99 ? "99+" : count.ToString();

    internal static bool ShouldShowUpdateBadge(int count) => count > 0;

    // ── 自测台只读访问 / 注入 ──

    internal void SetUpdateBadgeForLab(int count) => UpdateUpdateBadge(count);
    internal bool BadgeVisibleForLab => PluginsBadge.Visibility == Visibility.Visible;
    internal string BadgeTextForLab => PluginsBadgeText.Text;

    private void HighlightActive()
    {
        Button? target = _activeSection switch
        {
            "zones"    => NavZones,
            "merged"   => NavMerged,
            "docks"    => NavDocks,
            "panel"    => NavPanel,
            "zonebays" => NavZoneBays,
            "calendar" => NavCalendar,
            "clock"    => NavClock,
            "media"    => NavMedia,
            "sticky"   => NavSticky,
            "pluginwidgets" => NavPluginWidgets,
            "plugins"  => NavPlugins,
            "plugintools" => NavPluginTools,
            "about"    => NavAbout,
            "settings" => NavSettings,
            _          => NavZones,
        };
        // Swap Tag="active" between the previously-active button and the new one.
        // Restore the prior button's original section key (XAML value) — ClearValue
        // would wipe the XAML "zones"/"merged"/etc. local value and leave Tag=null,
        // which makes the next click on that button silently fail the section lookup.
        if (_activeButton != null && _activeButton != target
            && _sectionKeys.TryGetValue(_activeButton, out var prev))
            _activeButton.Tag = prev;
        if (target != null) target.Tag = "active";
        _activeButton = target;
    }

    private void NavItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && _sectionKeys.TryGetValue(btn, out var section))
            SectionChanged?.Invoke(this, section);
    }

    private void ShowAll_Click(object sender, RoutedEventArgs e) => ShowAllClicked?.Invoke(this, EventArgs.Empty);
    private void MinimizeAll_Click(object sender, RoutedEventArgs e) => MinimizeAllClicked?.Invoke(this, EventArgs.Empty);
    private void HideAll_Click(object sender, RoutedEventArgs e) => HideAllClicked?.Invoke(this, EventArgs.Empty);

    /// <summary>分区坞入口只在已安装并启用 zonebar 插件时出现。</summary>
    public void SetZoneBaysEntry(bool visible)
    {
        if (NavZoneBays == null) return;
        var target = visible ? Visibility.Visible : Visibility.Collapsed;
        if (NavZoneBays.Visibility != target) NavZoneBays.Visibility = target;
        if (visible) NavZoneBays.BringIntoView();
    }

    /// <summary>「插件组件」入口：有可用组件描述符或已有组件实例（含孤儿实例）时出现。</summary>
    public void SetPluginWidgetsEntry(bool visible)
    {
        if (NavPluginWidgets == null) return;
        var target = visible ? Visibility.Visible : Visibility.Collapsed;
        if (NavPluginWidgets.Visibility != target) NavPluginWidgets.Visibility = target;
        if (visible) NavPluginWidgets.BringIntoView();
    }
}
