using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using DesktopZones.Helpers;
using DesktopZones.Models;
using DesktopZones.Services;
using DesktopZones.Views.Components;

namespace DesktopZones.Views.Pages;

/// <summary>
/// Shared UI builders used by management Pages. The widget-card builders
/// (MakeWidgetCard, MakeWidgetIcon, MakeToggleDot, MakeSmallButton, MakeIconButton,
/// MakeSectionHead, MakeWidgetGridContainer) and MakeWidgetToolbar were removed
/// in Task 15 — Pages now use the EditableListRow primitive instead.
/// ponytail: RowContextMenu was removed when list-row right-click menus were
/// cancelled (operations moved into the PropertyPanel status area).
/// </summary>
public static class PageHelpers
{
    public static void SetText(TextBlock tb, string cnText)
    {
        tb.Text = cnText; // ponytail: CN-only labels (2026-08); add bilingual once loc dictionary covers pages.
    }

    public static Border MakeIconColumn(Border icon)
    {
        icon.VerticalAlignment = VerticalAlignment.Center;
        return icon;
    }

    /// <summary>
    /// Fill an <see cref="EditableListRow"/>'s icon slot from a model icon string.
    /// Native tokens ("@zones") map to the vector Path slot (IconKey); emoji map to
    /// the text slot (IconText); empty falls back to <paramref name="defaultKey"/>.
    /// </summary>
    public static void ApplyIcon(EditableListRow row, string? icon, string defaultKey)
    {
        if (IconGlyph.IsNative(icon))
        {
            row.IconKey = IconGlyph.ResourceKey(icon) ?? defaultKey;
            row.IconText = "";
        }
        else if (string.IsNullOrEmpty(icon))
        {
            row.IconKey = defaultKey;
            row.IconText = "";
        }
        else
        {
            row.IconKey = "";
            row.IconText = icon;
        }
    }

    /// <summary>
    /// Mark exactly one <see cref="EditableListRow"/> in <paramref name="host"/> as selected
    /// — the one whose <c>Tag</c> reference-equals <paramref name="selected"/>. Passing
    /// <c>null</c> clears all rows. Pages set row.Tag in BuildRow and call this on click.
    /// ponytail: walks Items, O(n), n is page list size (&lt;50).
    /// </summary>
    public static void SetSelection(ItemsControl host, object? selected)
    {
        if (host == null) return;
        foreach (var item in host.Items)
            if (item is EditableListRow row)
                row.IsSelected = selected != null && ReferenceEquals(row.Tag, selected);
    }

    /// <summary>
    /// Move a row inside the page's live row collection (drag reorder). Mirrors
    /// ObservableCollection.Move semantics: <paramref name="targetIndex"/> is the
    /// final 0-based index after removal. Clamps to [0, Count-1]; no-op on same
    /// index. Pages call this right after the matching model-level Move so the
    /// ItemsControl shifts live while the drag is still in progress.
    /// </summary>
    public static void MoveRow(System.Collections.ObjectModel.ObservableCollection<EditableListRow> rows,
        EditableListRow src, int targetIndex)
    {
        int cur = rows.IndexOf(src);
        if (cur < 0 || cur == targetIndex) return;
        if (targetIndex < 0) targetIndex = 0;
        if (targetIndex > rows.Count - 1) targetIndex = rows.Count - 1;
        rows.Move(cur, targetIndex);
    }
}
