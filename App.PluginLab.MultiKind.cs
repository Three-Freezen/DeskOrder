#if DEBUG
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using DeskOrder.PluginApi;
using DesktopZones.Helpers;
using DesktopZones.Models;
using DesktopZones.Services;
using DesktopZones.Views;
using DesktopZones.Views.Components;
using DesktopZones.Views.Pages;

namespace DesktopZones;

/// <summary>
/// 「一个包多类型」（v1.2.2）自测用例：manifest / registry 的 kinds 真值表、市场卡片多类型
/// 标签、类型筛选、更新弹窗新增能力提示，以及端到端 —— 装上 multikind-demo 示例包后
/// **widget + tool 两类能力同时注册**，最后抓一张市场截图。
///
/// 与 <c>App.PluginLab.cs</c> 同属一个 partial 类：主文件只加一行用例名与一行 switch 分派。
/// </summary>
public partial class App
{
    const string MultiKindDemoId = "lab.multikind-demo";

    async Task RunMultiKindLabCaseAsync(LabCase item, string dir)
    {
        var service = PluginService.Instance;
        var loc = LocalizationService.Instance;
        var tools = PluginToolService.Instance;

        // ── 1/4. manifest 真值表：一个包多类型的全部声明规则 ──
        var widgetTool = BuildLabManifest("widget", new List<string> { "widget", "tool" }, 4);
        item.Check(widgetTool.Validate(PluginInstaller.HostVersion, forLocalImport: true).Count == 0,
            "manifest: widget + tool with apiVersion 4 is accepted");
        item.Check(widgetTool.KindValues.SequenceEqual(new[] { PluginKind.Widget, PluginKind.Tool }),
            "manifest: KindValues returns the whole kinds list in declaration order");
        var singleKind = BuildLabManifest("integration", null, 4);
        item.Check(singleKind.KindValues.Count == 1 && singleKind.KindValues[0] == PluginKind.Integration,
            "manifest: without kinds KindValues falls back to the primary kind");

        var zonebarTool = BuildLabManifest("zonebar", new List<string> { "zonebar", "tool" }, 3,
            withWidgets: false, withZoneBar: true);
        item.Check(zonebarTool.Validate(PluginInstaller.HostVersion, forLocalImport: true).Count == 0,
            "manifest: zonebar + tool with apiVersion 3 is accepted");
        var zonebarToolTooOld = BuildLabManifest("zonebar", new List<string> { "zonebar", "tool" }, 2,
            withWidgets: false, withZoneBar: true);
        item.Check(HasManifestError(zonebarToolTooOld, "tool plugins require apiVersion 3"),
            "manifest: zonebar + tool below the tool capability's v3 is rejected");

        var integrationTooOld = BuildLabManifest("widget",
            new List<string> { "widget", "integration" }, 3);
        item.Check(HasManifestError(integrationTooOld, "apiVersion 4"),
            "manifest: an integration capability with apiVersion 3 is rejected");

        var toolTooOld = BuildLabManifest("tool", null, 2, withWidgets: false);
        item.Check(HasManifestError(toolTooOld, "tool plugins require apiVersion 3"),
            "manifest: a tool plugin below apiVersion 3 is rejected");
        var toolOk = BuildLabManifest("tool", null, 3, withWidgets: false);
        item.Check(toolOk.Validate(PluginInstaller.HostVersion, forLocalImport: true).Count == 0,
            "manifest: a tool plugin with apiVersion 3 is accepted");

        var wrongFirst = BuildLabManifest("widget", new List<string> { "tool", "widget" }, 4);
        item.Check(HasManifestError(wrongFirst, "kinds[0]"),
            "manifest: kinds[0] different from kind is rejected");

        var duplicateKinds = BuildLabManifest("widget", new List<string> { "widget", "widget", "tool" }, 4);
        item.Check(HasManifestError(duplicateKinds, "duplicate"),
            "manifest: duplicate kinds entries are rejected");

        var mixedKinds = BuildLabManifest("appearance", new List<string> { "appearance", "tool" }, 0);
        item.Check(HasManifestError(mixedKinds, "mix declarative"),
            "manifest: mixing declarative and code kinds is rejected");

        var declarativeKinds = BuildLabManifest("language",
            new List<string> { "language", "appearance" }, 0);
        item.Check(HasManifestError(declarativeKinds, "declarative packages cannot declare multiple kinds"),
            "manifest: a declarative package cannot declare multiple kinds");

        var missingWidgets = BuildLabManifest("widget", new List<string> { "widget", "tool" }, 4,
            withWidgets: false);
        item.Check(HasManifestError(missingWidgets, "widgets must declare"),
            "manifest: a widget capability without widgets is rejected");

        var tooManyKinds = BuildLabManifest("widget",
            new List<string> { "widget", "tool", "zonebar", "integration", "appearance" }, 4);
        item.Check(HasManifestError(tooManyKinds, "at most 4"),
            "manifest: more than four kinds is rejected");

        var apiTooNew = BuildLabManifest("widget", new List<string> { "widget", "tool" },
            PluginApiVersion.Current + 1);
        item.Check(HasManifestError(apiTooNew, "not supported"),
            "manifest: apiVersion above the host plugin API is rejected");

        // ── 2/4. registry 解析：非法 kinds 条目整条丢弃，合法条目保留完整集合 ──
        var catalog = new PluginCatalogService();
        var parsed = catalog.ParseRegistry(Encoding.UTF8.GetBytes(BuildMultiKindRegistryJson()));
        item.Check(parsed.Error == null && parsed.Entries.Count == 1,
            "registry: only the valid kinds entry survives parsing");
        item.Check(parsed.Entries.Count == 1 && parsed.Entries[0].Id == "lab.mk-valid",
            "registry: the surviving entry is the valid widget + tool one");
        item.Check(parsed.Entries.Count == 1
                   && parsed.Entries[0].KindValues.SequenceEqual(new[] { PluginKind.Widget, PluginKind.Tool }),
            "registry: the parsed entry exposes both capabilities");

        // ── 3/4. UI：卡片多标签 / 类型筛选 / 更新弹窗新增能力提示 ──
        var multiEntry = BuildMultiKindCatalogEntry();
        var card = new PluginCard();
        card.SetData(multiEntry, null, false, false);
        item.Check(card.MetaTextForLab.Contains(loc["Plugin.Type.Widget"])
                   && card.MetaTextForLab.Contains(loc["Plugin.Type.Tool"]),
            "ui: a two-kind card lists both capability labels in its meta line");

        var threeKindEntry = BuildMultiKindCatalogEntry();
        threeKindEntry.Kinds = new List<string> { "widget", "tool", "integration" };
        var threeKindCard = new PluginCard();
        threeKindCard.SetData(threeKindEntry, null, false, false);
        item.Check(threeKindCard.MetaTextForLab.Contains(
                loc.Get("Plugin.Type.Many", loc["Plugin.Type.Widget"], 3)),
            "ui: a three-kind card collapses to \"primary + N kinds\"");

        item.Check(PluginsPage.MatchesFilter(multiEntry, PluginKind.Tool)
                   && PluginsPage.MatchesFilter(multiEntry, PluginKind.Widget)
                   && PluginsPage.MatchesFilter(multiEntry, null),
            "ui: the type filter matches any declared capability, not only the primary kind");
        item.Check(!PluginsPage.MatchesFilter(multiEntry, PluginKind.Language),
            "ui: a kind the package does not declare is filtered out");

        var oldRecord = new PluginRecord { Id = MultiKindDemoId, Version = "0.9.0", Kind = "widget" };
        var updateDialog = new PluginUpdateDialog(multiEntry, oldRecord);
        var addedLine = updateDialog.AddedKindsTextForLab;
        item.Check(addedLine != null && addedLine.Contains(loc["Plugin.Type.Tool"]),
            "ui: the update dialog calls out a newly added capability");
        updateDialog.Close();
        var unchangedDialog = new PluginUpdateDialog(multiEntry, new PluginRecord
        {
            Id = MultiKindDemoId,
            Version = "1.0.0",
            Kind = "widget",
            Kinds = new List<string> { "widget", "tool" }
        });
        item.Check(unchangedDialog.AddedKindsTextForLab == null,
            "ui: no capability line when the kind sets are unchanged");
        unchangedDialog.Close();

        // ── 4/4. 端到端：安装 multikind-demo → widget + tool 同时注册 → 市场截图 ──
        var package = Path.Combine(dir, "multikind-demo-1.0.0.dopkg");
        item.Check(File.Exists(package), "e2e: the multikind-demo package is staged in the lab dir");
        if (!File.Exists(package)) return;

        // 同版本覆盖会先删旧目录；上一轮如果还加载着，DLL 被占用会删不掉，先卸载。
        if (service.GetRecord(MultiKindDemoId) != null)
            service.Uninstall(MultiKindDemoId, deleteData: true, out _);
        service.RefreshRecords();

        var install = await service.Installer!.InstallFromFileAsync(package);
        item.Check(install.Success,
            "e2e: installing the multikind-demo package succeeds"
            + (install.Success ? "" : ": " + install.Error));
        if (!install.Success) return;
        service.RefreshRecords();

        var record = service.GetRecord(MultiKindDemoId);
        item.Check(record != null && record.Kind == "widget" && record.Kinds != null
                   && record.KindValues.SequenceEqual(new[] { PluginKind.Widget, PluginKind.Tool }),
            "e2e: the installed record keeps both capabilities");
        item.Check(install.Record?.Kinds?.Count == 2,
            "e2e: the install result carries the kinds list");

        item.Check(service.SetEnabled(MultiKindDemoId, true), "e2e: the multikind plugin can be enabled");
        service.ReloadForLab();
        item.Check(service.GetRecord(MultiKindDemoId)?.KindValues.Count == 2,
            "e2e: kinds survive a reload (persisted in the record)");
        item.Check(service.GetLoaded(MultiKindDemoId) != null, "e2e: the multikind plugin is loaded");
        item.Check(service.GetDescriptors(MultiKindDemoId).Count == 1,
            "e2e: the widget capability registers one widget descriptor");
        var stamp = tools.Find(MultiKindDemoId, "stamp");
        item.Check(stamp != null && stamp.Command.DisplayName == "敲图章",
            "e2e: the tool capability registers the stamp command");
        item.Check(tools.Commands.Any(entry =>
                string.Equals(entry.PluginId, MultiKindDemoId, StringComparison.OrdinalIgnoreCase)),
            "e2e: the package shows up in the tool command center");

        var detail = new PluginDetailDialog(multiEntry, service.GetRecord(MultiKindDemoId));
        item.Check(detail.CapabilitiesVisibleForLab,
            "ui: the detail dialog shows the capability list for a multi-kind package");
        item.Check(detail.CapabilitiesTextForLab.Contains(loc["Plugin.Type.Widget"])
                   && detail.CapabilitiesTextForLab.Contains(loc["Plugin.Type.Tool"]),
            "ui: the capability list names every capability");
        item.Check(detail.CapabilitiesTextForLab.Contains(loc.Get("Plugin.Contents.ToolCommands", 1)),
            "ui: the capability list carries the live tool command count");
        detail.Close();

        // 市场截图：把多类型条目写进缓存（版本 1.1.0 > 已装的 1.0.0，会因「有更新」排在最前）。
        WriteMultiKindMarketRegistry(dir);
        EnsureManagementWindow();
        var window = ManagementWindow;
        if (window == null)
        {
            item.Check(false, "ui: the management window exists");
            return;
        }
        window.Show();
        window.ShowSection("plugins");
        window.Width = 1680;
        window.Height = 1120;
        window.Left = 40;
        window.Top = 20;
        if (window.MainContent.Content is PluginsPage page)
        {
            page.SelectTab(0);
            page.MarketScroll.ScrollToTop();
        }
        await Task.Delay(3000); // 重排 + 卡片渲染 + 预览图下载
        var shot = Path.Combine(dir, "shot-multikind-market.png");
        SaveScreenshot(window, shot);
        item.Check(File.Exists(shot), "ui: market screenshot written: shot-multikind-market.png");

        if (window.MainContent.Content is PluginsPage marketPage)
        {
            var cards = marketPage.CardsHost.Children.OfType<PluginCard>().ToList();
            var multiKindCard = cards.FirstOrDefault(c => c.Entry.Id == MultiKindDemoId);
            item.Check(multiKindCard != null, "ui: the multikind entry renders as a market card");
            item.Check(multiKindCard != null && multiKindCard.MetaTextForLab.Contains(loc["Plugin.Type.Tool"]),
                "ui: the rendered card shows the secondary capability label");
        }
        else
        {
            item.Check(false, "ui: the plugins page is reachable from the management window");
        }

        // 清理：把示例插件卸掉，别留在隔离数据目录里。
        service.Uninstall(MultiKindDemoId, deleteData: true, out _);
        service.RefreshRecords();
        item.Check(service.GetRecord(MultiKindDemoId) == null,
            "cleanup: the multikind demo is uninstalled again");
        item.Check(tools.Find(MultiKindDemoId, "stamp") == null,
            "cleanup: unloading drops the tool command");
    }

