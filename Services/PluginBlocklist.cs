using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DesktopZones.Helpers;
using DesktopZones.Models;

namespace DesktopZones.Services;

/// <summary>
/// 紧急禁用名单的本地副本（<c>Plugins\.state\blocked.json</c>）。
///
/// 为什么落盘：名单来自 registry，但「禁用」是个安全动作——不能因为断网、缓存过期，
/// 或者用户从不打开市场页，就让已被禁用的插件继续跑。所以每次成功联网拉到 registry
/// 就写一份到本地，之后启动时直接按本地副本判定。
///
/// 语义与桌面版一致：<c>version</c> 为空 = 禁用该插件的全部版本；命中即强制停用，
/// 用户不能手动重新启用，但可以卸载。
/// </summary>
public static class PluginBlocklist
{
    static readonly object Gate = new();
    static List<BlockedPlugin> _entries = new();
    static bool _loaded;
    static DateTime _sourceUpdatedAtUtc;

    sealed class BlockedFile
    {
        public DateTime UpdatedAtUtc { get; set; }
        /// <summary>这份名单对应的 registry 更新时间（用于判断新旧）。</summary>
        public DateTime SourceUpdatedAtUtc { get; set; }
        public List<BlockedPlugin> Entries { get; set; } = new();
    }

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static string FilePath => PluginPaths.BlockedListPath;

    public static void Load()
    {
        lock (Gate)
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                var path = FilePath;
                if (!File.Exists(path)) return;
                var text = File.ReadAllText(path).TrimStart('\uFEFF');
                var file = JsonSerializer.Deserialize<BlockedFile>(text, JsonOptions);
                if (file == null) return;
                // 来源时间必须一起读回来：SaveFromRegistry 的单调性保护靠它判断「本地名单是否
                // 有更强的旧证据」。以前只恢复 Entries、丢掉 SourceUpdatedAtUtc，重启后
                // localSourceKnown 永远为 false，旧 registry 就能把本地封禁清空。
                _sourceUpdatedAtUtc = file.SourceUpdatedAtUtc;
                if (file.Entries != null)
                    _entries = file.Entries.Where(e => !string.IsNullOrWhiteSpace(e.Id)).ToList();
            }
            catch (Exception ex)
            {
                DzTrace.Always("[Plugin] cannot read the blocked list: " + ex.Message);
            }
        }
    }

    public static void Reload()
    {
        lock (Gate)
        {
            _loaded = false;
            _entries = new List<BlockedPlugin>();
            // 重新解析 blocked.json 时来源时间也要重新读：旧的「本地来源时间」不属于新文件。
            _sourceUpdatedAtUtc = default;
        }
        Load();
    }

    public static IReadOnlyList<BlockedPlugin> Entries
    {
        get
        {
            Load();
            lock (Gate) return _entries.ToList();
        }
    }

#if DEBUG
    /// <summary>自测台断言用：本地副本记录的「来源 registry updatedAt」。Release 构建不存在。</summary>
    internal static DateTime SourceUpdatedAtUtc
    {
        get
        {
            Load();
            lock (Gate) return _sourceUpdatedAtUtc;
        }
    }
#endif

    public static BlockedPlugin? Find(string pluginId, string version)
    {
        Load();
        lock (Gate)
            return _entries.FirstOrDefault(entry => entry.Matches(pluginId, version));
    }

    public static bool IsBlocked(string pluginId, string version) => Find(pluginId, version) != null;

    /// <summary>
    /// 联网拿到 registry 后调用（只有「网络新鲜」的结果才允许覆盖本地副本）。
    ///
    /// 单调性保护：禁用是安全动作，不能让「旧 CDN 缓存 / 仓库回滚 / 被改过的 registry」
    /// 悄悄把本地已有的封禁清空。所以只有当新名单是旧名单的超集、或者 registry 的
    /// <c>updatedAt</c> 确实比本地记录的新时，才整体替换；否则取并集。
    /// </summary>
    public static void SaveFromRegistry(List<BlockedPlugin>? blocked, DateTime? registryUpdatedAt = null)
    {
        var incoming = (blocked ?? new List<BlockedPlugin>())
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Id))
            .GroupBy(entry => entry.Id + "@" + (entry.Version ?? "*"), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        lock (Gate)
        {
            var previous = _loaded ? _entries : new List<BlockedPlugin>();
            var previousSource = _sourceUpdatedAtUtc;

            var newKeys = incoming.Select(Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var isSuperset = previous.All(entry => newKeys.Contains(Key(entry)));
            // 本地副本没有记录来源时间（旧格式 / 自测台直接写）时按「可替换」处理：
            // 我们手里没有更强的旧证据，坚持并集只会让名单永远清不掉。
            var localSourceKnown = previousSource != default;
            var incomingIsNewer = registryUpdatedAt is { } stampCheck
                                  && (!localSourceKnown || stampCheck > previousSource);

            List<BlockedPlugin> effective;
            if (isSuperset || incomingIsNewer || !localSourceKnown)
            {
                effective = incoming;
                if (registryUpdatedAt is { } stampApply) _sourceUpdatedAtUtc = stampApply;
            }
            else
            {
                // 不敢整体替换：保住本地已有的封禁，只做并集。
                effective = previous.Concat(incoming.Where(entry => !previous.Any(p => Key(p) == Key(entry))))
                    .ToList();
                DzTrace.Always(
                    $"[Plugin] blocklist from registry looks older than the local copy " +
                    $"({incoming.Count} vs {previous.Count}); keeping the union");
            }

            _entries = effective;
            _loaded = true;
            try
            {
                PluginPaths.EnsureCreated();
                var json = JsonSerializer.Serialize(new BlockedFile
                {
                    UpdatedAtUtc = DateTime.UtcNow,
                    SourceUpdatedAtUtc = _sourceUpdatedAtUtc,
                    Entries = effective
                }, JsonOptions);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch (Exception ex)
            {
                DzTrace.Always("[Plugin] cannot persist the blocked list: " + ex.Message);
            }
        }
    }

    static string Key(BlockedPlugin entry)
        => entry.Id.Trim().ToLowerInvariant() + "@"
           + (string.IsNullOrWhiteSpace(entry.Version) ? "*" : entry.Version.Trim().ToLowerInvariant());

    /// <summary>禁用原因的本地化文本；没写 reason 时给一句兜底。</summary>
    public static string ReasonOf(BlockedPlugin entry, string language)
    {
        var text = entry.Reason?.Get(language);
        if (!string.IsNullOrWhiteSpace(text)) return text!;
        return LocalizationService.Instance["Plugin.Blocked.NoReason"];
    }
}
