namespace DesktopZones.Models;

/// <summary>
/// MergedGroup preset. Payload is the master <see cref="Zone"/> (which carries
/// the MergedGroup* style fields + MergedSubZoneIds). Sub-zone name/icon
/// resolution happens at render time via the running ZoneManager.
/// </summary>
public class MergedGroupPreset : PresetRecord
{
    public Zone Zone { get; set; } = new();

    public MergedGroupPreset() { Kind = PresetKind.MergedGroup; }
}