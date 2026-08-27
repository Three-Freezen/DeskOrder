using System;
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
