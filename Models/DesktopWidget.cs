using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DesktopZones.Models;

// ── Sticky Note ──

public class StickyNote
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "Note";
    public string Content { get; set; } = "";
    public double X { get; set; } = 300;
    public double Y { get; set; } = 200;
    public double Width { get; set; } = 260;
    public double Height { get; set; } = 200;
    public string NoteColor { get; set; } = "#30FFF9C4"; // yellow default (legacy, superseded by FillColor)
    public double FontSize { get; set; } = 14;
    public bool IsVisible { get; set; } = true;
    public bool PinnedTop { get; set; } = false;
    // ── Appearance (mirrors Zone appearance system) ──
    public bool EnableAcrylic { get; set; } = true;
    public string BorderColor { get; set; } = "#40FFFFFF";
    public string FillColor { get; set; } = "#08000000";
    public double BorderThickness { get; set; } = 1.0;
    public int GlassBlurAmount { get; set; } = 18;
    public int GlassTintOpacity { get; set; } = 50;
    public int GlassTintLuminosity { get; set; } = 100;
    public string GlassColorMode { get; set; } = "Default";
    public bool EnableLiquidGlass { get; set; } = false;
    public bool UseGlobalAppearance { get; set; } = true;
    // ── Title bar / button appearance ──
    public string TitleBarFillColor { get; set; } = "#10FFFFFF";
    public double TitleBarOpacity { get; set; } = 6;
    public double ControlOpacity { get; set; } = 40;
    public string TitleTextColor { get; set; } = "#E0E0E0";
    // ── Background image ──
    public string BackgroundImagePath { get; set; } = "";
    public string BgImageStretch { get; set; } = "UniformToFill";
    public double BackgroundImageOpacity { get; set; } = 30;
    public double BgImageZoom { get; set; } = 1.0;
    public double BgImageOffsetX { get; set; } = 0;
    public double BgImageOffsetY { get; set; } = 0;
    public bool EnableRestoreButton { get; set; } = true;
    // ── Save ──
    public string LastSavePath { get; set; } = "";
    // ── Hotkey ──
    public bool HotkeyEnabled { get; set; } = false;
    public int HotkeyModifiers { get; set; } = 1; // MOD_ALT = 0x0001
    public int HotkeyKey { get; set; } = 0x4E; // 'N'
    public List<CustomHotkey> CustomHotkeys { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime ModifiedAt { get; set; } = DateTime.Now;

    public StickyNote Clone() => new()
    {
        Id = Id, Title = Title, Content = Content,
        X = X, Y = Y, Width = Width, Height = Height,
        NoteColor = NoteColor, FontSize = FontSize,
        IsVisible = IsVisible, PinnedTop = PinnedTop,
        EnableAcrylic = EnableAcrylic, BorderColor = BorderColor,
        FillColor = FillColor, BorderThickness = BorderThickness,
        GlassBlurAmount = GlassBlurAmount, GlassTintOpacity = GlassTintOpacity,
        GlassTintLuminosity = GlassTintLuminosity, GlassColorMode = GlassColorMode,
        EnableLiquidGlass = EnableLiquidGlass, UseGlobalAppearance = UseGlobalAppearance,
        TitleBarFillColor = TitleBarFillColor, TitleBarOpacity = TitleBarOpacity,
        ControlOpacity = ControlOpacity, TitleTextColor = TitleTextColor,
        BackgroundImagePath = BackgroundImagePath, BgImageStretch = BgImageStretch,
        BackgroundImageOpacity = BackgroundImageOpacity, BgImageZoom = BgImageZoom,
        BgImageOffsetX = BgImageOffsetX, BgImageOffsetY = BgImageOffsetY,
        EnableRestoreButton = EnableRestoreButton, LastSavePath = LastSavePath,
        HotkeyEnabled = HotkeyEnabled, HotkeyModifiers = HotkeyModifiers,
        HotkeyKey = HotkeyKey, CustomHotkeys = new List<CustomHotkey>(CustomHotkeys),
        CreatedAt = CreatedAt, ModifiedAt = ModifiedAt
    };
}

// ── Desktop Clock ──

public enum ClockDisplayMode { Digital, Analog }

