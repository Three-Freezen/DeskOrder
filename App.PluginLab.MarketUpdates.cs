#if DEBUG
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
/// v1.2.3「插件市场 UX 收尾」的自测用例：市场列表窗口化、批量更新计划、更新角标、
/// 安装下载进度、三个 tab 的类型筛选。实现放在单独 partial 文件里（与 MarketUx 同款），
/// 主文件只负责分派。
///
/// 口径说明：所有断言都走生产代码里的同一个纯函数（计数 / 计划 / 窗口化 / 进度映射 /
/// 角标格式化），不在用例里重写一份规则；最后抓三张截图（批量确认框 / 更新 tab / 侧栏角标）。
/// </summary>
public partial class App
{
    async Task RunMarketUpdatesLabCaseAsync(LabCase item, string dir)
    {
        var service = PluginService.Instance;
        var loc = LocalizationService.Instance;
        var windows = new List<Window>();
        try
        {
            // ── 1/6. 更新计数口径：有更新 / 已最新 / blocked / 不兼容 / 不存在 ──
            var records = new List<PluginRecord>
            {
                new() { Id = "lab.mu-update", Version = "1.0.0", Kind = "widget" },
                new() { Id = "lab.mu-latest", Version = "1.1.0", Kind = "widget" },
                new() { Id = "lab.mu-blocked", Version = "1.0.0", Kind = "widget" },
                new() { Id = "lab.mu-incompatible", Version = "1.0.0", Kind = "widget" },
                new() { Id = "lab.mu-network", Version = "1.0.0", Kind = "widget" }
            };
            var entries = new List<PluginCatalogEntry>
            {
                BuildLabUpdateEntry("lab.mu-update", "1.1.0"),
                BuildLabUpdateEntry("lab.mu-latest", "1.1.0"),
                BuildLabUpdateEntry("lab.mu-blocked", "1.1.0"),
                BuildLabUpdateEntry("lab.mu-incompatible", "1.1.0", minAppVersion: "99.0.0"),
                BuildLabUpdateEntry("lab.mu-network", "1.1.0", permissions: new List<string> { "network" }),
                // registry 里有、本机没装：不算可更新。
                BuildLabUpdateEntry("lab.mu-missing", "1.1.0")
            };

            // 禁用名单：本轮只封 lab.mu-blocked 的已安装版本。
            PluginBlocklist.SaveFromRegistry(new List<BlockedPlugin>
            {
                new() { Id = "lab.mu-blocked", Version = "1.0.0" }
            }, NextLabRegistryStamp());
            PluginBlocklist.Reload();
            try
            {
                item.Check(PluginService.CountAvailableUpdatesInEntries(records, entries) == 2,
                    "update count: one normal update + one network-consent update, blocked/incompatible/latest/missing excluded");
                item.Check(PluginService.CountAvailableUpdatesInEntries(
                        new[] { records[2] }, new[] { entries[2] }) == 0,
                    "update count: a blocked installed version is not updatable");
                item.Check(PluginService.CountAvailableUpdatesInEntries(
                        new[] { records[3] }, new[] { entries[3] }) == 0,
                    "update count: an incompatible new version is not counted as updatable");
                item.Check(PluginService.CountAvailableUpdatesInEntries(
                        new[] { records[1] }, new[] { entries[1] }) == 0,
                    "update count: a plugin already on the latest version is not updatable");
                item.Check(PluginService.CountAvailableUpdatesInEntries(records, null) == 0,
                    "update count: no registry entries means zero updates");
                item.Check(PluginService.CountAvailableUpdates(
                        records, new PluginCatalogResult { Offline = true }) == 0,
                    "update count: an offline catalog is always zero (badge must not scare on stale data)");
                var cachedCatalog = new PluginCatalogResult { FromCache = true };
                cachedCatalog.Entries.AddRange(entries);
                item.Check(PluginService.CountAvailableUpdates(records, cachedCatalog) == 2,
                    "update count: a fresh cached catalog still counts updates");
            }
            finally
            {
                // 还原：更晚的 updatedAt 表示 registry 撤回了这条封禁（单调保护要求严格递增）。
                PluginBlocklist.SaveFromRegistry(new List<BlockedPlugin>(), NextLabRegistryStamp());
                PluginBlocklist.Reload();
            }
            item.Check(!PluginBlocklist.IsBlocked("lab.mu-blocked", "1.0.0"),
                "the synthetic blocklist entry is cleared again");

            // ── 2/6. AvailableUpdateCount 属性 + 变化事件（数值不变不发事件）──
            var badgeEvents = 0;
            Action badgeHandler = () => badgeEvents++;
            service.AvailableUpdatesChanged += badgeHandler;
            try
            {
                service.SetAvailableUpdateCountForLab(0);
                badgeEvents = 0;
                service.SetAvailableUpdateCountForLab(3);
                item.Check(service.AvailableUpdateCount == 3, "AvailableUpdateCount exposes the published count");
                item.Check(badgeEvents == 1, "AvailableUpdatesChanged fires once when the count changes");
                service.SetAvailableUpdateCountForLab(3);
                item.Check(badgeEvents == 1, "AvailableUpdatesChanged does not fire when the count is unchanged");
                service.SetAvailableUpdateCountForLab(0);
                item.Check(service.AvailableUpdateCount == 0 && badgeEvents == 2,
                    "AvailableUpdatesChanged fires when the count drops back to zero");
            }
            finally
            {
                service.AvailableUpdatesChanged -= badgeHandler;
                service.SetAvailableUpdateCountForLab(0);
            }

            // ── 3/6. 侧栏角标：0 隐藏 / 3 显示 3 / 120 显示 99+ ──
            item.Check(!SidebarNav.ShouldShowUpdateBadge(0) && SidebarNav.FormatUpdateBadge(0) == "",
                "badge is hidden at zero");
            item.Check(SidebarNav.ShouldShowUpdateBadge(3) && SidebarNav.FormatUpdateBadge(3) == "3",
                "badge shows the plain number for 3");
            item.Check(SidebarNav.FormatUpdateBadge(99) == "99", "badge shows 99 as-is");
            item.Check(SidebarNav.FormatUpdateBadge(120) == "99+", "badge caps above 99 at 99+");
            var nav = new SidebarNav();
            nav.SetUpdateBadgeForLab(5);
            item.Check(nav.BadgeVisibleForLab && nav.BadgeTextForLab == "5",
                "the sidebar badge control becomes visible with its text");
            nav.SetUpdateBadgeForLab(0);
            item.Check(!nav.BadgeVisibleForLab, "the sidebar badge hides again at zero");

            // ── 4/6. 批量更新计划：只含可更新且兼容；联网权限单独列出；blocked/不兼容跳过 ──
            var installedPermissions = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["lab.mu-update"] = new[] { "storage" },
                ["lab.mu-network"] = new[] { "storage" }
            };
            var plan = PluginsPage.BuildBatchUpdatePlan(
                records,
                entries,
                id => installedPermissions.TryGetValue(id, out var permissions) ? permissions : Array.Empty<string>(),
                (id, version) => string.Equals(id, "lab.mu-blocked", StringComparison.OrdinalIgnoreCase)
                                 && string.Equals(version, "1.0.0", StringComparison.Ordinal));
            item.Check(plan.Batched.Count == 1 && plan.Batched[0].Entry.Id == "lab.mu-update",
                "the batch plan contains exactly the safe update");
            item.Check(plan.NeedsConsent.Count == 1 && plan.NeedsConsent[0].Entry.Id == "lab.mu-network",
                "a network-permission update is listed for individual confirmation");
            item.Check(plan.NeedsConsent[0].RequiresNetworkConsent,
                "the network update is flagged as requiring consent");
            item.Check(plan.Batched.All(entry => !entry.RequiresNetworkConsent),
                "no network-consent update leaks into the executable batch");
            item.Check(plan.PendingCount == 2, "the pending count covers batch + individual confirmation");
            item.Check(plan.Skipped.Count == 2
                       && plan.Skipped.Any(skipped => skipped.Kind == PluginsPage.BatchUpdateSkipKind.Blocked
                                                      && skipped.Entry.Id == "lab.mu-blocked")
                       && plan.Skipped.Any(skipped => skipped.Kind == PluginsPage.BatchUpdateSkipKind.Incompatible
                                                      && skipped.Entry.Id == "lab.mu-incompatible"),
                "the plan skips the blocked and the incompatible update with distinct reasons");
            item.Check(plan.Skipped.First(skipped => skipped.Kind == PluginsPage.BatchUpdateSkipKind.Incompatible)
                    .Incompatibility == loc.Get("Plugin.Update.Requires", "99.0.0"),
                "the skipped incompatible item carries the localized reason");
            item.Check(!plan.Batched.Concat(plan.NeedsConsent)
                    .Any(entry => entry.Entry.Id is "lab.mu-latest" or "lab.mu-missing")
                && !plan.Skipped.Any(entry => entry.Entry.Id is "lab.mu-latest" or "lab.mu-missing"),
                "the plan ignores up-to-date plugins and entries that are not installed");
            item.Check(plan.Batched[0].AddedPermissions.Count == 0,
                "an update that drops from storage to no permission reports no added permission");
            item.Check(plan.Batched[0].RemovedPermissions.Contains("storage"),
                "the plan reports permissions that the new version no longer requests");
            item.Check(plan.NeedsConsent[0].AddedPermissions.Contains("network"),
                "the plan detects the newly added network permission");
            item.Check(plan.NeedsConsent[0].AddedKinds.Contains(PluginKind.Tool),
                "the plan detects a newly added capability kind");

            // ── 5/6. 批量确认框：条数 / 确认按钮可用性 / 截图 ──
            var dialog = new PluginBatchUpdateDialog(plan);
            windows.Add(dialog);
            item.Check(dialog.BatchedCountForLab == 1 && dialog.NeedsConsentCountForLab == 1
                       && dialog.SkippedCountForLab == 2,
                "the confirmation dialog lists batch / consent / skipped items");
            item.Check(dialog.ConfirmEnabledForLab,
                "the confirm button is enabled when the batch has work to do");
            var emptyDialog = new PluginBatchUpdateDialog(new PluginsPage.BatchUpdatePlan());
            windows.Add(emptyDialog);
            item.Check(!emptyDialog.ConfirmEnabledForLab,
                "the confirm button is disabled when nothing can be batched");
            dialog.Show();
            await Task.Delay(600);
            var batchShot = Path.Combine(dir, "shot-ux-updates-batch.png");
            SaveScreenshot(dialog, batchShot);
            item.Check(File.Exists(batchShot), "batch update dialog screenshot written: shot-ux-updates-batch.png");
            dialog.Close();

            // ── 6/6. 市场列表窗口化：200 条只先建一页，继续加载才增加 ──
            var bigCatalog = new PluginCatalogResult();
            for (var i = 0; i < 200; i++)
            {
                bigCatalog.Entries.Add(BuildLabUpdateEntry($"lab.mu-win-{i:000}", "1.0.0",
                    zhName: $"窗口化插件 {i}"));
            }
            var page = new PluginsPage();
            page.SetCatalogForLab(bigCatalog);
            item.Check(page.TotalCardCountForLab == 200, "the card window keeps all 200 filtered entries");
            item.Check(page.RenderedCardCountForLab == PluginsPage.InitialCardPageSize,
                "only the first card page is materialized for 200 entries");
            page.LoadMoreCardsForLab();
            item.Check(page.RenderedCardCountForLab
                       == PluginsPage.InitialCardPageSize + PluginsPage.NextCardPageSize,
                "loading the next page appends exactly one more page");
            var guard = 0;
            while (page.RenderedCardCountForLab < page.TotalCardCountForLab && guard++ < 100)
                page.LoadMoreCardsForLab();
            item.Check(page.RenderedCardCountForLab == 200,
                "repeatedly loading pages eventually materializes every card");
            item.Check(PluginsPage.ShouldLoadNextCardPage(0, 900, 2160) == false,
                "the next page does not load before the user scrolls");
            item.Check(PluginsPage.ShouldLoadNextCardPage(1600, 900, 2160),
                "the next page loads when the viewport approaches the bottom");
            item.Check(PluginsPage.ShouldLoadNextCardPage(500, 900, 2160) == false,
                "the next page stays away while the user is still far from the bottom");
            item.Check(PluginsPage.ShouldLoadNextCardPage(0, 1000, 1000),
                "a viewport with no scrollbar keeps filling until a scrollbar appears");
            item.Check(PluginsPage.ShouldLoadNextCardPage(0, 0, 0) == false,
                "an unmeasured viewport does not trigger loading");
            // tab 切换重置窗口：再切回市场，只应保留第一页。
            page.SelectTab(1);
            page.SelectTab(0);
            item.Check(page.RenderedCardCountForLab == PluginsPage.InitialCardPageSize,
                "switching tabs resets the card window back to the first page");
            // 类型筛选也重置窗口；筛成空类型时列表为空（空态逻辑不变）。
            page.SetFilterForLab(PluginKind.Widget);
            item.Check(page.TotalCardCountForLab == 200
                       && page.RenderedCardCountForLab == PluginsPage.InitialCardPageSize,
                "a type filter re-runs filtering and resets the card window");
            page.SetFilterForLab(PluginKind.Appearance);
            item.Check(page.TotalCardCountForLab == 0 && page.RenderedCardCountForLab == 0,
                "filtering to an unrepresented kind empties the card list");
            page.SetFilterForLab(null);

            // 已安装 tab 的类型筛选（一个包多类型时命中任一能力）。
            var multiKindRecord = new PluginRecord
            {
                Id = "lab.mu-multikind",
                Version = "1.0.0",
                Kind = "widget",
                Kinds = new List<string> { "widget", "tool" }
            };
            item.Check(PluginsPage.MatchesInstalledFilter(multiKindRecord, PluginKind.Tool),
                "the installed tab filter matches any declared kind");
            item.Check(!PluginsPage.MatchesInstalledFilter(multiKindRecord, PluginKind.Language),
                "the installed tab filter rejects a kind the plugin does not declare");
            item.Check(PluginsPage.MatchesInstalledFilter(multiKindRecord, null),
                "the installed tab shows everything without a filter");

            // ── 下载进度映射 0..1 → 0..100（含越界钳制）──
            item.Check(PluginsPage.ProgressPercent(0) == 0, "progress 0 maps to 0%");
            item.Check(PluginsPage.ProgressPercent(0.42) == 42, "progress 0.42 maps to 42%");
            item.Check(PluginsPage.ProgressPercent(1) == 100, "progress 1 maps to 100%");
            item.Check(PluginsPage.ProgressPercent(-0.5) == 0 && PluginsPage.ProgressPercent(1.5) == 100,
                "out-of-range progress is clamped to 0..100");
            var progressText = loc.Get("Plugin.Progress.Downloading", "Demo", PluginsPage.ProgressPercent(0.42));
            item.Check(progressText.Contains("Demo") && progressText.Contains("42"),
                "the download progress text names the plugin and the percent");

            // 真实管理窗口：更新 tab 的「全部更新」按钮 + 侧栏角标截图。
            // 让更新 tab 有确定内容：装好样例插件 → 写一份「有新版本」的本地 registry 缓存 →
            // 页面从缓存重载（不发网络；启动后台拉取已被 lab 关掉）。
            await EnsureEnabledAsync(dir);
            WriteLabMarketRegistry(dir);
            EnsureManagementWindow();
            var window = ManagementWindow!;
            windows.Add(window);
            window.Show();
            window.ShowSection("plugins");
            window.Width = 1440;
            window.Height = 920;
            window.Left = 60;
            window.Top = 40;
            // 截图干净一点：前面的 lab 用例可能把右侧属性面板打开了（这里只影响截图）。
            window.RightPane.Visibility = Visibility.Collapsed;
            window.RightCol.Width = new GridLength(0);
            await Task.Delay(1500); // 等页面刷新（本地缓存）与布局
            var updatesPageReachable = window.MainContent.Content is PluginsPage;
            var updateCount = 0;
            if (window.MainContent.Content is PluginsPage livePage)
            {
                // 前面用例可能切换过语言/留下旧页面：按当前语言重建文案，再从缓存重载 registry。
                livePage.RebuildTextsForLab();
                livePage.ReloadCatalogFromCacheForLab();
                livePage.SelectTab(2);
                await Task.Delay(600);
                updateCount = livePage.UpdateCountForLab;
                item.Check(updateCount >= 1,
                    "the updates tab lists the newer registry version for an installed plugin");
                item.Check(livePage.UpdateAllVisibleForLab,
                    "the update-all button is visible on the updates tab");
                item.Check(livePage.UpdateAllEnabledForLab == (updateCount > 0),
                    "the update-all button is enabled exactly when the updates tab has entries");
                var updatesTabShot = Path.Combine(dir, "shot-ux-updates-tab.png");
                SaveScreenshot(window, updatesTabShot);
                item.Check(File.Exists(updatesTabShot),
                    "updates tab screenshot written: shot-ux-updates-tab.png");
                // 角标截图换回市场 tab：两张图能分别看清「更新列表 + 全部更新」和「侧栏角标」。
                livePage.SelectTab(0);
                await Task.Delay(600);
            }
            else
            {
                item.Check(false, "the plugins page is reachable from the management window");
            }
            item.Check(updatesPageReachable, "the plugins page stays reachable after tab switching");

            // 侧栏角标：走真实的 AvailableUpdatesChanged 事件（不是直接写控件），验证订阅链路。
            service.SetAvailableUpdateCountForLab(120);
            await Task.Delay(250);
            item.Check(window.SideNav.BadgeVisibleForLab && window.SideNav.BadgeTextForLab == "99+",
                "the sidebar badge caps at 99+ in the live window");
            var badgeCount = Math.Max(1, updateCount);
            service.SetAvailableUpdateCountForLab(badgeCount);
            await Task.Delay(250);
            item.Check(window.SideNav.BadgeVisibleForLab
                       && window.SideNav.BadgeTextForLab == SidebarNav.FormatUpdateBadge(badgeCount),
                "the sidebar badge follows AvailableUpdatesChanged in the live window");
            var sidebarShot = Path.Combine(dir, "shot-ux-sidebar-badge.png");
            SaveScreenshot(window, sidebarShot);
            item.Check(File.Exists(sidebarShot),
                "sidebar badge screenshot written: shot-ux-sidebar-badge.png");
            service.SetAvailableUpdateCountForLab(0);
            await Task.Delay(200);
            item.Check(!window.SideNav.BadgeVisibleForLab,
                "the sidebar badge hides again when no update is available");
        }
        catch (Exception ex)
        {
            item.Check(false, "market-updates case crashed: " + ex);
        }
        finally
        {
            foreach (var window in windows)
            {
                try { window.Close(); } catch { }
            }
        }
    }

    /// <summary>自测台用的合成 registry 条目（可指定 minAppVersion / 权限 / 显示名）。</summary>
    static PluginCatalogEntry BuildLabUpdateEntry(
        string id,
        string version,
        string? minAppVersion = null,
        List<string>? permissions = null,
        string? zhName = null)
        => new()
        {
            Id = id,
            Kind = "widget",
            Kinds = id == "lab.mu-network" ? new List<string> { "widget", "tool" } : null,
            Name = new LocalizedText { Zh = zhName ?? id, En = id },
            Summary = new LocalizedText { Zh = "自测台合成条目", En = "lab synthetic entry" },
            Changelog = new LocalizedText { Zh = "修复若干问题，并改进桌面组件的渲染效果。", En = "Bug fixes and rendering improvements." },
            Author = new PluginAuthor { Name = "DeskOrder Lab", Github = "lab" },
            Repo = "lab/" + id,
            Version = version,
            ApiVersion = 1,
            MinAppVersion = minAppVersion ?? "1.0.0",
            License = "MIT",
            Permissions = permissions,
            DownloadUrl = "https://github.com/lab/plugins/releases/download/v" + version + "/" + id + ".dopkg",
            Size = 2048,
            Sha256 = LabShotSha,
            PublishedAt = DateTime.UtcNow.AddDays(-3)
        };
}
#endif
