using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DesktopZones.Helpers;

public enum AppThemeMode { System, Light, Dark }

/// <summary>Effective palette in use at runtime. Differs from <see cref="AppThemeMode"/>
/// when user is in System mode: System + Windows HC enabled → ResolvedTheme.HighContrast.
/// </summary>
public enum ResolvedTheme { Light, Dark, HighContrast }

public static class ThemeService
{
    static AppThemeMode _current = AppThemeMode.System;
    static ResolvedTheme _resolved = ResolvedTheme.Dark;
    public static AppThemeMode CurrentMode => _current;
    public static ResolvedTheme ResolvedMode => _resolved;

    // ponytail: last accent color successfully written by ApplySystemAccentIfApplicable.
    // The poll timer ticks once a second; this cache makes the steady-state cost one
    // registry read + one comparison instead of three brush writes + DynamicResource
    // re-resolve on every tick.
    static Color? _lastAppliedAccent;
    static DispatcherTimer? _accentPollTimer;

    public static event Action<AppThemeMode>? Changed;

    /// <summary>
    /// Maps every legacy brush key (Theme.xaml) and modern brush key (Theme.Brushes.xaml)
    /// to the Color.X key whose current value should be written into the brush's Color DP.
    /// WPF does not auto-refresh already-created SolidColorBrush.Color when a MergedDictionaries
    /// entry with the same key is removed — proof: app.Resources[Color.Bg.Base] changes from
    /// #FF1C1C1C to #FFF5F6F8 but Brush.BgBase.Color stays #FF1C1C1C. We bypass that by
    /// explicitly SetValue on each brush's ColorProperty after the dictionary swap.
    /// </summary>
    static readonly Dictionary<string, string> BrushToColorKey = new()
    {
        // Modern Brush.* keys
        ["Brush.Bg.Base"]               = "Color.Bg.Base",
        ["Brush.Bg.Chrome"]             = "Color.Bg.Chrome",
        ["Brush.Bg.Surface"]            = "Color.Bg.Surface",
        ["Brush.Bg.Hover"]              = "Color.Bg.Hover",
        ["Brush.Bg.Active"]             = "Color.Bg.Active",
        ["Brush.Bg.Input"]              = "Color.Bg.Input",
        ["Brush.Border.Subtle"]         = "Color.Border.Subtle",
        ["Brush.Border.Default"]        = "Color.Border.Default",
        ["Brush.Border.Strong"]         = "Color.Border.Strong",
        ["Brush.Text.Primary"]          = "Color.Text.Primary",
        ["Brush.Text.Secondary"]        = "Color.Text.Secondary",
        ["Brush.Text.Tertiary"]         = "Color.Text.Tertiary",
        ["Brush.Text.Disabled"]         = "Color.Text.Disabled",
        ["Brush.Accent"]                = "Color.Accent",
        ["Brush.Accent.Wash"]           = "Color.Accent.Wash",
        ["Brush.Accent.Solid"]          = "Color.Accent.Solid",
        ["Brush.Accent.Solid.Hover"]    = "Color.Accent.Solid.Hover",
        ["Brush.Accent.Solid.Press"]    = "Color.Accent.Solid.Press",
        ["Brush.Accent.On"]             = "Color.Accent.On",
        ["Brush.Accent.2"]              = "Color.Accent.2",
        ["Brush.Success"]               = "Color.Success",
        ["Brush.Warning"]               = "Color.Warning",
        ["Brush.Danger"]                = "Color.Danger",
        ["Brush.Danger.Solid"]          = "Color.Danger.Solid",
        ["Brush.Danger.Wash"]           = "Color.Danger.Wash",
        ["Brush.Close.Hover"]           = "Color.Close.Hover",
        // Legacy Theme.xaml keys
        ["BgApp"]                       = "Color.Bg.Base",
        ["BgSurface"]                   = "Color.Bg.Surface",
        ["BgElevated"]                  = "Color.Bg.Surface",
        ["BgHover"]                     = "Color.Bg.Hover",
        ["BgSelected"]                  = "Color.Bg.Active",
        ["Line"]                        = "Color.Border.Subtle",
        ["LineStrong"]                  = "Color.Border.Default",
        ["BrandCyan"]                   = "Color.Accent",
        ["BrandCyanDeep"]               = "Color.Accent.2",
        ["BrandBlue"]                   = "Color.Accent",
        ["BrandBlueMid"]                = "Color.Accent",
        ["T1"]                          = "Color.Text.Primary",
        ["T2"]                          = "Color.Text.Secondary",
        ["T3"]                          = "Color.Text.Tertiary",
        ["TDisabled"]                   = "Color.Text.Disabled",
        ["IBg"]                         = "Color.Bg.Input",
        ["IBgSolid"]                    = "Color.Bg.Input",
        ["IBd"]                         = "Color.Border.Default",
        ["Success"]                     = "Color.Success",
        ["Warning"]                     = "Color.Warning",
        ["Danger"]                      = "Color.Danger",
        ["DangerSoft"]                  = "Color.Danger",
        ["AccentBrush"]                 = "Color.Accent",
        ["AccentSoft"]                  = "Color.Accent.Wash",
        ["AccentLine"]                  = "Color.Accent",
        ["CardBg"]                      = "Color.Bg.Surface",
        ["CardBorder"]                  = "Color.Border.Subtle",
        ["IconBg"]                      = "Color.Bg.Input",
        ["IconBgSubtle"]                = "Color.Border.Subtle",
        ["IconFg"]                      = "Color.Text.Secondary",
        ["HoverOverlay"]                = "Color.Bg.Hover",
        ["SubTextFg"]                   = "Color.Text.Secondary",
        ["MutedTextFg"]                 = "Color.Text.Tertiary",
        ["DimTextFg"]                   = "Color.Text.Tertiary",
        ["SuccessBrush"]                = "Color.Success",
        ["DangerBrush"]                 = "Color.Danger",
        ["DangerBrushStrong"]           = "Color.Danger",
        ["WarnFg"]                      = "Color.Warning",
        ["InactiveBtnBg"]               = "Color.Bg.Input",
        ["DisabledTextFg"]              = "Color.Text.Disabled",
    };