public class DesktopClock
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public double X { get; set; } = 300;
    public double Y { get; set; } = 100;
    public double Width { get; set; } = 320;
    public double Height { get; set; } = 140;
    public bool IsVisible { get; set; } = true;
    public bool ShowSeconds { get; set; } = true;
    public bool ShowDate { get; set; } = true;
    public bool Use24Hour { get; set; } = true;
    public string TextColor { get; set; } = "#EEFFFFFF";
    public double FontSize { get; set; } = 48;
    public string FontFamily { get; set; } = "Segoe UI";
    public double Opacity { get; set; } = 1.0;
    public ClockDisplayMode Mode { get; set; } = ClockDisplayMode.Digital;
    public string AccentColor { get; set; } = "#FFFFFFFF";
    // ── Appearance ──
    public bool EnableAcrylic { get; set; } = true;
    public string BorderColor { get; set; } = "#40FFFFFF";
    public string FillColor { get; set; } = "#08000000";
    public double BorderThickness { get; set; } = 1.0;
    public int GlassBlurAmount { get; set; } = 18;
    public int GlassTintOpacity { get; set; } = 50;
    public int GlassTintLuminosity { get; set; } = 100;
    public string GlassColorMode { get; set; } = "Default";
    public bool EnableLiquidGlass { get; set; } = false;
    public bool UseGlobalAppearance { get; set; } = true;
    // ── Mode-independent fill ──
    public string AnalogFillColor { get; set; } = "#08000000";
    public string DigitalFillColor { get; set; } = "#08000000";
    // ── Analog clock background image ──
    public string BackgroundImagePath { get; set; } = "";
    public string BgImageStretch { get; set; } = "UniformToFill";
    public double BackgroundImageOpacity { get; set; } = 30;
    public double BgImageZoom { get; set; } = 1.0;
    public double BgImageOffsetX { get; set; } = 0;
    public double BgImageOffsetY { get; set; } = 0;
    public bool EnableRestoreButton { get; set; } = true;
    // ── Digital clock background image ──
    public string DigitalBackgroundImagePath { get; set; } = "";
    public string DigitalBgImageStretch { get; set; } = "UniformToFill";
    public double DigitalBackgroundImageOpacity { get; set; } = 30;
    public double DigitalBgImageZoom { get; set; } = 1.0;
    public double DigitalBgImageOffsetX { get; set; } = 0;
    public double DigitalBgImageOffsetY { get; set; } = 0;

    public DesktopClock Clone() => new()
    {
        Id = Id, X = X, Y = Y, Width = Width, Height = Height, IsVisible = IsVisible,
        ShowSeconds = ShowSeconds, ShowDate = ShowDate,
        Use24Hour = Use24Hour, TextColor = TextColor,
        FontSize = FontSize, FontFamily = FontFamily,
        Opacity = Opacity, Mode = Mode, AccentColor = AccentColor,
        EnableAcrylic = EnableAcrylic, BorderColor = BorderColor,
        FillColor = FillColor, BorderThickness = BorderThickness,
        GlassBlurAmount = GlassBlurAmount, GlassTintOpacity = GlassTintOpacity,
        GlassTintLuminosity = GlassTintLuminosity, GlassColorMode = GlassColorMode,
        EnableLiquidGlass = EnableLiquidGlass, UseGlobalAppearance = UseGlobalAppearance,
        AnalogFillColor = AnalogFillColor, DigitalFillColor = DigitalFillColor,
        BackgroundImagePath = BackgroundImagePath,
        BgImageStretch = BgImageStretch,
        BackgroundImageOpacity = BackgroundImageOpacity,
        BgImageZoom = BgImageZoom,
        BgImageOffsetX = BgImageOffsetX,
        BgImageOffsetY = BgImageOffsetY,
        EnableRestoreButton = EnableRestoreButton,
        DigitalBackgroundImagePath = DigitalBackgroundImagePath,
        DigitalBgImageStretch = DigitalBgImageStretch,
        DigitalBackgroundImageOpacity = DigitalBackgroundImageOpacity,
        DigitalBgImageZoom = DigitalBgImageZoom,
        DigitalBgImageOffsetX = DigitalBgImageOffsetX,
        DigitalBgImageOffsetY = DigitalBgImageOffsetY
    };
}

// ── Desktop Calendar ──

