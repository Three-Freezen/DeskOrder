using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;

namespace DesktopZones.Services;

/// <summary>
/// System tray icon using pure P/Invoke Shell_NotifyIcon + WPF ContextMenu.
///
/// 2026-10-24 加固（用户报"托盘图标不见了"）：
/// - **固定 GUID 身份**（NIF_GUID）：以前按 (hWnd, uID) 标识，换个 exe 路径 / 重装一次，
///   系统就当成"新图标"（默认进折叠区、之前的"始终显示"设置全丢）；用固定 GUID 后，
///   无论从 bin\Debug 还是安装目录启动，系统都记得它。
/// - **TaskbarCreated 重建**：explorer.exe 重启（崩溃、更新、注销重登）时系统会广播
///   "TaskbarCreated"，所有图标被清空、必须由程序自己重新添加。以前没有处理，于是
///   explorer 一重启图标就永远消失了。
/// - **失败重试 + 落盘日志**：登录瞬间 shell 还没就绪时 NIM_ADD 会失败；现在退避重试
///   若干次，并把每次失败原因写进 dz_trace.log（Release 也写），不再静默。
/// </summary>
public class TrayIconService : IDisposable
{
    private const int NIM_ADD = 0x00000000;
    private const int NIM_MODIFY = 0x00000001;
    private const int NIM_DELETE = 0x00000002;
    private const int NIF_MESSAGE = 0x00000001;
    private const int NIF_ICON = 0x00000002;
    private const int NIF_TIP = 0x00000004;
    private const int NIF_GUID = 0x00000020;
    private const int WM_TRAYICON = 0x8000;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;

    /// <summary>托盘图标的固定身份。不要改 —— 一改就等于换了个图标，系统的"始终显示"
    /// 设置会重来一遍（用户可以随时改 GUID，但要接受这个副作用）。</summary>
    private static readonly Guid TrayGuid = new("6E1F3B2A-9C4D-4A77-9F2E-3B7C1D5A8E64");

    private const int MaxAddAttempts = 12;
    private const int RetryIntervalMs = 1500;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public int uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    private IntPtr _hWnd;
    private bool _isCreated;
    private Icon _icon;
    private bool _disposed;
    private string _tooltip = "DeskOrder";
    private uint _taskbarCreatedMsg;
    private System.Windows.Threading.DispatcherTimer? _retryTimer;
    private int _addAttempts;
    /// <summary>当前用哪种身份注册：true = 固定 GUID，false = 传统 (hWnd, uID)。</summary>
    private bool _preferGuid = true;

    /// <summary>承载托盘回调的**自有**隐藏窗口。
    ///
    /// 以前直接借 <c>Application.Current.MainWindow</c> 的句柄：那个窗口一旦被
    /// 关闭 / 重建（"全部隐藏"、切换外观、关机流程），HWND 销毁后系统会把托盘图标
    /// 一起清掉，"图标无缘无故不见了"就是这么来的。现在自己建一个 0×0 的顶层隐藏窗口，
    /// 生命周期跟进程一致，且不受 WPF 窗口管理影响。</summary>
    private System.Windows.Interop.HwndSource? _hostSource;

    /// <summary>True when the icon was actually registered with the shell (NIM_ADD
    /// succeeded). When false there is no tray affordance, so callers should provide
    /// an alternate way to reach the app (e.g. show the management window).</summary>
    public bool IsAvailable { get; private set; }

    public event Action? LeftClick;
    public event Action? RightClick;
    public event Action? DoubleClick;
    public event Action? ManageZones;
    public event Action? Exit;

    /// <summary>Raised when a Shell_NotifyIcon call fails. Payload is a short diagnostic message.</summary>
    public event Action<string>? NotifyError;

    public TrayIconService(Icon icon, string tooltip = "DeskOrder")
    {
        _icon = icon;
        _tooltip = tooltip;

        // 自有的 0×0 顶层隐藏窗口：能收到 TaskbarCreated 广播，又不会被界面流程关掉。
        var p = new System.Windows.Interop.HwndSourceParameters("DeskOrderTrayHost")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,          // 不带 WS_VISIBLE → 不显示
            ExtendedWindowStyle = 0,
        };
        _hostSource = new System.Windows.Interop.HwndSource(p);
        _hWnd = _hostSource.Handle;

