using System;

namespace DesktopZones.Models;

public class ZonePreset : PresetRecord
{
    public Zone Zone { get; set; } = new();

    public ZonePreset() { Kind = PresetKind.Zone; }

    public ZonePreset Clone() => new()
    {
        Id = Id,
        Name = Name,
        CreatedAt = CreatedAt,
        Kind = Kind,
        Zone = Zone.Clone()
    };
}