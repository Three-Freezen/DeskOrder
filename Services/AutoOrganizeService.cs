using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using DesktopZones.Models;

namespace DesktopZones.Services;

/// <summary>自动整理服务（单例）。维护每分区的 FileSystemWatcher，
/// 命中后调 ZoneManager.AddItem。订阅 ZoneManager 事件自动同步 watcher 集合。</summary>
public sealed class AutoOrganizeService : IDisposable
{
    public static AutoOrganizeService Instance { get; } = new();

    ZoneManager? _zoneManager;
    readonly Dictionary<Guid, ZoneWatcher> _watchers = new();
    // 已提醒过的 (zoneId → "path\u001freason")，避免 ZonesChanged 高频触发时反复弹同一个 toast。
    readonly Dictionary<Guid, string> _lastFailureNotice = new();
    readonly object _lock = new();
    bool _disposed;

    AutoOrganizeService() { }

    /// <summary>由 App.OnStartup 注入；watcher 命中后走共享的 ZoneManager.AddItem 入口。</summary>
    public void Initialize(ZoneManager zoneManager) => _zoneManager = zoneManager;

    /// <summary>按当前分区集合同步 watcher（新增/删除/字段变更后调用；幂等）。</summary>
    public void SyncAll(IEnumerable<Zone> zones)
    {
        if (_disposed) return;
        lock (_lock)
        {
            var liveIds = new HashSet<Guid>();
            foreach (var z in zones)
            {
                liveIds.Add(z.Id);
                AttachZoneCore(z);
            }
            foreach (var id in _watchers.Keys.Where(id => !liveIds.Contains(id)).ToList())
                DetachZoneCore(id);
        }
    }

    /// <summary>挂载/重建某分区的 watcher（idempotent）。</summary>
    public void AttachZone(Zone z)
    {
        if (_disposed) return;
        lock (_lock) AttachZoneCore(z);
    }

    /// <summary>释放某分区的 watcher。</summary>
    public void DetachZone(Guid zoneId)
    {
        if (_disposed) return;
        lock (_lock) DetachZoneCore(zoneId);
    }

    void AttachZoneCore(Zone z)
    {
        // 监听开关关闭 → 不管有没有规则都暂停 watcher（保留规则，下次勾选即可恢复）。
        if (!z.AutoOrganizeWatching || !z.AutoOrganizeEnabled)
        {
            DetachZoneCore(z.Id);
            return;
        }
        if (string.IsNullOrWhiteSpace(z.AutoOrganizeWatchPath)) { DetachZoneCore(z.Id); return; }
        if (!Directory.Exists(z.AutoOrganizeWatchPath))
        {
            DetachZoneCore(z.Id);
            NotifyFailureOnce(z, PathMissingReason());
            return;
        }

        // 配置未变化时跳过重建，避免每次 ZonesChanged（拖拽、加图标等）都拆建 watcher。
        if (_watchers.TryGetValue(z.Id, out var existing) && existing.MatchesConfig(z))
        {
            _lastFailureNotice.Remove(z.Id);
            return;
        }

        DetachZoneCore(z.Id);
        try
        {
            _watchers[z.Id] = new ZoneWatcher(this, z);
            _lastFailureNotice.Remove(z.Id);
        }
        catch (Exception ex)
        {
            NotifyFailureOnce(z, ex.Message);
        }
    }

    void DetachZoneCore(Guid zoneId)
    {
        if (_watchers.Remove(zoneId, out var w)) w.Dispose();
    }

    /// <summary>仅在当前 watcher 就是 <paramref name="w"/> 时卸载（Watcher.Error 异步
    /// 触发时可能已有新一代 watcher 接管同一 zone）。</summary>
    void DetachIfCurrent(ZoneWatcher w)
    {
        lock (_lock)
        {
            if (_watchers.TryGetValue(w.Zone.Id, out var cur) && ReferenceEquals(cur, w))
                DetachZoneCore(w.Zone.Id);
        }
    }

    /// <summary>手动扫描某分区路径下的现有文件，命中后导入。
    /// 磁盘枚举 + 匹配在后台线程跑，AddItem/Notify 回 UI 线程（调用方 await 后
    /// 已在 UI SynchronizationContext 上继续）。</summary>
    public async Task<int> ScanExistingAsync(Zone z)
    {
        if (string.IsNullOrWhiteSpace(z.AutoOrganizeWatchPath)
            || !Directory.Exists(z.AutoOrganizeWatchPath))
            return 0;

        var matches = await Task.Run(() => Directory.EnumerateFiles(
                z.AutoOrganizeWatchPath, "*", SearchOption.AllDirectories)
            .Where(p => Matches(z, p))
            .ToList());

        int added = 0;
        foreach (var path in matches)
        {
            if (TryAddItem(z, path)) added++;
        }
        return added;
    }

    /// <summary>匹配函数（纯函数，便于测试与未来扩展）。
    /// 子开关（ExtEnabled / NameEnabled）为 false 时，对应规则列表不参与匹配（但内容保留）。</summary>
    public static bool Matches(Zone z, string filePath)
    {
        var ext = Path.GetExtension(filePath)?.ToLowerInvariant() ?? "";
        var name = Path.GetFileName(filePath);

        // 子开关控制：取消勾选 = 该类规则不参与匹配，但规则列表不删除
        bool extActive = z.AutoOrganizeExtEnabled && z.AutoOrganizeExtensions.Count > 0;
        bool nameActive = z.AutoOrganizeNameEnabled && z.AutoOrganizeNameTokens.Count > 0;
        if (!extActive && !nameActive) return false;

        bool extHit = extActive && z.AutoOrganizeExtensions
            .Any(t => string.Equals(t, ext, StringComparison.OrdinalIgnoreCase));
        bool nameHit = nameActive && z.AutoOrganizeNameTokens
            .Any(t => name.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0);

        if (extActive && nameActive) return extHit && nameHit;
        if (extActive) return extHit;
        return nameHit;
    }

