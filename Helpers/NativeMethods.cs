using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DesktopZones.Helpers;

public static class NativeMethods
{
    // Window styles
    public const int GWL_EXSTYLE = -20;
    public const int GWL_STYLE = -16;
    public const int WS_THICKFRAME = 0x00040000;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_APPWINDOW = 0x00040000;

    // SetWindowPos
    public static readonly IntPtr HWND_BOTTOM = new(1);
    public static readonly IntPtr HWND_TOP = IntPtr.Zero; // 0 = HWND_TOP per Win32
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new(-2);
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint SWP_FRAMECHANGED = 0x0020;
    // ponytail 2026-08-25: 移动窗口时不触发 WM_PAINT — PropertyWindow 16ms 拖动定时器必备。
    // 不加这个 flag，每次 SetWindowPos 都会让 WPF 收到 paint 消息，重新走一遍 layout/render，
    // 在 layered window (AllowsTransparency=True) 上会触发现有 layered surface 的重栅格化。
    public const uint SWP_NOREDRAW = 0x0008;

    // ShowWindow
    public const int SW_HIDE = 0;
    public const int SW_SHOW = 5;

    // Shell file info
    public const uint SHGFI_ICON = 0x100;
    public const uint SHGFI_LARGEICON = 0x0;
    public const uint SHGFI_SMALLICON = 0x1;
    public const uint SHGFI_SYSICONINDEX = 0x4000;
    public const uint SHGFI_LINKOVERLAY = 0x8000;
    public const uint SHGFI_DISPLAYNAME = 0x200;

    public const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    public const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;

    public const int IMAGE_ICON = 1;
    public const int ILD_NORMAL = 0x0;

    [DllImport("user32.dll")]
    public static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    public static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hwnd, IntPtr hwndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    /// <summary>
    /// ponytail: RDW_INVALIDATE | RDW_UPDATENOW — invalidate + immediate repaint.
    /// Used to flush WPF's <c>AllowsTransparency=True</c> layered-window DWM cache
    /// after inner Visibility flips that the renderer may otherwise leave stale
    /// (the "ghost rectangle" symptom where only ZoneBorder outline + FillRect
    /// translucent fill survive while the actual UI is supposed to be hidden).
    /// </summary>
    public const uint RDW_INVALIDATE = 0x0001;
    public const uint RDW_UPDATENOW = 0x0100;

    [DllImport("user32.dll")]
    public static extern bool RedrawWindow(IntPtr hwnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll")]
    public static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    public static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter,
        string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    // Icon extraction
    [StructLayout(LayoutKind.Sequential)]
    public struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    public static extern int GetDeviceCaps(IntPtr hdc, int nIndex);

    [DllImport("shell32.dll")]
    public static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
        ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

    // Resolve a known-folder GUID (Desktop/Documents/...) to its real folder path.
    [DllImport("shell32.dll")]
    public static extern int SHGetKnownFolderPath(ref Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr hIcon);

    // ── Rounded window corners ──
    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);

    [DllImport("user32.dll")]
    public static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    // ── Win11 native rounded corners ──
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWCP_ROUND = 2;
    public const int DWMWCP_DONOTROUND = 1;

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll")]
    public static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

