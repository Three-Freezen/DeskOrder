using System.Windows;
using System.Windows.Media;

namespace DesktopZones.Helpers;

/// <summary>
/// Static accessor for Theme.xaml brushes. Lets code-behind (Pages) reference
/// the same resource keys that XAML uses, so the color palette lives in one place.
/// All accessors cache the resolved brush after first lookup.
/// </summary>
public static class ThemeBrushes
{
    static T Get<T>(string key) where T : class =>
        Application.Current.Resources[key] as T
            ?? throw new System.InvalidOperationException($"Missing resource: {key}");

    public static SolidColorBrush CardBg => Get<SolidColorBrush>("CardBg");
    public static SolidColorBrush CardBorder => Get<SolidColorBrush>("CardBorder");
    public static SolidColorBrush IconBg => Get<SolidColorBrush>("IconBg");
    public static SolidColorBrush IconBgSubtle => Get<SolidColorBrush>("IconBgSubtle");
    public static SolidColorBrush IconFg => Get<SolidColorBrush>("IconFg");
    public static SolidColorBrush HoverOverlay => Get<SolidColorBrush>("HoverOverlay");
    public static SolidColorBrush SubTextFg => Get<SolidColorBrush>("SubTextFg");
    public static SolidColorBrush MutedTextFg => Get<SolidColorBrush>("MutedTextFg");
    public static SolidColorBrush DimTextFg => Get<SolidColorBrush>("DimTextFg");
    public static SolidColorBrush SuccessBrush => Get<SolidColorBrush>("SuccessBrush");
    public static SolidColorBrush DangerBrush => Get<SolidColorBrush>("DangerBrush");
    public static SolidColorBrush DangerBrushStrong => Get<SolidColorBrush>("DangerBrushStrong");
    public static SolidColorBrush WarnFg => Get<SolidColorBrush>("WarnFg");
    public static SolidColorBrush InactiveBtnBg => Get<SolidColorBrush>("InactiveBtnBg");
    public static SolidColorBrush DisabledTextFg => Get<SolidColorBrush>("DisabledTextFg");

    // Existing resources (re-exposed for symmetry)
    public static SolidColorBrush BgApp => Get<SolidColorBrush>("BgApp");
    public static SolidColorBrush BgSurface => Get<SolidColorBrush>("BgSurface");
    public static SolidColorBrush Bg3 => Get<SolidColorBrush>("BgElevated");
    public static SolidColorBrush BgElevated => Get<SolidColorBrush>("BgElevated");
    public static SolidColorBrush BgHover => Get<SolidColorBrush>("BgHover");
    public static SolidColorBrush BgSelected => Get<SolidColorBrush>("BgSelected");
    public static SolidColorBrush Line => Get<SolidColorBrush>("Line");
    public static SolidColorBrush LineStrong => Get<SolidColorBrush>("LineStrong");
    public static SolidColorBrush BrandCyan => Get<SolidColorBrush>("BrandCyan");
    public static SolidColorBrush AccentSoft => Get<SolidColorBrush>("AccentSoft");
    public static SolidColorBrush AccentLine => Get<SolidColorBrush>("AccentLine");
    public static SolidColorBrush T1 => Get<SolidColorBrush>("T1");
    public static SolidColorBrush T2 => Get<SolidColorBrush>("T2");
    public static SolidColorBrush T3 => Get<SolidColorBrush>("T3");
    public static SolidColorBrush TDisabled => Get<SolidColorBrush>("TDisabled");

    // ponytail: modern Brush.* keys. ApplySystemAccentIfApplicable replaces ONLY
    // these (not the legacy keys above), so anything that needs to follow the live
    // system accent in System mode must read from here. Legacy keys above still
    // follow Light/Dark/HC swaps via RepaintBrushes, just not the accent overlay.
    public static SolidColorBrush BgBaseModern => Get<SolidColorBrush>("Brush.Bg.Base");
    public static SolidColorBrush BgChromeModern => Get<SolidColorBrush>("Brush.Bg.Chrome");
    public static SolidColorBrush BgSurfaceModern => Get<SolidColorBrush>("Brush.Bg.Surface");
    public static SolidColorBrush BgHoverModern => Get<SolidColorBrush>("Brush.Bg.Hover");
    public static SolidColorBrush BgActiveModern => Get<SolidColorBrush>("Brush.Bg.Active");
    public static SolidColorBrush BgInputModern => Get<SolidColorBrush>("Brush.Bg.Input");
    public static SolidColorBrush BorderSubtleModern => Get<SolidColorBrush>("Brush.Border.Subtle");
    public static SolidColorBrush BorderDefaultModern => Get<SolidColorBrush>("Brush.Border.Default");
    public static SolidColorBrush BorderStrongModern => Get<SolidColorBrush>("Brush.Border.Strong");
    public static SolidColorBrush TextPrimaryModern => Get<SolidColorBrush>("Brush.Text.Primary");
    public static SolidColorBrush TextSecondaryModern => Get<SolidColorBrush>("Brush.Text.Secondary");
    public static SolidColorBrush TextTertiaryModern => Get<SolidColorBrush>("Brush.Text.Tertiary");
    public static SolidColorBrush TextDisabledModern => Get<SolidColorBrush>("Brush.Text.Disabled");
    public static SolidColorBrush AccentWashModern => Get<SolidColorBrush>("Brush.Accent.Wash");
    public static SolidColorBrush AccentSolidModern => Get<SolidColorBrush>("Brush.Accent.Solid");
    public static SolidColorBrush AccentModern => Get<SolidColorBrush>("Brush.Accent");
    // ponytail 2026-08-28: 固定蓝色按钮系（不跟随系统强调色）— 代码构建的填充/镂空按钮统一用这套。
    public static SolidColorBrush BtnSolidModern => Get<SolidColorBrush>("Brush.Btn.Solid");
    public static SolidColorBrush BtnOnModern => Get<SolidColorBrush>("Brush.Btn.On");
    public static SolidColorBrush DangerModern => Get<SolidColorBrush>("Brush.Danger");
    public static SolidColorBrush SuccessModern => Get<SolidColorBrush>("Brush.Success");
    public static SolidColorBrush WarningModern => Get<SolidColorBrush>("Brush.Warning");
}
