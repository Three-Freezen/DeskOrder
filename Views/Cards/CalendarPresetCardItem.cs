using System;
using System.Collections.Generic;
using System.Globalization;
using DesktopZones.Models;
using DesktopZones.Services;

namespace DesktopZones.Views.Cards;

/// <summary>
/// Single day cell in the calendar preset preview grid. Lives next to its
/// owner so the XAML template can <c>{Binding Day}/{Binding IsCurrentMonth}/{Binding IsToday}</c>
/// directly without a converter.
/// </summary>
public class DayCell
{
    public int Day { get; set; }
    public bool IsCurrentMonth { get; set; }
    public bool IsToday { get; set; }
}

/// <summary>
/// Calendar-flavoured <see cref="PresetCardItem"/>: exposes a precomputed
/// 42-cell day grid + month label so the <c>CalendarCardTemplate</c> can
/// <c>ItemsControl</c>-bind a real month layout instead of the previous
/// hardcoded "26/27/28/…/1/2/…" placeholder.
/// </summary>
public class CalendarPresetCardItem : PresetCardItem
{
    public string MonthLabel { get; }
    public IReadOnlyList<string> WeekdayLabels { get; }
    public IReadOnlyList<DayCell> Days { get; }
    public bool StartOnMonday { get; }

    public CalendarPresetCardItem(CalendarPreset preset) : base(preset)
    {
        var cal = preset.Calendar;
        StartOnMonday = cal.StartOnMonday;

        // DisplayYear/DisplayMonth are [JsonIgnore] on DesktopCalendar and default to
        // DateTime.Now — meaning every preset shows the *current* month. That's the
        // right default for a preview; we don't try to record a "preview month" in JSON.
        var firstOfMonth = new DateTime(cal.DisplayYear, cal.DisplayMonth, 1);
        var daysInMonth = DateTime.DaysInMonth(firstOfMonth.Year, firstOfMonth.Month);
        var today = DateTime.Today;
        var cn = LocalizationService.Instance.CurrentLanguage == "zh";

        // ── Day-of-week header ──
        WeekdayLabels = StartOnMonday
            ? (cn
                ? new[] { "一", "二", "三", "四", "五", "六", "日" }
                : new[] { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" })
            : (cn
                ? new[] { "日", "一", "二", "三", "四", "五", "六" }
                : new[] { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" });

        // ── 42-cell grid (6 weeks × 7 days) ──
        // (int)DayOfWeek: Sunday=0 .. Saturday=6
        int firstDayOfWeek = (int)firstOfMonth.DayOfWeek;
        int leading = StartOnMonday
            ? (firstDayOfWeek == 0 ? 6 : firstDayOfWeek - 1)
            : firstDayOfWeek;

        var prevMonth = firstOfMonth.AddMonths(-1);
        int prevDays = DateTime.DaysInMonth(prevMonth.Year, prevMonth.Month);
        var days = new List<DayCell>(42);

        // Leading spillover from previous month (greyed out via IsCurrentMonth=false).
        for (int i = leading - 1; i >= 0; i--)
            days.Add(new DayCell { Day = prevDays - i, IsCurrentMonth = false });

        // Current month.
        for (int d = 1; d <= daysInMonth; d++)
        {
            var date = new DateTime(firstOfMonth.Year, firstOfMonth.Month, d);
            days.Add(new DayCell
            {
                Day = d,
                IsCurrentMonth = true,
                IsToday = date == today
            });
        }

        // Trailing spillover from next month to fill 42 cells.
        int trailing = 42 - days.Count;
        for (int d = 1; d <= trailing; d++)
            days.Add(new DayCell { Day = d, IsCurrentMonth = false });

        Days = days;

        MonthLabel = cn
            ? $"{firstOfMonth.Year}年{firstOfMonth.Month}月"
            : firstOfMonth.ToString("MMMM yyyy", CultureInfo.InvariantCulture);
    }
}