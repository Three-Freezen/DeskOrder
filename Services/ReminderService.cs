using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using DesktopZones.Models;

namespace DesktopZones.Services;

/// <summary>
/// Manages calendar note reminders. Checks for due reminders on a timer
/// and fires balloon tip notifications via TrayIconService.
/// </summary>
public class ReminderService : IDisposable
{
    private readonly TrayIconService _tray;
    private readonly WidgetService _widgetService;
    private readonly ConfigService _configService;
    private readonly DispatcherTimer _timer;
    private bool _disposed;

    public ReminderService(TrayIconService tray, WidgetService widgetService, ConfigService configService)
    {
        _tray = tray;
        _widgetService = widgetService;
        _configService = configService;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _timer.Tick += (_, _) => CheckReminders();
    }

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();

    /// <summary>
    /// Check all calendars for due reminders and fire notifications.
    /// Called on timer tick and at startup for missed reminders.
    /// </summary>
    public void CheckReminders()
    {
        var now = DateTime.Now;
        bool changed = false;

        foreach (var cal in _widgetService.Calendars)
        {
            foreach (var kv in cal.Notes)
            {
                foreach (var note in kv.Value)
                {
                    if (!note.ReminderEnabled || note.ReminderFired || note.ReminderTime == null)
                        continue;

                    if (now >= note.ReminderTime.Value)
                    {
                        // Fire notification
                        var title = string.IsNullOrEmpty(note.Content) ? "Reminder" : note.Content[..Math.Min(note.Content.Length, 40)];
                        var message = $"{note.Date} — {note.Content}";
                        _tray.ShowBalloonTip(title, message);

                        // Mark as fired and clear reminder
                        note.ReminderFired = true;
                        note.ReminderEnabled = false;
                        note.ReminderTime = null;
                        changed = true;
                    }
                }
            }
        }

        if (changed)
        {
            // Persist changes
            var config = _configService.Load();
            foreach (var cal in _widgetService.Calendars)
            {
                var existing = config.Calendars.FirstOrDefault(c => c.Id == cal.Id);
                if (existing != null)
                {
                    var idx = config.Calendars.IndexOf(existing);
                    config.Calendars[idx] = cal;
                }
            }
            _configService.Save(config);
        }
    }

    /// <summary>
    /// Check for missed reminders at startup. Fires all due notifications.
    /// </summary>
    public void CheckMissedReminders()
    {
        CheckReminders();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
    }
}
