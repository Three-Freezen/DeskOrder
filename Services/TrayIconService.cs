using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;

namespace DesktopZones.Services;

/// <summary>
/// System tray icon using pure P/Invoke Shell_NotifyIcon + WPF ContextMenu.
/// </summary>
public class TrayIconService : IDisposable
{
    private const int NIM_ADD = 0x00000000;
    private const int NIM_MODIFY = 0x00000001;
    private const int NIM_DELETE = 0x00000002;
    private const int NIF_MESSAGE = 0x00000001;
    private const int NIF_ICON = 0x00000002;
    private const int NIF_TIP = 0x00000004;
    private const int WM_TRAYICON = 0x8000;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;

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

    private IntPtr _hWnd;
    private bool _isCreated;
    private Icon _icon;
    private bool _disposed;

    public event Action? LeftClick;
    public event Action? RightClick;
    public event Action? DoubleClick;
    public event Action? ShowAllZones;
    public event Action? HideAllZones;
    public event Action? NewZone;
    public event Action? NewNote;
    public event Action? NewClock;
    public event Action? NewCalendar;
    public event Action? ManageZones;
    public event Action? Exit;

    public TrayIconService(Icon icon, string tooltip = "DeskOrder")
    {
        _icon = icon;

        var helper = new System.Windows.Interop.WindowInteropHelper(
            Application.Current.MainWindow ?? new Window());
        helper.EnsureHandle();
        _hWnd = helper.Handle;

        AddIcon(tooltip);
        _isCreated = true;

        var source = System.Windows.Interop.HwndSource.FromHwnd(_hWnd);
        source?.AddHook(WndProc);
    }

    private void AddIcon(string tooltip)
    {
        var nid = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf(typeof(NOTIFYICONDATA)),
            hWnd = _hWnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = _icon.Handle,
            szTip = tooltip
        };
        Shell_NotifyIcon(NIM_ADD, ref nid);
    }

    public void UpdateTooltip(string tooltip)
    {
        var nid = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf(typeof(NOTIFYICONDATA)),
            hWnd = _hWnd,
            uID = 1,
            uFlags = NIF_TIP,
            szTip = tooltip
        };
        Shell_NotifyIcon(NIM_MODIFY, ref nid);
    }

    private const int NIF_INFO = 0x00000010;
    private const int NIIF_INFO = 0x00000001;

    public void ShowBalloonTip(string title, string message, int timeoutMs = 5000)
    {
        var nid = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf(typeof(NOTIFYICONDATA)),
            hWnd = _hWnd,
            uID = 1,
            uFlags = NIF_INFO,
            szInfoTitle = title.Length > 63 ? title[..63] : title,
            szInfo = message.Length > 255 ? message[..255] : message,
            dwInfoFlags = NIIF_INFO,
            uVersion = 3
        };
        Shell_NotifyIcon(NIM_MODIFY, ref nid);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
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

        var showAll = new MenuItem { Header = loc["Tray.ShowAll"] };
        showAll.Click += (_, _) => ShowAllZones?.Invoke();

        var hideAll = new MenuItem { Header = loc["Tray.HideAll"] };
        hideAll.Click += (_, _) => HideAllZones?.Invoke();

        menu.Items.Add(showAll);
        menu.Items.Add(hideAll);
        menu.Items.Add(new Separator());

        var newZone = new MenuItem { Header = loc["Tray.NewZone"] };
        newZone.Click += (_, _) => NewZone?.Invoke();

        menu.Items.Add(newZone);
        menu.Items.Add(new Separator());

        var newNote = new MenuItem { Header = loc["Tray.NewNote"] };
        newNote.Click += (_, _) => NewNote?.Invoke();
        var newClock = new MenuItem { Header = loc["Tray.NewClock"] };
        newClock.Click += (_, _) => NewClock?.Invoke();
        var newCalendar = new MenuItem { Header = loc["Tray.NewCalendar"] };
        newCalendar.Click += (_, _) => NewCalendar?.Invoke();
        menu.Items.Add(newNote);
        menu.Items.Add(newClock);
        menu.Items.Add(newCalendar);
        menu.Items.Add(new Separator());

        var manage = new MenuItem { Header = loc["Tray.Manage"] };
        manage.Click += (_, _) => ManageZones?.Invoke();

        menu.Items.Add(manage);
        menu.Items.Add(new Separator());

        // Language submenu
        var langMenu = new MenuItem { Header = loc["Menu.Language"] };
        var chineseItem = new MenuItem
        {
            Header = loc["Menu.Chinese"],
            IsChecked = loc.CurrentLanguage == Language.Chinese
        };
        chineseItem.Click += (_, _) => { loc.CurrentLanguage = Language.Chinese; };
        var englishItem = new MenuItem
        {
            Header = loc["Menu.English"],
            IsChecked = loc.CurrentLanguage == Language.English
        };
        englishItem.Click += (_, _) => { loc.CurrentLanguage = Language.English; };
        langMenu.Items.Add(chineseItem);
        langMenu.Items.Add(englishItem);
        menu.Items.Add(langMenu);

        menu.Items.Add(new Separator());

        var exit = new MenuItem { Header = loc["Tray.Exit"] };
        exit.Click += (_, _) => Exit?.Invoke();
        menu.Items.Add(exit);

        menu.IsOpen = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_isCreated)
        {
            var nid = new NOTIFYICONDATA { cbSize = Marshal.SizeOf(typeof(NOTIFYICONDATA)), hWnd = _hWnd, uID = 1 };
            Shell_NotifyIcon(NIM_DELETE, ref nid);
            _isCreated = false;
        }
        _icon?.Dispose();
    }
}
