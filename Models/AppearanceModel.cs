using System;

namespace DesktopZones.Models;

/// <summary>
/// Shared visual appearance fields for Zone / StickyNote / DesktopClock /
/// DesktopCalendar. Inherited (NOT composed) so the JSON shape stays flat
/// and existing preset files keep loading without migration.
///
/// Field defaults match the historical values each model declared before
/// the refactor, so a freshly-loaded preset reads identically.
///
/// Excluded fields (kept per-model because their defaults diverge):
/// - BorderThickness (Zone=1.5, others=1.0)
/// - BackgroundImageOpacity (Zone=40, others=30)
/// - UseGlobalAppearance (Zone has none, others have one)
///
/// Excluded fields (model-specific):
/// - TitleBarFillColor / TitleBarOpacity / ControlOpacity / TitleTextColor
///   (only StickyNote)
/// - AnalogFillColor / DigitalFillColor / DigitalBackgroundImage*
///   (only DesktopClock)
/// </summary>
public abstract class AppearanceModel
{
    public bool EnableAcrylic { get; set; } = true;
    public string BorderColor { get; set; } = "#40FFFFFF";
    public string FillColor { get; set; } = "#08000000";
    public int GlassBlurAmount { get; set; } = 18;
    public int GlassTintOpacity { get; set; } = 50;
    public int GlassTintLuminosity { get; set; } = 100;
    public string GlassColorMode { get; set; } = "Default";
    public bool EnableLiquidGlass { get; set; } = false;
    public string BackgroundImagePath { get; set; } = "";
    public string BgImageStretch { get; set; } = "UniformToFill";
    public double BgImageZoom { get; set; } = 1.0;
    public double BgImageOffsetX { get; set; } = 0;
    public double BgImageOffsetY { get; set; } = 0;
    public bool EnableRestoreButton { get; set; } = true;

    // ── Text color adaptive ──
    /// <summary>
    /// Auto-pick text/icon foreground color based on the widget's effective fill color.
    /// True = adaptive (overrides user-set TextColor); false = use configured TextColor.
    /// Default true: existing widgets enable this on first deserialize (C# field init fills
    /// missing JSON keys). Title bar elements use a separate flag on the subclass.
    /// </summary>
    public bool TextColorAdaptive { get; set; } = true;
}