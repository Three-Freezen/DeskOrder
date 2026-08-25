using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using DesktopZones.Helpers;
using DesktopZones.Models;
using DesktopZones.Services;
using DesktopZones.Views.Components;

namespace DesktopZones.Views.Pages;

/// <summary>
/// Settings page (spec §7.3): grouped list of General / Appearance / Hotkeys / Update.
/// All controls read from / write to AppConfig through the ConfigService so a Settings
/// edit is the same code path as a Tray-menu edit.
/// Segmented.SelectedIndexChanged fires on programmatic change too, so we guard with
/// a suppress flag during initial SyncFromConfig.
/// </summary>
public partial class SettingsPage : UserControl
{
    readonly ConfigService _configService;
    bool _suppress;

    public SettingsPage(ConfigService configService)
    {
        InitializeComponent();
        _configService = configService;
        BuildHotkeys();
        LocalizationService.Instance.LanguageChanged += _ => RebuildHotkeys();
        // ponytail: two-way bind to ThemeService so the title-bar cycle button
        // (ManagementWindow.ThemeBtn_Click) and any other runtime theme flip
        // (System accent change, UserPreferenceChanged → Apply(System)) keeps
        // these radios in sync. _suppress in SyncThemeRadios breaks the echo:
        // Apply → Changed → SyncThemeRadios → IsChecked setter → ThemeRb_Changed
        // → _suppress guard exits before writing back to cfg.
        ThemeService.Changed += SyncThemeRadios;
        Loaded += (_, _) => SyncFromConfig();
    }

    public void ApplyLoc() { /* labels hard-coded CN. */ }

    void BuildHotkeys()
    {
        var loc = LocalizationService.Instance;
        HotkeyStack.Children.Add(MakeHotkeyRow(loc["Settings.Hotkey.Panel"], () =>
        {
            var c = _configService.Load();
            return c.PanelHotkey.PanelHotkeyEnabled
                ? ManagementWindow.GetHotkeyLabel(c.PanelHotkey.PanelHotkeyModifiers, c.PanelHotkey.PanelHotkeyKey)
                : loc["Settings.Hotkey.NotSet"];
        }));
        HotkeyStack.Children.Add(MakeHotkeyRow(loc["Settings.Hotkey.ShowAll"], () => "Ctrl+Shift+A"));
        HotkeyStack.Children.Add(MakeHotkeyRow(loc["Settings.Hotkey.HideAll"], () => "Ctrl+Shift+H"));
    }

    void RebuildHotkeys()
    {
        HotkeyStack.Children.Clear();
        BuildHotkeys();
    }

    UIElement MakeHotkeyRow(string label, Func<string> getCurrent)
    {
        var grid = new Grid { Margin = new Thickness(0, 8, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new TextBlock { Text = label, Foreground = (System.Windows.Media.Brush)FindResource("Brush.Text.Primary") });
        stack.Children.Add(new TextBlock
        {
            Text = getCurrent(),
            FontSize = 11,
            FontFamily = new System.Windows.Media.FontFamily("Cascadia Code, Consolas, monospace"),
            Margin = new Thickness(0, 2, 0, 0),
            Foreground = (System.Windows.Media.Brush)FindResource("Brush.Text.Tertiary"),
        });
        Grid.SetColumn(stack, 0);
        grid.Children.Add(stack);
        return grid;
    }

    void SyncFromConfig()
    {
        _suppress = true;
        try
        {
            var cfg = _configService.Load();
            StartWithWindowsBox.IsChecked = cfg.StartWithWindows;
            StartMinimizedBox.IsChecked = cfg.StartMinimized;
            AutoAlignBox.IsChecked = cfg.AutoAlign;
            ReverseSyncBox.IsChecked = cfg.ReverseSyncEnabled;
            ImagePreviewBox.IsChecked = cfg.ImagePreviewEnabled;
            SelectComboByTag(LanguageCombo, cfg.Language);
            SyncThemeRadios(cfg.ThemeMode switch
            {
                "Light" => AppThemeMode.Light,
                "Dark"  => AppThemeMode.Dark,
                _       => AppThemeMode.System,
            });
        }
        finally { _suppress = false; }
    }

    // Shared by initial SyncFromConfig and the live ThemeService.Changed listener.
    // _suppress is the guard that breaks the round-trip: Apply → Changed →
    // SyncThemeRadios → IsChecked=true → ThemeRb_Changed → _suppress → return.
    void SyncThemeRadios(AppThemeMode mode)
    {
        // Read current suppress state; if we're already mid-sync (e.g. from
        // SyncFromConfig), don't nest the try/finally or we'd drop _suppress
        // back to false before the outer block finishes writing other controls.
        if (_suppress)
        {
            ThemeSystemRb.IsChecked = mode == AppThemeMode.System;
            ThemeLightRb.IsChecked = mode == AppThemeMode.Light;
            ThemeDarkRb.IsChecked = mode == AppThemeMode.Dark;
            return;
        }
        _suppress = true;
        try
        {
            ThemeSystemRb.IsChecked = mode == AppThemeMode.System;
            ThemeLightRb.IsChecked = mode == AppThemeMode.Light;
            ThemeDarkRb.IsChecked = mode == AppThemeMode.Dark;
        }
        finally { _suppress = false; }
    }

    static void SelectComboByTag(ComboBox box, string? tag)
    {
        foreach (var item in box.Items)
            if (item is ComboBoxItem ci && (ci.Tag as string) == tag) { box.SelectedItem = ci; return; }
        box.SelectedIndex = 0;
    }

    void StartWithWindows_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        var cfg = _configService.Load();
        cfg.StartWithWindows = StartWithWindowsBox.IsChecked == true;
        _configService.Save(cfg);
        UpdateStartupShortcut(cfg.StartWithWindows);
    }

