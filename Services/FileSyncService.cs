using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using DesktopZones.Models;

namespace DesktopZones.Services;

/// <summary>
/// 逆向同步服务（单例）。定期扫描所有分区的图标项，若原文件/文件夹已不存在
/// 则自动移除对应图标项（与 AutoOrganize 互补：AutoOrganize 发现新文件 → 添加图标，
/// FileSyncService 发现文件消失 → 删除图标）。
///
/// 设计：5 秒轮询（比 FileSystemWatcher 更稳健——无 buffer 溢出 / 网络驱动器 /
/// 多 watcher 生命周期管理问题，3-5 秒延迟对用户无感知）。仅处理真实文件路径
/// （ItemType.Folder / Shortcut / Application / Document），跳过 ShellLocation / SubFolder。
/// </summary>
public sealed class FileSyncService : IDisposable
{
    public static FileSyncService Instance { get; } = new();

    readonly DispatcherTimer _timer;
    bool _enabled;
    bool _disposed;

    FileSyncService()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick += (_, _) => ScanAll();
    }

    /// <summary>由 App.OnStartup 注入配置初始值并启动/停止定时器。</summary>
    public void Initialize(bool enabled)
    {
        _enabled = enabled;
        if (_enabled) _timer.Start();
    }

    /// <summary>运行时开关（由 SettingsPage 调用）。</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            if (_enabled) _timer.Start();
            else _timer.Stop();
        }
    }

    /// <summary>立即执行一次扫描（外部可手动触发）。</summary>
    public void ScanNow() => ScanAll();

    void ScanAll()
    {
        if (!_enabled || _disposed) return;
        var app = Application.Current;
        var zoneManager = app is App a ? a.ZoneManager : null;
        if (zoneManager == null) return;

        int totalRemoved = 0;
        // 收集需要删除的项（在遍历期间不修改集合）。
        var toRemove = new List<(Zone zone, List<ZoneItem> items)>();

        foreach (var zone in zoneManager.Zones)
        {
            var stale = new List<ZoneItem>();
            foreach (var item in zone.Items)
            {
                // 仅检查真实文件路径项。
                if (item.Type is ItemType.ShellLocation or ItemType.SubFolder) continue;
                if (string.IsNullOrWhiteSpace(item.TargetPath)) continue;

                // 纯虚拟 shell 对象（::{GUID}）没有文件系统路径。
                if (item.TargetPath.StartsWith("::")) continue;

                try
                {
                    if (!PathExists(item.TargetPath))
                        stale.Add(item);
                }
                catch
                {
                    // 路径格式异常（如非法字符）→ 视为不存在。
                    stale.Add(item);
                }
            }
            if (stale.Count > 0)
                toRemove.Add((zone, stale));
        }

        foreach (var (zone, items) in toRemove)
        {
            foreach (var item in items)
                zone.Items.Remove(item);
            totalRemoved += items.Count;
        }

        if (totalRemoved > 0)
        {
            zoneManager.SaveConfig();
            zoneManager.NotifyChanged();
        }
    }

    /// <summary>检查文件或文件夹是否存在（避免抛出异常中断扫描）。</summary>
    static bool PathExists(string path)
    {
        return File.Exists(path) || Directory.Exists(path);
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Stop();
    }
}
