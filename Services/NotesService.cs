using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using DesktopZones.Models;
using DesktopZones.Views;

namespace DesktopZones.Services;

public class NotesService
{
    private readonly ConfigService _configService;
    private AppConfig? _appConfig;

    /// <summary>Open StickyNoteWindow instances keyed by note Id. Owned by this service
    /// (was App._noteWindows before P5). Public so App.xaml.cs and WidgetSettingsDialog
    /// can read/write through the service.</summary>
    public Dictionary<Guid, StickyNoteWindow> Windows { get; } = new();

    public StickyNoteWindow? GetWindow(Guid id)
        => Windows.TryGetValue(id, out var w) ? w : null;

    public ObservableCollection<StickyNote> Notes { get; } = new();
    public event Action? NotesChanged;

    public AppConfig GetConfig() => _appConfig!;

    public NotesService(ConfigService configService)
    {
        _configService = configService;
    }

    public void Load(AppConfig config)
    {
        _appConfig = config;
        Notes.Clear();
        foreach (var note in config.Notes)
            Notes.Add(note);
    }

    public StickyNote CreateNote(double x = 300, double y = 200)
    {
        var note = new StickyNote { X = x, Y = y, CreatedAt = DateTime.Now, ModifiedAt = DateTime.Now };
        Notes.Add(note);
        Save();
        NotesChanged?.Invoke();
        return note;
    }

    public void UpdateNote(StickyNote note)
    {
        note.ModifiedAt = DateTime.Now;
        var existing = Notes.FirstOrDefault(n => n.Id == note.Id);
        if (existing != null)
        {
            var idx = Notes.IndexOf(existing);
            Notes[idx] = note;
        }
        Save();
        NotesChanged?.Invoke();
    }

    public void DeleteNote(Guid id)
    {
        var note = Notes.FirstOrDefault(n => n.Id == id);
        if (note != null) Notes.Remove(note);
        Save();
        NotesChanged?.Invoke();
    }

    public void Save()
    {
        if (_appConfig == null) return;
        ConfigSaver.SavePreservingPanelSettings(_configService, cfg =>
        {
            cfg.Notes = Notes.ToList();
        });
    }
}
