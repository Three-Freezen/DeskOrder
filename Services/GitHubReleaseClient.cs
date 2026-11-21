using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using DesktopZones.Helpers;

namespace DesktopZones.Services;

/// <summary>GitHub Release 里的一个资产（只保留安装需要知道的字段）。</summary>
public sealed class GitHubReleaseAsset
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("browser_download_url")] public string BrowserDownloadUrl { get; set; } = "";
}

/// <summary>GitHub Release 的摘要。<c>releases/latest</c> 与 <c>releases</c> 列表共用。</summary>
public sealed class GitHubReleaseInfo
{
    [JsonPropertyName("tag_name")] public string TagName { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("draft")] public bool Draft { get; set; }
    [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
    [JsonPropertyName("published_at")] public DateTime? PublishedAt { get; set; }
    [JsonPropertyName("assets")] public List<GitHubReleaseAsset> Assets { get; set; } = new();

    /// <summary>展示用的版本标签；<c>tag_name</c> 为空时退回 <c>name</c>。</summary>
    public string DisplayTag => string.IsNullOrWhiteSpace(TagName) ? Name : TagName;
}

/// <summary>
/// Release 资产挑选结果。<see cref="Package"/> 为 null 时 <see cref="Error"/> 一定是可读文案。
/// .minisig 资产只在文件名与包完全对应时才会被配对（不猜「哪个签名配哪个包」）。
/// </summary>
public sealed class GitHubAssetSelection
{
    public GitHubReleaseAsset? Package { get; init; }
    public GitHubReleaseAsset? Signature { get; init; }
    public string? Error { get; init; }

    public bool HasPackage => Package != null;
    public bool HasSignature => Signature != null;
}

/// <summary>一次 GitHub Release 查找的结果：Release 元数据 + 选中的资产 / 可读错误。</summary>
public sealed class GitHubReleaseLookup
{
    public GitHubReleaseInfo? Release { get; init; }
    public GitHubAssetSelection? Assets { get; init; }
    public string? Error { get; init; }
    /// <summary>用于展示的 Release 标签（找到 Release 时非空）。</summary>
    public string Tag => Release?.DisplayTag ?? "";

    public bool Success => Error == null && Assets?.Package != null;
}

/// <summary>资产下载结果。<c>Success=false</c> 时 <see cref="Error"/> 是可读文案。</summary>
public sealed class GitHubDownloadResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public long Bytes { get; init; }
    public string? FinalUri { get; init; }
}

/// <summary>
/// GitHub Release 查询与资产下载（「从 GitHub 仓库直接安装」的数据层）。
///
/// 职责边界：这里只负责「按 owner/repo[/tag] 找到 Release、挑出 .dopkg、把字节拿到本地」，
/// 解压 / manifest 校验 / 签名 / 落盘全部交给 <see cref="PluginInstaller"/> 的现有管线——
/// 直接安装的口子不能绕过市场安装已经有的那些检查。
///
/// 安全约束：
/// <list type="bullet">
/// <item>只请求 https://api.github.com；</item>
/// <item>包/签名只从 github.com、objects.githubusercontent.com、api.github.com 下载，
/// 每一跳重定向都重新校验（手动跟，不用 HttpClient 的自动重定向）；</item>
/// <item>API 响应最多读 2 MB，包最多 <see cref="PluginPackages.MaxPackageBytes"/>（边下边数）；</item>
/// <item>401/403 读出 X-RateLimit-Reset 换算成「X 分钟后再试」，而不是把原始状态码丢给用户。</item>
/// </list>
/// </summary>
public sealed class GitHubReleaseClient : IDisposable
{
    public const string DefaultApiBaseUrl = "https://api.github.com";
    public const string UserAgentText = "DeskOrder-PluginMarket/1.0";
    public const string AcceptText = "application/vnd.github+json";

