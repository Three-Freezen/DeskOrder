#if DEBUG
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DeskOrder.PluginApi;
using DesktopZones.Helpers;
using DesktopZones.Models;
using DesktopZones.Services;
using DesktopZones.Views;
using DesktopZones.Views.Components;
using DesktopZones.Views.Pages;

namespace DesktopZones;

/// <summary>
/// 插件市场 UX 收尾的自测用例（v1.2.1 批次）。与 <c>App.PluginLab.cs</c> 同属一个 partial 类：
/// 主文件只负责分派，用例实现放在这里，避免两个并行开发流同时改同一个文件。
///
/// 覆盖：apiVersion / minAppVersion 兼容性真值表、市场排序（更新 → 官方 → 发布时间）、
/// 已安装搜索（作者 / 标签 / 类型）、详情弹窗多图轮播、卡片权限角标与置灰，最后抓两张截图
/// （市场卡片 / 详情弹窗）。所有断言都走生产代码里的同一个纯函数，不在用例里重写规则。
/// </summary>
public partial class App
{
    // 截图用的真图：都在 raw.githubusercontent.com（图片白名单内）且小于 4 MB 上限。
    // 插件仓库的 icon 在 lab 里是故意写的 404 地址，这里换成主仓库文档图，截图才不是一片灰。
    const string LabShotManager =
        "https://raw.githubusercontent.com/Three-Freezen/DeskOrder/main/docs/images/manager.png";
    const string LabShotPanel =
        "https://raw.githubusercontent.com/Three-Freezen/DeskOrder/main/docs/images/panel.png";
    const string LabShotTiles =
        "https://raw.githubusercontent.com/Three-Freezen/DeskOrder/main/docs/images/tiles.png";
    const string LabShotZones =
        "https://raw.githubusercontent.com/Three-Freezen/DeskOrder/main/docs/images/zones.png";
    const string LabShotWidgets =
        "https://raw.githubusercontent.com/Three-Freezen/DeskOrder/main/docs/images/widgets.png";
    const string LabShotGlass =
        "https://raw.githubusercontent.com/Three-Freezen/DeskOrder/main/docs/images/glass.png";

    static readonly string[] LabShotImages =
    {
        LabShotManager, LabShotPanel, LabShotTiles, LabShotZones, LabShotWidgets, LabShotGlass
    };

