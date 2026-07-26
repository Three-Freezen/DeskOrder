using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using DesktopZones.Helpers;
using DesktopZones.Services;
using DesktopZones.Views;

namespace DesktopZones;

public partial class App : System.Windows.Application
{
    private TrayIconService? _trayIcon;
    private ZoneManager? _zoneManager;
    private ConfigService? _configService;
    private NotesService? _notesService;
    private WidgetService? _widgetService;
    private ReminderService? _reminderService;
    private ManagementWindow? _managementWindow;
    private readonly LocalizationService _loc = LocalizationService.Instance;
    private static Mutex? _mutex;

    // Track open widget windows
    internal readonly Dictionary<Guid, Window> _noteWindows = new();
    internal readonly Dictionary<Guid, Window> _clockWindows = new();
    internal readonly Dictionary<Guid, Window> _calendarWindows = new();
    internal Window? _panelWindow;

    // Public accessors for live preview lookup
    public Window? PanelWindow => _panelWindow;
    public ClockWidget? GetClockWindow(Guid id) => _clockWindows.TryGetValue(id, out var w) && w is ClockWidget cw ? cw : null;
    public CalendarWidget? GetCalendarWindow(Guid id) => _calendarWindows.TryGetValue(id, out var w) && w is CalendarWidget cal ? cal : null;

    // ── Global hotkey ──
    private const int WM_HOTKEY = 0x0312;
    private const int HOTKEY_ID_BASE = 0x4000;
    private const int HOTKEY_ID_PANEL = 0x3FFF;
    private readonly Dictionary<int, Guid> _hotkeyToNoteId = new();
    private readonly Dictionary<Guid, int> _noteIdToHotkeyId = new();
    private IntPtr _mainHwnd;

