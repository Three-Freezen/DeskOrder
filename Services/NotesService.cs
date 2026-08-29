using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using DesktopZones.Helpers;
using DesktopZones.Models;
using DesktopZones.Views;

namespace DesktopZones.Services;

public class NotesService
{
    private readonly ConfigService _configService;
    private AppConfig? _appConfig;

    /// <summary>Open StickyNoteWindow instances keyed by note Id. Owned by this service
    /// (was App._noteWindows before P5). Public so App.xaml.cs can read/write through
    /// the service.</summary>
    public Dictionary<Guid, StickyNoteWindow> Windows { get; } = new();

    public StickyNoteWindow? GetWindow(Guid id)
        => Windows.TryGetValue(id, out var w) ? w : null;

    public ObservableCollection<StickyNote> Notes { get; } = new();
    public event Action? NotesChanged;
    /// <summary>Fires when a note's lock state changes. Args: noteId (string), isLocked.</summary>
    public event Action<string, bool>? LockChanged;

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
        if (note != null)
        {
            // 同步关闭该便签的样式设置界面(浮动 + 停靠),避免残留编辑器。
            PropertyWindowService.CloseEditorsFor(note);
            Notes.Remove(note);
        }
        Save();
        DeleteNoteFile(id);
        NotesChanged?.Invoke();
    }

    /// <summary>Move a note to a new position (long-press drag reorder).
    /// ObservableCollection.Move fires CollectionChanged → ItemsControl reorders.
    /// Skips NotesChanged so RefreshList doesn't rebuild rows mid-drag.</summary>
    public void MoveNote(Guid noteId, int newIndex)
    {
        var note = Notes.FirstOrDefault(n => n.Id == noteId);
        if (note == null) return;
        int oldIndex = Notes.IndexOf(note);
        if (oldIndex < 0 || oldIndex == newIndex) return;
        if (newIndex < 0) newIndex = 0;
        if (newIndex > Notes.Count - 1) newIndex = Notes.Count - 1;
        Notes.Move(oldIndex, newIndex);
        Save();
    }

    public void Save()
    {
        if (_appConfig == null) return;
        ConfigSaver.SavePreservingPanelSettings(_configService, cfg =>
        {
            cfg.Notes = Notes.ToList();
        });
    }

    // ── 便签富文本独立 JSON 文件 ──

    // ponytail 2026-08-29: 落点随 DataLocator(安装器可选 AppData / 便携 Data)。
    private static string NotesDir => Path.Combine(DataLocator.Root, "Notes");

    private static readonly JsonSerializerOptions NoteFileJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public string NoteFilePath(Guid id) => Path.Combine(NotesDir, $"{id:N}.json");

    public NoteFileData? LoadNoteFile(Guid id)
    {
        try
        {
            var path = NoteFilePath(id);
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<NoteFileData>(json, NoteFileJsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public void SaveNoteFile(Guid id, NoteFileData data)
    {
        try
        {
            Directory.CreateDirectory(NotesDir);
            var json = JsonSerializer.Serialize(data, NoteFileJsonOptions);
            File.WriteAllText(NoteFilePath(id), json);
        }
        catch
        {
            // 独立文件写失败不应影响主配置保存。
        }
    }

    public void DeleteNoteFile(Guid id)
    {
        try
        {
            var path = NoteFilePath(id);
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 文件可能被占用/已不存在,忽略。
        }
    }

    /// <summary>Set locked state for a note by string id. Mirrors WidgetService.SetLocked — no event,
    /// no disk write. Caller is expected to UpdateNote() right after for persistence.</summary>
    public void SetLocked(string noteId, bool locked)
    {
        if (!Guid.TryParse(noteId, out var guid)) return;
        var note = Notes.FirstOrDefault(n => n.Id == guid);
        if (note == null) return;
        note.IsLocked = locked;
        LockChanged?.Invoke(noteId, locked);
    }
}
