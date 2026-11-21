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
    // 语言列表是动态的（语言包安装/卸载后重建下拉框）。
    readonly Action _onLanguagesChanged;
    // 插件注册/撤下设置 schema 时刷新「插件设置…」按钮（API v5）。
    readonly Action _onPluginSettingsChanged;

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
        // 下拉初值:等 Loaded 里的 SyncFromConfig 也不迟,但先填上避免"空框"闪一下。
        bool prevSuppress = _suppress;
        _suppress = true;
        try { SelectExpandCorner(_configService.Load().SubfolderExpandCorner); }
        finally { _suppress = prevSuppress; }
        _onLangChanged = _ => { RebuildHotkeys(); UpdateLanguageHint(); UpdatePluginSettingsUi(); };
        LocalizationService.Instance.LanguageChanged += _onLangChanged;
        _onLanguagesChanged = () => { RebuildLanguageCombo(); UpdateLanguageHint(); };
        LocalizationService.Instance.LanguagesChanged += _onLanguagesChanged;
        _onPluginSettingsChanged = () => Dispatcher.BeginInvoke(
            new Action(UpdatePluginSettingsUi), System.Windows.Threading.DispatcherPriority.Background);
        PluginSettingsService.Instance.SchemasChanged += _onPluginSettingsChanged;
        RebuildLanguageCombo();
        UpdateLanguageHint();
        UpdatePluginSettingsUi();
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
            UpdatePluginSettingsUi();
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
            LocalizationService.Instance.LanguagesChanged -= _onLanguagesChanged;
            PluginSettingsService.Instance.SchemasChanged -= _onPluginSettingsChanged;
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

    // ── 次级分区:默认展开方位 / 正方形次级分区 ──

    void ExpandCorner_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        var cfg = _configService.Load();
        cfg.SubfolderExpandCorner = ExpandCornerCombo.SelectedItem is ComboBoxItem ci && ci.Tag is string tag && tag.Length > 0
            ? tag
            : "BottomLeft";
        _configService.Save(cfg);
    }

    /// <summary>配置里的方位字符串 → 下拉项(非法值回落到左下 = 默认)。</summary>
    void SelectExpandCorner(string? corner)
    {
        foreach (var item in ExpandCornerCombo.Items)
        {
            if (item is ComboBoxItem ci && (ci.Tag as string) == corner) { ExpandCornerCombo.SelectedItem = ci; return; }
        }
        foreach (var item in ExpandCornerCombo.Items)
        {
            if (item is ComboBoxItem ci && (ci.Tag as string) == "BottomLeft") { ExpandCornerCombo.SelectedItem = ci; return; }
        }
        ExpandCornerCombo.SelectedIndex = 1;
    }

    void SquareSubfolder_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        var cfg = _configService.Load();
        cfg.SubfolderSquare = SquareSubfolderBox.IsChecked == true;
        _configService.Save(cfg);
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
            SquareSubfolderBox.IsChecked = cfg.SubfolderSquare;
            SelectExpandCorner(cfg.SubfolderExpandCorner);
            AutoCheckUpdateBox.IsChecked = cfg.AutoCheckUpdate;
            DeleteSetupAfterUpdateBox.IsChecked = cfg.DeleteSetupAfterUpdate;
            // 以运行中的语言为准（语言包卸载回退后 cfg 可能还是旧 code）。
            SelectComboByTag(LanguageCombo, LocalizationService.Instance.CurrentLanguage);
            PluginsEnabledBox.IsChecked = cfg.PluginsEnabled;
            LoadPluginsAtStartupBox.IsChecked = cfg.LoadPluginsAtStartup;
            MirrorUrlBox.Text = cfg.PluginRegistryUrl;
            MirrorStatusText.Text = "";
            MirrorStatusText.Visibility = Visibility.Collapsed;
            ImageCacheStatusText.Text = "";
            ImageCacheStatusText.Visibility = Visibility.Collapsed;
            UpdateMirrorHint();
            UpdateImageCacheUi();
            WallpaperRendererBox.IsChecked = cfg.UseWallpaperRenderer;
            SyncRendererUi();
            SyncThemeRadios(cfg.ThemeMode switch
            {
                "Light" => AppThemeMode.Light,
                "Dark"  => AppThemeMode.Dark,
                _       => AppThemeMode.System,
            });
        }
        finally { _suppress = false; }
    }

    void PluginsEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        var cfg = _configService.Load();
        cfg.PluginsEnabled = PluginsEnabledBox.IsChecked == true;
        _configService.Save(cfg);
    }

    // ── 插件：启动时加载 / 索引镜像 / 图片缓存 ──

    /// <summary>「启动时加载插件」：只写配置，重启后由 PluginService.Initialize 消费。</summary>
    void LoadPluginsAtStartup_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        var cfg = _configService.Load();
        cfg.LoadPluginsAtStartup = LoadPluginsAtStartupBox.IsChecked == true;
        _configService.Save(cfg);
    }

    /// <summary>
    /// 索引地址校验（纯函数，自测台直接断言）：它决定去哪里下载 registry.json，
    /// 而 registry 又决定「装哪个包、校验哪个哈希」，明文 HTTP 等于把这件事交给中间人。
    /// 所以默认只放行 https；本机回环地址（localhost / 127.0.0.1 / ::1）额外放开 http，
    /// 否则本地起个静态服务器调试镜像就没法用了。
    /// </summary>
    internal static bool IsAcceptableRegistryUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme == Uri.UriSchemeHttps) return true;
        return uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback;
    }

    /// <summary>「应用」按钮：空输入 = 恢复内置默认；非法地址只提示、不保存。</summary>
    void MirrorApply_Click(object sender, RoutedEventArgs e)
    {
        var raw = MirrorUrlBox.Text.Trim();
        if (raw.Length > 0 && !IsAcceptableRegistryUrl(raw))
        {
            MirrorStatusText.Text = LocalizationService.Instance["Settings.Plugins.Mirror.Invalid"];
            MirrorStatusText.Visibility = Visibility.Visible;
            return;
        }
        SaveRegistryUrl(raw);
    }

    void MirrorReset_Click(object sender, RoutedEventArgs e) => SaveRegistryUrl("");

    /// <summary>
    /// 保存索引地址并同步到运行态：配置里空串 = 用内置默认（既有语义），但
    /// <see cref="PluginCatalogService.RegistryUrl"/> 必须拿到真正的绝对地址，
    /// 否则下一次 <c>LoadAsync</c> 会拿空串去 new HttpRequestMessage 而抛异常。
    /// </summary>
    void SaveRegistryUrl(string url)
    {
        var cfg = _configService.Load();
        cfg.PluginRegistryUrl = url;
        _configService.Save(cfg);
        PluginService.Instance.Catalog.RegistryUrl = url.Length == 0
            ? PluginCatalogService.DefaultRegistryUrl
            : url;
        MirrorUrlBox.Text = url;
        MirrorStatusText.Text = LocalizationService.Instance["Settings.Plugins.Mirror.Saved"];
        MirrorStatusText.Visibility = Visibility.Visible;
    }

    /// <summary>默认地址提示（含内置 URL）；语言切换后页面会重建，这里读当前语言即可。</summary>
    void UpdateMirrorHint()
    {
        if (MirrorHintText == null) return;
        MirrorHintText.Text = LocalizationService.Instance.Get(
            "Settings.Plugins.Mirror.Hint", PluginCatalogService.DefaultRegistryUrl);
    }

    /// <summary>清除本地封面缓存（只删缓存目录，不影响已安装插件）。</summary>
    void ClearImageCache_Click(object sender, RoutedEventArgs e)
    {
        PluginService.Instance.Catalog.ClearImageCache();
        UpdateImageCacheUi();
        ImageCacheStatusText.Text = LocalizationService.Instance["Settings.Plugins.Cache.Cleared"];
        ImageCacheStatusText.Visibility = Visibility.Visible;
    }

    /// <summary>刷新「N 张 · X.X MB」；缓存目录不可读时按 0 张显示，不在设置页弹错误。</summary>
    void UpdateImageCacheUi()
    {
        if (CacheSizeText == null) return;
        var (files, bytes) = PluginService.Instance.Catalog.GetImageCacheStats();
        CacheSizeText.Text = LocalizationService.Instance.Get(
            "Settings.Plugins.Cache.Size", files, (bytes / 1024.0 / 1024.0).ToString("0.0"));
    }

    // ── 插件设置（API v5）──

    /// <summary>
    /// 「插件设置…」按钮：没有任何已启用插件注册 schema 时禁用（点了也没有内容可配）。
    /// 插件加载 / 卸载会触发 <see cref="PluginSettingsService.SchemasChanged"/>，这里跟着刷新。
    /// </summary>
    void UpdatePluginSettingsUi()
    {
        if (PluginSettingsButton == null) return;
        var loc = LocalizationService.Instance;
        var count = PluginSettingsService.Instance.Schemas.Count;
        PluginSettingsButton.IsEnabled = count > 0;
        PluginSettingsHint.Text = count > 0
            ? loc.Get("Settings.Plugins.Settings.Hint", count)
            : loc["Settings.Plugins.Settings.EmptyHint"];
    }

    /// <summary>打开宿主渲染的插件设置窗口（值由各插件自己声明、宿主负责校验 + 落盘）。</summary>
    void OpenPluginSettings_Click(object sender, RoutedEventArgs e)
    {
        if (!PluginSettingsService.Instance.HasAny)
        {
            UpdatePluginSettingsUi();
            return;
        }
        var dialog = new PluginSettingsDialog { Owner = Window.GetWindow(this) };
        dialog.ShowDialog();
    }

    // ── 玻璃渲染方案(二期) ──

    /// <summary>ponytail 2026-09-26(二期): 勾选「壁纸采样」→ 写全局开关 → 同步到每个对象
    /// (ConfigService.ApplyRendererSwitch) → 逐个刷新已显示的窗口。
    /// 刷新用 ApplyStyle/ApplyAcrylic 那条既有路径,所以切换后无需重启。</summary>
    void WallpaperRenderer_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        var cfg = _configService.Load();
        cfg.UseWallpaperRenderer = WallpaperRendererBox.IsChecked == true;
        ConfigService.ApplyRendererSwitch(cfg);
        _configService.Save(cfg);
        // 换了来源/方案 → 背板模糊缓存作废,下次渲染按新方案重建
        WallpaperBackdrop.Invalidate();
        RefreshGlassWindows();
        SyncRendererUi();
    }

    /// <summary>「重新采样」—— 让壁纸背板丢掉缓存重新采集一次。仅新方案下可用。
    /// 采集前会先把本应用窗口隐藏一会儿(见 WallpaperSource),避免把应用自己的窗口采进背板。</summary>
    void Resample_Click(object sender, RoutedEventArgs e)
    {
        if (WallpaperRendererBox.IsChecked != true) return;
        var loc = LocalizationService.Instance;
        ResampleButton.IsEnabled = false;
        ResampleHintText.Text = loc["Settings.Renderer.Resampling"];
        // ponytail 2026-09-26(二期修订): 抓屏是在 UI 线程上同步跑并 Sleep 110ms 的,
        // 不先逼一次渲染的话「正在采样…」根本来不及画出来,用户点了按钮看不到任何反馈。
        PumpRender();
        try
        {
            WallpaperBackdrop.Invalidate();
            var img = Helpers.WallpaperSource.Resample();
            if (img == null)
            {
                ResampleHintText.Text = loc["Settings.Renderer.Resample.Failed"];
            }
            else
            {
                // 新图 → 让每个窗口按新背板重画(否则要等下次状态变化才会用上新图)
                RefreshGlassWindows();
                SyncRendererUi();   // 采样来源文字也跟着更新
                // 明确回一句话:采样成功但屏幕上看不出变化时(比如新壁纸和旧的很像),
                // 这条文字是唯一能确认"真的采了一次"的地方。
                ResampleHintText.Text = loc.Get("Settings.Renderer.Resample.Done",
                    WallpaperSourceText.Text);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SettingsPage] Resample failed: {ex}");
            ResampleHintText.Text = loc["Settings.Renderer.Resample.Failed"];
        }
        finally
        {
            SyncRendererUi();
        }
    }

    /// <summary>把「正在采样…」这类文字逼上屏 —— 抓屏会 Sleep 110ms 卡住 UI 线程,
    /// 不先冲一次渲染队列的话文字只会和采样结果一起出现(= 没有"正在进行"的观感)。</summary>
    static void PumpRender()
        => System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
            () => { }, System.Windows.Threading.DispatcherPriority.Render);

    /// <summary>按当前开关状态刷新这一块的可用性/文字(采样来源、按钮可用性)。</summary>
    void SyncRendererUi()
    {
        bool on = WallpaperRendererBox.IsChecked == true;
        ResampleButton.IsEnabled = on;
        if (!on)
        {
            WallpaperSourceText.Text = LocalizationService.Instance["Settings.Renderer.Source.Off"];
            return;
        }
        // 读一次来源(会走缓存,不重复抓屏)
        try { Helpers.WallpaperSource.GetDesktop(); } catch (Exception ex) { Debug.WriteLine(ex.Message); }
        var desc = Helpers.WallpaperSource.Describe;
        WallpaperSourceText.Text = string.IsNullOrEmpty(desc)
            ? LocalizationService.Instance["Settings.Renderer.Source.None"] : desc;
    }

    /// <summary>让所有已显示的窗口按新方案重画玻璃。
    /// ponytail 2026-09-26(二期修订 3): **每个窗口都必须调到"重画玻璃"那个方法**。
    /// 日历原来调的是 ApplyStyle() —— 它只管边框/圆角/快捷栏,玻璃完全没刷,于是
    /// 「重新采样」「切换渲染方案」对日历静默失效(用户实测反馈:日历/时钟不实时同步)。
    /// 时钟那条本来就是 ApplyAcrylic()(内部会调 ApplyStyle),面板两个都要。</summary>
    static void RefreshGlassWindows()
    {
        foreach (Window w in Application.Current.Windows)
        {
            switch (w)
            {
                case Views.ZoneWindow z: z.ApplyStyle(); break;          // ApplyStyle 内部会调 ApplyAcrylic
                case Views.StickyNoteWindow n: n.ApplyAcrylic(); break;
                case Views.ClockWidget c: c.ApplyAcrylic(); break;       // ApplyAcrylic 内部会调 ApplyStyle
                case Views.CalendarWidget c: c.ApplyAcrylic(); break;    // ← 上一版这里错调了 ApplyStyle
                // 面板这两个方法是分开的 —— ApplyAcrylic 负责背板/DWM 玻璃,ApplyStyle 负责填充层。
                case Views.PanelWindow p: p.ApplyAcrylic(); p.ApplyStyle(); break;
            }
        }
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
            // 语言包提供的语言也是合法 code；TrySetLanguage 对已卸载的包返回 false。
            LocalizationService.Instance.TrySetLanguage(lang);
            UpdateLanguageHint();
        }
    }

    /// <summary>重建语言下拉框（内置 zh/en + 已安装语言包，显示覆盖率与作者）。</summary>
    void RebuildLanguageCombo()
    {
        var loc = LocalizationService.Instance;
        var previous = _suppress;
        _suppress = true;
        try
        {
            LanguageCombo.Items.Clear();
            foreach (var code in loc.AvailableLanguages)
            {
                LanguageCombo.Items.Add(new ComboBoxItem
                {
                    Content = loc.GetLanguageDisplay(code),
                    Tag = code
                });
            }
            SelectComboByTag(LanguageCombo, loc.CurrentLanguage);
        }
        finally { _suppress = previous; }
    }

    /// <summary>当前语言来自语言包时显示覆盖率；否则提示去插件市场装语言包。</summary>
    void UpdateLanguageHint()
    {
        if (LanguageHint == null) return;
        var loc = LocalizationService.Instance;
        var pack = loc.GetLanguagePack(loc.CurrentLanguage);
        LanguageHint.Text = pack != null
            ? loc.Get("Settings.Language.CoverageHint", pack.CoveragePercent, pack.Base)
            : loc["Settings.Language.Hint"];
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

    void DeleteSetupAfterUpdate_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        var cfg = _configService.Load();
        cfg.DeleteSetupAfterUpdate = DeleteSetupAfterUpdateBox.IsChecked == true;
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
            UpdateState.Ready => UpdateService.IsRunningPackaged
                ? loc["Settings.ReadyRestart.Msix"] : loc["Settings.ReadyRestart"],
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
            UpdateState.Ready => UpdateService.IsRunningPackaged
                ? loc["Settings.RestartUpdate.Msix"] : loc["Settings.RestartUpdate"],
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
        // ponytail 2026-08-28: 收敛到 StartupShortcut.Sync（App 启动自愈共用同一实现）。
        var loc = LocalizationService.Instance;
        var error = Helpers.StartupShortcut.Sync(create);
        if (error != null)
        {
            Debug.WriteLine($"[StartupShortcut] create failed: {error}");
            App.Notify?.Invoke(loc["Settings.StartupShortcut.FailedTitle"], loc.Get("Settings.StartupShortcut.FailedBody", error));
            return;
        }
        if (create)
            App.Notify?.Invoke(loc["Settings.StartupShortcut.CreatedTitle"], loc["Settings.StartupShortcut.CreatedBody"]);
        else
            App.Notify?.Invoke(loc["Settings.StartupShortcut.RemovedTitle"], loc["Settings.StartupShortcut.RemovedBody"]);
    }
}
