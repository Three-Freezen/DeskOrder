using System;
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
        Loaded += (_, _) => SyncFromConfig();
    }

    public void ApplyLoc() { /* labels hard-coded CN. */ }

    void BuildHotkeys()
    {
        HotkeyStack.Children.Add(MakeHotkeyRow("控制面板", () =>
        {
            var c = _configService.Load();
            return c.PanelHotkey.PanelHotkeyEnabled
                ? ManagementWindow.GetHotkeyLabel(c.PanelHotkey.PanelHotkeyModifiers, c.PanelHotkey.PanelHotkeyKey)
                : "未设置";
        }));
        HotkeyStack.Children.Add(MakeHotkeyRow("全部显示", () => "Ctrl+Shift+A"));
        HotkeyStack.Children.Add(MakeHotkeyRow("全部隐藏", () => "Ctrl+Shift+H"));
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
            SelectComboByTag(LanguageCombo, cfg.Language);
            ThemeSystemRb.IsChecked = (cfg.ThemeMode ?? "System") == "System";
            ThemeLightRb.IsChecked = cfg.ThemeMode == "Light";
            ThemeDarkRb.IsChecked = cfg.ThemeMode == "Dark";
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

    void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        if (LanguageCombo.SelectedItem is ComboBoxItem ci && ci.Tag is string lang)
        {
            var cfg = _configService.Load();
            cfg.Language = lang;
            _configService.Save(cfg);
            LocalizationService.Instance.CurrentLanguage = lang == "zh" ? Services.Language.Chinese : Services.Language.English;
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
        MessageBox.Show("已是最新版本 v0.9.0", "检查更新", MessageBoxButton.OK, MessageBoxImage.Information);
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
                var exePath = Environment.ProcessPath;
                if (exePath == null) return;
                Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return;
                dynamic? shell = Activator.CreateInstance(shellType);
                if (shell == null) return;
                dynamic? shortcut = shell.CreateShortcut(startupPath);
                shortcut.TargetPath = exePath;
                shortcut.WorkingDirectory = Path.GetDirectoryName(exePath);
                shortcut.Description = "DeskOrder";
                shortcut.Save();
            }
            catch { }
        }
        else if (File.Exists(startupPath))
        {
            try { File.Delete(startupPath); } catch { }
        }
    }
}
