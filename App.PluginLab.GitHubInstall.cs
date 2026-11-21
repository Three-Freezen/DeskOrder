#if DEBUG
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using DesktopZones.Helpers;
using DesktopZones.Models;
using DesktopZones.Services;
using DesktopZones.Views;

namespace DesktopZones;

/// <summary>
/// 「从 GitHub 仓库直接安装」的自测用例（v1.2.2）。
///
/// 覆盖三层：
/// <list type="number">
/// <item>纯函数：TryParseRepo 真值表、Release 资产选择、错误映射、下载域名白名单；</item>
/// <item>网络失败路径：API 指向不可达端口，断言返回可读错误而不是异常；</item>
/// <item>全链路：用 <see cref="FakeGitHubServer"/>（TcpListener 手工回 HTTP）扮演 GitHub API +
/// 资产 CDN，走真实的 <c>GitHubReleaseClient</c> / <c>PluginInstaller</c> 代码路径：
/// 查找 → 选资产 → 下载 → SHA-256 → 校验/落盘 → Source=github 记录。</item>
/// </list>
/// 不依赖真实 GitHub 网络：所有请求都打到 127.0.0.1 的假服务（不需要 UrlAcl）。
/// </summary>
public partial class App
{
    const string GitHubLabPackId = "lab.lang-gh";
    const string GitHubLabOwner = "lab";
    const string GitHubLabRepo = "deskorder-plugin-gh";

