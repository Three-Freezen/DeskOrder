using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DesktopZones.Helpers;
using DesktopZones.Models;
using DesktopZones.Services;
using DesktopZones.ViewModels;
using DesktopZones.Views.Components;
using static DesktopZones.Views.Pages.PageHelpers;
using RelayCommand = DesktopZones.ViewModels.RelayCommand;

namespace DesktopZones.Views.Pages;

/// <summary>
/// Sticky-note list page. Renders one EditableListRow per StickyNote.
/// Title shows the note's title (or "便签" when blank); subtitle shows
/// character count and last-modified date.
/// </summary>
public partial class StickyNotePage : UserControl
{
    readonly ManagementWindow _main;
    readonly NotesService? _notesService;
    StickyNote? _selected;
    // ponytail: live row collection bound to ListHost — drag reorder moves rows
    // through this OC (mirroring PropertyTabStrip's Tabs OC) so the ItemsControl
    // shifts live while the model collection moves in parallel for persistence.
    readonly ObservableCollection<EditableListRow> _rows = new();

    public StickyNotePage(ManagementWindow main, ManagementViewModel vm, NotesService? notesService)
    {
        InitializeComponent();
        _main = main;
        _notesService = notesService;
        ListHost.ItemsSource = _rows;
        // ponytail 2026-08-25: Persist is wired centrally in ManagementWindow
        // (WirePropertyPanelPersist) — one dispatcher for all target types,
        // docked and floating. Pages no longer overwrite it.
        Loaded += (_, _) =>
        {
            if (_notesService != null)
                _notesService.NotesChanged += RefreshList;
            RefreshList();
        };
        Unloaded += (_, _) =>
        {
            if (_notesService != null)
                _notesService.NotesChanged -= RefreshList;
        };
    }

    public void ApplyLoc() => RefreshList();

    public void RefreshList()
    {
        if (_notesService == null) return;
        var notes = _notesService.Notes;
        CountLabel.Text = $"{notes.Count} 项";
        // 拖动排序即列表顺序：不再重排，Rows 直接镜像 Notes 的持久化顺序。
        _rows.Clear();
        foreach (var n in notes) _rows.Add(BuildRow(n));
        EmptyHint.Visibility = notes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SetSelection(ListHost, _selected);
    }

    EditableListRow BuildRow(StickyNote note)
    {
        var title = string.IsNullOrEmpty(note.Title) ? "便签" : note.Title;
        var chars = string.IsNullOrEmpty(note.Content) ? 0 : note.Content.Length;
        var updated = note.ModifiedAt.ToString("MM-dd 更新");
        var row = new EditableListRow
        {
            Tag = note,
            Title = title,
            Subtitle = $"{chars} 字 · {updated}",
            IconKey = "Icon.Sticky",
            IsLocked = note.IsLocked,
            IsVisible = note.IsVisible,
        };
        row.ReorderRequested += (src, targetIdx) =>
        {
            if (src.Tag is not StickyNote n || _notesService == null) return;
            _notesService.MoveNote(n.Id, targetIdx);     // 模型 + 持久化
            MoveRow(_rows, src, targetIdx);              // 列表实时换位（镜像标签栏）
        };
        // ponytail: status badge removed (lock/hidden/hotkey chips no longer shown).
        row.LockCommand = new RelayCommand(_ =>
        {
            note.IsLocked = !note.IsLocked;
            _notesService?.UpdateNote(note);
            RefreshList();
        });
        row.VisibilityCommand = new RelayCommand(_ =>
        {
            // ponytail: 2026-08-26 — route straight through ToggleNoteWindow (see
            // ClockPage). Pre-flipping the model fired NotesChanged → ghost-stamp snap,
            // which reversed the toggle and left a transparent ghost.
            _main.ToggleNoteWindow(note);
        });
        row.DeleteCommand = new RelayCommand(_ => Delete(note));
        row.RenameCommand = new RelayCommand(p =>
        {
            var n = p?.ToString();
            if (!string.IsNullOrEmpty(n))
            {
                note.Title = n;
                _notesService?.UpdateNote(note);
                RefreshList();
            }
        });
        row.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject src)
            {
                var parent = src;
                while (parent != null && parent is not Button)
                    parent = LogicalTreeHelper.GetParent(parent);
                if (parent is Button) return;
            }
            // ponytail: workspace direction (dock only). Old code also called
            // PropertyWindowService.OpenOrFocus which created a duplicate
            // floating editor while the docked panel already showed this note.
            Select(note);
        };
        row.PreviewMouseRightButtonUp += (_, e) =>
        {
            Select(note);
            ShowNoteContextMenu(note, row);
        };
        return row;
    }

    void Select(StickyNote note)
    {
        // ponytail: route through DockTarget so any pre-existing floating editor
        // for this note is closed before the docked panel takes over.
        PropertyWindowManager.Instance.DockTarget(note, _main);
        if (!ReferenceEquals(_selected, note))
        {
            _selected = note;
            SetSelection(ListHost, note);
        }
    }

    void ShowNoteContextMenu(StickyNote note, EditableListRow row)
    {
        var items = new List<RowContextMenu.Item>
        {
            new(note.IsVisible ? "隐藏便签" : "显示便签", () => _main.ToggleNoteWindow(note)),
            new("设置快捷键", () => _main.ShowNoteHotkeyRecorderDialog(note)),
        };
        items.Add(new("删除便签", () => Delete(note), Danger: true));
        RowContextMenu.Show(row, items);
    }

    void NewNote_Click(object sender, RoutedEventArgs e)
    {
        _main.NewNote();
        RefreshList();
    }

    void Delete(StickyNote note)
    {
        if (MessageBox.Show($"删除便签「{note.Title}」？", "删除便签",
            MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _main.DeleteNote(note);
        if (ReferenceEquals(_selected, note)) _selected = null;
        RefreshList();
    }
}
