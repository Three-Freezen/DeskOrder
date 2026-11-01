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

    /// <summary>播放器（律动条）预设。与唱片机分开存放，互不混用。</summary>
    MediaPlayerBars,

    /// <summary>播放器（唱片机）预设。与律动条分开存放，互不混用。</summary>
    MediaPlayerVinyl,

    /// <summary>快捷栏（Dock 形态的分区）预设。和普通分区分开存放 —— 两边的外观
    /// 字段不是同一个集合（条体尺寸由内容算、没有标题栏 vs 没有悬停放大）。
    /// 落在 <c>Presets/Docks</c>，卡片是「一根长条」的自绘缩略图。</summary>
    Dock,
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
        // 律动条 / 唱片机分开存放：两个形态的外形字段完全不同，混在一起只会互相干扰。
        PresetKind.MediaPlayerBars => "MediaPlayersBars",
        PresetKind.MediaPlayerVinyl => "MediaPlayersVinyl",
        // 快捷栏与分区分开存放（同上：字段集合不同，套错预设等于把条体撑成方块）。
        PresetKind.Dock => "Docks",
        _ => "Zones"
    };
}