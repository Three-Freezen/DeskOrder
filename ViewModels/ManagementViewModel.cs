using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using DesktopZones.Models;
using DesktopZones.Services;

namespace DesktopZones.ViewModels;

public class ManagementViewModel : INotifyPropertyChanged
{
    private readonly ZoneManager _zoneManager;
    private readonly ConfigService _configService;
    private readonly NotesService? _notesService;
    private readonly WidgetService? _widgetService;
    private readonly PanelService? _panelService;

    public ObservableCollection<Zone> Zones => _zoneManager.Zones;
    public ObservableCollection<StickyNote>? Notes => _notesService?.Notes;
    public ObservableCollection<DesktopClock>? Clocks => _widgetService?.Clocks;
    public ObservableCollection<DesktopCalendar>? Calendars => _widgetService?.Calendars;

    private bool _startWithWindows;
    public bool StartWithWindows
    {
        get => _startWithWindows;
        set { _startWithWindows = value; OnPropertyChanged(); SaveSettings(); }
    }

    private bool _startMinimized;
    public bool StartMinimized
    {
        get => _startMinimized;
        set { _startMinimized = value; OnPropertyChanged(); SaveSettings(); }
    }

    private string _theme = "dark";
    public string Theme
    {
        get => _theme;
        set { _theme = value; OnPropertyChanged(); }
    }

    private string _language = "zh-CN";
    public string Language
    {
        get => _language;
        set { _language = value; OnPropertyChanged(); }
    }

    // ── Zone commands ──

    public ICommand CreateZoneCommand { get; }
    public ICommand DeleteZoneCommand { get; }
    public ICommand ToggleZoneCommand { get; }
    public ICommand ToggleZoneLockCommand { get; }
    public ICommand ShowAllZonesCommand { get; }
    public ICommand HideAllZonesCommand { get; }
    public ICommand FullHideAllZonesCommand { get; }
    public ICommand SwitchToSubZoneCommand { get; }

    // ── Note commands ──

    public ICommand CreateNoteCommand { get; }
    public ICommand DeleteNoteCommand { get; }
    public ICommand ToggleNoteCommand { get; }
    public ICommand ToggleNoteLockCommand { get; }

    // ── Clock commands ──

    public ICommand CreateClockCommand { get; }
    public ICommand DeleteClockCommand { get; }
    public ICommand ToggleClockCommand { get; }
    public ICommand ToggleClockLockCommand { get; }

    // ── Calendar commands ──

    public ICommand CreateCalendarCommand { get; }
    public ICommand DeleteCalendarCommand { get; }
    public ICommand ToggleCalendarCommand { get; }
    public ICommand ToggleCalendarLockCommand { get; }

    // ── Panel commands ──

    public ICommand TogglePanelCommand { get; }

    public ManagementViewModel(ZoneManager zoneManager, ConfigService configService,
        NotesService? notesService = null, WidgetService? widgetService = null, PanelService? panelService = null)
    {
        _zoneManager = zoneManager;
        _configService = configService;
        _notesService = notesService;
        _widgetService = widgetService;
        _panelService = panelService;

        var config = configService.Load();
        _startWithWindows = config.StartWithWindows;
        _startMinimized = config.StartMinimized;
        _language = config.Language ?? "zh-CN";
        _theme = config.Theme ?? "dark";

        // Zone
        CreateZoneCommand = new RelayCommand(_ => _zoneManager.CreateZone());
        DeleteZoneCommand = new RelayCommand<Zone>(z => { if (z != null) _zoneManager.DeleteZone(z.Id); });
        ToggleZoneCommand = new RelayCommand<Zone>(z => { if (z != null) { if (z.IsVisible) _zoneManager.HideZone(z.Id); else _zoneManager.ShowZone(z); } });
        ToggleZoneLockCommand = new RelayCommand<Zone>(z => { if (z != null) { _zoneManager.SetLocked(z.Id.ToString(), !z.IsLocked); _zoneManager.SaveConfig(); } });
        ShowAllZonesCommand = new RelayCommand(_ => _zoneManager.ShowAll());
        HideAllZonesCommand = new RelayCommand(_ => _zoneManager.HideAll());
        FullHideAllZonesCommand = new RelayCommand(_ => _zoneManager.FullHideAll());
        SwitchToSubZoneCommand = new RelayCommand<Zone>(z => { if (z != null) _zoneManager.ShowZone(z); });

        // Note
        CreateNoteCommand = new RelayCommand(_ => { /* Page handles window opening; VM just nudges service */ });
        DeleteNoteCommand = new RelayCommand<StickyNote>(n => { if (n != null) _notesService?.DeleteNote(n.Id); });
        ToggleNoteCommand = new RelayCommand<StickyNote>(_ => { /* routed via App.ToggleNoteWindow from Page; VM stays presentation-agnostic */ });
        ToggleNoteLockCommand = new RelayCommand<StickyNote>(n => { if (n != null) { _notesService?.SetLocked(n.Id.ToString(), !n.IsLocked); _notesService?.Save(); } });

        // Clock
        CreateClockCommand = new RelayCommand(_ => { });
        DeleteClockCommand = new RelayCommand<DesktopClock>(c => { if (c != null) _widgetService?.DeleteClock(c.Id); });
        ToggleClockCommand = new RelayCommand<DesktopClock>(_ => { });
        ToggleClockLockCommand = new RelayCommand<DesktopClock>(c => { if (c != null) { _widgetService?.SetLocked(c.Id.ToString(), !c.IsLocked); _widgetService?.Save(); } });

        // Calendar
        CreateCalendarCommand = new RelayCommand(_ => { });
        DeleteCalendarCommand = new RelayCommand<DesktopCalendar>(c => { if (c != null) _widgetService?.DeleteCalendar(c.Id); });
        ToggleCalendarCommand = new RelayCommand<DesktopCalendar>(_ => { });
        ToggleCalendarLockCommand = new RelayCommand<DesktopCalendar>(c => { if (c != null) { _widgetService?.SetLocked(c.Id.ToString(), !c.IsLocked); _widgetService?.Save(); } });

        // Panel
        TogglePanelCommand = new RelayCommand(_ => { /* handled by ManagementWindow (toggles _panelService.IsOpen) */ });

        _zoneManager.ZonesChanged += () => OnPropertyChanged(nameof(Zones));
    }

    private void SaveSettings()
    {
        var config = _zoneManager.GetConfig();
        config.StartWithWindows = _startWithWindows;
        config.StartMinimized = _startMinimized;
        _zoneManager.UpdateConfig(config);
        UpdateStartupShortcut(_startWithWindows);
    }

    private void UpdateStartupShortcut(bool create)
    {
        var startupPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            "DeskOrder.lnk");
        if (create)
        {
            try
            {
                var exePath = Environment.ProcessPath;
                if (exePath == null) return;
                Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return;
                dynamic? shell = Activator.CreateInstance(shellType);
                if (shell == null) return;
                dynamic? shortcut = shell.CreateShortcut(startupPath);
                shortcut.TargetPath = exePath;
                shortcut.WorkingDirectory = System.IO.Path.GetDirectoryName(exePath);
                shortcut.Description = "DeskOrder";
                shortcut.Save();
            }
            catch { }
        }
        else if (System.IO.File.Exists(startupPath))
        {
            try { System.IO.File.Delete(startupPath); } catch { }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
