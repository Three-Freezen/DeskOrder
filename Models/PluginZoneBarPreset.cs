namespace DesktopZones.Models;

/// <summary>
/// 分区坞（<see cref="PluginZoneBar"/>）外观预设。载荷是 <see cref="PluginZoneBarAppearance"/>
/// 快照，落在 <c>Presets/PluginZoneBars</c>，和分区/快捷栏预设互不混用：
///
/// ① 坞的外观字段集和分区不是同一个集合（芯片填充/芯片透明度/图标覆盖色/吸附边/展开方式）；
/// ② 套用预设只改外观与行为，坞的名称、显隐、折叠态、持有的分区 Id（<c>ZoneIds</c>）、
///    显示器归属和插件私有 SettingsJson 都不动 —— 换外观不能把坞里的分区弄丢；
/// ③ 卡片用「通用插件卡」（填充/圆角/玻璃色块 + 名称 + 类型标签）。
/// </summary>
public class PluginZoneBarPreset : PresetRecord
{
    /// <summary>外观/行为快照（吸附边 + 行为 + 外观 + 玻璃/材质）。</summary>
    public PluginZoneBarAppearance Bar { get; set; } = new();

    public PluginZoneBarPreset() { Kind = PresetKind.PluginZoneBar; }

    public PluginZoneBarPreset Clone() => new()
    {
        Id = Id,
        Name = Name,
        CreatedAt = CreatedAt,
        Kind = Kind,
        Bar = Bar.Clone(),
    };
}

/// <summary>
/// 分区坞的外观/行为快照 —— 预设文件里真正被保存的载荷。
/// 不含 Id/PluginId/BarTypeId/Name/IsVisible/IsCollapsed/ZoneIds/SortOrder/SettingsJson/MonitorIndex。
/// </summary>
public class PluginZoneBarAppearance
{
    // ── 吸附边 / 行为 ──
    public string Edge { get; set; } = ZoneBayEdges.Bottom;
    public bool AutoHide { get; set; }
    public bool ExpandOnHover { get; set; } = true;
    public string ExpandMode { get; set; } = ZoneBayExpandModes.Strip;
    public bool EnableRestoreButton { get; set; } = true;

    // ── 外观 ──
    public string FillColor { get; set; } = "#08000000";
    public double FillOpacity { get; set; } = 1.0;
    public string ChipFillColor { get; set; } = "#00FFFFFF";
    public double ChipOpacity { get; set; } = 1.0;
    public string IconColor { get; set; } = "";
    public string BorderColor { get; set; } = "#38FFFFFF";
    public double BorderThickness { get; set; } = 1.0;
    public int CornerRadius { get; set; } = 12;

    // ── 液态玻璃 / 材质 ──
    public bool EnableLiquidGlass { get; set; } = true;
    public int GlassBlurAmount { get; set; } = 18;
    public int GlassTintOpacity { get; set; } = 50;
    public int GlassTintLuminosity { get; set; } = 100;
    public string GlassColorMode { get; set; } = "Default";
    public string GlassMaterial { get; set; } = "";

    /// <summary>从活的坞实例抓一份快照。</summary>
    public static PluginZoneBarAppearance FromBar(PluginZoneBar bar) => new()
    {
        Edge = bar.Edge,
        AutoHide = bar.AutoHide,
        ExpandOnHover = bar.ExpandOnHover,
        ExpandMode = bar.ExpandMode,
        EnableRestoreButton = bar.EnableRestoreButton,
        FillColor = bar.FillColor,
        FillOpacity = bar.FillOpacity,
        ChipFillColor = bar.ChipFillColor,
        ChipOpacity = bar.ChipOpacity,
        IconColor = bar.IconColor,
        BorderColor = bar.BorderColor,
        BorderThickness = bar.BorderThickness,
        CornerRadius = bar.CornerRadius,
        EnableLiquidGlass = bar.EnableLiquidGlass,
        GlassBlurAmount = bar.GlassBlurAmount,
        GlassTintOpacity = bar.GlassTintOpacity,
        GlassTintLuminosity = bar.GlassTintLuminosity,
        GlassColorMode = bar.GlassColorMode,
        GlassMaterial = bar.GlassMaterial,
    };

    /// <summary>
    /// 套用到目标坞：只写吸附边/行为/外观字段。名称、显隐、折叠态、ZoneIds、
    /// SortOrder、SettingsJson、MonitorIndex 一律不碰。
    /// </summary>
    public void ApplyTo(PluginZoneBar bar)
    {
        bar.Edge = ZoneBayEdges.Normalize(Edge);
        bar.AutoHide = AutoHide;
        bar.ExpandOnHover = ExpandOnHover;
        bar.ExpandMode = ZoneBayExpandModes.Normalize(ExpandMode);
        bar.EnableRestoreButton = EnableRestoreButton;
        bar.FillColor = FillColor;
        bar.FillOpacity = FillOpacity;
        bar.ChipFillColor = ChipFillColor;
        bar.ChipOpacity = ChipOpacity;
        bar.IconColor = IconColor;
        bar.BorderColor = BorderColor;
        bar.BorderThickness = BorderThickness;
        bar.CornerRadius = CornerRadius;
        bar.EnableLiquidGlass = EnableLiquidGlass;
        bar.GlassBlurAmount = GlassBlurAmount;
        bar.GlassTintOpacity = GlassTintOpacity;
        bar.GlassTintLuminosity = GlassTintLuminosity;
        bar.GlassColorMode = GlassColorMode;
        bar.GlassMaterial = GlassMaterial;
    }

    public PluginZoneBarAppearance Clone() => (PluginZoneBarAppearance)MemberwiseClone();
}
