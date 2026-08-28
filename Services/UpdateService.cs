using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

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
/// 更新渠道抽象。GitHub 渠道由 Velopack 实现；微软商店渠道（打包 MSIX 后运行）
/// 必须走系统 StoreContext API，未来在此接口下补一个实现即可，UI 层零改动。
/// </summary>
public interface IUpdateChannel
{
    /// <summary>检查新版本；返回 null 表示已是最新。网络失败抛异常。</summary>
    Task<UpdateCheckResult?> CheckAsync(CancellationToken ct);

    /// <summary>下载并暂存已检查到的版本（进度 0-100）。</summary>
    Task DownloadAsync(IProgress<int> progress, CancellationToken ct);

    /// <summary>应用已暂存的更新并重启本进程（成功则不返回）。需先 Check + Download。</summary>
    void ApplyAndRestart();
}

public sealed class UpdateService
{
    public const string DefaultRepoUrl = "https://github.com/Three-Freezen/DeskOrder";
    private const string ReleasesUrl = DefaultRepoUrl + "/releases/latest";

    /// <summary>运行时覆盖更新源（--update-source=本地目录|URL）。
    /// 本地目录用于端到端测试；URL 用于镜像加速，均为 Velopack 支持的源。</summary>
    public static string? SourceOverride { get; set; }

    public static bool IsRunningPackaged { get; } = DetectPackaged();

    /// <summary>本环境是否支持应用内更新（懒建渠道判定）：非商店包 && Velopack 已安装（dotnet run 开发目录运行时为否）。</summary>
    public bool InAppUpdateSupported => GetChannel() != null;

    /// <summary>状态变化通知。已在 UI 线程上触发，订阅方无需自行 marshal。</summary>
    public event Action? StateChanged;

    public UpdateState State { get; private set; } = UpdateState.Idle;
    public string? NewVersion { get; private set; }
    public string? ErrorText { get; private set; }
    public int ProgressPercent { get; private set; }
    public string ReleaseUrl => ReleasesUrl;

    private readonly ConfigService _configService;
    private IUpdateChannel? _channel;
    private bool _veloUnavailable;
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
            ErrorText = ex.Message;
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
            ErrorText = ex.Message;
            SetState(UpdateState.Failed);
        }
    }

    /// <summary>应用已暂存的更新并重启进程（Velopack 拉起安装器完成换装）。失败抛异常。</summary>
    public void ApplyAndRestart() => GetChannel()?.ApplyAndRestart();

    /// <summary>启动后台检查：环境不支持 / 开关关闭 / 24 小时内查过 → 直接返回。
    /// 发现新版本发托盘气泡；不自动下载（交互定为「提示后更新」）。</summary>
    public async Task AutoCheckIfDueAsync()
    {
        try
        {
            if (!InAppUpdateSupported) return;            var cfg = _configService.Load();
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
        if (_veloUnavailable) return null;
        if (IsRunningPackaged) return null;   // 商店渠道占位：未来接 StoreContext 实现
        try
        {
            // UpdateManager.IsInstalled 是实例属性：先建渠道再判定（开发目录运行=未安装）。
            var channel = new VelopackChannel(CreateSource());
            if (!channel.IsInstalled) { _veloUnavailable = true; return null; }
            _channel = channel;
            return _channel;
        }
        catch
        {
            _veloUnavailable = true;          // 定位器异常（极端环境）视同不支持
            return null;
        }
    }

    private static IUpdateSource CreateSource()
    {
        var o = SourceOverride;
        if (!string.IsNullOrWhiteSpace(o))
        {
            if (Directory.Exists(o)) return new SimpleFileSource(new DirectoryInfo(o));
            if (o.Contains("github.com", StringComparison.OrdinalIgnoreCase))
                return new GithubSource(o, null, false);
            return new SimpleWebSource(o);
        }
        return new GithubSource(DefaultRepoUrl, null, false);
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

/// <summary>GitHub Releases 渠道（Velopack）：匿名访问公开仓库，自动增量更新，校验内置。</summary>
file sealed class VelopackChannel : IUpdateChannel
{
    private readonly Velopack.UpdateManager _mgr;
    private UpdateInfo? _pending;

    public VelopackChannel(IUpdateSource source) => _mgr = new Velopack.UpdateManager(source);

    /// <summary>Velopack 定位到安装信息即视为已安装（exe 旁有 Update.exe / 包元数据）。</summary>
    public bool IsInstalled => _mgr.IsInstalled;

    public async Task<UpdateCheckResult?> CheckAsync(CancellationToken ct)
    {
        var info = await _mgr.CheckForUpdatesAsync();
        if (info == null) return null;
        _pending = info;
        return new UpdateCheckResult(info.TargetFullRelease.Version.ToString(), null);
    }

    public async Task DownloadAsync(IProgress<int> progress, CancellationToken ct)
    {
        var pending = _pending ?? throw new InvalidOperationException("No update checked yet");
        await _mgr.DownloadUpdatesAsync(pending, p => progress.Report(p), ct);
    }

    public void ApplyAndRestart()
    {
        var pending = _pending ?? throw new InvalidOperationException("No update downloaded yet");
        _mgr.ApplyUpdatesAndRestart(pending.TargetFullRelease);
    }
}
