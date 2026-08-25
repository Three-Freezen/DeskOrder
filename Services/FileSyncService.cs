using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using DesktopZones.Models;

namespace DesktopZones.Services;

/// <summary>
/// 逆向同步服务（单例）。事件驱动——为每个分区图标的父目录注册
/// FileSystemWatcher，监听文件/文件夹的删除与重命名；命中后精确移除
/// 对应图标项（与 AutoOrganize 互补：AutoOrganize 发现新文件→添加图标，
/// FileSyncService 发现文件消失→删除图标）。
///
/// 设计要点：
///   - 事件驱动，空闲时零 CPU（对比轮询方案）。
///   - 每个父目录仅一个 watcher（多图标共享同一目录时不重复创建）。
///   - 防抖 500ms 合并批量删除（如清空文件夹时一次性移除所有对应图标）。
///   - 订阅 ZonesChanged 自动重建 watcher 集合（分区增删、图标增删时同步）。
///   - 仅处理真实文件路径项（Folder/Shortcut/Application/Document），
///     跳过 ShellLocation / SubFolder / ::{GUID} 虚拟对象。
/// </summary>
public sealed class FileSyncService : IDisposable
{
    public static FileSyncService Instance { get; } = new();

    ZoneManager? _zoneManager;
    readonly Dictionary<string, DirWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    readonly DispatcherTimer _debounce;
    readonly HashSet<string> _dirtyDirs = new(StringComparer.OrdinalIgnoreCase);
    readonly object _lock = new();
    bool _enabled;
    bool _disposed;

    FileSyncService()
    {
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _debounce.Tick += (_, _) => ProcessDirty();
    }

    /// <summary>由 App.OnStartup 注入；订阅 ZonesChanged 自动同步 watcher 集合。</summary>
    public void Initialize(ZoneManager zoneManager, bool enabled)
    {
        _zoneManager = zoneManager;
        _enabled = enabled;
        if (_enabled)
        {
            _zoneManager.ZonesChanged += OnZonesChanged;
            RebuildWatchers();
        }
    }

