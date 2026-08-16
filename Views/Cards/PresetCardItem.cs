using System;
using System.ComponentModel;
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
public class PresetCardItem : INotifyPropertyChanged
{
    public PresetRecord Record { get; }
    public object Payload { get; }

    public string Name => Record.Name;
    public DateTime CreatedAt => Record.CreatedAt;
    public PresetKind Kind => Record.Kind;

    /// <summary>Panel preset's primary fill color as a fully-formed <see cref="SolidColorBrush"/> (WPF
    /// can't always auto-convert a hex string to a brush when it's coming through an object cast —
    /// going through <see cref="ColorConverter"/> on the getter avoids silent fallback rendering).</summary>
    public System.Windows.Media.Brush? PanelFillBrush {
        get {
            try {
                var s = (Payload as PanelPresetConfig)?.PanelFillColor;
                if (string.IsNullOrEmpty(s)) return null;
                var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(s)!;
                return new System.Windows.Media.SolidColorBrush(c);
            } catch { return null; }
        }
    }
    /// <summary>Panel preset's border color as a <see cref="SolidColorBrush"/>.</summary>
    public System.Windows.Media.Brush? PanelBorderBrush {
        get {
            try {
                var s = (Payload as PanelPresetConfig)?.PanelBorderColor;
                if (string.IsNullOrEmpty(s)) return null;
                var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(s)!;
                return new System.Windows.Media.SolidColorBrush(c);
            } catch { return null; }
        }
    }
    /// <summary>Panel preset's primary fill color as hex string (kept for diagnostics / future string bindings).</summary>
    public string PanelFillColor => (Payload as PanelPresetConfig)?.PanelFillColor ?? "";
    /// <summary>Panel preset's border color as hex string.</summary>
    public string PanelBorderColor => (Payload as PanelPresetConfig)?.PanelBorderColor ?? "";

    /// <summary>Clock-only: which display mode the preset stores on disk. Null for non-Clock kinds.</summary>
    public ClockDisplayMode? ClockMode => Payload is DesktopClock c ? c.Mode : null;

    private ClockDisplayMode? _displayMode;
    /// <summary>
    /// Clock-only: which template the selector should render this card with.
    /// Defaults to the preset's stored <see cref="ClockMode"/>, but the dialog can
    /// override this to the live widget's current mode so all clock cards re-render
    /// in the matching style when the user toggles Digital ↔ Analog on the live clock.
    /// </summary>
    public ClockDisplayMode? DisplayClockMode
    {
        get => _displayMode ?? ClockMode;
        set
        {
            if (_displayMode != value)
            {
                _displayMode = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayClockMode)));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public PresetCardItem(PresetRecord record)
    {
        Record = record;
        Payload = PresetService.GetPayload(record);
    }
}