    /// <summary>
    /// DependencyProperty keys that commonly hold a Brush (or derived). We re-bind these
    /// on every element via SetResourceReference so a brush swap reaches the visual tree
    /// even when the original assignment was a one-shot FindResource (e.g. code-built
    /// controls in PropertyPanel).
    /// </summary>
    static readonly DependencyProperty[] BrushDPs =
    {
        Control.BackgroundProperty,
        Control.ForegroundProperty,
        Control.BorderBrushProperty,
        TextBox.CaretBrushProperty,
        TextBlock.ForegroundProperty,
        Shape.FillProperty,
        Shape.StrokeProperty,
    };

    /// <summary>
    /// For each known brush key, builds the *actual* resource key we should rebind to.
    /// "Brush.*" keys stay as-is; legacy keys (BgApp, T1, …) normalize to their modern
    /// counterpart so code-built elements that captured a legacy key still get the
    /// current color.
    /// </summary>
    static readonly Dictionary<string, string> LegacyToModern = new()
    {
        ["BgApp"]       = "Brush.Bg.Base",
        ["BgSurface"]   = "Brush.Bg.Surface",
        ["BgElevated"]  = "Brush.Bg.Surface",
        ["BgHover"]     = "Brush.Bg.Hover",
        ["BgSelected"]  = "Brush.Bg.Active",
        ["Line"]        = "Brush.Border.Subtle",
        ["LineStrong"]  = "Brush.Border.Default",
        ["BrandCyan"]   = "Brush.Accent",
        ["T1"]          = "Brush.Text.Primary",
        ["T2"]          = "Brush.Text.Secondary",
        ["T3"]          = "Brush.Text.Tertiary",
        ["IBg"]         = "Brush.Bg.Input",
        ["IBgSolid"]    = "Brush.Bg.Input",
        ["IBd"]         = "Brush.Border.Default",
        ["CardBg"]      = "Brush.Bg.Surface",
        ["CardBorder"]  = "Brush.Border.Subtle",
        ["IconBg"]      = "Brush.Bg.Input",
        ["IconBgSubtle"]= "Brush.Border.Subtle",
        ["IconFg"]      = "Brush.Text.Secondary",
        ["HoverOverlay"]= "Brush.Bg.Hover",
        ["SubTextFg"]   = "Brush.Text.Secondary",
        ["MutedTextFg"] = "Brush.Text.Tertiary",
        ["DimTextFg"]   = "Brush.Text.Tertiary",
        ["SuccessBrush"]= "Brush.Success",
        ["DangerBrush"] = "Brush.Danger",
        ["WarnFg"]      = "Brush.Warning",
        ["InactiveBtnBg"]= "Brush.Bg.Input",
        ["DisabledTextFg"]= "Brush.Text.Disabled",
    };