    private void Application_Startup(object sender, StartupEventArgs e)
    {
        // Single-instance check
        _mutex = new Mutex(true, "DeskOrder_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            Shutdown();
            return;
        }

        // Global crash guard — show error instead of crashing silently
        DispatcherUnhandledException += (s, args) =>
        {
            System.Diagnostics.Debug.WriteLine($"[DeskOrder] Unhandled: {args.Exception}");
            MessageBox.Show($"Unhandled error:\n{args.Exception.Message}\n\n{args.Exception.StackTrace}",
                "DeskOrder Error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        _configService = new ConfigService();
        _zoneManager = new ZoneManager(_configService);
        _notesService = new NotesService(_configService);
        _widgetService = new WidgetService(_configService);

        var appIcon = IconToImageSource(CreateAppIcon());

        MainWindow = new Window
        {
            Width = 0, Height = 0,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
            Left = -100, Top = -100,
            Icon = appIcon
        };
        MainWindow.Show();
        MainWindow.Hide();

        // Hook WndProc for hotkey messages
        _mainHwnd = new WindowInteropHelper(MainWindow).Handle;
        var source = HwndSource.FromHwnd(_mainHwnd);
        source?.AddHook(HotkeyWndProc);

        CreateTrayIcon();

        // Listen to language changes
        _loc.LanguageChanged += _ => UpdateTrayTooltip();

        _zoneManager.Initialize();

        var config = _configService.Load();
        // Apply persisted language preference
        if (!string.IsNullOrEmpty(config.Language))
        {
            _loc.CurrentLanguage = config.Language switch
            {
                "en" => Services.Language.English,
                _ => Services.Language.Chinese
            };
        }

        // Load existing widgets
        _notesService.Load(config);
        _widgetService.Load(config);

        // Subscribe to change events for cross-component sync
        _notesService.NotesChanged += SyncNotes;
        _notesService.NotesChanged += RefreshNoteHotkeys;

        // Register hotkeys for notes that have them enabled
        RefreshNoteHotkeys();

        // Register panel hotkey
        if (config.PanelHotkeyEnabled && _mainHwnd != IntPtr.Zero)
        {
            bool ok = NativeMethods.RegisterHotKey(_mainHwnd, HOTKEY_ID_PANEL, (uint)config.PanelHotkeyModifiers, (uint)config.PanelHotkeyKey);
            if (!ok)
            {
                // Hotkey registration failed — likely conflict with another app or system shortcut
                System.Diagnostics.Debug.WriteLine($"[DeskOrder] Failed to register panel hotkey: 0x{config.PanelHotkeyModifiers:X}+0x{config.PanelHotkeyKey:X}");
            }
        }

        // Initialize reminder service
        if (_trayIcon != null)
        {
            _reminderService = new ReminderService(_trayIcon, _widgetService!, _configService!);
            _reminderService.CheckMissedReminders();
            _reminderService.Start();
        }

        // Notes, clocks, and calendars are managed by ManagementWindow — no need to open here

        if (!config.StartMinimized)
            ShowManagementWindow();
    }

    // ── Hotkey WndProc ──

    private IntPtr HotkeyWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (_hotkeyToNoteId.TryGetValue(id, out var noteId))
            {
                ToggleNoteByHotkey(noteId);
            }
            if (id == HOTKEY_ID_PANEL)
            {
                if (_managementWindow != null)
                {
                    _managementWindow.TogglePanel();
                }
                else
                {
                    ShowManagementWindow();
                    _managementWindow?.TogglePanel();
                }
                handled = true;
                return IntPtr.Zero;
            }
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void ToggleNoteByHotkey(Guid noteId)
    {
        if (_noteWindows.TryGetValue(noteId, out var window))
        {
            if (window.IsVisible)
            {
                if (window.IsActive)
                {
                    // Already visible and active → hide it
                    window.Hide();
                }
                else
                {
                    // Visible but not active → bring to front
                    if (window is StickyNoteWindow snw)
                        snw.BringToFront();
                    else
                    {
                        window.Activate();
                        window.Topmost = true;
                    }
                }
            }
            else
            {
                // Hidden → show and bring to front
                window.Show();
                if (window is StickyNoteWindow snw)
                    snw.BringToFront();
            }
        }
        else
        {
            var note = _notesService?.Notes.FirstOrDefault(n => n.Id == noteId);
            if (note != null)
                OpenNoteWindow(note);
        }
    }

    // ── Note window management (used by ManagementWindow) ──

    public void ToggleNoteWindow(Models.StickyNote note)
    {
        if (_noteWindows.TryGetValue(note.Id, out var window))
        {
            if (window.IsVisible)
            {
                if (window is StickyNoteWindow snw)
                {
                    if (snw.MainContent.Visibility == Visibility.Visible) snw.HideNote();
                    else snw.ShowNote();
                }
                else
                {
                    window.Hide();
                }
            }
            else
            {
                if (window is StickyNoteWindow snw) snw.ShowNote();
                else window.Show();
            }
        }
        else
        {
            note.IsVisible = true;
            OpenNoteWindow(note);
        }
    }

    public void OpenNoteWindowFromManager(Models.StickyNote note)
    {
        if (_noteWindows.ContainsKey(note.Id)) return;
        note.IsVisible = true;
        OpenNoteWindow(note);
    }

    public bool IsNoteWindowOpen(Guid noteId) => _noteWindows.ContainsKey(noteId);

    public void RefreshNoteHotkeys()
    {
        if (_notesService == null || _mainHwnd == IntPtr.Zero) return;

        // Unregister all existing hotkeys
        foreach (var id in _noteIdToHotkeyId.Values)
            NativeMethods.UnregisterHotKey(_mainHwnd, id);
        _hotkeyToNoteId.Clear();
        _noteIdToHotkeyId.Clear();

        // Register hotkeys for enabled notes
        int nextId = HOTKEY_ID_BASE;
        foreach (var note in _notesService.Notes)
        {
            if (note.HotkeyEnabled)
            {
                bool ok = NativeMethods.RegisterHotKey(_mainHwnd, nextId, (uint)note.HotkeyModifiers, (uint)note.HotkeyKey);
                if (ok)
                {
                    _hotkeyToNoteId[nextId] = note.Id;
                    _noteIdToHotkeyId[note.Id] = nextId;
                    nextId++;
                }
            }
        }
    }

    public IntPtr GetMainHwnd() => _mainHwnd;

    // ── Tray ──

    private void CreateTrayIcon()
    {
        var appIcon = CreateAppIcon();
        _trayIcon = new TrayIconService(appIcon, _loc["App.TrayTooltip"]);
        UpdateTrayEvents();
    }

    private void UpdateTrayTooltip()
    {
        _trayIcon?.UpdateTooltip(_loc["App.TrayTooltip"]);
        UpdateTrayEvents();
    }

    private void UpdateTrayEvents()
    {
        if (_trayIcon == null) return;

        _trayIcon.LeftClick -= TrayLeftClick;
        _trayIcon.DoubleClick -= TrayDoubleClick;
        _trayIcon.ShowAllZones -= TrayShowAll;
        _trayIcon.HideAllZones -= TrayHideAll;
        _trayIcon.NewZone -= TrayNewZone;
        _trayIcon.NewNote -= TrayNewNote;
        _trayIcon.NewClock -= TrayNewClock;
        _trayIcon.NewCalendar -= TrayNewCalendar;
        _trayIcon.ManageZones -= TrayManage;
        _trayIcon.Exit -= TrayExit;

        _trayIcon.LeftClick += TrayLeftClick;
        _trayIcon.DoubleClick += TrayDoubleClick;
        _trayIcon.ShowAllZones += TrayShowAll;
        _trayIcon.HideAllZones += TrayHideAll;
        _trayIcon.NewZone += TrayNewZone;
        _trayIcon.NewNote += TrayNewNote;
        _trayIcon.NewClock += TrayNewClock;
        _trayIcon.NewCalendar += TrayNewCalendar;
        _trayIcon.ManageZones += TrayManage;
        _trayIcon.Exit += TrayExit;
    }

    private void TrayLeftClick() { /* no-op: don't auto-restore zones */ }
    private void TrayDoubleClick() => ShowManagementWindow();
    private void TrayShowAll() => _zoneManager?.ShowAll();
    private void TrayHideAll() => _zoneManager?.HideAll();
    private void TrayNewZone() { _zoneManager?.CreateZone(); ShowManagementWindow(); }
    private void TrayNewNote()
    {
        try
        {
            if (_notesService == null) return;
            var wa = System.Windows.SystemParameters.WorkArea;
            var note = _notesService.CreateNote(wa.Left + (wa.Width - 260) / 2, wa.Top + (wa.Height - 200) / 2);
            OpenNoteWindow(note);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Failed to create note:\n{ex.Message}", "DeskOrder");
        }
    }

    private void TrayNewClock()
    {
        try
        {
            if (_widgetService == null) return;
            var wa = System.Windows.SystemParameters.WorkArea;
            _widgetService.CreateClock(wa.Left + 300, wa.Top + 80);
            ShowManagementWindow();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Failed to create clock:\n{ex.Message}", "DeskOrder");
        }
    }

    private void TrayNewCalendar()
    {
        try
        {
            if (_widgetService == null) return;
            var wa = System.Windows.SystemParameters.WorkArea;
            _widgetService.CreateCalendar(wa.Left + 400, wa.Top + 80);
            ShowManagementWindow();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Failed to create calendar:\n{ex.Message}", "DeskOrder");
        }
    }

    private void TrayManage() => ShowManagementWindow();
    private void TrayExit() => ShutdownApplication();

    // ── Widget window management ──

    private void OpenNoteWindow(Models.StickyNote note)
    {
        if (_noteWindows.ContainsKey(note.Id)) return;
        var window = new StickyNoteWindow(note, _notesService!);
        window.Closed += (_, _) => _noteWindows.Remove(note.Id);
        _noteWindows[note.Id] = window;
        window.Show();
    }

    private System.Windows.Media.ImageSource? _appIconImage;

    private void ShowManagementWindow()
    {
        if (_managementWindow == null)
        {
            _managementWindow = new ManagementWindow(_zoneManager!, _configService!, _notesService, _widgetService);
            _managementWindow.Closed += (_, _) => _managementWindow = null;
            // Set the custom icon
            if (_appIconImage == null) _appIconImage = IconToImageSource(CreateAppIcon());
            _managementWindow.Icon = _appIconImage;
        }
        _managementWindow.Show();
        _managementWindow.Activate();
        _managementWindow.WindowState = WindowState.Normal;
    }

    private Icon CreateAppIcon()
    {
        // Load icon from embedded resource or file
        string iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "Icons", "DesktopZones.ico");
        if (System.IO.File.Exists(iconPath))
        {
            return new Icon(iconPath, 32, 32);
        }

        // Fallback: create a simple blue icon if file not found
        int size = 32;
        var bitmap = new Bitmap(size, size);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.Transparent);

        using var brush = new SolidBrush(Color.FromArgb(30, 136, 229)); // #1E88E5
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.FillRectangle(brush, 2, 2, size - 4, size - 4);

        // Simple grid pattern
        using var lineBrush = new SolidBrush(Color.FromArgb(200, 255, 255, 255));
        g.FillRectangle(lineBrush, size / 2 - 1, 4, 2, size - 8);
        g.FillRectangle(lineBrush, 4, size / 2 - 1, size - 8, 2);

        return Icon.FromHandle(bitmap.GetHicon());
    }