    async Task RunGitHubInstallLabCaseAsync(LabCase item, string dir)
    {
        var service = PluginService.Instance;
        var installer = service.Installer!;
        var loc = LocalizationService.Instance;
        FakeGitHubServer? server = null;
        try
        {
            // ── 1/5. TryParseRepo 真值表：合法 5 种 / 非法 7 种（含 tag 提取）──
            item.Check(GitHubReleaseClient.TryParseRepo(
                    "Three-Freezen/DeskOrder-Plugin-Hello",
                    out var owner, out var repo, out var tag)
                       && owner == "Three-Freezen" && repo == "DeskOrder-Plugin-Hello" && tag == null,
                "plain owner/repo parses");
            item.Check(GitHubReleaseClient.TryParseRepo(
                    "https://github.com/Three-Freezen/DeskOrder-Plugin-Hello",
                    out owner, out repo, out tag)
                       && owner == "Three-Freezen" && repo == "DeskOrder-Plugin-Hello" && tag == null,
                "repository URL parses");
            item.Check(GitHubReleaseClient.TryParseRepo(
                    "https://github.com/Three-Freezen/DeskOrder-Plugin-Hello/",
                    out owner, out repo, out tag)
                       && repo == "DeskOrder-Plugin-Hello" && tag == null,
                "a trailing slash is accepted");
            item.Check(GitHubReleaseClient.TryParseRepo(
                    "https://github.com/Three-Freezen/DeskOrder-Plugin-Hello.git",
                    out owner, out repo, out tag)
                       && repo == "DeskOrder-Plugin-Hello",
                "the .git suffix is stripped from the repository name");
            item.Check(GitHubReleaseClient.TryParseRepo(
                    "https://github.com/Three-Freezen/DeskOrder-Plugin-Hello/releases/tag/v1.2.3",
                    out owner, out repo, out tag)
                       && tag == "v1.2.3" && repo == "DeskOrder-Plugin-Hello",
                "a release tag URL extracts the tag");
            item.Check(!GitHubReleaseClient.TryParseRepo("https://gitlab.com/a/b", out _, out _, out _),
                "a non-GitHub host is rejected");
            item.Check(!GitHubReleaseClient.TryParseRepo("https://github.com/only-owner", out _, out _, out _),
                "a too-short GitHub path is rejected");
            item.Check(!GitHubReleaseClient.TryParseRepo("https://github.com/a/b/tree/main", out _, out _, out _),
                "an unsupported deeper path is rejected");
            item.Check(!GitHubReleaseClient.TryParseRepo("http://github.com/a/b", out _, out _, out _),
                "plain http is rejected");
            item.Check(!GitHubReleaseClient.TryParseRepo("a/b/c", out _, out _, out _),
                "a three-segment bare path is rejected");
            item.Check(!GitHubReleaseClient.TryParseRepo("a b/c", out _, out _, out _),
                "whitespace inside the input is rejected");
            item.Check(!GitHubReleaseClient.TryParseRepo("", out _, out _, out _),
                "empty input is rejected");

            // ── 2/5. 资产选择纯函数 ──
            var noAsset = GitHubReleaseClient.PickPackageAsset(new List<GitHubReleaseAsset>
            {
                new() { Name = "source.zip" }, new() { Name = "notes.txt" }
            });
            item.Check(noAsset.Package == null && noAsset.Error == loc["Plugin.GitHub.Error.NoAsset"],
                "a release without .dopkg reports the readable 'no asset' error");
            var onlySignature = GitHubReleaseClient.PickPackageAsset(new List<GitHubReleaseAsset>
            {
                new() { Name = "plugin-1.0.0.dopkg.minisig" }
            });
            item.Check(onlySignature.Package == null && onlySignature.Error == loc["Plugin.GitHub.Error.NoAsset"],
                "a .minisig alone is not mistaken for a package");
            var multiple = GitHubReleaseClient.PickPackageAsset(new List<GitHubReleaseAsset>
            {
                new() { Name = "a.dopkg" }, new() { Name = "b.DOPKG" }
            });
            item.Check(multiple.Package == null
                       && multiple.Error != null
                       && multiple.Error.Contains("a.dopkg") && multiple.Error.Contains("b.DOPKG"),
                "multiple .dopkg assets are rejected and both names are listed");
            var picked = GitHubReleaseClient.PickPackageAsset(new List<GitHubReleaseAsset>
            {
                new() { Name = "notes.txt" },
                new() { Name = "Plugin-1.2.3.DOPKG", Size = 123 },
                new() { Name = "Plugin-1.2.3.DOPKG.minisig", Size = 45 },
                new() { Name = "other-package.dopkg.minisig", Size = 45 }
            });
            item.Check(picked.Package?.Name == "Plugin-1.2.3.DOPKG"
                       && picked.Signature?.Name == "Plugin-1.2.3.DOPKG.minisig",
                "the single package is picked case-insensitively and its .minisig is paired");
            item.Check(picked.HasSignature, "the paired signature asset is exposed to the installer");

            // ── 3/5. 错误映射 / 限流倒计时 / 下载域名白名单 ──
            var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var resetHeader = new DateTimeOffset(now.AddMinutes(9)).ToUnixTimeSeconds().ToString();
            item.Check(GitHubReleaseClient.RateLimitResetMinutes(resetHeader, now) == 9,
                "X-RateLimit-Reset epoch is converted to remaining minutes");
            item.Check(GitHubReleaseClient.RateLimitResetMinutes("not-a-number", now) == null,
                "an unparseable reset header yields null instead of an exception");
            item.Check(GitHubReleaseClient.DescribeApiError(HttpStatusCode.Forbidden, resetHeader, now)
                       == loc.Get("Plugin.GitHub.Error.RateLimit", 9),
                "403 with a reset header maps to the localized rate-limit message");
            item.Check(GitHubReleaseClient.DescribeApiError(HttpStatusCode.Unauthorized, null, now)
                       == loc["Plugin.GitHub.Error.Forbidden"],
                "401 without a reset time falls back to the forbidden message");
            item.Check(GitHubReleaseClient.DescribeApiError(HttpStatusCode.NotFound, null, now)
                       == loc["Plugin.GitHub.Error.NotFound"],
                "404 maps to 'repository or release not found'");
            item.Check(GitHubReleaseClient.DescribeApiError(HttpStatusCode.InternalServerError, null, now)
                       == loc["Plugin.GitHub.Error.Network"],
                "5xx maps to the network message");
            item.Check(GitHubReleaseClient.IsAllowedDownloadUri(
                    new Uri("https://github.com/a/b/releases/download/v1/x.dopkg")),
                "an https github.com download URL is allowed");
            item.Check(GitHubReleaseClient.IsAllowedDownloadUri(
                    new Uri("https://objects.githubusercontent.com/x.dopkg")),
                "objects.githubusercontent.com is allowed");
            item.Check(GitHubReleaseClient.IsAllowedDownloadUri(
                    new Uri("https://api.github.com/repos/a/b/releases/assets/1")),
                "api.github.com is allowed");
            item.Check(!GitHubReleaseClient.IsAllowedDownloadUri(new Uri("https://evil.example/x.dopkg")),
                "an unknown download host is rejected");
            item.Check(!GitHubReleaseClient.IsAllowedDownloadUri(new Uri("http://github.com/x.dopkg")),
                "a plain http download URL is rejected");
            item.Check(!GitHubReleaseClient.IsAllowedDownloadUri(
                    new Uri("https://github.com.evil.example/x.dopkg")),
                "a look-alike host is rejected");
            item.Check(GitHubReleaseClient.ReleaseApiPath("a", "b", null) == "/repos/a/b/releases/latest",
                "without a tag the latest endpoint is used");
            item.Check(GitHubReleaseClient.ReleaseApiPath("a", "b", "v1.2.3") == "/repos/a/b/releases/tags/v1.2.3",
                "with a tag the tagged endpoint is used (tag wins over latest)");
            item.Check(GitHubReleaseClient.IsOfficialOwner("Three-Freezen")
                       && !GitHubReleaseClient.IsOfficialOwner("lab"),
                "only the Three-Freezen owner counts as official");

            // ── 4/5. 无网络 / 假 GitHub 服务 ──
            installer.ConfigureGitHubClientForLab = client =>
            {
                client.ApiBaseUrl = "http://127.0.0.1:9";
                client.AllowInsecureLoopbackForLab = true;
            };
            var unreachable = await installer.InstallFromGitHubAsync("lab", "unreachable", null);
            item.Check(!unreachable.Success, "an unreachable API fails without throwing");
            item.Check(unreachable.Error == loc["Plugin.GitHub.Error.Network"],
                "the unreachable API error is the localized network message");

            // 空状态对话框截图（还没配置假服务，不触发网络）。
            var emptyDialog = new PluginGitHubInstallDialog();
            emptyDialog.Show();
            await Task.Delay(300);
            item.Check(!emptyDialog.InstallButton.IsEnabled,
                "the install button starts disabled in the empty state");
            item.Check(emptyDialog.PreviewBox.Visibility == Visibility.Collapsed,
                "the empty state has no preview area");
            item.Check(emptyDialog.RepoHint.Visibility == Visibility.Visible,
                "the placeholder hint is visible in the empty state");
            SaveScreenshot(emptyDialog, Path.Combine(dir, "shot-gh-install-dialog.png"));
            item.Check(File.Exists(Path.Combine(dir, "shot-gh-install-dialog.png")),
                "the GitHub install dialog screenshot is written");
            emptyDialog.Close();

            // 运行时合成一个语言包当「Release 资产」：真实 .dopkg（有 manifest / payload / icon）。
            var packagePath = BuildLanguageLabPackage(dir, "lang-gh-1.0.0.dopkg", GitHubLabPackId,
                "fr", "Français", "en",
                new Dictionary<string, string> { ["Manage.Zones"] = "Zones" }, null);
            var packageBytes = File.ReadAllBytes(packagePath);
            var packageSha = PluginPackages.Sha256OfFile(packagePath);

            server = new FakeGitHubServer();
            var baseUrl = server.BaseUrl;
            server.RouteBytes("/dl/gh.dopkg", () => File.ReadAllBytes(packagePath));
            server.RouteBytes("/dl/bad.dopkg", () => File.ReadAllBytes(packagePath));
            server.Route("/dl/bad.minisig", 200, "untrusted comment: lab signature\nAAAA\n", "text/plain");
            string ReleaseJson(string tag, params (string Name, string Url, long Size)[] assets)
                => LabReleaseJson(tag, assets);

            // 正常仓库：只有一个 .dopkg、无签名。
            server.Route($"/repos/{GitHubLabOwner}/{GitHubLabRepo}/releases/latest", 200,
                ReleaseJson("v1.0.0", ("lang-gh-1.0.0.dopkg", baseUrl + "/dl/gh.dopkg", packageBytes.Length)));
            // 多资产仓库。
            server.Route("/repos/lab/deskorder-multi/releases/latest", 200,
                ReleaseJson("v1.0.0",
                    ("a.dopkg", baseUrl + "/dl/gh.dopkg", packageBytes.Length),
                    ("b.dopkg", baseUrl + "/dl/gh.dopkg", packageBytes.Length)));
            // 签名无效的仓库。
            server.Route("/repos/lab/deskorder-badsig/releases/latest", 200,
                ReleaseJson("v1.0.0",
                    ("lang-gh-1.0.0.dopkg", baseUrl + "/dl/bad.dopkg", packageBytes.Length),
                    ("lang-gh-1.0.0.dopkg.minisig", baseUrl + "/dl/bad.minisig", 40)));
            // 官方仓库但不带签名。
            server.Route("/repos/Three-Freezen/deskorder-unsigned/releases/latest", 200,
                ReleaseJson("v1.0.0", ("lang-gh-1.0.0.dopkg", baseUrl + "/dl/gh.dopkg", packageBytes.Length)));
            // 只有 tag 可用的仓库（latest 故意 500，证明没有退回 latest）。
            server.Route("/repos/lab/deskorder-tag/releases/latest", 500, "{\"message\":\"boom\"}");
            server.Route("/repos/lab/deskorder-tag/releases/tags/v2.0.0", 200,
                ReleaseJson("v2.0.0", ("lang-gh-1.0.0.dopkg", baseUrl + "/dl/gh.dopkg", packageBytes.Length)));
            // 限流仓库：403 + X-RateLimit-Reset（10 分钟后）。
            server.RouteRateLimit("/repos/ratelimit/deskorder-plugin/releases/latest");
            // 重定向仓库：302 到同域允许地址 / 302 到非 GitHub 域名。
            server.RouteRedirect("/dl/redirect.dopkg", "/dl/gh.dopkg");
            server.RouteRedirect("/dl/evil.dopkg", "https://evil.example/x.dopkg");
            server.Route("/repos/lab/deskorder-redirect/releases/latest", 200,
                ReleaseJson("v1.0.0", ("r.dopkg", baseUrl + "/dl/redirect.dopkg", packageBytes.Length)));
            server.Route("/repos/lab/deskorder-evil/releases/latest", 200,
                ReleaseJson("v1.0.0", ("e.dopkg", baseUrl + "/dl/evil.dopkg", packageBytes.Length)));
            // 超大资产：声称 50 MB + 1，安装器应在下载前拒绝。
            server.Route("/repos/lab/deskorder-huge/releases/latest", 200,
                ReleaseJson("v1.0.0",
                    ("huge.dopkg", baseUrl + "/dl/gh.dopkg", PluginPackages.MaxPackageBytes + 1)));

            installer.ConfigureGitHubClientForLab = client =>
            {
                client.ApiBaseUrl = server!.BaseUrl;
                client.AllowInsecureLoopbackForLab = true;
            };

            // ── 5/5. 全链路：查找 → 选资产 → 下载 → SHA-256 → 安装 ──
            var progress = new LabProgress();
            var install = await installer.InstallFromGitHubAsync(GitHubLabOwner, GitHubLabRepo, null, progress);
            item.Check(install.Success, "the full GitHub install chain succeeds"
                                        + (install.Success ? "" : ": " + install.Error));
            item.Check(progress.Messages.Any(message => message == loc["Plugin.GitHub.Status.Finding"]),
                "progress reports the release lookup stage");
            item.Check(progress.Messages.Any(message => message.Contains("lang-gh-1.0.0.dopkg")),
                "progress reports the download stage");
            service.RefreshRecords();
            var record = service.GetRecord(GitHubLabPackId);
            item.Check(record != null, "the installed record shows up after RefreshRecords");
            item.Check(record?.Source == "github", "the record source is github");
            item.Check(record?.Repo == $"{GitHubLabOwner}/{GitHubLabRepo}", "the record keeps owner/repo");
            item.Check(record?.Sha256 == packageSha, "the recorded SHA-256 matches the served bytes");
            item.Check(record?.Signature == "unsigned", "an unsigned GitHub package is recorded as unsigned");
            item.Check(record?.KindValue == PluginKind.Language && record.Enabled,
                "a declarative package installs enabled");
            item.Check(!Directory.EnumerateFiles(PluginPaths.CacheDir, "gh-*.dopkg*").Any(),
                "temporary GitHub downloads are cleaned up after install");
            var installJson = PluginPackages.ReadInstallInfo(PluginPaths.PluginDir(GitHubLabPackId));
            item.Check(installJson?.Source == "github"
                       && installJson?.Repo == $"{GitHubLabOwner}/{GitHubLabRepo}",
                "install.json records the GitHub source and repo");
            item.Check(!string.IsNullOrEmpty(install.InstallDir) && Directory.Exists(install.InstallDir),
                "the version directory is on disk");

            // 来源展示 + 「已下架」判定：github 不是 market，不应显示下架提示。
            item.Check(PluginDisplay.DescribeSource("github") == loc["Plugin.GitHub.Source"],
                "github has its own localized source label");
            item.Check(PluginDisplay.DescribeSource("github") != PluginDisplay.DescribeSource("market"),
                "github installs are not displayed as marketplace installs");

            // 哈希不符负例：prepare 之后改动临时文件，落盘前重算 SHA-256 必须拦住。
            var prepared = await installer.PrepareFromGitHubAsync(GitHubLabOwner, GitHubLabRepo, null);
            item.Check(prepared.Error == null && !string.IsNullOrEmpty(prepared.PackagePath),
                "prepare downloads the package for the tamper test");
            if (!string.IsNullOrEmpty(prepared.PackagePath))
            {
                File.AppendAllText(prepared.PackagePath, "tampered");
                var tampered = await installer.InstallPreparedAsync(prepared);
                item.Check(!tampered.Success
                           && tampered.Error == loc["Plugin.GitHub.Error.HashMismatch"],
                    "a package changed after download is refused by the SHA-256 re-check");
            }
            prepared.Dispose();

            // 签名负例：Release 挂着 .minisig 但验不过 → 拒装。
            var badSignature = await installer.InstallFromGitHubAsync("lab", "deskorder-badsig", null);
            item.Check(!badSignature.Success, "an invalid .minisig asset is refused");
            item.Check(badSignature.Error == loc["Plugin.Signature.Invalid"],
                "the invalid signature error is the readable localized message");

            // 官方策略：Three-Freezen 仓库必须验签通过（这个 Release 没签名）。
            var officialUnsigned = await installer.InstallFromGitHubAsync(
                "Three-Freezen", "deskorder-unsigned", null);
            item.Check(!officialUnsigned.Success, "an unsigned release from the official repo is refused");
            item.Check(officialUnsigned.Error == loc["Plugin.GitHub.Error.OfficialUnsigned"],
                "the official-repo unsigned error is readable");

            // API 错误映射（走真实 HTTP）：404 / 403+限流 / 多资产 / 超大 / tag 优先。
            var notFound = await installer.InstallFromGitHubAsync("lab", "does-not-exist", null);
            item.Check(!notFound.Success && notFound.Error == loc["Plugin.GitHub.Error.NotFound"],
                "a 404 from /latest and /releases maps to 'not found'");
            var rateLimited = await installer.InstallFromGitHubAsync("ratelimit", "deskorder-plugin", null);
            item.Check(!rateLimited.Success, "a rate-limited lookup fails cleanly");
            item.Check(rateLimited.Error == loc.Get("Plugin.GitHub.Error.RateLimit", 10),
                "403 + X-RateLimit-Reset maps to the minute countdown message");
            var multipleAssets = await installer.PrepareFromGitHubAsync("lab", "deskorder-multi", null);
            item.Check(multipleAssets.Error != null
                       && multipleAssets.Error.Contains("a.dopkg") && multipleAssets.Error.Contains("b.dopkg"),
                "multiple .dopkg assets from the API are rejected with both names");
            multipleAssets.Dispose();
            var huge = await installer.PrepareFromGitHubAsync("lab", "deskorder-huge", null);
            item.Check(huge.Error == loc["Plugin.GitHub.Error.TooLarge"],
                "an oversized asset is refused before downloading");
            huge.Dispose();

            var tagged = await installer.PrepareFromGitHubAsync("lab", "deskorder-tag", "v2.0.0");
            item.Check(tagged.Error == null && tagged.Tag == "v2.0.0",
                "the tagged release is resolved when a tag is given");
            tagged.Dispose();
            item.Check(server.RequestSnapshot().Any(path => path.Contains("/releases/tags/v2.0.0")),
                "the tagged endpoint is requested for a tag");
            item.Check(!server.RequestSnapshot().Any(path =>
                    path == "/repos/lab/deskorder-tag/releases/latest"),
                "the tag lookup does not fall back to the latest endpoint");

            // 重定向：允许的地址跟到底，非 GitHub 域名在发出请求前就被拒。
            var redirected = await installer.PrepareFromGitHubAsync("lab", "deskorder-redirect", null);
            item.Check(redirected.Error == null,
                "a redirect to another allowed loopback asset is followed");
            redirected.Dispose();
            var evilRedirect = await installer.PrepareFromGitHubAsync("lab", "deskorder-evil", null);
            item.Check(evilRedirect.Error == loc.Get("Plugin.GitHub.Error.BadHost", "evil.example"),
                "a redirect to a non-GitHub host is refused before requesting it");
            evilRedirect.Dispose();

            // 预览态 UI + 截图：粘贴 owner/repo → 查找 → 预览区出现。
            var dialog = new PluginGitHubInstallDialog();
            dialog.Show();
            await Task.Delay(200);
            dialog.RepoBox.Text = $"{GitHubLabOwner}/{GitHubLabRepo}";
            await dialog.FindAsync();
            item.Check(dialog.PreviewBox.Visibility == Visibility.Visible,
                "a successful lookup shows the preview area");
            item.Check(dialog.InstallButton.IsEnabled, "the install button is enabled after a verified lookup");
            item.Check(!string.IsNullOrEmpty(dialog.PreviewName.Text)
                       && dialog.PreviewHash.Text.Contains(packageSha),
                "the preview shows the plugin name and the package SHA-256");
            item.Check(dialog.PreviewSignature.Text == loc["Plugin.GitHub.Signature.Unsigned"],
                "the preview shows that the package is unsigned");
            SaveScreenshot(dialog, Path.Combine(dir, "shot-gh-install-found.png"));
            item.Check(File.Exists(Path.Combine(dir, "shot-gh-install-found.png")),
                "the GitHub install preview screenshot is written");
            dialog.Close();
        }
        catch (Exception ex)
        {
            item.Check(false, "exception: " + ex.Message);
            item.Details.Add(ex.ToString());
        }
        finally
        {
            try { installer.ConfigureGitHubClientForLab = null; } catch { }
            try { server?.Dispose(); } catch { }
            // 不留测试插件在隔离数据目录里。
            try
            {
                if (service.GetRecord(GitHubLabPackId) != null)
                {
                    service.Uninstall(GitHubLabPackId, deleteData: true, out _);
                    service.RefreshRecords();
                }
                item.Check(service.GetRecord(GitHubLabPackId) == null,
                    "the GitHub lab package is uninstalled at the end");
            }
            catch (Exception ex)
            {
                item.Check(false, "cleanup failed: " + ex.Message);
            }
        }
    }

