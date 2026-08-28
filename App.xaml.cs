using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using DesktopZones.Helpers;
using DesktopZones.Models;
using DesktopZones.Services;
using DesktopZones.Views;

namespace DesktopZones;

public partial class App : System.Windows.Application
{
    // ponytail 2026-08-24: 全局托盘气泡通知入口。委托由 InitializeTrayIcon 注入（在那之前调用会静默 no-op）。
    public static Action<string, string>? Notify { get; internal set; }

    private TrayIconService? _trayIcon;
    private ZoneManager? _zoneManager;
    private ConfigService? _configService;
    private NotesService? _notesService;
    private WidgetService? _widgetService;
    private PanelService? _panelService;
    private ReminderService? _reminderService;
    private ManagementWindow? _managementWindow;
    private readonly LocalizationService _loc = LocalizationService.Instance;
    private static Mutex? _mutex;
    private static EventWaitHandle? _activateEvent;
    private static Thread? _activateThread;
    private const string SingleInstanceMutexName = "DeskOrder_SingleInstance";
    private const string ActivateEventName = "DeskOrder_Activate";

    // Public accessors for live preview lookup (forward to services — windows
    // are now owned by NotesService / WidgetService / PanelService, not here)
    public PanelWindow? PanelWindow => _panelService?.Window;
    public ClockWidget? GetClockWindow(Guid id) => _widgetService?.GetClockWindow(id);
    public CalendarWidget? GetCalendarWindow(Guid id) => _widgetService?.GetCalendarWindow(id);
    public PanelService? PanelService => _panelService;
    public NotesService? NotesService => _notesService;
    public WidgetService? WidgetService => _widgetService;
    public ManagementWindow? ManagementWindow => _managementWindow;
    /// <summary>Exposed so panels can sync their field tree when a zone window
    /// mutates state directly (e.g. folder mapping toggled from the zone's ✕).</summary>
    public ZoneManager? ZoneManager => _zoneManager;

    // ── Global hotkey ──
    private const int WM_HOTKEY = 0x0312;
    private const int WM_SETTINGCHANGE = 0x001A;
    private const int HOTKEY_ID_BASE = 0x4000;
    private const int HOTKEY_ID_PANEL = 0x3FFF;
    // ponytail 2026-08-27: 全部显示/隐藏/最小化 全局热键 ID。
    private const int HOTKEY_ID_SHOW_ALL = 0x3FFD;
    private const int HOTKEY_ID_HIDE_ALL = 0x3FFC;
    private const int HOTKEY_ID_MIN_ALL = 0x3FFB;
    private readonly Dictionary<int, Guid> _hotkeyToNoteId = new();
    private readonly Dictionary<Guid, int> _noteIdToHotkeyId = new();
    private IntPtr _mainHwnd;