    void StartMinimized_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        var cfg = _configService.Load();
        cfg.StartMinimized = StartMinimizedBox.IsChecked == true;
        _configService.Save(cfg);
    }

    void AutoAlign_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        var cfg = _configService.Load();
        cfg.AutoAlign = AutoAlignBox.IsChecked == true;
        _configService.Save(cfg);
    }

    void ReverseSync_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        var cfg = _configService.Load();
        cfg.ReverseSyncEnabled = ReverseSyncBox.IsChecked == true;
        _configService.Save(cfg);
        FileSyncService.Instance.Enabled = cfg.ReverseSyncEnabled;
    }

    void ImagePreview_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        var cfg = _configService.Load();
        cfg.ImagePreviewEnabled = ImagePreviewBox.IsChecked == true;
        _configService.Save(cfg);
        // 同步运行态开关，清除图标缓存并刷新所有分区窗口，使缩略图/图标切换即时生效。
        ShellIconService.ImagePreviewEnabled = cfg.ImagePreviewEnabled;
        ShellIconService.Instance.ClearCache();
        (Application.Current as App)?.ZoneManager?.NotifyChanged();
    }

    void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        if (LanguageCombo.SelectedItem is ComboBoxItem ci && ci.Tag is string lang)
        {
            var cfg = _configService.Load();
            cfg.Language = lang;
            _configService.Save(cfg);
            LocalizationService.Instance.CurrentLanguage = lang == "zh" ? "zh" : "en";
        }
    }

    void ThemeRb_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        var mode = ThemeLightRb.IsChecked == true ? "Light"
                 : ThemeDarkRb.IsChecked == true ? "Dark" : "System";
        var cfg = _configService.Load();
        cfg.ThemeMode = mode;
        _configService.Save(cfg);
        ThemeService.Apply(mode switch
        {
            "Light" => AppThemeMode.Light,
            "Dark" => AppThemeMode.Dark,
            _ => AppThemeMode.System,
        });
    }

    void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        var loc = LocalizationService.Instance;
        MessageBox.Show(loc["Settings.UpToDate"], loc["Settings.CheckUpdate"],
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    void UpdateStartupShortcut(bool create)
    {
        var startupPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            "DeskOrder.lnk");

        if (create)
        {
            try
            {
                // ponytail 2026-08-24: 6 个 catch { } → 全部走 toast，让用户能看见失败原因。
                // 老逻辑每个失败都 silently return，勾上勾选框后用户毫无反馈，以为是空壳。
                var exePath = Environment.ProcessPath
                    ?? throw new InvalidOperationException("无法获取当前进程路径 (Environment.ProcessPath 为 null)");
                var shellType = Type.GetTypeFromProgID("WScript.Shell")
                    ?? throw new InvalidOperationException("WScript.Shell 不可用 — 可能是企业策略禁用了 WSH");
                dynamic shell = Activator.CreateInstance(shellType)
                    ?? throw new InvalidOperationException("无法创建 WScript.Shell 实例");
                dynamic shortcut = shell.CreateShortcut(startupPath);
                shortcut.TargetPath = exePath;
                shortcut.WorkingDirectory = Path.GetDirectoryName(exePath);
                shortcut.Description = "DeskOrder";
                shortcut.Save();
                // ponytail: 写完做一次回读 — AV / 权限问题会让 .Save() 不抛但也没文件。
                if (!File.Exists(startupPath))
                    throw new InvalidOperationException("快捷方式写入后未在磁盘上找到");

                var loc = LocalizationService.Instance;
                App.Notify?.Invoke(loc["Settings.StartupShortcut.CreatedTitle"], loc["Settings.StartupShortcut.CreatedBody"]);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[StartupShortcut] create failed: {ex}");
                var loc = LocalizationService.Instance;
                App.Notify?.Invoke(loc["Settings.StartupShortcut.FailedTitle"], loc.Get("Settings.StartupShortcut.FailedBody", ex.Message));
            }
        }
        else if (File.Exists(startupPath))
        {
            try
            {
                File.Delete(startupPath);
                var loc = LocalizationService.Instance;
                App.Notify?.Invoke(loc["Settings.StartupShortcut.RemovedTitle"], loc["Settings.StartupShortcut.RemovedBody"]);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[StartupShortcut] delete failed: {ex}");
                var loc = LocalizationService.Instance;
                App.Notify?.Invoke(loc["Settings.StartupShortcut.FailedTitle"], loc.Get("Settings.StartupShortcut.FailedBody", ex.Message));
            }
        }
    }
}
