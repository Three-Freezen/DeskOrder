using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using DesktopZones.Helpers;
using DesktopZones.Models;
using DesktopZones.Services;
using DesktopZones.Views;
using DesktopZones.Views.Components;
using Microsoft.Win32;

namespace DesktopZones.Views.Pages;

/// <summary>插件市场：市场 / 已安装 / 更新，本地导入、详情、启停与卸载。</summary>
public partial class PluginsPage : UserControl
{
    static readonly LocalizationService Loc = LocalizationService.Instance;
    readonly PluginService _service = PluginService.Instance;

    PluginCatalogResult? _catalog;
    int _tab;
    PluginKind? _filter;
    bool _settingSegments;
    bool _loading;
    bool _busy;

    readonly Dictionary<string, ImageSource?> _imageCache = new();
    readonly Dictionary<string, PluginManifest?> _manifestCache = new();
    readonly HashSet<string> _installing = new();

    public PluginsPage()
    {
        InitializeComponent();
        BuildTabs();
        BuildFilters();
        UpdateSearchHint();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        LocalizationService.Instance.LanguageChanged += OnLanguageChanged;
    }

    void OnLoaded(object sender, RoutedEventArgs e)
    {
        _service.PluginsChanged += OnPluginsChanged;
        _ = RefreshAsync(force: false);
    }

