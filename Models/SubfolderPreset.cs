using System;

namespace DesktopZones.Models;

/// <summary>
/// Preset record for a Subfolder's STYLE ONLY (no SubItems content) — applying
/// the preset overwrites all SubFolder专属 fields + Name on the target SubFolder,
/// leaving SubItems untouched. <see cref="Subfolder"/> is a deep clone.
/// ponytail: kept lean — SubItems is intentionally not in scope.
/// </summary>
public class SubfolderPreset : PresetRecord
{
    public ZoneItem Subfolder { get; set; } = new();

    public SubfolderPreset() { Kind = PresetKind.Subfolder; }

    public SubfolderPreset Clone() => new()
    {
        Id = Id,
        Name = Name,
        CreatedAt = CreatedAt,
        Kind = Kind,
        Subfolder = Subfolder.Clone(),
    };
}