namespace DesktopZones.Models;

public class StickyNotePreset : PresetRecord
{
    public StickyNote Note { get; set; } = new();

    public StickyNotePreset() { Kind = PresetKind.StickyNote; }
}