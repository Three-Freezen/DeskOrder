using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
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
    static HwndSource? _accentMsgSource;
    static DispatcherTimer? _accentSafetyPoll;

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
        // ponytail: invalidate the accent cache on every mode change. Without this
        // the System → Light → System path would skip the second accent apply
        // because _lastAppliedAccent still holds the value from the first System
        // session and the cache short-circuit returns early.
        _lastAppliedAccent = null;
        SwapColors(_resolved);
        RepaintBrushes();
        RebindOpenWindows();
        Changed?.Invoke(mode);
    }

    /// <summary>
    /// Force re-apply the system accent right now. Called from the Win32
    /// WM_SETTINGCHANGE hook in App.xaml.cs (ImmersiveColorSet /
    /// UserPreferences / WindowsThemeElement) so live accent changes don't have
    /// to wait for the 1-second DispatcherTimer poll. Invalidates the cache first
    /// because WM_SETTINGCHANGE already confirmed the accent changed; relying on
    /// the read-vs-cached comparison would race against the registry write.
    /// Also notifies AcrylicHelper so the "跟随系统" liquid glass preset
    /// re-tints its registered windows with the new accent.
    /// </summary>
    public static void ApplySystemAccent()
    {
        _lastAppliedAccent = null;
        ApplySystemAccentIfApplicable();
        AcrylicHelper.OnSystemAccentChanged();
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
        // ponytail: pick text color by WCAG contrast ratio, not luminance threshold.
        // Compare accent against pure black and pure white; whichever gives the
        // higher contrast wins. Guarantees that whichever side wins has at least
        // 4.5:1-ish (always picks the more readable of the two). Pure black or
        // pure white only — no gray, no chromatic tint.
        double Linear(byte v) { var s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        double Luminance(Color c) => 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);
        double Contrast(Color a, Color b)
        {
            var la = Luminance(a); var lb = Luminance(b);
            var hi = Math.Max(la, lb); var lo = Math.Min(la, lb);
            return (hi + 0.05) / (lo + 0.05);
        }
        var textColor = Contrast(accentColor, Colors.Black) >= Contrast(accentColor, Colors.White)
            ? Colors.Black
            : Colors.White;
        // ponytail 2026-08-25: 镂空算法已抽到 ApplyHollowAccentBrushes。
        // 这里只负责"换背景为系统主色"+"Wash/Text 系列 brush 重算"，
        // 镂空按钮 brush 在背景换完后再调用一次 ApplyHollowAccentBrushes。

        var brushesDict = Application.Current?.Resources.MergedDictionaries
            .Cast<ResourceDictionary>()
            .FirstOrDefault(d => d.Source?.OriginalString.EndsWith("Theme.Brushes.xaml", StringComparison.OrdinalIgnoreCase) == true);
        if (brushesDict == null) return;

        // Backgrounds: accent color everywhere. Replace brush instance
        // (in-place Color write fails — brushes in Theme.Brushes.xaml freeze once
        // referenced and reject SetValue with InvalidOperationException).
        brushesDict["Brush.Bg.Base"]            = new SolidColorBrush(accentColor);
        brushesDict["Brush.Bg.Chrome"]          = new SolidColorBrush(accentColor);
        brushesDict["Brush.Bg.Surface"]         = new SolidColorBrush(accentColor);
        brushesDict["Brush.Bg.Input"]           = new SolidColorBrush(accentColor);
        // Hover / Active: text color with low alpha so they read as subtle overlays
        // on the accent bg instead of disappearing (accent-on-accent).
        brushesDict["Brush.Bg.Hover"]           = new SolidColorBrush(Color.FromArgb(0x22, textColor.R, textColor.G, textColor.B));
        brushesDict["Brush.Bg.Active"]          = new SolidColorBrush(Color.FromArgb(0x33, textColor.R, textColor.G, textColor.B));

        // Borders: text color at rising alphas so dividers and card outlines
        // remain visible against the accent bg.
        brushesDict["Brush.Border.Subtle"]      = new SolidColorBrush(Color.FromArgb(0x40, textColor.R, textColor.G, textColor.B));
        brushesDict["Brush.Border.Default"]     = new SolidColorBrush(Color.FromArgb(0x55, textColor.R, textColor.G, textColor.B));
        brushesDict["Brush.Border.Strong"]      = new SolidColorBrush(Color.FromArgb(0x77, textColor.R, textColor.G, textColor.B));

        // Text: black or white, fading through alpha so the hierarchy reads.
        brushesDict["Brush.Text.Primary"]       = new SolidColorBrush(textColor);
        brushesDict["Brush.Text.Secondary"]     = new SolidColorBrush(Color.FromArgb(0xC0, textColor.R, textColor.G, textColor.B));
        brushesDict["Brush.Text.Tertiary"]      = new SolidColorBrush(Color.FromArgb(0x80, textColor.R, textColor.G, textColor.B));
        brushesDict["Brush.Text.Disabled"]      = new SolidColorBrush(Color.FromArgb(0x55, textColor.R, textColor.G, textColor.B));

        // Wash = textColor 20% alpha（用在 EditableListRow 等高亮背景）
        brushesDict["Brush.Accent.Wash"]        = new SolidColorBrush(Color.FromArgb(0x33, textColor.R, textColor.G, textColor.B));
        brushesDict["Brush.Accent.2"]           = new SolidColorBrush(textColor);

        // ponytail 2026-08-25: System 模式下 Solid/On 用系统主色按算法算（不用 Color.Accent dict）。
        //   Solid      = mix(accent, black, 20%)  — 加深 20%
        //   On         = mix(accent, accent, 50%) = accent 同色（镂空与背景同色）→ 截图 1 效果
        // Solid/On 的 dict 写入可能抛 InvalidOperationException（dict freeze），
        // 所以 dict 写入要 try/catch 兜底；失败时更新既有 brush 的 Color。
        Color Mix(Color a, Color b, double t) => Color.FromRgb(
            (byte)(a.R * (1 - t) + b.R * t),
            (byte)(a.G * (1 - t) + b.G * t),
            (byte)(a.B * (1 - t) + b.B * t));
        var sysSolid = Mix(accentColor, Colors.Black, 0.20);
        var sysOn    = Mix(accentColor, accentColor, 0.50);  // = accentColor（与背景同色）
        var sysSolidHover = Mix(accentColor, Colors.Black, 0.30);
        var sysSolidPress = Mix(accentColor, Colors.Black, 0.10);
        var sysSolidDisabled = Color.FromArgb(0x66, accentColor.R, accentColor.G, accentColor.B);
        var sysOnDisabled    = Color.FromArgb(0x55, accentColor.R, accentColor.G, accentColor.B);
        try
        {
            brushesDict["Brush.Accent.Solid"]          = new SolidColorBrush(sysSolid);
            brushesDict["Brush.Accent.Solid.Hover"]    = new SolidColorBrush(sysSolidHover);
            brushesDict["Brush.Accent.Solid.Press"]    = new SolidColorBrush(sysSolidPress);
            brushesDict["Brush.Accent.Solid.Disabled"] = new SolidColorBrush(sysSolidDisabled);
            brushesDict["Brush.Accent.On"]             = new SolidColorBrush(sysOn);
            brushesDict["Brush.Accent.On.Disabled"]    = new SolidColorBrush(sysOnDisabled);
        }
        catch (InvalidOperationException)
        {
            // dict frozen — 更新既有 brush 的 Color
            if (brushesDict["Brush.Accent.Solid"]          is SolidColorBrush sb1) sb1.Color = sysSolid;
            if (brushesDict["Brush.Accent.Solid.Hover"]    is SolidColorBrush sb2) sb2.Color = sysSolidHover;
            if (brushesDict["Brush.Accent.Solid.Press"]    is SolidColorBrush sb3) sb3.Color = sysSolidPress;
            if (brushesDict["Brush.Accent.Solid.Disabled"] is SolidColorBrush sb4) sb4.Color = sysSolidDisabled;
            if (brushesDict["Brush.Accent.On"]             is SolidColorBrush sb5) sb5.Color = sysOn;
            if (brushesDict["Brush.Accent.On.Disabled"]    is SolidColorBrush sb6) sb6.Color = sysOnDisabled;
        }

        // ponytail: DynamicResource references in XAML re-evaluate automatically
        // when the dictionary entry is replaced, but PropertyPanel builds elements
        // in code with FindResource (Local value) so those capture one brush at
        // construction time. Walk the management tree and re-bind those so a live
        // accent change reaches every element without a restart.
        RebindOpenWindows();
    }

    /// <summary>
    /// Update each known brush's Color in place. We do NOT replace the brush instance
    /// in the dictionary — replacing it would orphan every element that already
    /// resolved the brush via StaticResource (e.g. LoadPresetDialog's chrome uses
    /// {StaticResource Bg} which captures a brush reference at parse time and would
    /// stay frozen to the old color forever). SolidColorBrush is a Freezable: writing
    /// its Color DP fires Freezable.Changed, which WPF's render system listens to, so
    /// all referrers repaint automatically.
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
        // isn't in that table, so the event never fires for accent changes.
        // 2026-08-28 perf pass: 原先每秒轮询注册表一次(常驻 UI 线程唤醒)；改为
        // 隐藏消息窗口直接收 WM_SETTINGCHANGE("ImmersiveColorSet") 和
        // WM_DWMCOLORIZATIONCOLORCHANGED。保留 15s 兜底轮询 — 万一广播被吞掉，
        // 行为退化只是把延迟从 1s 拉长到 15s，不会丢功能。
        EnsureAccentMessageWindow();
        _accentSafetyPoll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _accentSafetyPoll.Tick += (_, _) =>
        {
            if (_current != AppThemeMode.System) return;
            ApplySystemAccentIfApplicable();
        };
        _accentSafetyPoll.Start();
    }

    const int WM_SETTINGCHANGE = 0x001A;
    const int WM_DWMCOLORIZATIONCOLORCHANGED = 0x0320;
    // WS_POPUP — 建一个从不 Show 的裸顶层 HWND。HWND_BROADCAST 只发顶层窗口
    // (message-only 窗口收不到广播)，所以不能用 HWND_MESSAGE。
    const int WS_POPUP = unchecked((int)0x80000000);

    static void EnsureAccentMessageWindow()
    {
        if (_accentMsgSource != null) return;
        try
        {
            var p = new HwndSourceParameters("DeskOrder.ThemeWatch", 1, 1)
            {
                WindowStyle = WS_POPUP,
                PositionX = -32000,
                PositionY = -32000,
            };
            var src = new HwndSource(p);
            src.AddHook(WndProc);
            _accentMsgSource = src;
        }
        catch
        {
            // 建窗失败 — 只剩 15s 兜底轮询，功能不丢。
        }
    }

    static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_current != AppThemeMode.System) return IntPtr.Zero;
        if (msg == WM_DWMCOLORIZATIONCOLORCHANGED)
        {
            ApplySystemAccentIfApplicable();
        }
        else if (msg == WM_SETTINGCHANGE)
        {
            var section = Marshal.PtrToStringAuto(lParam);
            if (string.Equals(section, "ImmersiveColorSet", StringComparison.OrdinalIgnoreCase))
                ApplySystemAccentIfApplicable();
        }
        return IntPtr.Zero;
    }
}
