using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DesktopZones.Helpers;

namespace DesktopZones.Services;

/// <summary>更新流程状态（设置页据此渲染卡片，后台检查据此发托盘气泡）。</summary>
public enum UpdateState
{
    Idle,         // 初始/可手动检查
    Checking,     // 正在检查
    UpToDate,     // 已是最新
    Available,    // 发现新版本，等待用户确认下载
    Downloading,  // 下载中
    Ready,        // 已下载暂存，重启后生效
    Failed,       // 上次操作失败（见 ErrorText）
    Unavailable,  // 当前环境不支持应用内更新（商店版 / 开发目录运行）
}

/// <summary>单次版本检查的结果。</summary>
public sealed record UpdateCheckResult(string Version, string? ReleaseUrl);

/// <summary>
/// 更新渠道抽象。当前实现：GitHub Releases + Inno Setup 安装包。
/// 微软商店渠道（打包 MSIX 后运行）必须走系统 StoreContext API，
/// 未来在此接口下补一个实现即可，UI 层零改动。
/// </summary>
public interface IUpdateChannel
{
    /// <summary>检查新版本；返回 null 表示已是最新。网络失败抛异常。</summary>
    Task<UpdateCheckResult?> CheckAsync(CancellationToken ct);

    /// <summary>下载安装包到临时目录（进度 0-100）。</summary>
    Task DownloadAsync(IProgress<int> progress, CancellationToken ct);

    /// <summary>启动安装器静默升级到原路径并退出本进程（成功则不返回）。需先 Check + Download。</summary>
    void ApplyAndRestart();
}

public sealed class UpdateService
{
    public const string DefaultRepoUrl = "https://github.com/Three-Freezen/DeskOrder";
    internal const string ReleasesApiUrl = "https://api.github.com/repos/Three-Freezen/DeskOrder/releases/latest";
    // 网页端点(非 REST API):无匿名 60 次/小时配额,302 Location 即最新 tag。
    internal const string ReleasesLatestWebUrl = "https://github.com/Three-Freezen/DeskOrder/releases/latest";
    internal const string SetupAssetName = "DeskOrder-win-Setup.exe";

    // ponytail 2026-08-29: 安装包下载落点 = 用户"下载"文件夹(用户要求:下载完的安装器
    // 要放在电脑的下载里,之前落 %TEMP% 等于藏起来了)。SHGetKnownFolderPath 取真实
    // 下载目录(兼容 OneDrive 重定向);解析/创建失败退回 %TEMP%,不阻塞更新流程。
    internal static readonly string SetupDownloadPath = ResolveSetupDownloadPath();

