#if DEBUG
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using DesktopZones.Helpers;
using DesktopZones.Models;
using DesktopZones.Services;
using DesktopZones.Views;

namespace DesktopZones;

/// <summary>
/// 分区坞 P0 自测台：真安装、真窗口、真分区。
/// 用法：DeskOrder.exe --zonebay-lab=&lt;dir&gt; --data-root=&lt;dir&gt;\data
/// 目录里需要 zone-bay-1.0.0.dopkg。
/// </summary>
public partial class App
{
    string? ParseZoneBayLabArg(string[] args)
    {
        const string prefix = "--zonebay-lab=";
        var arg = args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return arg == null ? null : arg[prefix.Length..].Trim('"');
    }

    async Task RunZoneBayLabAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        var details = new List<string>();
        bool pass = true;
        void Check(bool ok, string message)
        {
            details.Add((ok ? "PASS " : "FAIL ") + message);
            if (!ok) pass = false;
        }

        try
        {
            var plugins = PluginService.Instance;
            var bays = ZoneBayService.Instance;
            var zones = _zoneManager!;
            var loc = LocalizationService.Instance;

            // Phase 3 起「插件代码加载 + ZoneBayService 初始化」被挪到 ApplicationIdle 的
            // 启动收尾（OnDeferredStartupWork）；而自测台本身也在 ApplicationIdle 排队，
            // 可能先于收尾执行 → 这里看到的是未初始化的 ZoneBayService（_plugins 为 null，
            // HasAnyZoneBayPlugin / CreateBarForFirstPlugin 全 false）。显式补一次初始化
            // （Initialize 幂等），让自测台不再依赖两个 Idle 回调的排队顺序。
            if (!bays.IsInitialized && _zoneManager != null && _configService != null)
                bays.Initialize(PluginService.Instance, _zoneManager, _configService);

            var package = Path.Combine(dir, "zone-bay-1.0.0.dopkg");
            if (!File.Exists(package))
            {
                Check(false, "zone-bay-1.0.0.dopkg exists");
                return;
            }

            var install = await plugins.Installer!.InstallFromFileAsync(package);
            Check(install.Success, "install returns success" + (install.Error == null ? "" : " (" + install.Error + ")"));
            plugins.RefreshRecords();
            var record = plugins.GetRecord("three-freezen.zone-bay");
            Check(record != null, "zonebay record written");
            if (record == null) return;
            plugins.SetEnabled(record.Id, true);
            plugins.ReloadForLab();
            Check(plugins.GetLoaded(record.Id) != null, "zonebay plugin loaded");
            Check(bays.HasAnyZoneBayPlugin, "host sees a zonebay plugin");

            var bar = bays.CreateBarForFirstPlugin();
            Check(bar != null, "default bar created");
            if (bar == null) return;
            var window = bays.GetWindow(bar.Id);
            Check(window != null && window.IsVisible, "bar window visible");
            await Task.Delay(500);
            if (window != null) { DzTrace.Always("[ZoneBayLab] bar state: " + window.DebugVisualState()); SaveScreenshot(window, Path.Combine(dir, "shot-bar.png")); }
            Check(File.Exists(Path.Combine(dir, "shot-bar.png")), "bar screenshot written");

            bays.CollapseBar(bar);
            await Task.Delay(300);
            Check(window != null && window.Width <= 50, "bar collapses to the 36x36 restore button");
            bays.RestoreBar(bar);
            await Task.Delay(300);
            Check(window != null && window.Width > 100, "bar restores from the restore button");

            var zone = zones.CreateZone("ZoneBayLab", 260, 220, 420, 300);
            var itemDir = Path.Combine(dir, "zone-items");
            Directory.CreateDirectory(itemDir);
            for (int i = 0; i < 3; i++)
            {
                var file = Path.Combine(itemDir, $"item-{i}.txt");
                File.WriteAllText(file, "zone bay lab");
                zones.AddItem(zone.Id, file);
            }
            await Task.Delay(300);
            zones.HideZone(zone.Id);
            await Task.Delay(400);
            Check(zones.IsZoneMinimized(zone.Id), "lab zone minimized to restore button");

            var nesting = bays.Absorb(zone.Id, bar.Id, ZoneBaySourceKind.ZoneBayRestore, out _);
            Check(!nesting, "a zone-bay restore source is rejected (no nesting)");

            var absorbed = bays.Absorb(zone.Id, bar.Id, ZoneBaySourceKind.ZoneRestore, out var absorbError);
            Check(absorbed, "zone absorbed" + (absorbError == null ? "" : " (" + absorbError + ")"));
            Check(bays.IsAbsorbed(zone.Id), "zone is marked absorbed");
            Check(bar.ZoneIds.Contains(zone.Id), "bar holds the zone id");
            await Task.Delay(400);
            var barWindow = bays.GetWindow(bar.Id);
            if (barWindow != null) { DzTrace.Always("[ZoneBayLab] chip state: " + barWindow.DebugVisualState()); SaveScreenshot(barWindow, Path.Combine(dir, "shot-bar-chip.png")); }

            bays.ExpandInBay(zone.Id);
            await Task.Delay(600);
            var zoneWindow = zones.GetZoneWindow(zone.Id);
            Check(zoneWindow != null && zoneWindow.IsBayExpanded, "zone window is in bay-expanded state");
            if (zoneWindow != null) SaveScreenshot(zoneWindow, Path.Combine(dir, "shot-strip.png"));
            Check(File.Exists(Path.Combine(dir, "shot-strip.png")), "strip screenshot written");

            // 设置界面已并入原生属性面板（浮动窗）；这里验证它能对坞打开并截图。
            PropertyWindowService.OpenOrFocus(bar);
            await Task.Delay(700);
            var settings = Application.Current.Windows
                .OfType<DesktopZones.Views.Components.PropertyWindow>()
                .FirstOrDefault(w => ReferenceEquals(w.Target, bar));
            Check(settings != null && settings.IsVisible, "native property panel opens for the bar");
            if (settings != null) SaveScreenshot(settings, Path.Combine(dir, "shot-settings.png"));
            Check(File.Exists(Path.Combine(dir, "shot-settings.png")), "settings screenshot written");
            settings?.Close();
            await Task.Delay(300);

            bar.ExpandMode = ZoneBayExpandModes.Original;
            bays.ApplySettings(bar);
            bays.CollapseInBay(zone.Id);
            bays.ExpandInBay(zone.Id);
            await Task.Delay(500);
            Check(zoneWindow != null && Math.Abs(zoneWindow.Width - 420) < 2,
                "original expansion uses the zone width");
            if (zoneWindow != null) SaveScreenshot(zoneWindow, Path.Combine(dir, "shot-original.png"));
            Check(File.Exists(Path.Combine(dir, "shot-original.png")), "original screenshot written");

            // 第 6 条：坞内「原始展开」时点最小化 = 收回坞里，桌面不能再冒出 36×36 残影。
            zoneWindow?.HideZone();
            await Task.Delay(500);
            Check(zoneWindow != null && !zoneWindow.IsBayExpanded,
                "minimizing a bay-expanded zone collapses it back into the bay");
            Check(zoneWindow == null || zoneWindow.RestoreButton.Visibility != Visibility.Visible,
                "no ghost 36x36 restore button is created on the desktop");

            var barWindow2 = bays.GetWindow(bar.Id);
            if (barWindow2 != null)
            {
                bar.Edge = ZoneBayEdges.Top;
                bays.ApplySettings(bar);
                await Task.Delay(400);
                Check(barWindow2.Top <= SystemParameters.WorkArea.Top + 2, "top edge moves the bar to the top");
                bar.Edge = ZoneBayEdges.Bottom;
                bays.ApplySettings(bar);
            }

            bar.ExpandMode = ZoneBayExpandModes.Strip;
            bays.ApplySettings(bar);

            // ── 第 4 条：坞主体右键 = 坞菜单 ──
            var barMenu = bays.GetWindow(bar.Id)?.DebugOpenBarMenu() ?? "";
            Check(barMenu.Contains(loc["ZoneBay.Menu.Delete"]), "bar body right-click opens the bay menu");

            // ── 第 7 条：坞内展开的分区菜单不再提供「完全隐藏」 ──
            bays.ExpandInBay(zone.Id);
            await Task.Delay(500);
            var bayMenuText = zoneWindow?.DebugOpenZoneMenu() ?? "";
            Check(bayMenuText.Contains(loc["ZoneBay.Chip.Recall"]), "bay menu: minimize reads recall-to-bay");
            Check(!bayMenuText.Contains(loc["DockMenu.Hide"]), "bay menu: full-hide item removed");
            zoneWindow?.DebugCloseZoneMenu();
            bays.CollapseInBay(zone.Id);
            await Task.Delay(300);

            // ── P1：左 / 右边（纵向坞） ──
            var barWin = bays.GetWindow(bar.Id);
            var wa0 = MonitorHelper.WorkAreaDipAt(bar.MonitorIndex);
            bar.Edge = ZoneBayEdges.Left;
            bays.ApplySettings(bar);
            await Task.Delay(400);
            Check(barWin != null && Math.Abs(barWin.Width - 60) < 2 && Math.Abs(barWin.Height - wa0.Height) < 3,
                "left edge: one cell wide, work-area tall");
            Check(barWin != null && barWin.Left <= wa0.Left + 2, "left edge: flush to the left");
            if (barWin != null) SaveScreenshot(barWin, Path.Combine(dir, "shot-edge-left.png"));
            DzTrace.Always("[ZoneBayLab] left state: " + (barWin?.DebugVisualState() ?? "null"));
            DzTrace.Always("[ZoneBayLab] left tree:\n" + (barWin?.DebugContentTree() ?? "null"));
            bar.Edge = ZoneBayEdges.Right;
            bays.ApplySettings(bar);
            await Task.Delay(400);
            Check(barWin != null && Math.Abs(barWin.Left + barWin.Width - wa0.Right) < 3, "right edge: flush to the right");
            if (barWin != null) SaveScreenshot(barWin, Path.Combine(dir, "shot-edge-right.png"));
            bar.Edge = ZoneBayEdges.Bottom;
            bays.ApplySettings(bar);
            await Task.Delay(300);

            // ── P2：多屏 / 每屏 DPI ──
            if (MonitorHelper.MonitorCount() > 1)
            {
                bar.MonitorIndex = 1;
                bays.ApplySettings(bar);
                await Task.Delay(400);
                var wa1 = MonitorHelper.WorkAreaDipAt(1);
                Check(barWin != null && Math.Abs(barWin.Left - wa1.Left) < 3
                      && Math.Abs(barWin.Top + barWin.Height - wa1.Bottom) < 3,
                    "multi-monitor: the bar follows its own monitor's work area");
                bar.MonitorIndex = 0;
                bays.ApplySettings(bar);
                await Task.Delay(300);
            }
            else details.Add("SKIP multi-monitor: only one display");

            // ── P2：多实例（同边叠层） ──
            var bar2 = bays.CreateBarForFirstPlugin();
            Check(bar2 != null && bays.Bars.Count >= 2, "multi-instance: a second bar can be created");
            await Task.Delay(500);
            var w2 = bar2 == null ? null : bays.GetWindow(bar2.Id);
            Check(w2 != null && barWin != null && w2.Top < barWin.Top - 10,
                "same-edge second bar stacks one lane inward");
            if (bar2 != null) bays.DeleteBar(bar2);
            await Task.Delay(300);

            // ── 第 5 条 / P2：芯片换位 + 拖出恢复 ──
            var c1 = zones.CreateZone("BayChip1", 120, 560, 260, 200);
            var c2 = zones.CreateZone("BayChip2", 420, 560, 260, 200);
            await Task.Delay(300);
            zones.HideZone(c1.Id);
            zones.HideZone(c2.Id);
            await Task.Delay(400);
            Check(bays.Absorb(c1.Id, bar.Id, ZoneBaySourceKind.ZoneRestore, out _), "chip 1 absorbed");
            Check(bays.Absorb(c2.Id, bar.Id, ZoneBaySourceKind.ZoneRestore, out _), "chip 2 absorbed");
            int i1 = bar.ZoneIds.IndexOf(c1.Id);
            int i2 = bar.ZoneIds.IndexOf(c2.Id);
            Check(i1 >= 0 && i2 == i1 + 1, "chips keep insert order");
            bays.MoveChip(bar, i1, i2);
            Check(bar.ZoneIds.IndexOf(c1.Id) == i2, "MoveChip reorders the chips");
            await Task.Delay(300);
            // 换回来，后面的拖出测试用原始顺序。
            bays.MoveChip(bar, i2, i1);
            await Task.Delay(200);
            bays.ReleaseToDesktop(c1.Id.ToString(), new Point(520, 420));
            await Task.Delay(500);
            Check(!bays.IsAbsorbed(c1.Id) && zones.IsZoneMinimized(c1.Id),
                "chip dragged out restores a 36x36 button");
            var cw = zones.GetZoneWindow(c1.Id);
            Check(cw != null && Math.Abs(cw.Left - 502) < 5 && Math.Abs(cw.Top - 402) < 5,
                "the restored button lands at the drop point");

            // 第 4 条：芯片右键 = 这个分区自己的菜单（按需借一个隐藏 ZoneWindow 当菜单宿主，
            // 菜单关掉后宿主窗口要回收，不能留下隐藏窗口 + 订阅）。
            bool chipMenu = bays.OpenZoneMenu(c2.Id.ToString(), null);
            Check(chipMenu, "chip right-click opens the zone's own menu");
            var menuHost = zones.GetZoneWindow(c2.Id);
            menuHost?.DebugCloseZoneMenu();
            await Task.Delay(500);
            Check(zones.GetZoneWindow(c2.Id) == null, "the menu-only zone window is recycled");

            // ── P1：组合分区在长条模式下有标签栏 ──
            try
            {
                var m1 = zones.CreateZone("BayMergeA", 100, 660, 260, 220);
                var m2 = zones.CreateZone("BayMergeB", 400, 660, 260, 220);
                await Task.Delay(300);
                var gid = zones.MergeZones(m1.Id, m2.Id);
                var master = zones.Zones.FirstOrDefault(z =>
                    z.MergedGroupMembership.GroupId == gid && z.MergedGroupMembership.SubZoneIds.Count > 0);
                Check(master != null, "merged group created");
                if (master != null)
                {
                    zones.HideZone(master.Id);
                    await Task.Delay(400);
                    Check(bays.Absorb(master.Id, bar.Id, ZoneBaySourceKind.ZoneRestore, out _),
                        "merged zone absorbed");
                    bays.ExpandInBay(master.Id);
                    await Task.Delay(500);
                    var mwin = zones.GetZoneWindow(master.Id);
                    Check(mwin != null && mwin.SubZoneTabsRow.Visibility == Visibility.Visible,
                        "merged strip shows the sub-zone tab row");
                    Check(mwin != null && mwin.Height >= Math.Max(40, master.GridSize) + 12 + 20,
                        "merged strip height includes the tab row");
                    if (mwin != null) SaveScreenshot(mwin, Path.Combine(dir, "shot-strip-merged.png"));
                    bays.CollapseInBay(master.Id);
                    bays.Release(master.Id);
                    await Task.Delay(300);
                }
            }
            catch (Exception exMerge)
            {
                Check(false, "merged strip: " + exMerge.Message);
            }

            EnsureManagementWindow();
            var mw = ManagementWindow;
            if (mw != null)
            {
                mw.Show();
                mw.ShowSection("zonebays");
                await Task.Delay(700);
                SaveScreenshot(mw, Path.Combine(dir, "shot-page.png"));
                Check(File.Exists(Path.Combine(dir, "shot-page.png")), "management page screenshot written");

                // 点行 → 右侧原生属性面板（停靠态）
                Views.Components.PropertyWindowManager.Instance.DockTarget(bar, mw);
                await Task.Delay(800);
                SaveScreenshot(mw, Path.Combine(dir, "shot-settings-docked.png"));
                Check(File.Exists(Path.Combine(dir, "shot-settings-docked.png")),
                    "docked native settings panel screenshot written");
            }

            bays.CollapseInBay(zone.Id);
            await Task.Delay(300);
            Check(zoneWindow == null || !zoneWindow.IsBayExpanded, "zone window collapses back");

            bays.Release(zone.Id);
            await Task.Delay(500);
            Check(!bays.IsAbsorbed(zone.Id), "zone released from bar");
            Check(!bar.ZoneIds.Contains(zone.Id), "bar no longer holds the zone");
            Check(zones.IsZoneMinimized(zone.Id), "released zone is back as a 36x36 restore button");

            bays.DeleteBar(bar);
            Check(bays.Bars.Count == 0, "bar deleted");

            plugins.SetEnabled(record.Id, false);
            Check(!bays.HasAnyZoneBayPlugin, "disabling the plugin hides the zone bay");
            plugins.SetEnabled(record.Id, true);
            plugins.ReloadForLab();
            Check(plugins.GetLoaded(record.Id) != null, "re-enabling loads the plugin again");

            var uninstall = plugins.Uninstall(record.Id, deleteData: false, out var uninstallError);
            Check(uninstall, "uninstall returns success" + (uninstallError == null ? "" : " (" + uninstallError + ")"));
            Check(plugins.GetRecord(record.Id) == null, "zonebay record removed");
            Check(!bays.HasAnyZoneBayPlugin, "no zonebay plugin remains");
        }
        catch (Exception ex)
        {
            Check(false, "exception: " + ex.Message);
            details.Add(ex.ToString());
        }
        finally
        {
            var report = new
            {
                time = DateTime.Now,
                dataRoot = DataLocator.Root,
                pass,
                details
            };
            try
            {
                File.WriteAllText(Path.Combine(dir, "zonebay-lab-report.json"),
                    JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                File.WriteAllLines(Path.Combine(dir, "zonebay-lab.log"), details);
            }
            catch { }
            DzTrace.Always($"[ZoneBayLab] {(pass ? "PASS" : "FAIL")}");
        }
    }
}
#endif
