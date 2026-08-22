using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

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

    // ── Theme selection (three-valued; replaces ambiguous single `Theme`) ──
    /// <summary>"System" / "Light" / "Dark". Defaults to "System" so existing configs fall back to OS preference.</summary>
    public string ThemeMode { get; set; } = "System";

    // Legacy single-value theme field — kept [Obsolete] + [JsonIgnore] so old
    // config files still parse (the value lands in ExtensionData and is dropped
    // silently; ThemeMode is the live field). Do not reference in new code.
    [Obsolete("Use ThemeMode instead")]
    [JsonIgnore]
    public string Theme { get; set; } = "default";

    // ── Language ──
    public string Language { get; set; } = "zh"; // "zh" / "en"

    // ── Global appearance (all zones share these by default) ──
    public string GlobalBorderColor { get; set; } = "#40FFFFFF";
    public string GlobalFillColor { get; set; } = "#08000000";
    public double GlobalBorderThickness { get; set; } = 1.5;
    public bool UseGlobalAppearance { get; set; } = true;
    /// <summary>Spec §7.1 #1: global → per-instance migration completed. Set true after first migration run; never unset.</summary>
    public bool GlobalAppearanceMigrated { get; set; } = false;
    // ── Liquid Glass (ZenDesktop-style) ──
    public bool EnableLiquidGlass { get; set; } = true;
    public int GlassBlurAmount { get; set; } = 18;       // 0-60, default 18 = ZenDesktop standard
    public int GlassTintOpacity { get; set; } = 50;       // 0-100%
    public int GlassTintLuminosity { get; set; } = 100;   // 0-150%
    public string GlassColorMode { get; set; } = "Default"; // color preset name

    // ── Panel (POCOs, was 19 inline fields) ──
    public PanelConfig Panel { get; set; } = new();
    public PanelHotkeyConfig PanelHotkey { get; set; } = new();
    /// <summary>Orphan: user-added extra hotkeys (not the primary toggle).
    /// Stays on AppConfig because it is rarely touched and the primary hotkey
    /// state lives in <see cref="PanelHotkey"/>.</summary>
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

    // ── Property window (per-instance design system, spec §7.1 #6) ──
    public double PropertyWindowX { get; set; } = double.NaN;
    public double PropertyWindowY { get; set; } = double.NaN;
    public double PropertyWindowWidth { get; set; } = 360;
    public double PropertyWindowHeight { get; set; } = 600;
    public bool PropertyWindowTopmost { get; set; } = true;
    public bool PropertyPanelCollapsed { get; set; } = false;

    /// <summary>
    /// Captures top-level JSON fields with no matching property — used by
    /// <c>ConfigService</c> for one-time migration of legacy flat Panel* fields
    /// into the new nested <see cref="Panel"/> POCO. Cleared after migration.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public class CustomHotkey
{
    public int Modifiers { get; set; }
    public int Key { get; set; }
}
