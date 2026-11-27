using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using DeskOrder.PluginApi;
using DesktopZones.Helpers;
using DesktopZones.Models;
using DesktopZones.Views;

namespace DesktopZones.Services;

/// <summary>一个已加载的代码插件。</summary>
public sealed class LoadedPlugin
{
    public required PluginRecord Record { get; init; }
    public required PluginManifest Manifest { get; init; }
    public required string Directory { get; init; }
    public required PluginLoadContext Context { get; init; }
    public required IDeskOrderPlugin Instance { get; init; }
    public required PluginHostContext Host { get; init; }

    public PluginWidgetRegistry Registry => Host.Registry;
    public PluginZoneBayRegistry BayRegistry => Host.BayRegistry;
}

/// <summary>
/// 插件加载器 / 生命周期 / 组件窗口管理。单例，UI 线程使用。
/// </summary>
public sealed class PluginService
{
    public static PluginService Instance { get; } = new();

    PluginService()
    {
        _saveDebounce.Tick += (_, _) =>
        {
            _saveDebounce.Stop();
            if (_savePending) { _savePending = false; SaveWidgetsCore(); }
        };
    }

    ConfigService? _configService;
    bool _initialized;
    bool _suppressLoading;
    bool _themeHooked;
    readonly DispatcherTimer _saveDebounce = new() { Interval = TimeSpan.FromMilliseconds(300) };
    bool _savePending;

    // ── Phase 3（启动收尾）：代码插件加载延后到「10 startup complete」之后 ──
    /// <summary>本次 Initialize 把代码插件加载排到了启动收尾（见 <see cref="LoadCodePluginsDeferred"/>）。</summary>
    bool _codePluginsDeferred;
    /// <summary>代码插件的加载循环是否已经跑过（含同步与延后两条路径，防重复加载）。</summary>
    bool _codePluginsLoaded;

    public ObservableCollection<PluginRecord> Records { get; } = new();
    public ObservableCollection<PluginWidget> Widgets { get; } = new();

    public List<LoadedPlugin> Loaded { get; } = new();
    public Dictionary<string, string> LoadErrors { get; } = new(StringComparer.OrdinalIgnoreCase);

    readonly Dictionary<Guid, PluginWidgetWindow> _windows = new();
    /// <summary>开发者模式下插件 id → 源码输出目录（不写配置、退出即失效）。</summary>
    readonly Dictionary<string, string> _devDirs = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>禁用名单写进 LoadErrors 的原文，用于在解禁后精确撤回。</summary>
    readonly Dictionary<string, string> _blockedMessages = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 开发者模式目录（<c>--plugin-dev=&lt;dir&gt;</c>）。设置后启动时会把这个目录下
    /// 每个子目录当作一个「已启用」的代码插件直接加载：不校验 SHA、不写 plugins.json、
    /// 不计崩溃次数，退出即消失。
    /// </summary>
    public string? DevDirectory { get; set; }

    /// <summary>托盘通知出口（App 启动时注入）。参数：title, body。</summary>
    public Action<string, string>? NotifySink { get; set; }

    public PluginCatalogService Catalog { get; } = new();
    public PluginInstaller? Installer { get; private set; }

    public bool SafeMode => _suppressLoading;
    public bool IsInitialized => _initialized;

    /// <summary>
    /// 设置页「插件」总开关的本次进程快照。关闭后重启不恢复组件、不新建组件 ——
    /// 否则旧实例会以「插件不可用」占位窗的形式重新出现在桌面上。
    /// </summary>
    public bool PluginsEnabled { get; private set; } = true;

    /// <summary>自测台专用：模拟设置里关/开总开关后的进程内状态（不写配置、不重启）。</summary>
    internal void SetPluginsEnabledForLab(bool enabled) => PluginsEnabled = enabled;

    /// <summary>
    /// 启动时是否真的去加载代码插件（纯函数，自测台直接验真值表）。
    ///
    /// 三个条件缺一不可：插件总开关打开、用户没关「启动时加载插件」、本次进程没被
    /// <c>--no-plugins</c> / <c>--safe-mode</c> 压制。抽成纯函数是为了让这条语义能被
    /// 单独断言 —— Initialize 里的真实加载路径太贵，不适合在自测台里反复跑。
    /// 注意：语言/外观等声明式包不走这里（它们只受总开关约束）。
    /// </summary>
    internal static bool ShouldLoadCodePlugins(
        bool pluginsEnabled, bool loadPluginsAtStartup, bool suppressLoading)
        => pluginsEnabled && loadPluginsAtStartup && !suppressLoading;

    public event Action? PluginsChanged;
    public event Action<PluginWidget>? WidgetsChanged;

    // ── 更新角标（v1.2.3）──
    // 启动后台刷新 registry 时顺手算「可更新插件数」，侧栏角标 / 更新 tab / 托盘提醒共用。
    // 不为角标单独发网络请求：数据来自 RefreshBlocklistInBackground 已有的 LoadAsync。
    PluginCatalogResult? _lastCatalog;
    int _availableUpdateCount;
    /// <summary>托盘「有插件可更新」气泡每进程只弹一次（0 → &gt;0 的第一次跳变）。</summary>
    bool _availableUpdateToastShown;

    /// <summary>可更新插件数（0 = 没有 / 还没拿到 registry / 离线）。</summary>
    public int AvailableUpdateCount => _availableUpdateCount;

    /// <summary>可更新插件数发生变化（角标订阅；只在数值真的变化时触发）。</summary>
    public event Action? AvailableUpdatesChanged;

    /// <summary>
    /// 一条「已安装记录 + registry 条目」是否真的可以更新：比当前版本新、没被禁用名单
    /// 命中（旧版本/新版本都不行）、并且新版本兼容当前宿主。
    ///
    /// 市场列表、更新 tab、侧栏角标、批量更新计划全部走这一个判定，避免四个入口各写一份
    /// 规则后互相漂移（例如「tab 显示 3 条更新，点全部更新只处理 2 条」）。
    /// </summary>
    internal static bool IsUpdatable(PluginRecord record, PluginCatalogEntry entry)
        => PluginBlocklist.Find(record.Id, record.Version) == null
           && PluginBlocklist.Find(entry.Id, entry.Version) == null
           && PluginDetailDialog.IsUpdateAvailable(record, entry)
           && PluginDisplay.IncompatibilityReason(entry) == null;

