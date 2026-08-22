using System;

namespace DesktopZones.Models;

/// <summary>
/// Style overrides applied when a Zone is rendered as part of a merged group
/// (master in unified mode, or sub-zone standalone in unified mode).
/// Lifted out of <see cref="Zone"/> to flatten the god class — see bug report
/// item #14. Mirrors the override-relevant fields of <see cref="AppearanceModel"/>
/// plus the title-bar/quickbar toggles that are merged-group-specific.
///
/// Defaults match the historical values Zone declared before the refactor so
/// freshly-loaded presets read identically.
/// </summary>
public class MergedGroupStyle
{
    public string BorderColor { get; set; } = "#40FFFFFF";
    public double BorderThickness { get; set; } = 1.5;
    public int CornerRadius { get; set; } = 8;
    public string FillColor { get; set; } = "#08000000";
    public string TitleBarFillColor { get; set; } = "#10FFFFFF";
    public string TitleTextColor { get; set; } = "#A0FFFFFF";
    public string IconColor { get; set; } = "";          // emoji tint color
    public double ControlOpacity { get; set; } = 40;
    public double TitleBarOpacity { get; set; } = 6;
    public bool UseUnifiedFill { get; set; } = true;     // true=unified fill, false=keep original
    public bool QuickBarMode { get; set; } = false;      // title-bar-less compact mode
    public bool TitleBarTextColorAdaptive { get; set; } = true;
    public string BackgroundImagePath { get; set; } = "";
    public string BgImageStretch { get; set; } = "UniformToFill";
    public double BgImageOffsetX { get; set; } = 0;
    public double BgImageOffsetY { get; set; } = 0;
    public double BgImageZoom { get; set; } = 1.0;
    public double BackgroundImageOpacity { get; set; } = 40;
}