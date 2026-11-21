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
    private bool _calendarsHooked;

    public ReminderService(TrayIconService tray, WidgetService widgetService, ConfigService configService)
    {
        _tray = tray;
        _widgetService = widgetService;
        _configService = configService;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _timer.Tick += (_, _) => CheckReminders();
        // Phase 1：日历/提醒增删后重新判定要不要跑 30s 轮询（见 EnsureTimer）。
        try
        {
            _widgetService.CalendarsChanged += OnCalendarsChanged;
            _calendarsHooked = true;
        }
        catch { }
    }

    /// <summary>Phase 1：只有存在「已启用 + 未触发 + 有时间」的提醒时才启动 30s 轮询。
    /// 旧实现无条件起表，没有任何提醒的用户每分钟白醒 2 次。</summary>
    public void Start() => EnsureTimer();
    public void Stop() => _timer.Stop();

    void OnCalendarsChanged() => EnsureTimer();

    void EnsureTimer()
    {
        if (_disposed) return;
        if (HasPendingReminders())
        {
            if (!_timer.IsEnabled)
            {
                _timer.Start();
                CheckReminders();   // 起表瞬间补一次，别让 30s 内的到期提醒漏掉
            }
        }
        else
        {
            _timer.Stop();
        }
    }

    bool HasPendingReminders()
    {
        try
        {
            foreach (var cal in _widgetService.Calendars)
                foreach (var list in cal.Notes.Values)
                    foreach (var note in list)
                        if (note.ReminderEnabled && !note.ReminderFired && note.ReminderTime != null)
                            return true;
        }
        catch { return true; }   // 读不到配置就保守起见保持轮询
        return false;
    }

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

        // Phase 1：最后一个提醒已经发完 → 停表，等下一次 CalendarsChanged 再起。
        if (!HasPendingReminders()) _timer.Stop();
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
        if (_calendarsHooked)
        {
            _calendarsHooked = false;
            try { _widgetService.CalendarsChanged -= OnCalendarsChanged; } catch { }
        }
    }
}
