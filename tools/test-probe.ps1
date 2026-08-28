param([int]$x, [int]$y)
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class WinSpy2 {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
  [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
  [DllImport("user32.dll")] public static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int n);
  public static string Probe(int x, int y) {
    SetProcessDPIAware();
    SetCursorPos(x, y);
    System.Threading.Thread.Sleep(100);
    var pt = new POINT(); pt.X = x; pt.Y = y;
    var h = WindowFromPoint(pt);
    if (h == IntPtr.Zero) return "none at " + x + "," + y;
    var sb = new StringBuilder(256); GetClassName(h, sb, 256);
    return string.Format("hwnd=0x{0:X} class={1} at {2},{3}", h.ToInt64(), sb, x, y);
  }
}
"@
[WinSpy2]::Probe($x, $y)
