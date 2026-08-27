namespace DesktopZones.Models;

/// <summary>
/// Slim subset of <see cref="AppConfig"/> fields that constitute a Panel
/// preset. Excludes <c>PanelEnabled</c> (global toggle, not style) and
/// PanelHotkey* fields (those belong to the keyboard layer, not visual style).
/// Geometry + appearance + background image only.
/// </summary>
public class PanelPresetConfig
{
    public double PanelX { get; set; }
    public double PanelY { get; set; }
    public double PanelWidth { get; set; } = 340;
    public double PanelHeight { get; set; } = 500;
    public string PanelTitleBarFillColor { get; set; } = "#10FFFFFF";
    public string PanelFillColor { get; set; } = "#08000000";
    public string PanelBorderColor { get; set; } = "#40FFFFFF";
    public double PanelBorderThickness { get; set; } = 1.5;
    public int PanelCornerRadius { get; set; } = 10;
    public bool PanelTitleBarFillIndependent { get; set; } = false;
    public string PanelButtonColor { get; set; } = "#E8E8F0";
    public string PanelTextColor { get; set; } = "#FFFFFF";
    public double PanelControlOpacity { get; set; } = 40;
    public string PanelBackgroundImagePath { get; set; } = "";
    public string PanelBgImageStretch { get; set; } = "UniformToFill";
    public double PanelBackgroundImageOpacity { get; set; } = 30;
    public double PanelBgImageZoom { get; set; } = 1.0;
    public double PanelBgImageOffsetX { get; set; } = 0;
    public double PanelBgImageOffsetY { get; set; } = 0;
    // ── Liquid glass (per-panel POCO fields, mirrors PanelConfig) ──
    public bool EnableLiquidGlass { get; set; } = true;
    public int GlassBlurAmount { get; set; } = 18;
    public int GlassTintOpacity { get; set; } = 50;
    public int GlassTintLuminosity { get; set; } = 100;
    /// <summary>GlassColorMode included in preset so the panel preview card can render the same iridescence.</summary>
    public string GlassColorMode { get; set; } = "Default";

    // ── Hover expand (panel excluded from auto-expand per spec §7.2; speed is the only knob) ──
    public double HoverExpandSpeed { get; set; } = 1.0;
    /// <summary>Panel never has a restore button (spec §7.2 removed it). Default false; migration forces false.</summary>
    public bool EnableRestoreButton { get; set; } = false;
    // ── Panel popup animation (打开/关闭面板的弹出动效,共用 HoverExpandAnimationKind 预设) ──
    public HoverExpandAnimationKind PanelPopupMotion { get; set; } = HoverExpandAnimationKind.ScaleExpand;
    public PanelPopupOrigin PanelPopupOrigin { get; set; } = PanelPopupOrigin.BottomRight;
    public double PanelPopupSpeed { get; set; } = 1.0;

    public PanelPresetConfig Clone() => new()
    {
        PanelX = PanelX,
        PanelY = PanelY,
        PanelWidth = PanelWidth,
        PanelHeight = PanelHeight,
        PanelTitleBarFillColor = PanelTitleBarFillColor,
        PanelFillColor = PanelFillColor,
        PanelBorderColor = PanelBorderColor,
        PanelBorderThickness = PanelBorderThickness,
        PanelCornerRadius = PanelCornerRadius,
        PanelButtonColor = PanelButtonColor,
        PanelTextColor = PanelTextColor,
        PanelTitleBarFillIndependent = PanelTitleBarFillIndependent,
        PanelControlOpacity = PanelControlOpacity,
        PanelBackgroundImagePath = PanelBackgroundImagePath,
        PanelBgImageStretch = PanelBgImageStretch,
        PanelBackgroundImageOpacity = PanelBackgroundImageOpacity,
        PanelBgImageZoom = PanelBgImageZoom,
        PanelBgImageOffsetX = PanelBgImageOffsetX,
        PanelBgImageOffsetY = PanelBgImageOffsetY,
        EnableLiquidGlass = EnableLiquidGlass,
        GlassBlurAmount = GlassBlurAmount,
        GlassTintOpacity = GlassTintOpacity,
        GlassTintLuminosity = GlassTintLuminosity,
        GlassColorMode = GlassColorMode,
        HoverExpandSpeed = HoverExpandSpeed,
        EnableRestoreButton = EnableRestoreButton,
        PanelPopupMotion = PanelPopupMotion,
        PanelPopupOrigin = PanelPopupOrigin,
        PanelPopupSpeed = PanelPopupSpeed
    };

