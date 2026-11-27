using DeskOrder.PluginApi;

namespace DesktopZones.Models;

/// <summary>
/// 插件组件（<see cref="PluginWidget"/>）外观预设。载荷是 <see cref="PluginWidgetAppearance"/>
/// 快照，单独落在 <c>Presets/PluginWidgets</c>：
///
/// ① 字段集只取「窗口外观/几何」——位置、显隐、锁定、排序、实例 Id、插件私有
///    <c>SettingsJson</c> 都不进预设（套用预设不能把组件挪走、藏起来或清掉插件数据）；
/// ② 和内置组件同一套语义，但字段来自 SDK 的扁平结构，跨类型不通用，所以独立目录 + 独立 Kind；
/// ③ 卡片用「通用插件卡」（填充/圆角/玻璃色块 + 名称 + 类型标签），不套内置组件的缩略图。
/// </summary>
public class PluginWidgetPreset : PresetRecord
{
    /// <summary>外观/几何快照（宽高 + 外观 + 玻璃/材质 + 磁贴）。</summary>
    public PluginWidgetAppearance Widget { get; set; } = new();

    public PluginWidgetPreset() { Kind = PresetKind.PluginWidget; }

    public PluginWidgetPreset Clone() => new()
    {
        Id = Id,
        Name = Name,
        CreatedAt = CreatedAt,
        Kind = Kind,
        Widget = Widget.Clone(),
    };
}

/// <summary>
/// 插件组件的外观快照 —— 预设文件里真正被保存的载荷。
/// 只包含「保存/加载外观预设」应当改写的字段；位置/显隐/锁定/身份/插件数据全部不在里面，
/// 所以套用预设天然不会覆盖这些内容。
/// </summary>
public class PluginWidgetAppearance
{
    // ── 几何（只有宽高；位置不属于预设）──
    public double Width { get; set; } = 320;
    public double Height { get; set; } = 180;

    // ── 外观 ──
    public bool TileMode { get; set; }
    public double Opacity { get; set; } = 1.0;
    public string FillColor { get; set; } = "#08000000";
    public string BorderColor { get; set; } = "#40FFFFFF";
    public double BorderThickness { get; set; } = 1.0;
    public int CornerRadius { get; set; } = 12;
    public bool EnableAcrylic { get; set; } = true;
    public bool EnableLiquidGlass { get; set; } = true;
    public bool EnableRestoreButton { get; set; } = true;

    // ── 液态玻璃 / 材质 ──
    public int GlassBlurAmount { get; set; } = 18;
    public int GlassTintOpacity { get; set; } = 50;
    public int GlassTintLuminosity { get; set; } = 100;
    public string GlassColorMode { get; set; } = "Default";
    public string GlassMaterial { get; set; } = "";

    /// <summary>从活的组件实例抓一份快照（成员都是值类型/字符串，浅拷即够）。</summary>
    public static PluginWidgetAppearance FromWidget(PluginWidget widget) => new()
    {
        Width = widget.Width,
        Height = widget.Height,
        TileMode = widget.TileMode,
        Opacity = widget.Opacity,
        FillColor = widget.FillColor,
        BorderColor = widget.BorderColor,
        BorderThickness = widget.BorderThickness,
        CornerRadius = widget.CornerRadius,
        EnableAcrylic = widget.EnableAcrylic,
        EnableLiquidGlass = widget.EnableLiquidGlass,
        EnableRestoreButton = widget.EnableRestoreButton,
        GlassBlurAmount = widget.GlassBlurAmount,
        GlassTintOpacity = widget.GlassTintOpacity,
        GlassTintLuminosity = widget.GlassTintLuminosity,
        GlassColorMode = widget.GlassColorMode,
        GlassMaterial = widget.GlassMaterial,
    };

    /// <summary>
    /// 套用到目标组件：只写外观/几何字段。身份（Id/PluginId/WidgetTypeId）、位置、
    /// 显隐、锁定、排序和插件私有 SettingsJson 一律不碰。
    /// </summary>
    public void ApplyTo(PluginWidget widget)
    {
        widget.Width = Width;
        widget.Height = Height;
        widget.TileMode = TileMode;
        widget.Opacity = Opacity;
        widget.FillColor = FillColor;
        widget.BorderColor = BorderColor;
        widget.BorderThickness = BorderThickness;
        widget.CornerRadius = CornerRadius;
        widget.EnableAcrylic = EnableAcrylic;
        widget.EnableLiquidGlass = EnableLiquidGlass;
        widget.EnableRestoreButton = EnableRestoreButton;
        widget.GlassBlurAmount = GlassBlurAmount;
        widget.GlassTintOpacity = GlassTintOpacity;
        widget.GlassTintLuminosity = GlassTintLuminosity;
        widget.GlassColorMode = GlassColorMode;
        widget.GlassMaterial = GlassMaterial;
    }

    public PluginWidgetAppearance Clone() => (PluginWidgetAppearance)MemberwiseClone();
}