    const string LabShotSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    async Task RunMarketUxLabCaseAsync(LabCase item, string dir)
    {
        var service = PluginService.Instance;
        var loc = LocalizationService.Instance;
        var dialogs = new List<Window>();
        try
        {
            // 需要一个已安装记录来构造「有更新」：hello-widget 是既有用例都会装的样例。
            await EnsureEnabledAsync(dir);
            item.Check(service.GetRecord(SamplePluginId) != null, "the sample plugin is installed for the update case");

            // ── 1/5. 兼容性真值表：apiVersion 99 / ==Current / minApp 99 / 空 minApp / 声明式包 ──
            var newerApi = new PluginCatalogEntry { Id = "lab.mux-api-99", ApiVersion = 99 };
            var apiReason = PluginDisplay.IncompatibilityReason(newerApi);
            item.Check(apiReason == loc.Get("Plugin.Requires.Api", 99),
                "apiVersion 99 is rejected with the localized API reason");
            item.Check(PluginDisplay.IncompatibilityReason(
                    new PluginCatalogEntry { Id = "lab.mux-api-ok", ApiVersion = PluginApiVersion.Current }) == null,
                "apiVersion == Current is compatible");
            item.Check(PluginDisplay.IncompatibilityReason(
                    new PluginCatalogEntry { Id = "lab.mux-api-zero", Kind = "language", ApiVersion = 0 }) == null,
                "a declarative package (apiVersion 0) is compatible");
            var minReason = PluginDisplay.IncompatibilityReason(
                new PluginCatalogEntry { Id = "lab.mux-min-99", ApiVersion = 1, MinAppVersion = "99.0.0" });
            item.Check(minReason == loc.Get("Plugin.Update.Requires", "99.0.0"),
                "minAppVersion 99.0.0 is rejected and names the required host version");
            item.Check(PluginDisplay.IncompatibilityReason(
                    new PluginCatalogEntry { Id = "lab.mux-min-empty", ApiVersion = 1 }) == null,
                "a missing minAppVersion is compatible");
            item.Check(PluginDisplay.IncompatibilityReason(
                    new PluginCatalogEntry { Id = "lab.mux-lang-old", Kind = "language", MinAppVersion = "99.0.0" }) != null,
                "even a declarative package is gated by minAppVersion");

            // ── 2/5. 排序：更新优先 → 官方优先 → PublishedAt 降序 ──
            var newer = new PluginCatalogEntry
            {
                Id = SamplePluginId,
                Version = "1.1.0",
                PublishedAt = DateTime.UtcNow.AddDays(-30)
            };
            var official = new PluginCatalogEntry
            {
                Id = "lab.mux-official",
                Author = new PluginAuthor { Name = "Three-Freezen", Github = "Three-Freezen" },
                Repo = "Three-Freezen/DeskOrder-Plugin-Official",
                Version = "1.0.0",
                PublishedAt = DateTime.UtcNow.AddDays(-5)
            };
            var communityNew = new PluginCatalogEntry
            {
                Id = "lab.mux-community-new",
                Version = "1.0.0",
                PublishedAt = DateTime.UtcNow.AddDays(-1)
            };
            var communityOld = new PluginCatalogEntry
            {
                Id = "lab.mux-community-old",
                Version = "1.0.0",
                PublishedAt = DateTime.UtcNow.AddDays(-60)
            };
            var sorted = PluginsPage.SortCatalogEntries(
                new[] { communityOld, communityNew, official, newer }, service.GetRecord);
            item.Check(sorted.Count == 4 && sorted[0].Id == SamplePluginId,
                "an entry with an available update sorts before everything else");
            item.Check(sorted[1].Id == official.Id, "official entries come before community ones");
            item.Check(sorted[2].Id == communityNew.Id && sorted[3].Id == communityOld.Id,
                "PublishedAt sorts the remaining entries newest first");
            item.Check(PluginsPage.SortCatalogEntries(new[] { communityOld, communityNew }, _ => null)
                           .Select(e => e.Id).SequenceEqual(new[] { communityNew.Id, communityOld.Id }),
                "the sort stays correct when no plugin is installed");

            // 已安装行的「更新」按钮判定：有更新才出现，同版本 / 已下架都不出现。
            var installedRecord = service.GetRecord(SamplePluginId)!;
            item.Check(PluginsPage.ShouldOfferRowUpdate(installedRecord,
                    new PluginCatalogEntry { Id = SamplePluginId, Version = "1.1.0" }),
                "a newer registry version offers the installed-row update button");
            item.Check(!PluginsPage.ShouldOfferRowUpdate(installedRecord,
                    new PluginCatalogEntry { Id = SamplePluginId, Version = installedRecord.Version }),
                "the same version does not offer the installed-row update button");
            item.Check(!PluginsPage.ShouldOfferRowUpdate(installedRecord, null),
                "a delisted plugin (no registry entry) offers no update button");

            // ── 3/5. 搜索：作者 / 标签 / 类型命中 + 反例 ──
            var searchManifest = new PluginManifest
            {
                Id = "lab.mux-search",
                Kind = "widget",
                Name = new LocalizedText { Zh = "天气小组件", En = "Weather widget" },
                Author = new PluginAuthor { Name = "Aiko", Github = "aiko-lab" }
            };
            var searchRecord = new PluginRecord { Id = "lab.mux-search", Kind = "integration", Version = "1.0.0" };
            var searchEntry = new PluginCatalogEntry
            {
                Id = "lab.mux-search",
                Tags = new List<string> { "weather", "clock" }
            };
            item.Check(PluginsPage.MatchesInstalled(searchRecord, searchManifest, searchEntry, "aiko"),
                "installed search matches the author name");
            item.Check(PluginsPage.MatchesInstalled(searchRecord, searchManifest, null, "aiko-lab"),
                "installed search matches the author GitHub handle");
            item.Check(PluginsPage.MatchesInstalled(searchRecord, searchManifest, searchEntry, "clock"),
                "installed search matches registry tags");
            item.Check(PluginsPage.MatchesInstalled(searchRecord, searchManifest, searchEntry,
                    loc[searchRecord.KindValue.I18nKey()]),
                "installed search matches the localized plugin kind");
            item.Check(!PluginsPage.MatchesInstalled(searchRecord, searchManifest, searchEntry, "no-such-term-xyz"),
                "installed search rejects a miss");

            // 市场搜索补的 Description / Repo 命中（卡片的「搜不到描述里的词」问题）。
            var marketEntry = new PluginCatalogEntry
            {
                Id = "lab.mux-market",
                Repo = "aiko-lab/DeskOrder-Weather",
                Description = new LocalizedText { Zh = "按小时天气预报", En = "Hourly forecast" }
            };
            item.Check(PluginsPage.Matches(marketEntry, "hourly"), "market search matches the description");
            item.Check(PluginsPage.Matches(marketEntry, "aiko-lab/DeskOrder"), "market search matches the repo");
            item.Check(!PluginsPage.Matches(marketEntry, "no-such-term-xyz"), "market search rejects a miss");

            // ── 4/5. 详情弹窗多图轮播 + 卡片置灰 / 权限角标 ──
            var detailEntry = BuildLabDetailEntry();
            var dialog3 = new PluginDetailDialog(detailEntry, null);
            dialogs.Add(dialog3);
            item.Check(dialog3.PreviewCountForLab == 3, "three preview URLs produce three slides");
            item.Check(dialog3.PreviewNavVisibleForLab, "three slides show the carousel arrows");
            item.Check(dialog3.PreviewCounterForLab == "1 / 3", "the carousel counter starts at 1 / 3");
            dialog3.Show();
            await Task.Delay(1800); // 等三张预览图下载/解码（失败也不影响断言）
            var detailShot = Path.Combine(dir, "shot-marketux-detail.png");
            SaveScreenshot(dialog3, detailShot);
            item.Check(File.Exists(detailShot), "detail dialog screenshot written: shot-marketux-detail.png");

            var singlePreview = BuildLabDetailEntry();
            singlePreview.PreviewUrls = new List<string> { LabShotManager };
            var dialog1 = new PluginDetailDialog(singlePreview, null);
            dialogs.Add(dialog1);
            item.Check(dialog1.PreviewCountForLab == 1 && !dialog1.PreviewNavVisibleForLab,
                "a single preview hides the arrows and the counter");

            var noPreview = BuildLabDetailEntry();
            noPreview.PreviewUrls = new List<string>();
            var dialog0 = new PluginDetailDialog(noPreview, null);
            dialogs.Add(dialog0);
            item.Check(dialog0.PreviewCountForLab == 0 && !dialog0.PreviewNavVisibleForLab,
                "no preview hides the arrows and the counter");

            var codeEntry = new PluginCatalogEntry
            {
                Id = "lab.mux-permissions",
                Kind = "widget",
                Name = new LocalizedText { Zh = "权限角标", En = "Permission badge" },
                Summary = new LocalizedText { Zh = "示例", En = "sample" },
                Author = new PluginAuthor { Name = "DeskOrder Lab", Github = "lab" },
                Version = "1.0.0",
                ApiVersion = 1,
                MinAppVersion = "1.0.0",
                Permissions = new List<string> { "network", "zones.read" }
            };
            var codeCard = new PluginCard();
            codeCard.SetData(codeEntry, null, false, false);
            item.Check(codeCard.PermissionBadgeVisibleForLab,
                "a code plugin with permissions shows the permission badge");

            var declarativeEntry = new PluginCatalogEntry
            {
                Id = "lab.mux-appearance",
                Kind = "appearance",
                Name = new LocalizedText { Zh = "外观包", En = "Appearance" },
                Summary = new LocalizedText { Zh = "示例", En = "sample" },
                Author = new PluginAuthor { Name = "DeskOrder Lab", Github = "lab" },
                Version = "1.0.0",
                Permissions = new List<string> { "network" }
            };
            var declarativeCard = new PluginCard();
            declarativeCard.SetData(declarativeEntry, null, false, false);
            item.Check(!declarativeCard.PermissionBadgeVisibleForLab,
                "a declarative package never shows the permission badge");

            var incompatibleEntry = new PluginCatalogEntry
            {
                Id = "lab.mux-incompatible",
                Kind = "widget",
                Name = new LocalizedText { Zh = "需要新版", En = "Needs newer host" },
                Summary = new LocalizedText { Zh = "示例", En = "sample" },
                Author = new PluginAuthor { Name = "DeskOrder Lab", Github = "lab" },
                Version = "1.0.0",
                ApiVersion = 1,
                MinAppVersion = "99.0.0"
            };
            var incompatibleCard = new PluginCard();
            incompatibleCard.SetData(incompatibleEntry, null, false, false);
            item.Check(!incompatibleCard.ActionEnabledForLab && !string.IsNullOrEmpty(incompatibleCard.ActionToolTipForLab),
                "an incompatible card greys out install and explains why");
            var compatibleCard = new PluginCard();
            compatibleCard.SetData(codeEntry, null, false, false);
            item.Check(compatibleCard.ActionEnabledForLab,
                "a compatible card keeps the install button enabled");

            // ── 4.5/5. 空态按钮可见性：市场空 → 重试 + 导入；更新空 → 只重试 ──
            var emptyPage = new PluginsPage();
            emptyPage.SelectTab(0);
            item.Check(emptyPage.EmptyRetryVisibleForLab && emptyPage.EmptyImportVisibleForLab,
                "an empty marketplace shows both retry and import");
            item.Check(emptyPage.EmptyBodyForLab == loc["Plugin.Empty.Market.Body"],
                "the empty marketplace explains the next step");
            emptyPage.SelectTab(2);
            item.Check(emptyPage.EmptyRetryVisibleForLab && !emptyPage.EmptyImportVisibleForLab,
                "an empty updates tab shows retry but not import");

            // ── 5/5. 市场卡片截图：把 lab registry 换成本地缓存，塞两条「不兼容」条目 ──
            WriteLabMarketRegistry(dir);
            EnsureManagementWindow();
            var window = ManagementWindow!;
            window.Show();
            window.ShowSection("plugins");
            // 默认 1100x680 一列只放得下 1 张卡：先放大窗口再截图，才能同时看到
            // 「更新置顶 / 置灰 / 权限角标」三种状态（截图只用于自测报告，不影响产品行为）。
            window.Width = 1680;
            window.Height = 1120;
            window.Left = 40;
            window.Top = 20;
            if (window.MainContent.Content is PluginsPage pageForShot)
            {
                pageForShot.SelectTab(0);
                pageForShot.MarketScroll.ScrollToTop();
            }
            await Task.Delay(3000); // 重排 + 卡片渲染 + 预览图下载
            var cardsShot = Path.Combine(dir, "shot-marketux-cards.png");
            SaveScreenshot(window, cardsShot);
            item.Check(File.Exists(cardsShot), "market cards screenshot written: shot-marketux-cards.png");

            if (window.MainContent.Content is PluginsPage page)
            {
                var cards = page.CardsHost.Children.OfType<PluginCard>().ToList();
                item.Check(cards.Count > 0, "the market tab renders cards from the lab registry");
                item.Check(cards.FirstOrDefault()?.Entry.Id == SamplePluginId,
                    "the update-pinned card is rendered first");
                var needsHost = cards.FirstOrDefault(c => c.Entry.Id == "lab.mux-needs-host");
                item.Check(needsHost != null && !needsHost.ActionEnabledForLab,
                    "a card that needs a newer host is greyed out in the live page");
                var apiNewer = cards.FirstOrDefault(c => c.Entry.Id == "lab.mux-api-newer");
                item.Check(apiNewer != null && !apiNewer.ActionEnabledForLab,
                    "an apiVersion-too-new card is greyed out in the live page");
                var zoneBay = cards.FirstOrDefault(c => c.Entry.Id == "three-freezen.zone-bay");
                item.Check(zoneBay != null && zoneBay.PermissionBadgeVisibleForLab,
                    "the zone-bay card shows its two-permission badge");
                var appearance = cards.FirstOrDefault(c => c.Entry.Id == "lab.aurora");
                item.Check(appearance != null && !appearance.PermissionBadgeVisibleForLab,
                    "the declarative appearance card has no permission badge");

                // 已安装 tab：hello-widget 记录 1.0.0、registry 1.1.0 → 行上应该有可点的「更新」。
                page.SelectTab(1);
                await Task.Delay(900);
                var updateTooltip = loc.Get("Plugin.UpdateAvailable", "1.1.0");
                var updateButtons = WalkVisualTree(page.InstalledHost).OfType<Button>()
                    .Where(button => Equals(button.ToolTip, updateTooltip)).ToList();
                item.Check(updateButtons.Count == 1 && updateButtons[0].IsEnabled,
                    "the installed row shows an enabled update button for the newer version");
                page.SelectTab(0);
            }
            else
            {
                item.Check(false, "the plugins page is reachable from the management window");
            }
        }
        catch (Exception ex)
        {
            item.Check(false, "marketux case crashed: " + ex);
        }
        finally
        {
            foreach (var dialog in dialogs)
            {
                try { dialog.Close(); } catch { }
            }
        }
    }

