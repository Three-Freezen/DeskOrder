Add-Type @"
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class WinEnum2 {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
  [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int n);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  public static List<string> List(uint target) {
    SetProcessDPIAware();
    var result = new List<string>();
    EnumWindows((h, lp) => {
      uint pid; GetWindowThreadProcessId(h, out pid);
      if (pid != target) return true;
      var cn = new StringBuilder(128); GetClassName(h, cn, 128);
      RECT r; GetWindowRect(h, out r);
      result.Add(string.Format("hwnd=0x{0:X} vis={1} iconic={2} class={3} rect=({4},{5})-({6},{7})",
        h.ToInt64(), IsWindowVisible(h), IsIconic(h), cn, r.L, r.T, r.R, r.B));
      return true;
    }, IntPtr.Zero);
    return result;
  }
}
"@
$p = Get-Process DeskOrder -ErrorAction Stop | Select-Object -First 1
[WinEnum2]::List([uint32]$p.Id) | ForEach-Object { Write-Output $_ }
