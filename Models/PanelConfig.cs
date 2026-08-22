namespace DesktopZones.Models;

/// <summary>
/// All panel-specific state formerly inlined on <see cref="AppConfig"/>.
/// Extracted into its own POCO so the God class is smaller and Panel-related
/// fields can be round-tripped by reference (see ConfigSaver).
/// Property names keep the legacy "Panel*" prefix to preserve System.Text.Json
/// property paths used by existing config files.
/// </summary>
public class PanelConfig
{
    public bool PanelUseGlobalAppearance { get; set; } = true;
    public bool PanelEnabled { get; set; } = false;
    public double PanelX { get; set; }
    public double PanelY { get; set; }
    public double PanelWidth { get; set; } = 340;
    public double PanelHeight { get; set; } = 500;
    public string PanelTitleBarFillColor { get; set; } = "#10FFFFFF";
    public string PanelFillColor { get; set; } = "#08000000";
    public bool PanelTextColorAdaptive { get; set; } = true;
    public bool PanelTitleBarTextColorAdaptive { get; set; } = true;
    public string PanelBorderColor { get; set; } = "#40FFFFFF";
    public double PanelControlOpacity { get; set; } = 40;
    // ── Panel Background Image ──
    public string PanelBackgroundImagePath { get; set; } = "";
    public string PanelBgImageStretch { get; set; } = "UniformToFill";
    public double PanelBackgroundImageOpacity { get; set; } = 30;
    public double PanelBgImageZoom { get; set; } = 1.0;
    public double PanelBgImageOffsetX { get; set; } = 0;
    public double PanelBgImageOffsetY { get; set; } = 0;
    /// <summary>Spec §7.2: panel only honors hover-expand speed, not auto-expand toggle.</summary>
    public double PanelHoverExpandSpeed { get; set; } = 1.0;
}
