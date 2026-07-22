using System;
using System.Collections;
using System.Collections.ObjectModel;
using System.Linq;
using DesktopZones.Models;

namespace DesktopZones.Services;

public class WidgetService
{
    private readonly ConfigService _configService;
    private AppConfig? _appConfig;

    public ObservableCollection<DesktopClock> Clocks { get; } = new();
    public ObservableCollection<DesktopCalendar> Calendars { get; } = new();
    public event Action? ClocksChanged;
    public event Action? CalendarsChanged;

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
        // Reload config to preserve settings managed by other components (e.g., hotkeys)
        var latestConfig = _configService.Load();
        _appConfig.Clocks = Clocks.ToList();
        _appConfig.Calendars = Calendars.ToList();
        // Preserve hotkey and other settings from the latest config
        _appConfig.PanelHotkeyEnabled = latestConfig.PanelHotkeyEnabled;
        _appConfig.PanelHotkeyModifiers = latestConfig.PanelHotkeyModifiers;
        _appConfig.PanelHotkeyKey = latestConfig.PanelHotkeyKey;
        _appConfig.PanelCustomHotkeys = latestConfig.PanelCustomHotkeys;
        try { _configService.Save(_appConfig); } catch { }
    }
}