    private void Application_Startup(object sender, StartupEventArgs e)
    {
#if DEBUG
        // ponytail 2026-08-26: fresh diagnostics log per run (ghost-ring regression trace).
        Helpers.DzTrace.Reset();
#endif
        // ponytail: capture all Debug.WriteLine output to a file so we can post-mortem
        // hover-expand behavior without attaching a debugger.
        try
        {
            var tracePath = @"D:\BS\he_debug.log";
            var tw = new System.IO.StreamWriter(tracePath, append: true) { AutoFlush = true };
            System.Diagnostics.Trace.Listeners.Add(new System.Diagnostics.TextWriterTraceListener(tw));
            System.Diagnostics.Trace.AutoFlush = true;
        }
        catch { }

        // Single-instance check (session-local). The mutex is only an existence
        // marker; a second launch signals the activation event so the running
        // instance surfaces its management window instead of silently dying into
        // an unreaped zombie under a handle-holding launcher.
        bool createdNew;
        try
        {
            _mutex = new Mutex(false, SingleInstanceMutexName, out createdNew);
        }
        catch (UnauthorizedAccessException)
        {
            // Another (typically elevated) instance owns the lock and our lower-
            // integrity process can't open it. Treat as already running and exit.
            Shutdown();
            return;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            Shutdown();
            return;
        }

        if (!createdNew)
        {
            SignalActivation();
            Shutdown();
            return;
        }

        StartActivationListener();

        // Global crash guard — show error instead of crashing silently
        DispatcherUnhandledException += (s, args) =>
        {
            System.Diagnostics.Debug.WriteLine($"[DeskOrder] Unhandled: {args.Exception}");
            var ex = args.Exception;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Outer: {ex.GetType().FullName}");
            for (int i = 0; ex != null && i < 8; i++)
            {
                sb.AppendLine($"[{i}] {ex.GetType().FullName}: {ex.Message}");
                ex = ex.InnerException;
            }
            sb.AppendLine("--- Stack ---");
            sb.AppendLine(args.Exception.StackTrace);
            MessageBox.Show(sb.ToString(), LocalizationService.Instance["App.ErrorTitle"], MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        // Apply runtime theme: load config first so the persisted ThemeMode wins
        // over the OS default on startup. Without this the user picks Dark, restarts,
        // and the window reverts to whatever the OS happens to be set to.
        _configService = new ConfigService();
        ThemeService.Apply(ParseThemeMode(_configService.Load().ThemeMode));
        ThemeService.StartListeningToSystem();
        // 右键菜单调色板:跟随 Windows 系统主题(与应用主题无关,管理界面不受影响)。
        MenuThemeService.Start();
        // 全局菜单钩子:托盘菜单等代码创建的 ContextMenu 也套同一套 Win11 外观 + 毛玻璃。
        AcrylicHelper.EnsureGlobalContextMenuHook();
        _zoneManager = new ZoneManager(_configService);
        _notesService = new NotesService(_configService);
        _widgetService = new WidgetService(_configService);
        _panelService = new PanelService(_zoneManager, _configService);

        var appIcon = AppIcon;

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

        // 自动整理：注入 ZoneManager + 启动时挂载已启用分区；之后跟随 ZonesChanged
        // 自动同步 watcher 集合（规则/监听路径/启停即时生效，删除分区自动卸载）。
        AutoOrganizeService.Instance.Initialize(_zoneManager);
        AutoOrganizeService.Instance.SyncAll(_zoneManager.Zones);
        _zoneManager.ZonesChanged += () => AutoOrganizeService.Instance.SyncAll(_zoneManager.Zones);

        // 逆向同步：原文件消失/变更时自动删除分区图标（事件驱动 FileSystemWatcher）。
        FileSyncService.Instance.Initialize(_zoneManager, _configService.Load().ReverseSyncEnabled);

        // 分区图片预览：同步运行态开关（ZoneItemViewModel 渲染时读取）。
        ShellIconService.ImagePreviewEnabled = _configService.Load().ImagePreviewEnabled;

        var config = _configService.Load();
        // Apply persisted language preference
        if (!string.IsNullOrEmpty(config.Language))
        {
            _loc.CurrentLanguage = config.Language switch
            {
                "en" => "en",
                _ => "zh"
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

        // ponytail: 启动时恢复可见的小组件窗口（位置 + 显示状态持久化，与分区一致）。
        // 只打开模型 IsVisible=true 的组件；隐藏组件保持隐藏。
        EnsureManagementWindow();
        _managementWindow?.RestoreVisibleWidgets();

        // Register panel hotkey
        if (config.PanelHotkey.PanelHotkeyEnabled && _mainHwnd != IntPtr.Zero)
        {
            bool ok = NativeMethods.RegisterHotKey(_mainHwnd, HOTKEY_ID_PANEL, (uint)config.PanelHotkey.PanelHotkeyModifiers, (uint)config.PanelHotkey.PanelHotkeyKey);
            if (!ok)
            {
                // Hotkey registration failed — likely conflict with another app or system shortcut
                System.Diagnostics.Debug.WriteLine($"[DeskOrder] Failed to register panel hotkey: 0x{config.PanelHotkey.PanelHotkeyModifiers:X}+0x{config.PanelHotkey.PanelHotkeyKey:X}");
                _trayIcon?.ShowBalloonTip(_loc["Toast.HotkeyFailed.Title"], _loc.Get("Toast.HotkeyFailed.Body", config.PanelHotkey.PanelHotkeyModifiers, config.PanelHotkey.PanelHotkeyKey));
            }
        }

        // ponytail 2026-08-27: 注册全部显示/隐藏/最小化 全局热键。
        TryRegisterHotkey(HOTKEY_ID_SHOW_ALL, config.ShowAllHotkey.Modifiers, config.ShowAllHotkey.Key);
        TryRegisterHotkey(HOTKEY_ID_HIDE_ALL, config.HideAllHotkey.Modifiers, config.HideAllHotkey.Key);
        TryRegisterHotkey(HOTKEY_ID_MIN_ALL, config.MinimizeAllHotkey.Modifiers, config.MinimizeAllHotkey.Key);

        // ponytail 2026-08-27: 启动 LowLevel 双击桌面监听(若开启)。
        // 当前若有任意 zone/widget/note 处于隐藏态 → 全部显示;否则 → 全部隐藏(完全隐藏)。
        AppDesktopDoubleClick.Install(config.DoubleClickToggleShowHide, () =>
        {
            bool anyHidden = AnyWidgetHidden();
            Dispatcher.Invoke(() =>
            {
                if (anyHidden) TrayShowAll();
                else TrayFullHideAll();
            });
        });

        // Initialize reminder service
        if (_trayIcon != null)
        {
            _reminderService = new ReminderService(_trayIcon, _widgetService!, _configService!);
            _reminderService.CheckMissedReminders();
            _reminderService.Start();
        }

        // Notes, clocks, and calendars are managed by ManagementWindow — no need to open here

        // ── Debug: --load-preset=KIND auto-opens LoadPresetDialog (useful for screenshots) ──
        var debugKind = ParseLoadPresetArg(e.Args);
        if (debugKind.HasValue)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var svc = PresetService.For(debugKind.Value);
                // Use ShowDialog so ApplyButton_Click's DialogResult=true path works
                // (otherwise setting DialogResult on a non-modal Window throws).
                new LoadPresetDialog(svc, _widgetService) { Owner = null }.ShowDialog();
            }), DispatcherPriority.Background);
            return;
        }

        // ── Debug: --test-dialogs instantiates every secondary Window so a crash in
        //    any of them surfaces through DispatcherUnhandledException. Each dialog is
        //    wrapped in its own try/catch so the first failure doesn't mask the rest —
        //    run once, report which title in the error dialog is the first to fail. ──
        if (e.Args.Any(a => a == "--test-dialogs"))
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                void TryDialog(string name, Action open)
                {
                    try { open(); }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[test-dialogs] {name} FAILED: {ex}");
                        // Wrap and re-throw on the dispatcher so the global handler
                        // (which writes the inner-exception chain to a MessageBox) sees it.
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            throw new InvalidOperationException($"--test-dialogs: {name} failed", ex);
                        }));
                    }
                }
                TryDialog("ColorPickerDialog", () => new ColorPickerDialog("FF8800").ShowDialog());
                TryDialog("EmojiPickerDialog", () => new EmojiPickerDialog().ShowDialog());
                TryDialog("IconPickerDialog",  () => new IconPickerDialog().ShowDialog());
                TryDialog("RenameDialog",      () => new RenameDialog("test").ShowDialog());
                TryDialog("SavePresetDialog",  () => new SavePresetDialog(
                    PresetService.For(PresetKind.Zone), new Zone { Name = "test" }) { Owner = null }.ShowDialog());
                TryDialog("LoadPresetDialog",  () => new LoadPresetDialog(
                    PresetService.For(PresetKind.Zone), _widgetService) { Owner = null }.ShowDialog());
                MessageBox.Show("All dialogs tested. Check the error dialog above for any failures.",
                    "--test-dialogs", MessageBoxButton.OK, MessageBoxImage.Information);
            }), DispatcherPriority.Background);
            return;
        }

        // ── Debug: --diag-menu opens a synthetic ContextMenu offscreen and logs
        //    style/template resolution for ContextMenu / MenuItem / Separator. ──
        if (e.Args.Any(a => a == "--diag-menu"))
        {
            Dispatcher.BeginInvoke(new Action(RunContextMenuDiagnostic), DispatcherPriority.ApplicationIdle);
        }

        // ── Debug: --spawn-widget=KIND auto-creates one widget for reference screenshots ──
        var spawnKind = ParseSpawnWidgetArg(e.Args);
        if (spawnKind != null)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var wa = System.Windows.SystemParameters.WorkArea;
                switch (spawnKind)
                {
                    case "clock":
                        var c = _widgetService?.CreateClock(wa.Left + 200, wa.Top + 100);
                        if (c != null) new ClockWidget(c, _widgetService!).Show();
                        break;
                    case "calendar":
                        var cal = _widgetService?.CreateCalendar(wa.Left + 200, wa.Top + 100);
                        if (cal != null) new CalendarWidget(cal, _widgetService!).Show();
                        break;
                    case "stickynote":
                        var note = _notesService?.CreateNote(wa.Left + 200, wa.Top + 100);
                        if (note != null) OpenNoteWindow(note);
                        break;
                    case "panel":
                        _panelService?.Show(_configService!.Load());
                        break;
                }
            }), DispatcherPriority.Background);
        }

        if (!config.StartMinimized)
            ShowManagementWindow();
        else if (_trayIcon is { IsAvailable: false })
        {
            // ponytail: tray icon failed to register — never leave the user with an
            // invisible, unreachable process. Surface the management window instead.
            ShowManagementWindow();
        }
    }

    /// <summary>Signal the running instance to show its management window. Called by
    /// a second launch right before it shuts down.</summary>
    static void SignalActivation()
    {
        try
        {
            using var ev = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
            ev.Set();
        }
        catch { }
    }

    void StartActivationListener()
    {
        try
        {
            _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
            _activateThread = new Thread(ActivationListener) { IsBackground = true, Name = "DeskOrder.Activation" };
            _activateThread.Start();
        }
        catch
        {
            _activateEvent?.Dispose();
            _activateEvent = null;
        }
    }

    /// <summary>Background loop: a second DeskOrder launch sets the activation event,
    /// so surface the management window on the UI thread.</summary>
    void ActivationListener()
    {
        try
        {
            while (_activateEvent != null)
            {
                if (_activateEvent.WaitOne())
                    Dispatcher.BeginInvoke(new Action(ShowManagementWindow));
            }
        }
        catch
        {
            // Event disposed during shutdown — exit the listener silently.
        }
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
                    // Create instance without showing — only need it for TogglePanel
                    _managementWindow = new ManagementWindow(_zoneManager!, _configService!, _notesService, _widgetService, _panelService);
                    _managementWindow.Closed += (_, _) => _managementWindow = null;
                    _managementWindow.Icon = AppIcon;
                    _managementWindow.TogglePanel();
                }
                handled = true;
                return IntPtr.Zero;
            }
            // ponytail 2026-08-27: 全部显示/隐藏/最小化 分发。
            // 与左侧边栏同名按钮保持一致：全部最小化 = 折叠(HideAll)，
            // 全部隐藏 = 完全隐藏(FullHideAll)。此前两者反了。
            if (id == HOTKEY_ID_SHOW_ALL) { TrayShowAll(); handled = true; return IntPtr.Zero; }
            if (id == HOTKEY_ID_MIN_ALL) { TrayHideAll(); handled = true; return IntPtr.Zero; }
            if (id == HOTKEY_ID_HIDE_ALL) { TrayFullHideAll(); handled = true; return IntPtr.Zero; }
            handled = true;
        }
        // ponytail: WM_SETTINGCHANGE with lParam pointing to a setting name is the
        // canonical Win32 hook for Windows Personalization changes. The 1-second
        // DispatcherTimer poll in ThemeService is a fallback for cases where this
        // broadcast doesn't reach our hidden MainWindow (some shell versions don't
        // deliver the broadcast to windows without a title bar / taskbar entry).
        // Strings observed in practice: "ImmersiveColorSet" (accent change),
        // "UserPreferences" (bulk prefs), "WindowsThemeElement" (HC toggle).
        if (msg == WM_SETTINGCHANGE && lParam != IntPtr.Zero)
        {
            var setting = System.Runtime.InteropServices.Marshal.PtrToStringAuto(lParam);
            if (setting is "ImmersiveColorSet" or "UserPreferences" or "WindowsThemeElement")
            {
                ThemeService.ApplySystemAccent();
            }
        }
        return IntPtr.Zero;
    }

    private void ToggleNoteByHotkey(Guid noteId)
    {
        if (_notesService == null) return;
        if (_notesService.Windows.TryGetValue(noteId, out var window))
        {
            // NotesService.Windows is typed Dictionary<Guid, StickyNoteWindow>, so every
            // branch below routes through the SAME ShowNote/HideNote code the note's own
            // title-bar "─" minimize button runs — never raw Window.Hide().
            if (window.RestoreButton.Visibility == Visibility.Visible)
            {
                // Minimized to restore button → show full note
                window.ShowNote();
            }
            else if (window.IsVisible)
            {
                if (window.IsActive)
                {
                    // Minimize via the same code as the top-right "─" button
                    // (HideNote handles EnableRestoreButton correctly).
                    window.HideNote();
                }
                else
                {
                    window.BringToFront();
                }
            }
            else
            {
                window.ShowNote();
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
        if (_notesService == null) return;
        if (_notesService.Windows.TryGetValue(note.Id, out var window))
        {
            // ponytail: 2026-08-26 — single source of truth: the RestoreButton (the
            // minimized indicator) + window visibility, routed through ShowNote/HideNote
            // — the SAME code the note's own title-bar "─" minimize button runs. The
            // previous MainContent.Visibility check misread mid-animation windows and
            // left a transparent ghost behind. (NotesService.Windows is typed
            // Dictionary<Guid, StickyNoteWindow>, so the old non-StickyNoteWindow
            // raw-Hide/Show branches were unreachable.)
            bool show = !window.IsVisible || window.RestoreButton.Visibility == Visibility.Visible;
#if DEBUG
            Helpers.DzTrace.Log($"[Toggle] ToggleNoteWindow -> {(show ? "ShowNote" : "HideNote")} (winVisible={window.IsVisible} content={window.MainContent.Visibility} btn={window.RestoreButton.Visibility})");
#endif
            if (show) window.ShowNote();
            else window.HideNote();
        }
        else
        {
            note.IsVisible = true;
            OpenNoteWindow(note);
        }
    }

    public void OpenNoteWindowFromManager(Models.StickyNote note)
    {
        if (_notesService == null) return;
        if (_notesService.Windows.ContainsKey(note.Id)) return;
        note.IsVisible = true;
        OpenNoteWindow(note);
    }

    public bool IsNoteWindowOpen(Guid noteId) => _notesService?.Windows.ContainsKey(noteId) ?? false;

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
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[DeskOrder] Failed to register note hotkey {note.Id}: {note.HotkeyModifiers}+{note.HotkeyKey}");
                }
            }
        }
    }

    public IntPtr GetMainHwnd() => _mainHwnd;

    private static PresetKind? ParseLoadPresetArg(string[] args)
    {
        foreach (var a in args)
        {
            if (a.StartsWith("--load-preset=", StringComparison.OrdinalIgnoreCase))
            {
                var s = a.Substring("--load-preset=".Length);
                return s.ToLowerInvariant() switch
                {
                    "zone" => PresetKind.Zone,
                    "clock" => PresetKind.Clock,
                    "calendar" => PresetKind.Calendar,
                    "stickynote" => PresetKind.StickyNote,
                    "mergedgroup" => PresetKind.MergedGroup,
                    "panel" => PresetKind.Panel,
                    _ => null
                };
            }
        }
        return null;
    }

    private static string? ParseSpawnWidgetArg(string[] args)
    {
        foreach (var a in args)
        {
            if (a.StartsWith("--spawn-widget=", StringComparison.OrdinalIgnoreCase))
                return a.Substring("--spawn-widget=".Length).ToLowerInvariant();
        }
        return null;
    }