public enum NotePriority { None, Low, Normal, High }

public class CalendarNote
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Date { get; set; } = "";       // "2026-06-10"
    public string Content { get; set; } = "";
    public NotePriority Priority { get; set; } = NotePriority.None;
    public bool IsCompleted { get; set; } = false;
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    // ── Reminder ──
    public bool ReminderEnabled { get; set; } = false;
    public DateTime? ReminderTime { get; set; } = null;
    public bool ReminderFired { get; set; } = false;

    public CalendarNote Clone() => new()
    {
        Id = Id, Date = Date, Content = Content,
        Priority = Priority, IsCompleted = IsCompleted,
        CreatedAt = CreatedAt,
        ReminderEnabled = ReminderEnabled, ReminderTime = ReminderTime,
        ReminderFired = ReminderFired
    };
}

public class DesktopCalendar
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public double X { get; set; } = 400;
    public double Y { get; set; } = 100;
    public double Width { get; set; } = 320;
    public double Height { get; set; } = 440;
    public bool IsVisible { get; set; } = true;
    public bool ShowWeekNumbers { get; set; } = false;
    public bool StartOnMonday { get; set; } = true;
    public string TextColor { get; set; } = "#EEFFFFFF";
    public string TodayColor { get; set; } = "#FF6C63FF";
    public double FontSize { get; set; } = 14;
    public double Opacity { get; set; } = 1.0;
    // ── Appearance ──
    public bool EnableAcrylic { get; set; } = true;
    public string BorderColor { get; set; } = "#40FFFFFF";
    public string FillColor { get; set; } = "#08000000";
    public double BorderThickness { get; set; } = 1.0;
    public int GlassBlurAmount { get; set; } = 18;
    public int GlassTintOpacity { get; set; } = 50;
    public int GlassTintLuminosity { get; set; } = 100;
    public string GlassColorMode { get; set; } = "Default";
    public bool EnableLiquidGlass { get; set; } = false;
    public bool UseGlobalAppearance { get; set; } = true;
    // ── Background image ──
    public string BackgroundImagePath { get; set; } = "";
    public string BgImageStretch { get; set; } = "UniformToFill";
    public double BackgroundImageOpacity { get; set; } = 30;
    public double BgImageZoom { get; set; } = 1.0;
    public double BgImageOffsetX { get; set; } = 0;
    public double BgImageOffsetY { get; set; } = 0;
    public bool EnableRestoreButton { get; set; } = true;
    // Notes keyed by "yyyy-MM-dd"
    private Dictionary<string, List<CalendarNote>> _notes = new();
    public Dictionary<string, List<CalendarNote>> Notes
    {
        get => _notes;
        set => _notes = value ?? new();
    }

    [JsonIgnore]
    public int DisplayYear { get; set; } = DateTime.Now.Year;
    [JsonIgnore]
    public int DisplayMonth { get; set; } = DateTime.Now.Month;

    public DesktopCalendar Clone() => new()
    {
        Id = Id, X = X, Y = Y, Width = Width, Height = Height, IsVisible = IsVisible,
        ShowWeekNumbers = ShowWeekNumbers, StartOnMonday = StartOnMonday,
        TextColor = TextColor, TodayColor = TodayColor,
        FontSize = FontSize, Opacity = Opacity,
        EnableAcrylic = EnableAcrylic, BorderColor = BorderColor,
        FillColor = FillColor, BorderThickness = BorderThickness,
        GlassBlurAmount = GlassBlurAmount, GlassTintOpacity = GlassTintOpacity,
        GlassTintLuminosity = GlassTintLuminosity, GlassColorMode = GlassColorMode,
        EnableLiquidGlass = EnableLiquidGlass, UseGlobalAppearance = UseGlobalAppearance,
        BackgroundImagePath = BackgroundImagePath, BgImageStretch = BgImageStretch,
        BackgroundImageOpacity = BackgroundImageOpacity, BgImageZoom = BgImageZoom,
        BgImageOffsetX = BgImageOffsetX, BgImageOffsetY = BgImageOffsetY,
        EnableRestoreButton = EnableRestoreButton,
        Notes = Notes.ToDictionary(kvp => kvp.Key, kvp => new List<CalendarNote>(kvp.Value.Select(n => n.Clone())))
    };
}