        try { _taskbarCreatedMsg = RegisterWindowMessage("TaskbarCreated"); } catch { }

        AddIcon();
        _isCreated = true;

        _hostSource.AddHook(WndProc);
    }

    private NOTIFYICONDATA BaseData(bool? byGuid = null)
    {
        bool useGuid = byGuid ?? _preferGuid;
        return new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf(typeof(NOTIFYICONDATA)),
            hWnd = _hWnd,
            uID = 1,
            // 固定 GUID：图标身份跟 GUID 走，不再依赖窗口句柄 / exe 路径。
            uFlags = useGuid ? NIF_GUID : 0,
            guidItem = useGuid ? TrayGuid : Guid.Empty,
            szTip = _tooltip,
        };
    }

    private void AddIcon()
    {
        _addAttempts++;
        IsAvailable = TryAdd(byGuid: _preferGuid);
        if (!IsAvailable && _preferGuid)
        {
            // 固定 GUID 身份在个别系统/策略下会失败：退回旧的 (hWnd, uID) 身份，
            // 至少图标能出来（代价是换 exe 路径后 Windows 会当成新图标）。
            Helpers.DzTrace.Always("[Tray] GUID 身份注册失败，回退到 hWnd/uID 身份");
            _preferGuid = false;
            IsAvailable = TryAdd(byGuid: false);
        }

        if (IsAvailable)
        {
            if (_addAttempts > 1)
                Helpers.DzTrace.Always($"[Tray] 图标注册成功（第 {_addAttempts} 次尝试，guid={_preferGuid}）");
            _retryTimer?.Stop();
            return;
        }

        int err = Marshal.GetLastWin32Error();
        Helpers.DzTrace.Always(
            $"[Tray] NIM_ADD 失败（第 {_addAttempts} 次）hWnd=0x{_hWnd.ToInt64():X} err={err}" +
            (err == 5 ? " ACCESS_DENIED —— 进程完整性太低或没有可用的任务栏（从沙箱/托管进程启动会这样）" : "") +
            (Helpers.ProcessIntegrity.RestrictionHint.Length > 0
                ? "；" + Helpers.ProcessIntegrity.RestrictionHint
                : ""));
        NotifyError?.Invoke($"Shell_NotifyIcon(NIM_ADD) failed (err={err})");

        // 低完整性（沙箱）下永远注册不上：别每 1.5s 重试到天荒地老，证据已经写进日志了。
        if (Helpers.ProcessIntegrity.IsRestricted) return;

        // 登录瞬间 / explorer 正在重启时 shell 还没准备好：退避重试，别就此放弃。
        if (_addAttempts >= MaxAddAttempts)
        {
            _retryTimer?.Stop();
            return;
        }
        try
        {
            _retryTimer ??= new System.Windows.Threading.DispatcherTimer(
                TimeSpan.FromMilliseconds(RetryIntervalMs),
                System.Windows.Threading.DispatcherPriority.Background,
                (_, _) => { if (!IsAvailable) AddIcon(); else _retryTimer?.Stop(); },
                Application.Current?.Dispatcher ?? System.Windows.Threading.Dispatcher.CurrentDispatcher);
            _retryTimer.Start();
        }
        catch { }
    }

    private bool TryAdd(bool byGuid)
    {
        var nid = BaseData(byGuid);
        nid.uFlags |= NIF_MESSAGE | NIF_ICON | NIF_TIP;
        nid.uCallbackMessage = WM_TRAYICON;
        nid.hIcon = _icon?.Handle ?? IntPtr.Zero;
        return Shell_NotifyIcon(NIM_ADD, ref nid);
    }

    public void UpdateTooltip(string tooltip)
    {
        _tooltip = tooltip;
        var nid = BaseData();
        nid.uFlags |= NIF_TIP;
        if (!Shell_NotifyIcon(NIM_MODIFY, ref nid))
            System.Diagnostics.Debug.WriteLine("[TrayIconService] NIM_MODIFY (tooltip) failed: False");
    }

    private const int NIF_INFO = 0x00000010;
    private const int NIIF_INFO = 0x00000001;

    public void ShowBalloonTip(string title, string message, int timeoutMs = 5000)
    {
        var nid = BaseData();
        nid.uFlags = NIF_INFO;
        nid.szInfoTitle = title.Length > 63 ? title[..63] : title;
        nid.szInfo = message.Length > 255 ? message[..255] : message;
        nid.dwInfoFlags = NIIF_INFO;
        nid.uVersion = 3;
        if (!Shell_NotifyIcon(NIM_MODIFY, ref nid))
            System.Diagnostics.Debug.WriteLine("[TrayIconService] NIM_MODIFY (balloon) failed: False");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // explorer 重启后系统广播 TaskbarCreated：所有图标都没了，必须自己重新加回来。
        if (_taskbarCreatedMsg != 0 && (uint)msg == _taskbarCreatedMsg)
        {
            Helpers.DzTrace.Always("[Tray] 收到 TaskbarCreated，重新注册托盘图标");
            _addAttempts = 0;
            AddIcon();
            handled = true;
            return IntPtr.Zero;
        }

        if (msg == WM_TRAYICON)
        {
            int lp = lParam.ToInt32();
            switch (lp)
            {
                case WM_LBUTTONUP:
                    if (_openMenu != null) { _openMenu.IsOpen = false; _openMenu = null; }
                    LeftClick?.Invoke();
                    break;
                case WM_LBUTTONDBLCLK:
                    DoubleClick?.Invoke();
                    break;
                case WM_RBUTTONUP:
                    ShowContextMenu();
                    RightClick?.Invoke();
                    break;
            }
            handled = true;
        }
        return IntPtr.Zero;
    }

    private ContextMenu? _openMenu;

    private void ShowContextMenu()
    {
        // Close any previously open menu
        if (_openMenu != null) { _openMenu.IsOpen = false; _openMenu = null; }

        // Activate the parent window so ContextMenu can detect focus loss
        SetForegroundWindow(_hWnd);

        var loc = LocalizationService.Instance;
        var menu = new ContextMenu { StaysOpen = false };
        _openMenu = menu;
        menu.Closed += (_, _) => _openMenu = null;

        // ponytail 2026-08-27: 精简到 3 项 — 管理分区 / 语言 / 退出。ShowAll/HideAll/
        // NewZone/NewNote/NewClock/NewCalendar 已废弃。每次右键重新构建,所以语言
        // 切换后再开菜单自动拿新 i18n(吸取 XAML 静态绑定教训)。

        var manage = new MenuItem { Header = loc["Tray.Manage"] };
        manage.Click += (_, _) => ManageZones?.Invoke();
        menu.Items.Add(manage);

        // Language submenu
        var langMenu = new MenuItem { Header = loc["Menu.Language"] };
        var chineseItem = new MenuItem
        {
            Header = loc["Menu.Chinese"],
            IsChecked = loc.CurrentLanguage == "zh"
        };
        chineseItem.Click += (_, _) => { loc.CurrentLanguage = "zh"; };
        var englishItem = new MenuItem
        {
            Header = loc["Menu.English"],
            IsChecked = loc.CurrentLanguage == "en"
        };
        englishItem.Click += (_, _) => { loc.CurrentLanguage = "en"; };
        langMenu.Items.Add(chineseItem);
        langMenu.Items.Add(englishItem);
        menu.Items.Add(langMenu);

        var exit = new MenuItem { Header = loc["Tray.Exit"] };
        exit.Click += (_, _) => Exit?.Invoke();
        menu.Items.Add(exit);

        menu.IsOpen = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _retryTimer?.Stop(); } catch { }
        _retryTimer = null;

        if (_isCreated)
        {
            var nid = BaseData();
            if (!Shell_NotifyIcon(NIM_DELETE, ref nid))
                System.Diagnostics.Debug.WriteLine("[TrayIconService] NIM_DELETE failed: False");
            _isCreated = false;
        }
        _icon?.Dispose();
        try { _hostSource?.Dispose(); } catch { }
        _hostSource = null;
    }
}
