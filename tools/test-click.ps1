param([int]$x, [int]$y)
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class NativeClick {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);
  public const uint LEFTDOWN = 0x02;
  public const uint LEFTUP = 0x04;
  public static void Click(int x, int y) {
    SetProcessDPIAware();
    SetCursorPos(x, y);
    System.Threading.Thread.Sleep(80);
    mouse_event(LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
    System.Threading.Thread.Sleep(60);
    mouse_event(LEFTUP, 0, 0, 0, UIntPtr.Zero);
  }
}
"@
[NativeClick]::Click($x, $y)
Write-Output "clicked $x,$y"