    /// <summary>在视觉树里找按钮（安装行是代码拼出来的，没有可断言的 x:Name）。</summary>
    static IEnumerable<DependencyObject> WalkVisualTree(DependencyObject root)
    {
        yield return root;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            foreach (var child in WalkVisualTree(VisualTreeHelper.GetChild(root, i)))
                yield return child;
        }
    }

    /// <summary>详情弹窗截图用的合成条目：3 张真实预览图 + 与宿主兼容的版本声明。</summary>
    static PluginCatalogEntry BuildLabDetailEntry() => new()
    {
        Id = "lab.mux-detail",
        Kind = "widget",
        Name = new LocalizedText { Zh = "天气概览", En = "Weather overview" },
        Summary = new LocalizedText
        {
            Zh = "在桌面组件里显示逐小时天气，支持多张预览图轮播。",
            En = "Shows hourly weather in a desktop widget; previews support a multi-image carousel."
        },
        Description = new LocalizedText
        {
            Zh = "这是自测台的合成条目：预览图来自 DeskOrder 主仓库的文档截图，用来验证详情弹窗的"
                 + "多图轮播（箭头 / 计数 / 加载失败保留当前图）。",
            En = "A lab-only synthetic entry: the previews are DeskOrder docs screenshots, used to verify "
                 + "the detail dialog carousel (arrows / counter / keeping the current image on failure)."
        },
        Author = new PluginAuthor { Name = "DeskOrder Lab", Github = "Three-Freezen" },
        Repo = "Three-Freezen/DeskOrder",
        Version = "1.2.0",
        ApiVersion = 1,
        MinAppVersion = "1.0.0",
        License = "Apache-2.0",
        Permissions = new List<string> { "network" },
        IconUrl = LabShotManager,
        PreviewUrls = new List<string> { LabShotManager, LabShotPanel, LabShotZones },
        DownloadUrl = "https://github.com/Three-Freezen/DeskOrder/releases/download/v1.2.0/lab-mux.dopkg",
        Size = 4096,
        Sha256 = LabShotSha,
        PublishedAt = DateTime.UtcNow.AddDays(-2)
    };

    /// <summary>
    /// 把 lab 目录的 registry.json 写进本地缓存，并补两条「不兼容」条目 + 把 hello-widget
    /// 抬到 1.1.0，让市场截图能同时看到「更新置顶 / 置灰 / 权限角标」三种状态。
    /// </summary>
    static void WriteLabMarketRegistry(string dir)
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

        // 预览图换成能真正下载到的文档截图（原 iconUrl 是故意的 404 地址）。
        for (var i = 0; i < file.Plugins.Count; i++)
        {
            var entry = file.Plugins[i];
            entry.IconUrl = LabShotImages[i % LabShotImages.Length];
            entry.PreviewUrls = new List<string>
            {
                LabShotImages[(i + 1) % LabShotImages.Length]
            };
            entry.PublishedAt ??= DateTime.UtcNow.AddDays(-10 - i);
        }

        // hello-widget 有新版本：市场卡片里它应该被置顶（压过「官方优先 / 时间降序」）。
        var hello = file.Plugins.FirstOrDefault(entry => entry.Id == SamplePluginId);
        if (hello != null)
        {
            hello.Version = "1.1.0";
            hello.PublishedAt = DateTime.UtcNow.AddDays(-30);
        }

        var downloadBase = "https://github.com/Three-Freezen/DeskOrder/releases/download/v1.2.0/";
        file.Plugins.Add(new PluginCatalogEntry
        {
            Id = "lab.mux-needs-host",
            Kind = "widget",
            Name = new LocalizedText { Zh = "需要新版 DeskOrder", En = "Needs newer DeskOrder" },
            Summary = new LocalizedText
            {
                Zh = "示例条目：minAppVersion=99.0.0，卡片上的安装按钮应置灰。",
                En = "Sample entry: minAppVersion=99.0.0, so the install button is greyed out."
            },
            Author = new PluginAuthor { Name = "DeskOrder Lab", Github = "lab" },
            Repo = "lab/mux-needs-host",
            Version = "1.0.0",
            ApiVersion = 1,
            MinAppVersion = "99.0.0",
            License = "Apache-2.0",
            Tags = new List<string> { "sample" },
            IconUrl = LabShotTiles,
            PreviewUrls = new List<string> { LabShotWidgets },
            DownloadUrl = downloadBase + "lab-mux-needs-host.dopkg",
            Size = 2048,
            Sha256 = LabShotSha,
            PublishedAt = DateTime.UtcNow.AddDays(-1)
        });
        file.Plugins.Add(new PluginCatalogEntry
        {
            Id = "lab.mux-api-newer",
            Kind = "widget",
            Name = new LocalizedText { Zh = "需要更新的插件 API", En = "Needs a newer plugin API" },
            Summary = new LocalizedText
            {
                Zh = "示例条目：apiVersion=99，卡片上的安装按钮应置灰。",
                En = "Sample entry: apiVersion=99, so the install button is greyed out."
            },
            Author = new PluginAuthor { Name = "DeskOrder Lab", Github = "lab" },
            Repo = "lab/mux-api-newer",
            Version = "1.0.0",
            ApiVersion = 99,
            MinAppVersion = "1.0.0",
            License = "Apache-2.0",
            Tags = new List<string> { "sample" },
            IconUrl = LabShotGlass,
            PreviewUrls = new List<string> { LabShotZones },
            DownloadUrl = downloadBase + "lab-mux-api-newer.dopkg",
            Size = 2048,
            Sha256 = LabShotSha,
            PublishedAt = DateTime.UtcNow
        });

        file.UpdatedAt = DateTime.UtcNow;
        PluginPaths.EnsureCreated();
        File.WriteAllText(PluginPaths.RegistryCachePath, JsonSerializer.Serialize(file,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        // FetchedAtUtc 设为现在：市场页 RefreshAsync(force:false) 会走 6 小时 TTL 内的缓存，
        // 不会被线上 registry 覆盖（lab 里后台拉取已被 SuppressBackgroundRefreshForLab 关掉）。
        File.WriteAllText(PluginPaths.RegistryMetaPath,
            "{\"Etag\":null,\"FetchedAtUtc\":\"" + DateTime.UtcNow.ToString("O") + "\"}");
    }
}
#endif
