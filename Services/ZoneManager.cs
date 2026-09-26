using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows.Threading;
using DesktopZones.Helpers;
using DesktopZones.Models;
using DesktopZones.Views;
using DesktopZones.Views.Components;
using DesktopZones.ViewModels;

namespace DesktopZones.Services;

public class ZoneManager
{
    private readonly ConfigService _configService;
    private readonly Dictionary<Guid, ZoneWindow> _zoneWindows = new();
    private AppConfig _config;

    // Guard flag to prevent re-entrant batch operations
    private bool _isBatchOperation;

    /// <summary>批量期间是否有窗口状态变化需要收尾时统一通知(见 EndBatch)。</summary>
    private bool _batchNotifyPending;

    // ponytail: debounce SaveConfig calls during drag — multiple ScheduleSaveConfig()
    // within 1s coalesce into one disk write. Timer fires on the UI thread (DispatcherTimer
    // contract), so no lock needed; each Tick stops + nulls the timer so the next Schedule
    // creates a fresh one.
    private DispatcherTimer? _saveDebounceTimer;

    public ObservableCollection<Zone> Zones { get; } = new();

    public event Action? ZonesChanged;
    /// <summary>Fires when a zone's visibility changes (show/hide/close). Args: zoneId, isVisible</summary>
    public event Action<Guid, bool>? ZoneVisibilityChanged;
    /// <summary>Fires when a zone's lock state changes. Args: zoneId (string), isLocked.</summary>
    public event Action<string, bool>? LockChanged;

    /// <summary>Manually fire ZoneVisibilityChanged (for ZoneWindow internal state changes).</summary>
    public void FireZoneVisibilityChanged(Guid zoneId, bool isVisible)
        => ZoneVisibilityChanged?.Invoke(zoneId, isVisible);

    public ZoneManager(ConfigService configService)
    {
        _configService = configService;
        _config = configService.Load();
    }

    /// <summary>批量操作收尾:统一写盘 + 统一通知(见 <see cref="ShowZone"/> 里的说明)。</summary>
    void EndBatch(bool save)
    {
        _isBatchOperation = false;
        if (save) SaveConfig();
        if (_batchNotifyPending)
        {
            _batchNotifyPending = false;
            ZonesChanged?.Invoke();
        }
    }

