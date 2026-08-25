using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopZones.Models;

/// <summary>
/// Style overrides applied when a Zone is rendered as part of a merged group
/// (master in unified mode, or sub-zone standalone in unified mode).
/// Lifted out of <see cref="Zone"/> to flatten the god class — see bug report
/// item #14. Mirrors the override-relevant fields of <see cref="AppearanceModel"/>
/// plus the title-bar/quickbar toggles that are merged-group-specific.
///
/// Defaults match the historical values Zone declared before the refactor so
/// freshly-loaded presets read identically.
/// </summary>
public class MergedGroupStyle
{
    public string BorderColor { get; set; } = "#40FFFFFF";
    public double BorderThickness { get; set; } = 1.5;
    public int CornerRadius { get; set; } = 8;
    public string FillColor { get; set; } = "#08000000";
    public string TitleBarFillColor { get; set; } = "#10FFFFFF";
    public string TitleTextColor { get; set; } = "#A0FFFFFF";
    public string IconColor { get; set; } = "";          // emoji tint color
    public double ControlOpacity { get; set; } = 40;
    public double TitleBarOpacity { get; set; } = 6;
    public bool UseUnifiedFill { get; set; } = true;     // true=unified fill, false=keep original
    /// <summary>磁贴模式 — 隐藏两层标题栏与底部 8px 分割条（组合分区整体形态）。</summary>
    public bool TileMode { get; set; } = false;
    public bool TitleBarTextColorAdaptive { get; set; } = true;
    /// <summary>标题栏填充单独设置 — 统一填充模式下主体填充不铺到标题栏下方。</summary>
    public bool TitleBarFillIndependent { get; set; } = false;
    public string BackgroundImagePath { get; set; } = "";
    public string BgImageStretch { get; set; } = "UniformToFill";
    public double BgImageOffsetX { get; set; } = 0;
    public double BgImageOffsetY { get; set; } = 0;
    public double BgImageZoom { get; set; } = 1.0;
    public double BackgroundImageOpacity { get; set; } = 40;

    // ── Folder mapping (组合分区内容区展示映射文件夹/磁盘的内容) ──
    public bool FolderMappingEnabled { get; set; } = false;
    public string FolderMappingPath { get; set; } = "";

    /// <summary>配置兼容 — 旧 config 的组合分区样式用 "QuickBarMode" 字段名，
    /// 重命名后通过扩展数据回填到 TileMode。</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    [OnDeserialized]
    internal void OnDeserializedAfterRename(StreamingContext _)
    {
        if (ExtensionData == null) return;
        if (ExtensionData.TryGetValue("QuickBarMode", out var old)
            && old.ValueKind is JsonValueKind.True or JsonValueKind.False)
            TileMode = old.GetBoolean();
    }
}