    void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _service.PluginsChanged -= OnPluginsChanged;
        LocalizationService.Instance.LanguageChanged -= OnLanguageChanged;
    }

    /// <summary>切到某个 tab（0 市场 / 1 已安装 / 2 更新）。自测台与将来的深链用。</summary>
    public void SelectTab(int index)
    {
        _tab = Math.Clamp(index, 0, 2);
        BuildTabs();
        Render();
    }

    // 自测台只读访问器：空态按钮 / 文案的可见性（在没窗口的页面上也能算出来）。
    internal bool EmptyRetryVisibleForLab => EmptyRetryButton.Visibility == Visibility.Visible;
    internal bool EmptyImportVisibleForLab => EmptyImportButton.Visibility == Visibility.Visible;
    internal string EmptyBodyForLab => EmptyBody.Text;

    void OnPluginsChanged()
    {
        // 判断必须放在 lambda 里：投递到 Background 队列的这段时间里页面可能已经卸载
        // （ShowSection 每次 new PluginsPage，旧页面照样会收到事件），这时再画就是画给死页面。
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!IsLoaded || AppLifetime.IsShuttingDown) return;
            BuildTabs();
            Render();
        }), DispatcherPriority.Background);
    }

    void OnLanguageChanged(string _)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!IsLoaded || AppLifetime.IsShuttingDown) return;
            BuildTabs();
            BuildFilters();
            Render();
        }), DispatcherPriority.Background);
    }

    // ── Segments ──

    void BuildTabs()
    {
        var updates = CountUpdates();
        _settingSegments = true;
        try
        {
            var selected = _tab;
            Tabs.Items = new ObservableCollection<SegmentItem>
            {
                new() { Text = Loc["Plugin.Tab.Market"] },
                new() { Text = Loc.Get("Plugin.Tab.Installed", _service.Records.Count) },
                new() { Text = Loc.Get("Plugin.Tab.Updates", updates) }
            };
            Tabs.SelectedIndex = selected;
        }
        finally { _settingSegments = false; }
        Tabs.SelectedIndexChanged -= Tabs_SelectedIndexChanged;
        Tabs.SelectedIndexChanged += Tabs_SelectedIndexChanged;
    }

    void BuildFilters()
    {
        Filters.Children.Clear();
        AddFilterChip(null, Loc["Plugin.Filter.All"]);
        AddFilterChip(PluginKind.Widget, Loc["Plugin.Filter.Widget"]);
        AddFilterChip(PluginKind.ZoneBar, Loc["Plugin.Filter.ZoneBar"]);
        AddFilterChip(PluginKind.Appearance, Loc["Plugin.Filter.Appearance"]);
        AddFilterChip(PluginKind.Language, Loc["Plugin.Filter.Language"]);
        AddFilterChip(PluginKind.Tool, Loc["Plugin.Filter.Tool"]);
        AddFilterChip(PluginKind.Integration, Loc["Plugin.Filter.Integration"]);
    }

    void AddFilterChip(PluginKind? kind, string text)
    {
        var chip = new ToggleButton
        {
            Content = text,
            Tag = kind,
            IsChecked = _filter == kind,
            Style = (Style)FindResource("PluginFilterChip")
        };
        chip.Click += (_, _) =>
        {
            _filter = kind;
            foreach (var child in Filters.Children.OfType<ToggleButton>())
                child.IsChecked = ReferenceEquals(child, chip);
            Render();
        };
        Filters.Children.Add(chip);
    }

    void Tabs_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (_settingSegments) return;
        _tab = Math.Clamp(Tabs.SelectedIndex, 0, 2);
        Render();
    }

    // ── 数据 ──

    async System.Threading.Tasks.Task RefreshAsync(bool force)
    {
        if (_loading) return;
        _loading = true;
        ShowStatus(Loc["Plugin.Refresh"] + "…");
        try
        {
            _catalog = await _service.Catalog.LoadAsync(force);
        }
        catch (Exception ex)
        {
            _catalog = new PluginCatalogResult { Offline = true, Error = ex.Message };
        }
        finally
        {
            _loading = false;
        }
        // 拉到新 registry = 拿到新禁用名单：必须立刻执行一次，否则「已启用但开关被置灰」。
        if (_catalog is { Offline: false, FromCache: false })
            _service.ApplyBlocklist();
        UpdateStatusBar();
        // tab 上的「更新 N」依赖 catalog；catalog 到货后不重建 tab 就会一直显示 0。
        BuildTabs();
        Render();
    }

    void UpdateStatusBar()
    {
        if (_catalog == null)
        {
            StatusBar.Visibility = Visibility.Collapsed;
            return;
        }
        var when = _catalog.FetchedAtUtc?.ToLocalTime().ToString("MM-dd HH:mm") ?? "—";
        if (_catalog.Offline)
        {
            ShowStatus(Loc.Get("Plugin.Offline", when));
        }
        else if (_catalog.FromCache)
        {
            // 6 小时内用缓存是正常路径，不能报「无法连接 GitHub」。
            ShowStatus(Loc.Get("Plugin.CachedAt", when));
        }
        else
        {
            StatusBar.Visibility = Visibility.Collapsed;
        }
    }

    void ShowStatus(string text)
    {
        StatusText.Text = text;
        StatusBar.Visibility = Visibility.Visible;
    }

    void Render()
    {
        // 关闭期不要再重建视觉树：Application.DoShutdown() 是「先关所有窗口、最后才
        // BeginInvokeShutdown」，所以 modal 的 ShowDialog() 会在关闭已经开始之后才返回，
        // 调用方（ShowDetail）随后继续往下执行。此时 new PluginCard() 里 InitializeComponent()
        // 会撞上 Application.LoadComponent 的「应用程序正在关闭」守卫并抛 InvalidOperationException
        // （2026-11-14 关托盘退出时弹「DeskOrder 错误」就是这条）。
        // Dispatcher.HasShutdownStarted 是「dispatcher 已开始关」，AppLifetime 是「进程已经
        // 决定退出」（更早、且覆盖不走 tray 的退出路径）；两者互补，关闭期一律不重建视觉树。
        if (Dispatcher.HasShutdownStarted || AppLifetime.IsShuttingDown) return;
        if (Window.GetWindow(this) is { IsLoaded: false }) return;

        var installed = _service.Records.Count;
        CountLabel.Text = Loc.Get("Plugin.Count", _catalog?.Entries.Count ?? 0, installed);

        var marketTab = _tab != 1;
        MarketScroll.Visibility = marketTab ? Visibility.Visible : Visibility.Collapsed;
        InstalledScroll.Visibility = marketTab ? Visibility.Collapsed : Visibility.Visible;
        Filters.Visibility = _tab == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (marketTab) RenderCards();
        else RenderInstalled();
        UpdateEmpty();
    }

    IEnumerable<PluginCatalogEntry> FilteredEntries()
    {
        if (_catalog == null) yield break;
        var query = SearchBox.Text?.Trim() ?? "";
        var visible = new List<PluginCatalogEntry>();
        foreach (var entry in _catalog.Entries)
        {
            // 被禁用 / 下架的不进市场列表：官方的处置动作应该立刻从货架上消失。
            if (PluginBlocklist.Find(entry.Id, entry.Version) != null) continue;
            if (!MatchesFilter(entry, _filter)) continue;
            var record = _service.GetRecord(entry.Id);
            if (_tab == 2 && (record == null || !PluginDetailDialog.IsUpdateAvailable(record, entry))) continue;
            if (query.Length > 0 && !Matches(entry, query)) continue;
            visible.Add(entry);
        }
        foreach (var entry in SortCatalogEntries(visible, _service.GetRecord))
            yield return entry;
    }

    /// <summary>
    /// 市场 / 更新 tab 的排序：有可用更新的条目最前 → 官方优先 → <c>PublishedAt</c> 降序。
    ///
    /// 「有更新」比「官方」更该被看见：用户装过的插件出新版，如果沉在几十张卡后面，
    /// 就等于没提示。更新 tab 沿用同一排序，两个 tab 的顺序保持一致。
    /// 刻意不改 <see cref="PluginCatalogService"/> 的解析排序：那是 registry 的默认顺序，
    /// 页面排序属于展示策略，放在 UI 层可以在不影响分发数据的前提下调整。
    /// </summary>
    internal static List<PluginCatalogEntry> SortCatalogEntries(
        IEnumerable<PluginCatalogEntry> entries, Func<string, PluginRecord?> findRecord)
    {
        return entries
            .Select(entry => new
            {
                Entry = entry,
                Update = findRecord(entry.Id) is { } record
                         && PluginDetailDialog.IsUpdateAvailable(record, entry)
            })
            .OrderByDescending(item => item.Update)
            .ThenByDescending(item => item.Entry.Official)
            .ThenByDescending(item => item.Entry.PublishedAt ?? DateTime.MinValue)
            // id 兜底：发布时间相同的条目顺序稳定，自测台断言与用户看到的顺序都可复现。
            .ThenBy(item => item.Entry.Id, StringComparer.Ordinal)
            .Select(item => item.Entry)
            .ToList();
    }

    /// <summary>
    /// 市场 / 更新 tab 的类型筛选：一个包多类型时命中「任一能力」即显示（主类型不决定筛选结果）。
    /// 抽成 static 纯函数，自测台直接断言筛选语义，不用把页面开起来点筛选芯片。
    /// </summary>
    internal static bool MatchesFilter(PluginCatalogEntry entry, PluginKind? filter)
        => filter == null || entry.KindValues.Contains(filter.Value);

    internal static bool Matches(PluginCatalogEntry entry, string query)
    {
        bool Has(string? value) =>
            !string.IsNullOrEmpty(value)
            && value.Contains(query, StringComparison.OrdinalIgnoreCase);
        return Has(entry.Id) || Has(entry.Name.Get("zh")) || Has(entry.Name.Get("en"))
               || Has(entry.Summary.Get("zh")) || Has(entry.Summary.Get("en"))
               || Has(entry.Description?.Get("zh")) || Has(entry.Description?.Get("en"))
               || Has(entry.Repo)
               || Has(entry.Author?.Name) || Has(entry.Author?.Github)
               || (entry.Tags?.Any(Has) ?? false);
    }

    void RenderCards()
    {
        CardsHost.Children.Clear();
        foreach (var entry in FilteredEntries())
        {
            var record = _service.GetRecord(entry.Id);
            var update = record != null && PluginDetailDialog.IsUpdateAvailable(record, entry);
            var card = new PluginCard();
            card.SetData(entry, record, update, _installing.Contains(entry.Id));
            card.ActionRequested += OnCardAction;
            card.OpenRequested += OnCardOpen;
            CardsHost.Children.Add(card);
            _ = LoadCardPreviewAsync(card, entry);
        }
    }

    async System.Threading.Tasks.Task LoadCardPreviewAsync(PluginCard card, PluginCatalogEntry entry)
    {
        var url = entry.PreviewUrls is { Count: > 0 } ? entry.PreviewUrls[0] : entry.IconUrl;
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!_imageCache.TryGetValue(url, out var image))
        {
            // 命中本地缓存时 GetImageAsync 同步返回、续体不真正切线程；只有真去下载才会让出。
            var wasLoaded = card.IsLoaded;
            var path = await _service.Catalog.GetImageAsync(url);
            image = path == null ? null : LoadBitmap(path);
            _imageCache[url] = image;
            // 下载期间卡片可能已被下一次 Render 从 CardsHost 摘下（Parent 变 null），或
            // 页面已卸载（IsLoaded 由 true 变 false）。刚 Add 进来还没走布局时 IsLoaded 也是
            // false，所以只把「true → false」当作卸载信号，避免误伤刚建好的卡片。
            if (card.Parent == null || (wasLoaded && !card.IsLoaded)) return;
        }
        if (image != null && ReferenceEquals(card.Entry.Id, entry.Id)) card.SetPreview(image);
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

    // ── 卡片操作 ──

    void OnCardAction(PluginCard card)
    {
        if (_busy) return;
        var record = _service.GetRecord(card.Entry.Id);
        var update = record != null && PluginDetailDialog.IsUpdateAvailable(record, card.Entry);
        if (record != null && !update)
        {
            if (!record.Enabled) EnableRecord(record);
            return;
        }
        _ = InstallEntryAsync(card.Entry, card);
    }

    void OnCardOpen(PluginCard card)
        => ShowDetail(card.Entry, _service.GetRecord(card.Entry.Id));

    async System.Threading.Tasks.Task<bool> InstallEntryAsync(PluginCatalogEntry entry, PluginCard? card = null)
    {
        if (_installing.Contains(entry.Id)) return false;
        var existing = _service.GetRecord(entry.Id);
        var isUpdate = existing != null && PluginDetailDialog.IsUpdateAvailable(existing, entry);

        // minAppVersion / apiVersion 门禁：卡片和详情弹窗已经提前置灰，这里再挡一次，
        // 防止从别的入口（lab、将来的深链）绕过；文案与卡片 ToolTip 完全同源。
        if (PluginDisplay.IncompatibilityReason(entry) is { } incompatible)
        {
            MessageBox.Show(Window.GetWindow(this), incompatible,
                Loc["Plugin.PageTitle"], MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        string name;
        if (isUpdate)
        {
            // 更新必须先看 changelog + 权限差异；新增联网权限要重新勾选。
            var dialog = new PluginUpdateDialog(entry, existing!) { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() != true) return false;
            name = entry.Name.Get(Loc.CurrentLanguage);
        }
        else
        {
            var prompt = BuildPrompt(entry);
            var dialog = new PluginInstallDialog(prompt) { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() != true) return false;
            name = prompt.Name;
        }

        _installing.Add(entry.Id);
        card?.SetBusy(true);
        try
        {
            var result = await _service.Installer!.InstallFromCatalogAsync(entry);
            _service.RefreshRecords();
            if (!result.Success)
            {
                MessageBox.Show(Window.GetWindow(this), Loc.Get("Plugin.InstallFailed", result.Error ?? ""),
                    Loc["Plugin.PageTitle"], MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            var isCode = entry.KindValue.IsCode();
            var headline = isUpdate
                ? (isCode
                    ? Loc.Get("Plugin.Update.RestartDone", name, entry.Version)
                    : Loc.Get("Plugin.Update.Done", name, entry.Version))
                // 声明式包安装即生效，说「默认未启用」是错的。
                : (isCode
                    ? Loc.Get("Plugin.InstallDone", name)
                    : Loc.Get("Plugin.Installed") + " · " + name);
            MessageBox.Show(Window.GetWindow(this), headline
                    + (entry.KindValue switch
                    {
                        PluginKind.Language => "\n\n" + Loc["Plugin.Language.Choose"],
                        PluginKind.Appearance => "\n\n" + Loc["Plugin.Appearance.Hint"],
                        _ => ""
                    }),
                Loc["Plugin.PageTitle"], MessageBoxButton.OK, MessageBoxImage.Information);
            _manifestCache.Clear();
            // 代码类更新后要重启才生效；不提示的话用户会以为「更新成功了但版本没变」。
            if (isUpdate && isCode) PromptRestart();
            return true;
        }
        finally
        {
            _installing.Remove(entry.Id);
            Render();
        }
    }

    PluginInstallPrompt BuildPrompt(PluginCatalogEntry entry)
    {
        var kind = PluginDisplay.DescribeKinds(entry.KindValues);
        var described = PluginDisplay.DescribePermissions(entry.Permissions);
        var permissions = string.IsNullOrEmpty(described) ? Loc["Plugin.Permissions.None"] : described;
        return new PluginInstallPrompt
        {
            Name = entry.Name.Get(Loc.CurrentLanguage),
            KindLine = kind,
            Author = entry.Official
                ? $"{entry.Author.Name} · {Loc["Plugin.Official"]}"
                : (string.IsNullOrWhiteSpace(entry.Author.Github)
                    ? Loc["Plugin.InstallConfirm.UnknownAuthor"]
                    : $"{entry.Author.Name} · @{entry.Author.Github}"),
            Version = entry.Version,
            Size = entry.Size > 0 ? FormatSize(entry.Size) : "",
            Sha256 = entry.Sha256 ?? "",
            Permissions = permissions,
            Contents = entry.KindValue switch
            {
                PluginKind.Language when entry.Language != null => Loc.Get("Plugin.Contents.Language",
                    entry.Language.NativeName, (int)Math.Round(entry.Language.Coverage * 100),
                    entry.Language.Base),
                PluginKind.Appearance => Loc["Plugin.Appearance.Hint"],
                _ => ""
            },
            Declarative = !entry.KindValue.IsCode()
        };
    }

    static string FormatSize(long bytes)
        => bytes >= 1024 * 1024
            ? (bytes / 1024d / 1024d).ToString("0.0") + " MB"
            : (bytes / 1024d).ToString("0") + " KB";

    // ── 启用 / 禁用 ──

    void EnableRecord(PluginRecord record)
    {
        if (!_service.SetEnabled(record.Id, true))
        {
            // 禁用名单命中：服务层已经写好原因，这里只负责让用户看见。
            MessageBox.Show(Window.GetWindow(this), Loc["Plugin.Blocked.CannotEnable"],
                Loc["Plugin.PageTitle"], MessageBoxButton.OK, MessageBoxImage.Warning);
            Render();
            return;
        }
        Render();
        // 声明式包（语言包）即时生效，不需要重启。
        if (record.KindValue.IsCode()) PromptRestart();
    }

    bool Blocked(PluginRecord record) => PluginBlocklist.Find(record.Id, record.Version) != null;

    /// <summary>
    /// 已安装行是否要显示「更新」按钮：registry 里存在同 id 的新版本，且当前版本没被禁用名单拦截。
    /// registry 里没有这条（已下架 / 纯本地包）时自然不显示。抽成纯函数供页面与自测台共用。
    /// </summary>
    internal static bool ShouldOfferRowUpdate(PluginRecord record, PluginCatalogEntry? entry)
        => entry != null
           && PluginBlocklist.Find(record.Id, record.Version) == null
           && PluginDetailDialog.IsUpdateAvailable(record, entry);

    /// <summary>记录来自市场，但当前 registry 里已经没有这条 → 已下架（文件保留、不再更新）。</summary>
    bool IsDelisted(PluginRecord record)
        => string.Equals(record.Source, "market", StringComparison.OrdinalIgnoreCase)
           // 拿到过一份真 registry（不是离线、不是解析失败）才有资格判「已下架」；
           // 空市场的合法情况也能判，否则官方清空 plugins[] 做紧急处置时所有下架提示同时失效。
           && _catalog is { Offline: false, Error: null }
           && FindCatalogEntry(record.Id) == null;

    void DisableRecord(PluginRecord record)
    {
        _service.SetEnabled(record.Id, false);
        Render();
    }

    void PromptRestart()
    {
        var result = MessageBox.Show(Window.GetWindow(this),
            Loc["Plugin.RestartNeeded"] + "\n\n" + Loc["Plugin.RestartNow"] + "?",
            Loc["Plugin.RestartNow"], MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result == MessageBoxResult.Yes) RestartApp();
    }

    static void RestartApp()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            DzTrace.Always("[Plugin] restart failed: " + ex.Message);
        }
    }

    // ── 已安装列表 ──

    void RenderInstalled()
    {
        InstalledHost.Children.Clear();
        var query = SearchBox.Text?.Trim() ?? "";
        foreach (var record in _service.Records.OrderByDescending(r => r.InstalledAtUtc))
        {
            if (query.Length > 0
                && !MatchesInstalled(record, ReadManifest(record), FindCatalogEntry(record.Id), query))
                continue;
            InstalledHost.Children.Add(BuildInstalledRow(record));
        }
    }

    /// <summary>
    /// 已安装 tab 的搜索：id / 中英名 / 作者（name、github）/ registry 标签 / 插件类型本地化名，
    /// 任一命中即可。抽成 static 纯函数是为了让自测台能直接断言搜索命中，不用把页面开起来。
    /// </summary>
    internal static bool MatchesInstalled(
        PluginRecord record, PluginManifest? manifest, PluginCatalogEntry? entry, string query)
    {
        bool Has(string? value) => !string.IsNullOrEmpty(value)
                                   && value.Contains(query, StringComparison.OrdinalIgnoreCase);
        if (Has(record.Id)) return true;
        // manifest 读不出来（安装损坏 / 目录被手工改过）时保持旧行为：不因为搜不到就把它藏起来，
        // 用户还需要在列表里看到它并卸载。id 命中仍然走上面那条。
        if (manifest == null) return true;
        if (Has(manifest.Name.Get("zh")) || Has(manifest.Name.Get("en"))) return true;
        // 作者名与 GitHub 账号：用户经常按「谁做的」找插件。
        if (Has(manifest.Author?.Name) || Has(manifest.Author?.Github)) return true;
        // registry 标签（时钟 / 剪贴板 / 示例…）只在市场条目里有，本地包没有标签。
        if (entry?.Tags != null && entry.Tags.Any(Has)) return true;
        // 类型名（桌面组件 / 数据源…）：manifest 里的 kind 是英文标识，搜「数据源」得翻成当前语言；
        // 一个包多类型时任一能力命中即可。
        return record.KindValues.Any(kind => Has(Loc[kind.I18nKey()]));
    }

    FrameworkElement BuildInstalledRow(PluginRecord record)
    {
        var manifest = ReadManifest(record);
        var catalogEntry = FindCatalogEntry(record.Id);
        var entry = catalogEntry ?? BuildSyntheticEntry(record, manifest);
        var name = manifest?.Name.Get(Loc.CurrentLanguage) ?? record.Id;
        if (string.IsNullOrWhiteSpace(name)) name = record.Id;

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

        var icon = BuildInstalledIcon(record);
        icon.Margin = new Thickness(0, 0, 14, 0);
        icon.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeBrushes.TextPrimaryModern,
            VerticalAlignment = VerticalAlignment.Center
        });
        if (record.Official)
        {
            titleRow.Children.Add(new Border
            {
                Margin = new Thickness(8, 0, 0, 0),
                Height = 18,
                Padding = new Thickness(7, 0, 7, 0),
                CornerRadius = new CornerRadius(9),
                Background = ThemeBrushes.AccentWashModern,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = Loc["Plugin.Official"],
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = ThemeBrushes.AccentModern,
                    VerticalAlignment = VerticalAlignment.Center
                }
            });
        }
        info.Children.Add(titleRow);

        var kind = PluginDisplay.DescribeKinds(record.KindValues);
        var source = PluginDisplay.DescribeSource(record.Source);
        info.Children.Add(new TextBlock
        {
            Text = $"{kind} · v{record.Version} · {source} · {record.InstalledAtUtc.ToLocalTime():yyyy-MM-dd}",
            FontSize = 11,
            Margin = new Thickness(0, 4, 0, 0),
            // 已安装行右侧按钮多（启用/新建/详情/卸载），窗口窄时这一行会被硬裁掉半行字；
            // 用省略号收尾，比「桌面组件」后面直接消失更可读。
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = ThemeBrushes.TextTertiaryModern
        });

        var widgets = _service.CountWidgets(record.Id);
        var status = record.KindValues.Contains(PluginKind.Widget) && widgets > 0
            ? Loc.Plural("Plugin.WidgetCount", widgets)
            : "";
        var statusIsError = false;
        if (record.KindValue == PluginKind.Language && manifest?.Language != null)
        {
            var language = manifest.Language;
            var pack = LocalizationService.Instance.GetLanguagePack(language.Code);
            var coverage = pack?.CoveragePercent ?? (int)Math.Round(language.Coverage * 100);
            status = $"{language.NativeName} · {Loc.Get("Plugin.Language.Coverage", coverage)}";
            if (string.Equals(LocalizationService.Instance.CurrentLanguage, language.Code,
                    StringComparison.OrdinalIgnoreCase))
                status += " · " + Loc["Plugin.Language.Active"];
        }
        else if (record.KindValue == PluginKind.Appearance)
        {
            var summary = AppearancePackService.Instance.GetSummary(record.Id);
            if (summary is { } counts)
                status = Loc.Get("Plugin.Appearance.Status", counts.Materials, counts.Presets);
        }
        if (!record.Enabled && widgets > 0)
        {
            status = Loc.Get("Plugin.OrphanWidgets", widgets);
            statusIsError = true;
        }
        if (PluginService.Instance.LoadErrors.TryGetValue(record.Id, out var error))
        {
            status = error;
            statusIsError = true;
        }
        // 禁用名单优先于其它状态文案：这是「官方已经动手」的状态，不能被覆盖成普通错误。
        var blocked = PluginBlocklist.Find(record.Id, record.Version);
        if (blocked != null)
        {
            status = Loc.Get("Plugin.Blocked.Banner",
                PluginBlocklist.ReasonOf(blocked, Loc.CurrentLanguage));
            statusIsError = true;
        }
        else if (IsDelisted(record))
        {
            status = Loc["Plugin.Delisted"];
        }
        if (!string.IsNullOrEmpty(status))
        {
            info.Children.Add(new TextBlock
            {
                Text = status,
                FontSize = 11,
                Margin = new Thickness(0, 4, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Foreground = statusIsError ? ThemeBrushes.DangerModern : ThemeBrushes.TextSecondaryModern
            });
        }
        Grid.SetColumn(info, 1);
        grid.Children.Add(info);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (record.KindValue is PluginKind.Widget or PluginKind.ZoneBar or PluginKind.Tool
            or PluginKind.Language or PluginKind.Appearance or PluginKind.Integration)
        {
            var toggle = new Button
            {
                Content = record.Enabled ? Loc["Plugin.Disable"] : Loc["Plugin.Enable"],
                Height = 30,
                Width = 78,
                FontSize = 11.5,
                Cursor = Cursors.Hand,
                Style = (Style)FindResource("OutlineBtn")
            };
            toggle.Click += (_, _) =>
            {
                if (record.Enabled) DisableRecord(record);
                else EnableRecord(record);
            };
            if (Blocked(record))
            {
                // 禁用名单命中：开关置灰，只能卸载。
                toggle.IsEnabled = false;
                toggle.Opacity = 0.45;
                toggle.ToolTip = Loc["Plugin.Blocked.CannotEnable"];
            }
            actions.Children.Add(toggle);
        }

        // widget 插件启用后，行上直接给「新建组件」入口（与「插件组件」页共用同一个菜单）；
        // 已启用但描述符还没加载（装了没重启/加载失败）时禁用并提示重启。
        // 行上已有 4 个按钮，这里只放一个「＋」图标按钮（文字入口在插件组件页与详情弹窗里），
        // 否则 84px 的文字按钮会把左侧信息列挤到换行。
        if (record.KindValues.Contains(PluginKind.Widget) && record.Enabled)
        {
            var newWidget = new Button
            {
                // 用文本「＋」而不是 Icon.Plus 几何：OutlineBtn 的 Padding=(14,6) 会把 30px 按钮的
                // 内容区压到 2px 宽，几何图形会被裁成一条横杠（属性面板的 MakeAddButton 也是文本写法）。
                Content = "+",
                FontSize = 15,
                Padding = new Thickness(0),
                Height = 30,
                Width = 30,
                Margin = new Thickness(8, 0, 0, 0),
                Cursor = Cursors.Hand,
                Style = (Style)FindResource("OutlineBtn"),
                ToolTip = Loc["PluginWidget.New"]
            };
            if (_service.GetDescriptors(record.Id).Count > 0)
            {
                newWidget.Click += (_, _) => CreateWidgetFromRow(newWidget);
            }
            else
            {
                newWidget.IsEnabled = false;
                newWidget.Opacity = 0.45;
                newWidget.ToolTip = Loc["PluginWidget.NeedsRestart"];
            }
            actions.Children.Add(newWidget);
        }

        // 「更新到 x.y.z」：registry 里有同 id 的新版本才出现，走和市场卡片相同的
        // InstallEntryAsync（更新确认 / 权限差异 / 重启提示都在里面）。
        // 被禁用名单拦截的插件只允许卸载，不给更新入口。
        if (catalogEntry != null && ShouldOfferRowUpdate(record, catalogEntry))
        {
            var updateButton = new Button
            {
                Content = Loc["Plugin.Update"],
                Height = 30,
                Width = 70,
                Margin = new Thickness(8, 0, 0, 0),
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Cursor = Cursors.Hand,
                Style = (Style)FindResource("FillBtn"),
                ToolTip = Loc.Get("Plugin.UpdateAvailable", catalogEntry.Version)
            };
            var upgradeBlocked = PluginDisplay.IncompatibilityReason(catalogEntry);
            if (upgradeBlocked != null)
            {
                // 有更新但新版本要求更高宿主：入口保留（否则用户永远不知道有新版），置灰说明原因。
                updateButton.IsEnabled = false;
                updateButton.Opacity = 0.45;
                updateButton.ToolTip = upgradeBlocked;
            }
            else
            {
                updateButton.Click += async (_, _) =>
                {
                    if (_installing.Contains(record.Id)) return;
                    // 先禁用再 await：否则双击会弹第二个更新确认框。
                    updateButton.IsEnabled = false;
                    updateButton.Content = Loc["Plugin.Installing"];
                    try
                    {
                        await InstallEntryAsync(catalogEntry);
                    }
                    catch (Exception ex)
                    {
                        // async void 处理器漏出去的异常会进 DispatcherUnhandledException 弹
                        // 「DeskOrder 错误」；更新失败应该留在页面上说。
                        ShowStatus(Loc.Get("Plugin.InstallFailed", ex.Message));
                        DzTrace.Always($"[Plugin] update from the installed list failed: {ex}");
                    }
                    finally
                    {
                        Render();
                    }
                };
            }
            actions.Children.Add(updateButton);
        }

        var open = new Button
        {
            Content = Loc["Plugin.Detail"],
            Height = 30,
            Width = 70,
            Margin = new Thickness(8, 0, 0, 0),
            FontSize = 11.5,
            Cursor = Cursors.Hand,
            Style = (Style)FindResource("OutlineBtn")
        };
        open.Click += (_, _) => ShowDetail(entry, record);
        actions.Children.Add(open);

        var uninstall = new Button
        {
            Content = Loc["Plugin.Uninstall"],
            Height = 30,
            Width = 78,
            Margin = new Thickness(8, 0, 0, 0),
            FontSize = 11.5,
            Cursor = Cursors.Hand,
            Foreground = ThemeBrushes.DangerModern,
            Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0x5A, 0x5A)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0x5A, 0x5A)),
            Style = (Style)FindResource("OutlineBtn")
        };
        uninstall.Click += (_, _) => UninstallRecord(record);
        actions.Children.Add(uninstall);

        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);

        border.Child = grid;
        return border;
    }

    /// <summary>已安装行「新建组件」：打开与「插件组件」页共用的创建菜单，建完重绘本页。</summary>
    void CreateWidgetFromRow(FrameworkElement anchor)
        => PluginWidgetCreateMenu.Open(anchor, Window.GetWindow(this), _ => Render());

    FrameworkElement BuildInstalledIcon(PluginRecord record)
    {
        var border = new Border
        {
            Width = 42,
            Height = 42,
            CornerRadius = new CornerRadius(9),
            Background = ThemeBrushes.BgHoverModern,
            ClipToBounds = true
        };
        var manifest = ReadManifest(record);
        var iconFull = manifest != null ? PluginPaths.ResolveInside(
            PluginPaths.VersionDir(record.Id, record.Version), manifest.Icon) : null;
        if (iconFull != null && System.IO.File.Exists(iconFull))
        {
            var bitmap = LoadBitmap(iconFull);
            if (bitmap != null)
            {
                border.Child = new Image { Source = bitmap, Stretch = Stretch.Uniform };
                return border;
            }
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

    PluginManifest? ReadManifest(PluginRecord record)
    {
        var key = record.Id + "@" + record.Version;
        if (_manifestCache.TryGetValue(key, out var cached)) return cached;
        var dir = PluginPaths.VersionDir(record.Id, record.Version);
        var manifest = PluginPackages.ReadManifest(dir, out _);
        _manifestCache[key] = manifest;
        return manifest;
    }

    PluginCatalogEntry? FindCatalogEntry(string id)
        => _catalog?.Entries.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

    static PluginCatalogEntry BuildSyntheticEntry(PluginRecord record, PluginManifest? manifest)
        => new()
        {
            Id = record.Id,
            Kind = record.Kind,
            Kinds = record.Kinds,
            Name = manifest?.Name ?? new LocalizedText { Zh = record.Id, En = record.Id },
            Summary = manifest?.Summary ?? new LocalizedText(),
            Description = manifest?.Description,
            Author = manifest?.Author ?? new PluginAuthor(),
            Repo = manifest?.Repo ?? "",
            Version = record.Version,
            ApiVersion = manifest?.ApiVersion ?? 0,
            MinAppVersion = manifest?.MinAppVersion ?? "",
            License = manifest?.License ?? "",
            Permissions = manifest?.Permissions,
            Language = manifest?.Language
        };

    void UninstallRecord(PluginRecord record)
    {
        var manifest = ReadManifest(record);
        var name = manifest?.Name.Get(Loc.CurrentLanguage) ?? record.Id;
        var widgets = _service.CountWidgets(record.Id);
        if (MessageBox.Show(Window.GetWindow(this), Loc.Get("Plugin.UninstallConfirm", name, widgets),
                Loc["Plugin.Uninstall"], MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        // 插件数据（Plugins\data\<id>）默认保留；有数据时再问一次要不要一起删。
        var deleteData = false;
        if (System.IO.Directory.Exists(PluginPaths.PluginDataDir(record.Id)))
        {
            deleteData = MessageBox.Show(Window.GetWindow(this),
                    Loc["Plugin.UninstallDeleteDataPrompt"],
                    Loc["Plugin.Uninstall"], MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        }
        _service.Uninstall(record.Id, deleteData, out var error);
        if (error != null)
        {
            MessageBox.Show(Window.GetWindow(this), error, Loc["Plugin.Uninstall"],
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        _manifestCache.Clear();
        Render();
    }

    void ShowDetail(PluginCatalogEntry entry, PluginRecord? record)
    {
        var delisted = record != null && IsDelisted(record);
        var dialog = new PluginDetailDialog(entry, record, delisted) { Owner = Window.GetWindow(this) };
        dialog.InstallRequested += async () =>
        {
            // 这是 async void 匿名处理器：异常一旦冒出去就直接进 DispatcherUnhandledException
            // 弹「DeskOrder 错误」。安装失败要留在对话框里，不能让整页/整个进程背锅。
            try
            {
                dialog.SetBusy(true);
                var ok = await InstallEntryAsync(entry);
                dialog.SetBusy(false);
                if (ok)
                {
                    _service.RefreshRecords();
                    dialog.SetRecord(_service.GetRecord(entry.Id));
                }
            }
            catch (Exception ex)
            {
                dialog.SetBusy(false);
                ShowStatus(Loc.Get("Plugin.InstallFailed", ex.Message));
                DzTrace.Always($"[Plugin] install from the detail dialog failed: {ex}");
            }
        };
        dialog.ToggleRequested += () =>
        {
            var current = _service.GetRecord(entry.Id);
            if (current == null) return;
            if (current.Enabled) DisableRecord(current);
            else EnableRecord(current);
            dialog.SetRecord(_service.GetRecord(entry.Id));
        };
        dialog.UninstallRequested += () =>
        {
            var current = _service.GetRecord(entry.Id);
            if (current == null) return;
            dialog.Close();
            UninstallRecord(current);
        };
        dialog.NewWidgetRequested += () =>
        {
            // 菜单挂在弹窗里的「新建组件」按钮上（弹窗是模态的，用弹窗自己做 owner）。
            PluginWidgetCreateMenu.Open(dialog.NewWidgetButton, dialog, widget =>
            {
                if (widget == null) return;
                // 建完把弹窗状态区里的「N 个组件」刷新一下；市场列表在 ShowDialog 返回后统一重绘。
                dialog.SetRecord(_service.GetRecord(entry.Id));
            });
        };
        dialog.ShowDialog();
        // 应用退出时 WPF 会强制关掉这个模态窗口，ShowDialog() 立刻返回；页面已经 Unloaded，
        // 这时再 Render() 只会白跑一遍（而且在关闭期会抛「应用程序正在关闭」）。
        if (IsLoaded) Render();
    }

    // ── 本地导入 ──

    async void ImportLocal_Click(object sender, RoutedEventArgs e)
    {
        var open = new OpenFileDialog
        {
            Filter = "DeskOrder plugin (*.dopkg)|*.dopkg|All files (*.*)|*.*",
            CheckFileExists = true
        };
        if (open.ShowDialog(Window.GetWindow(this)) != true) return;

        var preview = _service.Installer!.PreviewPackage(open.FileName, out var error);
        if (preview == null)
        {
            MessageBox.Show(Window.GetWindow(this), Loc.Get("Plugin.ImportInvalid", error ?? ""),
                Loc["Plugin.PageTitle"], MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var manifest = preview.Manifest;
        var kind = manifest.KindValue;
        var describedPermissions = PluginDisplay.DescribePermissions(manifest.Permissions);
        var author = string.IsNullOrWhiteSpace(manifest.Author.Github)
            ? manifest.Author.Name
            : $"{manifest.Author.Name} · @{manifest.Author.Github}";
        // 官方签名的本地包：签名已验证过才算官方，光填 author/repo 不算。
        if (preview.Official)
            author += " · " + Loc["Plugin.Local.OfficialSigned"];
        var prompt = new PluginInstallPrompt
        {
            Name = manifest.Name.Get(Loc.CurrentLanguage),
            KindLine = Loc[kind.I18nKey()],
            Author = author,
            Version = manifest.Version,
            Size = FormatSize(preview.Size),
            Sha256 = preview.Sha256,
            Permissions = string.IsNullOrEmpty(describedPermissions)
                ? Loc["Plugin.Permissions.None"]
                : describedPermissions,
            Contents = preview.Contents,
            Declarative = !kind.IsCode()
        };
        var dialog = new PluginInstallDialog(prompt) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;

        var result = await _service.Installer.InstallFromFileAsync(open.FileName);
        _service.RefreshRecords();
        if (!result.Success)
        {
            MessageBox.Show(Window.GetWindow(this), Loc.Get("Plugin.InstallFailed", result.Error ?? ""),
                Loc["Plugin.PageTitle"], MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        MessageBox.Show(Window.GetWindow(this), (kind.IsCode()
                    ? Loc.Get("Plugin.InstallDone", prompt.Name)
                    : Loc["Plugin.Installed"] + " · " + prompt.Name)
                + (kind switch
                {
                    PluginKind.Language => "\n\n" + Loc["Plugin.Language.Choose"],
                    PluginKind.Appearance => "\n\n" + Loc["Plugin.Appearance.Hint"],
                    _ => ""
                }),
            Loc["Plugin.PageTitle"], MessageBoxButton.OK, MessageBoxImage.Information);
        // 本地导入同版本覆盖时也要让已安装列表读到新 manifest。
        _manifestCache.Clear();
        Render();
    }

    // ── 搜索 / 刷新 / 空态 ──

    void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateSearchHint();
        if (_tab == 1)
        {
            // 已安装 tab 之前直接 return，搜索框摆在那儿却完全没反应。
            RenderInstalled();
            UpdateEmpty();
            return;
        }
        RenderCards();
        UpdateEmpty();
    }

    void UpdateSearchHint()
        => SearchHint.Visibility = string.IsNullOrEmpty(SearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;

    void Refresh_Click(object sender, RoutedEventArgs e) => _ = RefreshAsync(force: true);

    /// <summary>空态「重试」：强制刷新（绕过 6 小时缓存 TTL），离线时也能立刻再试一次。</summary>
    void EmptyRetry_Click(object sender, RoutedEventArgs e) => _ = RefreshAsync(force: true);

    int CountUpdates()
    {
        if (_catalog == null) return 0;
        return _service.Records.Count(record =>
        {
            // 与市场/更新列表同口径：被禁用或已下架的不算「可更新」。
            if (PluginBlocklist.Find(record.Id, record.Version) != null) return false;
            var entry = FindCatalogEntry(record.Id);
            return entry != null && PluginDetailDialog.IsUpdateAvailable(record, entry);
        });
    }

    void UpdateEmpty()
    {
        var empty = _tab == 1 ? InstalledHost.Children.Count == 0 : CardsHost.Children.Count == 0;
        EmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        // 操作按钮可见性：市场空/离线 → 重试 + 本地导入；已安装空 → 只导入；
        // 更新空（「没有可用更新」是正常结果）→ 只重试，导入对新版本没有意义。
        EmptyRetryButton.Visibility = _tab == 1 ? Visibility.Collapsed : Visibility.Visible;
        EmptyImportButton.Visibility = _tab == 2 ? Visibility.Collapsed : Visibility.Visible;
        if (!empty) return;

        switch (_tab)
        {
            case 1:
                EmptyTitle.Text = Loc["Plugin.Empty.Installed"];
                EmptyBody.Text = "";
                break;
            case 2:
                EmptyTitle.Text = Loc["Plugin.Empty.Updates"];
                EmptyBody.Text = "";
                break;
            default:
                if (_catalog?.Offline == true)
                {
                    var when = _catalog.FetchedAtUtc?.ToLocalTime().ToString("MM-dd HH:mm") ?? "—";
                    EmptyTitle.Text = Loc.Get("Plugin.Offline", when);
                    // 原始 message 是 .NET 的英文异常文本，翻成「超时 / DNS / 证书」这类可读原因。
                    EmptyBody.Text = Loc.Get("Plugin.Empty.Offline.Body",
                        DescribeOfflineReason(_catalog.Error));
                }
                else
                {
                    EmptyTitle.Text = Loc["Plugin.Empty.Market"];
                    EmptyBody.Text = Loc["Plugin.Empty.Market.Body"];
                }
                break;
        }
    }

    /// <summary>把拉取 registry 的异常归成几类可读原因；识别不了就报「网络请求失败」。</summary>
    static string DescribeOfflineReason(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return Loc["Plugin.Offline.Network"];
        bool Has(params string[] words)
            => words.Any(word => error.Contains(word, StringComparison.OrdinalIgnoreCase));
        if (Has("timed out", "timeout", "TaskCanceled", "超时")) return Loc["Plugin.Offline.Timeout"];
        if (Has("resolve", "No such host", "Name or service", "DNS", "名称解析"))
            return Loc["Plugin.Offline.Dns"];
        if (Has("SSL", "TLS", "certificate", "证书")) return Loc["Plugin.Offline.Tls"];
        return Loc["Plugin.Offline.Network"];
    }
}
