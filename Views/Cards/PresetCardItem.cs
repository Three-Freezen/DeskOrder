using System;
using DesktopZones.Models;
using DesktopZones.Services;

namespace DesktopZones.Views.Cards;

/// <summary>
/// Wrapper around a <see cref="PresetRecord"/> that exposes the typed payload
/// as a public <see cref="Payload"/> property. Card XAML templates bind via
/// <c>{Binding Payload.XXX}</c> and reflection finds the right field on the
/// runtime payload type (Zone, DesktopClock, …).
///
/// Why: each card is a <see cref="System.Windows.DataTemplate"/> with a static
/// binding path. The ItemsControl's items are PresetRecord (base), but each
/// kind's card template wants to bind to the typed payload's fields. A small
/// wrapper keeps XAML declarative and avoids a TemplateSelector or per-kind
/// converter.
/// </summary>
public class PresetCardItem
{
    public PresetRecord Record { get; }
    public object Payload { get; }

    public string Name => Record.Name;
    public DateTime CreatedAt => Record.CreatedAt;
    public PresetKind Kind => Record.Kind;

    public PresetCardItem(PresetRecord record)
    {
        Record = record;
        Payload = PresetService.GetPayload(record);
    }
}