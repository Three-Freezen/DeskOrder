using System.Collections.Generic;

namespace DesktopZones.Models;

/// <summary>
/// 外观包里的一个材质（<c>materials/*.json</c>）。映射到宿主
/// <see cref="Helpers.AcrylicHelper.GlassMaterialRecipe"/>：
/// <c>backdrop</c> 决定 DWM 背板 + 是否叠加经典模糊，其余字段直接进着色/颗粒。
/// </summary>
public sealed class AppearanceMaterialDefinition
{
    /// <summary>包内唯一 id（跨包会加 <c>pack:&lt;pluginId&gt;:</c> 前缀）。</summary>
    public string Id { get; set; } = "";

    public LocalizedText Name { get; set; } = new();

    /// <summary><c>acrylic</c>（AccentState 4 + 经典模糊）或 <c>blur</c>（AccentState 3）。</summary>
    public string Backdrop { get; set; } = "acrylic";

    /// <summary>0..60，对应模型的 GlassBlurAmount（与外观对话框滑块上限一致）。</summary>
    public int Blur { get; set; } = 24;

    /// <summary>0..0.3，自绘背板颗粒强度。</summary>
    public double Grain { get; set; }

    /// <summary>0..100，对应 GlassTintOpacity。</summary>
    public int Tint { get; set; } = 40;

    /// <summary>0..150，对应 GlassTintLuminosity（与滑块上限一致）。</summary>
    public int Luminosity { get; set; } = 100;
}

/// <summary>材质文件：<c>{ "materials": [ ... ] }</c>（也接受裸数组）。</summary>
public sealed class AppearanceMaterialFile
{
    public List<AppearanceMaterialDefinition> Materials { get; set; } = new();
}
