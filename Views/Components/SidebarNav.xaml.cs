using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

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
        _sectionKeys[NavPlugins]  = "plugins";
        _sectionKeys[NavSettings] = "settings";
        _sectionKeys[NavAbout]    = "about";
        Loaded += (_, _) => HighlightActive();
    }

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
            "plugins"  => NavPlugins,
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
}