#if DEBUG
    void RunContextMenuDiagnostic()
    {
        try
        {
            var cmStyle = TryFindResource(typeof(System.Windows.Controls.ContextMenu)) as System.Windows.Style;
            var miStyle = TryFindResource(typeof(System.Windows.Controls.MenuItem)) as System.Windows.Style;
            var sepStyle = TryFindResource(typeof(System.Windows.Controls.Separator)) as System.Windows.Style;
            System.Diagnostics.Debug.WriteLine(
                $"[MenuDiag] resources cm={(cmStyle != null)} mi={(miStyle != null)} sep={(sepStyle != null)}");

            var w = new Window
            {
                Width = 120, Height = 60,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
                ShowInTaskbar = false,
                Left = -32000, Top = -32000,
                Topmost = true
            };
            var host = new System.Windows.Controls.Border { Width = 80, Height = 24 };
            w.Content = host;
            w.Show();

            var cm = new System.Windows.Controls.ContextMenu();
            var mi = new System.Windows.Controls.MenuItem { Header = "DiagItem" };
            var sep = new System.Windows.Controls.Separator();
            cm.Items.Add(mi);
            cm.Items.Add(sep);
            host.ContextMenu = cm;
            cm.PlacementTarget = host;
            cm.Opened += (_, _) =>
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[MenuDiag] opened cmStyle={(cm.Style != null)} cmTemplate={(cm.Template != null)} " +
                    $"miStyle={(mi.Style != null)} miTemplate={(mi.Template != null)} miH={(int)mi.ActualHeight} " +
                    $"cmBg={((cm.Background as System.Windows.Media.SolidColorBrush)?.Color.ToString() ?? "null")} " +
                    $"miBg={((mi.Background as System.Windows.Media.SolidColorBrush)?.Color.ToString() ?? "null")} " +
                    $"sepStyle={(sep.Style != null)} sepTemplate={(sep.Template != null)}");
            };
            cm.IsOpen = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try { cm.IsOpen = false; } catch { }
                try { w.Close(); } catch { }
                System.Diagnostics.Debug.WriteLine("[MenuDiag] done");
            }), DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MenuDiag] ERROR {ex}");
        }
    }