    /// <summary>真值表用的合法 manifest 骨架；按参数补 widgets / zoneBar。</summary>
    static PluginManifest BuildLabManifest(string kind, List<string>? kinds, int apiVersion,
        bool withWidgets = true, bool withZoneBar = false)
    {
        var manifest = new PluginManifest
        {
            SchemaVersion = 1,
            Id = "lab.multikind",
            Name = new LocalizedText { Zh = "多类型自测", En = "Multikind lab" },
            Summary = new LocalizedText { Zh = "多类型自测", En = "multikind lab" },
            Version = "1.0.0",
            Author = new PluginAuthor { Name = "DeskOrder Lab", Github = "lab" },
            Repo = "lab/DeskOrder-Multikind",
            License = "Apache-2.0",
            Kind = kind,
            Kinds = kinds,
            ApiVersion = apiVersion,
            MinAppVersion = "1.0.0",
            Entry = new PluginEntry { Assembly = "lib/Lab.Multikind.dll", Type = "Lab.Multikind.Plugin" },
            Icon = "icon.png"
        };
        if (withWidgets)
        {
            manifest.Widgets = new List<PluginWidgetDeclaration>
            {
                new() { Id = "demo", Name = new LocalizedText { Zh = "演示", En = "Demo" } }
            };
        }
        if (withZoneBar)
        {
            manifest.ZoneBar = new PluginZoneBarDeclaration
            {
                Id = "default",
                Name = new LocalizedText { Zh = "分区坞", En = "Zone bay" }
            };
        }
        return manifest;
    }

