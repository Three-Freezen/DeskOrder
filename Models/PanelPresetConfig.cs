namespace DesktopZones.Models;

/// <summary>
/// Slim subset of <see cref="AppConfig"/> fields that constitute a Panel
/// preset. Excludes <c>PanelEnabled</c> (global toggle, not style) and
/// PanelHotkey* fields (those belong to the keyboard layer, not visual style).
/// Geometry + appearance + background image only.
/// </summary>
public class PanelPresetConfig
{
    public bool PanelUseGlobalAppearance { get; set; } = true;
    public double PanelX { get; set; }
    public double PanelY { get; set; }
    public double PanelWidth { get; set; } = 340;
    public double PanelHeight { get; set; } = 500;
    public string PanelTitleBarFillColor { get; set; } = "#10FFFFFF";
    public string PanelFillColor { get; set; } = "#08000000";
    public string PanelBorderColor { get; set; } = "#40FFFFFF"; // matches GlobalBorderColor default
    public double PanelControlOpacity { get; set; } = 40;
    public string PanelBackgroundImagePath { get; set; } = "";
    public string PanelBgImageStretch { get; set; } = "UniformToFill";
    public double PanelBackgroundImageOpacity { get; set; } = 30;
    public double PanelBgImageZoom { get; set; } = 1.0;
    public double PanelBgImageOffsetX { get; set; } = 0;
    public double PanelBgImageOffsetY { get; set; } = 0;
    /// <summary>GlassColorMode inherited from global AppConfig — included in preset so the panel preview card can render the same iridescence.</summary>
    public string GlassColorMode { get; set; } = "Default";

    public PanelPresetConfig Clone() => new()
    {
        PanelUseGlobalAppearance = PanelUseGlobalAppearance,
        PanelX = PanelX,
        PanelY = PanelY,
        PanelWidth = PanelWidth,
        PanelHeight = PanelHeight,
        PanelTitleBarFillColor = PanelTitleBarFillColor,
        PanelFillColor = PanelFillColor,
        PanelBorderColor = PanelBorderColor,
        PanelControlOpacity = PanelControlOpacity,
        PanelBackgroundImagePath = PanelBackgroundImagePath,
        PanelBgImageStretch = PanelBgImageStretch,
        PanelBackgroundImageOpacity = PanelBackgroundImageOpacity,
        PanelBgImageZoom = PanelBgImageZoom,
        PanelBgImageOffsetX = PanelBgImageOffsetX,
        PanelBgImageOffsetY = PanelBgImageOffsetY,
        GlassColorMode = GlassColorMode
    };

    /// <summary>Snapshot the relevant Panel* fields off an AppConfig instance.</summary>
    public static PanelPresetConfig FromConfig(AppConfig cfg) => new()
    {
        PanelUseGlobalAppearance = cfg.PanelUseGlobalAppearance,
        PanelX = cfg.PanelX,
        PanelY = cfg.PanelY,
        PanelWidth = cfg.PanelWidth,
        PanelHeight = cfg.PanelHeight,
        PanelTitleBarFillColor = cfg.PanelTitleBarFillColor,
        PanelFillColor = cfg.PanelFillColor,
        PanelBorderColor = cfg.PanelBorderColor,
        PanelControlOpacity = cfg.PanelControlOpacity,
        PanelBackgroundImagePath = cfg.PanelBackgroundImagePath,
        PanelBgImageStretch = cfg.PanelBgImageStretch,
        PanelBackgroundImageOpacity = cfg.PanelBackgroundImageOpacity,
        PanelBgImageZoom = cfg.PanelBgImageZoom,
        PanelBgImageOffsetX = cfg.PanelBgImageOffsetX,
        PanelBgImageOffsetY = cfg.PanelBgImageOffsetY,
        GlassColorMode = cfg.GlassColorMode
    };

    /// <summary>Apply this preset's fields back to a target AppConfig. Caller is responsible for Save() + repaint.</summary>
    public void ApplyTo(AppConfig cfg)
    {
        cfg.PanelUseGlobalAppearance = PanelUseGlobalAppearance;
        cfg.PanelX = PanelX;
        cfg.PanelY = PanelY;
        cfg.PanelWidth = PanelWidth;
        cfg.PanelHeight = PanelHeight;
        cfg.PanelTitleBarFillColor = PanelTitleBarFillColor;
        cfg.PanelFillColor = PanelFillColor;
        cfg.PanelBorderColor = PanelBorderColor;
        cfg.PanelControlOpacity = PanelControlOpacity;
        cfg.PanelBackgroundImagePath = PanelBackgroundImagePath;
        cfg.PanelBgImageStretch = PanelBgImageStretch;
        cfg.PanelBackgroundImageOpacity = PanelBackgroundImageOpacity;
        cfg.PanelBgImageZoom = PanelBgImageZoom;
        cfg.PanelBgImageOffsetX = PanelBgImageOffsetX;
        cfg.PanelBgImageOffsetY = PanelBgImageOffsetY;
        cfg.GlassColorMode = GlassColorMode;
    }
}