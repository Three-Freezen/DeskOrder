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
        int daysInMonth = DateTime.DaysInMonth(DisplayYear, DisplayMonth);

        // DayOfWeek: Sunday=0, Monday=1 ...
        int startOffset = (int)firstDay.DayOfWeek;
        if (_calendar.StartOnMonday)
        {
            startOffset = startOffset == 0 ? 6 : startOffset - 1; // shift to Mon=0
        }

        var today = DateTime.Today;

        for (int i = 0; i < 42; i++)
        {
            int dayNum = i - startOffset + 1;
            bool inMonth = dayNum >= 1 && dayNum <= daysInMonth;
            var date = new DateTime(DisplayYear, DisplayMonth, Math.Clamp(dayNum, 1, daysInMonth));
            string dateKey = date.ToString("yyyy-MM-dd");
            bool isToday = inMonth && date == today;
            bool hasNotes = false;
            int notePriority = 0;
            if (inMonth && _calendar.Notes.TryGetValue(dateKey, out var notes) && notes.Count > 0)
            {
                // Only count incomplete notes for the dot indicator
                var incomplete = notes.Where(n => !n.IsCompleted).ToList();
                hasNotes = incomplete.Count > 0;
                notePriority = incomplete.Select(n => (int)n.Priority).DefaultIfEmpty(0).Max();
            }

            Cells.Add(new CalendarCell
            {
                DayNumber = dayNum,
                DateKey = dateKey,
                InMonth = inMonth,
                IsToday = isToday,
                HasNotes = hasNotes,
                NotePriority = notePriority
            });
        }
    }

    public void SelectDate(string dateKey)
    {
        _selectedDate = dateKey;
        OnPropertyChanged(nameof(SelectedDate));
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
