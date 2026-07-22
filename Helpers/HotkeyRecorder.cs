using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace DesktopZones.Helpers;

/// <summary>
/// Records a hotkey combination using a low-level keyboard hook (WH_KEYBOARD_LL).
/// Supports Ctrl/Alt/Shift/Win + any key — including the Win key which WPF cannot capture natively.
/// Reference: VideoPauseHotkey project's HotkeyRecordDialog implementation.
/// </summary>
public static class HotkeyRecorder
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYUP = 0x0105;

    private const int VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3;
    private const int VK_LMENU = 0xA4, VK_RMENU = 0xA5;
    private const int VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1;
    private const int VK_LWIN = 0x5B, VK_RWIN = 0x5C;

    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandleW(string lpModuleName);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private static IntPtr _hookHandle = IntPtr.Zero;
    private static LowLevelKeyboardProc? _hookProc;
    private static readonly System.Collections.Generic.HashSet<string> _pressed = new();
    private static Action<uint, uint>? _onComboCaptured;
    private static Action? _onCancelled;

    /// <summary>
    /// Start recording. Calls onCombo(modFlags, vkCode) when a combo is captured,
    /// or onCancelled when Escape is pressed.
    /// </summary>
    public static void StartRecording(Action<uint, uint> onCombo, Action? onCancelled = null)
    {
        StopRecording();
        _onComboCaptured = onCombo;
        _onCancelled = onCancelled;
        _pressed.Clear();
        _hookProc = HookCallback;
        IntPtr hMod = GetModuleHandleW(Process.GetCurrentProcess().MainModule?.ModuleName ?? "");
        _hookHandle = SetWindowsHookExW(WH_KEYBOARD_LL, _hookProc, hMod, 0);
    }

    /// <summary>
    /// Stop the keyboard hook.
    /// </summary>
    public static void StopRecording()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
        _hookProc = null;
        _onComboCaptured = null;
        _onCancelled = null;
        _pressed.Clear();
    }

    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var hookStruct = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            uint vk = hookStruct.vkCode;
            int msg = wParam.ToInt32();

            if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
            {
                if (vk == VK_LCONTROL || vk == VK_RCONTROL) _pressed.Add("ctrl");
                else if (vk == VK_LMENU || vk == VK_RMENU) _pressed.Add("alt");
                else if (vk == VK_LSHIFT || vk == VK_RSHIFT) _pressed.Add("shift");
                else if (vk == VK_LWIN || vk == VK_RWIN) _pressed.Add("win");
                else
                {
                    // Non-modifier key — capture combo
                    uint mods = pressedToMods();
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        StopRecording();
                        _onComboCaptured?.Invoke(mods, vk);
                    });
                    return (IntPtr)1; // swallow key
                }

                // Escape cancels
                if (vk == 0x1B)
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        StopRecording();
                        _onCancelled?.Invoke();
                    });
                    return (IntPtr)1;
                }
            }
            else if (msg == WM_KEYUP || msg == WM_SYSKEYUP)
            {
                if (vk == VK_LCONTROL || vk == VK_RCONTROL) _pressed.Remove("ctrl");
                else if (vk == VK_LMENU || vk == VK_RMENU) _pressed.Remove("alt");
                else if (vk == VK_LSHIFT || vk == VK_RSHIFT) _pressed.Remove("shift");
                else if (vk == VK_LWIN || vk == VK_RWIN) _pressed.Remove("win");
            }
        }
        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private static uint pressedToMods()
    {
        uint mods = 0;
        if (_pressed.Contains("ctrl")) mods |= MOD_CONTROL;
        if (_pressed.Contains("alt")) mods |= MOD_ALT;
        if (_pressed.Contains("shift")) mods |= MOD_SHIFT;
        if (_pressed.Contains("win")) mods |= MOD_WIN;
        return mods;
    }

    /// <summary>
    /// Convert modifier flags + virtual key code to a display string like "Ctrl + Alt + N".
    /// </summary>
    public static string ComboToDisplay(uint mods, uint vk)
    {
        var parts = new System.Collections.Generic.List<string>();
        if ((mods & MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((mods & MOD_ALT) != 0) parts.Add("Alt");
        if ((mods & MOD_SHIFT) != 0) parts.Add("Shift");
        if ((mods & MOD_WIN) != 0) parts.Add("Win");

        string keyName = KeyInterop.KeyFromVirtualKey((int)vk).ToString();
        string prefix = string.Join(" + ", parts);
        return string.IsNullOrEmpty(prefix) ? keyName : $"{prefix} + {keyName}";
    }
}
