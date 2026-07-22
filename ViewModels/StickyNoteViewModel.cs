using System.ComponentModel;
using System.Runtime.CompilerServices;
using DesktopZones.Models;

namespace DesktopZones.ViewModels;

public class StickyNoteViewModel : INotifyPropertyChanged
{
    private StickyNote _note;
    public StickyNote Note
    {
        get => _note;
        set { _note = value; OnPropertyChanged(); }
    }

    private string _title = "";
    public string Title { get => _title; set { _title = value; OnPropertyChanged(); } }

    private string _content = "";
    public string Content { get => _content; set { _content = value; OnPropertyChanged(); } }

    private string _noteColor = "#30FFF9C4";
    public string NoteColor { get => _noteColor; set { _noteColor = value; OnPropertyChanged(); } }

    private double _fontSize = 14;
    public double FontSize { get => _fontSize; set { _fontSize = value; OnPropertyChanged(); } }

    private bool _pinnedTop;
    public bool PinnedTop { get => _pinnedTop; set { _pinnedTop = value; OnPropertyChanged(); } }

    public StickyNoteViewModel(StickyNote note)
    {
        _note = note;
        Title = note.Title;
        Content = note.Content;
        NoteColor = note.NoteColor;
        FontSize = note.FontSize;
        PinnedTop = note.PinnedTop;
    }

    public void ApplyToModel()
    {
        _note.Title = Title;
        _note.Content = Content;
        _note.NoteColor = NoteColor;
        _note.FontSize = FontSize;
        _note.PinnedTop = PinnedTop;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