    private static string ResolveSetupDownloadPath()
    {
        try
        {
            // FOLDERID_Downloads {374DE290-123F-4565-9164-39C4925E467B}
            var downloads = GetKnownFolderPath(
                new Guid("374DE290-123F-4565-9164-39C4925E467B"));
            if (!string.IsNullOrEmpty(downloads))
            {
                Directory.CreateDirectory(downloads);
                return Path.Combine(downloads, SetupAssetName);
            }
        }
        catch { }
        return Path.Combine(Path.GetTempPath(), SetupAssetName);
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(in Guid rfid, uint flags, IntPtr token, out IntPtr path);

    private static string? GetKnownFolderPath(Guid guid)
    {
        int hr = SHGetKnownFolderPath(guid, 0, IntPtr.Zero, out var ptr);
        if (hr != 0 || ptr == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUni(ptr); }
        finally { Marshal.FreeCoTaskMem(ptr); }
    }

    /// <summary>运行时覆盖更新源（--update-source=本地目录）：目录里含 DeskOrder-win-Setup.exe
    /// 即视为有更新（版本取安装包内嵌 ProductVersion），供本地端到端测试，不触网。</summary>
    public static string? SourceOverride { get; set; }

    public static bool IsRunningPackaged { get; } = DetectPackaged();

    /// <summary>本进程是否由安装器安装而来：Inno 安装必然在 {app} 落一个 unins000.exe。
    /// 开发目录运行（dotnet run / bin\Debug）没有 → 不支持应用内更新。</summary>
    public static bool IsInstalledBuild =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));

    /// <summary>本环境是否支持应用内更新。</summary>
    public bool InAppUpdateSupported => !IsRunningPackaged && IsInstalledBuild;

    /// <summary>状态变化通知。已在 UI 线程上触发，订阅方无需自行 marshal。</summary>
    public event Action? StateChanged;

    public UpdateState State { get; private set; } = UpdateState.Idle;
    public string? NewVersion { get; private set; }
    public string? ErrorText { get; private set; }
    public int ProgressPercent { get; private set; }
    public string ReleaseUrl => DefaultRepoUrl + "/releases/latest";

    private readonly ConfigService _configService;
    private IUpdateChannel? _channel;
    private bool _busy;

    public UpdateService(ConfigService configService) => _configService = configService;

    // ── UI / 启动流程入口 ──

    /// <summary>检查更新（手动按钮与后台共用）。返回非 null 表示发现新版本。</summary>
    public async Task<UpdateCheckResult?> CheckForUpdatesAsync()
    {
        var channel = GetChannel();
        if (channel == null) { ErrorText = null; SetState(UpdateState.Unavailable); return null; }
        if (_busy) return null;
        _busy = true;
        ErrorText = null;
        SetState(UpdateState.Checking);
        try
        {
            var result = await channel.CheckAsync(CancellationToken.None);
            TouchLastCheckTime();
            NewVersion = result?.Version;
            SetState(result == null ? UpdateState.UpToDate : UpdateState.Available);
            return result;
        }
        catch (Exception ex)
        {
            TouchLastCheckTime();
            ErrorText = FriendlyError(ex);
            SetState(UpdateState.Failed);
            return null;
        }
        finally { _busy = false; }
    }

    /// <summary>下载已发现的新版本（应用内进度条驱动）。</summary>
    public async Task DownloadAsync()
    {
        var channel = GetChannel();
        if (channel == null) { SetState(UpdateState.Unavailable); return; }
        SetState(UpdateState.Downloading);
        ProgressPercent = 0;
        try
        {
            await channel.DownloadAsync(new Progress<int>(p =>
            {
                ProgressPercent = p;
                RaiseStateChanged();
            }), CancellationToken.None);
            SetState(UpdateState.Ready);
        }
        catch (Exception ex)
        {
            ErrorText = FriendlyError(ex);
            SetState(UpdateState.Failed);
        }
    }

    /// <summary>启动已下载的安装器静默升级到原路径（/SILENT /DIR=当前目录），
    /// 随后退出本进程让 Inno 接管；装完由安装器自动重启应用。失败抛异常。</summary>
    public void ApplyAndRestart()
    {
        // ponytail 2026-08-30: 勾选"更新完成后自动删除安装包"时留下待清理标记 —
        // 新版本首次启动消费它(安装器运行期间删不了自己,只有新版真正跑起来才算
        // "更新完毕");标记里记录旧版本号,只有真的升上去了才删,避免误删可手动
        // 重试的安装包。标记存在本身即"用户已勾选",新实例无需再读配置。
        try
        {
            if (_configService.Load().DeleteSetupAfterUpdate)
                File.WriteAllText(PendingSetupCleanupPath,
                    $"{SetupDownloadPath}\n{AppVersion.Current}");
        }
        catch { }
        GetChannel()?.ApplyAndRestart();
    }

    // ── 更新后安装包自动清理(设置页"更新完成后自动删除安装包") ──

    /// <summary>待清理标记:数据根目录下,内容两行 = 安装包绝对路径 \n 旧版本号。
    /// 由 ApplyAndRestart(勾选时)写入,新版首启 ConsumePendingSetupCleanup 消费。</summary>
    internal static string PendingSetupCleanupPath => Path.Combine(DataLocator.Root, "pending-setup-cleanup.txt");

    /// <summary>
    /// 新版本首次启动消费待清理标记:安装包路径与名称校验通过、且当前版本确实
    /// 大于标记里的旧版本(= 升级成功)时,删除下载文件夹里的安装包。任何一步失败
    /// 都只放弃,绝不阻塞启动;同版本(更新没完成/被取消)保留安装包给用户手动处理。
    /// 标记读到一个字节就先删 — 只会消费一次,不因反复启动反复尝试。
    /// </summary>
    public static void ConsumePendingSetupCleanup()
    {
        try
        {
            if (!File.Exists(PendingSetupCleanupPath)) return;
            var lines = File.ReadAllLines(PendingSetupCleanupPath);
            File.Delete(PendingSetupCleanupPath);
            var setupPath = lines.Length > 0 ? lines[0].Trim() : "";
            var oldVer = lines.Length > 1 ? lines[1].Trim() : "";
            // 双保险:只删"名字对得上 + 路径存在 + 版本真的升上去了"的那个文件,
            // 用户手动从浏览器下载的同名安装包没有标记,永远不会被这条链路碰。
            if (Path.GetFileName(setupPath) != SetupAssetName || !File.Exists(setupPath)) return;
            if (!Version.TryParse(oldVer.TrimStart('v', 'V'), out var oldV)) return;
            if (!Version.TryParse(AppVersion.Current.Split('+')[0], out var curV)) return;
            if (curV > oldV)
                File.Delete(setupPath);
        }
        catch
        {
            // 清理失败(占用/权限)无关紧要 — 下次更新的标记会覆盖同名文件。
        }
    }

    /// <summary>启动后台检查：环境不支持 / 开关关闭 / 24 小时内查过 → 直接返回。
    /// 发现新版本发托盘气泡；不自动下载（交互定为「提示后更新」）。</summary>
    public async Task AutoCheckIfDueAsync()
    {
        try
        {
            if (!InAppUpdateSupported) return;
            var cfg = _configService.Load();
            if (!cfg.AutoCheckUpdate) return;
            if (cfg.LastUpdateCheckUtc != default &&
                DateTime.UtcNow - cfg.LastUpdateCheckUtc < TimeSpan.FromHours(24)) return;

            var result = await CheckForUpdatesAsync();
            if (result != null)
            {
                var loc = LocalizationService.Instance;
                App.Notify?.Invoke(loc["Settings.Update.ToastTitle"],
                    loc.Get("Settings.NewVersionFound", result.Version));
            }
        }
        catch { /* 后台检查的任何失败都静默，不打扰启动流程 */ }
    }

    // ── 内部 ──

    private IUpdateChannel? GetChannel()
    {
        if (_channel != null) return _channel;
        if (IsRunningPackaged) return null;    // 商店渠道占位：未来接 StoreContext 实现
        if (!IsInstalledBuild) return null;    // 开发目录运行（dotnet run / bin\Debug）
        _channel = new GitHubSetupChannel();
        return _channel;
    }

    /// <summary>记录检查时间（失败也记）：断网/仓库 404 时避免启动反复撞接口。</summary>
    private void TouchLastCheckTime()
    {
        try
        {
            var cfg = _configService.Load();
            cfg.LastUpdateCheckUtc = DateTime.UtcNow;
            _configService.Save(cfg);
        }
        catch { /* 时间戳丢失只影响节流，不致命 */ }
    }

    private void SetState(UpdateState state)
    {
        State = state;
        RaiseStateChanged();
    }

    /// <summary>把底层网络异常翻译成用户能看懂的提示（GitHub API 直连被墙 /
    /// 共享代理出口限流是国内最常见的两种失败，必须单独说明而不是甩英文异常）。</summary>
    private static string FriendlyError(Exception ex)
    {
        var loc = LocalizationService.Instance;
        if (ex is HttpRequestException hre)
        {
            var code = hre.StatusCode;
            if (code == System.Net.HttpStatusCode.Forbidden) return loc["Settings.UpdateErr.RateLimit"];
            if (code == System.Net.HttpStatusCode.ProxyAuthenticationRequired) return loc["Settings.UpdateErr.Proxy"];
            if (code != null && (int)code >= 400)
                return loc.Get("Settings.UpdateErr.Http", (int)code);
            return loc["Settings.UpdateErr.Network"];
        }
        if (ex is TaskCanceledException) return loc["Settings.UpdateErr.Timeout"];
        return ex.Message;
    }

    private void RaiseStateChanged()
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d == null || d.CheckAccess()) StateChanged?.Invoke();
        else d.BeginInvoke(() => StateChanged?.Invoke());
    }

    private static bool DetectPackaged()
    {
        try
        {
            uint len = 0;
            // 未打包进程返回 APPMODEL_ERROR_NO_PACKAGE(15700)；打包进程为缓冲区不足(122)。
            return GetCurrentPackageFamilyName(ref len, null) != 15700;
        }
        catch { return false; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFamilyName(ref uint packageFamilyNameLength, StringBuilder? packageFamilyName);
}

/// <summary>
/// GitHub Releases 渠道：匿名访问公开仓库的 latest release，比较 tag 与当前版本，
/// 下载固定文件名的 DeskOrder-win-Setup.exe（每次发布覆盖同名资产，latest 永远最新），
/// 升级时静默运行 Inno 安装器（/SILENT /DIR=原路径，自动重启应用）。
/// </summary>
file sealed class GitHubSetupChannel : IUpdateChannel
{
    private static readonly HttpClient Http = CreateHttp();
    // ponytail 2026-08-30: 版本检查用"禁止重定向"的客户端读 302 Location — 默认
    // HttpClient 会静默跟随重定向拿回整个 HTML 页,拿不到 Location 头。
    private static readonly HttpClient NoRedirectHttp = CreateHttp(allowAutoRedirect: false);
    private string? _downloadUrl;
    private string? _pendingVersion;

    private static HttpClient CreateHttp(bool allowAutoRedirect = true)
    {
        // AllowAutoRedirect 在 handler 上;HttpClientHandler 默认走系统代理(Clash 开着就跟随)。
        var handler = new HttpClientHandler { AllowAutoRedirect = allowAutoRedirect };
        var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        // GitHub API 强制要求 User-Agent
        c.DefaultRequestHeaders.UserAgent.ParseAdd("DeskOrder-UpdateCheck");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    public async Task<UpdateCheckResult?> CheckAsync(CancellationToken ct)
    {
        // 本地测试源：--update-source=目录 直接把目录里的安装包当新版本，不触网。
        var o = UpdateService.SourceOverride;
        if (!string.IsNullOrWhiteSpace(o) && Directory.Exists(o))
        {
            var localSetup = Path.Combine(o, UpdateService.SetupAssetName);
            if (File.Exists(localSetup))
                return IsNewer(FileVersionInfo.GetVersionInfo(localSetup).ProductVersion)
                    ? new UpdateCheckResult(_pendingVersion!, null) : null;
        }

        // ponytail 2026-08-30: 版本检查不再走 REST API — 匿名配额只有每 IP 每小时
        // 60 次,共享代理出口(Clash 节点/NAT)极易被同 IP 的其他用户烧光 → 403 弹
        // 「GitHub 限流」。改请求网页端点 releases/latest,从 302 的 Location
        // (.../releases/tag/vX.Y.Z)解析最新版本 — github.com 网页路径无此配额。
        // API 保留为兜底(重定向被网关拦截等异常时再试一次,限流了也只是维持原状)。
        string tag;
        try
        {
            tag = await ResolveLatestTagViaRedirect(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            using var resp = await Http.GetAsync(UpdateService.ReleasesApiUrl, ct);
            resp.EnsureSuccessStatusCode();
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
        }

        if (!IsNewer(tag)) return null;

        // 下载走版本无关直链(releases/latest/download/),不再经 API 枚举 assets。
        _downloadUrl = $"{UpdateService.DefaultRepoUrl}/releases/latest/download/{UpdateService.SetupAssetName}";
        return new UpdateCheckResult(_pendingVersion!, UpdateService.DefaultRepoUrl + "/releases/latest");
    }

    /// <summary>
    /// 从 releases/latest 的 302 Location 头解析最新 tag。Location 可能是绝对 URL
    /// 或相对路径(/Three-Freezen/DeskOrder/releases/tag/vX.Y.Z),统一取 "tag" 段后
    /// 的那一段;拿到 Location 却解析不出 tag 视为环境异常,抛出走 API 兜底。
    /// </summary>
    private static async Task<string> ResolveLatestTagViaRedirect(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, UpdateService.ReleasesLatestWebUrl);
        using var resp = await NoRedirectHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        var loc = resp.Headers.Location;
        if (loc == null)
            throw new HttpRequestException($"releases/latest 未重定向 (HTTP {(int)resp.StatusCode})");
        var path = loc.IsAbsoluteUri ? loc.AbsolutePath : loc.OriginalString;
        var parts = path.Split('/');
        var tagIdx = Array.LastIndexOf(parts, "tag");
        var tag = tagIdx >= 0 && tagIdx + 1 < parts.Length ? parts[tagIdx + 1] : "";
        if (string.IsNullOrEmpty(tag))
            throw new HttpRequestException($"重定向地址不含版本 tag: {path}");
        return tag;
    }

    /// <summary>远端 tag（v 前缀可带）是否比当前程序集版本新。解析失败视为不更新（保守）。</summary>
    private bool IsNewer(string? remoteTag)
    {
        if (!Version.TryParse(remoteTag?.TrimStart('v', 'V'), out var remote)) return false;
        var cur = AppVersion.Current.Split('+')[0];
        if (!Version.TryParse(cur, out var local)) return false;
        _pendingVersion = remote.ToString();
        return remote > local;
    }

    public async Task DownloadAsync(IProgress<int> progress, CancellationToken ct)
    {
        if (_downloadUrl == null) throw new InvalidOperationException("No update checked yet");
        // 先写 .part 临时名,完成后再改成正式名 — 中断/崩溃不会在"下载"文件夹里留下
        // 半截 DeskOrder-win-Setup.exe 冒充安装包(误点会装坏)。
        var partPath = UpdateService.SetupDownloadPath + ".part";
        using var resp = await Http.GetAsync(_downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? -1;
        await using (var dst = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            var buf = new byte[81920];
            long read = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                read += n;
                if (total > 0) progress.Report((int)(read * 100 / total));
            }
        }
        File.Move(partPath, UpdateService.SetupDownloadPath, overwrite: true);
        progress.Report(100);
    }

    public void ApplyAndRestart()
    {
        if (!File.Exists(UpdateService.SetupDownloadPath))
            throw new InvalidOperationException("No installer downloaded yet");
        // /SILENT：无向导仅进度；/DIR= 原安装路径；Inno CloseApplications+AppMutex
        // 会等本进程退出后接管；[Run] 的静默项装完自动重启应用。
        Process.Start(new ProcessStartInfo(UpdateService.SetupDownloadPath)
        {
            Arguments = $"/SILENT /DIR=\"{AppContext.BaseDirectory.TrimEnd('\\')}\"",
            UseShellExecute = true,
        });
        System.Windows.Application.Current.Shutdown();
    }
}
