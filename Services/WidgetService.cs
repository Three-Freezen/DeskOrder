using System;
using System.Collections;
using System.Collections.ObjectModel;
using System.Linq;
using DesktopZones.Models;
using DesktopZones.Views;

namespace DesktopZones.Services;

public class WidgetService
{
    private readonly ConfigService _configService;
    private AppConfig? _appConfig;

    public ObservableCollection<DesktopClock> Clocks { get; } = new();
    public ObservableCollection<DesktopCalendar> Calendars { get; } = new();
    public event Action? ClocksChanged;
    public event Action? CalendarsChanged;
    /// <summary>Fires when a widget's lock state changes. Args: widgetId (string), isLocked.</summary>
    public event Action<string, bool>? LockChanged;

    /// <summary>Open ClockWidget instances keyed by clock Id (was App._clockWindows before P5).</summary>
    public Dictionary<Guid, ClockWidget> ClockWindows { get; } = new();
    /// <summary>Open CalendarWidget instances keyed by calendar Id (was App._calendarWindows before P5).</summary>
    public Dictionary<Guid, CalendarWidget> CalendarWindows { get; } = new();

    public ClockWidget? GetClockWindow(Guid id)
        => ClockWindows.TryGetValue(id, out var w) ? w : null;
    public CalendarWidget? GetCalendarWindow(Guid id)
        => CalendarWindows.TryGetValue(id, out var w) ? w : null;

    public AppConfig GetConfig() => _appConfig!;

    public WidgetService(ConfigService configService)
    {
        _configService = configService;
    }

    public void Load(AppConfig config)
    {
        _appConfig = config;
        Clocks.Clear();
        Calendars.Clear();
        foreach (var c in config.Clocks) Clocks.Add(c);
        foreach (var c in config.Calendars) Calendars.Add(c);
    }

    // ── Clock ──

    public DesktopClock CreateClock(double x = 300, double y = 100)
    {
        var clock = new DesktopClock { X = x, Y = y };
        Clocks.Add(clock);
        Save();
        ClocksChanged?.Invoke();
        return clock;
    }

    public void UpdateClock(DesktopClock clock)
    {
        var existing = Clocks.FirstOrDefault(c => c.Id == clock.Id);
        if (existing != null) { var idx = Clocks.IndexOf(existing); Clocks[idx] = clock; }
        Save();
        ClocksChanged?.Invoke();
    }

    public void DeleteClock(Guid id)
    {
        var c = Clocks.FirstOrDefault(x => x.Id == id);
        if (c != null) Clocks.Remove(c);
        Save();
        ClocksChanged?.Invoke();
    }

    /// <summary>
    /// The clock the user is most likely interacting with — currently the most
    /// recently added visible clock. Used by <see cref="Views.LoadPresetDialog"/>
    /// to live-track which mode (Digital / Analog) to show in the ClockCard preview.
    /// Returns null if no visible clock exists.
    /// </summary>
    public DesktopClock? GetActiveClock()
    {
        return Clocks.LastOrDefault(c => c.IsVisible);
    }

    // ── Calendar ──

    public DesktopCalendar CreateCalendar(double x = 400, double y = 100)
    {
        var cal = new DesktopCalendar();
        cal.X = x; cal.Y = y;
        Calendars.Add(cal);
        Save();
        CalendarsChanged?.Invoke();
        return cal;
    }

    public void UpdateCalendar(DesktopCalendar cal)
    {
        var existing = Calendars.FirstOrDefault(c => c.Id == cal.Id);
        if (existing != null) { var idx = Calendars.IndexOf(existing); Calendars[idx] = cal; }
        Save();
        CalendarsChanged?.Invoke();
    }

    public void DeleteCalendar(Guid id)
    {
        var c = Calendars.FirstOrDefault(x => x.Id == id);
        if (c != null) Calendars.Remove(c);
        Save();
        CalendarsChanged?.Invoke();
    }

    public void Save()
    {
        if (_appConfig == null) return;
        ConfigSaver.SavePreservingPanelSettings(_configService, cfg =>
        {
            cfg.Clocks = Clocks.ToList();
            cfg.Calendars = Calendars.ToList();
        });
    }

    // ── Lock ──

    /// <summary>Set locked state for a widget (Clock or Calendar) by string id. Fires LockChanged after state update.</summary>
    public void SetLocked(string widgetId, bool locked)
    {
        if (!Guid.TryParse(widgetId, out var guid)) return;
        var clock = Clocks.FirstOrDefault(c => c.Id == guid);
        if (clock != null)
        {
            clock.IsLocked = locked;
            LockChanged?.Invoke(widgetId, locked);
            return;
        }
        var cal = Calendars.FirstOrDefault(c => c.Id == guid);
        if (cal != null)
        {
            cal.IsLocked = locked;
            LockChanged?.Invoke(widgetId, locked);
        }
    }
}