    private static System.Windows.Media.ImageSource IconToImageSource(Icon icon)
    {
        using var bmp = icon.ToBitmap();
        var hBitmap = bmp.GetHbitmap();
        try
        {
            return System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, IntPtr.Zero, System.Windows.Int32Rect.Empty,
                System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
        }
        finally { NativeMethods.DeleteObject(hBitmap); }
    }

    // ── Sync methods: close windows for deleted widgets ──

    void SyncNotes()
    {
        if (_notesService == null) return;
        var activeIds = new HashSet<Guid>(_notesService.Notes.Select(n => n.Id));
        foreach (var kv in _noteWindows.ToList())
        {
            if (!activeIds.Contains(kv.Key))
            {
                try { kv.Value.Close(); } catch { }
                _noteWindows.Remove(kv.Key);
            }
        }
    }

    public void RegisterPanelHotkey(int modifiers, int key)
    {
        if (_mainHwnd != IntPtr.Zero)
        {
            NativeMethods.UnregisterHotKey(_mainHwnd, HOTKEY_ID_PANEL);
            NativeMethods.RegisterHotKey(_mainHwnd, HOTKEY_ID_PANEL, (uint)modifiers, (uint)key);
        }
    }

    public void UnregisterPanelHotkey()
    {
        if (_mainHwnd != IntPtr.Zero)
            NativeMethods.UnregisterHotKey(_mainHwnd, HOTKEY_ID_PANEL);
    }

    private void ShutdownApplication()
    {
        // Unregister all hotkeys
        if (_mainHwnd != IntPtr.Zero)
        {
            foreach (var id in _noteIdToHotkeyId.Values)
                NativeMethods.UnregisterHotKey(_mainHwnd, id);
            NativeMethods.UnregisterHotKey(_mainHwnd, HOTKEY_ID_PANEL);
        }
        _reminderService?.Dispose();
        _zoneManager?.Shutdown();
        _trayIcon?.Dispose();
        Current.Shutdown();
    }

    private void Application_Exit(object sender, ExitEventArgs e)
    {
        if (_mainHwnd != IntPtr.Zero)
        {
            foreach (var id in _noteIdToHotkeyId.Values)
                NativeMethods.UnregisterHotKey(_mainHwnd, id);
            NativeMethods.UnregisterHotKey(_mainHwnd, HOTKEY_ID_PANEL);
        }
        _reminderService?.Dispose();
        _zoneManager?.Shutdown();
        _trayIcon?.Dispose();
    }
}
