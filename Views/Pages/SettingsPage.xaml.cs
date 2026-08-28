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
    // ponytail 2026-08-28: 更新服务（App 启动创建，页面每次导航重建 → 状态存服务不放页面）。
    readonly UpdateService? _updates;
    bool _suppress;
    bool _updateSubscribed;
    // 保存委托引用以便 Unloaded 时退订(LanguageChanged 订阅点需要同一引用才能 -=)。
    readonly Action<string> _onLangChanged;

    // 当前值 TextBlock + 取值委托，改键后/注入 getter 后增量刷新，避免一直停留在「未设置」。
    readonly System.Collections.Generic.List<(TextBlock Value, Func<string> Getter)> _hotkeyValueBindings = new();

    // ponytail 2026-08-27: 注入全局热键 UI → 保存/注册回调(由 ManagementWindow 设置,
    // 因为热键注册需要 App 实例的 _mainHwnd + WM_HOTKEY 分发)。
    public Action<FrameworkElement>? OnShowAllHotkeyPicked { get; set; }
    public Action<FrameworkElement>? OnHideAllHotkeyPicked { get; set; }
    public Action<FrameworkElement>? OnMinimizeAllHotkeyPicked { get; set; }
    public Action<bool>? OnDoubleClickToggleShowHideChanged { get; set; }

    public Func<string>? GetShowAllHotkeyLabel { get; set; }
    public Func<string>? GetHideAllHotkeyLabel { get; set; }
    public Func<string>? GetMinimizeAllHotkeyLabel { get; set; }

    public SettingsPage(ConfigService configService, UpdateService? updateService = null)
    {
        InitializeComponent();
        _configService = configService;
        _updates = updateService;
        BuildHotkeys();
        _onLangChanged = _ => RebuildHotkeys();
        LocalizationService.Instance.LanguageChanged += _onLangChanged;
        // ponytail: two-way bind to ThemeService so the title-bar cycle button
        // (ManagementWindow.ThemeBtn_Click) and any other runtime theme flip
        // (System accent change, UserPreferenceChanged → Apply(System)) keeps
        // these radios in sync. _suppress in SyncThemeRadios breaks the echo:
        // Apply → Changed → SyncThemeRadios → IsChecked setter → ThemeRb_Changed
        // → _suppress guard exits before writing back to cfg.
        ThemeService.Changed += SyncThemeRadios;
        Loaded += (_, _) =>
        {
            SyncFromConfig();
            // 更新卡片初挂载：订阅服务状态（防御性 _updateSubscribed 防重复订阅），
            // 并按服务当前状态渲染（后台检查可能已在启动时跑过）。
            if (_updates != null && !_updateSubscribed)
            {
                _updates.StateChanged += OnUpdateStateChanged;
                _updateSubscribed = true;
            }
            SyncUpdateUi();
        };
        // ponytail 2026-08-28: 本页面每次导航都会被 ManagementWindow.BuildSettingsPage
        // 重建，而上面订阅的 ThemeService.Changed 是 static event、LanguageChanged 是
        // 单例事件、UpdateService.StateChanged 是长生命周期实例事件 — 都是 GC Root。
        // 不在 Unloaded 退订的话，每进一次设置页就有一棵页面树连同 ManagementWindow
        // 闭包被永久钉住（此前实测的内存泄漏）。
        Unloaded += (_, _) =>
        {
            ThemeService.Changed -= SyncThemeRadios;
            LocalizationService.Instance.LanguageChanged -= _onLangChanged;
            if (_updates != null && _updateSubscribed)
            {
                _updates.StateChanged -= OnUpdateStateChanged;
                _updateSubscribed = false;
            }
        };
    }

    public void ApplyLoc() { /* labels hard-coded CN. */ }

    void BuildHotkeys()
    {
        var loc = LocalizationService.Instance;
        _hotkeyValueBindings.Clear();

        // ponytail 2026-08-27: 顶部 — 双击桌面切换全部显示/隐藏 勾选项。
        var dblClickRow = new Grid { Margin = new Thickness(0, 8, 0, 8) };
        var dblClickCb = new CheckBox
        {
            Content = loc["Settings.DoubleClickToggleShowHide"],
            IsChecked = _configService.Load().DoubleClickToggleShowHide,
        };
        dblClickCb.Checked += (_, _) => OnDoubleClickToggleShowHideChanged?.Invoke(true);
        dblClickCb.Unchecked += (_, _) => OnDoubleClickToggleShowHideChanged?.Invoke(false);
        dblClickRow.Children.Add(dblClickCb);
        HotkeyStack.Children.Add(dblClickRow);

        // ponytail 2026-08-27: 全局热键行 — 顺序与左侧边栏一致：
        // 全部显示 → 全部最小化 → 全部隐藏。
        HotkeyStack.Children.Add(MakeHotkeyRow(
            loc["Settings.Hotkey.ShowAll"],
            () => GetShowAllHotkeyLabel?.Invoke() ?? loc["Settings.Hotkey.NotSet"],
            btn => OnShowAllHotkeyPicked?.Invoke(btn)));
        HotkeyStack.Children.Add(MakeHotkeyRow(
            loc["Settings.Hotkey.MinimizeAll"],
            () => GetMinimizeAllHotkeyLabel?.Invoke() ?? loc["Settings.Hotkey.NotSet"],
            btn => OnMinimizeAllHotkeyPicked?.Invoke(btn)));
        HotkeyStack.Children.Add(MakeHotkeyRow(
            loc["Settings.Hotkey.HideAll"],
            () => GetHideAllHotkeyLabel?.Invoke() ?? loc["Settings.Hotkey.NotSet"],
            btn => OnHideAllHotkeyPicked?.Invoke(btn)));
    }

    /// <summary>改键后 / getter 注入后刷新三个快捷键当前值文本。</summary>
    public void RefreshHotkeyLabels()
    {
        foreach (var (value, getter) in _hotkeyValueBindings)
            value.Text = getter();
    }

    void RebuildHotkeys()
    {
        HotkeyStack.Children.Clear();
        BuildHotkeys();
    }

    UIElement MakeHotkeyRow(string label, Func<string> getCurrent, Action<FrameworkElement>? onButtonClick = null)
    {
        var grid = new Grid { Margin = new Thickness(0, 8, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new TextBlock { Text = label, Foreground = (System.Windows.Media.Brush)FindResource("Brush.Text.Primary") });
        var valueText = new TextBlock
        {
            Text = getCurrent(),
            FontSize = 11,
            FontFamily = new System.Windows.Media.FontFamily("Cascadia Code, Consolas, monospace"),
            Margin = new Thickness(0, 2, 0, 0),
            Foreground = (System.Windows.Media.Brush)FindResource("Brush.Text.Tertiary"),
        };
        _hotkeyValueBindings.Add((valueText, getCurrent));
        stack.Children.Add(valueText);
        Grid.SetColumn(stack, 0);
        grid.Children.Add(stack);
        if (onButtonClick != null)
        {
            // ponytail 2026-08-27: 按钮样式与属性面板「设置快捷键」按钮一致
            // (Brush.Bg.Input 底 + 1px 描边 + 次级文字)，不再是一个裸「...」。
            var btn = new Button
            {
                Content = LocalizationService.Instance["StickyNotePage.SetHotkey"],
                Cursor = System.Windows.Input.Cursors.Hand,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(8, 0, 0, 0),
                FontSize = 11,
                Background = (System.Windows.Media.Brush)FindResource("Brush.Bg.Input"),
                Foreground = (System.Windows.Media.Brush)FindResource("Brush.Text.Secondary"),
                BorderBrush = (System.Windows.Media.Brush)FindResource("Brush.Border.Subtle"),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
            };
            btn.Click += (_, _) => onButtonClick(btn);
            Grid.SetColumn(btn, 1);
            grid.Children.Add(btn);
        }
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
            AutoCheckUpdateBox.IsChecked = cfg.AutoCheckUpdate;
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

    void AutoCheckUpdate_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        var cfg = _configService.Load();
        cfg.AutoCheckUpdate = AutoCheckUpdateBox.IsChecked == true;
        _configService.Save(cfg);
    }

    // ── 更新卡片（状态机渲染，状态源在 UpdateService，页面只读） ──

    void OnUpdateStateChanged() => SyncUpdateUi();

    void SyncUpdateUi()
    {
        var svc = _updates;
        if (svc == null) return;
        var loc = LocalizationService.Instance;

        UpdateProgress.Visibility = svc.State == UpdateState.Downloading ? Visibility.Visible : Visibility.Collapsed;
        if (svc.State == UpdateState.Downloading) UpdateProgress.Value = svc.ProgressPercent;

        UpdateStatusText.Text = svc.State switch
        {
            UpdateState.Idle => loc.Get("Settings.CurrentVersion", AppVersion.Current),
            UpdateState.Checking => loc["Settings.Checking"],
            UpdateState.UpToDate => loc["Settings.UpToDate"],
            UpdateState.Available => loc.Get("Settings.NewVersionFound", svc.NewVersion ?? ""),
            UpdateState.Downloading => loc.Get("Settings.Downloading", svc.ProgressPercent),
            UpdateState.Ready => loc["Settings.ReadyRestart"],
            UpdateState.Failed => loc.Get("Settings.UpdateFailed", svc.ErrorText ?? ""),
            UpdateState.Unavailable => UpdateService.IsRunningPackaged
                ? loc["Settings.StoreChannel"]
                : loc["Settings.DevNoUpdate"],
            _ => "",
        };

        // Failed 也可重试：否则一次网络抖动/限流就把按钮永久锁死（状态在服务里，
        // 页面重建也不复位），用户只能重启应用。
        UpdateButton.IsEnabled = svc.State is UpdateState.Idle or UpdateState.UpToDate
            or UpdateState.Available or UpdateState.Ready or UpdateState.Failed;
        UpdateButton.Content = svc.State switch
        {
            UpdateState.Available => loc["Settings.DownloadUpdate"],
            UpdateState.Ready => loc["Settings.RestartUpdate"],
            _ => loc["Settings.CheckUpdate"],
        };

        ReleaseLink.Visibility = (svc.State == UpdateState.Available || svc.State == UpdateState.Ready)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        var svc = _updates;
        if (svc == null) return;
        try
        {
            if (svc.State == UpdateState.Ready)
            {
                var loc = LocalizationService.Instance;
                var choice = MessageBox.Show(
                    loc.Get("Settings.RestartConfirm", svc.NewVersion ?? ""),
                    loc["Settings.CheckUpdate"],
                    MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (choice == MessageBoxResult.Yes)
                    svc.ApplyAndRestart(); // Velopack 拉起安装器并退出本进程；失败抛异常 → Failed 状态
                return;
            }
            if (svc.State == UpdateState.Available)
            {
                await svc.DownloadAsync();
                return;
            }
            await svc.CheckForUpdatesAsync();
        }
        catch { /* 服务已把失败转成 Failed 状态渲染 */ }
    }

    void ReleaseLink_Click(object sender, RoutedEventArgs e)
    {
        if (_updates?.ReleaseUrl is not string url) return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[UpdateCard] open release url failed: {ex}");
        }
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
