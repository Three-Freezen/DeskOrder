using System;
using System.Runtime.InteropServices;
using DesktopZones.Helpers;

namespace DesktopZones.Views;

/// <summary>
/// 快捷栏的「玻璃层」—— 一个**裸 Win32 窗口**（第 7 轮 B）。
///
/// **为什么需要第二个窗口**：快捷栏的内容窗口比可见胶囊大（上方放大余量 + 下方倒影带 +
/// 左右放大余量），而 DWM 的亚克力背板**只能整窗**、`SetWindowRgn` 对
/// `AllowsTransparency=True` 的**分层**窗口又完全无效（第 7 轮实测：整窗 353×131 一起被糊，
/// 裁与不裁逐像素一致，见 `--dock-lab-case=dwmscope`）。所以想让胶囊里有**真**玻璃
/// —— 实景模糊，动态壁纸 / 壁纸引擎的画面会跟着实时变 —— 只能把玻璃做成一个
/// **正好胶囊大小**、且**非分层**（能被窗口区域裁成圆角）的窗口，垫在内容窗口正下方。
///
/// <code>
///   内容窗口（WPF 分层，逐像素 alpha）：图标 / 放大溢出 / 倒影 / 拖拽 / 菜单
///                                      胶囊区铺 HitTestFill（alpha=1）抢点击
///   玻璃窗口（本类，裸 HWND + 圆角区域）：整块由 DWM accent 背板填满（模糊 + 着色）
/// </code>
///
/// **为什么必须是裸 HWND 而不是 WPF Window**：非分层的 WPF 窗口表面没有 alpha，
/// `Background=Transparent` 会被合成成**不透明黑**（第 7 轮实测：胶囊变成一块黑板），
/// 把 DWM 背板整个盖住。裸窗口只要**不画**（窗口类 `hbrBackground = NULL`，WM_PAINT 只
/// 做 BeginPaint/EndPaint），屏幕上就只剩 DWM 的背板。
///
/// 它点击穿透（`WS_EX_TRANSPARENT` + `WM_NCHITTEST → HTTRANSPARENT`）、永不激活
/// （`WS_EX_NOACTIVATE` + `WM_MOUSEACTIVATE → MA_NOACTIVATE`）、不进任务栏/Alt-Tab
/// （`WS_EX_TOOLWINDOW`），命中检测全部交给上面的内容窗口。
/// </summary>
sealed class DockGlassWindow : IDisposable
{
    const string ClassName = "DeskOrderDockGlass";

