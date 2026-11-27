using System;
using System.Text.Json.Serialization;

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

    /// <summary>插件组件（SDK 的 <c>PluginWidget</c>）外观预设：宽高 + 外观 + 玻璃/材质 + 磁贴。
    /// 落在 <c>Presets/PluginWidgets</c>，卡片是「通用插件卡」（色块 + 名称 + 类型标签）。</summary>
    /// <remarks>
    /// 新增枚举值只能**追加在末尾**：<c>PresetKind</c> 用 System.Text.Json 默认序列化成整数，
    /// 已经落盘的预设文件靠这个数值分派；插在中间会让旧文件的 Kind 错位。
    /// </remarks>
    PluginWidget,

    /// <summary>分区坞（宿主侧 <c>PluginZoneBar</c>）外观预设：吸附边 + 行为 + 外观 + 玻璃/材质。
    /// 落在 <c>Presets/PluginZoneBars</c>，卡片同样走通用插件卡。</summary>
    PluginZoneBar,
}

/// <summary>
/// Common metadata for every preset record (Zone / Clock / Calendar /
/// StickyNote / MergedGroup / Panel / … / PluginWidget / PluginZoneBar). The
/// strongly-typed payload lives on each derived class (<see cref="ZonePreset.Zone"/>,
/// <see cref="ClockPreset.Clock"/>, <see cref="PluginWidgetPreset.Widget"/>, etc.).
/// Each preset JSON file looks like
/// <c>{ Id, Name, CreatedAt, Kind, &lt;PayloadField&gt;: {...} }</c>.
/// </summary>
public class PresetRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New Preset";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public PresetKind Kind { get; set; } = PresetKind.Zone;

    /// <summary>
    /// 外观包导入的预设会带上来源插件 id；卸载/停用外观包时按它清理。
    /// 用户自己保存的预设为 null，不写进 JSON（旧文件也不会多出字段）。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourcePackId { get; set; }

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
        // 插件组件 / 分区坞各存各的：字段来自 SDK 的扁平结构，和任何内置组件都不通用。
        PresetKind.PluginWidget => "PluginWidgets",
        PresetKind.PluginZoneBar => "PluginZoneBars",
        _ => "Zones"
    };
}