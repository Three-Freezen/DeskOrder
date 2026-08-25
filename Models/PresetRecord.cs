using System;

namespace DesktopZones.Models;

/// <summary>
/// Which component a preset describes. Drives JSON deserialization dispatch and
/// which <see cref="Views.LoadPresetDialog"/> card template to render.
/// </summary>
public enum PresetKind
{
    Zone,
    Clock,
    Calendar,
    StickyNote,
    MergedGroup,
    Panel,
    Subfolder,
}

/// <summary>
/// Common metadata for every preset record (Zone / Clock / Calendar /
/// StickyNote / MergedGroup / Panel). The strongly-typed payload lives on
/// each derived class (<see cref="ZonePreset.Zone"/>, <see cref="ClockPreset.Clock"/>,
/// etc.). Each preset JSON file looks like
/// <c>{ Id, Name, CreatedAt, Kind, &lt;PayloadField&gt;: {...} }</c>.
/// </summary>
public class PresetRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New Preset";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public PresetKind Kind { get; set; } = PresetKind.Zone;

    /// <summary>Sub-folder under ./Presets where files for this kind are stored.</summary>
    public static string SubFolderFor(PresetKind kind) => kind switch
    {
        PresetKind.Zone => "Zones",
        PresetKind.Clock => "Clocks",
        PresetKind.Calendar => "Calendars",
        PresetKind.StickyNote => "StickyNotes",
        PresetKind.MergedGroup => "MergedGroups",
        PresetKind.Panel => "Panels",
        PresetKind.Subfolder => "Subfolders",
        _ => "Zones"
    };
}