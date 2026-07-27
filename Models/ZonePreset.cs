using System;

namespace DesktopZones.Models;

public class ZonePreset
{
    /// <summary>
    /// Persistent identifier used as the on-disk filename ("./Presets/Zones/{id}.json").
    /// Preserved across renames so re-saves with the same display name reuse the same file.
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "New Preset";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public Zone Zone { get; set; } = new();

    public ZonePreset Clone()
    {
        return new ZonePreset
        {
            Id = Id,
            Name = Name,
            CreatedAt = CreatedAt,
            Zone = Zone.Clone()
        };
    }
}
