using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DesktopZones.Models;

// ── Sticky Note ──

public class StickyNote : AppearanceModel
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
    public bool IsLocked { get; set; } = false;
    public bool PinnedTop { get; set; } = false;
    // ── Appearance (most fields inherited from AppearanceModel) ──
    public double BorderThickness { get; set; } = 1.0;
    // ponytail 2026-08-26: per-instance corner radius (圆角/尖角 switch).
    // Default 10 matches the XAML hardcoded radius these widgets shipped with.
    public int CornerRadius { get; set; } = 10;
    // ── Title bar / button appearance ──
    public string TitleBarFillColor { get; set; } = "#10FFFFFF";
    public double TitleBarOpacity { get; set; } = 6;
    public double ControlOpacity { get; set; } = 40;
    public string TitleTextColor { get; set; } = "#E0E0E0";
    // ── Background image (Opacity stays per-model — StickyNote uses 30, Zone uses 40) ──
    public double BackgroundImageOpacity { get; set; } = 30;
    // ── Title bar text color adaptive ──
    /// <summary>Auto-pick sticky note title bar text color based on <see cref="TitleBarFillColor"/>.</summary>
    public bool TitleBarTextColorAdaptive { get; set; } = true;
    /// <summary>标题栏填充单独设置 — 勾选后主体填充(FillColor)不再铺到标题栏下方。</summary>
    public bool TitleBarFillIndependent { get; set; } = false;
    // ── Save ──
    public string LastSavePath { get; set; } = "";
    // ── Hotkey ──
    public bool HotkeyEnabled { get; set; } = false;
    public int HotkeyModifiers { get; set; } = 1; // MOD_ALT = 0x0001
    public int HotkeyKey { get; set; } = 0x4E; // 'N'
    public List<CustomHotkey> CustomHotkeys { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime ModifiedAt { get; set; } = DateTime.Now;

    public StickyNote Clone()
    {
        var copy = new StickyNote
        {
            Id = Id, Title = Title, Content = Content,
            X = X, Y = Y, Width = Width, Height = Height,
            NoteColor = NoteColor, FontSize = FontSize,
            IsVisible = IsVisible, IsLocked = IsLocked, PinnedTop = PinnedTop,
            BorderThickness = BorderThickness,
            CornerRadius = CornerRadius,
            TitleBarFillColor = TitleBarFillColor, TitleBarOpacity = TitleBarOpacity,
            ControlOpacity = ControlOpacity, TitleTextColor = TitleTextColor,
            TitleBarTextColorAdaptive = TitleBarTextColorAdaptive,
            TitleBarFillIndependent = TitleBarFillIndependent,
            BackgroundImageOpacity = BackgroundImageOpacity,
            LastSavePath = LastSavePath,
            HotkeyEnabled = HotkeyEnabled, HotkeyModifiers = HotkeyModifiers,
            HotkeyKey = HotkeyKey, CustomHotkeys = new List<CustomHotkey>(CustomHotkeys),
            CreatedAt = CreatedAt, ModifiedAt = ModifiedAt
        };
        Helpers.CloneHelper.CopyBaseProperties<AppearanceModel>(this, copy);
        return copy;
    }
}

// ── Desktop Clock ──

public enum ClockDisplayMode { Digital, Analog }