    static bool HasManifestError(PluginManifest manifest, string fragment)
        => manifest.Validate(PluginInstaller.HostVersion, forLocalImport: true)
            .Any(error => error.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    /// <summary>卡片 / 详情 / 更新弹窗用例共用的市场条目（与示例包的 manifest 一致）。</summary>
    static PluginCatalogEntry BuildMultiKindCatalogEntry() => new()
    {
        Id = MultiKindDemoId,
        Kind = "widget",
        Kinds = new List<string> { "widget", "tool" },
        Name = new LocalizedText { Zh = "多类型示例", En = "Multikind demo" },
        Summary = new LocalizedText
        {
            Zh = "一个包两类能力：桌面组件 + 工具命令。",
            En = "One package, two capabilities: widget + tool."
        },
        Author = new PluginAuthor { Name = "DeskOrder Lab", Github = "lab" },
        Repo = "lab/DeskOrder-MultikindDemo",
        Version = "1.0.0",
        ApiVersion = 4,
        MinAppVersion = "1.2.0",
        License = "Apache-2.0",
        DownloadUrl = "https://github.com/Three-Freezen/DeskOrder-Plugins/releases/download/samples/multikind-demo-1.0.0.dopkg",
        Size = 12000,
        Sha256 = LabShotSha,
        PublishedAt = DateTime.UtcNow.AddDays(-1)
    };

    /// <summary>一条合法多类型条目 + 四条各种非法的条目（重复 / kinds[0] / 混装 / 超量）。</summary>
    static string BuildMultiKindRegistryJson()
    {
        const string sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        string Entry(string id, string kind, string kindsJson) => $$"""
            {
              "id": "{{id}}",
              "kind": "{{kind}}",
              "kinds": {{kindsJson}},
              "name": { "zh": "{{id}}" },
              "summary": { "zh": "自测条目" },
              "author": { "name": "Lab", "github": "lab" },
              "repo": "lab/x",
              "version": "1.0.0",
              "apiVersion": 4,
              "minAppVersion": "1.0.0",
              "license": "MIT",
              "downloadUrl": "https://example.invalid/x.dopkg",
              "size": 1024,
              "sha256": "{{sha}}"
            }
            """;
        return $$"""
            {
              "schemaVersion": 1,
              "plugins": [
                {{Entry("lab.mk-valid", "widget", "[\"widget\",\"tool\"]")}},
                {{Entry("lab.mk-dup", "widget", "[\"widget\",\"widget\"]")}},
                {{Entry("lab.mk-first", "widget", "[\"tool\",\"widget\"]")}},
                {{Entry("lab.mk-mixed", "language", "[\"language\",\"tool\"]")}},
                {{Entry("lab.mk-many", "widget", "[\"widget\",\"tool\",\"zonebar\",\"integration\",\"appearance\"]")}}
              ]
            }
            """;
    }

    /// <summary>
    /// 把 lab registry 写成市场缓存，并追加一条「已装 1.0.0 / 市场 1.1.0」的多类型条目
    /// （照抄 <c>App.PluginLab.MarketUx.cs</c> 的写法）：有更新会排到最前，截图里第一张卡
    /// 就能看到「桌面组件 + 工具插件」的多类型标签。
    /// </summary>
    static void WriteMultiKindMarketRegistry(string dir)
    {
        var path = Path.Combine(dir, "registry.json");
        PluginCatalogFile file;
        if (File.Exists(path))
        {
            file = JsonSerializer.Deserialize<PluginCatalogFile>(
                       File.ReadAllText(path),
                       new JsonSerializerOptions
                       {
                           PropertyNameCaseInsensitive = true,
                           PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                       })
                   ?? new PluginCatalogFile();
        }
        else
        {
            file = new PluginCatalogFile();
        }

        // 预览图换成能真正下载到的文档截图（lab 夹具里的 iconUrl 是故意的 404 地址）。
        for (var i = 0; i < file.Plugins.Count; i++)
        {
            var entry = file.Plugins[i];
            entry.IconUrl = LabShotImages[i % LabShotImages.Length];
            entry.PreviewUrls = new List<string> { LabShotImages[(i + 1) % LabShotImages.Length] };
            entry.PublishedAt ??= DateTime.UtcNow.AddDays(-10 - i);
        }

        var marketEntry = BuildMultiKindCatalogEntry();
        marketEntry.Version = "1.1.0";
        marketEntry.Changelog = new LocalizedText
        {
            Zh = "新增工具命令能力：一个包同时提供桌面组件与工具。",
            En = "Adds a tool command capability next to the widget."
        };
        marketEntry.IconUrl = LabShotWidgets;
        marketEntry.PreviewUrls = new List<string> { LabShotWidgets };
        file.Plugins.RemoveAll(entry => string.Equals(entry.Id, MultiKindDemoId, StringComparison.OrdinalIgnoreCase));
        file.Plugins.Add(marketEntry);

        file.UpdatedAt = DateTime.UtcNow;
        PluginPaths.EnsureCreated();
        File.WriteAllText(PluginPaths.RegistryCachePath, JsonSerializer.Serialize(file,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        // FetchedAtUtc 设为现在：市场页 RefreshAsync(force:false) 走 6 小时 TTL 内的缓存，
        // 不会被线上 registry 覆盖（lab 里后台拉取已被 SuppressBackgroundRefreshForLab 关掉）。
        File.WriteAllText(PluginPaths.RegistryMetaPath,
            "{\"Etag\":null,\"FetchedAtUtc\":\"" + DateTime.UtcNow.ToString("O") + "\"}");
    }
}
#endif