#endif

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
        _trayIcon.ManageZones -= TrayManage;
        _trayIcon.Exit -= TrayExit;

        _trayIcon.LeftClick += TrayLeftClick;
        _trayIcon.NotifyError += msg => _trayIcon?.ShowBalloonTip(_loc["Toast.TrayError.Title"], msg);
        // ponytail 2026-08-24: 给非 App 内部代码（SettingsPage 等）一个走托盘气泡的入口，
        // 避免 SettingsPage 还要反射 / 强转才能拿到 _trayIcon。失败时静默 return。
        App.Notify = (title, body) => _trayIcon?.ShowBalloonTip(title, body);
        _trayIcon.DoubleClick += TrayDoubleClick;
        _trayIcon.ManageZones += TrayManage;
        _trayIcon.Exit += TrayExit;
    }

    private void TrayLeftClick() { /* no-op: don't auto-restore zones */ }
    private void TrayDoubleClick() => ShowManagementWindow();
    // ponytail: 2026-08-23 — the tray's 全部显示/全部隐藏 used to act on zones ONLY,
    // so the sticky note (and any clock/calendar) stayed on the desktop after "hide
    // all" — reported as "全部隐藏时便签不隐藏". Now they cover every window type:
    // route through the management window's batch methods when it exists (those also
    // open never-opened widgets on Show All), otherwise sweep the live app windows
    // directly so the behavior holds even with StartMinimized.
    private void TrayShowAll()
    {
        _zoneManager?.ShowAll();
        if (_managementWindow != null)
        {
            _managementWindow.ShowAllWidgetsFromVm();
        }
        else
        {
            try
            {
                foreach (var w in System.Windows.Application.Current.Windows.OfType<ClockWidget>().ToList())
                    w.ShowClock();
                foreach (var w in System.Windows.Application.Current.Windows.OfType<CalendarWidget>().ToList())
                    w.ShowCalendar();
                foreach (var w in System.Windows.Application.Current.Windows.OfType<StickyNoteWindow>().ToList())
                    w.ShowNote();
            }
            catch { }
        }
    }
    private void TrayHideAll()
    {
        _zoneManager?.HideAll();
        if (_managementWindow != null)
        {
            _managementWindow.HideAllWidgetsFromVm();
        }
        else
        {
            try
            {
                foreach (var w in System.Windows.Application.Current.Windows.OfType<ClockWidget>().ToList())
                    if (w.MainContent.Visibility == Visibility.Visible) w.HideClock();
                foreach (var w in System.Windows.Application.Current.Windows.OfType<CalendarWidget>().ToList())
                    if (w.MainContent.Visibility == Visibility.Visible) w.HideCalendar();
                foreach (var w in System.Windows.Application.Current.Windows.OfType<StickyNoteWindow>().ToList())
                    if (w.MainContent.Visibility == Visibility.Visible) w.HideNote();
            }
            catch { }
        }
    }
    // ponytail 2026-08-27: 双击桌面逻辑依据 — 用模型 IsVisible 判断。
    // 全隐藏(FullHideAll)会把窗口全部关闭，不能再靠窗口可见性判断；
    // 模型 IsVisible 与 FullHideAll/ShowAll 的持久化一致。
    bool AnyWidgetHidden()
    {
        try
        {
            if (_zoneManager != null)
            {
                foreach (var z in _zoneManager.Zones)
                {
                    // 合并分区里的子分区随主分区一起显示/隐藏，跳过，避免误判。
                    if (z.MergedGroupMembership.GroupId.HasValue
                        && z.MergedGroupMembership.SubZoneIds.Count == 0)
                        continue;
                    if (!z.IsVisible) return true;
                }
            }
            if (_widgetService != null)
            {
                foreach (var c in _widgetService.Clocks)
                    if (!c.IsVisible) return true;
                foreach (var cal in _widgetService.Calendars)
                    if (!cal.IsVisible) return true;
            }
            if (_notesService != null)
            {
                foreach (var n in _notesService.Notes)
                    if (!n.IsVisible) return true;
            }
        }
        catch { }
        return false;
    }

    // ponytail 2026-08-27: 全部隐藏 — 与左侧边栏「全部隐藏」按钮一致(FullHideAll：
    // 关闭所有组件窗口并持久化 IsVisible=false)。分区用 FullHideAll。
    private void TrayFullHideAll()
    {
        if (_managementWindow == null) EnsureManagementWindow();
        _managementWindow?.FullHideAll();
    }

    // ponytail 2026-08-27: 全局热键注册失败兜底(冲突 / hwnd 未就绪)→ 静默 + debug。
    void TryRegisterHotkey(int id, int modifiers, int vk)
    {
        if (_mainHwnd == IntPtr.Zero) return;
        // 先无条件注销：切到「无」(modifiers==0 或 vk==0) 时必须释放旧热键，
        // 否则旧的 Ctrl+Shift+X 会继续拦截。
        NativeMethods.UnregisterHotKey(_mainHwnd, id);
        if (vk == 0 || modifiers == 0) return;
        if (!NativeMethods.RegisterHotKey(_mainHwnd, id, (uint)modifiers, (uint)vk))
            System.Diagnostics.Debug.WriteLine($"[DeskOrder] Hotkey id=0x{id:X} (mods=0x{modifiers:X}, key=0x{vk:X}) register failed");
    }

    private void TrayManage() => ShowManagementWindow();
    private void TrayExit() => ShutdownApplication();

    // ── Widget window management ──

    private void OpenNoteWindow(Models.StickyNote note)
    {
        if (_notesService == null) return;
        if (_notesService.Windows.ContainsKey(note.Id)) return;
        var window = new StickyNoteWindow(note, _notesService);
        window.Closed += (_, _) => _notesService.Windows.Remove(note.Id);
        _notesService.Windows[note.Id] = window;
        window.Show();
    }

    // ponytail: tray + window title both want the same image. Exposed as a public
    // static so XAML can bind via {x:Static Application.AppIcon} without a
    // xaml-side indirection. Lazily resolved on first access.
    public static System.Windows.Media.ImageSource AppIcon => _appIconImage ??= IconToImageSource(CreateAppIcon());
    private static System.Windows.Media.ImageSource? _appIconImage;

    private void ShowManagementWindow()
    {
        EnsureManagementWindow();
        _managementWindow!.Show();
        _managementWindow.Activate();
        _managementWindow.WindowState = WindowState.Normal;
    }

    /// <summary>Create the ManagementWindow (if it doesn't exist yet) WITHOUT showing it.
    /// Needed because the property editor (PropertyWindowService) routes through the
    /// ManagementWindow, and with StartMinimized=true the window is never constructed at
    /// startup — so opening a SubFolder/zone settings panel before first opening the
    /// management UI would otherwise silently no-op.</summary>
    public void EnsureManagementWindow()
    {
        if (_managementWindow != null) return;
        _managementWindow = new ManagementWindow(_zoneManager!, _configService!, _notesService, _widgetService, _panelService);
        _managementWindow.Closed += (_, _) => _managementWindow = null;
        _managementWindow.Icon = AppIcon;
    }

    private static Icon CreateAppIcon()
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
        foreach (var kv in _notesService.Windows.ToList())
        {
            if (!activeIds.Contains(kv.Key))
            {
                try { kv.Value.Close(); } catch { }
                _notesService.Windows.Remove(kv.Key);
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

    // ponytail 2026-08-27: 设置页改键后调用 — 重注册 3 个全局热键。
    public void ReRegisterGlobalHotkeys()
    {
        // 用运行中的 live config(设置页刚改的就是它)，不要从磁盘 Load()——
        // 否则「先注册后保存」的顺序会拿到旧值，改完要重启才生效。
        var cfg = _zoneManager?.GetConfig();
        if (cfg == null) return;
        TryRegisterHotkey(HOTKEY_ID_SHOW_ALL, cfg.ShowAllHotkey.Modifiers, cfg.ShowAllHotkey.Key);
        TryRegisterHotkey(HOTKEY_ID_HIDE_ALL, cfg.HideAllHotkey.Modifiers, cfg.HideAllHotkey.Key);
        TryRegisterHotkey(HOTKEY_ID_MIN_ALL, cfg.MinimizeAllHotkey.Modifiers, cfg.MinimizeAllHotkey.Key);
    }

    // ponytail 2026-08-27: 设置页改勾后调用 — 开关桌面双击监听。
    public void SetDesktopDoubleClickEnabled(bool enabled)
    {
        if (enabled) AppDesktopDoubleClick.Install(true, () =>
        {
            bool anyHidden = AnyWidgetHidden();
            Dispatcher.Invoke(() => { if (anyHidden) TrayShowAll(); else TrayFullHideAll(); });
        });
        else AppDesktopDoubleClick.Uninstall();
    }

    private void ShutdownApplication()
    {
        // Unregister all hotkeys
        if (_mainHwnd != IntPtr.Zero)
        {
            foreach (var id in _noteIdToHotkeyId.Values)
                NativeMethods.UnregisterHotKey(_mainHwnd, id);
            NativeMethods.UnregisterHotKey(_mainHwnd, HOTKEY_ID_PANEL);
            NativeMethods.UnregisterHotKey(_mainHwnd, HOTKEY_ID_SHOW_ALL);
            NativeMethods.UnregisterHotKey(_mainHwnd, HOTKEY_ID_HIDE_ALL);
            NativeMethods.UnregisterHotKey(_mainHwnd, HOTKEY_ID_MIN_ALL);
        }
        _reminderService?.Dispose();
        AutoOrganizeService.Instance.Dispose();
        FileSyncService.Instance.Dispose();
        _zoneManager?.Shutdown();
        _trayIcon?.Dispose();
        // ponytail 2026-08-27: 拆卸桌面双击钩子,避免 DLL 注入残留。
        AppDesktopDoubleClick.Uninstall();
        DisposeSingleInstance();
        Current.Shutdown();
    }

    private void Application_Exit(object sender, ExitEventArgs e)
    {
        if (_mainHwnd != IntPtr.Zero)
        {
            foreach (var id in _noteIdToHotkeyId.Values)
                NativeMethods.UnregisterHotKey(_mainHwnd, id);
            NativeMethods.UnregisterHotKey(_mainHwnd, HOTKEY_ID_PANEL);
            NativeMethods.UnregisterHotKey(_mainHwnd, HOTKEY_ID_SHOW_ALL);
            NativeMethods.UnregisterHotKey(_mainHwnd, HOTKEY_ID_HIDE_ALL);
            NativeMethods.UnregisterHotKey(_mainHwnd, HOTKEY_ID_MIN_ALL);
        }
        _reminderService?.Dispose();
        AutoOrganizeService.Instance.Dispose();
        FileSyncService.Instance.Dispose();
        _zoneManager?.Shutdown();
        _trayIcon?.Dispose();
        // ponytail 2026-08-27: 拆卸桌面双击钩子,避免 DLL 注入残留。
        AppDesktopDoubleClick.Uninstall();
        DisposeSingleInstance();
    }

    void DisposeSingleInstance()
    {
        var ev = _activateEvent;
        _activateEvent = null;
        if (ev != null)
        {
            try { ev.Set(); } catch { }
            try { ev.Dispose(); } catch { }
        }
        try { _mutex?.Dispose(); } catch { }
        _mutex = null;
    }

    static AppThemeMode ParseThemeMode(string? s) => s switch
    {
        "Light" => AppThemeMode.Light,
        "Dark"  => AppThemeMode.Dark,
        _       => AppThemeMode.System,
    };
}

// ponytail 2026-08-27: 桌面双击切换 全部显示/隐藏 — 装配 / 拆解 LowLevel 鼠标钩子。
static class AppDesktopDoubleClick
{
    // 双击阈值 — 沿用 Windows 默认 500ms / 10px (GetSystemMetrics SM_CXDOUBLECLK/SM_CYDOUBLECLK)。
    const int DoubleClickMs = 500;
    const int DoubleClickPx = 10;

    static IntPtr _hook;
    static DesktopHook.LowLevelMouseProc? _proc;
    static DateTime _lastDownUtc = DateTime.MinValue;
    static NativeMethods.POINT _lastDownPoint;
    static Action? _onDesktopDoubleClick;

    public static void Install(bool enabled, Action onDesktopDoubleClick)
    {
        Uninstall();
        if (!enabled) return;
        _onDesktopDoubleClick = onDesktopDoubleClick;
        _proc = HookProc;
        // ponytail: LL hook hMod=IntPtr.Zero 表示用调用线程所在模块(本进程 EXE/DLL),
        // 适用于 .NET 托管进程,无需 GetModuleHandle。
        _hook = DesktopHook.SetWindowsHookEx(DesktopHook.WH_MOUSE_LL, _proc, IntPtr.Zero, 0);
        if (_hook == IntPtr.Zero)
            System.Diagnostics.Debug.WriteLine("[DeskOrder] Desktop double-click hook install failed");
    }

    public static void Uninstall()
    {
        if (_hook != IntPtr.Zero)
        {
            DesktopHook.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
        _proc = null;
        _onDesktopDoubleClick = null;
    }

    static IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == (IntPtr)DesktopHook.WM_LBUTTONDOWN)
        {
            var info = System.Runtime.InteropServices.Marshal.PtrToStructure<DesktopHook.MSLLHOOKSTRUCT>(lParam);
            // 仅「空白桌面」有效：按桌面自身窗口句柄判定（资源管理器里也有同名
            // SHELLDLL_DefView，按类名会误判），并用 LVM_HITTEST 排除桌面图标。
            IntPtr hwndAtPoint = DesktopHook.WindowFromPoint(info.pt);
            if (DesktopHook.IsDesktopBackground(hwndAtPoint, info.pt))
            {
                var now = DateTime.UtcNow;
                bool isDouble = (now - _lastDownUtc).TotalMilliseconds <= DoubleClickMs
                    && Math.Abs(info.pt.x - _lastDownPoint.x) <= DoubleClickPx
                    && Math.Abs(info.pt.y - _lastDownPoint.y) <= DoubleClickPx;
                _lastDownUtc = now;
                _lastDownPoint = info.pt;
                if (isDouble)
                {
                    _onDesktopDoubleClick?.Invoke();
                    _lastDownUtc = DateTime.MinValue; // 防连击
                }
            }
            else
            {
                _lastDownUtc = DateTime.MinValue;
            }
        }
        return DesktopHook.CallNextHookEx(_hook, nCode, wParam, lParam);
    }
}