    /// <summary>按一组 registry 条目统计可更新数（纯函数，自测台直接断言口径）。</summary>
    internal static int CountAvailableUpdatesInEntries(
        IEnumerable<PluginRecord> records, IEnumerable<PluginCatalogEntry>? entries)
    {
        if (entries == null) return 0;
        var byId = new Dictionary<string, PluginCatalogEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (!string.IsNullOrEmpty(entry.Id)) byId.TryAdd(entry.Id, entry);
        }
        var count = 0;
        foreach (var record in records)
        {
            if (byId.TryGetValue(record.Id, out var entry) && IsUpdatable(record, entry)) count++;
        }
        return count;
    }

    /// <summary>
    /// 按一次 catalog 加载结果统计：离线 / 解析失败 / 没有缓存 → 0（角标不能拿旧数据吓人，
    /// 这是「启动后台检查」的语义）。页面自己的 tab 计数另走
    /// <see cref="CountAvailableUpdatesInEntries"/>（列表里有几条就显示几条）。
    /// </summary>
    internal static int CountAvailableUpdates(IEnumerable<PluginRecord> records, PluginCatalogResult? catalog)
        => catalog is { Offline: false, Error: null }
            ? CountAvailableUpdatesInEntries(records, catalog.Entries)
            : 0;

    /// <summary>页面自己拉到的 registry 回填给服务：角标、tab、批量计划共用同一份数据。</summary>
    internal void PublishCatalogForUpdates(PluginCatalogResult? catalog)
    {
        _lastCatalog = catalog;
        RecountAvailableUpdates();
    }

    /// <summary>记录/禁用名单变化后重算可更新数（不需要重新联网）。</summary>
    void RecountAvailableUpdates() => PublishAvailableUpdateCount(CountAvailableUpdates(Records, _lastCatalog));

    /// <summary>写入新的可更新数；数值变化才发事件，0 → &gt;0 的第一次顺带弹一次托盘气泡。</summary>
    void PublishAvailableUpdateCount(int count)
    {
        if (count < 0) count = 0;
        var previous = _availableUpdateCount;
        if (previous == count) return;
        _availableUpdateCount = count;
        AvailableUpdatesChanged?.Invoke();
        if (previous == 0 && count > 0 && !_availableUpdateToastShown && !SuppressBackgroundRefreshForLab)
        {
            // 自测台把后台刷新整个关掉了（SuppressBackgroundRefreshForLab），也不该在跑用例时
            // 从托盘弹真实气泡抢焦点：同一个「lab 环境」判断用在两处即可。
            _availableUpdateToastShown = true;
            Notify(LocalizationService.Instance["Plugin.Toast.Updates.Title"],
                LocalizationService.Instance.Get("Plugin.Toast.Updates.Body", count));
        }
    }

    /// <summary>自测台专用：直接写入可更新数并走同一条事件广播（不弹托盘气泡）。</summary>
    internal void SetAvailableUpdateCountForLab(int count)
    {
        _availableUpdateToastShown = true;
        PublishAvailableUpdateCount(count);
    }

    /// <summary>
    /// 初始化插件系统。启动路径传 <paramref name="deferCodePlugins"/>=true：只做目录/记录/
    /// 禁用名单/声明式包这些轻量准备，代码插件的加载留到「10 startup complete」之后
    /// （见 <see cref="LoadCodePluginsDeferred"/>）。自测台 / 重载路径传 false，保持同步加载。
    /// </summary>
    public void Initialize(ConfigService configService, bool suppressLoading, bool deferCodePlugins = false)
    {
        if (_initialized) return;
        _initialized = true;
        _configService = configService;
        _suppressLoading = suppressLoading;
        _codePluginsDeferred = deferCodePlugins;
        _codePluginsLoaded = false;
        Installer = new PluginInstaller(configService);
        if (!_themeHooked)
        {
            ThemeService.Changed += _ =>
            {
                foreach (var window in _windows.Values.ToList())
                {
                    try { window.NotifyThemeChanged(); } catch { }
                }
            };
            _themeHooked = true;
        }

        try
        {
            PluginPaths.EnsureCreated();
        }
        catch (Exception ex)
        {
            // 只读/受限环境（低完整性、被策略锁定的 AppData）不能让插件把主程序带崩。
            DzTrace.Always("[Plugin] cannot create plugin directories, plugin system disabled: " + ex.Message);
            _initialized = false;
            return;
        }
        PluginInstaller.ProcessPendingDeletes();

        var config = configService.Load();
        PluginsEnabled = config.PluginsEnabled;
        if (!string.IsNullOrWhiteSpace(config.PluginRegistryUrl))
            Catalog.RegistryUrl = config.PluginRegistryUrl.Trim();

        Records.Clear();
        foreach (var record in config.Plugins) Records.Add(record);
        Widgets.Clear();
        foreach (var widget in config.PluginWidgets) Widgets.Add(widget);

        // 开发者模式先进（它只是往 Records 里塞临时记录），再让禁用名单统一裁决：
        // 否则开发中的插件会绕过 blocked[]，而它恰恰是最该被拦住的那类未发布代码。
        // 「启动时加载插件」关闭时开发者目录也跳过：它同样是执行中的第三方代码。
        if (config.LoadPluginsAtStartup) LoadDevPlugins(suppressLoading);
        // 紧急禁用名单要在「加载 / 同步声明式包」之前生效，否则被禁用的插件仍会跑一轮。
        ApplyBlocklist();

        // 语言包是声明式包：不加载代码，只要按记录重建索引即可生效。
        SyncLanguagePacks(config.PluginsEnabled);
        // 外观包同理：材质表按记录重建，预设文件在安装时已导入。
        SyncAppearancePacks(config.PluginsEnabled);

        // 三个条件都满足才加载代码插件：总开关开、用户没关「启动时加载」、本次进程没被
        // --no-plugins / --safe-mode 压制。声明式包在上面已经同步完，不受这三个条件影响
        // （只受总开关约束）—— 语言包是纯数据，不应该被「本轮不加载代码」连带关掉。
        var loadCodePlugins = ShouldLoadCodePlugins(
            config.PluginsEnabled, config.LoadPluginsAtStartup, suppressLoading);
        if (!loadCodePlugins)
        {
            if (!config.PluginsEnabled)
                DzTrace.Always("[Plugin] system disabled in config; skipping load");
            else if (!config.LoadPluginsAtStartup)
                DzTrace.Always("[Plugin] startup load disabled in config; skipping code plugins");
            else
                DzTrace.Always("[Plugin] safe mode; skipping load");
            // 同时跳过后台禁用名单刷新：本次没有任何代码插件在跑，等真正加载的那次启动
            // 再联网核对（禁用状态已由上面的 ApplyBlocklist 用本地副本执行过）。
            return;
        }

        // Phase 3（启动收尾）：启动路径把代码插件加载排到「10 startup complete」之后，
        // 见 App.OnDeferredStartupWork → LoadCodePluginsDeferred。
        if (deferCodePlugins)
        {
            DzTrace.Always("[Plugin] code plugin loading deferred to idle");
            return;
        }
        LoadCodePlugins();
    }

    /// <summary>
    /// Phase 3（启动收尾）：在「10 startup complete」之后由 App 在 ApplicationIdle 调用，
    /// 加载启动期被延后的代码插件。幂等；安全模式/总开关关闭/未初始化时是空操作。
    /// </summary>
    /// <returns>本次是否真的执行了加载循环（调用方据此决定要不要补恢复插件挂件窗口）。</returns>
    public bool LoadCodePluginsDeferred()
    {
        if (!_initialized || !_codePluginsDeferred || _codePluginsLoaded) return false;
        if (_suppressLoading || !PluginsEnabled) return false;
        LoadCodePlugins();
        return true;
    }

    /// <summary>代码插件加载循环（同步启动路径 / 延后启动收尾共用；异常处理与原启动期一致）。</summary>
    void LoadCodePlugins()
    {
        if (_codePluginsLoaded) return;
        _codePluginsLoaded = true;

        foreach (var record in Records.Where(r => r.KindValue.IsCode() && r.Enabled).ToList())
        {
            try { LoadRecord(record); }
            catch (Exception ex) { RecordLoadFailure(record, ex); }
        }
        DzTrace.Always($"[Plugin] loaded {Loaded.Count} plugin(s)");

        // 安装了插件才有必要联网核对禁用名单：否则「从不打开市场页」的用户永远收不到
        // 紧急禁用（LoadAsync 内部有 6 小时缓存，不会每次启动都打网络）。
        RefreshBlocklistInBackground();
    }

    /// <summary>
    /// 自测台专用：禁用「启动后台拉取 registry」。用例会自己构造缓存/禁用名单，
    /// 真实后台拉取会在用例跑到一半时把缓存覆盖成线上数据（2026-11-14 的 cache 用例偶发就是这样）。
    /// </summary>
    internal static bool SuppressBackgroundRefreshForLab { get; set; }

    /// <summary>后台刷新一次 registry，把新的 blocked[] 落到本地并立即执行。</summary>
    void RefreshBlocklistInBackground()
    {
        if (SuppressBackgroundRefreshForLab) return;
        if (Records.Count == 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await Catalog.LoadAsync(force: false).ConfigureAwait(false);
                // 缓存/离线只读不写，也不需要再执行一次（启动时 ApplyBlocklist 已用过本地副本）；
                // 但可更新数要用这份结果算出来（缓存命中也算数），否则「6 小时内启动」永远没有角标。
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher == null) return;
                await dispatcher.InvokeAsync(() =>
                {
                    if (!result.FromCache && !result.Offline) ApplyBlocklist();
                    PublishCatalogForUpdates(result);
                });
            }
            catch (Exception ex)
            {
                DzTrace.Always("[Plugin] background blocklist refresh failed: " + ex.Message);
            }
        });
    }

    /// <summary>重新加载（自测台 / 启用后重启模拟）。</summary>
    public void ReloadForLab()
    {
        ShutdownAll();
        Loaded.Clear();
        _initialized = false;
        if (_configService != null) Initialize(_configService, suppressLoading: false);
        RestoreWidgets();
    }

    /// <summary>
    /// 扫描开发者模式目录：每个含 <c>plugin.json</c> 的子目录算一个插件。
    /// 记进 <see cref="Records"/> 但不落盘，界面上带「开发中」标记。
    /// 安全模式（<c>--safe-mode</c>）下不扫：那时连已安装的代码插件都不加载，
    /// 再把开发中的挂上去会让「安全模式」名不副实。
    /// </summary>
    void LoadDevPlugins(bool suppressLoading)
    {
        _devDirs.Clear();
        var root = DevDirectory;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;
        if (suppressLoading)
        {
            DzTrace.Always("[Plugin] dev: skipped because safe mode is on");
            return;
        }

        foreach (var dir in Directory.GetDirectories(root))
        {
            var manifest = PluginPackages.ReadManifest(dir, out var error);
            if (manifest == null)
            {
                DzTrace.Always($"[Plugin] dev: skip {Path.GetFileName(dir)}: {error}");
                continue;
            }
            if (!manifest.KindValue.IsCode())
            {
                DzTrace.Always($"[Plugin] dev: {manifest.Id} is a declarative pack, not a code plugin");
                continue;
            }
            if (Records.Any(r => string.Equals(r.Id, manifest.Id, StringComparison.OrdinalIgnoreCase)))
            {
                DzTrace.Always($"[Plugin] dev: {manifest.Id} is already installed, dev copy ignored");
                continue;
            }

            Records.Add(new PluginRecord
            {
                Id = manifest.Id,
                Version = manifest.Version,
                Kind = manifest.KindValue.ToId(),
                Kinds = manifest.KindValues.Count > 1
                    ? manifest.KindValues.Select(k => k.ToId()).ToList()
                    : null,
                Enabled = true,
                Source = "dev",
                Signature = "unsigned",
                InstalledAtUtc = DateTime.UtcNow
            });
            _devDirs[manifest.Id] = dir;
            DzTrace.Always($"[Plugin] dev: {manifest.Id} {manifest.Version} -> {dir}");
        }
    }

    // ── 加载 ──

    void LoadRecord(PluginRecord record)
    {
        // 开发者模式：插件直接用源码输出目录加载，不写崩溃标记、不计崩溃次数，
        // 否则开发时改崩两次就会被自动禁用，得去删状态文件才能继续。
        var isDev = _devDirs.TryGetValue(record.Id, out var devDir);
        var marker = PluginPaths.LoadingMarker(record.Id);
        if (!isDev && File.Exists(marker))
        {
            // 上次加载没收尾 → 记为一次崩溃，本次不加载。
            record.CrashCount++;
            try { File.Delete(marker); } catch { }
            DzTrace.Always($"[Plugin] {record.Id} left a .loading marker (crash #{record.CrashCount})");
            if (record.CrashCount >= 2)
            {
                record.Enabled = false;
                LoadErrors[record.Id] = LocalizationService.Instance.Get("Plugin.Crash.AutoDisabled", record.Id);
            }
            PluginIntegrationService.Instance.RemovePlugin(record.Id);
            PluginSettingsService.Instance.RemovePlugin(record.Id);
            PersistRecords();
            return;
        }

        var dir = isDev ? devDir! : PluginPaths.VersionDir(record.Id, record.Version);
        if (!Directory.Exists(dir))
        {
            LoadErrors[record.Id] = "files are missing; reinstall the plugin";
            record.Enabled = false;
            PluginIntegrationService.Instance.RemovePlugin(record.Id);
            PluginSettingsService.Instance.RemovePlugin(record.Id);
            PersistRecords();
            return;
        }

        var manifest = PluginPackages.ReadManifest(dir, out var manifestError);
        if (manifest == null)
        {
            LoadErrors[record.Id] = manifestError ?? "plugin.json is invalid";
            record.Enabled = false;
            PluginIntegrationService.Instance.RemovePlugin(record.Id);
            PluginSettingsService.Instance.RemovePlugin(record.Id);
            PersistRecords();
            return;
        }
        if (manifest.Entry == null)
        {
            LoadErrors[record.Id] = "entry is missing";
            record.Enabled = false;
            PluginIntegrationService.Instance.RemovePlugin(record.Id);
            PluginSettingsService.Instance.RemovePlugin(record.Id);
            PersistRecords();
            return;
        }

        if (!isDev) File.WriteAllText(marker, DateTime.UtcNow.ToString("O"));
        PluginLoadContext? context = null;
        try
        {
            context = new PluginLoadContext(dir);
            var assemblyPath = PluginPaths.ResolveInside(dir, manifest.Entry.Assembly)
                               ?? throw new InvalidOperationException("entry assembly path is unsafe");
            var assembly = context.LoadFile(assemblyPath);
            var type = assembly.GetType(manifest.Entry.Type, throwOnError: false)
                       ?? throw new InvalidOperationException($"entry type not found: {manifest.Entry.Type}");
            if (!typeof(IDeskOrderPlugin).IsAssignableFrom(type))
                throw new InvalidOperationException("entry type does not implement IDeskOrderPlugin");

            var instance = (IDeskOrderPlugin)Activator.CreateInstance(type)!;
            var registry = new PluginWidgetRegistry();
            var bayRegistry = new PluginZoneBayRegistry();
            var toolRegistry = new PluginToolRegistry();
            var integrationRegistry = new PluginIntegrationRegistry();
            var settingsRegistry = new PluginSettingsRegistry();
            // 插件级存储只建一次：插件拿到的 IPluginHost.Storage 与设置页保存用的是同一个实例，
            // 所以设置窗口保存后插件在本次进程里用 host.Storage.Get("settings.x") 就能读到新值。
            var pluginStorage = PluginStorage.Create(record.Id);
            var host = new PluginHostContext
            {
                PluginId = record.Id,
                HostVersion = PluginInstaller.HostVersion,
                Storage = pluginStorage,
                Ui = new PluginUiFacade(Notify),
                Widgets = registry,
                ZoneBays = bayRegistry,
                Tools = toolRegistry,
                Integrations = integrationRegistry,
                Settings = settingsRegistry,
                Log = new PluginLogger(record.Id)
            };

            instance.Initialize(host);
            // 工具命令/数据源/设置 schema 不进插件程序集持久化：每次加载重新收集，
            // 数量为 0 也发布一次（清掉上一轮残留）。
            PluginToolService.Instance.Publish(record, manifest, toolRegistry.Commands);
            PluginIntegrationService.Instance.Publish(record, manifest, integrationRegistry.Sources);
            PluginSettingsService.Instance.Publish(record, manifest, settingsRegistry.Schema, pluginStorage);

            var loaded = new LoadedPlugin
            {
                Record = record,
                Manifest = manifest,
                Directory = dir,
                Context = context,
                Instance = instance,
                Host = host
            };
            Loaded.Add(loaded);
            record.CrashCount = 0;
            record.LastLoadedUtc = DateTime.UtcNow;
            LoadErrors.Remove(record.Id);
            try { File.Delete(marker); } catch { }
            PersistRecords();
            if (loaded.BayRegistry.Descriptors.Count > 0)
                ZoneBayService.Instance.OnPluginLoaded(loaded);
            DzTrace.Always($"[Plugin] {record.Id} {record.Version} loaded ({registry.Descriptors.Count} widget type(s), {bayRegistry.Descriptors.Count} zone bay type(s), {toolRegistry.Commands.Count} tool command(s), {integrationRegistry.Sources.Count} media source(s))");
        }
        catch (Exception ex)
        {
            context?.Unload();
            // Initialize 成功之后、LoadRecord 收尾之前还可能失败（例如坞插件建窗）；
            // 那时工具命令/数据源/设置已经发布了，失败路径必须撤掉，否则菜单/下拉/设置窗口里会留下不可用项。
            PluginToolService.Instance.RemovePlugin(record.Id);
            PluginIntegrationService.Instance.RemovePlugin(record.Id);
            PluginSettingsService.Instance.RemovePlugin(record.Id);
            try { File.Delete(marker); } catch { }
            RecordLoadFailure(record, ex);
        }
    }

    void RecordLoadFailure(PluginRecord record, Exception ex)
    {
        // 开发中的插件崩了不自动禁用：下一次重启就能复现，禁用只会挡住调试。
        var isDev = _devDirs.ContainsKey(record.Id);
        if (!isDev)
        {
            record.CrashCount++;
            record.Enabled = record.CrashCount < 2;
        }
        LoadErrors[record.Id] = LocalizationService.Instance.Get("Plugin.Error.LoadFailed",
            record.Id, ex.Message);
        PersistRecords();
        DzTrace.Always($"[Plugin] {record.Id} load failed (crash #{record.CrashCount}): {ex}");
    }

    void PersistRecords()
    {
        if (_configService == null) return;
        try
        {
            ConfigSaver.SavePreservingPanelSettings(_configService, cfg =>
            {
                // 开发者模式的记录只在本次进程内有效，绝不能写进用户配置。
                cfg.Plugins = Records.Where(r => !string.Equals(r.Source, "dev", StringComparison.OrdinalIgnoreCase)).ToList();
            });
        }
        catch (Exception ex)
        {
            DzTrace.Always("[Plugin] failed to persist records: " + ex.Message);
        }
        PluginsChanged?.Invoke();
    }

    // ── 查询 ──

    public PluginRecord? GetRecord(string pluginId)
        => Records.FirstOrDefault(r => string.Equals(r.Id, pluginId, StringComparison.OrdinalIgnoreCase));

    /// <summary>安装器直接写配置后，把记录同步回内存集合。</summary>
    public void RefreshRecords()
    {
        if (_configService == null) return;
        var config = _configService.Load();
        Records.Clear();
        foreach (var record in config.Plugins) Records.Add(record);
        ApplyBlocklist();
        SyncLanguagePacks(config.PluginsEnabled);
        SyncAppearancePacks(config.PluginsEnabled);
        // 安装/卸载/启停可能改变「可更新数」（装上了最新版就少一条）：不用重新联网，按上次 catalog 重算。
        RecountAvailableUpdates();
        PluginsChanged?.Invoke();
    }

    /// <summary>
    /// 按本地禁用名单强制停用命中的插件。只停用、不删除：用户仍能在「已安装」里看到并卸载。
    /// 名单来源见 <see cref="PluginBlocklist"/>（registry 顶层 blocked[] 的本地副本）。
    /// </summary>
    public void ApplyBlocklist()
    {
        var changed = false;
        var language = LocalizationService.Instance.CurrentLanguage;
        var stillBlocked = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in Records.ToList())
        {
            var blocked = PluginBlocklist.Find(record.Id, record.Version);
            if (blocked == null) continue;

            var message = LocalizationService.Instance.Get(
                "Plugin.Blocked.Status", PluginBlocklist.ReasonOf(blocked, language));
            LoadErrors[record.Id] = message;
            stillBlocked[record.Id] = message;

            if (record.Enabled)
            {
                record.Enabled = false;
                record.CrashCount = 0;
                changed = true;
            }

            var loaded = GetLoaded(record.Id);
            if (loaded != null) UnloadOne(loaded);
            foreach (var widget in Widgets.Where(w => w.PluginId == record.Id).ToList())
            {
                // 只关窗不够：RestoreVisibleWidgets 会按 IsVisible 把被禁用的组件重新开成占位窗口。
                widget.IsVisible = false;
                CloseWindow(widget.Id);
            }
        }

        // 更新到干净版本后要把「已被禁用」清掉；只清我们自己写的那条，别误伤真正的加载失败。
        foreach (var pair in _blockedMessages)
        {
            if (stillBlocked.ContainsKey(pair.Key)) continue;
            if (LoadErrors.TryGetValue(pair.Key, out var current) && current == pair.Value)
                LoadErrors.Remove(pair.Key);
        }
        _blockedMessages.Clear();
        foreach (var pair in stillBlocked) _blockedMessages[pair.Key] = pair.Value;

        if (changed) PersistRecords();
        // 被禁用名单命中的插件不该继续算「可更新」：名单生效后立刻重算角标。
        if (_lastCatalog != null) RecountAvailableUpdates();
    }

    /// <summary>
    /// 按当前记录重建语言包索引，并让 <see cref="LocalizationService"/> 刷新语言列表。
    /// 安装/卸载/启停/启动都走这里，保证声明式包无「重启生效」这一层。
    /// </summary>
    void SyncLanguagePacks(bool? pluginsEnabled = null)
    {
        try
        {
            var enabled = pluginsEnabled ?? _configService?.Load().PluginsEnabled ?? true;
            foreach (var record in Records.Where(r => r.KindValue == PluginKind.Language).ToList())
            {
                // 被禁用名单命中的包：原因文案不能被「同步」擦掉（同步是常规路径，会反复跑）。
                if (PluginBlocklist.Find(record.Id, record.Version) == null)
                    LoadErrors.Remove(record.Id);
            }
            LanguagePackService.Instance.Refresh(Records, enabled);
            foreach (var pair in LanguagePackService.Instance.Errors)
                LoadErrors[pair.Key] = pair.Value;
            LocalizationService.Instance.ReloadLanguagePacks();
        }
        catch (Exception ex)
        {
            DzTrace.Always("[Plugin] language pack sync failed: " + ex.Message);
        }
    }

    /// <summary>按当前记录重建外观包材质表；预设文件在安装时导入，停用不删（重新启用即回来）。</summary>
    void SyncAppearancePacks(bool? pluginsEnabled = null)
    {
        try
        {
            var enabled = pluginsEnabled ?? _configService?.Load().PluginsEnabled ?? true;
            foreach (var record in Records.Where(r => r.KindValue == PluginKind.Appearance).ToList())
            {
                if (PluginBlocklist.Find(record.Id, record.Version) == null)
                    LoadErrors.Remove(record.Id);
            }
            AppearancePackService.Instance.Refresh(Records, enabled);
            foreach (var pair in AppearancePackService.Instance.Errors)
                LoadErrors[pair.Key] = pair.Value;
        }
        catch (Exception ex)
        {
            DzTrace.Always("[Plugin] appearance pack sync failed: " + ex.Message);
        }
    }

    public LoadedPlugin? GetLoaded(string pluginId)
        => Loaded.FirstOrDefault(p => string.Equals(p.Record.Id, pluginId, StringComparison.OrdinalIgnoreCase));

    public PluginWidgetDescriptor? FindDescriptor(string pluginId, string widgetTypeId)
        => GetLoaded(pluginId)?.Registry.Descriptors.TryGetValue(widgetTypeId, out var descriptor) == true
            ? descriptor
            : null;

    public IReadOnlyList<PluginWidgetDescriptor> GetDescriptors(string pluginId)
        => GetLoaded(pluginId)?.Registry.Descriptors.Values.ToList() ?? new List<PluginWidgetDescriptor>();

    /// <summary>
    /// 已加载且至少注册了一种组件的代码插件（顺序即 <see cref="Loaded"/> 的加载顺序）。
    /// 「插件组件」页与市场页的「新建组件」菜单共用：页面不再直接摸内部字典。
    /// </summary>
    public IReadOnlyList<(PluginRecord Record, IReadOnlyList<PluginWidgetDescriptor> Descriptors)>
        GetWidgetPlugins()
        => Loaded
            .Where(l => l.Registry.Descriptors.Count > 0)
            .Select(l => (l.Record, (IReadOnlyList<PluginWidgetDescriptor>)l.Registry.Descriptors.Values.ToList()))
            .ToList();

    public int CountWidgets(string pluginId)
        => Widgets.Count(w => string.Equals(w.PluginId, pluginId, StringComparison.OrdinalIgnoreCase));

    // ── 启用 / 禁用 / 卸载 ──

    public bool SetEnabled(string pluginId, bool enabled)
    {
        var record = GetRecord(pluginId);
        if (record == null) return false;
        if (enabled && PluginBlocklist.Find(record.Id, record.Version) is { } blocked)
        {
            // 禁用名单是安全动作：界面上开关置灰，但托盘/自测台也可能调到这里，双保险。
            LoadErrors[record.Id] = LocalizationService.Instance.Get(
                "Plugin.Blocked.Status",
                PluginBlocklist.ReasonOf(blocked, LocalizationService.Instance.CurrentLanguage));
            PluginsChanged?.Invoke();
            return false;
        }
        record.Enabled = enabled;
        if (!enabled) record.CrashCount = 0;
        PersistRecords();
        if (!enabled)
        {
            var loaded = GetLoaded(pluginId);
            if (loaded != null) UnloadOne(loaded);
            foreach (var widget in Widgets.Where(w => w.PluginId == pluginId).ToList())
                CloseWindow(widget.Id);
        }
        if (record.KindValue == PluginKind.Language) SyncLanguagePacks();
        if (record.KindValue == PluginKind.Appearance)
        {
            // 外观包停用 = 材质从下拉框消失、导入的预设也一并撤掉；重新启用时从包目录再导入。
            if (enabled) ReimportAppearancePresets(record);
            else AppearancePackService.Instance.DeletePresets(record.Id);
            SyncAppearancePacks();
        }
        return true;
    }

    /// <summary>重新启用外观包时，把预设重新复制进用户预设目录。</summary>
    void ReimportAppearancePresets(PluginRecord record)
    {
        try
        {
            var dir = PluginPaths.VersionDir(record.Id, record.Version);
            var manifest = PluginPackages.ReadManifest(dir, out var manifestError);
            if (manifest == null)
            {
                LoadErrors[record.Id] = manifestError ?? "plugin.json is invalid";
                return;
            }
            if (!AppearancePackService.Instance.ImportPresets(dir, manifest, out var importError))
                LoadErrors[record.Id] = importError ?? "cannot import presets";
            else
                LoadErrors.Remove(record.Id);
        }
        catch (Exception ex)
        {
            LoadErrors[record.Id] = ex.Message;
            DzTrace.Always("[Plugin] cannot reimport appearance presets: " + ex.Message);
        }
    }

    public bool Uninstall(string pluginId, bool deleteData, out string? error)
    {
        error = null;
        var record = GetRecord(pluginId);
        var loaded = GetLoaded(pluginId);
        if (loaded != null)
        {
            UnloadOne(loaded);
            // Phase 3：卸载/升级要立刻删插件目录，先等后台 ALC 回收把 DLL 句柄放掉
            // （最多 ~2s；无插件加载/无后台回收时立即返回）。等不到就退化成
            // PluginInstaller 的 pending-delete，下次启动再删。
            WaitForBackgroundAlcCleanup(2000);
        }
        foreach (var widget in Widgets.Where(w => w.PluginId == pluginId).ToList())
            CloseWindow(widget.Id);

        // 开发者模式的插件根本不在磁盘上的插件目录里，也没写进配置：
        // 卸载只是「从本次会话的列表里拿掉」，否则会去删一个不存在的目录并报错。
        if (record != null && string.Equals(record.Source, "dev", StringComparison.OrdinalIgnoreCase))
        {
            Records.Remove(record);
            _devDirs.Remove(pluginId);
            LoadErrors.Remove(pluginId);
            PluginsChanged?.Invoke();
            DzTrace.Always($"[Plugin] dev plugin {pluginId} removed from this session");
            return true;
        }

        var ok = Installer?.Uninstall(pluginId, deleteData, out error, record?.Kind) ?? false;
        if (ok) ZoneBayService.Instance.RemoveBarsForPlugin(pluginId);
        if (record != null) Records.Remove(record);
        LoadErrors.Remove(pluginId);
        if (record?.KindValue == PluginKind.Language) SyncLanguagePacks();
        if (record?.KindValue == PluginKind.Appearance) SyncAppearancePacks();
        PluginsChanged?.Invoke();
        return ok;
    }

    void UnloadOne(LoadedPlugin loaded)
    {
        // 先释放坞内分区、关坞窗口，再把插件程序集卸掉；顺序反了会让插件内容在
        // ALC 卸载后仍被窗口引用。
        try { ZoneBayService.Instance.OnPluginDisabling(loaded.Record.Id); }
        catch (Exception ex) { DzTrace.Always("[Plugin] zone bay disable: " + ex.Message); }
        try
        {
            // API 约定：Shutdown 在 UI 线程调用，且不要做 UI。这里不做超时线程切换，
            // 避免把插件的 DispatcherObject 访问扔到后台线程上。
            loaded.Instance.Shutdown();
        }
        catch (Exception ex)
        {
            DzTrace.Always($"[Plugin] {loaded.Record.Id} Shutdown threw: {ex.Message}");
        }
        foreach (var descriptor in loaded.Registry.Descriptors.Values)
        {
            _ = descriptor; // 组件实例的清理由 PluginWidgetWindow / DeleteWidget 负责
        }
        loaded.Registry.Clear();
        // 工具命令/数据源/设置是加载期收集的：卸载/禁用/被封锁时同步从菜单、热键表、
        // 数据源下拉与设置窗口里撤掉。
        PluginToolService.Instance.RemovePlugin(loaded.Record.Id);
        PluginIntegrationService.Instance.RemovePlugin(loaded.Record.Id);
        PluginSettingsService.Instance.RemovePlugin(loaded.Record.Id);
        Loaded.Remove(loaded);
        try { loaded.Context.Unload(); } catch { }
        // Phase 3（启动收尾）：collectible ALC 的卸载是异步的。这里原先在 UI 线程上同步跑
        // GC.Collect + WaitForPendingFinalizers + Collect（卸载/禁用/退出插件时界面要停一下，
        // 停顿随托管堆大小增长）。现在只把回收排到线程池后台执行：UI 线程立即返回。
        // 需要立刻删插件目录的路径（卸载/升级）会先等后台回收把 DLL 句柄放掉
        // （见 WaitForBackgroundAlcCleanup）；极端情况下退化成 pending-delete 下次启动再删，
        // PluginInstaller 本来就有这套机制。
        ScheduleBackgroundAlcCleanup();
    }

    // ── Phase 3：后台 ALC 回收 ──
    static readonly object _alcCleanupGate = new();
    static Task _alcCleanupChain = Task.CompletedTask;

    /// <summary>
    /// 排队一次后台 full GC（链式：每次 Unload 之后都保证有一次回收 —— collectible ALC
    /// 的卸载是异步的，早于本次 Unload 的 GC 覆盖不到它）。合并策略上，连续卸载多个插件
    /// 会排几次回收，但全部在后台线程，不占用 UI。
    /// </summary>
    static void ScheduleBackgroundAlcCleanup()
    {
        // 退出路径上回收没有意义：进程马上结束，别让后台 GC 拖慢退出。
        if (Helpers.AppLifetime.IsShuttingDown) return;
        lock (_alcCleanupGate)
        {
            _alcCleanupChain = _alcCleanupChain.ContinueWith(
                _ =>
                {
                    try
                    {
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                        GC.Collect();
                    }
                    catch { }
                },
                System.Threading.CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
        }
    }

    /// <summary>等待已排队的后台 ALC 回收完成（最多 <paramref name="timeoutMs"/> 毫秒）。</summary>
    static void WaitForBackgroundAlcCleanup(int timeoutMs)
    {
        Task pending;
        lock (_alcCleanupGate) pending = _alcCleanupChain;
        try { pending.Wait(timeoutMs); } catch { }
    }

    public void ShutdownAll()
    {
        foreach (var loaded in Loaded.ToList()) UnloadOne(loaded);
        // 兜底：任何没走到 UnloadOne 的发布路径（异常中断的加载）也不能把命令/数据源/设置留在界面里。
        PluginToolService.Instance.Clear();
        PluginIntegrationService.Instance.Clear();
        PluginSettingsService.Instance.Clear();
        foreach (var window in _windows.Values.ToList())
        {
            try { window.CloseWithoutSaving(); } catch { }
        }
        _windows.Clear();
        SaveWidgetsNow();
    }

    // ── 组件实例 ──

    public PluginWidget? CreateWidget(string pluginId, string widgetTypeId)
    {
        // 总开关关闭时组件系统整体停用：不新建实例，也不留占位窗。
        if (!PluginsEnabled) return null;
        var descriptor = FindDescriptor(pluginId, widgetTypeId);
        if (descriptor == null) return null;
        var offset = Widgets.Count(w => w.PluginId == pluginId) * 24;
        var widget = new PluginWidget
        {
            PluginId = pluginId,
            WidgetTypeId = widgetTypeId,
            Width = Math.Max(descriptor.MinSize.Width, descriptor.DefaultSize.Width),
            Height = Math.Max(descriptor.MinSize.Height, descriptor.DefaultSize.Height),
            X = 300 + offset,
            Y = 160 + offset,
            SortOrder = Widgets.Count
        };
        Widgets.Add(widget);
        SaveWidgets();
        WidgetsChanged?.Invoke(widget);
        ShowWidget(widget);
        return widget;
    }

    public void DeleteWidget(PluginWidget widget)
    {
        var descriptor = FindDescriptor(widget.PluginId, widget.WidgetTypeId);
        try { descriptor?.OnDelete?.Invoke(widget); } catch (Exception ex) { DzTrace.Always("[Plugin] OnDelete: " + ex.Message); }
        CloseWindow(widget.Id);
        Widgets.Remove(widget);
        SaveWidgets();
        WidgetsChanged?.Invoke(null!);
    }

    public void ShowWidget(PluginWidget widget)
    {
        widget.IsVisible = true;
        if (!_windows.TryGetValue(widget.Id, out var window) || !window.IsLoaded)
        {
            window = new PluginWidgetWindow(widget, this);
            window.Closed += (_, _) => _windows.Remove(widget.Id);
            _windows[widget.Id] = window;
        }
        window.ShowWidget();
        SaveWidgets();
    }

    public void HideWidget(PluginWidget widget)
    {
        widget.IsVisible = false;
        if (_windows.TryGetValue(widget.Id, out var window))
            window.HideWidget();
        SaveWidgets();
    }

    public void ToggleWidget(PluginWidget widget)
    {
        if (widget.IsVisible) HideWidget(widget);
        else ShowWidget(widget);
    }

    public void CloseWindow(Guid widgetId)
    {
        if (_windows.TryGetValue(widgetId, out var window))
        {
            try { window.CloseWithoutSaving(); } catch { }
            _windows.Remove(widgetId);
        }
    }

    public PluginWidgetWindow? GetWindow(Guid widgetId)
        => _windows.TryGetValue(widgetId, out var window) ? window : null;

    /// <summary>启动 / 显示全部时恢复可见组件（插件加载完成之后调用）。</summary>
    public void RestoreWidgets()
    {
        // 插件系统被关掉时旧实例只是数据：恢复它们会开出「插件不可用」占位窗，
        // 用户看到一桌面残留却没处关。直接跳过，等用户重新打开插件再恢复。
        if (!PluginsEnabled) return;
        // Phase 3（启动收尾）：启动恢复挂件时代码插件还没加载（延后到 10 之后），
        // 此时建窗会开出「插件不可用」占位窗。跳过，等 OnDeferredStartupWork 里
        // 插件加载完成后补一次 RestoreWidgets。
        if (_codePluginsDeferred && !_codePluginsLoaded && !_suppressLoading) return;
        foreach (var widget in Widgets.Where(w => w.IsVisible).ToList())
        {
            try { ShowWidget(widget); }
            catch (Exception ex) { DzTrace.Always($"[Plugin] restore widget {widget.Id} failed: {ex.Message}"); }
        }
    }

    public void ShowAllWidgets()
    {
        if (!PluginsEnabled) return;
        foreach (var widget in Widgets.ToList())
        {
            if (!widget.IsVisible) ShowWidget(widget);
            else if (_windows.TryGetValue(widget.Id, out var window)) window.ShowWidget();
        }
    }

    public void HideAllWidgets(double baseDelayMs = 0)
    {
        var windows = _windows.Values
            .Where(w => w.IsVisible)
            .OrderBy(w => w.Top).ThenBy(w => w.Left)
            .ToList();
        for (int i = 0; i < windows.Count; i++)
        {
            try { windows[i].HideWidget(baseDelayMs + i * 40); } catch { }
        }
    }

    /// <summary>「全部隐藏」：关闭窗口并把 IsVisible 落盘（与其它组件一致）。</summary>
    public void FullHideAllWidgets()
    {
        foreach (var widget in Widgets.ToList())
        {
            widget.IsVisible = false;
            CloseWindow(widget.Id);
        }
        SaveWidgets();
    }

    public void SaveWidgets()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(SaveWidgets));
            return;
        }
        _savePending = true;
        _saveDebounce.Stop();
        _saveDebounce.Start();
    }

    /// <summary>立即落盘（退出 / 卸载前调用，避免 300ms 防抖窗口丢掉最后一次改动）。</summary>
    public void SaveWidgetsNow()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            SaveWidgetsCore();
            return;
        }
        _saveDebounce.Stop();
        _savePending = false;
        SaveWidgetsCore();
    }

    void SaveWidgetsCore()
    {
        if (_configService == null) return;
        try
        {
            ConfigSaver.SavePreservingPanelSettings(_configService, cfg =>
            {
                cfg.PluginWidgets = Widgets.ToList();
            });
        }
        catch (Exception ex)
        {
            DzTrace.Always("[Plugin] failed to persist widgets: " + ex.Message);
        }
    }

    public void Notify(string title, string body)
    {
        try { NotifySink?.Invoke(title, body); } catch { }
    }

    /// <summary>未处理异常归因：栈里出现哪个插件的目录，就报哪个 id。</summary>
    public string? DescribeCulprit(Exception exception)
    {
        if (exception == null) return null;
        var text = exception.ToString();
        foreach (var loaded in Loaded)
        {
            if (text.Contains(loaded.Directory, StringComparison.OrdinalIgnoreCase))
                return loaded.Record.Id;
        }
        return null;
    }
}

