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
    readonly LocalizationService _loc = LocalizationService.Instance;
    StickyNote? _selected;
    // ponytail: live row collection bound to ListHost — drag reorder moves rows
    // through this OC (mirroring PropertyTabStrip's Tabs OC) so the ItemsControl
    // shifts live while the model collection moves in parallel for persistence.
    // ponytail 2026-11-14(性能审计): 批量替换行集合,只发一次 Reset。
    readonly BulkObservableCollection<EditableListRow> _rows = new();

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
        CountLabel.Text = _loc.Get("StickyNotePage.ItemCount", notes.Count);
        // 拖动排序即列表顺序：不再重排，Rows 直接镜像 Notes 的持久化顺序。
        _rows.ReplaceAll(notes.Select(BuildRow));
        EmptyHint.Visibility = notes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SetSelection(ListHost, _selected);
    }

    EditableListRow BuildRow(StickyNote note)
    {
        var title = string.IsNullOrEmpty(note.Title) ? _loc["StickyNotePage.FallbackTitle"] : note.Title;
        var chars = string.IsNullOrEmpty(note.Content) ? 0 : note.Content.Length;
        var updated = string.Format(_loc["StickyNotePage.Updated"],
            note.ModifiedAt.ToString("MM-dd"));
        var row = new EditableListRow
        {
            Tag = note,
            Title = title,
            Subtitle = string.Format(_loc["StickyNotePage.Subtitle"], chars, updated),
        };
        ApplyIcon(row, note.IconChar, "Icon.Sticky");
        row.ReorderRequested += (src, targetIdx) =>
        {
            if (src.Tag is not StickyNote n || _notesService == null) return;
            _notesService.MoveNote(n.Id, targetIdx);     // 模型 + 持久化
            MoveRow(_rows, src, targetIdx);              // 列表实时换位（镜像标签栏）
        };
        // ponytail: status badge removed (lock/hidden/hotkey chips no longer shown).
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
        row.PreviewMouseLeftButtonUp += (_, _) => Select(note);
        // 右键菜单已取消：显示/隐藏、删除、快捷键设置全部移到属性面板顶部状态区。
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

    void NewNote_Click(object sender, RoutedEventArgs e)
    {
        _main.NewNote();
        RefreshList();
    }
}
