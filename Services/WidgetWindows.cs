using System;
using System.Collections.Generic;

namespace DesktopZones.Services;

/// <summary>
/// 挂件窗口（时钟/日历/播放器）的创建与启动恢复 —— Phase 2 从 <c>ManagementWindow</c>
/// 里拆出来的。
///
/// 为什么拆：这些 Open* 原来都是 ManagementWindow 的实例方法，启动时为了恢复可见挂件，
/// App 必须先 <c>new ManagementWindow()</c>（1100×680 的游戏壳 + 页面容器 + 停靠属性面板，
/// 常驻 4~10MB）—— 而用户可能整场都不打开管理界面。拆出去之后启动只恢复挂件、不建管理
/// 窗口；管理窗口改为首次点托盘/双击桌面/打开设置时才懒建（见 App.EnsureManagementWindow）。
///
/// 窗口归属记录统一放 <see cref="WidgetService"/> 的 ClockWindows / CalendarWindows /
/// MediaWindows —— 它本来就是进程级唯一真相（管理窗口里的私有字典只是同一批窗口的镜像）。
/// </summary>
public static class WidgetWindows
{
    /// <summary>打开时钟窗口（已开着就直接返回）。<paramref name="onClosed"/> 给管理窗口刷新列表用。</summary>
    public static Views.ClockWidget? OpenClock(Models.DesktopClock clock, WidgetService widgets, Action? onClosed = null)
    {
        if (widgets.ClockWindows.TryGetValue(clock.Id, out var existing)) return existing;
        var window = new Views.ClockWidget(clock, widgets);
        window.Closed += (_, _) =>
        {
            widgets.ClockWindows.Remove(clock.Id);
            onClosed?.Invoke();
        };
        widgets.ClockWindows[clock.Id] = window;
        window.Show();
        return window;
    }

    public static Views.CalendarWidget? OpenCalendar(Models.DesktopCalendar cal, WidgetService widgets, Action? onClosed = null)
    {
        if (widgets.CalendarWindows.TryGetValue(cal.Id, out var existing)) return existing;
        var window = new Views.CalendarWidget(cal, widgets);
        window.Closed += (_, _) =>
        {
            widgets.CalendarWindows.Remove(cal.Id);
            onClosed?.Invoke();
        };
        widgets.CalendarWindows[cal.Id] = window;
        window.Show();
        window.Activate();
        return window;
    }

    public static Views.MediaPlayerWidget? OpenMediaPlayer(Models.DesktopMediaPlayer player, WidgetService widgets, Action? onClosed = null)
    {
        if (widgets.MediaWindows.TryGetValue(player.Id, out var existing)) return existing;
        var window = new Views.MediaPlayerWidget(player, widgets);
        // 这行日志和 WidgetService 的载入行一起，能区分"配置里没有"还是"窗口没恢复"。
        Helpers.DzTrace.Always(
            $"[Widgets] 打开播放器窗口 {player.Id} mode={player.Mode} pos=({player.X:0},{player.Y:0}) visible={player.IsVisible}");
        window.Closed += (_, _) =>
        {
            widgets.MediaWindows.Remove(player.Id);
            onClosed?.Invoke();
        };
        widgets.MediaWindows[player.Id] = window;
        window.Show();
        return window;
    }

    /// <summary>启动时恢复可见的挂件窗口（位置/显示状态持久化，与分区一致）。
    /// 隐藏的组件保持隐藏；已经开着的窗口（例如热重启/管理窗口刚创建）不重复建。</summary>
    public static void RestoreVisible(WidgetService? widgets)
    {
        if (widgets != null)
        {
            foreach (var clock in new List<Models.DesktopClock>(widgets.Clocks))
            {
                if (!clock.IsVisible) continue;
                try { OpenClock(clock, widgets); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[RestoreVisibleWidgets] clock {clock.Id} failed: {ex}"); }
            }
            foreach (var cal in new List<Models.DesktopCalendar>(widgets.Calendars))
            {
                if (!cal.IsVisible) continue;
                try { OpenCalendar(cal, widgets); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[RestoreVisibleWidgets] calendar {cal.Id} failed: {ex}"); }
            }
            foreach (var media in new List<Models.DesktopMediaPlayer>(widgets.MediaPlayers))
            {
                if (!media.IsVisible) continue;
                try { OpenMediaPlayer(media, widgets); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[RestoreVisibleWidgets] media {media.Id} failed: {ex}"); }
            }
        }
        try { PluginService.Instance.RestoreWidgets(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[RestoreVisibleWidgets] plugin widgets failed: {ex}"); }
    }
}