    /// <summary>允许下载包/签名的域名（重定向后的最终地址也必须在里面）。</summary>
    internal static readonly string[] AllowedDownloadHosts =
    {
        "github.com", "objects.githubusercontent.com", "api.github.com"
    };

    const long MaxApiResponseBytes = 2L * 1024 * 1024;
    const int MaxRedirects = 5;

    readonly HttpClient _http;
    readonly bool _ownsHttp;

    /// <summary>API 基址。默认 https://api.github.com；自测台会指向本地假服务。</summary>
    public string ApiBaseUrl { get; set; } = DefaultApiBaseUrl;

#if DEBUG
    /// <summary>自测台专用：允许 http + 127.0.0.1 的假 GitHub 服务（Release 之外的真实路径永不打开）。</summary>
    internal bool AllowInsecureLoopbackForLab { get; set; }
#endif

    public GitHubReleaseClient() : this(null)
    {
    }

    public GitHubReleaseClient(HttpClient? http)
    {
        _ownsHttp = http == null;
        _http = http ?? CreateHttpClient();
    }

    static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            // 重定向必须由我们手动跟：默认自动跟会先连上陌生域名，再谈「最终地址校验」就晚了。
            AllowAutoRedirect = false
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgentText);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", AcceptText);
        return client;
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }

    // ── 纯函数：输入解析 ──

    /// <summary>
    /// 把用户粘贴的内容解析成 owner / repo / 可选的 tag。接受：
    /// <c>owner/repo</c>、<c>https://github.com/owner/repo</c>（可带结尾 / 或 .git）、
    /// <c>https://github.com/owner/repo/releases/tag/v1.2.3</c>。
    /// 拒绝：非 github.com 域名、http、路径过深/不认识的页面、非法字符、空输入。
    /// </summary>
    internal static bool TryParseRepo(string? input, out string owner, out string repo, out string? tag)
    {
        owner = "";
        repo = "";
        tag = null;
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > 300) return false;
        if (text.Any(ch => ch is ' ' or '\t' or '\r' or '\n')) return false;

        if (text.Contains("://", StringComparison.Ordinal))
        {
            if (!text.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return false;
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return false;
            if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return false;
            if (!uri.IsDefaultPort || uri.UserInfo.Length != 0) return false;
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2) return false;
            owner = segments[0];
            repo = StripGitSuffix(segments[1]);
            if (!IsValidOwner(owner) || !IsValidRepo(repo)) return false;
            if (segments.Length == 2) return true;
            if (segments.Length >= 5
                && segments[2].Equals("releases", StringComparison.OrdinalIgnoreCase)
                && segments[3].Equals("tag", StringComparison.OrdinalIgnoreCase))
            {
                var rawTag = Uri.UnescapeDataString(string.Join('/', segments.Skip(4)));
                if (!IsValidTag(rawTag)) return false;
                tag = rawTag;
                return true;
            }
            // /tree/main、/issues 之类的页面不是「仓库根」：路径过深一律拒绝，避免猜错。
            return false;
        }

        var parts = text.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return false;
        owner = parts[0];
        repo = StripGitSuffix(parts[1]);
        return IsValidOwner(owner) && IsValidRepo(repo);
    }

    static string StripGitSuffix(string repo)
        => repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? repo[..^4] : repo;

    internal static bool IsValidOwner(string? owner)
    {
        if (string.IsNullOrEmpty(owner) || owner.Length > 39) return false;
        if (!char.IsAsciiLetterOrDigit(owner[0]) || !char.IsAsciiLetterOrDigit(owner[^1])) return false;
        return owner.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-');
    }

    internal static bool IsValidRepo(string? repo)
    {
        if (string.IsNullOrEmpty(repo) || repo.Length > 100) return false;
        if (repo is "." or "..") return false;
        return repo.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-');
    }

    internal static bool IsValidTag(string? tag)
    {
        if (string.IsNullOrEmpty(tag) || tag.Length > 100) return false;
        if (tag.Contains("..", StringComparison.Ordinal)) return false;
        if (tag[0] == '/' || tag[^1] == '/') return false;
        // GitHub tag 还允许 '+'（如 v1.0.0+build），其余字符按 URL 安全集合收紧。
        return tag.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-' or '/' or '+');
    }

    // ── 纯函数：Release 路径 ──

    /// <summary>Release API 路径：有 tag 走 <c>/releases/tags/{tag}</c>，否则 <c>/releases/latest</c>。</summary>
    internal static string ReleaseApiPath(string owner, string repo, string? tag)
        => string.IsNullOrWhiteSpace(tag)
            ? $"/repos/{owner}/{repo}/releases/latest"
            : $"/repos/{owner}/{repo}/releases/tags/{EscapePathSegment(tag)}";

    // ── 纯函数：Release 资产选择 ──

    /// <summary>
    /// 只认 <c>.dopkg</c>（大小写不敏感，<c>.minisig</c> 天然不算包）。
    /// 0 个 → 「该 Release 没有 .dopkg 资产」；多个 → 无法自动判断并列名字；
    /// 恰好一个时配对同名 <c>.dopkg.minisig</c>（有就记下地址，下载后必须验过）。
    /// </summary>
    internal static GitHubAssetSelection PickPackageAsset(IReadOnlyList<GitHubReleaseAsset>? assets)
    {
        var list = (assets ?? Array.Empty<GitHubReleaseAsset>())
            .Where(asset => asset != null && !string.IsNullOrWhiteSpace(asset.Name))
            .ToList();

        var packages = list
            .Where(asset => asset.Name.Trim().EndsWith(".dopkg", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (packages.Count == 0)
            return new GitHubAssetSelection { Error = Loc["Plugin.GitHub.Error.NoAsset"] };
        if (packages.Count > 1)
        {
            var names = string.Join(", ", packages.Select(asset => asset.Name.Trim()));
            return new GitHubAssetSelection
            {
                Error = Loc.Get("Plugin.GitHub.Error.MultipleAssets", names)
            };
        }

        var package = packages[0];
        var signatureName = package.Name.Trim() + ".minisig";
        var signature = list.FirstOrDefault(asset =>
            asset.Name.Trim().Equals(signatureName, StringComparison.OrdinalIgnoreCase));
        return new GitHubAssetSelection { Package = package, Signature = signature };
    }

    // ── 纯函数：HTTP 错误映射 ──

    /// <summary>
    /// 状态码 → 可读文案。403/401 若带 <c>X-RateLimit-Reset</c>（Unix 秒），换算成还有多少分钟。
    /// 纯函数（时间由调用方传入）方便自测台钉死断言。
    /// </summary>
    internal static string DescribeApiError(HttpStatusCode status, string? rateLimitReset, DateTime nowUtc)
    {
        var code = (int)status;
        if (code is 401 or 403)
        {
            var minutes = RateLimitResetMinutes(rateLimitReset, nowUtc);
            return minutes is { } value
                ? Loc.Get("Plugin.GitHub.Error.RateLimit", Math.Max(1, value))
                : Loc["Plugin.GitHub.Error.Forbidden"];
        }
        if (code == 404) return Loc["Plugin.GitHub.Error.NotFound"];
        return Loc["Plugin.GitHub.Error.Network"];
    }

    /// <summary>X-RateLimit-Reset（Unix 秒）→ 剩余分钟数（向上取整，最小 0）；解析不了返回 null。</summary>
    internal static int? RateLimitResetMinutes(string? resetHeader, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(resetHeader)) return null;
        if (!long.TryParse(resetHeader.Trim(), out var seconds)) return null;
        try
        {
            var reset = DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
            var remaining = reset - nowUtc;
            if (remaining <= TimeSpan.Zero) return 0;
            return (int)Math.Ceiling(remaining.TotalMinutes);
        }
        catch
        {
            return null;
        }
    }

    // ── 纯函数：下载地址白名单 ──

    /// <summary>
    /// 只允许 https + 三个 GitHub 域名。重定向后的每一跳都用同一个函数判。
    /// <paramref name="allowInsecureLoopback"/> 只给自测台的 127.0.0.1 假服务用（Debug 才可能为 true）。
    /// </summary>
    internal static bool IsAllowedDownloadUri(Uri? uri, bool allowInsecureLoopback = false)
    {
        if (uri == null) return false;
        if (uri.UserInfo.Length != 0) return false;
        if (uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return AllowedDownloadHosts.Any(host => uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase));
#if DEBUG
        if (allowInsecureLoopback
            && uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && uri.IsLoopback)
            return true;
#endif
        return false;
    }

    // ── 网络：查找 Release ──

    /// <summary>
    /// 找到 <paramref name="owner"/>/<paramref name="repo"/> 的 Release 并挑好 .dopkg。
    /// 有 tag 走 <c>/releases/tags/{tag}</c>；没有走 <c>/releases/latest</c>，404 时回退
    /// <c>/releases?per_page=5</c> 取第一条非 draft。所有失败都返回可读文案，不抛异常（取消除外）。
    /// </summary>
    public async Task<GitHubReleaseLookup> ResolvePackageAsync(string owner, string repo, string? tag,
        CancellationToken ct = default)
    {
        if (!IsValidOwner(owner) || !IsValidRepo(repo))
            return new GitHubReleaseLookup { Error = Loc["Plugin.GitHub.Error.InvalidInput"] };

        GitHubReleaseInfo? release = null;
        string? error = null;

        if (!string.IsNullOrWhiteSpace(tag))
        {
            if (!IsValidTag(tag))
                return new GitHubReleaseLookup { Error = Loc["Plugin.GitHub.Error.InvalidInput"] };
            var result = await GetJsonAsync<GitHubReleaseInfo>(
                ReleaseApiPath(owner, repo, tag), ct).ConfigureAwait(false);
            release = result.Value;
            error = result.Error;
        }
        else
        {
            var latest = await GetJsonAsync<GitHubReleaseInfo>(
                ReleaseApiPath(owner, repo, null), ct).ConfigureAwait(false);
            release = latest.Value;
            error = latest.Error;
            if (release == null && latest.Status == HttpStatusCode.NotFound)
            {
                // 仓库只发预发布 / latest 端点缺失时，退而求其次按发布时间取第一条非 draft。
                var list = await GetJsonAsync<List<GitHubReleaseInfo>>(
                    $"/repos/{owner}/{repo}/releases?per_page=5", ct).ConfigureAwait(false);
                if (list.Value != null)
                {
                    release = list.Value.FirstOrDefault(item => item != null && !item.Draft);
                    error = release == null ? Loc["Plugin.GitHub.Error.NotFound"] : null;
                }
                else
                {
                    error = list.Error ?? Loc["Plugin.GitHub.Error.NotFound"];
                }
            }
        }

        if (release == null || string.IsNullOrWhiteSpace(release.DisplayTag))
            return new GitHubReleaseLookup { Error = error ?? Loc["Plugin.GitHub.Error.NotFound"] };

        var selection = PickPackageAsset(release.Assets);
        return new GitHubReleaseLookup { Release = release, Assets = selection, Error = selection.Error };
    }

    sealed class ApiResult<T>
    {
        public T? Value { get; init; }
        public HttpStatusCode? Status { get; init; }
        public string? Error { get; init; }
    }

    async Task<ApiResult<T>> GetJsonAsync<T>(string path, CancellationToken ct) where T : class
    {
        var baseUri = new Uri(ApiBaseUrl.TrimEnd('/') + path, UriKind.Absolute);
        using var request = new HttpRequestMessage(HttpMethod.Get, baseUri);
        request.Headers.TryAddWithoutValidation("Accept", AcceptText);

        var (response, sendError) = await SendFollowRedirectsAsync(
            request, uri => IsAllowedApiUri(uri), ct).ConfigureAwait(false);
        if (response == null) return new ApiResult<T> { Error = sendError };

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var reset = ReadRateLimitReset(response);
                return new ApiResult<T>
                {
                    Status = response.StatusCode,
                    Error = DescribeApiError(response.StatusCode, reset, DateTime.UtcNow)
                };
            }

            byte[]? bytes;
            try
            {
                bytes = await ReadCappedAsync(response.Content, MaxApiResponseBytes, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                return new ApiResult<T> { Status = response.StatusCode, Error = Loc["Plugin.GitHub.Error.Network"] };
            }
            if (bytes == null)
                return new ApiResult<T> { Status = response.StatusCode, Error = Loc["Plugin.GitHub.Error.Network"] };

            try
            {
                var value = JsonSerializer.Deserialize<T>(bytes, JsonOptions);
                if (value == null)
                    return new ApiResult<T> { Status = response.StatusCode, Error = Loc["Plugin.GitHub.Error.Network"] };
                return new ApiResult<T> { Status = response.StatusCode, Value = value };
            }
            catch (JsonException)
            {
                return new ApiResult<T> { Status = response.StatusCode, Error = Loc["Plugin.GitHub.Error.Network"] };
            }
        }
    }

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    static string? ReadRateLimitReset(HttpResponseMessage response)
    {
        try
        {
            if (response.Headers.TryGetValues("X-RateLimit-Reset", out var values))
                return values.FirstOrDefault();
        }
        catch
        {
            // 头读不出来就按「没有限流时间」处理，退回泛化文案。
        }
        return null;
    }

    bool IsAllowedApiUri(Uri uri)
    {
        if (!uri.Host.Equals(new Uri(ApiBaseUrl).Host, StringComparison.OrdinalIgnoreCase)) return false;
        if (uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return true;
#if DEBUG
        return AllowInsecureLoopbackForLab
               && uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
               && uri.IsLoopback;
#else
        return false;
#endif
    }

    // ── 网络：下载资产 ──

    /// <summary>
    /// 下载一个 Release 资产到 <paramref name="destinationPath"/>。
    /// 大小上限 <paramref name="maxBytes"/>（Content-Length 与流式累计都检查）；
    /// 重定向每一跳都校验域名；失败返回可读文案，不抛异常（取消除外）。
    /// </summary>
    public async Task<GitHubDownloadResult> DownloadAsync(GitHubReleaseAsset asset, string destinationPath,
        long maxBytes, CancellationToken ct = default)
    {
        if (asset == null) return Fail(Loc["Plugin.GitHub.Error.NoAsset"]);
        if (asset.Size > maxBytes) return Fail(Loc["Plugin.GitHub.Error.TooLarge"]);
        if (!Uri.TryCreate(asset.BrowserDownloadUrl?.Trim(), UriKind.Absolute, out var uri))
            return Fail(Loc.Get("Plugin.GitHub.Error.BadHost", asset.BrowserDownloadUrl ?? ""));
        if (!IsAllowedDownloadUri(uri, AllowInsecureLoopback)) return Fail(Loc.Get("Plugin.GitHub.Error.BadHost", uri.Host));

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("Accept", "application/octet-stream");
        var (response, sendError) = await SendFollowRedirectsAsync(
            request, candidate => IsAllowedDownloadUri(candidate, AllowInsecureLoopback), ct).ConfigureAwait(false);
        if (response == null) return Fail(sendError ?? Loc["Plugin.GitHub.Error.Network"]);

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                return Fail(Loc.Get("Plugin.GitHub.Error.Download", (int)response.StatusCode));

            var declared = response.Content.Headers.ContentLength;
            if (declared > maxBytes) return Fail(Loc["Plugin.GitHub.Error.TooLarge"]);

            var success = false;
            try
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(destinationPath));
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var output = File.Create(destinationPath);
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > maxBytes) return Fail(Loc["Plugin.GitHub.Error.TooLarge"]);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }

                if (total <= 0) return Fail(Loc["Plugin.GitHub.Error.Network"]);
                success = true;
                return new GitHubDownloadResult
                {
                    Success = true,
                    Bytes = total,
                    FinalUri = response.RequestMessage?.RequestUri?.ToString()
                };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // IO / 连接中断：对用户就是「下载失败」，具体异常已经写不进界面，给可读的网络文案。
                return Fail(Loc["Plugin.GitHub.Error.Network"]);
            }
            finally
            {
                if (!success) TryDelete(destinationPath);
            }
        }
    }

    bool AllowInsecureLoopback
    {
        get
        {
#if DEBUG
            return AllowInsecureLoopbackForLab;
#else
            return false;
#endif
        }
    }

    static GitHubDownloadResult Fail(string error) => new() { Success = false, Error = error };

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    // ── HTTP 基础设施 ──

    /// <summary>手动跟重定向：每一跳都先过 <paramref name="isAllowed"/> 再发请求。</summary>
    async Task<(HttpResponseMessage? Response, string? Error)> SendFollowRedirectsAsync(
        HttpRequestMessage request, Func<Uri, bool> isAllowed, CancellationToken ct)
    {
        var current = request;
        try
        {
            for (var hop = 0; hop <= MaxRedirects; hop++)
            {
                HttpResponseMessage response;
                try
                {
                    response = await _http.SendAsync(current, HttpCompletionOption.ResponseHeadersRead, ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // HttpClient.Timeout 触发的取消：对用户是「网络超时」，不是用户取消。
                    return (null, Loc["Plugin.GitHub.Error.Network"]);
                }
                catch (HttpRequestException)
                {
                    return (null, Loc["Plugin.GitHub.Error.Network"]);
                }

                if (!IsRedirect(response.StatusCode)) return (response, null);

                var location = response.Headers.Location;
                if (location == null) return (response, null);
                Uri next;
                try
                {
                    next = location.IsAbsoluteUri ? location : new Uri(current.RequestUri!, location);
                }
                catch
                {
                    response.Dispose();
                    return (null, Loc["Plugin.GitHub.Error.Network"]);
                }
                response.Dispose();
                if (!isAllowed(next))
                    return (null, Loc.Get("Plugin.GitHub.Error.BadHost", next.Host));
                if (hop == MaxRedirects)
                    return (null, Loc["Plugin.GitHub.Error.Network"]);

                if (!ReferenceEquals(current, request)) current.Dispose();
                current = new HttpRequestMessage(HttpMethod.Get, next);
                foreach (var header in request.Headers)
                    current.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            return (null, Loc["Plugin.GitHub.Error.Network"]);
        }
        finally
        {
            // 最后一跳（如果已经换过）由这里释放；第一跳的 request 由调用方的 using 释放。
            if (!ReferenceEquals(current, request)) current.Dispose();
        }
    }

    static bool IsRedirect(HttpStatusCode status)
        => status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    static async Task<byte[]?> ReadCappedAsync(HttpContent content, long maxBytes, CancellationToken ct)
    {
        if (content.Headers.ContentLength > maxBytes) return null;
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16384];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > maxBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    /// <summary>tag 里允许 <c>/</c>：按段编码，保留斜杠（GitHub 的 tag 路径就是这样）。</summary>
    static string EscapePathSegment(string tag)
        => string.Join('/', tag.Split('/').Select(Uri.EscapeDataString));

    // ── 纯函数：官方仓库判定 ──

    /// <summary>
    /// 官方仓库 = owner 恰好是 Three-Freezen。GitHub 直接安装没有 registry 可以声明 official，
    /// 只能按仓库归属判；判成官方后安装器会强制要求官方 minisign 验签通过。
    /// </summary>
    internal static bool IsOfficialOwner(string? owner)
        => string.Equals(owner?.Trim(), "Three-Freezen", StringComparison.OrdinalIgnoreCase);

    static LocalizationService Loc => LocalizationService.Instance;
}
