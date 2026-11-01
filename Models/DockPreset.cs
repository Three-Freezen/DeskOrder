using System;

namespace DesktopZones.Models;

/// <summary>
/// 快捷栏（Dock）预设。载荷仍是 <see cref="Zone"/>（快捷栏就是一个
/// <c>DockMode=true</c> 的分区），但单独一个类型 + 单独一个目录
/// （<c>Presets/Docks</c>），原因有三个：
///
/// ① 两边的可调字段不是同一个集合 —— 快捷栏没有标题栏/宽高/网格吸附/自定义图标，
///    多出图标大小、悬停动效、倒影、磁贴（全透明）、单击打开；
/// ② 预设卡要长得不一样（一根长条，见 <c>DockPresetPreview</c>）；
/// ③ 套用时的语义不同：只覆盖外观，名称/图标/条体内的图标一个都不动。
/// </summary>
public class DockPreset : PresetRecord
{
    public Zone Zone { get; set; } = new();

    public DockPreset() { Kind = PresetKind.Dock; }

    public DockPreset Clone() => new()
    {
        Id = Id,
        Name = Name,
        CreatedAt = CreatedAt,
        Kind = Kind,
        Zone = Zone.Clone(),
    };
}
