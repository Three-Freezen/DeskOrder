using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using DesktopZones.Models;

namespace DesktopZones.ViewModels;

public class CalendarViewModel : INotifyPropertyChanged
{
    private DesktopCalendar _calendar;
    public DesktopCalendar Calendar
    {
        get => _calendar;
        set { _calendar = value; OnPropertyChanged(); }
    }

    private int _displayYear;
    public int DisplayYear { get => _displayYear; set { _displayYear = value; OnPropertyChanged(); OnPropertyChanged(nameof(MonthTitle)); } }

    private int _displayMonth;
    public int DisplayMonth { get => _displayMonth; set { _displayMonth = value; OnPropertyChanged(); OnPropertyChanged(nameof(MonthTitle)); } }

    public string MonthTitle => $"{DisplayYear}年{DisplayMonth}月";

    // Calendar cells: 7 columns × 6 rows = 42 items
    public ObservableCollection<CalendarCell> Cells { get; } = new();

    // Notes for the selected date
    private string _selectedDate = "";
    public string SelectedDate => _selectedDate;

    public ObservableCollection<CalendarNoteViewModel> SelectedNotes { get; } = new();

    private bool _isLocked;
    public bool IsLocked
    {
        get => _isLocked;
        set
        {
            if (_isLocked != value)
            {
                _isLocked = value;
                LockChanged?.Invoke(value);
            }
        }
    }

    public event Action<bool>? LockChanged;

    public CalendarViewModel(DesktopCalendar calendar)
    {
        _calendar = calendar;
        DisplayYear = DateTime.Now.Year;
        DisplayMonth = DateTime.Now.Month;
        RebuildCells();
    }

    public void RebuildCells()
    {
        Cells.Clear();
        var firstDay = new DateTime(DisplayYear, DisplayMonth, 1);

        // DayOfWeek: Sunday=0, Monday=1 ...
        int startOffset = (int)firstDay.DayOfWeek;
        if (_calendar.StartOnMonday)
        {
            startOffset = startOffset == 0 ? 6 : startOffset - 1; // shift to Mon=0
        }

        var today = DateTime.Today;

        // ponytail: always 6 rows × 7 cols = 42 cells, matching August's format. Months
        // that would naturally fit in 5 rows still show Row 6 as next-month overflow —
        // consistent widget height across all months trumps saving a row of dim cells.
        for (int i = 0; i < 42; i++)
        {
            // ponytail: each cell carries a real date (prev/current/next month) instead of a
            // clamped dayNum. DayNumber + DateKey reflect the cell's actual calendar position.
            var cellDate = firstDay.AddDays(i - startOffset);
            bool inMonth = cellDate.Month == DisplayMonth && cellDate.Year == DisplayYear;
            string dateKey = cellDate.ToString("yyyy-MM-dd");
            bool isToday = cellDate == today;
            bool hasNotes = false;
            int notePriority = 0;
            if (_calendar.Notes.TryGetValue(dateKey, out var notes) && notes.Count > 0)
            {
                // Only count incomplete notes for the dot indicator. Cross-month cells now
                // also surface their notes (e.g. row-1 "31" = July 31 shows July 31's dots).
                var incomplete = notes.Where(n => !n.IsCompleted).ToList();
                hasNotes = incomplete.Count > 0;
                notePriority = incomplete.Select(n => (int)n.Priority).DefaultIfEmpty(0).Max();
            }

            Cells.Add(new CalendarCell
            {
                DayNumber = cellDate.Day,
                DateKey = dateKey,
                InMonth = inMonth,
                IsToday = isToday,
                HasNotes = hasNotes,
                NotePriority = notePriority,
                // ponytail 2026-08-28: 翻月/加备注等触发的重建会生成全新 cell 对象,
                // 选中态必须在这里回放, 否则选中边框翻一圈月份后消失。
                IsSelected = dateKey == _selectedDate
            });
        }
    }

    public void SelectDate(string dateKey)
    {
        _selectedDate = dateKey;
        OnPropertyChanged(nameof(SelectedDate));
        // ponytail 2026-08-28: 同步日格选中态 — 驱动模板里的蓝色圆角边框。
        // cell 的 IsSelected 带 INPC 通知, 无需 RebuildCells 即可实时刷新。
        foreach (var cell in Cells)
            cell.IsSelected = cell.DateKey == dateKey;
        SelectedNotes.Clear();
        if (_calendar.Notes.TryGetValue(dateKey, out var notes))
        {
            foreach (var n in notes)
                SelectedNotes.Add(new CalendarNoteViewModel(n));
        }
    }

