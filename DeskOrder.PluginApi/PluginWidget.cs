using System;

namespace DeskOrder.PluginApi;

/// <summary>
/// 一个插件组件实例（持久化到 <c>AppConfig.PluginWidgets</c>）。
/// 窗口、位置、外观由宿主负责；插件只通过 <see cref="PluginWidgetContext.Settings"/> 读写自己的设置。
///
/// 说明：本类型刻意不继承宿主的 <c>AppearanceModel</c> —— SDK 不能引用主工程。
/// 因此把窗口真正用到的少量外观字段平铺在这里；新增字段必须保持 JSON 向后兼容。
/// </summary>
public class PluginWidget
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>所属插件 id（manifest.id）。</summary>
    public string PluginId { get; set; } = "";

    /// <summary>插件内组件类型 id（descriptor.Id）。</summary>
    public string WidgetTypeId { get; set; } = "";

    public double X { get; set; } = 300;
    public double Y { get; set; } = 160;
    public double Width { get; set; } = 320;
    public double Height { get; set; } = 180;

    public bool IsVisible { get; set; } = true;
    public bool IsLocked { get; set; }

    /// <summary>磁贴模式（去掉窗口内边距 / 标题装饰，由插件自行解释）。</summary>
    public bool TileMode { get; set; }

    /// <summary>插件私有设置（JSON 字符串）。</summary>
    public string SettingsJson { get; set; } = "{}";

    public int SortOrder { get; set; }

    // ── 外观（宿主窗口使用） ──
    public double Opacity { get; set; } = 1.0;
    public string FillColor { get; set; } = "#08000000";
    public string BorderColor { get; set; } = "#40FFFFFF";
    public double BorderThickness { get; set; } = 1.0;
    public int CornerRadius { get; set; } = 12;
    public bool EnableAcrylic { get; set; } = true;
    public bool EnableLiquidGlass { get; set; } = true;
    public bool EnableRestoreButton { get; set; } = true;
    public int GlassBlurAmount { get; set; } = 18;
    public int GlassTintOpacity { get; set; } = 50;
    public int GlassTintLuminosity { get; set; } = 100;
    public string GlassColorMode { get; set; } = "Default";
    public string GlassMaterial { get; set; } = "";

    /// <summary>浅拷贝（属性面板的「取消」快照用）。身份字段与插件私有设置不参与深拷。</summary>
    public PluginWidget Clone() => (PluginWidget)MemberwiseClone();

    /// <summary>
    /// 把另一份快照的**窗口外观/几何**拷回来（属性面板点「取消」时用）。
    /// 不碰 <see cref="Id"/> / <see cref="PluginId"/> / <see cref="WidgetTypeId"/> / <see cref="SettingsJson"/>：
    /// 前三者是身份，第四个是插件自己的数据，面板无权回滚。
    /// </summary>
    public void CopyFrom(PluginWidget other)
    {
        if (other == null) return;
        X = other.X;
        Y = other.Y;
        Width = other.Width;
        Height = other.Height;
        IsVisible = other.IsVisible;
        IsLocked = other.IsLocked;
        TileMode = other.TileMode;
        SortOrder = other.SortOrder;
        Opacity = other.Opacity;
        FillColor = other.FillColor;
        BorderColor = other.BorderColor;
        BorderThickness = other.BorderThickness;
        CornerRadius = other.CornerRadius;
        EnableAcrylic = other.EnableAcrylic;
        EnableLiquidGlass = other.EnableLiquidGlass;
        EnableRestoreButton = other.EnableRestoreButton;
        GlassBlurAmount = other.GlassBlurAmount;
        GlassTintOpacity = other.GlassTintOpacity;
        GlassTintLuminosity = other.GlassTintLuminosity;
        GlassColorMode = other.GlassColorMode;
        GlassMaterial = other.GlassMaterial;
    }
}
