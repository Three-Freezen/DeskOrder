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

namespace DesktopZones.Views;

/// <summary>
/// 更新确认弹窗：新版本号、changelog、权限差异。
///
/// 存在的理由：更新不是「换个文件」——新版本可能多要一项权限。**新增联网权限必须重新勾选**
/// 才能继续（方案 §8.7），其余新增权限只做红色提示；被移除的权限灰字展示。
/// </summary>
public partial class PluginUpdateDialog : Window
{
    static readonly LocalizationService Loc = LocalizationService.Instance;

    readonly List<string> _added;

    public bool Confirmed { get; private set; }

    public PluginUpdateDialog(PluginCatalogEntry entry, PluginRecord record)
    {
        InitializeComponent();
        var language = Loc.CurrentLanguage;

        NameText.Text = entry.Name.Get(language);
        VersionText.Text = Loc.Get("Plugin.Update.From", record.Version, entry.Version);

        var changelog = entry.Changelog?.Get(language);
        if (string.IsNullOrWhiteSpace(changelog)) changelog = Loc["Plugin.Update.NoChangelog"];
        ChangelogText.Text = changelog;

        // 旧权限从「已落盘的 plugin.json」读：记录里不存权限，避免两份真相。
        var installed = PluginPackages.ReadManifest(PluginPaths.VersionDir(record.Id, record.Version), out _);
        var before = installed?.Permissions ?? new List<string>();
        var after = entry.Permissions ?? new List<string>();
        _added = after.Where(p => !before.Contains(p)).ToList();
        var removed = before.Where(p => !after.Contains(p)).ToList();

        if (_added.Count == 0 && removed.Count == 0)
        {
            AddPermissionLine(Loc["Plugin.Update.Permissions.Same"], ThemeBrushes.TextSecondaryModern);
        }
        else
        {
            if (_added.Count > 0)
                AddPermissionLine(Loc.Get("Plugin.Update.Permissions.Added", Join(_added)), ThemeBrushes.DangerModern);
            if (removed.Count > 0)
                AddPermissionLine(Loc.Get("Plugin.Update.Permissions.Removed", Join(removed)), ThemeBrushes.TextTertiaryModern);
        }

        if (_added.Contains("network"))
        {
            // 新增联网权限：必须显式同意，否则不下载。
            ConsentCheck.Content = Loc["Plugin.Update.NetworkConsent"];
            ConsentCheck.Visibility = Visibility.Visible;
            ConsentCheck.IsChecked = false;
            UpdateButton.IsEnabled = false;
            UpdateButton.Opacity = 0.5;
        }

        if (entry.KindValue.IsCode())
        {
            AddPermissionLine(Loc["Plugin.Update.Restart"], ThemeBrushes.TextTertiaryModern);
        }
    }

    void AddPermissionLine(string text, Brush brush)
    {
        PermissionsHost.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 12,
            LineHeight = 19,
            TextWrapping = TextWrapping.Wrap,
            Foreground = brush,
            Margin = new Thickness(0, 0, 0, 2)
        });
    }

    static string Join(IEnumerable<string> permissions)
        => string.Join(" · ", permissions.Select(PluginDisplay.DescribePermission));

    void ConsentCheck_Changed(object sender, RoutedEventArgs e)
    {
        var agreed = ConsentCheck.IsChecked == true;
        UpdateButton.IsEnabled = agreed;
        UpdateButton.Opacity = agreed ? 1 : 0.5;
    }

    void Update_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = true;
        DialogResult = true;
        Close();
    }

    void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