    public static void Apply(AppThemeMode mode)
    {
        _current = mode;
        _resolved = mode == AppThemeMode.System
            ? ResolveSystemState()
            : (mode == AppThemeMode.Light ? ResolvedTheme.Light : ResolvedTheme.Dark);
        SwapColors(_resolved);
        RepaintBrushes();
        RebindOpenWindows();
        Changed?.Invoke(mode);
    }

    static ResolvedTheme ResolveSystemState()
    {
        // 1) High Contrast takes precedence over Light/Dark.
        //    Use SystemParameters.HighContrast (SPI_GETHIGHCONTRAST wrapper) instead
        //    of a registry probe — the documented HC flag lives at
        //    HKCU\Control Panel\Accessibility\HighContrast\Flags as REG_SZ "126"
        //    (not at HKCU\...\Themes\HighContrast\Status as DWORD), so a registry
        //    read is unreliable across machines. SystemParameters also raises
        //    StaticPropertyChanged so StartListeningToSystem gets live toggles
        //    via the existing UserPreferenceChanged handler — no extra wiring.
        if (SystemParameters.HighContrast) return ResolvedTheme.HighContrast;

        // 2) Otherwise read AppsUseLightTheme.
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is 0
                ? ResolvedTheme.Dark
                : ResolvedTheme.Light;
        }
        catch { return ResolvedTheme.Dark; }
    }

    static void SwapColors(ResolvedTheme theme)
    {
        var app = Application.Current;
        if (app == null) return;
        var merged = app.Resources.MergedDictionaries;
        ResourceDictionary? darkDict = null, lightDict = null, hcDict = null;
        foreach (var d in merged)
        {
            if (d.Source?.OriginalString.EndsWith("Theme.Colors.Dark.xaml", StringComparison.OrdinalIgnoreCase) == true) darkDict = d;
            else if (d.Source?.OriginalString.EndsWith("Theme.Colors.Light.xaml", StringComparison.OrdinalIgnoreCase) == true) lightDict = d;
            else if (d.Source?.OriginalString.EndsWith("Theme.Colors.HighContrast.xaml", StringComparison.OrdinalIgnoreCase) == true) hcDict = d;
        }

        ResourceDictionary? keep = theme switch
        {
            ResolvedTheme.HighContrast => hcDict,
            ResolvedTheme.Light        => lightDict,
            _                          => darkDict,
        };

        foreach (var d in new[] { darkDict, lightDict, hcDict })
        {
            if (d != null && d != keep) merged.Remove(d);
        }

        if (keep == null)
        {
            var source = theme == ResolvedTheme.HighContrast
                ? "Resources/Theme.Colors.HighContrast.xaml"
                : theme == ResolvedTheme.Light
                    ? "Resources/Theme.Colors.Light.xaml"
                    : "Resources/Theme.Colors.Dark.xaml";
            merged.Insert(0, new ResourceDictionary { Source = new Uri(source, UriKind.Relative) });
        }
    }

    static Color? TryReadSystemAccent()
    {
        // AccentColor lives at HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Accent\AccentColorMenu
        // on Windows 10/11. Win10 also wrote Personalize\AccentColor but Win11 stopped updating that
        // path, so we read the modern location. DWORD value is COLORREF 0x00BBGGRR:
        //   low byte     = R
        //   bits  8-15   = G
        //   bits 16-23   = B
        //   bits 24-31   = unused (alpha)
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");
            if (key?.GetValue("AccentColorMenu") is int colorref)
            {
                return Color.FromRgb(
                    (byte)( colorref        & 0xFF),
                    (byte)((colorref >>  8) & 0xFF),
                    (byte)((colorref >> 16) & 0xFF));
            }
        }
        catch { }
        return null;
    }

    static bool IsAccentVisible(Color c)
    {
        // Relative luminance per WCAG (sRGB linearized). Skip override below ~5% so
        // black-ish system accents don't hide hover/selection states.
        double Linear(byte v) { var s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        var L = 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);
        return L > 0.05;
    }

    static void ApplySystemAccentIfApplicable()
    {
        if (_current != AppThemeMode.System) return;
        var c = TryReadSystemAccent();
        if (!c.HasValue || !IsAccentVisible(c.Value)) return;
        if (_lastAppliedAccent.HasValue && _lastAppliedAccent.Value == c.Value) return;
        _lastAppliedAccent = c.Value;
        var accentColor = c.Value;
        // ponytail: accent drives the background; contrast color (AdaptiveTextColor's
        // HSL flip) drives text and any brush that needs to stand out against the new
        // bg. Result: the whole palette inverts around the user's accent — light
        // accent → dark text on light bg, dark accent → light text on dark bg.
        var contrastColor = AdaptiveTextColor.ResolveTextColor(accentColor);
        var wash = Color.FromArgb(0x33, accentColor.R, accentColor.G, accentColor.B);
        var brushesDict = Application.Current?.Resources.MergedDictionaries
            .Cast<ResourceDictionary>()
            .FirstOrDefault(d => d.Source?.OriginalString.EndsWith("Theme.Brushes.xaml", StringComparison.OrdinalIgnoreCase) == true);
        if (brushesDict == null) return;
        brushesDict["Brush.Bg.Base"]        = new SolidColorBrush(accentColor);
        brushesDict["Brush.Bg.Surface"]     = new SolidColorBrush(accentColor);
        brushesDict["Brush.Text.Primary"]   = new SolidColorBrush(contrastColor);
        brushesDict["Brush.Text.Secondary"] = new SolidColorBrush(contrastColor);
        brushesDict["Brush.Accent"]         = new SolidColorBrush(contrastColor);
        brushesDict["Brush.Accent.Wash"]    = new SolidColorBrush(wash);
        brushesDict["Brush.Accent.Solid"]   = new SolidColorBrush(contrastColor);
    }

    /// <summary>
    /// Write a fresh, unfrozen brush into each known brush key. We don't SetValue on
    /// the existing brush (it may be frozen once any element references it), we
    /// replace it in the dictionary. Newly-built UIs pick up the new instance via
    /// FindResource; existing elements need RebindOpenWindows to re-bind.
    /// </summary>
    static void RepaintBrushes()
    {
        var app = Application.Current;
        if (app == null) return;
        var themeDict = app.Resources.MergedDictionaries
            .Cast<ResourceDictionary>()
            .FirstOrDefault(d => d.Source?.OriginalString.EndsWith("Resources/Theme.xaml", StringComparison.OrdinalIgnoreCase) == true);
        var brushesDict = app.Resources.MergedDictionaries
            .Cast<ResourceDictionary>()
            .FirstOrDefault(d => d.Source?.OriginalString.EndsWith("Theme.Brushes.xaml", StringComparison.OrdinalIgnoreCase) == true);
        foreach (var (brushKey, colorKey) in BrushToColorKey)
        {
            if (app.Resources[colorKey] is not Color color) continue;
            var host = brushKey.StartsWith("Brush.") ? brushesDict : themeDict;
            if (host == null) continue;
            host[brushKey] = new SolidColorBrush(color);
        }
        ApplySystemAccentIfApplicable();
    }

    /// <summary>
    /// Walk management-only windows (ManagementWindow, PropertyWindow) and re-bind
    /// code-built elements to Brush.* keys. Intentionally skips ZoneWindow /
    /// ClockWidget / CalendarWidget / StickyNoteWindow / PanelWindow — those are
    /// desktop surface windows whose visual design is independent of the management
    /// theme palette (they own their own per-instance color settings).
    /// </summary>
    static void RebindOpenWindows()
    {
        var app = Application.Current;
        if (app == null) return;
        // ponytail: compare by type name to avoid a cross-namespace using. Only the
        // management shell and its floating property windows follow the theme palette;
        // desktop surface windows (ZoneWindow / ClockWidget / CalendarWidget /
        // StickyNoteWindow / PanelWindow) own their own per-instance colors and MUST
        // not be re-themed on a palette swap.
        foreach (var window in app.Windows.OfType<Window>())
        {
            var name = window.GetType().Name;
            if (name != "ManagementWindow" && name != "PropertyWindow") continue;
            if (window.Content is DependencyObject root) RebindVisualTree(root);
        }
    }

    static void RebindVisualTree(DependencyObject root)
    {
        // BFS over the visual tree
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);
        int visited = 0;
        while (queue.Count > 0 && visited < 50000) // hard cap: full tree of 1100x680 window is <5000
        {
            var node = queue.Dequeue();
            visited++;
            if (node is FrameworkElement fe) RebindElement(fe);
            int n = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < n; i++)
                queue.Enqueue(VisualTreeHelper.GetChild(node, i));
        }
    }

    static void RebindElement(FrameworkElement fe)
    {
        foreach (var dp in BrushDPs)
        {
            // Only re-bind values that came from a local set (LocalValueSource) — i.e.
            // code-built controls that captured one brush at construction time. Skip
            // values that came from a Style setter (Style / StyleTrigger /
            // ImplicitStyle) — we don't want to overwrite a themed template button
            // (PrimaryButton, DarkCheckBox, etc.) with a generic neutral background.
            // Also skip `Transparent` — that is a deliberate "no fill" intent on
            // icon buttons in titles, theme switcher, etc.
            var src = System.Windows.DependencyPropertyHelper.GetValueSource(fe, dp);
            if (src.BaseValueSource != BaseValueSource.Local) continue;
            if (fe.GetValue(dp) is not SolidColorBrush scb) continue;
            if (scb.Color == Colors.Transparent) continue;
            var key = ModernBrushKeyFor(dp);
            if (key != null)
                fe.SetResourceReference(dp, key);
        }
    }

    /// <summary>Map a brush DP to the most appropriate modern Brush.* key based on the
    /// element type and adjacent properties. Background gets Brush.Bg.Surface for
    /// everything except Input elements where Bg.Input is more accurate.</summary>
    static string? ModernBrushKeyFor(DependencyProperty dp)
    {
        if (dp == Control.BackgroundProperty) return "Brush.Bg.Surface";
        if (dp == Control.ForegroundProperty || dp == TextBlock.ForegroundProperty) return "Brush.Text.Primary";
        if (dp == Control.BorderBrushProperty) return "Brush.Border.Subtle";
        if (dp == TextBox.CaretBrushProperty) return "Brush.Text.Primary";
        if (dp == Shape.FillProperty) return "Brush.Accent";
        if (dp == Shape.StrokeProperty) return "Brush.Text.Primary";
        return null;
    }

    public static void StartListeningToSystem()
    {
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (_current != AppThemeMode.System) return;
            // ponytail: HC toggle fires Accessibility, accent color fires Color,
            // AppsUseLightTheme fires General. Filter on all three so live changes
            // actually re-resolve the palette. Without Accessibility the user could
            // flip Windows HC on and the app would silently stay on Light/Dark.
            if (e.Category is UserPreferenceCategory.General
                or UserPreferenceCategory.Color
                or UserPreferenceCategory.Accessibility)
            {
                Apply(AppThemeMode.System);
            }
        };

        // ponytail: Win32 broadcasts "ImmersiveColorSet" on accent color changes,
        // but .NET's SystemEvents.UserPreferenceChanged only maps a small fixed
        // set of lParam strings to UserPreferenceCategory — "ImmersiveColorSet"
        // isn't in that table, so the event never fires for accent changes. Poll
        // the registry once a second and let ApplySystemAccentIfApplicable's cache
        // short-circuit the steady-state (one read + one comparison per tick).
        // 1-second latency is invisible; the alternative (HwndSource + lParam
        // string parsing) is ~3x the code for the same outcome.
        _accentPollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _accentPollTimer.Tick += (_, _) =>
        {
            if (_current != AppThemeMode.System) return;
            ApplySystemAccentIfApplicable();
        };
        _accentPollTimer.Start();
    }
}
