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
/// ponytail: SetSelection + RowContextMenu here so each page stays one-liner-thin.
/// </summary>
public static class PageHelpers
{
    public static SolidColorBrush FgPrimary => ThemeBrushes.T1;
    public static SolidColorBrush FgSecondary => ThemeBrushes.T2;
    public static SolidColorBrush FgMuted => ThemeBrushes.DimTextFg;

    public static Border MakeCardBorder() => new()
    {
        Background = ThemeBrushes.CardBg,
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(12),
        Margin = new Thickness(0, 0, 0, 6),
        BorderBrush = ThemeBrushes.CardBorder,
        BorderThickness = new Thickness(1)
    };

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
    /// Show a sort-options menu via the same <see cref="RowContextMenu"/> chrome.
    /// Prepends a check mark to the currently active option. On selection, calls
    /// <paramref name="onPick"/> with the chosen index (caller then updates state and RefreshList).
    /// ponytail: reuses the existing popup pipeline — no second chrome to maintain.
    /// </summary>
    public static void ShowSortMenu(FrameworkElement placement, string[] labels, int currentIndex, Action<int> onPick)
    {
        var items = new List<RowContextMenu.Item>(labels.Length);
        for (int i = 0; i < labels.Length; i++)
        {
            int captured = i;
            var prefix = captured == currentIndex ? "✓ " : "   ";
            items.Add(new RowContextMenu.Item(prefix + labels[captured], () => onPick(captured)));
        }
        RowContextMenu.Show(placement, items);
    }
}

/// <summary>
/// Minimal dark-mode context menu used by EditableListRow right-click handlers.
/// Each page builds an <see cref="RowContextMenu"/> config (label + callback + optional
/// danger flag) and calls <see cref="Show"/> from a row's <c>PreviewMouseRightButtonUp</c>.
/// ponytail: Popup-based to avoid the built-in ContextMenu style (would inherit white chrome);
/// auto-closes on outside click via window-level PreviewMouseDown that unsubscribes on close.
/// </summary>
public sealed class RowContextMenu
{
    public record Item(string Label, Action OnClick, bool Danger = false);

    public static void Show(FrameworkElement placement, IList<Item> items)
    {
        if (items.Count == 0) return;

        var popup = new Popup
        {
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            StaysOpen = false,
            PlacementTarget = placement,
            Placement = PlacementMode.Bottom,
        };

        // ponytail: pull from ThemeBrushes so the menu follows the active palette.
        // ThemeService.RepaintBrushes replaces the resource dictionary entries on every
        // theme swap, so the next time the menu is opened it picks up the new color.
        var hoverBrush = ThemeBrushes.HoverOverlay;
        var menuBorder = new Border
        {
            Background = ThemeBrushes.BgSurface,
            BorderBrush = ThemeBrushes.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(4),
            MinWidth = 180,
        };

        var stack = new StackPanel();
        foreach (var it in items)
        {
            var captured = it;
            var fg = captured.Danger ? ThemeBrushes.DangerBrush : ThemeBrushes.T1;

            var itemBorder = new Border
            {
                Background = Brushes.Transparent,
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(2, 1, 2, 1),
                Cursor = Cursors.Hand,
            };
            itemBorder.Child = new TextBlock
            {
                Text = captured.Label,
                Foreground = fg,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
            };
            itemBorder.MouseEnter += (_, _) => itemBorder.Background = hoverBrush;
            itemBorder.MouseLeave += (_, _) => itemBorder.Background = Brushes.Transparent;
            itemBorder.MouseLeftButtonDown += (_, _) =>
            {
                popup.IsOpen = false;
                captured.OnClick();
            };
            stack.Children.Add(itemBorder);
        }

        menuBorder.Child = stack;
        popup.Child = menuBorder;

        // ponytail: Popup.StaysOpen = false already auto-closes on outside click.
        // No manual handler needed; item click sets IsOpen = false itself.
        popup.IsOpen = true;
    }
}
