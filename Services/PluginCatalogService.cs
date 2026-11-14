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

    static readonly TimeSpan CacheTtl = TimeSpan.FromHours(6);
    const long MaxRegistryBytes = 2L * 1024 * 1024;
    const long MaxImageBytes = 4L * 1024 * 1024;

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

    PluginCatalogResult ParseRegistry(byte[] bytes)
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
            // registry 是外部数据：单条畸形不能让整个市场变空/渲染崩。
            if (entry == null) continue;
            try
            {
                entry.Id = entry.Id?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(entry.Id) || !seen.Add(entry.Id)) continue;
                entry.Name ??= new LocalizedText();
                entry.Summary ??= new LocalizedText();
                entry.Author ??= new PluginAuthor();
                entry.Version ??= "";
                entry.License ??= "";
                entry.MinAppVersion ??= "";
                if (string.IsNullOrWhiteSpace(entry.Name.Get("zh")) && string.IsNullOrWhiteSpace(entry.Name.Get("en")))
                    continue;
                if (string.IsNullOrWhiteSpace(entry.Version)) continue;
                if (string.IsNullOrWhiteSpace(entry.DownloadUrl)) continue;
                if (!entry.DownloadUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) continue;
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
        if (File.Exists(path) && new FileInfo(path).Length > 0) return path;

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
            return path;
        }
        catch
        {
            return null;
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