    /// <summary>Snapshot the Panel POCO off an AppConfig instance (ponytail: reads from Panel POCO instead of 19 loose fields).</summary>
    public static PanelPresetConfig FromConfig(AppConfig cfg) => new()
    {
        PanelX = cfg.Panel.PanelX,
        PanelY = cfg.Panel.PanelY,
        PanelWidth = cfg.Panel.PanelWidth,
        PanelHeight = cfg.Panel.PanelHeight,
        PanelTitleBarFillColor = cfg.Panel.PanelTitleBarFillColor,
        PanelFillColor = cfg.Panel.PanelFillColor,
        PanelBorderColor = cfg.Panel.PanelBorderColor,
        PanelBorderThickness = cfg.Panel.PanelBorderThickness,
        PanelCornerRadius = cfg.Panel.PanelCornerRadius,
        PanelButtonColor = cfg.Panel.PanelButtonColor,
        PanelTextColor = cfg.Panel.PanelTextColor,
        PanelTitleBarFillIndependent = cfg.Panel.PanelTitleBarFillIndependent,
        PanelControlOpacity = cfg.Panel.PanelControlOpacity,
        PanelBackgroundImagePath = cfg.Panel.PanelBackgroundImagePath,
        PanelBgImageStretch = cfg.Panel.PanelBgImageStretch,
        PanelBackgroundImageOpacity = cfg.Panel.PanelBackgroundImageOpacity,
        PanelBgImageZoom = cfg.Panel.PanelBgImageZoom,
        PanelBgImageOffsetX = cfg.Panel.PanelBgImageOffsetX,
        PanelBgImageOffsetY = cfg.Panel.PanelBgImageOffsetY,
        EnableLiquidGlass = cfg.Panel.PanelEnableLiquidGlass,
        GlassBlurAmount = cfg.Panel.PanelGlassBlurAmount,
        GlassTintOpacity = cfg.Panel.PanelGlassTintOpacity,
        GlassTintLuminosity = cfg.Panel.PanelGlassTintLuminosity,
        GlassColorMode = cfg.Panel.PanelGlassColorMode,
        HoverExpandSpeed = cfg.Panel.PanelHoverExpandSpeed,
        EnableRestoreButton = false,
        PanelPopupMotion = cfg.Panel.PanelPopupMotion,
        PanelPopupOrigin = cfg.Panel.PanelPopupOrigin,
        PanelPopupSpeed = cfg.Panel.PanelPopupSpeed
    };

    /// <summary>Apply this preset's fields back to a target AppConfig's Panel POCO. Caller is responsible for Save() + repaint.</summary>
    public void ApplyTo(AppConfig cfg)
    {
        cfg.Panel.PanelX = PanelX;
        cfg.Panel.PanelY = PanelY;
        cfg.Panel.PanelWidth = PanelWidth;
        cfg.Panel.PanelHeight = PanelHeight;
        cfg.Panel.PanelTitleBarFillColor = PanelTitleBarFillColor;
        cfg.Panel.PanelFillColor = PanelFillColor;
        cfg.Panel.PanelBorderColor = PanelBorderColor;
        cfg.Panel.PanelBorderThickness = PanelBorderThickness;
        cfg.Panel.PanelCornerRadius = PanelCornerRadius;
        cfg.Panel.PanelButtonColor = PanelButtonColor;
        cfg.Panel.PanelTextColor = PanelTextColor;
        cfg.Panel.PanelTitleBarFillIndependent = PanelTitleBarFillIndependent;
        cfg.Panel.PanelControlOpacity = PanelControlOpacity;
        cfg.Panel.PanelBackgroundImagePath = PanelBackgroundImagePath;
        cfg.Panel.PanelBgImageStretch = PanelBgImageStretch;
        cfg.Panel.PanelBackgroundImageOpacity = PanelBackgroundImageOpacity;
        cfg.Panel.PanelBgImageZoom = PanelBgImageZoom;
        cfg.Panel.PanelBgImageOffsetX = PanelBgImageOffsetX;
        cfg.Panel.PanelBgImageOffsetY = PanelBgImageOffsetY;
        cfg.Panel.PanelEnableLiquidGlass = EnableLiquidGlass;
        cfg.Panel.PanelGlassBlurAmount = GlassBlurAmount;
        cfg.Panel.PanelGlassTintOpacity = GlassTintOpacity;
        cfg.Panel.PanelGlassTintLuminosity = GlassTintLuminosity;
        cfg.Panel.PanelGlassColorMode = GlassColorMode;
        cfg.Panel.PanelHoverExpandSpeed = HoverExpandSpeed;
        cfg.Panel.PanelPopupMotion = PanelPopupMotion;
        cfg.Panel.PanelPopupOrigin = PanelPopupOrigin;
        cfg.Panel.PanelPopupSpeed = PanelPopupSpeed;
    }
}
