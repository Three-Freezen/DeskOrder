using System;

namespace DesktopZones.Models;

public class ZonePreset
{
    public string Name { get; set; } = "New Preset";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public Zone Zone { get; set; } = new();

    public ZonePreset Clone()
    {
        return new ZonePreset
        {
            Name = Name,
            CreatedAt = CreatedAt,
            Zone = Zone.Clone()
        };
    }
}
