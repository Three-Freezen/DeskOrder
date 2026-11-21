using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DesktopZones.Helpers;
using DesktopZones.Models;

namespace DesktopZones.Services;

public sealed class PluginCatalogResult
{
    public List<PluginCatalogEntry> Entries { get; } = new();
    /// <summary>registry 顶层的紧急禁用名单（同时会写进 <see cref="PluginBlocklist"/>）。</summary>
    public List<BlockedPlugin> Blocked { get; } = new();
    public bool FromCache { get; set; }
    public bool Offline { get; set; }
    public DateTime? FetchedAtUtc { get; set; }
    /// <summary>registry 自带的 updatedAt（用于判断禁用名单新旧）。</summary>
    public DateTime? RegistryUpdatedAt { get; set; }
    public string? Error { get; set; }
    public bool IsEmpty => Entries.Count == 0;
}

/// <summary>
/// 市场索引（<c>registry.json</c>）拉取 / ETag 缓存 / 离线降级 / 图片缓存。
/// 只认 registry，不走 GitHub API（避免限流，也避免「谁打 tag 谁上架」）。
/// </summary>
public sealed class PluginCatalogService
{
    public const string DefaultRegistryUrl =
        "https://raw.githubusercontent.com/Three-Freezen/DeskOrder-Plugins/main/registry.json";

    /// <summary>图片缓存默认上限 50 MB（方案 §5.7「按 URL SHA-1 缓存，LRU 上限 50 MB」）。</summary>
    public const long DefaultImageCacheBytes = 50L * 1024 * 1024;

    static readonly TimeSpan CacheTtl = TimeSpan.FromHours(6);
    const long MaxRegistryBytes = 2L * 1024 * 1024;
    const long MaxImageBytes = 4L * 1024 * 1024;