/// <summary>插件 ALC：包内 DLL 就近加载，宿主/BCL 回落默认 ALC（共享类型标识）。</summary>
public sealed class PluginLoadContext : AssemblyLoadContext
{
    readonly string _dir;

    public PluginLoadContext(string dir) : base("DeskOrder.Plugin:" + Path.GetFileName(dir), isCollectible: true)
    {
        _dir = dir;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var name = assemblyName.Name;
        if (string.IsNullOrEmpty(name)) return null;
        foreach (var candidate in new[]
                 {
                     Path.Combine(_dir, name + ".dll"),
                     Path.Combine(_dir, "lib", name + ".dll")
                 })
        {
            if (File.Exists(candidate)) return LoadFile(candidate);
        }
        return null;
    }

    /// <summary>
    /// 从内存加载（而不是 LoadFromAssemblyPath）：后者的内存映射会一直占着 DLL，
    /// 用户点「卸载」时会因为文件被锁而删不掉目录。插件 DLL 通常很小，读进内存即可。
    /// </summary>
    public Assembly LoadFile(string path)
    {
        using var stream = File.OpenRead(path);
        using var buffer = new MemoryStream((int)stream.Length);
        stream.CopyTo(buffer);
        buffer.Position = 0;
        return LoadFromStream(buffer);
    }
}