    /// <summary>通过 ZoneManager 添加一项（内部已去重 + SaveConfig + Notify）。</summary>
    bool TryAddItem(Zone z, string path) => _zoneManager?.AddItem(z.Id, path) ?? false;

    static string PathMissingReason() =>
        LocalizationService.Instance["AutoOrganize.WatcherPathMissing"];

    void NotifyFailureOnce(Zone z, string reason)
    {
        var key = $"{z.AutoOrganizeWatchPath}\u001f{reason}";
        if (_lastFailureNotice.TryGetValue(z.Id, out var prev) && prev == key) return;
        _lastFailureNotice[z.Id] = key;
        ShowWatcherFailed(z, reason);
    }

    void ShowWatcherFailed(Zone z, string reason)
    {
        var msg = string.Format(System.Globalization.CultureInfo.InvariantCulture,
            LocalizationService.Instance["ZoneProp.AutoOrganize.WatcherFailed"], reason);
        RunOnUi(() =>
        {
            if (App.Notify != null)
                App.Notify(LocalizationService.Instance["ZoneProp.Section.AutoOrganize"], $"{z.Name}: {msg}");
            else
                MessageBox.Show($"{z.Name}\n{msg}", LocalizationService.Instance["ZoneProp.Section.AutoOrganize"], MessageBoxButton.OK, MessageBoxImage.Warning);
        });
    }

    static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) { action(); return; }
        dispatcher.Invoke(action);
    }

    public void Dispose()
    {
        _disposed = true;
        lock (_lock)
        {
            foreach (var w in _watchers.Values) w.Dispose();
            _watchers.Clear();
        }
    }

    // ── Per-zone watcher ──

    sealed class ZoneWatcher : IDisposable
    {
        readonly AutoOrganizeService _owner;
        readonly FileSystemWatcher _fsw;
        readonly Dictionary<string, DateTime> _recent = new(StringComparer.OrdinalIgnoreCase);
        readonly object _lock = new();
        static readonly TimeSpan ThrottleSpan = TimeSpan.FromMilliseconds(500);

        // 本 watcher 对应的配置快照，用于跳过无变化重建。
        readonly string _path;
        readonly bool _extEnabled;
        readonly bool _nameEnabled;
        readonly int _extHash;
        readonly int _tokenHash;

        public Zone Zone { get; }

        public ZoneWatcher(AutoOrganizeService owner, Zone z)
        {
            _owner = owner;
            Zone = z;
            _path = z.AutoOrganizeWatchPath ?? "";
            _extEnabled = z.AutoOrganizeExtEnabled;
            _nameEnabled = z.AutoOrganizeNameEnabled;
            _extHash = HashSet(z.AutoOrganizeExtensions);
            _tokenHash = HashSet(z.AutoOrganizeNameTokens);

            _fsw = new FileSystemWatcher(_path)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                EnableRaisingEvents = true,
                InternalBufferSize = 8192,
            };
            _fsw.Created += OnEvent;
            _fsw.Renamed += OnRenamed;
            _fsw.Error += OnError;
        }

        public bool MatchesConfig(Zone z) =>
            string.Equals(_path, z.AutoOrganizeWatchPath ?? "", StringComparison.OrdinalIgnoreCase)
            && z.AutoOrganizeWatching
            && z.AutoOrganizeEnabled
            && _extEnabled == z.AutoOrganizeExtEnabled
            && _nameEnabled == z.AutoOrganizeNameEnabled
            && _extHash == HashSet(z.AutoOrganizeExtensions)
            && _tokenHash == HashSet(z.AutoOrganizeNameTokens);

        static int HashSet(IEnumerable<string> values)
        {
            unchecked
            {
                int hash = 17;
                foreach (var v in values.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    hash = hash * 31 + StringComparer.OrdinalIgnoreCase.GetHashCode(v ?? "");
                return hash;
            }
        }

        void OnEvent(object sender, FileSystemEventArgs e) => Handle(e.FullPath);
        void OnRenamed(object sender, RenamedEventArgs e) => Handle(e.FullPath);

        void Handle(string path)
        {
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                if (_recent.TryGetValue(path, out var t) && now - t < ThrottleSpan) return;
                _recent[path] = now;
                if (_recent.Count > 100) PurgeOld(now);
            }
            if (!Matches(Zone, path)) return;
            RunOnUi(() => _owner.TryAddItem(Zone, path));
        }

        void PurgeOld(DateTime now)
        {
            var stale = _recent.Where(kv => now - kv.Value > TimeSpan.FromSeconds(5))
                .Select(kv => kv.Key).ToList();
            foreach (var k in stale) _recent.Remove(k);
        }

        void OnError(object sender, ErrorEventArgs e)
        {
            _owner.DetachIfCurrent(this);
            _owner.ShowWatcherFailed(Zone, e.GetException()?.Message ?? "unknown");
        }

        public void Dispose()
        {
            _fsw.Created -= OnEvent;
            _fsw.Renamed -= OnRenamed;
            _fsw.Error -= OnError;
            _fsw.EnableRaisingEvents = false;
            _fsw.Dispose();
        }
    }
}
