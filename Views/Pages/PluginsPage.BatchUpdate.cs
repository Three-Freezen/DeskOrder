using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using DeskOrder.PluginApi;
using DesktopZones.Helpers;
using DesktopZones.Models;
using DesktopZones.Services;
using DesktopZones.Views;

namespace DesktopZones.Views.Pages;

/// <summary>
/// 插件市场的「全部更新」（v1.2.3）：聚合确认框 + 顺序批量安装。
///
/// 单独拆 partial 文件，是为了让批量更新计划（<see cref="BuildBatchUpdatePlan"/>）可以被
/// 自测台当成纯函数断言，也避免和页面主文件里其他并行改动的区块互相覆盖。
///
/// 安全边界：
/// <list type="bullet">
/// <item>只用 <see cref="PluginService.IsUpdatable"/> 认定的更新（版本更新、未禁用、兼容宿主）。</item>
/// <item>新增 <c>network</c> 权限的更新**不进入批量**：它必须像单条更新那样逐个勾选确认
/// （<see cref="PluginUpdateDialog"/> 已有的联网同意门禁），批量路径不能绕过这条安全闸门。</item>
/// <item>新增其它权限 / 新增能力只做提示，不阻断（与单条更新的策略一致）。</item>
/// </list>
/// </summary>
public partial class PluginsPage
{
    /// <summary>批量计划里一条被跳过的更新的原因。</summary>
    internal enum BatchUpdateSkipKind
    {
        /// <summary>旧版本或新版本命中官方禁用名单。</summary>
        Blocked,
        /// <summary>新版本要求更高宿主（apiVersion / minAppVersion）。</summary>
        Incompatible
    }

    /// <summary>一个待更新条目：记录 + registry 条目 + 版本差异（权限 / 能力）。</summary>
    internal sealed class BatchUpdateItem
    {
        public required PluginRecord Record { get; init; }
        public required PluginCatalogEntry Entry { get; init; }
        public IReadOnlyList<string> AddedPermissions { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> RemovedPermissions { get; init; } = Array.Empty<string>();
        public IReadOnlyList<PluginKind> AddedKinds { get; init; } = Array.Empty<PluginKind>();

        /// <summary>新版本新增联网权限：必须逐个确认，批量路径跳过（不提供「一键同意」）。</summary>
        public bool RequiresNetworkConsent =>
            AddedPermissions.Any(permission => string.Equals(permission, "network", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>一个被跳过的更新（对话框里说明原因，不参与批量执行）。</summary>
    internal sealed class BatchUpdateSkippedItem
    {
        public required PluginRecord Record { get; init; }
        public required PluginCatalogEntry Entry { get; init; }
        public BatchUpdateSkipKind Kind { get; init; }
        /// <summary>不兼容原因（<see cref="BatchUpdateSkipKind.Incompatible"/> 时有值）。</summary>
        public string? Incompatibility { get; init; }
    }

    /// <summary>
    /// 批量更新计划：<see cref="Batched"/> 会被顺序执行；<see cref="NeedsConsent"/> 新增了联网
    /// 权限，只在对话框里列出并提示逐个确认；<see cref="Skipped"/> 是被禁用/不兼容的更新。
    /// </summary>
    internal sealed class BatchUpdatePlan
    {
        public List<BatchUpdateItem> Batched { get; } = new();
        public List<BatchUpdateItem> NeedsConsent { get; } = new();
        public List<BatchUpdateSkippedItem> Skipped { get; } = new();

        /// <summary>待更新总数（不含跳过）：批量条数 + 需逐个确认条数。</summary>
        public int PendingCount => Batched.Count + NeedsConsent.Count;
    }

    /// <summary>
    /// 构建批量更新计划（纯函数，无 UI / 无网络）：自测台直接断言「只含可更新且兼容的条目」
    /// 「联网权限条目单独列出」「blocked / 不兼容被跳过」。
    /// </summary>
    /// <param name="records">已安装记录。</param>
    /// <param name="entries">registry 条目（顺序即对话框里的展示顺序）。</param>
    /// <param name="installedPermissionsOf">读取已安装版本声明的权限，用于算权限差异。</param>
    /// <param name="isBlocked">(插件 id, 版本) 是否命中禁用名单。</param>
    internal static BatchUpdatePlan BuildBatchUpdatePlan(
        IEnumerable<PluginRecord> records,
        IEnumerable<PluginCatalogEntry> entries,
        Func<string, IReadOnlyList<string>> installedPermissionsOf,
        Func<string, string, bool> isBlocked)
    {
        var byId = new Dictionary<string, PluginRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records)
        {
            if (!string.IsNullOrEmpty(record.Id)) byId.TryAdd(record.Id, record);
        }

        var plan = new BatchUpdatePlan();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (string.IsNullOrEmpty(entry.Id) || !seen.Add(entry.Id)) continue;
            if (!byId.TryGetValue(entry.Id, out var record)) continue;
            // 已是最新（或 resolution 说没有新版本）→ 完全不算待更新。
            if (!PluginDetailDialog.IsUpdateAvailable(record, entry)) continue;

            if (isBlocked(record.Id, record.Version) || isBlocked(entry.Id, entry.Version))
            {
                plan.Skipped.Add(new BatchUpdateSkippedItem
                {
                    Record = record,
                    Entry = entry,
                    Kind = BatchUpdateSkipKind.Blocked
                });
                continue;
            }

            var incompatibility = PluginDisplay.IncompatibilityReason(entry);
            if (incompatibility != null)
            {
                plan.Skipped.Add(new BatchUpdateSkippedItem
                {
                    Record = record,
                    Entry = entry,
                    Kind = BatchUpdateSkipKind.Incompatible,
                    Incompatibility = incompatibility
                });
                continue;
            }

            var before = installedPermissionsOf(record.Id) ?? Array.Empty<string>();
            var after = entry.Permissions ?? new List<string>();
            var addedPermissions = after
                .Where(permission => !string.IsNullOrWhiteSpace(permission))
                .Where(permission => !before.Contains(permission, StringComparer.OrdinalIgnoreCase))
                .ToList();
            var removedPermissions = before
                .Where(permission => !string.IsNullOrWhiteSpace(permission))
                .Where(permission => !after.Contains(permission, StringComparer.OrdinalIgnoreCase))
                .ToList();
            var addedKinds = entry.KindValues
                .Where(kind => !record.KindValues.Contains(kind))
                .ToList();

            var item = new BatchUpdateItem
            {
                Record = record,
                Entry = entry,
                AddedPermissions = addedPermissions,
                RemovedPermissions = removedPermissions,
                AddedKinds = addedKinds
            };
            if (item.RequiresNetworkConsent) plan.NeedsConsent.Add(item);
            else plan.Batched.Add(item);
        }
        return plan;
    }

    /// <summary>按页面当前 catalog + 已安装记录构建计划（点击「全部更新」时调用）。</summary>
    BatchUpdatePlan BuildBatchPlanFromPage()
    {
        IEnumerable<PluginCatalogEntry> entries = Array.Empty<PluginCatalogEntry>();
        if (_catalog != null)
        {
            // 与更新 tab 相同的排序（有更新 → 官方 → 发布时间降序），对话框顺序和列表一致。
            var pending = _catalog.Entries.Where(entry =>
            {
                var record = _service.GetRecord(entry.Id);
                return record != null && PluginService.IsUpdatable(record, entry);
            });
            entries = SortCatalogEntries(pending, _service.GetRecord);
        }

        return BuildBatchUpdatePlan(
            _service.Records,
            entries,
            recordId => PermissionsOfInstalled(recordId),
            (id, version) => PluginBlocklist.Find(id, version) != null);
    }

    /// <summary>读已安装版本 manifest 的权限声明（读不出按「没申请」处理）。</summary>
    IReadOnlyList<string> PermissionsOfInstalled(string pluginId)
    {
        var record = _service.GetRecord(pluginId);
        var manifest = record == null ? null : ReadManifest(record);
        return manifest?.Permissions ?? (IReadOnlyList<string>)Array.Empty<string>();
    }

    async void UpdateAll_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await RunBatchUpdateAsync();
        }
        catch (Exception ex)
        {
            // async void 漏出去的异常会进 DispatcherUnhandledException 弹「DeskOrder 错误」。
            ShowStatus(Loc.Get("Plugin.Batch.Failed", ex.Message));
            DzTrace.Always("[Plugin] batch update failed: " + ex);
        }
    }