    public void Initialize()
    {
        bool anyNormalized = false;
        bool anyResolved = false;
        bool anyShown = false;
        // ponytail 2026-09-26(审计修订): 整个初始化只写一次盘、只通知一次。
        // 原来每个可见分区都会在 ShowZone 里 SaveConfig() + ZonesChanged?.Invoke() ——
        // N 个分区 = 首帧之前 N 次整份 config 序列化写盘 + N 次 FileSystemWatcher 全量重建
        // + N 次自动整理扫描,全部压在启动路径上(而本方法末尾本来就还有一次统一保存)。
        _isBatchOperation = true;
        try
        {
            foreach (var zone in _config.Zones)
            {
                Zones.Add(zone);
                // 面板对齐迁移：旧默认网格 56 → 新 80×80（间距 88），与面板卡片一致。
                if (zone.GridSize == 56)
                {
                    zone.GridSize = 80;
                    anyNormalized = true;
                    // 仅在旧网格迁移时重排到新间距一次；之后每次启动不再自动重排/居中，
                    // 避免重新打开应用后图标又偏移（与 ZoneWindow.OnSize 的修复一致）。
                    if (ZoneLayout.NormalizeZone(zone)) anyNormalized = true;
                }
                // Migrate legacy shortcuts: re-associate imported .lnk items with their real
                // targets AND keep the shortcut's custom icon location, so icons render
                // without the link-arrow overlay and identical to the desktop (the "二次关联" fix).
                foreach (var it in zone.Items)
                {
                    if (it.Type == ItemType.Shortcut && ShortcutResolver.IsShortcut(it.TargetPath))
                    {
                        var (target, type, iconLoc) = ShortcutResolver.NormalizeItem(it.TargetPath, it.Type);
                        if (!string.Equals(target, it.TargetPath, StringComparison.OrdinalIgnoreCase))
                        {
                            it.TargetPath = target;
                            anyResolved = true;
                        }
                        if (type != it.Type) { it.Type = type; anyResolved = true; }
                        if (iconLoc != null && it.IconPath == null) { it.IconPath = iconLoc; anyResolved = true; }
                    }

                    // 系统项目修复:已知文件夹(文档/图片/音乐/视频等)的 "::{GUID}" 壳
                    // 无法被 shell 解析(打不开/空壳)— 迁移为真实文件夹路径。
                    if (it.Type == ItemType.ShellLocation)
                    {
                        var kfPath = ShellLocationResolver.ResolveKnownFolderPath(it.TargetPath);
                        if (kfPath != null)
                        {
                            it.TargetPath = kfPath;
                            it.Type = ItemType.Folder;
                            anyResolved = true;
                        }
                    }

                    // Recovery: file items already migrated (shortcut path lost) whose icon
                    // is missing a custom desktop-shortcut icon — e.g. 必剪.lnk points at
                    // BCUT.exe but renders BCUT_Deskpic.ico. Match the desktop shortcut by
                    // target path and adopt its icon location.
                    //
                    // ponytail 2026-09-26(审计修订): 只**收集**,不再在这里同步跑 —— 这条恢复
                    // 要对桌面上的每个 .lnk 做一次 WScript.Shell COM 解析。实测本机:桌面 23 个
                    // .lnk,每个 ResolveTarget 约 6.5ms(COM 就是慢),**跑一遍全表 149ms**;而
                    // 这条路径是"每个缺图标的条目各跑一遍全表",全部堵在首帧之前。
                    // 现在:① 整轮只建一次索引(COM 次数 = 快捷方式数,条目查询 O(1));
                    // ② 排到 Dispatcher 空闲时跑(见 RecoverMissingIcons)。
                    if (it.IconPath == null
                        && it.Type is ItemType.Application or ItemType.Shortcut
                        && !string.IsNullOrEmpty(it.TargetPath)
                        && File.Exists(it.TargetPath))
                        _iconRecoveryQueue.Add(it);
                }
                if (zone.IsVisible)
                {
                    ShowZone(zone);
                    anyShown = true;   // ShowZone 会置 IsVisible=true,批量期间由这里统一落盘
                }
            }
        }
        finally
        {
            EndBatch(anyNormalized || anyResolved || anyShown);
        }

        // 缺图标的恢复排在首帧之后(见 RecoverMissingIcons)
        if (_iconRecoveryQueue.Count > 0)
            (_dispatcher ??= System.Windows.Application.Current?.Dispatcher
                             ?? System.Windows.Threading.Dispatcher.CurrentDispatcher)
                .BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                    new Action(RecoverMissingIcons));
    }

    /// <summary>UI Dispatcher(见 <see cref="Initialize"/> 末尾的延迟恢复)。</summary>
    private System.Windows.Threading.Dispatcher? _dispatcher;

    /// <summary>缺自定义图标的条目(待恢复)。见 <see cref="RecoverMissingIcons"/>。</summary>
    private readonly List<ZoneItem> _iconRecoveryQueue = new();

    /// <summary>桌面 .lnk 的「解析目标 → 快捷方式路径」索引,整轮恢复只建一次。
    /// ponytail 2026-09-26(审计修订): 原来每个缺图标的条目都要把桌面 .lnk **全扫一遍**
    /// (每个文件一次 COM ResolveTarget);现在一次遍历建成索引,条目查询变 O(1)。</summary>
    private Dictionary<string, string>? _desktopShortcutIndex;

    /// <summary>把「缺自定义图标」的条目补回桌面快捷方式上的图标位置。
    ///
    /// ponytail 2026-09-26(审计修订): 从 <see cref="Initialize"/> 的同步路径里挪出来 ——
    /// 它对桌面上的每个 .lnk 做一次 WScript.Shell COM 解析(实测本机 23 个 .lnk × 6.5ms
    /// = **149ms 一遍全表**,而旧实现是每个缺图标的条目各跑一遍全表),全部堵在**首帧之前**。
    /// 现在:① 整轮只建一次索引(COM 次数 = 桌面快捷方式数);② 排到 Dispatcher 空闲时再跑,
    /// 启动不再等它;③ 补到的图标位置落盘一次并抛一次 ZonesChanged,分区/面板自己重取图标。
    /// 绝大多数情况下这里一条都不用补(图标位置第一次补到就落盘了),队列是空的。
    /// 注意:只扫 *.lnk —— 目标是 .url 之类的条目永远匹配不到,会每次启动白扫一遍(现在只是
    /// 空闲时的一遍,可接受;要根治得连 .url 一起解析,那是另一件事)。</summary>
    void RecoverMissingIcons()
    {
        if (_iconRecoveryQueue.Count == 0) return;
        _desktopShortcutIndex = null;   // 每轮重建(用户可能刚在桌面上加了快捷方式)
        int pending = _iconRecoveryQueue.Count, recovered = 0;
        foreach (var it in _iconRecoveryQueue)
        {
            var iconLoc = FindDesktopIcon(it.TargetPath);
            if (iconLoc != null) { it.IconPath = iconLoc; recovered++; }
        }
        _iconRecoveryQueue.Clear();
        if (recovered == 0) return;
        DzTrace.Log($"[ZoneManager] 首帧后补回自定义图标 {recovered} 个(待补 {pending} 个)");
        SaveConfig();
        ZonesChanged?.Invoke();
    }

    /// <summary>桌面 .lnk 索引(懒建一次)。</summary>
    private Dictionary<string, string> DesktopShortcutIndex()
    {
        if (_desktopShortcutIndex != null) return _desktopShortcutIndex;
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (Directory.Exists(desktop))
            {
                foreach (var lnk in Directory.GetFiles(desktop, "*.lnk"))
                {
                    var target = ShortcutResolver.ResolveTarget(lnk);
                    if (!string.IsNullOrEmpty(target) && !map.ContainsKey(target!)) map[target!] = lnk;
                }
            }
        }
        catch { /* desktop scan is best-effort */ }
        _desktopShortcutIndex = map;
        return map;
    }

    /// <summary>
    /// Look for a desktop .lnk whose resolved target equals <paramref name="target"/> and
    /// return its custom icon location. 走 <see cref="DesktopShortcutIndex"/> 的一次性索引。
    /// </summary>
    private string? FindDesktopIcon(string target)
    {
        try
        {
            return DesktopShortcutIndex().TryGetValue(target, out var lnk)
                ? ShortcutResolver.ResolveIconLocation(lnk)
                : null;
        }
        catch { return null; }
    }

    public Zone CreateZone(string name = "New Zone", double x = 200, double y = 200,
        double width = 400, double height = 300)
    {
        var zone = new Zone
        {
            Name = name,
            X = x,
            Y = y,
            Width = width,
            Height = height
        };

        _config.Zones.Add(zone);
        Zones.Add(zone);
        SaveConfig();
        ShowZone(zone);
        ZonesChanged?.Invoke();
        return zone;
    }

    /// <summary>Create a new SubFolder ZoneItem under <paramref name="owner"/>'s item list.
    /// ponytail: simplified placement — append at the last item's X+GridSize, or (0,0) when the
    /// owner is empty. Replace with a real "find empty cell" sweep when AutoArrange becomes
    /// aware of SubFolder footprints (out of scope here).</summary>
    public ZoneItem CreateSubfolder(Zone owner, string name)
    {
        // ponytail 2026-08-26: 占位走 ZoneLayout.FindFreeSpot,和正常 AddItem 同一路径,
        // 自动按方形格子(GridSize×GridSize)在当前已有 items 周围找空格子,
        // 而不是无脑追加到 last 后面导致挤到一起或越界。
        var (x, y) = ZoneLayout.FindFreeSpot(
            owner.Items, owner.Width, owner.Height, owner.GridSize, owner.GridSize);
        var sub = new ZoneItem(name, "", ItemType.SubFolder, x, y);
        owner.Items.Add(sub);
        SaveConfig();
        ZonesChanged?.Invoke();
        return sub;
    }

    /// <summary>Add a file/folder path to a zone — shared entry point for auto-organize.
    /// Uses the same normalization (ShortcutResolver) + collision-aware placement
    /// (ZoneLayout.FindFreeSpot) as the manual drag-drop import path. Dedups by
    /// TargetPath (case-insensitive) and returns whether an item was actually added.</summary>
    public bool AddItem(Guid zoneId, string path)
    {
        var zone = Zones.FirstOrDefault(z => z.Id == zoneId);
        if (zone == null) return false;
        if (zone.Items.Any(i => string.Equals(i.TargetPath, path, StringComparison.OrdinalIgnoreCase)))
            return false;

        var type = Directory.Exists(path)
            ? ItemType.Folder
            : Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".lnk" => ItemType.Shortcut,
                ".exe" => ItemType.Application,
                _ => ItemType.Shortcut,
            };
        var (target, normalizedType, iconLoc) = ShortcutResolver.NormalizeItem(path, type);
        var item = new ZoneItem(Path.GetFileNameWithoutExtension(path), target, normalizedType, 0, 0)
        {
            IconPath = iconLoc,
        };

        // 导入时就按原始路径 + 文件类型解析并缓存图标（渲染端 ZoneItemViewModel 直接命中
        // 共享缓存，避免懒解析在文件尚未写完时把 null 图标缓存成永久空白）。
        ShellIconService.Instance.GetIcon(item.TargetPath, item.Type, item.IconPath);

        double itemW = zone.GridSize;
        var (x, y) = ZoneLayout.FindFreeSpot(zone.Items, zone.Width, zone.Height, itemW, itemW);
        item.X = x;
        item.Y = y;

        zone.Items.Add(item);
        SaveConfig();
        NotifyChanged();
        return true;
    }

    /// <summary>Debounced SaveConfig — coalesces bursts (drag moves, batch edits) into a single
    /// disk write after 1s of quiet. Idempotent: each call resets the 1s window; the timer fires
    /// once, saves, then nulls itself so a later Schedule creates a fresh instance.</summary>
    public void ScheduleSaveConfig()
    {
        if (_saveDebounceTimer == null)
        {
            _saveDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _saveDebounceTimer.Tick += (_, _) =>
            {
                _saveDebounceTimer!.Stop();
                _saveDebounceTimer = null;
                SaveConfig();
            };
        }
        _saveDebounceTimer.Stop();
        _saveDebounceTimer.Start();
    }

    public void DeleteZone(Guid zoneId)
    {
        var toDelete = Zones.FirstOrDefault(z => z.Id == zoneId);
        if (toDelete == null) return;

        // ponytail: 删除前先同步关闭该分区与其子文件夹的样式设置界面(浮动 + 停靠),
        // 否则残留编辑器仍握着已删除实例,后续编辑会造出幽灵组件甚至抛异常。
        // 组合分区编辑器的关闭由 DisbandMergedGroup / RemoveFromMergedGroup 处理。
        PropertyWindowService.CloseEditorsFor(toDelete);
        foreach (var item in toDelete.Items.Where(i => i.Type == ItemType.SubFolder).ToList())
            PropertyWindowService.CloseEditorsFor(item);

        // If this zone is part of a merged group, disband it first
        if (toDelete.MergedGroupMembership.GroupId.HasValue)
        {
            if (toDelete.MergedGroupMembership.SubZoneIds.Count > 0)
                DisbandMergedGroup(toDelete.MergedGroupMembership.GroupId.Value);
            else
                RemoveFromMergedGroup(zoneId);
        }

        // Close and remove the window completely
        if (_zoneWindows.TryGetValue(zoneId, out var window))
        {
            window.Close();
            _zoneWindows.Remove(zoneId);
        }

        var zone = _config.Zones.FirstOrDefault(z => z.Id == zoneId);
        if (zone != null)
            _config.Zones.Remove(zone);

        var toRemove = Zones.FirstOrDefault(z => z.Id == zoneId);
        if (toRemove != null)
            Zones.Remove(toRemove);

        SaveConfig();
        ZonesChanged?.Invoke();
    }

    public void ShowZone(Zone zone, double waveDelayMs = 0)
    {
        // If this zone is a sub-zone of a merged group, show the master instead
        if (zone.MergedGroupMembership.GroupId.HasValue && zone.MergedGroupMembership.SubZoneIds.Count == 0)
        {
            var master = Zones.FirstOrDefault(z => z.MergedGroupMembership.GroupId == zone.MergedGroupMembership.GroupId && z.MergedGroupMembership.SubZoneIds.Count > 0);
            if (master != null)
            {
                master.IsVisible = true;
                ShowZone(master, waveDelayMs);
                // Try to set the master to show the requested sub-zone's items
                if (_zoneWindows.TryGetValue(master.Id, out var masterWin) && masterWin?.IsLoaded == true)
                {
                    var vm = masterWin.DataContext as ViewModels.ZoneViewModel;
                    if (vm != null) vm.SelectedSubZoneId = zone.Id;
                }
                return;
            }
        }

        // Set IsVisible BEFORE creating the window to prevent constructor from calling ApplyHidden()
        zone.IsVisible = true;

        if (_zoneWindows.ContainsKey(zone.Id))
        {
            _zoneWindows[zone.Id].ShowZone(waveDelayMs);
        }
        else
        {
            var window = new ZoneWindow(zone, this, ShellIconService.Instance);
            window.Show();
            _zoneWindows[zone.Id] = window;
            // ponytail: batch "Show All" wave — a freshly created window starts expanded
            // from the ctor; re-collapse it and play its own entrance animation at the
            // stagger slot so new windows join the cascade.
            if (waveDelayMs > 0) window.PlayEntranceAnimation(waveDelayMs);
        }
        if (_isBatchOperation)
        {
            // 批量期间(ShowAll / HideAll / Initialize)不各自写盘、不各自通知:
            // 收尾由 EndBatch 统一落盘 + 统一抛一次 ZonesChanged。
            _batchNotifyPending = true;
        }
        else
        {
            SaveConfig();
            ZonesChanged?.Invoke();
        }
        ZoneVisibilityChanged?.Invoke(zone.Id, true);
    }
    public void HideZone(Guid zoneId, double waveDelayMs = 0)
    {
        var zone = Zones.FirstOrDefault(z => z.Id == zoneId);
        if (_zoneWindows.TryGetValue(zoneId, out var window))
        {
            window.HideZone(waveDelayMs);
            // If EnableRestoreButton is false, remove window from dictionary (like FullHideZone)
            if (zone != null && !zone.EnableRestoreButton)
            {
                _zoneWindows.Remove(zoneId);
                if (waveDelayMs <= 0)
                {
                    // ponytail: 2026-08-23 — close the removed window instead of leaking it.
                    // A hidden-but-alive window keeps its HoverExpandBehavior poll timer and
                    // ZonesChanged/LockChanged handlers running, and its stale state could
                    // re-enable the DWM glass on the hidden HWND (ghost glass bug).
                    // The batch-wave path (waveDelayMs > 0) closes itself after its collapse
                    // animation finishes — closing now would kill the animation.
                    window.Close();
                }
            }
        }
        if (zone != null)
            zone.IsVisible = false;
        if (_isBatchOperation)
        {
            _batchNotifyPending = true;   // 见 ShowZone 同款说明
        }
        else
        {
            SaveConfig();
            ZonesChanged?.Invoke();
        }
        ZoneVisibilityChanged?.Invoke(zoneId, false);
    }

    public void ToggleZone(Guid zoneId)
    {
        var zone = Zones.FirstOrDefault(z => z.Id == zoneId);
        if (zone == null) return;

        if (zone.IsVisible)
            HideZone(zoneId);
        else
            ShowZone(zone);
    }

    public void ShowAll(bool staggered = true)
    {
        if (_isBatchOperation) return;
        _isBatchOperation = true;
        try
        {
            // ponytail: 2026-08-23 batch wave — sort by screen position (row-major)
            // and stagger each zone by BatchStaggerMs so "Show All" opens as a
            // left-to-right / top-to-bottom cascade; each zone plays its OWN
            // configured animation kind/speed/origin. staggered=false → 即时显示
            // (双击桌面切换路径用，不走级联动画)。
            int i = 0;
            foreach (var zone in Zones
                         .Where(z => !(z.MergedGroupMembership.GroupId.HasValue
                                       && z.MergedGroupMembership.SubZoneIds.Count == 0))
                         .OrderBy(z => z.Y).ThenBy(z => z.X))
            {
                // (i+1) 保证第一个分区也拿到 >0 的 waveDelay，走动画分支；
                // 旧实现第一个 delay=0 走 SnapToExpanded，导致总有一个分区不播动画。
                ShowZone(zone, staggered ? (i + 1) * HoverExpandBehavior.BatchStaggerMs : 0);
                i++;
            }
        }
        finally { EndBatch(save: true); }
    }

    public void HideAll()
    {
        if (_isBatchOperation) return;
        _isBatchOperation = true;
        try
        {
            // ponytail: batch wave — mirror of ShowAll: each zone collapses with its
            // own animation at its stagger slot (see ShowAll for the sort rationale).
            int i = 0;
            foreach (var zone in Zones.OrderBy(z => z.Y).ThenBy(z => z.X))
            {
                HideZone(zone.Id, i * HoverExpandBehavior.BatchStaggerMs);
                i++;
            }
        }
        finally { EndBatch(save: true); }
    }

    /// <summary>Fully close the zone window (no restore button).</summary>
    public void FullHideZone(Guid zoneId)
    {
        if (_zoneWindows.TryGetValue(zoneId, out var window))
        {
            var z = Zones.FirstOrDefault(x => x.Id == zoneId);
            if (z != null) { z.Width = window.Width; z.Height = window.FullModelHeight; z.X = window.Left; z.Y = window.Top; }
            window.Close();
            _zoneWindows.Remove(zoneId);
        }
        var zone = Zones.FirstOrDefault(z => z.Id == zoneId);
        if (zone != null) zone.IsVisible = false;
        SaveConfig();
        ZonesChanged?.Invoke();
        ZoneVisibilityChanged?.Invoke(zoneId, false);
    }

    /// <summary>Fully close all zone windows.</summary>
    public void FullHideAll()
    {
        if (_isBatchOperation) return;
        _isBatchOperation = true;
        try
        {
            foreach (var kv in _zoneWindows.ToList())
            {
                var z = Zones.FirstOrDefault(x => x.Id == kv.Key);
                if (z != null) { z.Width = kv.Value.Width; z.Height = kv.Value.FullModelHeight; z.X = kv.Value.Left; z.Y = kv.Value.Top; }
                kv.Value.Close();
            }
            _zoneWindows.Clear();
            foreach (var zone in Zones) zone.IsVisible = false;
            SaveConfig();
            ZonesChanged?.Invoke();
        }
        finally { _isBatchOperation = false; }
    }

    public bool IsZoneShown(Guid zoneId) => _zoneWindows.ContainsKey(zoneId);
    public ZoneWindow? GetZoneWindow(Guid zoneId) => _zoneWindows.TryGetValue(zoneId, out var w) ? w : null;
    public bool IsZoneMinimized(Guid zoneId) => _zoneWindows.TryGetValue(zoneId, out var w) && w.RestoreButton.Visibility == System.Windows.Visibility.Visible;

    public void ToggleAll()
    {
        bool anyVisible = Zones.Any(z => z.IsVisible);
        if (anyVisible)
            HideAll();
        else
            ShowAll();
    }

    public void UpdateZone(Zone updatedZone)
    {
        var zone = _config.Zones.FirstOrDefault(z => z.Id == updatedZone.Id);
        if (zone == null) return;

        var index = _config.Zones.IndexOf(zone);
        _config.Zones[index] = updatedZone;

        var listIndex = -1;
        for (int i = 0; i < Zones.Count; i++)
        {
            if (Zones[i].Id == updatedZone.Id)
            {
                listIndex = i;
                break;
            }
        }
        if (listIndex >= 0)
            Zones[listIndex] = updatedZone;

        // Refresh the window
        if (_zoneWindows.TryGetValue(updatedZone.Id, out var window))
        {
            window.RefreshZone(updatedZone);
            if (updatedZone.IsVisible)
                window.ShowZone();
            else
                window.HideZone();
        }
        else if (updatedZone.IsVisible)
        {
            ShowZone(updatedZone);
        }

        SaveConfig();
        ZonesChanged?.Invoke();
    }

    /// <summary>
    /// 分区的「改名」唯一出口 —— 分区窗口标题栏内联改名(Enter / 失焦)直接调这里。
    ///
    /// ponytail 2026-09-26: 旧实现是窗口自己 `_zone.Name = text; SaveConfig();` ——
    /// 缺了通知(见下),实测后果:
    ///   ① 设置界面(分区列表 / 属性面板头部 / 名称输入框 / 标签页)全都停在旧名上;
    ///   ② 用户看面板还显示旧名 → 顺手点「取消」→ PropertyPanel.CancelBtn_Click 会用
    ///      打开面板时的快照 CopyZoneFields(含 dst.Name = src.Name) 把模型名字**改回旧值**
    ///      (注释写着"不写盘",但模型已经变了,之后任何一次配置写入都会把旧名落盘)——
    ///      用户视角就是"标题栏改的名不持久化"。
    /// 现在:改模型 + 立即落盘 + 抛 ZonesChanged(所有设置界面都挂在这一个事件上),
    /// 不经过设置界面里的"二次应用"。
    ///
    /// 组合分区的标题栏显示的是 <see cref="MergedGroupMembership.DisplayName"/>(空则回落
    /// 主分区自己的 Name),所以同一个入口按「是不是组合分区」写不同字段 —— 与标题栏看到
    /// 的东西严格一致。
    /// </summary>
    /// <returns>真的改了返回 true(空串 / 与当前显示名相同都返回 false,不写盘不通知)。</returns>
    public bool RenameZone(Zone? zone, string? newName)
    {
        if (zone == null) return false;
        var name = (newName ?? "").Trim();
        if (name.Length == 0) return false;   // 空名 = 放弃改名(与窗口标题栏的历史行为一致)

        bool merged = zone.MergedGroupMembership.SubZoneIds.Count > 0;
        string current = merged
            ? (string.IsNullOrEmpty(zone.MergedGroupMembership.DisplayName)
                ? zone.Name : zone.MergedGroupMembership.DisplayName)
            : zone.Name;
        if (name == current) return false;

        if (merged) zone.MergedGroupMembership.DisplayName = name;
        else zone.Name = name;

        SaveConfig();      // 立即落盘
        NotifyChanged();   // 设置界面实时同步(列表行 / 属性面板头部 + 名称框 / 停靠标签页)
        return true;
    }

    public void SaveConfig() => ConfigSaver.SavePreservingPanelSettings(_configService, cfg =>
    {
        cfg.Zones = Zones.ToList();
    });

    /// <summary>Move a zone to a new position in the list (long-press drag reorder).
    /// ObservableCollection.Move fires CollectionChanged which the ItemsControl reflects
    /// directly; we deliberately skip ZonesChanged to avoid RefreshList rebuilding all
    /// rows mid-drag. <paramref name="newIndex"/> clamps to [0, Count-1] — drop-on-empty
    /// space below the last row maps to "after last" = position Count-1, same effect.</summary>
    public void MoveZone(Guid zoneId, int newIndex)
    {
        var zone = Zones.FirstOrDefault(z => z.Id == zoneId);
        if (zone == null) return;
        int oldIndex = Zones.IndexOf(zone);
        if (oldIndex < 0 || oldIndex == newIndex) return;
        if (newIndex < 0) newIndex = 0;
        if (newIndex > Zones.Count - 1) newIndex = Zones.Count - 1;
        Zones.Move(oldIndex, newIndex);
        SaveConfig();
    }

    /// <summary>Move a merged-group master relative to the other masters (merged-groups
    /// page drag reorder). The page lists only masters, but <see cref="Zones"/> stores
    /// every zone — reorder the full collection so the masters' relative order matches
    /// the drop and the filtered view persists across restarts. <paramref name="targetMasterIndex"/>
    /// is the final 0-based index among masters; clamps to [0, masterCount-1].</summary>
    public void MoveMergedGroupMaster(Guid masterId, int targetMasterIndex)
    {
        var master = Zones.FirstOrDefault(z => z.Id == masterId);
        if (master == null || master.MergedGroupMembership.SubZoneIds.Count == 0) return;

        var masters = Zones.Where(z => z.MergedGroupMembership.SubZoneIds.Count > 0).ToList();
        int oldMasterIdx = masters.IndexOf(master);
        if (oldMasterIdx < 0) return;
        if (targetMasterIndex < 0) targetMasterIndex = 0;
        if (targetMasterIndex > masters.Count - 1) targetMasterIndex = masters.Count - 1;
        if (oldMasterIdx == targetMasterIndex) return;

        int oldZoneIdx = Zones.IndexOf(master);
        Zones.RemoveAt(oldZoneIdx);
        var anchor = masters[targetMasterIndex];
        int insertIdx = Zones.IndexOf(anchor);
        // Downward move → land AFTER the anchor; upward → land BEFORE it.
        if (targetMasterIndex > oldMasterIdx) insertIdx++;
        Zones.Insert(insertIdx, master);
        SaveConfig();
    }

    public AppConfig GetConfig() => _config;

    /// <summary>Notify all listeners that zones have changed (for item-level changes).</summary>
    public void NotifyChanged() => ZonesChanged?.Invoke();

    public void UpdateConfig(AppConfig config)
    {
        _config = config;
        SaveConfig();
    }

    public void Shutdown()
    {
        foreach (var window in _zoneWindows.Values)
        {
            window.Close();
        }
        _zoneWindows.Clear();
    }

    // ── Zone Merge ──

    /// <summary>Merge zoneB into zoneA's group. If neither is merged, creates a new group.
    /// If zoneA is already a merged master, adds zoneB as a sub-zone.</summary>
    public Guid MergeZones(Guid zoneAId, Guid zoneBId)
    {
        var zoneA = Zones.FirstOrDefault(z => z.Id == zoneAId);
        var zoneB = Zones.FirstOrDefault(z => z.Id == zoneBId);
        if (zoneA == null || zoneB == null || zoneAId == zoneBId)
            return Guid.Empty;

        Guid groupId;
        Zone master;

        if (zoneA.MergedGroupMembership.GroupId.HasValue)
        {
            groupId = zoneA.MergedGroupMembership.GroupId.Value;
            master = Zones.FirstOrDefault(z => z.MergedGroupMembership.GroupId == groupId && z.MergedGroupMembership.SubZoneIds.Count > 0)
                     ?? zoneA;
        }
        else
        {
            groupId = Guid.NewGuid();
            master = zoneA;
            zoneA.MergedGroupMembership.GroupId = groupId;
        }

        // If zoneB is already in a merged group, remove it first
        if (zoneB.MergedGroupMembership.GroupId.HasValue)
            RemoveFromMergedGroup(zoneB.Id);

        zoneB.MergedGroupMembership.GroupId = groupId;
        master.MergedGroupMembership.SubZoneIds.Add(zoneB.Id);
        master.MergedGroupMembership.DisplayName = BuildMergedGroupName(master);
        zoneB.MergedGroupMembership.DisplayName = master.MergedGroupMembership.DisplayName;

        // Hide sub-zone window
        FullHideZone(zoneB.Id);

        // Refresh master window
        if (_zoneWindows.TryGetValue(master.Id, out var masterWin))
        {
            masterWin.RefreshZone(master);
        }

        SaveConfig();
        ZonesChanged?.Invoke();
        return groupId;
    }

    /// <summary>Disband all zones in a merged group.</summary>
    public void DisbandMergedGroup(Guid groupId)
    {
        var members = Zones.Where(z => z.MergedGroupMembership.GroupId == groupId).ToList();
        // ponytail: 解散前捕获组合分区编辑器目标 — MergedGroupTarget.GroupId 在
        // GroupId 清空后会回落成 Master.Id,TargetKey 变掉就关不到已打开的组合
        // 设置界面了,所以必须在这里(清空字段之前)就执行关闭。
        var groupTarget = members.FirstOrDefault(z => z.MergedGroupMembership.SubZoneIds.Count > 0)
            ?? members.FirstOrDefault();
        if (groupTarget != null)
            PropertyWindowService.CloseEditorsFor(MergedGroupTarget.For(groupTarget));
        // Close the master window first
        foreach (var z in members)
        {
            if (z.MergedGroupMembership.SubZoneIds.Count > 0 && _zoneWindows.TryGetValue(z.Id, out var win))
            {
                z.Width = win.Width; z.Height = win.FullModelHeight; z.X = win.Left; z.Y = win.Top;
                win.Close();
                _zoneWindows.Remove(z.Id);
            }
        }
        // Clear merge fields on all members
        foreach (var z in members)
        {
            z.MergedGroupMembership.GroupId = null;
            z.MergedGroupMembership.SubZoneIds.Clear();
            z.MergedGroupMembership.TabOrder.Clear();
            z.MergedGroupMembership.DisplayName = "";
            z.MergedGroupMembership.Icon = "";
        }
        // Re-show individual windows
        foreach (var z in members)
        {
            ShowZone(z);
        }
        SaveConfig();
        ZonesChanged?.Invoke();
    }

    /// <summary>Remove a single zone from its merged group.</summary>
    public void RemoveFromMergedGroup(Guid zoneId)
    {
        var zone = Zones.FirstOrDefault(z => z.Id == zoneId);
        if (zone == null || !zone.MergedGroupMembership.GroupId.HasValue) return;

        var groupId = zone.MergedGroupMembership.GroupId.Value;
        var master = Zones.FirstOrDefault(z => z.MergedGroupMembership.GroupId == groupId && z.MergedGroupMembership.SubZoneIds.Count > 0);
        if (master != null)
        {
            master.MergedGroupMembership.SubZoneIds.Remove(zoneId);
            master.MergedGroupMembership.TabOrder.Remove(zoneId);
            if (master.MergedGroupMembership.SubZoneIds.Count == 0)
            {
                // 组合解散 — 同步关闭组合分区设置界面(必须在清空 GroupId 之前,
                // 否则 MergedGroupTarget 的 TargetKey 会变掉)。
                PropertyWindowService.CloseEditorsFor(MergedGroupTarget.For(master));
                // Only master remains — clear its merge state
                master.MergedGroupMembership.GroupId = null;
                master.MergedGroupMembership.DisplayName = "";
                master.MergedGroupMembership.Icon = "";
                master.MergedGroupMembership.TabOrder.Clear();
                if (_zoneWindows.TryGetValue(master.Id, out var win))
                    win.RefreshZone(master);
            }
            else
            {
                master.MergedGroupMembership.DisplayName = BuildMergedGroupName(master);
                if (_zoneWindows.TryGetValue(master.Id, out var win))
                    win.RefreshZone(master);
            }
        }

        zone.MergedGroupMembership.GroupId = null;
        zone.MergedGroupMembership.DisplayName = "";
        zone.MergedGroupMembership.Icon = "";
        zone.MergedGroupMembership.TabOrder.Clear();

        SaveConfig();
        ZonesChanged?.Invoke();
    }

    /// <summary>
    /// Merge the dragged zone into the target zone's merged group (creating one when
    /// the target is standalone). The target stays the group master and its name comes
    /// first; the dragged zone is appended, and when the dragged zone is itself a group
    /// master its whole group is folded in (order preserved). Fires a single save/refresh.
    /// </summary>
    public Guid MergeZoneInto(Guid targetZoneId, Guid draggedZoneId)
    {
        var target = Zones.FirstOrDefault(z => z.Id == targetZoneId);
        var dragged = Zones.FirstOrDefault(z => z.Id == draggedZoneId);
        if (target == null || dragged == null || target.Id == dragged.Id)
            return Guid.Empty;

        // Defensive: a hidden sub-zone target can't be dropped onto (its window is
        // closed), but redirect it to its master just in case.
        if (target.MergedGroupMembership.GroupId.HasValue && target.MergedGroupMembership.SubZoneIds.Count == 0)
        {
            var tm = Zones.FirstOrDefault(z => z.MergedGroupMembership.GroupId == target.MergedGroupMembership.GroupId
                                               && z.MergedGroupMembership.SubZoneIds.Count > 0);
            if (tm != null) target = tm;
        }

        // Payload = dragged first, then its subs in order when it's a master.
        var payload = new List<Zone> { dragged };
        if (dragged.MergedGroupMembership.SubZoneIds.Count > 0)
        {
            foreach (var subId in dragged.MergedGroupMembership.SubZoneIds.ToList())
            {
                var sub = Zones.FirstOrDefault(z => z.Id == subId);
                if (sub != null && sub.Id != target.Id) payload.Add(sub);
            }
        }

        // Detach payload zones from their current groups, subs first so a dragged
        // master never needs a mid-flight promotion. No per-zone events here — the
        // single SaveConfig/ZonesChanged at the end is the only refresh.
        for (int i = payload.Count - 1; i >= 0; i--)
            DetachZoneFromGroup(payload[i]);

        bool targetWasMerged = target.MergedGroupMembership.GroupId.HasValue;

        // Ensure the target is a master (new group when standalone).
        Guid groupId;
        Zone master;
        if (target.MergedGroupMembership.GroupId.HasValue)
        {
            groupId = target.MergedGroupMembership.GroupId.Value;
            master = Zones.FirstOrDefault(z => z.MergedGroupMembership.GroupId == groupId
                                               && z.MergedGroupMembership.SubZoneIds.Count > 0) ?? target;
        }
        else
        {
            groupId = Guid.NewGuid();
            target.MergedGroupMembership.GroupId = groupId;
            master = target;
        }

        // Normalize the master's display order BEFORE the fold so an existing
        // user-arranged order survives, then append the incoming members at the end.
        var order = master.MergedGroupMembership.TabOrder;
        if (order.Count != master.MergedGroupMembership.SubZoneIds.Count + 1
            || !order.Contains(master.Id)
            || master.MergedGroupMembership.SubZoneIds.Any(id => !order.Contains(id)))
        {
            order.Clear();
            order.Add(master.Id);
            order.AddRange(master.MergedGroupMembership.SubZoneIds);
        }

        foreach (var z in payload)
        {
            if (z.Id == master.Id) continue;
            z.MergedGroupMembership.SubZoneIds.Clear();
            z.MergedGroupMembership.GroupId = groupId;
            master.MergedGroupMembership.SubZoneIds.Add(z.Id);
            order.Add(z.Id);
        }

        // New groups get the generated "target + sub + sub" default; adding to an
        // existing group appends the incoming names to its current (possibly
        // user-edited) display name instead of clobbering it.
        string groupName;
        if (targetWasMerged)
        {
            var added = payload.Where(z => z.Id != master.Id).Select(z => z.Name).ToList();
            var current = string.IsNullOrWhiteSpace(master.MergedGroupMembership.DisplayName)
                ? master.Name
                : master.MergedGroupMembership.DisplayName;
            groupName = added.Count > 0 ? current + " + " + string.Join(" + ", added) : current;
        }
        else
        {
            groupName = BuildMergedGroupName(master);
        }
        master.MergedGroupMembership.DisplayName = groupName;
        foreach (var m in Zones.Where(z => z.MergedGroupMembership.GroupId == groupId))
            m.MergedGroupMembership.DisplayName = groupName;

        // Hide payload windows (the dragged window closes; already-hidden subs are no-ops).
        foreach (var z in payload)
            if (z.Id != master.Id) FullHideZone(z.Id);

        if (_zoneWindows.TryGetValue(master.Id, out var masterWin))
            masterWin.RefreshZone(master);

        SaveConfig();
        ZonesChanged?.Invoke();
        return groupId;
    }

    /// <summary>Detach a zone from its current group without firing save/refresh events.
    /// Handles both sub-zones and masters (a detached master promotes its first sub).</summary>
    private void DetachZoneFromGroup(Zone zone)
    {
        if (!zone.MergedGroupMembership.GroupId.HasValue) return;
        var groupId = zone.MergedGroupMembership.GroupId.Value;
        var master = Zones.FirstOrDefault(m => m.MergedGroupMembership.GroupId == groupId && m.MergedGroupMembership.SubZoneIds.Count > 0);

        if (master != null)
        {
            if (master.Id == zone.Id)
            {
                var subs = zone.MergedGroupMembership.SubZoneIds.ToList();
                var newMaster = Zones.FirstOrDefault(m => m.Id == subs.FirstOrDefault());
                if (newMaster != null)
                {
                    newMaster.MergedGroupMembership.GroupId = groupId;
                    newMaster.MergedGroupMembership.SubZoneIds = subs.Skip(1).ToList();
                    newMaster.MergedGroupMembership.DisplayName = BuildMergedGroupName(newMaster);
                    newMaster.MergedGroupMembership.Icon = zone.MergedGroupMembership.Icon;
                    newMaster.MergedGroupMembership.TabOrder.Clear();
                    newMaster.MergedGroupMembership.TabOrder.Add(newMaster.Id);
                    newMaster.MergedGroupMembership.TabOrder.AddRange(newMaster.MergedGroupMembership.SubZoneIds);
                }
            }
            else
            {
                master.MergedGroupMembership.SubZoneIds.Remove(zone.Id);
                master.MergedGroupMembership.TabOrder.Remove(zone.Id);
                if (master.MergedGroupMembership.SubZoneIds.Count == 0)
                {
                    master.MergedGroupMembership.GroupId = null;
                    master.MergedGroupMembership.DisplayName = "";
                    master.MergedGroupMembership.Icon = "";
                    master.MergedGroupMembership.TabOrder.Clear();
                }
                else
                {
                    master.MergedGroupMembership.DisplayName = BuildMergedGroupName(master);
                }
            }
        }

        zone.MergedGroupMembership.GroupId = null;
        zone.MergedGroupMembership.SubZoneIds.Clear();
        zone.MergedGroupMembership.TabOrder.Clear();
        zone.MergedGroupMembership.DisplayName = "";
        zone.MergedGroupMembership.Icon = "";
    }

    private string BuildMergedGroupName(Zone master)
    {
        var names = new List<string> { master.Name };
        foreach (var subId in master.MergedGroupMembership.SubZoneIds)
        {
            var sub = Zones.FirstOrDefault(z => z.Id == subId);
            if (sub != null) names.Add(sub.Name);
        }
        return string.Join(" + ", names);
    }

    /// <summary>Get all zones in a merged group.</summary>
    public List<Zone> GetMergedGroupZones(Guid groupId)
        => Zones.Where(z => z.MergedGroupMembership.GroupId == groupId).ToList();

    /// <summary>
    /// Detach a zone from its merged group while keeping the group window alive.
    /// Sub-zones use the plain removal. Detaching the MASTER promotes the first
    /// remaining member in display order to the new master (or dissolves the group
    /// when only one member remains), transfers the group style to the new host and
    /// re-keys the current group window to it. The detached zone is not shown here —
    /// the caller positions and shows it (drag-out drops it at the cursor).
    /// </summary>
    public void DetachZoneAt(Guid zoneId)
    {
        var zone = Zones.FirstOrDefault(z => z.Id == zoneId);
        if (zone == null || !zone.MergedGroupMembership.GroupId.HasValue) return;

        // Sub-zone: plain detach (auto-dissolves when only the master remains).
        if (zone.MergedGroupMembership.SubZoneIds.Count == 0)
        {
            RemoveFromMergedGroup(zoneId);
            return;
        }

        var groupId = zone.MergedGroupMembership.GroupId.Value;

        // Remaining members in display order.
        var remaining = new List<Guid>(zone.MergedGroupMembership.TabOrder);
        if (remaining.Count != zone.MergedGroupMembership.SubZoneIds.Count + 1
            || !remaining.Contains(zone.Id)
            || zone.MergedGroupMembership.SubZoneIds.Any(id => !remaining.Contains(id)))
        {
            remaining.Clear();
            remaining.Add(zone.Id);
            remaining.AddRange(zone.MergedGroupMembership.SubZoneIds);
        }
        remaining.Remove(zone.Id);

        var host = Zones.FirstOrDefault(z => z.Id == remaining.FirstOrDefault());
        if (host == null) return;

        // Re-key the current group window to the successor so it stays on screen.
        _zoneWindows.Remove(zone.Id, out var window);
        if (window != null)
        {
            host.X = window.Left; host.Y = window.Top;
            host.Width = window.Width; host.Height = window.FullModelHeight;
        }

        zone.MergedGroupMembership.GroupId = null;
        zone.MergedGroupMembership.SubZoneIds.Clear();
        zone.MergedGroupMembership.TabOrder.Clear();
        zone.MergedGroupMembership.DisplayName = "";
        zone.MergedGroupMembership.Icon = "";
        zone.IsVisible = false;

        if (remaining.Count == 1)
        {
            // Only one member left → the group dissolves; it keeps the window standalone.
            host.MergedGroupMembership.GroupId = null;
            host.MergedGroupMembership.SubZoneIds.Clear();
            host.MergedGroupMembership.TabOrder.Clear();
            host.MergedGroupMembership.DisplayName = "";
            host.MergedGroupMembership.Icon = "";
            host.IsVisible = true;
            AdoptWindow(host, window, merged: false);
        }
        else
        {
            // Promote the first remaining member to the new master.
            host.MergedGroupMembership.GroupId = groupId;
            host.MergedGroupMembership.SubZoneIds = remaining.Skip(1).ToList();
            host.MergedGroupMembership.TabOrder = new List<Guid>(remaining);
            host.MergedGroupMembership.DisplayName = BuildMergedGroupName(host);
            host.MergedGroupMembership.Icon = zone.MergedGroupMembership.Icon;
            // Keep the group's merged style on the new host.
            CloneHelper.CopyBaseProperties<MergedGroupStyle>(zone.MergedGroupStyle, host.MergedGroupStyle);
            host.IsVisible = true;
            AdoptWindow(host, window, merged: true);
        }

        SaveConfig();
        ZonesChanged?.Invoke();
    }

    /// <summary>Point the existing group window at the new host zone (or open one
    /// when the window wasn't open), refreshing items and selection accordingly.</summary>
    private void AdoptWindow(Zone host, ZoneWindow? window, bool merged)
    {
        if (window == null)
        {
            ShowZone(host);
            return;
        }
        _zoneWindows[host.Id] = window;
        if (window.DataContext is ViewModels.ZoneViewModel vm)
        {
            if (merged) vm.SelectedSubZoneId = host.Id; // refreshes merged items
            else { vm.RefreshZone(host); vm.SelectedSubZoneId = null; }
        }
        window.RefreshZone(host);
    }

    /// <summary>Persist a merged master's sub-zone order after a tab reorder and notify
    /// listeners. The combined display name is intentionally left untouched — it is
    /// user-editable once generated.</summary>
    public void SaveSubZoneOrder(Guid masterZoneId)
    {
        var master = Zones.FirstOrDefault(z => z.Id == masterZoneId);
        if (master == null || master.MergedGroupMembership.SubZoneIds.Count == 0) return;
        SaveConfig();
        ZonesChanged?.Invoke();
    }

    // ── Lock ──

    /// <summary>Set locked state for a zone by string id. Fires LockChanged after state update.</summary>
    public void SetLocked(string zoneId, bool locked)
    {
        if (!Guid.TryParse(zoneId, out var guid)) return;
        var zone = Zones.FirstOrDefault(z => z.Id == guid);
        if (zone == null) return;
        zone.IsLocked = locked;
        LockChanged?.Invoke(zoneId, locked);
    }
}
