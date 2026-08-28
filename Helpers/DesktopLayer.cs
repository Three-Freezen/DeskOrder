using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Interop;
using DesktopZones.Views;

namespace DesktopZones.Helpers;

/// <summary>
/// 桌面层层级策略：分区/时钟/日历始终位于「壁纸(Progman)之上、全部普通应用窗口
/// 之下」——与锁定态所处的层级一致。BringToFront 把窗口提到桌面层顶部（高于全部
/// 兄弟桌面层窗口、仍低于应用窗口），用于显示/拖拽结束等置顶时刻；回落到底部
/// （紧贴壁纸上方）由失焦/锁定路径直接调 PinBelowProgman。面板与便签不归本类管，
/// 保持各自的浮动策略。
/// </summary>
public static class DesktopLayer
{
    /// <summary>把窗口提到桌面层顶部。替代旧 PinToDesktop(HWND_TOP)——后者会把
    /// 窗口浮到所有应用窗口之上，违反桌面层策略。</summary>
    public static void BringToFront(Window window)
    {
        NativeMethods.InsertAbove(window, FindTopmostSibling(window));
    }

    /// <summary>桌面层里最顶部的兄弟窗口：从自身所在带的顶端（GW_HWNDFIRST 已跳过
    /// Topmost 带）向下扫，第一个命中的兄弟即锚点——插到它上方后本窗口成为桌面层
    /// 最顶部、仍低于全部应用窗口。没有兄弟则锚定壁纸（等价 PinBelowProgman）。</summary>
    static IntPtr FindTopmostSibling(Window window)
    {
        var helper = new WindowInteropHelper(window);
        helper.EnsureHandle();

        var siblings = new HashSet<IntPtr>();
        foreach (Window w in Application.Current.Windows)
        {
            if (ReferenceEquals(w, window) || !w.IsVisible
                || w is not (ZoneWindow or ClockWidget or CalendarWidget)) continue;
            // 未 SourceInitialized 的窗口 Handle 为零且不在 z 序链上，直接跳过
            var h = new WindowInteropHelper(w).Handle;
            if (h != IntPtr.Zero) siblings.Add(h);
        }

        var cur = NativeMethods.GetWindow(helper.Handle, NativeMethods.GW_HWNDFIRST);
        while (cur != IntPtr.Zero)
        {
            if (cur != helper.Handle && siblings.Contains(cur)) return cur;
            cur = NativeMethods.GetWindow(cur, NativeMethods.GW_HWNDNEXT);
        }
        // 没有可见兄弟（或扫描失败）：直接贴壁纸；Progman 也找不到时 InsertAbove
        // 的零锚点退化为 HWND_TOP（旧行为兜底）。
        return NativeMethods.GetProgmanHandle();
    }
}