    /// <summary>运行时开关（由 SettingsPage 调用）。</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            if (_enabled)
            {
                _zoneManager ??= (Application.Current as App)?.ZoneManager;
                if (_zoneManager != null)
                {
                    _zoneManager.ZonesChanged += OnZonesChanged;
                    RebuildWatchers();
                }
            }
            else
            {
                if (_zoneManager != null)
                    _zoneManager.ZonesChanged -= OnZonesChanged;
                ClearWatchers();
            }
        }
    }

    void OnZonesChanged()
    {
        if (!_enabled || _disposed) return;
        RebuildWatchers();
    }

    /// <summary>重建 watcher 集合：收集当前所有分区图标的父目录，创建缺失的 watcher，
    /// 释放不再需要的 watcher（幂等，高频调用无副作用）。</summary>
    void RebuildWatchers()
    {
        if (_zoneManager == null) return;

        // 收集当前所有需要监听的父目录。
        var needed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var zone in _zoneManager.Zones)
        {
            foreach (var item in zone.Items)
            {
                if (!ShouldWatch(item)) continue;
                var dir = GetParentDir(item.TargetPath);
                if (dir != null) needed.Add(dir);
            }
        }

        // 移除不再需要的 watcher。
        foreach (var dir in _watchers.Keys.Where(d => !needed.Contains(d)).ToList())
        {
            _watchers[dir].Dispose();
            _watchers.Remove(dir);
        }

        // 创建缺失的 watcher。
        foreach (var dir in needed)
        {
            if (_watchers.ContainsKey(dir)) continue;
            if (!Directory.Exists(dir)) continue;
            try
            {
                _watchers[dir] = new DirWatcher(dir, OnFileEvent, OnWatcherError);
            }
            catch
            {
                // 无法创建 watcher（权限不足等）→ 跳过该目录。
            }
        }
    }

    /// <summary>释放所有 watcher。</summary>
    void ClearWatchers()
    {
        foreach (var w in _watchers.Values) w.Dispose();
        _watchers.Clear();
        _dirtyDirs.Clear();
        _debounce.Stop();
    }

    /// <summary>FileSystemWatcher 回调（线程池线程）：标记目录为脏，启动防抖定时器。</summary>
    void OnFileEvent(string directory)
    {
        lock (_lock)
        {
            _dirtyDirs.Add(directory);
        }
        // DispatcherTimer 自动 marshal 到 UI 线程。
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _debounce.Stop();
            _debounce.Start();
        });
    }

    /// <summary>Watcher 错误回调：移除该 watcher，下次 RebuildWatchers 会重建。</summary>
    void OnWatcherError(string directory)
    {
        lock (_lock)
        {
            if (_watchers.Remove(directory, out var w)) w.Dispose();
        }
    }

    /// <summary>防抖到期：检查所有脏目录下的图标项，移除已失效的。</summary>
    void ProcessDirty()
    {
        _debounce.Stop();
        if (!_enabled || _disposed || _zoneManager == null) return;

        List<string> dirs;
        lock (_lock)
        {
            if (_dirtyDirs.Count == 0) return;
            dirs = _dirtyDirs.ToList();
            _dirtyDirs.Clear();
        }

        var toRemove = new List<(Zone zone, ZoneItem item)>();

        foreach (var zone in _zoneManager.Zones)
        {
            foreach (var item in zone.Items)
            {
                if (!ShouldWatch(item)) continue;
                var dir = GetParentDir(item.TargetPath);
                if (dir == null || !dirs.Contains(dir)) continue;

                try
                {
                    if (!PathExists(item.TargetPath))
                        toRemove.Add((zone, item));
                }
                catch
                {
                    toRemove.Add((zone, item));
                }
            }
        }

        foreach (var (zone, item) in toRemove)
            zone.Items.Remove(item);

        if (toRemove.Count > 0)
        {
            _zoneManager.SaveConfig();
            _zoneManager.NotifyChanged();
            // 删除操作会触发 ZonesChanged → RebuildWatchers，自动清理已失效的 watcher。
        }
    }

    static bool ShouldWatch(ZoneItem item)
    {
        if (item.Type is ItemType.ShellLocation or ItemType.SubFolder) return false;
        if (string.IsNullOrWhiteSpace(item.TargetPath)) return false;
        if (item.TargetPath.StartsWith("::")) return false;
        return true;
    }

    /// <summary>获取路径的父目录（兼容 UNC / 根目录等边界情况）。</summary>
    static string? GetParentDir(string path)
    {
        try
        {
            // Path.GetDirectoryName 对根目录（如 "C:\"）返回 null。
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) return dir;
            // 根目录本身作为父目录（如网络驱动器根）。
            if (Path.IsPathRooted(path)) return Path.GetPathRoot(path);
        }
        catch { }
        return null;
    }

    static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);

    public void Dispose()
    {
        _disposed = true;
        if (_zoneManager != null)
            _zoneManager.ZonesChanged -= OnZonesChanged;
        ClearWatchers();
    }

    // ── Per-directory watcher ──

    sealed class DirWatcher : IDisposable
    {
        readonly FileSystemWatcher _fsw;
        readonly Action<string> _onEvent;
        readonly Action<string> _onError;
        readonly string _directory;

        public DirWatcher(string directory, Action<string> onEvent, Action<string> onError)
        {
            _directory = directory;
            _onEvent = onEvent;
            _onError = onError;

            _fsw = new FileSystemWatcher(directory)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                EnableRaisingEvents = true,
                InternalBufferSize = 8192,
            };
            _fsw.Deleted += OnChanged;
            _fsw.Renamed += OnRenamed;
            _fsw.Error += OnError;
        }

        void OnChanged(object sender, FileSystemEventArgs e) => _onEvent(_directory);
        void OnRenamed(object sender, RenamedEventArgs e) => _onEvent(_directory);

        void OnError(object sender, ErrorEventArgs e)
        {
            _fsw.EnableRaisingEvents = false;
            _onError(_directory);
        }

        public void Dispose()
        {
            _fsw.Deleted -= OnChanged;
            _fsw.Renamed -= OnRenamed;
            _fsw.Error -= OnError;
            _fsw.EnableRaisingEvents = false;
            _fsw.Dispose();
        }
    }
}
