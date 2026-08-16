using System.Windows;
using System.Windows.Controls;
using DesktopZones.Models;
using DesktopZones.Views.Cards;

namespace DesktopZones.Views;

/// <summary>
/// Picks the right card DataTemplate based on the preset kind. All templates
/// live as resources inside <see cref="LoadPresetDialog"/> and are wired up
/// in its constructor; the selector just looks them up by key. This avoids
/// runtime XamlReader.Parse — handler bindings like MouseEnter="Card_MouseEnter"
/// only resolve when the template is part of the compiled dialog tree.
/// </summary>
public class PresetCardTemplateSelector : DataTemplateSelector
{
    public DataTemplate? ZoneTemplate { get; set; }
    public DataTemplate? ClockAnalogTemplate { get; set; }
    public DataTemplate? ClockDigitalTemplate { get; set; }
    public DataTemplate? CalendarTemplate { get; set; }
    public DataTemplate? StickyNoteTemplate { get; set; }
    public DataTemplate? MergedGroupTemplate { get; set; }
    public DataTemplate? PanelTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        if (item is not PresetCardItem ci) return null;
        return ci.Kind switch
        {
            PresetKind.Zone => ZoneTemplate,
            // Clock dispatches on DisplayClockMode (overridable by the dialog to match the live
            // widget's current Digital/Analog state). Falls back to stored ClockMode when not set.
            PresetKind.Clock => ci.DisplayClockMode == ClockDisplayMode.Analog ? ClockAnalogTemplate : ClockDigitalTemplate,
            PresetKind.Calendar => CalendarTemplate,
            PresetKind.StickyNote => StickyNoteTemplate,
            PresetKind.MergedGroup => MergedGroupTemplate,
            PresetKind.Panel => PanelTemplate,
            _ => null
        };
    }
}