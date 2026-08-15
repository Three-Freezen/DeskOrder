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
    public DataTemplate? ClockTemplate { get; set; }
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
            PresetKind.Clock => ClockTemplate,
            PresetKind.Calendar => CalendarTemplate,
            PresetKind.StickyNote => StickyNoteTemplate,
            PresetKind.MergedGroup => MergedGroupTemplate,
            PresetKind.Panel => PanelTemplate,
            _ => null
        };
    }
}