    public void AddNote(string dateKey, string content, NotePriority priority = NotePriority.None,
        bool reminderEnabled = false, DateTime? reminderTime = null)
    {
        if (!_calendar.Notes.ContainsKey(dateKey))
            _calendar.Notes[dateKey] = new List<CalendarNote>();
        var note = new CalendarNote
        {
            Date = dateKey, Content = content, Priority = priority,
            CreatedAt = DateTime.Now,
            ReminderEnabled = reminderEnabled && reminderTime.HasValue,
            ReminderTime = reminderEnabled ? reminderTime : null,
            ReminderFired = false
        };
        _calendar.Notes[dateKey].Add(note);
        SelectedNotes.Add(new CalendarNoteViewModel(note));
    }

    public void UpdateNote(CalendarNoteViewModel noteVm, string content, NotePriority priority,
        bool reminderEnabled, DateTime? reminderTime, string? newDate = null)
    {
        // Find the note in the old date's list
        CalendarNote? item = null;
        string oldDate = noteVm.Date;

        if (_calendar.Notes.TryGetValue(oldDate, out var oldNotes))
        {
            item = oldNotes.FirstOrDefault(n => n.Id == noteVm.Id);
        }

        if (item == null) return;

        // Move to new date if changed
        string targetDate = newDate ?? oldDate;
        if (targetDate != oldDate)
        {
            oldNotes!.Remove(item);
            if (oldNotes.Count == 0) _calendar.Notes.Remove(oldDate);

            item.Date = targetDate;
            if (!_calendar.Notes.ContainsKey(targetDate))
                _calendar.Notes[targetDate] = new List<CalendarNote>();
            _calendar.Notes[targetDate].Add(item);

            // Update ViewModel reference
            noteVm.Date = targetDate;
        }

        // Update fields
        item.Content = content;
        item.Priority = priority;
        item.ReminderEnabled = reminderEnabled && reminderTime.HasValue;
        item.ReminderTime = reminderEnabled ? reminderTime : null;
        if (item.ReminderEnabled) item.ReminderFired = false;

        noteVm.Content = content;
        noteVm.Priority = priority;

        // Rebuild SelectedNotes if the selected date changed
        if (targetDate != oldDate && _selectedDate == oldDate)
        {
            SelectDate(targetDate);
        }
    }

    public void DeleteNote(CalendarNoteViewModel noteVm)
    {
        if (_calendar.Notes.TryGetValue(noteVm.Date, out var notes))
        {
            var item = notes.FirstOrDefault(n => n.Id == noteVm.Id);
            if (item != null) notes.Remove(item);
        }
        SelectedNotes.Remove(noteVm);
    }

    public void ToggleNoteComplete(CalendarNoteViewModel noteVm)
    {
        noteVm.IsCompleted = !noteVm.IsCompleted;
        if (_calendar.Notes.TryGetValue(noteVm.Date, out var notes))
        {
            var item = notes.FirstOrDefault(n => n.Id == noteVm.Id);
            if (item != null) item.IsCompleted = noteVm.IsCompleted;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class CalendarCell : INotifyPropertyChanged
{
    public int DayNumber { get; set; }
    public string DateKey { get; set; } = "";
    public bool InMonth { get; set; }
    public bool IsToday { get; set; }
    public bool HasNotes { get; set; }
    public int NotePriority { get; set; }

    // ponytail 2026-08-28: 选中态需实时驱动蓝色边框显隐, 所以带变更通知;
    // 其余属性每次 RebuildCells 都换新对象, 用不上通知。
    bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class CalendarNoteViewModel : INotifyPropertyChanged
{
    private CalendarNote _note;
    public Guid Id => _note.Id;
    public string Date { get => _note.Date; set { _note.Date = value; OnPropertyChanged(); } }
    public string Content { get => _note.Content; set { _note.Content = value; OnPropertyChanged(); } }
    public NotePriority Priority { get => _note.Priority; set { _note.Priority = value; OnPropertyChanged(); } }

    private bool _isCompleted;
    public bool IsCompleted { get => _isCompleted; set { _isCompleted = value; OnPropertyChanged(); } }

    public CalendarNoteViewModel(CalendarNote note)
    {
        _note = note;
        _isCompleted = note.IsCompleted;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