    /// <summary>
    /// 图片缓存上限（字节）。自测台会临时调小来验证 LRU 清理；正常使用就是
    /// <see cref="DefaultImageCacheBytes"/>。做成实例属性而不是常量，是为了让「清理策略」
    /// 能在不写满 50 MB 假文件的前提下被真实跑到。
    /// </summary>
    public long MaxImageCacheBytes { get; set; } = DefaultImageCacheBytes;

    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DeskOrder-PluginMarket/1.0");
        return client;
    }

    public string RegistryUrl { get; set; } = DefaultRegistryUrl;

    sealed class CacheMeta
    {
        public string? Etag { get; set; }
        public DateTime FetchedAtUtc { get; set; }
    }

    public async Task<PluginCatalogResult> LoadAsync(bool force, CancellationToken ct = default)
    {
        PluginPaths.EnsureCreated();
        var meta = ReadMeta();
        var cachePath = PluginPaths.RegistryCachePath;
        bool hasCache = File.Exists(cachePath);

        if (!force && hasCache && meta.FetchedAtUtc != default
            && DateTime.UtcNow - meta.FetchedAtUtc < CacheTtl)
        {
            var cached = ParseCache(meta);
            // 缓存损坏（Error 非空）时不要拿空表充数，继续尝试网络。
            if (cached.Error == null) return cached;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, RegistryUrl);
            if (hasCache && !string.IsNullOrEmpty(meta.Etag))
                request.Headers.TryAddWithoutValidation("If-None-Match", meta.Etag);

            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotModified && hasCache)
            {
                WriteMeta(new CacheMeta { Etag = meta.Etag, FetchedAtUtc = DateTime.UtcNow });
                var cached = ParseCache(new CacheMeta
                {
                    Etag = meta.Etag,
                    FetchedAtUtc = DateTime.UtcNow
                });
                if (cached != null) return cached;
            }

            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxRegistryBytes)
                throw new InvalidOperationException("registry.json is larger than 2 MB");

            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (bytes.Length > MaxRegistryBytes)
                throw new InvalidOperationException("registry.json is larger than 2 MB");

            var etag = response.Headers.ETag?.Tag;
            WriteCacheAtomically(bytes);
            var fetchedAt = DateTime.UtcNow;
            WriteMeta(new CacheMeta { Etag = etag, FetchedAtUtc = fetchedAt });

            var result = ParseRegistry(bytes);
            result.FetchedAtUtc = fetchedAt;
            // 只有「网络新鲜」的名单才覆盖本地副本：离线和缓存不能悄悄解禁。
            PluginBlocklist.SaveFromRegistry(result.Blocked, result.RegistryUpdatedAt);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var cached = hasCache ? ParseCache(meta) : null;
            if (cached != null)
            {
                cached.Offline = true;
                cached.Error = ex.Message;
                return cached;
            }
            return new PluginCatalogResult { Offline = true, Error = ex.Message };
        }
    }

    PluginCatalogResult ParseCache(CacheMeta meta)
    {
        try
        {
            var path = PluginPaths.RegistryCachePath;
            if (!File.Exists(path)) return new PluginCatalogResult();
            var result = ParseRegistry(File.ReadAllBytes(path));
            result.FromCache = true;
            result.FetchedAtUtc = meta.FetchedAtUtc == default ? File.GetLastWriteTimeUtc(path) : meta.FetchedAtUtc;
            return result;
        }
        catch
        {
            return new PluginCatalogResult();
        }
    }

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>解析 registry 字节。internal：自测台要直接验证「不合格条目被丢弃」。</summary>
    internal PluginCatalogResult ParseRegistry(byte[] bytes)
    {
        var result = new PluginCatalogResult();
        PluginCatalogFile? file;
        try
        {
            var text = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
            file = JsonSerializer.Deserialize<PluginCatalogFile>(text, JsonOptions);
        }
        catch (JsonException)
        {
            result.Error = "registry.json is not valid JSON";
            return result;
        }
        if (file == null) return result;

        if (file.SchemaVersion != 1)
        {
            result.Error = $"unsupported registry schemaVersion {file.SchemaVersion}";
            return result;
        }
        result.FetchedAtUtc = file.UpdatedAt;
        result.RegistryUpdatedAt = file.UpdatedAt;
        if (file.Blocked != null)
            result.Blocked.AddRange(file.Blocked.Where(b => !string.IsNullOrWhiteSpace(b.Id)));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in file.Plugins ?? new List<PluginCatalogEntry>())
        {
            // registry 是外部数据：单条畸形不能让整个市场变空/渲染崩，但也不能「兜底后放行」——
            // 缺 sha256 / 尺寸离谱的条目一旦进市场，安装器就只能跳过校验或下载垃圾。
            // 所以这里逐条严格校验，不合规的整条丢弃并留日志。
            if (entry == null) continue;
            try
            {
                entry.Id = entry.Id?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(entry.Id)) continue;
                if (!PluginManifest.IdRe.IsMatch(entry.Id))
                {
                    DzTrace.Always($"[Plugin] registry entry '{entry.Id}' dropped: id is not a valid plugin id");
                    continue;
                }
                if (seen.Contains(entry.Id)) continue;

                entry.Name ??= new LocalizedText();
                entry.Summary ??= new LocalizedText();
                entry.Author ??= new PluginAuthor();
                entry.Version = entry.Version?.Trim() ?? "";
                entry.Kind = entry.Kind?.Trim() ?? "";
                entry.License ??= "";
                entry.MinAppVersion = entry.MinAppVersion?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(entry.Name.Get("zh")) && string.IsNullOrWhiteSpace(entry.Name.Get("en")))
                    continue;

                if (!PluginManifest.SemVerRe.IsMatch(entry.Version)
                    || entry.Version.EndsWith('.') || entry.Version.Contains(".."))
                {
                    DzTrace.Always($"[Plugin] registry entry '{entry.Id}' dropped: version '{entry.Version}' is not SemVer");
                    continue;
                }
                if (!PluginKinds.TryParse(entry.Kind, out _))
                {
                    DzTrace.Always($"[Plugin] registry entry '{entry.Id}' dropped: kind '{entry.Kind}' is unknown");
                    continue;
                }
                if (!string.IsNullOrEmpty(entry.MinAppVersion)
                    && !PluginManifest.SemVerRe.IsMatch(entry.MinAppVersion))
                {
                    DzTrace.Always($"[Plugin] registry entry '{entry.Id}' dropped: " +
                                   $"minAppVersion '{entry.MinAppVersion}' is not SemVer");
                    continue;
                }
                if (string.IsNullOrWhiteSpace(entry.DownloadUrl)
                    || !entry.DownloadUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (entry.Size < 1 || entry.Size > PluginPackages.MaxPackageBytes)
                {
                    DzTrace.Always($"[Plugin] registry entry '{entry.Id}' dropped: size {entry.Size} " +
                                   $"is outside 1..{PluginPackages.MaxPackageBytes}");
                    continue;
                }
                if (!PluginPackages.IsValidSha256(entry.Sha256))
                {
                    DzTrace.Always($"[Plugin] registry entry '{entry.Id}' dropped: sha256 is missing or not 64 hex chars");
                    continue;
                }

                seen.Add(entry.Id);
                result.Entries.Add(entry);
            }
            catch (Exception ex)
            {
                DzTrace.Always($"[Plugin] skipping a malformed registry entry: {ex.Message}");
            }
        }

        result.Entries.Sort((a, b) =>
        {
            if (a.Official != b.Official) return a.Official ? -1 : 1;
            var ad = a.PublishedAt ?? DateTime.MinValue;
            var bd = b.PublishedAt ?? DateTime.MinValue;
            return bd.CompareTo(ad);
        });
        return result;
    }

    static void WriteCacheAtomically(byte[] bytes)
    {
        var path = PluginPaths.RegistryCachePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
    }

    static CacheMeta ReadMeta()
    {
        try
        {
            var path = PluginPaths.RegistryMetaPath;
            if (!File.Exists(path)) return new CacheMeta();
            return JsonSerializer.Deserialize<CacheMeta>(File.ReadAllText(path), JsonOptions) ?? new CacheMeta();
        }
        catch
        {
            return new CacheMeta();
        }
    }

    static void WriteMeta(CacheMeta meta)
    {
        try
        {
            Directory.CreateDirectory(PluginPaths.CatalogDir);
            File.WriteAllText(PluginPaths.RegistryMetaPath,
                JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    /// <summary>图片缓存。返回本地绝对路径；失败返回 null（卡片退回占位图）。</summary>
    public async Task<string?> GetImageAsync(string? url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps) return null;
        if (!(uri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
              || uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
              || uri.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase)))
            return null;

        var ext = Path.GetExtension(uri.AbsolutePath);
        if (string.IsNullOrEmpty(ext) || ext.Length > 5) ext = ".png";
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant();
        var path = Path.Combine(PluginPaths.ImagesDir, hash + ext);
        if (File.Exists(path) && new FileInfo(path).Length > 0)
        {
            // 命中即刷新时间戳 —— 这就是本实现的「LRU」：不额外维护访问计数文件，
            // 缓存目录自身就是唯一状态（崩溃/被清空都不影响正确性）。
            TouchImageCacheEntry(path);
            return path;
        }

        try
        {
            Directory.CreateDirectory(PluginPaths.ImagesDir);
            using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxImageBytes) return null;
            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (bytes.Length == 0 || bytes.Length > MaxImageBytes) return null;
            var tmp = path + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes, ct).ConfigureAwait(false);
            File.Move(tmp, path, overwrite: true);
            // 写入后立即收敛一次：市场页一次刷新会并发下载几十张封面，等「下次启动再清」
            // 会让缓存无界增长。
            TrimImageCache();
            return path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>缓存命中时把文件时间改到现在（近似 LRU 的「最近使用」标记）。失败不算错。</summary>
    static void TouchImageCacheEntry(string path)
    {
        try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); } catch { }
    }

    /// <summary>
    /// 把图片缓存压回上限以内：总大小超过 <see cref="MaxImageCacheBytes"/> 时，按
    /// <c>LastWriteTimeUtc</c> 从旧到新删除，直到 ≤ 上限的 90%（留 10% 余量，避免「刚删完
    /// 又超限」的抖动 —— 每次只多写一张图时不该触发第二次成批删除）。顺手清掉写入中断
    /// 残留的 <c>*.tmp</c>（它们不计入统计，但不该永久占空间）。
    ///
    /// 所有异常都吞掉：缓存目录不可写 / 文件被杀软占用时，市场页必须照常可用，只是缓存
    /// 不收敛而已，不影响核心功能。internal：自测台要直接驱动清理。
    /// </summary>
    internal void TrimImageCache()
    {
        try
        {
            var dir = PluginPaths.ImagesDir;
            if (!Directory.Exists(dir)) return;

            foreach (var tmp in Directory.EnumerateFiles(dir, "*.tmp"))
            {
                try { File.Delete(tmp); } catch { }
            }

            var files = new DirectoryInfo(dir)
                .EnumerateFiles("*", SearchOption.TopDirectoryOnly)
                .Where(f => !f.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                .ToList();
            long total = files.Sum(f => f.Length);
            if (total <= MaxImageCacheBytes) return;

            var target = (long)(MaxImageCacheBytes * 0.9);
            foreach (var file in files.OrderBy(f => f.LastWriteTimeUtc))
            {
                if (total <= target) break;
                try
                {
                    var size = file.Length;
                    file.Delete();
                    total -= size;
                }
                catch
                {
                    // 文件正被别的进程占用：跳过，留到下次清理。总大小因此可能仍超限，
                    // 但下一次 GetImageAsync 写入后会再试一次。
                }
            }
        }
        catch
        {
            // 见上面的注释：缓存清理失败不能影响市场页。
        }
    }

    /// <summary>当前图片缓存现状（文件数 / 总字节）。设置页展示与自测台断言共用同一口径。</summary>
    public (int Files, long Bytes) GetImageCacheStats()
    {
        try
        {
            var dir = PluginPaths.ImagesDir;
            if (!Directory.Exists(dir)) return (0, 0);
            int count = 0;
            long bytes = 0;
            foreach (var file in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.TopDirectoryOnly))
            {
                if (file.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                count++;
                bytes += file.Length;
            }
            return (count, bytes);
        }
        catch
        {
            return (0, 0);
        }
    }

    public void ClearImageCache()
    {
        try
        {
            if (Directory.Exists(PluginPaths.ImagesDir))
                Directory.Delete(PluginPaths.ImagesDir, recursive: true);
        }
        catch { }
    }
}
