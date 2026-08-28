using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;

namespace DesktopZones.Helpers;

/// <summary>
/// 当前「焦点显示器」判定 + 工作区几何。面板弹出动画以该显示器工作区右下角为原点：
/// 优先取前台窗口所在显示器，其次取鼠标所在显示器，最后回退到主显示器。
/// </summary>
public static class MonitorHelper
{
    const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromPoint(NativeMethods.POINT pt, uint flags);

    [DllImport("user32.dll")]
    static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out NativeMethods.POINT pt);

    /// <summary>MDT_EFFECTIVE_DPI:跟随每显示器缩放(与 app.manifest 的 PerMonitorV2 一致)。</summary>
    const int MDT_EFFECTIVE_DPI = 0;

    [DllImport("shcore.dll")]
    static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    /// <summary>物理像素点所在显示器的 DPI 缩放(96 基准)。失败回退 1.0。</summary>
    public static double DpiScaleAt(Point physicalPx)
    {
        try
        {
            var pt = new NativeMethods.POINT
            {
                x = (int)Math.Round(physicalPx.X),
                y = (int)Math.Round(physicalPx.Y)
            };
            var mon = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
            if (mon != IntPtr.Zero
                && GetDpiForMonitor(mon, MDT_EFFECTIVE_DPI, out var dx, out _) == 0
                && dx > 0)
                return dx / 96.0;
        }
        catch { }
        return 1.0;
    }

    /// <summary>把物理屏幕像素换算成 WPF 屏幕 DIP。
    /// ponytail 2026-08-28: 多屏混合 DPI 下 WPF Popup 的 PointToScreen 会给出与 Win32
    /// 真实几何矛盾的坐标(实测出现 -1927 这类副屏错值),贴点弹窗会跑错屏甚至被
    /// 错误钳位。换算只用 Win32:物理像素 ÷ 所在显示器缩放 = 与 Window.Left/Top
    /// 同空间的 DIP(虚拟原点 = 主显示器原点),完全不经过 WPF 的坐标翻译。</summary>
    public static Point PhysicalToDip(Point physicalPx)
    {
        double scale = DpiScaleAt(physicalPx);
        return new Point(physicalPx.X / scale, physicalPx.Y / scale);
    }

    /// <summary>物理光标位置换算成 WPF 屏幕 DIP。GetCursorPos 失败返回 null。</summary>
    public static Point? CursorDip()
    {
        try
        {
            if (!GetCursorPos(out var pt)) return null;
            return PhysicalToDip(new Point(pt.x, pt.y));
        }
        catch { return null; }
    }

    /// <summary>候选 DIP 点所在显示器的工作区(rcWork,物理像素按该显示器 DPI 换成 DIP)。
    /// 遍历所有显示器逐一换算判定,点不在任何显示器内时回退主显示器工作区。</summary>
    public static Rect WorkAreaDipContaining(Point dip)
    {
        try
        {
            var monitors = new List<IntPtr>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
                (IntPtr h, IntPtr hdc, ref RECT r, IntPtr dw) => { monitors.Add(h); return true; },
                IntPtr.Zero);
            foreach (var h in monitors)
            {
                var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (!GetMonitorInfo(h, ref info)) continue;
                if (h == IntPtr.Zero) continue;
                if (GetDpiForMonitor(h, MDT_EFFECTIVE_DPI, out var dx, out var dy) != 0 || dx == 0 || dy == 0) continue;
                double sx = dx / 96.0, sy = dy / 96.0;
                var wa = new Rect(
                    info.rcWork.Left / sx,
                    info.rcWork.Top / sy,
                    (info.rcWork.Right - info.rcWork.Left) / sx,
                    (info.rcWork.Bottom - info.rcWork.Top) / sy);
                if (wa.Contains(dip)) return wa;
            }
        }
        catch { }
        return SystemParameters.WorkArea;
    }

    static MONITORINFO GetFocusedMonitorInfo()
    {
        IntPtr mon = IntPtr.Zero;

        var fg = GetForegroundWindow();
        if (fg != IntPtr.Zero)
            mon = MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST);

        if (mon == IntPtr.Zero && GetCursorPos(out var cursor))
            mon = MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);

        if (mon == IntPtr.Zero)
            mon = MonitorFromPoint(new NativeMethods.POINT(), MONITOR_DEFAULTTONEAREST);

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(mon, ref info);
        return info;
    }

    /// <summary>当前焦点显示器的工作区（不含任务栏）矩形，虚拟屏幕坐标。
    /// 注意:GetMonitorInfo 返回的是物理像素,调用方需按窗口 DPI 换算成 DIP。</summary>
    public static Rect FocusedWorkArea()
    {
        try
        {
            var info = GetFocusedMonitorInfo();
            return new Rect(
                info.rcWork.Left,
                info.rcWork.Top,
                info.rcWork.Right - info.rcWork.Left,
                info.rcWork.Bottom - info.rcWork.Top);
        }
        catch
        {
            return SystemParameters.WorkArea;
        }
    }
}