public class DesktopClock : AppearanceModel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public double X { get; set; } = 300;
    public double Y { get; set; } = 100;
    public double Width { get; set; } = 320;
    public double Height { get; set; } = 140;
    public bool IsVisible { get; set; } = true;
    public bool IsLocked { get; set; } = false;
    public bool ShowSeconds { get; set; } = true;
    public bool ShowDate { get; set; } = true;
    public bool Use24Hour { get; set; } = true;
    /// <summary>极简模式 — hides the minimize + lock buttons in BOTH digital and analog modes.</summary>
    public bool QuickBarMode { get; set; } = false;
    /// <summary>Opacity of the title-bar control buttons (lock / hide), 5-100. Zone-style.</summary>
    public double ControlOpacity { get; set; } = 40;
    public string TextColor { get; set; } = "#EEFFFFFF";
    public double FontSize { get; set; } = 48;
    public string FontFamily { get; set; } = "Segoe UI";
    public double Opacity { get; set; } = 1.0;
    public ClockDisplayMode Mode { get; set; } = ClockDisplayMode.Digital;
    public string AccentColor { get; set; } = "#FFFFFFFF";
    // ── Appearance (most fields inherited from AppearanceModel) ──
    public double BorderThickness { get; set; } = 1.0;
    // ponytail 2026-08-26: per-instance corner radius (圆角/尖角 switch).
    public int CornerRadius { get; set; } = 10;
    // ── Mode-independent fill ──
    public string AnalogFillColor { get; set; } = "#08000000";
    public string DigitalFillColor { get; set; } = "#08000000";
    // ── Background image (Opacity stays per-model — 30 default) ──
    public double BackgroundImageOpacity { get; set; } = 30;
    // ── Digital clock background image ──
    public string DigitalBackgroundImagePath { get; set; } = "";
    public string DigitalBgImageStretch { get; set; } = "UniformToFill";
    public double DigitalBackgroundImageOpacity { get; set; } = 30;
    public double DigitalBgImageZoom { get; set; } = 1.0;
    public double DigitalBgImageOffsetX { get; set; } = 0;
    public double DigitalBgImageOffsetY { get; set; } = 0;

    public DesktopClock Clone()
    {
        var copy = new DesktopClock
        {
            Id = Id, X = X, Y = Y, Width = Width, Height = Height, IsVisible = IsVisible,
            IsLocked = IsLocked,
            ShowSeconds = ShowSeconds, ShowDate = ShowDate,
            Use24Hour = Use24Hour, TextColor = TextColor,
            QuickBarMode = QuickBarMode, ControlOpacity = ControlOpacity,
            FontSize = FontSize, FontFamily = FontFamily,
            Opacity = Opacity, Mode = Mode, AccentColor = AccentColor,
            BorderThickness = BorderThickness,
            CornerRadius = CornerRadius,
            AnalogFillColor = AnalogFillColor, DigitalFillColor = DigitalFillColor,
            BackgroundImageOpacity = BackgroundImageOpacity,
            DigitalBackgroundImagePath = DigitalBackgroundImagePath,
            DigitalBgImageStretch = DigitalBgImageStretch,
            DigitalBackgroundImageOpacity = DigitalBackgroundImageOpacity,
            DigitalBgImageZoom = DigitalBgImageZoom,
            DigitalBgImageOffsetX = DigitalBgImageOffsetX,
            DigitalBgImageOffsetY = DigitalBgImageOffsetY
        };
        Helpers.CloneHelper.CopyBaseProperties<AppearanceModel>(this, copy);
        return copy;
    }
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

public class DesktopCalendar : AppearanceModel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public double X { get; set; } = 400;
    public double Y { get; set; } = 100;
    public double Width { get; set; } = 320;
    public double Height { get; set; } = 440;
    public bool IsVisible { get; set; } = true;
    public bool IsLocked { get; set; } = false;
    public bool ShowWeekNumbers { get; set; } = false;
    public bool StartOnMonday { get; set; } = true;
    /// <summary>极简模式 — hides the minimize + lock buttons.</summary>
    public bool QuickBarMode { get; set; } = false;
    /// <summary>Opacity of the title-bar control buttons (lock / hide), 5-100. Zone-style.</summary>
    public double ControlOpacity { get; set; } = 40;
    public string TextColor { get; set; } = "#EEFFFFFF";
    public string TodayColor { get; set; } = "#FF6C63FF";
    public double FontSize { get; set; } = 14;
    public double Opacity { get; set; } = 1.0;
    // ── Appearance (most fields inherited from AppearanceModel) ──
    public double BorderThickness { get; set; } = 1.0;
    // ponytail 2026-08-26: per-instance corner radius (圆角/尖角 switch).
    public int CornerRadius { get; set; } = 10;
    // ── Background image (Opacity stays per-model — 30 default) ──
    public double BackgroundImageOpacity { get; set; } = 30;
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

    public DesktopCalendar Clone()
    {
        var copy = new DesktopCalendar
        {
            Id = Id, X = X, Y = Y, Width = Width, Height = Height, IsVisible = IsVisible,
            IsLocked = IsLocked,
            ShowWeekNumbers = ShowWeekNumbers, StartOnMonday = StartOnMonday,
            QuickBarMode = QuickBarMode, ControlOpacity = ControlOpacity,
            TextColor = TextColor, TodayColor = TodayColor,
            FontSize = FontSize, Opacity = Opacity,
            BorderThickness = BorderThickness,
            CornerRadius = CornerRadius,
            BackgroundImageOpacity = BackgroundImageOpacity,
            Notes = Notes.ToDictionary(kvp => kvp.Key, kvp => new List<CalendarNote>(kvp.Value.Select(n => n.Clone())))
        };
        Helpers.CloneHelper.CopyBaseProperties<AppearanceModel>(this, copy);
        return copy;
    }
}
