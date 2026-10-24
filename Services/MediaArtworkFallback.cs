using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using DesktopZones.Helpers;
using DesktopZones.Models;

namespace DesktopZones.Services;

/// <summary>
/// 给「没有系统媒体会话」的播放器补封面（目前实现的是网易云音乐）。
///
/// 为什么只能这么做：网易云不注册 SMTC，system media session 里根本拿不到缩略图，
/// 私有缓存目录里也没有可直接用的封面文件。但它把自己的**播放队列**缓存成了 JSON
/// （<c>%LOCALAPPDATA%\Netease\CloudMusic\webdata\file\playingList</c>），
/// 每条 track 都带 <c>album.picUrl</c> —— 于是：
///
/// 1. 按「歌名 + 歌手」在这份队列里找回当前曲目（歌名/歌手来自窗口标题，见 AudioNowPlayingProbe）；
/// 2. 拿到的封面链接加 <c>?param=400y400</c> 下载成正方形封面；
/// 3. 图片按 URL 哈希缓存到 <c>Data\cache\cover\</c>，同一首歌 / 重启后不再联网。
///
/// 只发一个匿名 GET（不带 cookie、不带账号信息），失败一律静默返回 null。
/// 关掉 <see cref="DesktopMediaPlayer.FetchCover"/> 就完全不读队列也不联网。
/// </summary>
public static class MediaArtworkFallback
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(8),
    };

    /// <summary>队列 JSON 解析结果缓存：文件没变就不重复解析（1.6MB，解析一次几毫秒）。</summary>
    private static readonly object _cacheLock = new();
    private static string _cacheStamp = "";
    private static QueueIndex? _index;
    /// <summary>失败过的封面链接 → 允许重试的起始时间（TickCount64）。
    /// 用超时而不是永久拉黑：网络抖动 / 断线重连后还能自己恢复。</summary>
    private static readonly Dictionary<string, long> _failedUntil = new(StringComparer.Ordinal);
    private const long RetryAfterMs = 10 * 60 * 1000;

    private sealed class QueueIndex
    {
        /// <summary>「歌名|歌手」→ 曲目（精确匹配用）。</summary>
        public readonly Dictionary<string, NetEaseTrack> Full = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>「歌名」→ 曲目（歌名唯一 / 歌手写法不同时的兜底）。</summary>
        public readonly Dictionary<string, NetEaseTrack> ByTitle = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>去掉空格 / 全角空格 / 括号差异后的歌名 → 曲目。</summary>
        public readonly Dictionary<string, NetEaseTrack> BySquashedTitle = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>歌曲 id → 曲目（用播放缓存的文件名反查当前曲目时用）。</summary>
        public readonly Dictionary<string, NetEaseTrack> ById = new(StringComparer.Ordinal);
    }

    /// <summary>队列里的一条曲目。</summary>
    public sealed class NetEaseTrack
    {
        public string Id = "";
        public string Title = "";
        public string Artist = "";
        public string PicUrl = "";
    }

    private static string CacheDir => Path.Combine(DataLocator.Root, "cache", "cover");

    /// <summary>按曲目信息找封面。找不到 / 下载失败返回 null（调用方保持占位盘面）。</summary>
    public static async Task<BitmapSource?> TryFetchAsync(
        string title, string artist, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        try
        {
            var index = LoadIndex();
            if (index == null) return null;

            var track = ResolveTrack(index, title, artist);
            if (track == null || track.PicUrl.Length == 0) return null;

            return await TryFetchAsync(track, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            DzTrace.Log($"[CoverFallback] 取封面失败: {ex.GetType().Name} {ex.Message}");
            return null;
        }
    }

    /// <summary>按队列里的曲目条目下载封面。</summary>
    public static async Task<BitmapSource?> TryFetchAsync(NetEaseTrack track, CancellationToken ct = default)
    {
        if (track == null || track.PicUrl.Length == 0) return null;
        try
        {
            var bytes = await LoadImageBytesAsync(track.PicUrl, ct).ConfigureAwait(false);
            return bytes == null ? null : Decode(bytes);
        }
        catch (Exception ex)
        {
            DzTrace.Log($"[CoverFallback] 取封面失败: {ex.GetType().Name} {ex.Message}");
            return null;
        }
    }

    // ── 当前曲目（不依赖窗口标题）──
    //
    // 网易云边播边把音频写进 Cache\<songId>-<bitrate>-<hash>.uc，正在播的那首会持续被写。
    // 于是「拿最新 .uc 的文件名里的 songId」= 当前曲目 id，再回队列 JSON 里查歌名/歌手/封面。
    // 这条路在窗口最小化到托盘、标题里没有歌名时仍然有效。

    /// <summary>网易云自己的数据根目录（<c>%LOCALAPPDATA%\Netease\CloudMusic</c>）。</summary>
    private static string NetEaseRoot => NetEaseRootOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Netease", "CloudMusic");

    /// <summary>测试钩子：把这套解析指到一份临时目录（同程序集内的自测harness用）。
    /// 产品代码永远不写它，保持 null。</summary>
    internal static string? NetEaseRootOverride;

    /// <summary>最近还在被写入的缓存文件里的歌曲 id（150 秒内改过才算数）。</summary>
    public static string? RecentSongIdFromCache()
    {
        try
        {
            var dir = Path.Combine(NetEaseRoot, "Cache");
            if (!Directory.Exists(dir)) return null;

            // 网易云的缓存在 Cache\Cache\ 这层子目录里（不同版本文案不同，有的还会分
            // 按位速再分子目录），所以必须递归找 —— 只扫一层会永远扫不到文件。
            var newest = (FileInfo?)null;
            foreach (var file in EnumerateFilesSafe(new DirectoryInfo(dir), "*.uc", depth: 0))
            {
                if (newest == null || file.LastWriteTimeUtc > newest.LastWriteTimeUtc) newest = file;
            }
            if (newest == null) return null;
            // 只认 150 秒内还在被写的文件：正在播的那首会边播边写，预读的下一首写完就停，
            // 窗口太宽会把"下一首"当成"当前曲目"。
            if (DateTime.UtcNow - newest.LastWriteTimeUtc > TimeSpan.FromSeconds(150)) return null;

            // 文件名形如 28152560-320-958240140d944ef031575939a2d76530.uc
            var name = Path.GetFileNameWithoutExtension(newest.Name);
            int dash = name.IndexOf('-');
            var id = dash > 0 ? name[..dash] : name;
            return id.Length > 0 && id.All(char.IsDigit) ? id : null;
        }
        catch (Exception ex)
        {
            DzTrace.Log($"[CoverFallback] 读播放缓存失败: {ex.GetType().Name} {ex.Message}");
            return null;
        }
    }

    /// <summary>容错递归枚举（网易云缓存目录里可能有权限受限 / 正在被写的子目录，
    /// 一个异常不该让整次扫描失败）。最多下探 <paramref name="maxDepth"/> 层。</summary>
    private static IEnumerable<FileInfo> EnumerateFilesSafe(
        DirectoryInfo dir, string pattern, int depth, int maxDepth = 2)
    {
        FileInfo[] files;
        try { files = dir.GetFiles(pattern); }
        catch { files = Array.Empty<FileInfo>(); }
        foreach (var f in files) yield return f;

        if (depth >= maxDepth) yield break;
        DirectoryInfo[] subdirs;
        try { subdirs = dir.GetDirectories(); }
        catch { subdirs = Array.Empty<DirectoryInfo>(); }
        foreach (var sub in subdirs)
            foreach (var f in EnumerateFilesSafe(sub, pattern, depth + 1, maxDepth))
                yield return f;
    }

    /// <summary>按"最近写入的缓存文件"反查当前曲目（标题里没歌名时用）。</summary>
    public static NetEaseTrack? FindRecentTrack()
    {
        var id = RecentSongIdFromCache();
        if (id == null) return null;
        var index = LoadIndex();
        if (index == null) return null;
        return index.ById.TryGetValue(id, out var track) ? track : null;
    }

    /// <summary>按歌名 / 歌手在队列里找曲目。</summary>
    public static NetEaseTrack? FindByTitle(string title, string artist)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var index = LoadIndex();
        return index == null ? null : ResolveTrack(index, title, artist);
    }

    /// <summary>这个进程 / 应用名像不像网易云（只有它走这套缓存解析）。</summary>
    public static bool LooksLikeNetEase(string processName, string appName)
    {
        if (processName.Contains("cloudmusic", StringComparison.OrdinalIgnoreCase)) return true;
        if (appName.Contains("NetEase", StringComparison.OrdinalIgnoreCase)) return true;
        if (appName.Contains("网易云", StringComparison.Ordinal)) return true;
        return false;
    }

    // ── 队列索引 ──

    private static IEnumerable<string> QueuePaths()
    {
        var dir = Path.Combine(NetEaseRoot, "webdata", "file");
        yield return Path.Combine(dir, "playingList");
        yield return Path.Combine(dir, "lastTimePlayingList");
    }

    private static QueueIndex? LoadIndex()
    {
        var files = new List<string>();
        foreach (var p in QueuePaths())
            if (File.Exists(p)) files.Add(p);
        if (files.Count == 0) return null;

        var stamp = string.Join("|", files.ConvertAll(f =>
        {
            var fi = new FileInfo(f);
            return $"{fi.FullName}:{fi.Length}:{fi.LastWriteTimeUtc.Ticks}";
        }));

        lock (_cacheLock)
        {
            if (stamp == _cacheStamp && _index != null) return _index;
            var index = new QueueIndex();
            foreach (var f in files) ParseInto(index, f);
            _index = index;
            _cacheStamp = stamp;
            return index;
        }
    }

    private static void ParseInto(QueueIndex index, string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            if (!doc.RootElement.TryGetProperty("list", out var list)
                || list.ValueKind != JsonValueKind.Array)
                return;

            foreach (var item in list.EnumerateArray())
            {
                if (!item.TryGetProperty("track", out var track) || track.ValueKind != JsonValueKind.Object)
                    continue;
                if (!track.TryGetProperty("album", out var album) || album.ValueKind != JsonValueKind.Object)
                    continue;
                if (!album.TryGetProperty("picUrl", out var pic) || pic.ValueKind != JsonValueKind.String)
                    continue;
                var url = pic.GetString();
                if (string.IsNullOrWhiteSpace(url)) continue;

                var title = track.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                    ? (n.GetString() ?? "").Trim()
                    : "";
                if (title.Length == 0) continue;

                var artists = new List<string>();
                if (track.TryGetProperty("artists", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in arr.EnumerateArray())
                    {
                        if (a.TryGetProperty("name", out var an) && an.ValueKind == JsonValueKind.String)
                        {
                            var name = (an.GetString() ?? "").Trim();
                            if (name.Length > 0) artists.Add(name);
                        }
                    }
                }

                var id = track.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                    ? (idEl.GetString() ?? "").Trim()
                    : "";

                var entry = new NetEaseTrack
                {
                    Id = id,
                    Title = title,
                    Artist = string.Join("/", artists),
                    PicUrl = url,
                };

                index.ByTitle.TryAdd(title, entry);
                index.BySquashedTitle.TryAdd(Squash(title), entry);
                if (id.Length > 0) index.ById.TryAdd(id, entry);
                foreach (var joined in new[] { string.Join("/", artists), string.Join("、", artists) })
                    index.Full.TryAdd(title + "|" + joined, entry);
                // 单歌手 / 歌手顺序不同也允许匹配
                foreach (var one in artists)
                    index.Full.TryAdd(title + "|" + one, entry);
            }
        }
        catch (Exception ex)
        {
            DzTrace.Log($"[CoverFallback] 解析播放队列失败({Path.GetFileName(path)}): {ex.GetType().Name} {ex.Message}");
        }
    }

    private static NetEaseTrack? ResolveTrack(QueueIndex index, string title, string artist)
    {
        var t = title.Trim();
        var a = artist.Trim();

        if (a.Length > 0)
        {
            if (index.Full.TryGetValue(t + "|" + a, out var exact)) return exact;
            foreach (var one in a.Split('/', '、', '&', ','))
            {
                var single = one.Trim();
                if (single.Length > 0 && index.Full.TryGetValue(t + "|" + single, out var byArtist))
                    return byArtist;
            }
        }
        if (index.ByTitle.TryGetValue(t, out var byTitle)) return byTitle;
        if (index.BySquashedTitle.TryGetValue(Squash(t), out var bySquash)) return bySquash;
        return null;
    }

    /// <summary>去掉空白、全角空格与常见括号/标点差异，给"歌名写法略有出入"兜底。</summary>
    private static string Squash(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (char.IsWhiteSpace(ch) || ch == '\u3000') continue;
            if (ch is '(' or ')' or '（' or '）' or '[' or ']' or '【' or '】') continue;
            sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    // ── 下载 + 缓存 ──

    private static async Task<byte[]?> LoadImageBytesAsync(string url, CancellationToken ct)
    {
        // 网易云给的是 http://p?.music.126.net/...，统一走 https，并按 400×400 取正方形封面
        var normalized = url.Replace("http://", "https://", StringComparison.OrdinalIgnoreCase);
        if (!normalized.Contains("param=", StringComparison.Ordinal))
            normalized += normalized.Contains('?') ? "&param=400y400" : "?param=400y400";

        var path = Path.Combine(CacheDir, Sha1(normalized) + ".img");
        try
        {
            if (File.Exists(path))
            {
                var cached = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
                if (cached.Length > 1024) return cached;
            }
        }
        catch { /* 缓存读不动就当没有 */ }

        if (IsRecentlyFailed(normalized)) return null;

        // 先走 https；有些网络 / 代理环境里该 CDN 的 https 会被掐断（本机实测就遇到过），
        // 这时退回它原本给的 http 链接再试一次 —— 只取一张公开封面图，不带任何凭据。
        var secure = normalized;
        var insecure = normalized.Replace("https://", "http://", StringComparison.OrdinalIgnoreCase);

        var bytes = await DownloadAsync(secure, path, ct).ConfigureAwait(false);
        if (bytes == null && insecure != secure)
        {
            DzTrace.Log("[CoverFallback] https 取封面失败，改用 http 重试");
            bytes = await DownloadAsync(insecure, path, ct).ConfigureAwait(false);
        }
        if (bytes == null) Remember(normalized);
        return bytes;
    }

    /// <summary>下载一次（网络抖动重试一次）并写缓存。失败返回 null，由调用方决定要不要换协议。</summary>
    private static async Task<byte[]?> DownloadAsync(string url, string cachePath, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseContentRead, ct)
                    .ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return null;

                var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                if (bytes.Length < 1024) return null;

                try
                {
                    Directory.CreateDirectory(CacheDir);
                    await File.WriteAllBytesAsync(cachePath, bytes, ct).ConfigureAwait(false);
                }
                catch { /* 缓存写不了不影响这次显示 */ }
                return bytes;
            }
            catch (Exception ex)
            {
                bool retryable = ex is HttpRequestException or TaskCanceledException;
                if (!retryable || attempt == 1)
                {
                    DzTrace.Log($"[CoverFallback] 下载封面失败({url[..Math.Min(8, url.Length)]}…): {ex.GetType().Name} {ex.Message}");
                    return null;
                }
                await Task.Delay(400, CancellationToken.None).ConfigureAwait(false);
            }
        }
        return null;
    }

    private static bool IsRecentlyFailed(string url)
    {
        lock (_cacheLock)
        {
            return _failedUntil.TryGetValue(url, out var until) && Environment.TickCount64 < until;
        }
    }

    private static void Remember(string url)
    {
        lock (_cacheLock)
        {
            if (_failedUntil.Count > 512) _failedUntil.Clear();
            _failedUntil[url] = Environment.TickCount64 + RetryAfterMs;
        }
    }

    private static BitmapSource? Decode(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 512;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex)
        {
            DzTrace.Log($"[CoverFallback] 封面解码失败: {ex.GetType().Name} {ex.Message}");
            return null;
        }
    }

    private static string Sha1(string text)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