    // ── Win32 ──
    delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public int fErase;
        public int left, top, right, bottom;
        public int fRestore, fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr hInstance, IntPtr param);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT ps);
    [DllImport("user32.dll")] static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT ps);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);
    [DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after,
        int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hWnd);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    static WndProcDelegate? _proc;          // 必须留引用：委托被 GC 后回调会崩
    static bool _classReady;

    /// <summary>最后一次失败的原因（自测/日志用）。</summary>
    public static string LastError { get; private set; } = "";

    const int WS_POPUP = unchecked((int)0x80000000);
    const int WS_EX_TOOLWINDOW = 0x00000080;
    const int WS_EX_TRANSPARENT = 0x00000020;
    const int WS_EX_NOACTIVATE = 0x08000000;
    const uint SWP_NOACTIVATE = 0x0010, SWP_NOOWNERZORDER = 0x0200;
    const int SW_SHOWNOACTIVATE = 4, SW_HIDE = 0;
    const uint WM_PAINT = 0x000F, WM_ERASEBKGND = 0x0014, WM_NCHITTEST = 0x0084, WM_MOUSEACTIVATE = 0x0021;
    const int HTTRANSPARENT = -1, MA_NOACTIVATE = 3;
    const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_DONOTROUND = 1;

    IntPtr _hwnd;

    public IntPtr Handle => _hwnd;
    public bool IsAlive => _hwnd != IntPtr.Zero && IsWindow(_hwnd);
    public bool IsVisibleGlass => _hwnd != IntPtr.Zero && IsWindowVisible(_hwnd);
    public bool HasGlass { get; private set; }

    static void EnsureClass()
    {
        if (_classReady) return;
        _proc = StaticProc;
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            style = 0,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = GetModuleHandle(null),
            hbrBackground = IntPtr.Zero,        // 关键：没有背景刷 → 不画 → DWM 背板透出来
            lpszClassName = ClassName,
        };
        RegisterClassEx(ref wc);
        int err = Marshal.GetLastWin32Error();
        _classReady = true;
        if (err != 0 && err != 1410 /*ERROR_CLASS_ALREADY_EXISTS*/)
            LastError = $"RegisterClassEx err={err}";
    }

    void EnsureWindow()
    {
        if (IsAlive) return;
        EnsureClass();
        _hwnd = CreateWindowEx(WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE,
            ClassName, "", WS_POPUP, -32000, -32000, 1, 1,
            IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            LastError = $"CreateWindowEx err={err} classReady={_classReady}";
            DzTrace.Log($"[DockGlassWindow] {LastError}");
            return;
        }
        try
        {
            int pref = DWMWCP_DONOTROUND;        // 形状由区域决定，别再叠 Win11 整窗圆角
            DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
        }
        catch { }
    }

    static IntPtr StaticProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_PAINT:                       // 什么都不画，只是把区域标成已处理
                {
                    var hdc = BeginPaint(hWnd, out var ps);
                    _ = hdc;
                    EndPaint(hWnd, ref ps);
                    return IntPtr.Zero;
                }
            case WM_ERASEBKGND:
                return new IntPtr(1);            // 不用擦背景
            case WM_NCHITTEST:
                return new IntPtr(HTTRANSPARENT);   // 命中穿透（配合 WS_EX_TRANSPARENT）
            case WM_MOUSEACTIVATE:
                return new IntPtr(MA_NOACTIVATE);
            default:
                return DefWindowProc(hWnd, msg, wParam, lParam);
        }
    }

    /// <summary>摆到胶囊的屏幕矩形（**物理像素**），并插到 <paramref name="below"/> 窗口正下方
    /// （= 内容窗口下面）。regionRadiusPx 是胶囊圆角的像素半径。</summary>
    public bool SyncTo(IntPtr below, int x, int y, int w, int h, int regionRadiusPx)
    {
        EnsureWindow();
        if (_hwnd == IntPtr.Zero || w < 1 || h < 1) return false;
        int d = Math.Max(0, regionRadiusPx) * 2;   // CreateRoundRectRgn 收的是椭圆的宽高 = 直径
        SetWindowRgn(_hwnd, CreateRoundRectRgn(0, 0, w, h, d, d), true);
        return SetWindowPos(_hwnd, below, x, y, w, h, SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    /// <summary>上玻璃配方 —— 与分区/律动条同一个 <see cref="AcrylicHelper.EnableBlurComposite"/>。</summary>
    public BlurResult ApplyGlass(int blurAmount, string? fillHex, double fillOpacity01,
        string glassMode, int tintOpacity, int tintLuminosity, int accentState, bool useClassicBlur)
    {
        EnsureWindow();
        if (_hwnd == IntPtr.Zero) return BlurResult.Fail("no hwnd");
        var r = AcrylicHelper.EnableBlurComposite(_hwnd, blurAmount, fillHex, fillOpacity01, glassMode,
            tintOpacity, tintLuminosity, skipClassicBlur: !useClassicBlur, accentState: accentState);
        HasGlass = r.Success;
        return r;
    }

    public void ShowNoActivate()
    {
        EnsureWindow();
        if (_hwnd == IntPtr.Zero) return;
        ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
    }

    public void HideGlass()
    {
        if (_hwnd == IntPtr.Zero) return;
        ShowWindow(_hwnd, SW_HIDE);
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero && IsWindow(_hwnd))
        {
            try { DestroyWindow(_hwnd); } catch { }
        }
        _hwnd = IntPtr.Zero;
        HasGlass = false;
    }
}
