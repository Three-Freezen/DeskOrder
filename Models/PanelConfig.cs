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
    public bool PanelEnabled { get; set; } = false;
    public double PanelX { get; set; }
    public double PanelY { get; set; }
    public double PanelWidth { get; set; } = 340;
    public double PanelHeight { get; set; } = 500;
    public string PanelTitleBarFillColor { get; set; } = "#10FFFFFF";
    public string PanelFillColor { get; set; } = "#08000000";
    /// <summary>标题栏内容颜色（替代原 PanelTitleBarTextColorAdaptive）— 面板顶栏文本/时钟/日期/搜索。</summary>
    public string PanelButtonColor { get; set; } = "#E8E8F0";
    /// <summary>主体内容颜色（替代原 PanelTextColorAdaptive）— 面板分区卡片名称。</summary>
    public string PanelTextColor { get; set; } = "#FFFFFF";
    public bool PanelTitleBarFillIndependent { get; set; } = false;
    public string PanelBorderColor { get; set; } = "#40FFFFFF";
    // ponytail 2026-08-25: per-panel border thickness. Previously hardcoded 1.5 in
    // PanelWindow.ApplyStyle (the old global-appearance default). Own field now so
    // the 面板设置 editor can drive it like every other component.
    public double PanelBorderThickness { get; set; } = 1.5;
    // ponytail 2026-08-26: per-panel corner radius (圆角/尖角 switch). Default 10
    // matches the PanelWindow XAML hardcoded radius.
    public int PanelCornerRadius { get; set; } = 10;
    public double PanelControlOpacity { get; set; } = 40;
    // ── Liquid glass (per-panel; migrated from AppConfig-level globals) ──
    public bool PanelEnableLiquidGlass { get; set; } = true;
    public int PanelGlassBlurAmount { get; set; } = 18;
    public int PanelGlassTintOpacity { get; set; } = 50;
    public int PanelGlassTintLuminosity { get; set; } = 100;
    public string PanelGlassColorMode { get; set; } = "Default";
    /// <summary>One-shot migration flag: legacy configs kept liquid-glass on AppConfig;
    /// on first load those values are copied into the Panel POCO and this flag is set.</summary>
    public bool PanelGlassMigrated { get; set; } = false;
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
