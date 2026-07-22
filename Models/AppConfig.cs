using System.Collections.Generic;

namespace DesktopZones.Models;

public class AppConfig
{
    private List<Zone> _zones = new();
    public List<Zone> Zones
    {
        get => _zones;
        set => _zones = value ?? new();
    }
    public bool StartWithWindows { get; set; } = false;
    public bool StartMinimized { get; set; } = true;
    public bool ShowAllOnStartup { get; set; } = true;
    public string Theme { get; set; } = "default";

    // ── Language ──
    public string Language { get; set; } = "zh"; // "zh" / "en"

    // ── Global appearance (all zones share these by default) ──
    public string GlobalBorderColor { get; set; } = "#40FFFFFF";
    public string GlobalFillColor { get; set; } = "#08000000";
    public double GlobalBorderThickness { get; set; } = 1.5;
    public bool UseGlobalAppearance { get; set; } = true;
    // ── Liquid Glass (ZenDesktop-style) ──
    public bool EnableLiquidGlass { get; set; } = true;
    public int GlassBlurAmount { get; set; } = 18;       // 0-60, default 18 = ZenDesktop standard
    public int GlassTintOpacity { get; set; } = 50;       // 0-100%
    public int GlassTintLuminosity { get; set; } = 100;   // 0-150%
    public string GlassColorMode { get; set; } = "Default"; // color preset name

    // ── Panel ──
    public bool PanelUseGlobalAppearance { get; set; } = true;
    public bool PanelEnabled { get; set; } = false;
    public double PanelX { get; set; }
    public double PanelY { get; set; }
    public double PanelWidth { get; set; } = 340;
    public double PanelHeight { get; set; } = 500;
    // ── Panel Background Image ──
    public string PanelBackgroundImagePath { get; set; } = "";
    public string PanelBgImageStretch { get; set; } = "UniformToFill";
    public double PanelBackgroundImageOpacity { get; set; } = 30;
    public double PanelBgImageZoom { get; set; } = 1.0;
    public double PanelBgImageOffsetX { get; set; } = 0;
    public double PanelBgImageOffsetY { get; set; } = 0;

    // ── Panel Hotkey ──
    public bool PanelHotkeyEnabled { get; set; } = false;
    public int PanelHotkeyModifiers { get; set; } = 0x0008; // MOD_WIN
    public int PanelHotkeyKey { get; set; } = 0x50; // 'P'
    public List<CustomHotkey> PanelCustomHotkeys { get; set; } = new();

    // ── Widgets ──
    private List<StickyNote> _notes = new();
    public List<StickyNote> Notes
    {
        get => _notes;
        set => _notes = value ?? new();
    }
    private List<DesktopClock> _clocks = new();
    public List<DesktopClock> Clocks
    {
        get => _clocks;
        set => _clocks = value ?? new();
    }
    private List<DesktopCalendar> _calendars = new();
    public List<DesktopCalendar> Calendars
    {
        get => _calendars;
        set => _calendars = value ?? new();
    }
}

public class CustomHotkey
{
    public int Modifiers { get; set; }
    public int Key { get; set; }
}
