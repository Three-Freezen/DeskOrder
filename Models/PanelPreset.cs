namespace DesktopZones.Models;

public class PanelPreset : PresetRecord
{
    public PanelPresetConfig Config { get; set; } = new();

    public PanelPreset() { Kind = PresetKind.Panel; }
}