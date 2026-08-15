using System.Windows;
using DesktopZones.Models;
using DesktopZones.Services;

namespace DesktopZones.Views;

/// <summary>
/// Single entry-point for "load preset" / "save preset" buttons across the
/// app's settings dialogs. Wraps dialog construction + service instantiation
/// so callers write one line per button instead of two.
///
/// All kinds share the same <see cref="LoadPresetDialog"/> window — its
/// internal <see cref="PresetCardTemplateSelector"/> picks the right card
/// template based on each preset's <see cref="PresetRecord.Kind"/>.
/// </summary>
public static class PresetButtonsHelper
{
    /// <summary>
    /// Show the Load Preset dialog. Returns the dialog's <see cref="System.Windows.Window.ShowDialog"/>
    /// result (true = Apply, false / null = Cancel / closed).
    /// <para>
    /// <paramref name="onCardPicked"/> fires every time the user clicks a card (real-time preview
    /// hook); <paramref name="onPicked"/> fires once after the dialog closes with OK and receives
    /// the typed payload (Zone / DesktopClock / …). Callers wanting to mirror Zone's pattern should
    /// do work in <paramref name="onCardPicked"/> and leave <paramref name="onPicked"/> empty.
    /// </para>
    /// </summary>
    public static bool? OpenLoad(Window owner, PresetKind kind, object? currentPayload = null,
        Action<object>? onPicked = null,
        Action<PresetRecord>? onCardPicked = null)
    {
        var svc = PresetService.For(kind);
        var widgetSvc = TryGetWidgetService();
        var dlg = new LoadPresetDialog(svc, widgetSvc, onCardPicked)
        {
            Owner = owner
        };
        var result = dlg.ShowDialog();
        if (result == true && dlg.SelectedPayload != null)
        {
            onPicked?.Invoke(dlg.SelectedPayload);
        }
        return result;
    }

    /// <summary>Show the Save Preset dialog with the current component state as payload.</summary>
    public static void OpenSave(Window owner, PresetKind kind, object payload)
    {
        var svc = PresetService.For(kind);
        var dlg = new SavePresetDialog(svc, payload) { Owner = owner };
        dlg.ShowDialog();
    }

    private static WidgetService? TryGetWidgetService()
    {
        if (System.Windows.Application.Current is App app && app.ManagementWindow?.WidgetService is { } ws)
            return ws;
        return null;
    }
}