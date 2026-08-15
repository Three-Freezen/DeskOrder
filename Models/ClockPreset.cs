namespace DesktopZones.Models;

public class ClockPreset : PresetRecord
{
    public DesktopClock Clock { get; set; } = new();

    public ClockPreset() { Kind = PresetKind.Clock; }
}