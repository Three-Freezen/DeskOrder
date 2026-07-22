using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using DesktopZones.Models;

namespace DesktopZones.Services;

public class NotesService
{
    private readonly ConfigService _configService;
    private AppConfig? _appConfig;

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
        // Reload config to preserve settings managed by other components (e.g., hotkeys)
        var latestConfig = _configService.Load();
        _appConfig.Notes = Notes.ToList();
        // Preserve hotkey and other settings from the latest config
        _appConfig.PanelHotkeyEnabled = latestConfig.PanelHotkeyEnabled;
        _appConfig.PanelHotkeyModifiers = latestConfig.PanelHotkeyModifiers;
        _appConfig.PanelHotkeyKey = latestConfig.PanelHotkeyKey;
        _appConfig.PanelCustomHotkeys = latestConfig.PanelCustomHotkeys;
        _appConfig.PanelUseGlobalAppearance = latestConfig.PanelUseGlobalAppearance;
        try { _configService.Save(_appConfig); } catch { }
    }
}