    // ── 假 GitHub 服务（TcpListener 手工回 HTTP/1.1，不需要 UrlAcl） ──

    /// <summary>
    /// 极简 HTTP/1.1 服务：用 TcpListener 监听 127.0.0.1 随机端口，按路径回预设响应。
    /// 目的是让真实的 <see cref="GitHubReleaseClient"/>（包含重定向/限流/大小校验逻辑）跑在
    /// 可控数据上，而不是依赖公网 GitHub 或 mock 掉整层 HttpClient。
    /// </summary>
    sealed class FakeGitHubServer : IDisposable
    {
        sealed record FakeResponse(int Status, string ContentType, byte[] Body, Dictionary<string, string> Headers);

        readonly TcpListener _listener;
        readonly CancellationTokenSource _cts = new();
        readonly Dictionary<string, Func<FakeResponse>> _routes = new(StringComparer.OrdinalIgnoreCase);
        readonly Task _acceptLoop;
        readonly List<string> _requests = new();

        public string BaseUrl { get; }

        public FakeGitHubServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            BaseUrl = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
            _acceptLoop = Task.Run(AcceptLoopAsync);
        }

        public void Route(string path, int status, string body,
            string contentType = "application/json", Dictionary<string, string>? headers = null)
            => _routes[path] = () => new FakeResponse(status, contentType,
                Encoding.UTF8.GetBytes(body), headers ?? new Dictionary<string, string>());