    [StructLayout(LayoutKind.Sequential)]
    public struct MARGINS { public int Left, Right, Top, Bottom; }

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    /// <summary>Apply rounded corners to a WPF Window via Win11 DWM. No-op on older Windows (avoids SetWindowRgn clipping).</summary>
    public static void SetRoundedCorners(Window window, int radius = 12)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        try
        {
            // ponytail 2026-08-26: radius ≤ 0 means the zone is in sharp-corner
            // mode — DWM must stop rounding the HWND surface, otherwise Win11
            // clips the sharp WPF corners into a residual rounded "bite"
            // ("尖角裁切不干净"). radius > 0 → DWM round corners.
            int cornerPref = radius > 0 ? DWMWCP_ROUND : DWMWCP_DONOTROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPref, sizeof(int));
        }
        catch { /* older Windows — sharp corners, no clipping */ }
    }

    /// <summary>No-op on Win11 (DWM handles corners). Clears any stale region on older Windows.</summary>
    public static void UpdateRoundedCorners(Window window, int radius = 12)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        // Clear any leftover SetWindowRgn region from previous versions
        try
        {
            var oldRgn = SetWindowRgn(hwnd, IntPtr.Zero, false);
            if (oldRgn != IntPtr.Zero)
                DeleteObject(oldRgn);
        }
        catch { }
    }

    /// <summary>Disable DWM rounded corners (for minimized state).</summary>
    public static void DisableRoundedCorners(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        try
        {
            int cornerPref = DWMWCP_DONOTROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPref, sizeof(int));
        }
        catch { }
    }

    /// <summary>
    /// ponytail 2026-08-26 ghost-ring fix: DWM draws a drop shadow around borderless
    /// layered windows' opaque content — for a collapsed widget that's the RestoreButton,
    /// so the shadow hugs the button and shows as a dark ring on the wallpaper (the
    /// reported "阴影"). DwmExtendFrameIntoClientArea with all margins = -1 removes the
    /// DWM frame INCLUDING its shadow — the documented way to get a frameless,
    /// shadow-less window.
    /// </summary>
    public static void DisableDwmFrameShadow(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        try
        {
            var m = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            DwmExtendFrameIntoClientArea(hwnd, ref m);
        }
        catch { /* older Windows — no frame shadow to disable */ }
    }

    // ── Global Hotkey ──
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll")]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // ── Folder browser (Vista+ IFileOpenDialog is preferred; this is for legacy callers) ──
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SHBrowseForFolderW(ref BROWSEINFOW b);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern bool SHGetPathFromIDListW(IntPtr p, System.Text.StringBuilder s);

    [DllImport("ole32.dll")]
    public static extern void CoTaskMemFree(IntPtr p);

    [StructLayout(LayoutKind.Sequential)]
    public struct BROWSEINFOW
    {
        public IntPtr hwndOwner;
        public IntPtr pidlRoot;
        public IntPtr pszDisplayName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszTitle;
        public uint ulFlags;
        public IntPtr lpfn;
        public IntPtr lParam;
        public int iImage;
    }

    // ── Win32 file drop (for transparent-window fallback) ──
    public const int WM_DROPFILES = 0x0233;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int x; public int y; }

    [DllImport("shell32.dll")]
    public static extern void DragAcceptFiles(IntPtr h, bool a);

    [DllImport("shell32.dll")]
    public static extern void DragFinish(IntPtr h);

    [DllImport("shell32.dll")]
    public static extern uint DragQueryFile(IntPtr h, uint i, System.Text.StringBuilder? f, uint c);

    [DllImport("shell32.dll")]
    public static extern bool DragQueryPoint(IntPtr h, out POINT p);

    // Extract icon from file
    [DllImport("shell32.dll")]
    public static extern int ExtractIconEx(string lpszFile, int nIconIndex,
        out IntPtr phiconLarge, out IntPtr phiconSmall, int nIcons);

    // Recycle Bin state: i64NumItems > 0 means the bin is full (has items).
    [StructLayout(LayoutKind.Sequential)]
    public struct SHQUERYRBINFO
    {
        public uint cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHQueryRecycleBinW(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    /// <summary>Extract a sized icon from an icon location ("file.dll,-index") — the same way the desktop resolves DefaultIcon entries.</summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHDefExtractIconW(string pszIconFile, int iIconIndex, uint uFlags,
        out IntPtr phiconLarge, out IntPtr phiconSmall, uint nIconSize);

    // Empty the Recycle Bin (quietly, no confirmation/progress/sound).
    public const uint SHERB_NOCONFIRMATION = 0x00000001;
    public const uint SHERB_NOPROGRESSUI = 0x00000002;
    public const uint SHERB_NOSOUND = 0x00000004;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHEmptyRecycleBinW(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    // Set window as a tool window (hides from taskbar)
    public static void SetToolWindow(Window window)
    {
        var helper = new WindowInteropHelper(window);
        helper.EnsureHandle();
        int exStyle = GetWindowLong(helper.Handle, GWL_EXSTYLE);
        SetWindowLong(helper.Handle, GWL_EXSTYLE,
            exStyle | WS_EX_TOOLWINDOW);
    }

    /// <summary>Remove WS_THICKFRAME to prevent OS-interactive edge resize (keep corner grips via SendMessage).</summary>
    public static void RemoveThickFrame(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        int style = GetWindowLong(hwnd, GWL_STYLE);
        SetWindowLong(hwnd, GWL_STYLE, style & ~WS_THICKFRAME);
    }

    // ponytail: insert the window at the top of Z-order (HWND_TOP) without
    // activating it (SWP_NOACTIVATE). Fixes two regressions from removing the
    // old _topHwnd-linked-list tracking:
    //   1. Drag-end drop-to-bottom — without an anchor above progman, inserting
    //      above progman puts the dragged window BELOW every other DZ window
    //      that already sits above progman. HWND_TOP has no such dependency.
    //   2. Analog clock click-to-front — AnalogDrag called PinToDesktop after
    //      DragMove; the same "above progman" anchor dropped it below other DZ
    //      windows.
    // SWP_NOACTIVATE preserves whatever window the user is currently focused on
    // (e.g., WeChat) — the moved DZ window goes to the top of Z-order but does
    // not become the foreground window.
    public static void PinToDesktop(Window window)
    {
        var helper = new WindowInteropHelper(window);
        helper.EnsureHandle();
        var hwnd = helper.Handle;
        SetWindowPos(hwnd, HWND_TOP, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    /// <summary>
    /// Lock the window at the desktop layer — above the wallpaper (progman) but below all app windows.
    /// Called once when entering locked state; not on Show/Hide.
    /// </summary>
    public static void PinBelowProgman(Window window)
    {
        var helper = new WindowInteropHelper(window);
        helper.EnsureHandle();
        var hwnd = helper.Handle;
        var progman = FindWindow("Progman", null);
        SetWindowPos(hwnd, progman, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    // Make window click-through in transparent areas
    public static void SetClickThrough(Window window, bool clickThrough)
    {
        var helper = new WindowInteropHelper(window);
        helper.EnsureHandle();
        int exStyle = GetWindowLong(helper.Handle, GWL_EXSTYLE);
        if (clickThrough)
            SetWindowLong(helper.Handle, GWL_EXSTYLE, exStyle | WS_EX_TRANSPARENT);
        else
            SetWindowLong(helper.Handle, GWL_EXSTYLE, exStyle & ~WS_EX_TRANSPARENT);
    }

    // Get icon from file path as a BitmapSource-friendly Bitmap
    public static System.Drawing.Icon? ExtractIcon(string path, bool large = true)
    {
        try
        {
            var shinfo = new SHFILEINFO();
            uint flags = SHGFI_ICON | (large ? SHGFI_LARGEICON : SHGFI_SMALLICON);

            IntPtr result = SHGetFileInfo(path, 0, ref shinfo, (uint)Marshal.SizeOf(shinfo), flags);
            if (result != IntPtr.Zero && shinfo.hIcon != IntPtr.Zero)
            {
                return System.Drawing.Icon.FromHandle(shinfo.hIcon);
            }
        }
        catch
        {
            // fall through to ExtractIconEx
        }

        try
        {
            ExtractIconEx(path, 0, out var largeIcon, out var smallIcon, 1);
            var iconHandle = large ? largeIcon : smallIcon;
            if (iconHandle != IntPtr.Zero)
            {
                return System.Drawing.Icon.FromHandle(iconHandle);
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }
}

// ponytail 2026-08-27: 桌面双击全局监听 — LowLevel 鼠标钩子。
public static class DesktopHook
{
    public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    public const int WH_MOUSE_LL = 14;
    public const int WM_LBUTTONDOWN = 0x0201;
    public const int WM_LBUTTONUP   = 0x0202;

    // ListView 命中测试：用于区分「空白桌面」与「桌面图标」。
    const int LVM_FIRST = 0x1000;
    const int LVM_HITTEST = LVM_FIRST + 18;
    const uint LVHT_ONITEMICON = 0x0002;
    const uint LVHT_ONITEMLABEL = 0x0004;
    const uint LVHT_ONITEMSTATEICON = 0x0008;
    const uint LVHT_ONITEM = LVHT_ONITEMICON | LVHT_ONITEMLABEL | LVHT_ONITEMSTATEICON;

    const uint PROCESS_QUERY_INFORMATION = 0x0400;
    const uint PROCESS_VM_OPERATION = 0x0008;
    const uint PROCESS_VM_READ = 0x0010;
    const uint PROCESS_VM_WRITE = 0x0020;
    const uint MEM_COMMIT = 0x1000;
    const uint MEM_RESERVE = 0x2000;
    const uint MEM_RELEASE = 0x8000;
    const uint PAGE_READWRITE = 0x04;

    [StructLayout(LayoutKind.Sequential)]
    public struct LVHITTESTINFO
    {
        public NativeMethods.POINT pt;
        public uint flags;
        public int iItem;
        public int iSubItem;
        public int iGroup;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(NativeMethods.POINT point);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ScreenToClient(IntPtr hWnd, ref NativeMethods.POINT lpPoint);

    [DllImport("user32.dll", EntryPoint = "SendMessage")]
    public static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll")]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, IntPtr dwSize, uint flAllocationType, uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr lpAddress, IntPtr dwSize, uint dwFreeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, ref LVHITTESTINFO lpBuffer, IntPtr nSize, out IntPtr lpNumberOfBytesWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, out LVHITTESTINFO lpBuffer, IntPtr nSize, out IntPtr lpNumberOfBytesRead);

    // ---- 桌面窗口缓存（Explorer 重启后句柄会失效，失效时重新查找）----
    static IntPtr _desktopProgman;
    static IntPtr _desktopWorkerW;
    static IntPtr _desktopDefView;
    static IntPtr _desktopListView;

    static void EnsureDesktopWindows()
    {
        if (_desktopDefView != IntPtr.Zero && IsWindow(_desktopDefView)) return;

        _desktopProgman = IntPtr.Zero;
        _desktopWorkerW = IntPtr.Zero;
        _desktopDefView = IntPtr.Zero;
        _desktopListView = IntPtr.Zero;

        _desktopProgman = FindWindow("Progman", null);
        if (_desktopProgman == IntPtr.Zero) return;

        // 收集候选：Progman 直系 DefView + 顶层 WorkerW 内的 DefView。
        // Win10/11 上真实桌面 DefView 常驻 WorkerW；Progman 有时会残留一个隐藏 DefView，
        // 因此优先选择「SysListView32 可见」的那一组。
        IntPtr bestWorkerW = IntPtr.Zero, bestDefView = IntPtr.Zero, bestListView = IntPtr.Zero;
        bool bestVisible = false;

        void Consider(IntPtr workerW, IntPtr defView, IntPtr listView)
        {
            if (defView == IntPtr.Zero) return;
            bool visible = listView != IntPtr.Zero && IsWindow(listView) && IsWindowVisible(listView);
            if (bestDefView == IntPtr.Zero || (visible && !bestVisible))
            {
                bestWorkerW = workerW;
                bestDefView = defView;
                bestListView = listView;
                bestVisible = visible;
            }
        }

        var progmanDefView = FindWindowEx(_desktopProgman, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (progmanDefView != IntPtr.Zero)
            Consider(IntPtr.Zero, progmanDefView, FindWindowEx(progmanDefView, IntPtr.Zero, "SysListView32", null));

        IntPtr workerw = IntPtr.Zero;
        while ((workerw = FindWindowEx(IntPtr.Zero, workerw, "WorkerW", null)) != IntPtr.Zero)
        {
            var defView = FindWindowEx(workerw, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (defView != IntPtr.Zero)
                Consider(workerw, defView, FindWindowEx(defView, IntPtr.Zero, "SysListView32", null));
        }

        _desktopWorkerW = bestWorkerW;
        _desktopDefView = bestDefView;
        _desktopListView = bestListView;
    }

    /// <summary>
    /// 判定 hwnd 是否属于桌面窗口树：沿父链比对「桌面自身」的窗口句柄
    /// （Progman / WorkerW / SHELLDLL_DefView / SysListView32）。
    /// 这里必须比句柄而不是类名 —— 文件资源管理器的文件列表同样是
    /// SHELLDLL_DefView + SysListView32，按类名会把资源管理器误判成桌面。
    /// </summary>
    static bool IsDesktopTreeWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        var current = hwnd;
        for (int i = 0; i < 16 && current != IntPtr.Zero; i++)
        {
            if (current == _desktopProgman || current == _desktopWorkerW ||
                current == _desktopDefView || current == _desktopListView)
                return true;
            current = GetParent(current);
        }
        return false;
    }

    static bool IsSameOrDescendant(IntPtr ancestor, IntPtr node)
    {
        if (ancestor == IntPtr.Zero || node == IntPtr.Zero) return false;
        var current = node;
        for (int i = 0; i < 16 && current != IntPtr.Zero; i++)
        {
            if (current == ancestor) return true;
            current = GetParent(current);
        }
        return false;
    }

    /// <summary>
    /// 桌面图标列表属于 explorer.exe 进程，LVM_HITTEST 的 lParam 是指针，
    /// 跨进程必须把 LVHITTESTINFO 写到目标进程再读回，不能直接传本进程地址。
    /// </summary>
    static bool IsIconAtPoint(IntPtr listView, NativeMethods.POINT ptScreen)
    {
        if (listView == IntPtr.Zero) return false;
        var pt = ptScreen;
        if (!ScreenToClient(listView, ref pt)) return false;

        var info = new LVHITTESTINFO { pt = pt };
        int size = Marshal.SizeOf<LVHITTESTINFO>();

        GetWindowThreadProcessId(listView, out uint pid);
        IntPtr hProc = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE, false, pid);
        if (hProc == IntPtr.Zero) return false; // 拿不到进程权限时保守视为空白桌面

        try
        {
            IntPtr remote = VirtualAllocEx(hProc, IntPtr.Zero, (IntPtr)size, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
            if (remote == IntPtr.Zero) return false;
            try
            {
                WriteProcessMemory(hProc, remote, ref info, (IntPtr)size, out _);
                SendMessage(listView, LVM_HITTEST, IntPtr.Zero, remote);
                ReadProcessMemory(hProc, remote, out info, (IntPtr)size, out _);
            }
            finally
            {
                VirtualFreeEx(hProc, remote, IntPtr.Zero, MEM_RELEASE);
            }
        }
        finally
        {
            CloseHandle(hProc);
        }

        return (info.flags & LVHT_ONITEM) != 0;
    }

    /// <summary>
    /// 判定某点是否落在「空白桌面」上（可触发双击切换），排除：
    /// - 桌面图标（SysListView32 命中到图标/文字）；
    /// - 文件资源管理器等其他窗口。
    /// </summary>
    public static bool IsDesktopBackground(IntPtr hwndAtPoint, NativeMethods.POINT ptScreen)
    {
        EnsureDesktopWindows();
        if (_desktopProgman == IntPtr.Zero) return false;
        if (!IsDesktopTreeWindow(hwndAtPoint)) return false;

        if (_desktopListView != IntPtr.Zero && IsSameOrDescendant(_desktopListView, hwndAtPoint))
        {
            if (IsIconAtPoint(_desktopListView, ptScreen)) return false;
        }
        return true;
    }

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out NativeMethods.POINT lpPoint);

    public struct MSLLHOOKSTRUCT { public NativeMethods.POINT pt; public uint mouseData; public uint flags; public uint time; public IntPtr dwExtraInfo; }
}
