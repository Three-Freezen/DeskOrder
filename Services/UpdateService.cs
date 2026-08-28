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
    internal const string SetupAssetName = "DeskOrder-win-Setup.exe";
    internal static readonly string TempSetupPath = Path.Combine(Path.GetTempPath(), SetupAssetName);

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
    public void ApplyAndRestart() => GetChannel()?.ApplyAndRestart();

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
    private string? _downloadUrl;
    private string? _pendingVersion;

    private static HttpClient CreateHttp()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
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

        using var resp = await Http.GetAsync(UpdateService.ReleasesApiUrl, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
        if (!IsNewer(tag)) return null;

        foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != UpdateService.SetupAssetName) continue;
            _downloadUrl = asset.GetProperty("browser_download_url").GetString();
            return new UpdateCheckResult(_pendingVersion!, UpdateService.DefaultRepoUrl + "/releases/latest");
        }
        throw new InvalidOperationException($"Release {tag} 缺少安装包资产 {UpdateService.SetupAssetName}");
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
        using var resp = await Http.GetAsync(_downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? -1;
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = new FileStream(UpdateService.TempSetupPath, FileMode.Create, FileAccess.Write, FileShare.None);
        var buf = new byte[81920];
        long read = 0;
        int n;
        while ((n = await src.ReadAsync(buf, ct)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n), ct);
            read += n;
            if (total > 0) progress.Report((int)(read * 100 / total));
        }
        progress.Report(100);
    }

    public void ApplyAndRestart()
    {
        if (!File.Exists(UpdateService.TempSetupPath))
            throw new InvalidOperationException("No installer downloaded yet");
        // /SILENT：无向导仅进度；/DIR= 原安装路径；Inno CloseApplications+AppMutex
        // 会等本进程退出后接管；[Run] 的静默项装完自动重启应用。
        Process.Start(new ProcessStartInfo(UpdateService.TempSetupPath)
        {
            Arguments = $"/SILENT /DIR=\"{AppContext.BaseDirectory.TrimEnd('\\')}\"",
            UseShellExecute = true,
        });
        System.Windows.Application.Current.Shutdown();
    }
}
