using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
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
/// 更新渠道抽象。实现：GitHub Releases 的 Setup 渠道（Inno 安装版，静默升级）与
/// MSIX 渠道（打包分发版，下载后交给系统应用安装程序就地更新）。
/// 微软商店渠道（Store 签名分发）未来走 StoreContext API，再补一个实现即可，UI 层零改动。
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
    // MSIX 渠道资产名(与 tools/make-msix.ps1 输出、release.yml 挂载的固定文件名一致)。
    internal const string MsixAssetName = "DeskOrder-win-MSIX.msix";

    // ponytail 2026-08-29: 安装包下载落点 = 用户"下载"文件夹(用户要求:下载完的安装器
    // 要放在电脑的下载里,之前落 %TEMP% 等于藏起来了)。SHGetKnownFolderPath 取真实
    // 下载目录(兼容 OneDrive 重定向);解析/创建失败退回 %TEMP%,不阻塞更新流程。
    internal static readonly string SetupDownloadPath = ResolveDownloadPath(SetupAssetName);
    internal static readonly string MsixDownloadPath = ResolveDownloadPath(MsixAssetName);

    private static string ResolveDownloadPath(string assetName)
    {
        try
        {
            // FOLDERID_Downloads {374DE290-123F-4565-9164-39C4925E467B}
            var downloads = GetKnownFolderPath(
                new Guid("374DE290-123F-4565-9164-39C4925E467B"));
            if (!string.IsNullOrEmpty(downloads))
            {
                Directory.CreateDirectory(downloads);
                return Path.Combine(downloads, assetName);
            }
        }
        catch { }
        return Path.Combine(Path.GetTempPath(), assetName);
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

    /// <summary>本进程是否 MSIX 打包运行。统一走 DataLocator.IsPackaged(同一 kernel32
    /// 判定,DataLocator 的便携模式判定也用它,避免两处 P/Invoke 各自为政)。</summary>
    public static bool IsRunningPackaged => DataLocator.IsPackaged;

    // Partner Center 分配的商店包身份(make-msix.ps1 -ForStore 写进清单,两者必须一致)。
    // 注意:身份名以连字符结尾是 Partner Center 的原样分配值,不是笔误。
    internal const string StoreIdentityName = "Three-Freezen.DeskOrder-";

    /// <summary>本进程是否商店渠道安装(包身份 = StoreIdentityName)。商店版的检查、
    /// 下载、安装全部由 Microsoft Store 负责;GitHub 侧载 MSIX 是占位身份,走 MSIX 渠道。</summary>
    public static bool IsStorePackage => IsRunningPackaged && DataLocator.PackageIdentityName == StoreIdentityName;

    /// <summary>本进程是否由安装器安装而来：Inno 安装必然在 {app} 落一个 unins000.exe。
    /// 开发目录运行（dotnet run / bin\Debug）没有 → 不支持应用内更新。</summary>
    public static bool IsInstalledBuild =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));

    /// <summary>本环境是否支持应用内更新：安装器版走 Setup 渠道，MSIX 打包版走
    /// MSIX 渠道（商店身份除外——商店版由商店更新）；只有开发目录运行（dotnet run /
    /// bin\Debug）不支持。</summary>
    public bool InAppUpdateSupported => (IsInstalledBuild || IsRunningPackaged) && !IsStorePackage;

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
            // 下载完成即写待清理标记,不等用户点「重启并更新」:Ready 之后用户完全
            // 可能自己手动跑安装包(实测 1.0.13 升级就漏了一种),装完的新版首启
            // 只认标记,不关心安装动作是静默还是手动。
            TryWritePendingSetupCleanup();
            SetState(UpdateState.Ready);
        }
        catch (Exception ex)
        {
            ErrorText = FriendlyError(ex);
            SetState(UpdateState.Failed);
        }
    }

    /// <summary>按当前勾选状态维护待清理标记(勾选=写入,取消=撤除)。任何失败静默
    /// — 清理是锦上添花,绝不干扰更新主流程。</summary>
    private void TryWritePendingSetupCleanup()
    {
        try
        {
            // MSIX 渠道没有"安装包"概念(App Installer 管下载与旧版清理),跳过标记。
            if (IsRunningPackaged || !_configService.Load().DeleteSetupAfterUpdate) return;
            // 只写纯版本号 — InformationalVersion 在 Git 仓库构建时会带 "+提交哈希"
            // (1.0.5+abc123),版本比较用的 Version.TryParse 不认它。
            var ver = AppVersion.Current.Split('+')[0];
            File.WriteAllText(PendingSetupCleanupPath, $"{SetupDownloadPath}\n{ver}");
        }
        catch { }
    }

    /// <summary>启动已下载的安装器静默升级到原路径（/SILENT /DIR=当前目录），
    /// 随后退出本进程让 Inno 接管；装完由安装器自动重启应用。失败抛异常。</summary>
    public void ApplyAndRestart()
    {
        // 标记已在下载完成时写入;这里按当下的勾选状态做最后一次校正 — 下载后
        // 反悔取消勾选的用户,装完也不该被删安装包。
        try
        {
            if (!IsRunningPackaged && !_configService.Load().DeleteSetupAfterUpdate)
                File.Delete(PendingSetupCleanupPath);
        }
        catch { }
        GetChannel()?.ApplyAndRestart();
    }

    // ── 更新后安装包自动清理(设置页"更新完成后自动删除安装包") ──

    /// <summary>待清理标记:数据根目录下,内容两行 = 安装包绝对路径 \n 旧版本号。
    /// 由 DownloadAsync 成功后(勾选时)写入,新版首启 ConsumePendingSetupCleanup
    /// 消费;ApplyAndRestart 按当下勾选状态做撤除校正。</summary>
    internal static string PendingSetupCleanupPath => Path.Combine(DataLocator.Root, "pending-setup-cleanup.txt");

    /// <summary>
    /// 新版本首次启动消费待清理标记:安装包名称校验通过、且当前版本确实大于标记里
    /// 的旧版本(= 升级成功)时,删除下载文件夹里的安装包。无效标记 / 同版本(更新
    /// 没完成或被取消) / 安装包已不在 → 只作废标记,安装包留给用户手动处理;用户
    /// 手动从浏览器下载的同名安装包没有标记,永远不会被这条链路碰。
    /// 标记只在安装包真删掉了之后才消费 — 1.0.13 实测教训:Inno 引导器要等整个
    /// 安装结束才退出,新版首启时下载的安装包还是运行态,删一次失败就被吞而标记
    /// 已一次性消费 → 安装包永久残留。现在失败保留标记,后台短重试 + 下次启动再试。
    /// </summary>
    public static void ConsumePendingSetupCleanup()
    {
        try
        {
            if (!File.Exists(PendingSetupCleanupPath)) return;
            var lines = File.ReadAllLines(PendingSetupCleanupPath);
            var setupPath = lines.Length > 0 ? lines[0].Trim() : "";
            // 双侧剥 "+哈希" 后缀(写入侧已剥,这里再剥一次兼容旧标记):CI 在 Git 仓库
            // 构建出的 InformationalVersion 形如 1.0.5+abc123,直接 TryParse 会失败,
            // 静默不删 — v1.0.6 实测「开了自动删除却没删」的根源。
            var oldVer = (lines.Length > 1 ? lines[1].Trim() : "").Split('+')[0];
            if (Path.GetFileName(setupPath) != SetupAssetName
                || !Version.TryParse(oldVer.TrimStart('v', 'V'), out var oldV)
                || !Version.TryParse(AppVersion.Current.Split('+')[0], out var curV)
                || curV <= oldV
                || !File.Exists(setupPath))
            {
                File.Delete(PendingSetupCleanupPath);
                return;
            }
            try
            {
                File.Delete(setupPath);
                File.Delete(PendingSetupCleanupPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                RetryPendingSetupCleanup(setupPath);
            }
        }
        catch
        {
            // 读取/解析类失败无关紧要 — 标记保留,下次启动重走一遍。
        }
    }

    /// <summary>安装包被占用(Inno 引导器还没退 / 杀软扫描)时的后台重试:每 5 秒
    /// 一次至多 12 次,锁是瞬态的,通常几十秒内就能删掉。成功后只清理仍指向同一
    /// 安装包的标记,避免误删新一轮更新刚写的;始终删不掉则保留标记,下次启动再试。</summary>
    private static void RetryPendingSetupCleanup(string setupPath)
    {
        _ = Task.Run(async () =>
        {
            for (var i = 0; i < 12; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                try
                {
                    File.Delete(setupPath);
                    if (File.Exists(PendingSetupCleanupPath)
                        && File.ReadAllLines(PendingSetupCleanupPath) is { Length: > 0 } check
                        && check[0].Trim() == setupPath)
                        File.Delete(PendingSetupCleanupPath);
                    return;
                }
                catch { /* 还在占用,等下一轮 */ }
            }
        });
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
        if (IsRunningPackaged)
        {
            // 商店安装返回 null → CheckForUpdatesAsync 走 Unavailable,设置页显示
            // 「商店版本由微软商店负责更新」;GitHub 侧载的 MSIX(占位身份)继续走 MSIX 渠道。
            if (IsStorePackage) return null;
            return _channel = new GitHubMsixChannel();
        }
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
}

/// <summary>
/// GitHub Releases 更新源的共用机制：版本探测（releases/latest 302 重定向为主，
/// REST API 兜底）、版本比较、流式下载（.part 临时名 + 原子改名）。Setup 与 MSIX
/// 两个渠道共用，只差资产名与落地动作。
/// </summary>
file static class GitHubReleaseFeed
{
    internal static readonly HttpClient Http = CreateHttp();
    // ponytail 2026-08-30: 版本检查用"禁止重定向"的客户端读 302 Location — 默认
    // HttpClient 会静默跟随重定向拿回整个 HTML 页,拿不到 Location 头。
    internal static readonly HttpClient NoRedirectHttp = CreateHttp(allowAutoRedirect: false);

    static HttpClient CreateHttp(bool allowAutoRedirect = true)
    {
        // AllowAutoRedirect 在 handler 上;HttpClientHandler 默认走系统代理(Clash 开着就跟随)。
        var handler = new HttpClientHandler { AllowAutoRedirect = allowAutoRedirect };
        var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        // GitHub API 强制要求 User-Agent
        c.DefaultRequestHeaders.UserAgent.ParseAdd("DeskOrder-UpdateCheck");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    /// <summary>固定名资产直链（每次发布覆盖同名资产，latest 永远最新）。</summary>
    internal static string AssetUrl(string assetName) =>
        $"{UpdateService.DefaultRepoUrl}/releases/latest/download/{assetName}";

    /// <summary>
    /// 最新 tag。ponytail 2026-08-30: 版本检查不走 REST API — 匿名配额只有每 IP 每小时
    /// 60 次,共享代理出口(Clash 节点/NAT)极易被同 IP 的其他用户烧光 → 403 弹
    /// 「GitHub 限流」。改请求网页端点 releases/latest,从 302 的 Location
    /// (.../releases/tag/vX.Y.Z)解析最新版本 — github.com 网页路径无此配额。
    /// API 保留为兜底(重定向被网关拦截等异常时再试一次,限流了也只是维持原状)。
    /// </summary>
    internal static async Task<string> ResolveLatestTagAsync(CancellationToken ct)
    {
        string tag;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, UpdateService.ReleasesLatestWebUrl);
            using var resp = await NoRedirectHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            var loc = resp.Headers.Location;
            if (loc == null)
                throw new HttpRequestException($"releases/latest 未重定向 (HTTP {(int)resp.StatusCode})");
            var path = loc.IsAbsoluteUri ? loc.AbsolutePath : loc.OriginalString;
            var parts = path.Split('/');
            var tagIdx = Array.LastIndexOf(parts, "tag");
            tag = tagIdx >= 0 && tagIdx + 1 < parts.Length ? parts[tagIdx + 1] : "";
            if (string.IsNullOrEmpty(tag))
                throw new HttpRequestException($"重定向地址不含版本 tag: {path}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            using var resp = await Http.GetAsync(UpdateService.ReleasesApiUrl, ct);
            resp.EnsureSuccessStatusCode();
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
        }
        return tag;
    }

    /// <summary>远端 tag（v 前缀可带）是否比当前程序集版本新。新则返回纯版本号，
    /// 否则 null — 解析失败视为不更新（保守）。</summary>
    internal static string? ParseNewerVersion(string? remoteTag)
    {
        if (!Version.TryParse(remoteTag?.TrimStart('v', 'V'), out var remote)) return null;
        if (!Version.TryParse(AppVersion.Current.Split('+')[0], out var local)) return null;
        return remote > local ? remote.ToString() : null;
    }

    /// <summary>流式下载到 .part 临时名,完成后原子改名 — 中断/崩溃不会在"下载"
    /// 文件夹里留下半截文件冒充安装包(误点会装坏)。</summary>
    internal static async Task DownloadToFileAsync(string url, string targetPath, IProgress<int> progress, CancellationToken ct)
    {
        var partPath = targetPath + ".part";
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
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
        File.Move(partPath, targetPath, overwrite: true);
        progress.Report(100);
    }
}

/// <summary>
/// GitHub Releases 渠道基类：匿名访问公开仓库的 latest release，比较 tag 与当前版本，
/// 下载固定文件名资产到用户"下载"文件夹。子类只决定资产名与落地动作。
/// </summary>
file abstract class GitHubReleaseChannelBase : IUpdateChannel
{
    protected abstract string AssetName { get; }
    protected abstract string DownloadPath { get; }

    public async Task<UpdateCheckResult?> CheckAsync(CancellationToken ct)
    {
        // 本地测试源：--update-source=目录 直接把目录里的同名资产当新版本，不触网。
        var o = UpdateService.SourceOverride;
        if (!string.IsNullOrWhiteSpace(o) && Directory.Exists(o))
        {
            var localAsset = Path.Combine(o, AssetName);
            if (File.Exists(localAsset))
            {
                var localVer = GitHubReleaseFeed.ParseNewerVersion(
                    FileVersionInfo.GetVersionInfo(localAsset).ProductVersion);
                return localVer == null ? null : new UpdateCheckResult(localVer, null);
            }
        }

        var remoteVer = GitHubReleaseFeed.ParseNewerVersion(await GitHubReleaseFeed.ResolveLatestTagAsync(ct));
        return remoteVer == null ? null : new UpdateCheckResult(remoteVer, UpdateService.DefaultRepoUrl + "/releases/latest");
    }

    public Task DownloadAsync(IProgress<int> progress, CancellationToken ct) =>
        GitHubReleaseFeed.DownloadToFileAsync(GitHubReleaseFeed.AssetUrl(AssetName), DownloadPath, progress, ct);

    public abstract void ApplyAndRestart();
}

/// <summary>Setup 渠道(常规 Inno 安装版)：升级时静默运行安装器
/// （/SILENT /DIR=原路径，自动重启应用）。</summary>
file sealed class GitHubSetupChannel : GitHubReleaseChannelBase
{
    protected override string AssetName => UpdateService.SetupAssetName;
    protected override string DownloadPath => UpdateService.SetupDownloadPath;

    public override void ApplyAndRestart()
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

/// <summary>MSIX 渠道(打包分发版)：下载 DeskOrder-win-MSIX.msix 后交给系统应用
/// 安装程序(App Installer)就地更新 — 签名/身份校验与包生命周期由 Windows 管理,
/// 无需静默参数。真商店分发(StoreContext API)未来另补一个 IUpdateChannel 实现。</summary>
file sealed class GitHubMsixChannel : GitHubReleaseChannelBase
{
    protected override string AssetName => UpdateService.MsixAssetName;
    protected override string DownloadPath => UpdateService.MsixDownloadPath;

    public override void ApplyAndRestart()
    {
        if (!File.Exists(UpdateService.MsixDownloadPath))
            throw new InvalidOperationException("No MSIX package downloaded yet");
        // ShellExecute 走 .msix 文件关联拉起 App Installer 展示「更新」;本进程退出
        // 让更新落位,新版从开始菜单原条目启动(包身份不变,用户数据沿用)。
        Process.Start(new ProcessStartInfo(UpdateService.MsixDownloadPath) { UseShellExecute = true });
        System.Windows.Application.Current.Shutdown();
    }
}
