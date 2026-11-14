using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DesktopZones.Models;

/// <summary>坞吸附的屏幕边。</summary>
public static class ZoneBayEdges
{
    public const string Top = "Top";
    public const string Bottom = "Bottom";
    public const string Left = "Left";
    public const string Right = "Right";

    public static string Normalize(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "top" => Top,
        "left" => Left,
        "right" => Right,
        _ => Bottom
    };

    public static bool IsVertical(string? edge) =>
        Normalize(edge) is Left or Right;

    public static string I18nKey(string? edge) => Normalize(edge) switch
    {
        Top => "ZoneBay.Edge.Top",
        Left => "ZoneBay.Edge.Left",
        Right => "ZoneBay.Edge.Right",
        _ => "ZoneBay.Edge.Bottom"
    };
}

public static class ZoneBayExpandModes
{
    public const string Strip = "Strip";
    public const string Original = "Original";

    public static string Normalize(string? raw) =>
        string.Equals(raw?.Trim(), Original, StringComparison.OrdinalIgnoreCase) ? Original : Strip;
}

/// <summary>吸收来源：只有被最小化的分区恢复按钮可以进坞。</summary>
public enum ZoneBaySourceKind
{
    ZoneRestore,
    ZoneBayRestore
}

/// <summary>
/// 一条分区坞（持久化到 <c>AppConfig.PluginZoneBars</c>）。
/// 坞是“分区引用的容器”，不是分区本身；分区几何、外观仍归 <see cref="Zone"/>。
/// </summary>
public sealed class PluginZoneBar
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string PluginId { get; set; } = "";
    public string BarTypeId { get; set; } = "";
    public string Name { get; set; } = "";

    // ── 行为 ──
    public string Edge { get; set; } = ZoneBayEdges.Bottom;
    public bool AutoHide { get; set; }
    public bool ExpandOnHover { get; set; } = true;
    public string ExpandMode { get; set; } = ZoneBayExpandModes.Strip;
    public bool EnableRestoreButton { get; set; } = true;

    // ── 外观（与宿主 AppearanceModel 同一套语义） ──
    /// <summary>背板填充（#AARRGGBB 或 #RRGGBB）。默认 = 原生分区/快捷栏的 #08000000。</summary>
    public string FillColor { get; set; } = "#08000000";
    public double FillOpacity { get; set; } = 1.0;
    /// <summary>芯片（按钮）填充，不作用背板/图标。默认透明 —— 原生快捷栏只有图标。</summary>
    public string ChipFillColor { get; set; } = "#00FFFFFF";
    public double ChipOpacity { get; set; } = 1.0;
    /// <summary>芯片图标覆盖色；空 = 跟随原分区图标色。</summary>
    public string IconColor { get; set; } = "";
    public string BorderColor { get; set; } = "#38FFFFFF";
    public double BorderThickness { get; set; } = 1.0;
    public int CornerRadius { get; set; } = 12;
    /// <summary>与分区/快捷栏一致：新坞默认开液态玻璃。</summary>
    public bool EnableLiquidGlass { get; set; } = true;
    public int GlassBlurAmount { get; set; } = 18;
    public int GlassTintOpacity { get; set; } = 50;
    public int GlassTintLuminosity { get; set; } = 100;
    public string GlassColorMode { get; set; } = "Default";
    public string GlassMaterial { get; set; } = "";
    /// <summary>外观迁移版本：P0 默认值 → 原生观感只迁一次，用户改过的不覆盖。</summary>
    public int AppearanceVersion { get; set; }

    // ── 状态 ──
    public bool IsVisible { get; set; } = true;
    public bool IsCollapsed { get; set; }
    /// <summary>芯片顺序；第一个在前。</summary>
    public List<Guid> ZoneIds { get; set; } = new();
    public int SortOrder { get; set; }
    /// <summary>插件私有偏好（P1 起用）。</summary>
    public string SettingsJson { get; set; } = "{}";

    /// <summary>坞贴在哪块显示器上（0 = 主显示器，按 <c>MonitorHelper</c> 的「主屏优先」顺序）。
    /// -1 / 越界 = 自动跟随主显示器。多屏且每屏 DPI 不同时，坞的边长、自动隐藏、展开定位
    /// 全部按这块显示器的工作区算，而不是 <c>SystemParameters.WorkArea</c>。</summary>
    public int MonitorIndex { get; set; } = -1;

    [JsonIgnore] public bool IsVertical => ZoneBayEdges.IsVertical(Edge);
    [JsonIgnore] public string NormalizedEdge => ZoneBayEdges.Normalize(Edge);
    [JsonIgnore] public string NormalizedExpandMode => ZoneBayExpandModes.Normalize(ExpandMode);

    /// <summary>深拷贝（属性面板的「应用/取消」快照、文件落盘前隔离用）。</summary>
    public PluginZoneBar Clone()
    {
        var copy = (PluginZoneBar)MemberwiseClone();
        copy.ZoneIds = new List<Guid>(ZoneIds);
        copy.SettingsJson = SettingsJson;
        return copy;
    }

    /// <summary>把 <paramref name="src"/> 的字段拷回本实例（取消时还原快照）。
    /// 不换 <see cref="Id"/> —— 面板/坞窗口都按这个 Id 记账。</summary>
    public void CopyFrom(PluginZoneBar src)
    {
        PluginId = src.PluginId;
        BarTypeId = src.BarTypeId;
        Name = src.Name;
        Edge = src.Edge;
        AutoHide = src.AutoHide;
        ExpandOnHover = src.ExpandOnHover;
        ExpandMode = src.ExpandMode;
        EnableRestoreButton = src.EnableRestoreButton;
        FillColor = src.FillColor;
        FillOpacity = src.FillOpacity;
        ChipFillColor = src.ChipFillColor;
        ChipOpacity = src.ChipOpacity;
        IconColor = src.IconColor;
        BorderColor = src.BorderColor;
        BorderThickness = src.BorderThickness;
        CornerRadius = src.CornerRadius;
        EnableLiquidGlass = src.EnableLiquidGlass;
        GlassBlurAmount = src.GlassBlurAmount;
        GlassTintOpacity = src.GlassTintOpacity;
        GlassTintLuminosity = src.GlassTintLuminosity;
        GlassColorMode = src.GlassColorMode;
        GlassMaterial = src.GlassMaterial;
        AppearanceVersion = src.AppearanceVersion;
        IsVisible = src.IsVisible;
        IsCollapsed = src.IsCollapsed;
        ZoneIds = new List<Guid>(src.ZoneIds);
        SortOrder = src.SortOrder;
        SettingsJson = src.SettingsJson;
        MonitorIndex = src.MonitorIndex;
    }
}