    async System.Threading.Tasks.Task RunBatchUpdateAsync()
    {
        if (_busy || _loading) return;
        var plan = BuildBatchPlanFromPage();
        if (plan.PendingCount == 0 && plan.Skipped.Count == 0) return;

        var dialog = new PluginBatchUpdateDialog(plan) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        var items = plan.Batched.ToList();
        if (items.Count == 0) return;

        _busy = true;
        Render(); // 让「全部更新」和卡片上的单条操作立即进入忙碌态
        var succeeded = 0;
        var failed = 0;
        var codeUpdated = false;
        var failures = new List<string>();
        try
        {
            for (var i = 0; i < items.Count; i++)
            {
                // 关闭期不再发起下一次安装（安装器本身也会失败，但没必要继续）。
                if (AppLifetime.IsShuttingDown) return;
                var item = items[i];
                var name = DisplayNameOf(item.Entry);
                ShowStatus(Loc.Get("Plugin.Batch.Progress", i + 1, items.Count, name));
                var result = await _service.Installer!.InstallFromCatalogAsync(
                    item.Entry, CreateDownloadProgress(name));
                // 每装完一条就同步记录：中途失败/关闭时已装好的版本也不会从列表里“消失”。
                _service.RefreshRecords();
                if (result.Success)
                {
                    succeeded++;
                    if (item.Entry.KindValue.IsCode()) codeUpdated = true;
                }
                else
                {
                    failed++;
                    failures.Add("· " + name + " → v" + item.Entry.Version + "：" + (result.Error ?? ""));
                }
            }
        }
        finally
        {
            EndDownloadProgress();
            _busy = false;
            Render();
        }

        if (AppLifetime.IsShuttingDown || !IsLoaded) return;
        var summary = Loc.Get("Plugin.Batch.Summary",
            succeeded, failed, plan.NeedsConsent.Count, plan.Skipped.Count);
        if (failures.Count > 0) summary += "\n\n" + string.Join("\n", failures.Take(6));
        MessageBox.Show(Window.GetWindow(this), summary, Loc["Plugin.Batch.Title"],
            MessageBoxButton.OK, failed > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        // 代码类插件更新后要重启才生效：与单条更新一致，结束后只提示一次。
        if (codeUpdated) PromptRestart();
    }

    /// <summary>插件显示名（按当前语言，空则退回 id）。</summary>
    internal static string DisplayNameOf(PluginCatalogEntry entry)
    {
        var name = entry.Name?.Get(Loc.CurrentLanguage);
        return string.IsNullOrWhiteSpace(name) ? entry.Id : name;
    }
}