        public void RouteBytes(string path, Func<byte[]> bytes,
            string contentType = "application/octet-stream")
            => _routes[path] = () => new FakeResponse(200, contentType, bytes(),
                new Dictionary<string, string>());

        public void RouteRedirect(string path, string location)
            => _routes[path] = () => new FakeResponse(302, "text/plain", Array.Empty<byte>(),
                new Dictionary<string, string> { ["Location"] = location });

        /// <summary>403 + 动态算出的 X-RateLimit-Reset（10 分钟后），用于验证限流文案。</summary>
        public void RouteRateLimit(string path)
            => _routes[path] = () => new FakeResponse(403, "application/json",
                Encoding.UTF8.GetBytes("{\"message\":\"API rate limit exceeded\"}"),
                new Dictionary<string, string>
                {
                    ["X-RateLimit-Reset"] = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds().ToString()
                });

        public List<string> RequestSnapshot()
        {
            lock (_requests) return _requests.ToList();
        }

        async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                }
                catch
                {
                    break;
                }
                _ = Task.Run(() => ServeAsync(client));
            }
        }

        async Task ServeAsync(TcpClient client)
        {
            try
            {
                using (client)
                await using (var stream = client.GetStream())
                {
                    // 只读请求头（到空行为止）；请求体用不到。
                    var head = new StringBuilder();
                    var one = new byte[1];
                    while (head.Length < 16 * 1024)
                    {
                        var read = await stream.ReadAsync(one.AsMemory(0, 1)).ConfigureAwait(false);
                        if (read == 0) return;
                        head.Append((char)one[0]);
                        if (head.Length >= 4 && head.ToString(head.Length - 4, 4) == "\r\n\r\n") break;
                    }

                    var requestLine = head.ToString().Split("\r\n")[0];
                    var parts = requestLine.Split(' ');
                    var path = parts.Length >= 2 ? parts[1] : "/";
                    lock (_requests) _requests.Add(path);

                    FakeResponse response;
                    if (_routes.TryGetValue(path.Split('?')[0], out var factory))
                    {
                        response = factory();
                    }
                    else
                    {
                        response = new FakeResponse(404, "application/json",
                            Encoding.UTF8.GetBytes("{\"message\":\"Not Found\"}"),
                            new Dictionary<string, string>());
                    }

                    var header = new StringBuilder();
                    header.Append("HTTP/1.1 ").Append(response.Status).Append(' ')
                        .Append(response.Status == 200 ? "OK" : "ERR").Append("\r\n");
                    header.Append("Content-Type: ").Append(response.ContentType).Append("\r\n");
                    header.Append("Content-Length: ").Append(response.Body.Length).Append("\r\n");
                    foreach (var pair in response.Headers)
                        header.Append(pair.Key).Append(": ").Append(pair.Value).Append("\r\n");
                    header.Append("Connection: close\r\n\r\n");
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(header.ToString())).ConfigureAwait(false);
                    if (response.Body.Length > 0)
                        await stream.WriteAsync(response.Body).ConfigureAwait(false);
                    await stream.FlushAsync().ConfigureAwait(false);
                }
            }
            catch
            {
                // 客户端提前断开 / 服务停止都走这里：自测断言看请求记录，不看服务端日志。
            }
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { }
            try { _listener.Stop(); } catch { }
            try { _acceptLoop.Wait(2000); } catch { }
            _cts.Dispose();
        }
    }

    /// <summary>构造 GitHub Release JSON（只有安装链路用到的字段）。</summary>
    static string LabReleaseJson(string tag, params (string Name, string Url, long Size)[] assets)
    {
        var payload = new Dictionary<string, object?>
        {
            ["tag_name"] = tag,
            ["name"] = "Lab release " + tag,
            ["draft"] = false,
            ["prerelease"] = false,
            ["published_at"] = "2026-01-01T00:00:00Z",
            ["assets"] = assets.Select(asset => new Dictionary<string, object?>
            {
                ["name"] = asset.Name,
                ["size"] = asset.Size,
                ["browser_download_url"] = asset.Url
            }).ToList()
        };
        return JsonSerializer.Serialize(payload);
    }

    /// <summary>同步收集进度文案（避免 Progress&lt;T&gt; 的异步投递让断言看到空列表）。</summary>
    sealed class LabProgress : IProgress<string>
    {
        public List<string> Messages { get; } = new();

        public void Report(string value)
        {
            lock (Messages) Messages.Add(value);
        }
    }
}
#endif
