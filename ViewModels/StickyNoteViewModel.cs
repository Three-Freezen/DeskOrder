using System;
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

    // ponytail 2026-11-28: FontSize 已升格为「便签文档样式」,由 StickyNote 自己持有。
    // 原来 VM 缓存一份构造时的值、关窗时 ApplyToModel 写回模型 —— 设置面板改了字号后
    // 关窗会被旧值覆盖,所以这里彻底删掉。

    private bool _pinnedTop;
    public bool PinnedTop { get => _pinnedTop; set { _pinnedTop = value; OnPropertyChanged(); } }

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

    public StickyNoteViewModel(StickyNote note)
    {
        _note = note;
        Title = note.Title;
        Content = note.Content;
        NoteColor = note.NoteColor;
        PinnedTop = note.PinnedTop;
    }

    public void ApplyToModel()
    {
        _note.Title = Title;
        _note.Content = Content;
        _note.NoteColor = NoteColor;
        _note.PinnedTop = PinnedTop;
        _note.IsLocked = IsLocked;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
