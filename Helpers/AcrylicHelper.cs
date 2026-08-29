using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using DesktopZones.Services;

namespace DesktopZones.Helpers;

/// <summary>
/// Enables frosted-glass / acrylic blur behind WPF layered windows.
/// Uses DwmEnableBlurBehindWindow + SetWindowCompositionAttribute.
/// Liquid Glass system inspired by ZenDesktop: 3 sliders (blur, tint, luminosity) + color presets.
/// </summary>
/// <summary>Result of a blur/composition P/Invoke. <c>Error</c> is null on success.</summary>
public readonly record struct BlurResult(bool Success, string? Error)
{
    public static BlurResult Ok { get; } = new(true, null);
    public static BlurResult Fail(string err) => new(false, err);
}

public static class AcrylicHelper
{
    // ── DWM Blur Behind (works with layered/transparent windows) ──

    [StructLayout(LayoutKind.Sequential)]
    private struct DWM_BLURBEHIND
    {
        public uint dwFlags;
        public bool fEnable;
        public IntPtr hRgnBlur;
        public bool fTransitionOnMaximized;
    }

    [DllImport("dwmapi.dll", PreserveSig = false)]
    private static extern void DwmEnableBlurBehindWindow(IntPtr hwnd, ref DWM_BLURBEHIND blurBehind);

    [DllImport("dwmapi.dll")]
    private static extern int DwmIsCompositionEnabled(out bool enabled);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetColorizationColor(out int colorization, out bool opaqueBlend);

    private const uint DWM_BB_ENABLE = 0x00000001;

    // ── Win10+ composition attribute (stronger blur) ──

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    // ── 右键菜单弹层圆角(窗口级裁剪 + 圆角模糊区域 + Win11 原生圆角偏好) ──
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr hRgn, bool bRedraw);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr o);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private const uint DWM_BB_BLURREGION = 0x00000002;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    // AccentState values
    private const int ACCENT_DISABLED = 0;
    private const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;

    // ── Color presets ──

    private static readonly LocalizationService _loc = LocalizationService.Instance;

    public static readonly IReadOnlyList<string> ColorPresetNames = new[]
    {
        "Default", "Accent", "GlassWhite", "MistGrey", "DeepBlack",
        "OceanBlue", "AuroraCyan", "RosePink", "BordeauxRed", "ForestGreen",
        "RoyalPurple", "SunsetOrange", "ChampagneGold", "MorandiSage"
    };

    // ponytail 2026-08-27: 静态字典会冻结 i18n — 切语言后下拉框仍显示旧语言。
    // 改成按需查表:每次调用读取当前 LocalizationService,自动跟随语言。
    public static string GetPresetDisplayName(string key) => key switch
    {
        "Default"       => _loc["LiquidGlass.Default"],
        "Accent"        => _loc["LiquidGlass.FollowSystem"],
        "GlassWhite"    => _loc["LiquidGlass.GlassWhite"],
        "MistGrey"      => _loc["LiquidGlass.MistGray"],
        "DeepBlack"     => _loc["LiquidGlass.DeepBlack"],
        "OceanBlue"     => _loc["LiquidGlass.OceanBlue"],
        "AuroraCyan"    => _loc["LiquidGlass.AuroraCyan"],
        "RosePink"      => _loc["LiquidGlass.RosePink"],
        "BordeauxRed"   => _loc["LiquidGlass.BordeauxRed"],
        "ForestGreen"   => _loc["LiquidGlass.ForestGreen"],
        "RoyalPurple"   => _loc["LiquidGlass.RoyalPurple"],
        "SunsetOrange"  => _loc["LiquidGlass.SunsetOrange"],
        "ChampagneGold" => _loc["LiquidGlass.ChampagneGold"],
        "MorandiSage"   => _loc["LiquidGlass.MorandiGrayGreen"],
        _ => key,
    };

    /// <summary>True when the glass color mode is a custom "#RRGGBB"/"#AARRGGBB" hex
    /// instead of one of the preset names.</summary>
    public static bool IsCustomGlassColor(string? mode)
        => !string.IsNullOrEmpty(mode) && mode![0] == '#';

    /// <summary>Parse a custom glass color hex ("#RRGGBB" or "#AARRGGBB") into a WPF Color.</summary>
    public static bool TryParseGlassColor(string? mode, out Color color)
    {
        color = default;
        if (!IsCustomGlassColor(mode)) return false;
        var hex = mode!.TrimStart('#');
        try
        {
            byte a = 255, r, g, b;
            if (hex.Length == 8)
            {
                a = byte.Parse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
                r = byte.Parse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
                g = byte.Parse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
                b = byte.Parse(hex.Substring(6, 2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
            }
            else if (hex.Length == 6)
            {
                r = byte.Parse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
                g = byte.Parse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
                b = byte.Parse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
            }
            else
            {
                return false;
            }
            color = Color.FromArgb(a, r, g, b);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// ponytail 2026-08-26: 可见的"液态玻璃"渐变预览画刷(3 停靠点)。用于预设卡 /
    /// SubFolder 图标格 / SubFolder Flyout 等非 DWM 表面 — 实时窗口本体走 EnableBlur
    /// 的 DWM 玻璃路径,不用这个。与 Views/LoadPresetDialog.xaml.cs 的
    /// LiquidGlassBrushConverter 保持同一套底色表与停靠点 alpha(0xC0)。
    /// </summary>
    public static LinearGradientBrush MakePreviewGlassBrush(string? mode)
    {
        string m = mode ?? "Default";
        if (!s_PreviewGlassBase.TryGetValue(m, out var baseColor) &&
            !TryParseGlassColor(m, out baseColor))
            baseColor = s_PreviewGlassBase["Default"];

        const byte stopAlpha = 0xC0;
        baseColor = Color.FromArgb(stopAlpha, baseColor.R, baseColor.G, baseColor.B);

        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        brush.GradientStops.Add(new GradientStop(LightenGlass(baseColor, 0.35), 0.0));
        brush.GradientStops.Add(new GradientStop(baseColor, 0.5));
        brush.GradientStops.Add(new GradientStop(DarkenGlass(baseColor, 0.30), 1.0));
        brush.Freeze();
        DzTrace.Log($"[SubEdit] MakePreviewGlassBrush: mode={m} baseColor=#{baseColor.R:X2}{baseColor.G:X2}{baseColor.B:X2} (alpha={stopAlpha:X2})");
        return brush;
    }

    static readonly IReadOnlyDictionary<string, Color> s_PreviewGlassBase = new Dictionary<string, Color>
    {
        ["Default"]       = Color.FromArgb(0xFF, 0x70, 0x95, 0xC5),
        ["Accent"]        = Color.FromArgb(0xFF, 0x40, 0x90, 0xE2),
        ["GlassWhite"]    = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
        ["MistGrey"]      = Color.FromArgb(0xFF, 0xC0, 0xC0, 0xC0),
        ["DeepBlack"]     = Color.FromArgb(0xFF, 0x10, 0x10, 0x10),
        ["OceanBlue"]     = Color.FromArgb(0xFF, 0x11, 0x85, 0xFF),
        ["AuroraCyan"]    = Color.FromArgb(0xFF, 0x00, 0xD4, 0xD4),
        ["RosePink"]      = Color.FromArgb(0xFF, 0xFF, 0x69, 0xB4),
        ["BordeauxRed"]   = Color.FromArgb(0xFF, 0x8B, 0x00, 0x00),
        ["ForestGreen"]   = Color.FromArgb(0xFF, 0x22, 0x8B, 0x22),
        ["RoyalPurple"]   = Color.FromArgb(0xFF, 0x6A, 0x0D, 0xAD),
        ["SunsetOrange"]  = Color.FromArgb(0xFF, 0xFF, 0x8C, 0x00),
        ["ChampagneGold"] = Color.FromArgb(0xFF, 0xDA, 0xA5, 0x20),
        ["MorandiSage"]   = Color.FromArgb(0xFF, 0x87, 0xA9, 0x6B),
    };

    static Color LightenGlass(Color c, double amt) => Color.FromArgb(c.A,
        (byte)Math.Min(255, c.R + (255 - c.R) * amt),
        (byte)Math.Min(255, c.G + (255 - c.G) * amt),
        (byte)Math.Min(255, c.B + (255 - c.B) * amt));

    static Color DarkenGlass(Color c, double amt) => Color.FromArgb(c.A,
        (byte)(c.R * (1 - amt)), (byte)(c.G * (1 - amt)), (byte)(c.B * (1 - amt)));

    /// <summary>
    /// Resolve a color mode name to an ARGB tint color (0xAARRGGBB).
    /// The caller will apply tintOpacity (alpha) and tintLuminosity (brightness).
    /// </summary>
    private static uint ResolveBaseColorARGB(string colorMode)
    {
        // ponytail: custom color — stored as "#RRGGBB"/"#AARRGGBB" in GlassColorMode.
        if (IsCustomGlassColor(colorMode))
            return TryParseGlassColor(colorMode, out var c)
                ? ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B
                : 0u;

        return colorMode switch
        {
            "Default" => 0x00000000,      // Transparent / use system default
            "Accent" => GetSystemAccentARGB(),
            "GlassWhite" => 0xFF_FF_FF_FF, // full white base, alpha applied later
            "MistGrey" => 0xFF_C0_C0_C0,
            "DeepBlack" => 0xFF_10_10_10,
            "OceanBlue" => 0xFF_11_85_FF,
            "AuroraCyan" => 0xFF_00_D4_D4,
            "RosePink" => 0xFF_FF_69_B4,
            "BordeauxRed" => 0xFF_8B_00_00,
            "ForestGreen" => 0xFF_22_8B_22,
            "RoyalPurple" => 0xFF_6A_0D_AD,
            "SunsetOrange" => 0xFF_FF_8C_00,
            "ChampagneGold" => 0xFF_DA_A5_20,
            "MorandiSage" => 0xFF_87_A9_6B,
            _ => 0x00000000
        };
    }

    private static uint GetSystemAccentARGB()
    {
        // ponytail: same source as ThemeService — read the user's accent from
        // HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Accent\AccentColorMenu
        // (Win10/11). DwmGetColorizationColor returns the OS *colorization* color which
        // is DWM's tint for surfaces, not the user-chosen accent — on Win11 they
        // diverge and DWM often reads as a dim legacy color. Registry is the live
        // value the user picks in Personalization → Colors.
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");
            if (key?.GetValue("AccentColorMenu") is int colorref)
            {
                // COLORREF is 0x00BBGGRR → convert to 0xAARRGGBB (alpha = FF).
                // G MUST be shifted left by 8 to land in bits 8-15; without it,
                // G and B both OR into bits 0-7, which makes sage-green
                // (R=G_byte0, G=byte1, B=byte2 where R and B are equal-ish) lose
                // its G channel and render as a violet. Pure red/blue hid the bug
                // because G | B == B when the unused channel is zero.
                // Cast each shift result to uint so the OR doesn't sign-extend
                // the int colorref (top bit is often set on Win11 sage accents).
                return 0xFF000000u
                    | ((uint)(colorref        & 0xFF) << 16)   // R: bits 16-23
                    | ((uint)((colorref >>  8) & 0xFF) <<  8)  // G: bits  8-15
                    |  (uint)((colorref >> 16) & 0xFF);        // B: bits  0-7
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AcrylicHelper] ReadSystemAccent registry: {ex}");
        }
        // Fallback: Win11 default blue.
        return 0xFF_00_78_D4;
    }

    /// <summary>ponytail 2026-08-28: 公有访问器 — 返回 Windows 系统强调色(Win10/11
    /// 注册表 AccentColorMenu,失败回退默认蓝)。供 MenuThemeService 等需要「真·系统
    /// 强调色」(不随应用主题模式)的模块使用。</summary>
    public static Color GetSystemAccentColor()
    {
        uint argb = GetSystemAccentARGB();
        return Color.FromArgb(
            (byte)((argb >> 24) & 0xFF),
            (byte)((argb >> 16) & 0xFF),
            (byte)((argb >>  8) & 0xFF),
            (byte)( argb        & 0xFF));
    }

    /// <summary>DWM blur-behind toggle. Logs + returns Fail on P/Invoke exception.</summary>
    private static BlurResult TryBlurBehind(IntPtr hwnd, bool enable)
    {
        try
        {
            var bb = new DWM_BLURBEHIND
            {
                dwFlags = DWM_BB_ENABLE,
                fEnable = enable,
                hRgnBlur = IntPtr.Zero
            };
            DwmEnableBlurBehindWindow(hwnd, ref bb);
            return BlurResult.Ok;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AcrylicHelper] DwmEnableBlurBehindWindow(enable={enable}): {ex}");
            return BlurResult.Fail(ex.Message);
        }
    }

    /// <summary>Win10+ accent policy. Logs + returns Fail on P/Invoke exception.</summary>
    private static BlurResult TrySetAccent(IntPtr hwnd, int accentState, int accentFlags, int gradientColor)
    {
        var ptr = IntPtr.Zero;
        try
        {
            var accent = new AccentPolicy
            {
                AccentState = accentState,
                AccentFlags = accentFlags,
                GradientColor = gradientColor,
                AnimationId = 0
            };
            ptr = Marshal.AllocHGlobal(Marshal.SizeOf(accent));
            Marshal.StructureToPtr(accent, ptr, false);
            var data = new WindowCompositionAttributeData
            {
                Attribute = 19, // WCA_ACCENT_POLICY
                SizeOfData = Marshal.SizeOf(accent),
                Data = ptr
            };
            SetWindowCompositionAttribute(hwnd, ref data);
            return BlurResult.Ok;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AcrylicHelper] SetWindowCompositionAttribute(state={accentState}): {ex}");
            return BlurResult.Fail(ex.Message);
        }
        finally
        {
            if (ptr != IntPtr.Zero) Marshal.FreeHGlobal(ptr);
        }
    }

    /// <summary>
    /// Convert an ARGB color (0xAARRGGBB) to ABGR format (0xAABBGGRR) used by AccentPolicy.GradientColor.
    /// </summary>
    private static int ArgbToAbgr(uint argb)
    {
        uint a = (argb >> 24) & 0xFF;
        uint r = (argb >> 16) & 0xFF;
        uint g = (argb >> 8) & 0xFF;
        uint b = argb & 0xFF;
        return (int)((a << 24) | (b << 16) | (g << 8) | r);
    }

    private static readonly Dictionary<(string, int, int), int> _tintCache = new();

    // ponytail: registry of windows that called EnableBlur, so OnSystemAccentChanged
    // can re-apply blur to the windows that actually use the "Accent" preset. Keyed
    // by window instance; entries get removed in DisableBlur.
    // fillHex/fillOpacity:分区本体一体化时填充并入玻璃 tint,重算"Accent"需要回填原始
    // 填充输入;普通玻璃窗口为 null/0。
    private static readonly Dictionary<Window, (int blur, int opacity, int lum, string mode, string? fillHex, double fillOpacity)> _registered = new();

    /// <summary>
    /// Build the GradientColor (ABGR format) from color mode + tint opacity + tint luminosity.
    /// </summary>
    public static int ResolveGlassTintColor(string colorMode, int tintOpacity, int tintLuminosity)
    {
        // ponytail: skip the cache for "Accent" — the system accent can change mid-session
        // and every call must re-read GetSystemAccentARGB so the live tint follows. Other
        // presets are static so caching them saves the luminosity+opacity math.
        if (colorMode != "Accent")
        {
            var key = (colorMode, tintOpacity, tintLuminosity);
            if (_tintCache.TryGetValue(key, out var cached)) return cached;
        }

        uint argb = ResolveBaseColorARGB(colorMode);

        if (colorMode == "Default" || argb == 0)
            return 0; // transparent — no tint

        // Extract RGB components
        uint r = (argb >> 16) & 0xFF;
        uint g = (argb >> 8) & 0xFF;
        uint b = argb & 0xFF;

        // Apply luminosity: 0-150% multiplier (100 = original)
        double lum = Math.Clamp(tintLuminosity, 0, 150) / 100.0;
        r = (uint)Math.Min(255, r * lum);
        g = (uint)Math.Min(255, g * lum);
        b = (uint)Math.Min(255, b * lum);

        // Apply tint opacity: 0-100% → alpha byte 0-255
        double opacity = Math.Clamp(tintOpacity, 0, 100) / 100.0;
        uint alpha = (uint)(opacity * 255);

        uint finalArgb = (alpha << 24) | (r << 16) | (g << 8) | b;
        int result = ArgbToAbgr(finalArgb);
        if (colorMode != "Accent")
            _tintCache[(colorMode, tintOpacity, tintLuminosity)] = result;
        return result;
    }

    /// <summary>
    /// Drop every cached "Accent" entry and re-apply blur to every registered window
    /// that uses the "Accent" color mode. Called by ThemeService.ApplySystemAccent
    /// whenever the system accent color changes (WM_SETTINGCHANGE → ImmersiveColorSet
    /// or the 1-second DispatcherTimer fallback) so live accent changes propagate
    /// to the liquid glass tint without restarting the app.
    /// </summary>
    public static void OnSystemAccentChanged()
    {
        // Clear stale "Accent" cache entries so a fresh re-apply reads the current
        // system accent. Other modes are static so we leave them alone.
        var staleKeys = _tintCache.Keys.Where(k => k.Item1 == "Accent").ToList();
        foreach (var k in staleKeys) _tintCache.Remove(k);

        // Re-apply to every window registered with mode == "Accent". Snapshot first
        // because EnableBlur may mutate the dict if the window is being torn down.
        foreach (var entry in _registered.ToList())
        {
            var (window, settings) = (entry.Key, entry.Value);
            if (settings.mode != "Accent") continue;
            if (!window.IsLoaded) continue;
            try
            {
                // ponytail 2026-08-30: 分区本体一体化路径按原始填充+玻璃输入重算合成 tint。
                if (settings.fillHex is null)
                    EnableBlur(window, settings.blur, settings.opacity, settings.lum, settings.mode);
                else
                    EnableBlurComposite(window, settings.blur, settings.fillHex, settings.fillOpacity,
                        settings.mode, settings.opacity, settings.lum);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AcrylicHelper] OnSystemAccentChanged reapply: {ex}");
            }
        }
    }

    /// <summary>
    /// Enable liquid glass blur behind a WPF window using ZenDesktop-style parameters.
    /// Uses ACCENT_ENABLE_ACRYLICBLURBEHIND (4) when blurAmount > 0.
    /// </summary>
    /// <param name="blurAmount">Blur radius 0-60. 0 = disable.</param>
    /// <param name="tintOpacity">Tint alpha 0-100%.</param>
    /// <param name="tintLuminosity">Color brightness 0-150%.</param>
    /// <param name="colorMode">Color preset name (Default, Accent, GlassWhite, etc.).</param>
    public static BlurResult EnableBlur(Window window, int blurAmount, int tintOpacity, int tintLuminosity, string colorMode)
    {
        // ponytail: remember (window → settings) so OnSystemAccentChanged can re-apply
        // when the system accent changes. Override existing entry if EnableBlur is
        // called again with different params (e.g. user edited settings live).
        _registered[window] = (blurAmount, tintOpacity, tintLuminosity, colorMode, null, 0);
        return EnableBlur(new WindowInteropHelper(window).Handle, blurAmount, tintOpacity, tintLuminosity, colorMode);
    }

    // ── 分区本体一体化:内部填充 + 液态玻璃合成 ──
    // ponytail 2026-08-30: 分区背景现在是「壁纸 → DWM 玻璃(模糊+玻璃着色) → WPF 填充
    // (FillColor)」两个独立图层。这里把两者在计算层合成一个有效着色 T = 填充 over
    // 玻璃着色(alpha 合成),玻璃开时填充并入玻璃 tint、FillRect 透明 — 实际渲染算
    // 一层;填充色与玻璃配色作为两个独立输入「本质上还是两层」。

    /// <summary>把「内部填充 over 玻璃着色」合成一个有效 ARGB 着色。
    /// fillOpacity01 = 填充色 alpha 的额外乘数(分区本体恒为 1.0,alpha 已含在 FillColor)。</summary>
    public static Color CompositeFillOverGlass(string? fillHex, double fillOpacity01,
        string glassMode, int tintOpacity, int tintLuminosity)
    {
        Color f = Color.FromArgb(0, 0, 0, 0);
        if (!string.IsNullOrEmpty(fillHex) && TryParseGlassColor(fillHex, out var parsed))
        {
            f = parsed;
            f.A = (byte)Math.Max(0, Math.Min(255, f.A * Math.Max(0.0, Math.Min(1.0, fillOpacity01))));
        }
        Color g = AbgrToColor(ResolveGlassTintColor(glassMode, tintOpacity, tintLuminosity));
        return Over(f, g);
    }

    /// <summary>分区本体一体化开玻璃:填充并入玻璃 tint 后走与 EnableBlur 同款 DWM 配方
    /// (经典 blurbehind + accent),只是 accent 的着色换成合成值。注册原始填充输入,
    /// 系统强调色(Accent)变化时能按原始输入重算。</summary>
    public static BlurResult EnableBlurComposite(Window window, int blurAmount,
        string? fillHex, double fillOpacity01, string glassMode, int tintOpacity, int tintLuminosity)
    {
        _registered[window] = (blurAmount, tintOpacity, tintLuminosity, glassMode, fillHex, fillOpacity01);
        return EnableBlurComposite(new WindowInteropHelper(window).Handle, blurAmount,
            fillHex, fillOpacity01, glassMode, tintOpacity, tintLuminosity, skipClassicBlur: false);
    }

    /// <summary>HWND 版一体化开玻璃(次级分区浮层用,不注册 _registered)。
    /// skipClassicBlur=true 时只设 accent(与浮层现配方一致),否则经典 blurbehind + accent。</summary>
    public static BlurResult EnableBlurComposite(IntPtr hwnd, int blurAmount,
        string? fillHex, double fillOpacity01, string glassMode, int tintOpacity, int tintLuminosity, bool skipClassicBlur)
    {
        if (hwnd == IntPtr.Zero) return BlurResult.Fail("Window handle not created yet");

        bool dwmOn = true;
        try { DwmIsCompositionEnabled(out dwmOn); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AcrylicHelper] DwmIsCompositionEnabled: {ex}");
            return BlurResult.Fail(ex.Message);
        }
        if (!dwmOn) return BlurResult.Fail("DWM composition is disabled");
        if (blurAmount <= 0) { DisableBlur(hwnd); return BlurResult.Ok; }

        var tint = CompositeFillOverGlass(fillHex, fillOpacity01, glassMode, tintOpacity, tintLuminosity);
        int abgr = ArgbToAbgr(tint);
        int accentFlags = (Math.Clamp(blurAmount, 1, 60) << 8) | 0x100;
        var primary = skipClassicBlur ? BlurResult.Fail("skipped by caller") : TryBlurBehind(hwnd, true);
        var secondary = TrySetAccent(hwnd, ACCENT_ENABLE_ACRYLICBLURBEHIND, accentFlags, abgr);
        if (primary.Success || secondary.Success) return BlurResult.Ok;
        return BlurResult.Fail(primary.Error ?? secondary.Error ?? "unknown");
    }

    /// <summary>把「填充 over 玻璃渐变」合成一个单画刷 — 纯 WPF 表面用(次级分区图标格 /
    /// 浮层渐变兜底,无 DWM 玻璃)。glassBrush 非渐变时退回纯填充画刷。</summary>
    public static Brush? CompositeFillOverBrush(string? fillHex, double fillOpacity01, Brush? glassBrush)
    {
        Color f = Color.FromArgb(0, 0, 0, 0);
        bool hasFill = false;
        if (!string.IsNullOrEmpty(fillHex) && TryParseGlassColor(fillHex, out var parsed))
        {
            f = parsed;
            f.A = (byte)Math.Max(0, Math.Min(255, f.A * Math.Max(0.0, Math.Min(1.0, fillOpacity01))));
            hasFill = true;
        }

        if (glassBrush is not LinearGradientBrush grad)
        {
            if (!hasFill) return null;
            var sb = new SolidColorBrush(f);
            sb.Freeze();
            return sb;
        }

        var lg = new LinearGradientBrush
        {
            StartPoint = grad.StartPoint,
            EndPoint = grad.EndPoint,
            SpreadMethod = grad.SpreadMethod,
            MappingMode = grad.MappingMode,
            Opacity = grad.Opacity
        };
        foreach (var stop in grad.GradientStops)
            lg.GradientStops.Add(new GradientStop(hasFill ? Over(f, stop.Color) : stop.Color, stop.Offset));
        lg.Freeze();
        return lg;
    }

    static Color AbgrToColor(int abgr) => Color.FromArgb(
        (byte)((uint)abgr >> 24),
        (byte)(abgr & 0xFF),
        (byte)((abgr >> 8) & 0xFF),
        (byte)((abgr >> 16) & 0xFF));

    static int ArgbToAbgr(Color c)
        => ArgbToAbgr((uint)((c.A << 24) | (c.R << 16) | (c.G << 8) | c.B));

    /// <summary>f over g 的标准 alpha 合成(前后均为不透明背景上的预乘语义)。</summary>
    static Color Over(Color f, Color g)
    {
        double fa = f.A / 255.0, ga = g.A / 255.0;
        double a = fa + ga * (1.0 - fa);
        if (a <= 0.0001) return Color.FromArgb(0, 0, 0, 0);
        return Color.FromArgb(
            (byte)Math.Round(a * 255.0),
            (byte)Math.Round((f.R * fa + g.R * ga * (1.0 - fa)) / a),
            (byte)Math.Round((f.G * fa + g.G * ga * (1.0 - fa)) / a),
            (byte)Math.Round((f.B * fa + g.B * ga * (1.0 - fa)) / a));
    }

    /// <summary>
    /// ponytail 2026-08-26: HWND 版重载 — 给 Popup 子窗口(SubFolder Flyout)开真玻璃。
    /// 与 Window 版同一套 DWM 配方;不注册 _registered(Popup 生命周期短,不参与
    /// 系统强调色变化的批量重刷)。
    /// </summary>
    public static BlurResult EnableBlur(IntPtr hwnd, int blurAmount, int tintOpacity, int tintLuminosity, string colorMode)
        => EnableBlur(hwnd, blurAmount, tintOpacity, tintLuminosity, colorMode, skipClassicBlur: false);

    /// <summary>
    /// ponytail 2026-08-29: <paramref name="skipClassicBlur"/>=true 时只设 WCA_ACCENT_POLICY
    /// (accent 着色+模糊),跳过 DwmEnableBlurBehindWindow 经典 blur。实测 Popup 子窗口上
    /// 经典 blur 生效而 accent 被静默忽略时,浮层会显示成"压暗 ~30% 的灰底"(对比分区
    /// 的明亮着色玻璃)"浮层更深"的根源。去掉经典 blur 后:accent 成功 = 与分区同款
    /// 着色玻璃;accent 无效果 = 调用方走渐变兜底,两者都不再变暗。</summary>
    public static BlurResult EnableBlur(IntPtr hwnd, int blurAmount, int tintOpacity, int tintLuminosity, string colorMode, bool skipClassicBlur)
    {
        if (hwnd == IntPtr.Zero) return BlurResult.Fail("Window handle not created yet");

        // Check DWM is on
        bool dwmOn = true;
        try { DwmIsCompositionEnabled(out dwmOn); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AcrylicHelper] DwmIsCompositionEnabled: {ex}");
            return BlurResult.Fail(ex.Message);
        }
        if (!dwmOn) return BlurResult.Fail("DWM composition is disabled");

        if (blurAmount <= 0)
        {
            DisableBlur(hwnd);
            return BlurResult.Ok;
        }

        // Calculate gradient color from color mode + tint settings
        int gradientColor = ResolveGlassTintColor(colorMode, tintOpacity, tintLuminosity);

        // AccentFlags encodes blur radius: bits 8-15 carry the radius, bits 0-7 carry style flags
        int accentFlags = (Math.Clamp(blurAmount, 1, 60) << 8) | 0x100;

        // Primary: DWM Blur Behind (works with AllowsTransparency) — 对 Popup 子窗口会
        // 把背景压暗(经典 blur 无着色),仅"可能挡刀"的路径不再使用。
        var primary = skipClassicBlur
            ? BlurResult.Fail("skipped by caller")
            : TryBlurBehind(hwnd, true);

        // Secondary: Win10+ acrylic accent for stronger / varied effect
        var secondary = TrySetAccent(hwnd, ACCENT_ENABLE_ACRYLICBLURBEHIND, accentFlags, gradientColor);

        // Blur is considered enabled if either path worked.
        if (primary.Success || secondary.Success) return BlurResult.Ok;
        return BlurResult.Fail(primary.Error ?? secondary.Error ?? "unknown");
    }

    /// <summary>Disable blur on a window.</summary>
    public static BlurResult DisableBlur(Window window)
    {
        // ponytail: drop the registry entry so OnSystemAccentChanged doesn't try to
        // re-apply to a torn-down window.
        _registered.Remove(window);
        try { return DisableBlur(new WindowInteropHelper(window).Handle); }
        catch { return BlurResult.Fail("Window handle not created yet"); }
    }

    /// <summary>ponytail 2026-08-26: HWND 版重载 — 关闭 Popup 子窗口上的玻璃。</summary>
    public static BlurResult DisableBlur(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return BlurResult.Fail("Window handle not created yet");
        var primary = TryBlurBehind(hwnd, false);
        var secondary = TrySetAccent(hwnd, ACCENT_DISABLED, 0, 0);
        if (primary.Success || secondary.Success) return BlurResult.Ok;
        return BlurResult.Fail(primary.Error ?? secondary.Error ?? "unknown");
    }

    /// <summary>
    /// Create a chromatic dispersion border brush inspired by ZenDesktop's Apple Liquid Glass.
    /// Diagonal prismatic gradient: red → orange → green → blue → purple.
    /// </summary>
    public static LinearGradientBrush CreateChromaticBorder()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
            SpreadMethod = GradientSpreadMethod.Repeat
        };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x60, 0xFF, 0x44, 0x44), 0.0));  // red
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x50, 0xFF, 0x88, 0x00), 0.2));  // orange
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x50, 0x44, 0xCC, 0x44), 0.4));  // green
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x55, 0x44, 0x88, 0xFF), 0.6));  // blue
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x60, 0xAA, 0x44, 0xFF), 0.8));  // purple
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x50, 0xFF, 0x44, 0x88), 1.0));  // pink-red
        return brush;
    }

    // ── Liquid Glass Settings Dialog ──

    /// <summary>
    /// Show a liquid glass settings popup dialog. Returns true if saved, false if cancelled.
    /// Modifies the ref parameters on save.
    /// </summary>
    public static bool ShowLiquidGlassDialog(Window owner, string title,
        ref int blurAmount, ref int tintOpacity, ref int tintLuminosity, ref string colorMode,
        bool isChinese, Action<int, int, int, string>? onPreviewChanged = null)
    {
        // Copy ref params to locals for lambda capture
        int localBlur = blurAmount;
        int localTintOpacity = tintOpacity;
        int localTintLuminosity = tintLuminosity;
        string localColorMode = colorMode;
        string colorModeSaved = colorMode;

        // Helper to fire live preview
        void FirePreview() => onPreviewChanged?.Invoke(localBlur, localTintOpacity, localTintLuminosity, localColorMode);

        var dlg = new Window
        {
            Title = isChinese ? $"💧 {title}" : $"💧 {title}",
            Width = 440, Height = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = owner,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent
        };

        var dlgBg = new Border
        {
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1)
        };
        dlgBg.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "Brush.Bg.Chrome");
        dlgBg.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "Brush.Border.Subtle");

        var rootGrid = new Grid();
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // title bar
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // separator
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // content

        // Custom title bar
        var titleBar = new Border
        {
            CornerRadius = new CornerRadius(10, 10, 0, 0),
            Padding = new Thickness(12, 8, 12, 8),
            Cursor = System.Windows.Input.Cursors.SizeAll
        };
        titleBar.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "Brush.Bg.Chrome");
        titleBar.MouseLeftButtonDown += (_, _) => { try { dlg.DragMove(); } catch { } };

        var titlePanel = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        var titleText = new System.Windows.Controls.TextBlock
        {
            Text = $"💧 {title}",
            FontSize = 14, FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        titleText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "Brush.Text.Primary");
        titlePanel.Children.Add(titleText);

        var closeBtn = new System.Windows.Controls.Button
        {
            Content = "✕", Width = 28, Height = 28,
            FontSize = 12, Cursor = System.Windows.Input.Cursors.Hand,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 0)
        };
        closeBtn.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "Brush.Text.Secondary");
        closeBtn.Click += (_, _) => dlg.Close();

        var titleRow = new System.Windows.Controls.Grid();
        titleRow.Children.Add(titlePanel);
        titleRow.Children.Add(closeBtn);
        System.Windows.Controls.Grid.SetColumn(closeBtn, 0);
        titleBar.Child = titleRow;

        // ponytail 2026-08-28: 分隔线改接管理界面同款文字自适应色（Brush.Text.Secondary
        // 跟随 Light/Dark/HC 主题与系统强调色重绘），不再用固定半透明白。
        var separator = new Border
        {
            Height = 1,
            Margin = new Thickness(0, 0, 0, 0)
        };
        separator.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "Menu.Separator");

        System.Windows.Controls.Grid.SetRow(titleBar, 0);
        System.Windows.Controls.Grid.SetRow(separator, 1);
        rootGrid.Children.Add(titleBar);
        rootGrid.Children.Add(separator);

        var grid = new Grid { Margin = new Thickness(20) };
        System.Windows.Controls.Grid.SetRow(grid, 2);
        rootGrid.Children.Add(grid);

        dlgBg.Child = rootGrid;
        dlg.Content = dlgBg;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // title
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // blur slider
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // opacity slider
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // luminosity slider
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // color preset
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // custom color
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // exclusive hint
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // hint
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // buttons

        var t1 = (Brush)Application.Current.FindResource("Brush.Text.Primary");
        var t2 = (Brush)Application.Current.FindResource("Brush.Text.Secondary");
        var ibg = (Brush)Application.Current.FindResource("Brush.Bg.Input");
        var ibd = (Brush)Application.Current.FindResource("Brush.Border.Subtle");

        int row = 0;

        // Title
        var titleTb = new TextBlock
        {
            Text = _loc["LiquidGlass.Title"],
            FontSize = 16, FontWeight = FontWeights.SemiBold,
            Foreground = t1, Margin = new Thickness(0, 0, 0, 16)
        };
        Grid.SetRow(titleTb, row++);
        grid.Children.Add(titleTb);

        // Blur Amount slider (0-60)
        var blurSaved = localBlur;
        var blurLabelRow = BuildSliderRow(_loc["LiquidGlass.BlurRadius"], 0, 60, localBlur,
            t1, t2, (v, lbl) => { localBlur = (int)v; lbl.Text = $"{(int)v}"; FirePreview(); });
        Grid.SetRow(blurLabelRow, row++);
        grid.Children.Add(blurLabelRow);

        // Tint Opacity slider (0-100%)
        var opacitySaved = localTintOpacity;
        var opacityLabelRow = BuildSliderRow(_loc["LiquidGlass.TintOpacity"], 0, 100, localTintOpacity,
            t1, t2, (v, lbl) => { localTintOpacity = (int)v; lbl.Text = $"{localTintOpacity}%"; FirePreview(); });
        Grid.SetRow(opacityLabelRow, row++);
        grid.Children.Add(opacityLabelRow);

        // Tint Luminosity slider (0-150%)
        var luminositySaved = localTintLuminosity;
        var luminosityLabelRow = BuildSliderRow(_loc["LiquidGlass.TintLuminosity"], 0, 150, localTintLuminosity,
            t1, t2, (v, lbl) => { localTintLuminosity = (int)v; lbl.Text = $"{localTintLuminosity}%"; FirePreview(); });
        Grid.SetRow(luminosityLabelRow, row++);
        grid.Children.Add(luminosityLabelRow);

        // ── Color mode: preset (checkbox + combo) ──
        bool usePreset = !IsCustomGlassColor(localColorMode);
        bool useCustom = !usePreset;
        string customColor = useCustom ? localColorMode : "#FF7095C5"; // Default preset base color
        bool syncingColorMode = false;

        var presetCb = new CheckBox
        {
            Content = _loc["LiquidGlass.ColorPreset"],
            Foreground = t2, FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 100,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        var presetCombo = ComboBoxHelper.Create(width: 200, fontSize: 12,
            margin: new Thickness(8, 0, 0, 0));
        // ponytail: implicit Style from Controls/ComboBox.xaml sets Foreground to
        // {DynamicResource Brush.Text.Primary} — leave it alone, don't bypass with a
        // local brush reference (which would freeze the brush and skip theme follow).
        int selectedIdx = 0;
        for (int i = 0; i < ColorPresetNames.Count; i++)
        {
            // ponytail 2026-08-27: GetPresetDisplayName 每次按当前 _loc 读 — 切语言后
            // 下次开 dialog 自动拿新语言(吸取 XAML 静态绑定冻结教训)。
            presetCombo.Items.Add(GetPresetDisplayName(ColorPresetNames[i]));
            if (ColorPresetNames[i] == localColorMode) selectedIdx = i;
        }
        presetCombo.SelectedIndex = selectedIdx;
        presetCombo.SelectionChanged += (_, _) =>
        {
            if (syncingColorMode || !usePreset) return;
            localColorMode = ColorPresetNames[presetCombo.SelectedIndex];
            FirePreview();
        };
        var colorRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 12, 0, 0)
        };
        colorRow.Children.Add(presetCb);
        colorRow.Children.Add(presetCombo);
        Grid.SetRow(colorRow, row++);
        grid.Children.Add(colorRow);

        // ── Custom color: checkbox + pick button + swatch ──
        var customCb = new CheckBox
        {
            Content = _loc["LiquidGlass.Custom"],
            Foreground = t2, FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 100,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        var customBtn = new Button
        {
            Content = _loc["LiquidGlass.CustomPick"],
            Padding = new Thickness(10, 4, 10, 4),
            FontSize = 12,
            Cursor = System.Windows.Input.Cursors.Hand,
            Background = ibg, Foreground = t2,
            BorderBrush = ibd, BorderThickness = new Thickness(1),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        var customSwatch = new Border
        {
            Width = 18, Height = 18,
            CornerRadius = new CornerRadius(3),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            BorderBrush = ibd, BorderThickness = new Thickness(1),
            Background = TryParseGlassColor(customColor, out var swatchColor)
                ? new SolidColorBrush(swatchColor)
                : Brushes.Transparent
        };
        var customRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 10, 0, 0)
        };
        customRow.Children.Add(customCb);
        customRow.Children.Add(customBtn);
        customRow.Children.Add(customSwatch);
        Grid.SetRow(customRow, row++);
        grid.Children.Add(customRow);

        void ApplyColorModeVisuals()
        {
            syncingColorMode = true;
            presetCb.IsChecked = usePreset;
            customCb.IsChecked = useCustom;
            presetCombo.IsEnabled = usePreset;
            customBtn.IsEnabled = useCustom;
            customSwatch.Opacity = useCustom ? 1.0 : 0.5;
            syncingColorMode = false;
        }

        // 预设与自定义互斥：勾选一方自动取消另一方；取消当前一方则切到另一方，
        // 保证始终有且仅有一个颜色来源生效。
        void SetColorMode(bool presetOn)
        {
            usePreset = presetOn;
            useCustom = !presetOn;
            ApplyColorModeVisuals();
            localColorMode = presetOn ? ColorPresetNames[presetCombo.SelectedIndex] : customColor;
            FirePreview();
        }

        ApplyColorModeVisuals(); // initial visual state (no preview fire)

        presetCb.Checked += (_, _) => { if (syncingColorMode || usePreset) return; SetColorMode(true); };
        presetCb.Unchecked += (_, _) => { if (syncingColorMode || !usePreset) return; SetColorMode(false); };
        customCb.Checked += (_, _) => { if (syncingColorMode || useCustom) return; SetColorMode(false); };
        customCb.Unchecked += (_, _) => { if (syncingColorMode || !useCustom) return; SetColorMode(true); };

        customBtn.Click += (_, _) =>
        {
            var initial = customColor.Length >= 6 ? customColor.Substring(customColor.Length - 6) : "7095C5";
            var picker = new DesktopZones.Views.ColorPickerDialog(initial) { Owner = dlg };
            if (picker.ShowDialog() == true)
            {
                customColor = "#FF" + picker.SelectedColor;
                customSwatch.Background = TryParseGlassColor(customColor, out var picked)
                    ? new SolidColorBrush(picked)
                    : Brushes.Transparent;
                if (useCustom)
                {
                    localColorMode = customColor;
                    FirePreview();
                }
            }
        };

        // Mutual-exclusion note (below both color rows)
        var exclusiveTb = new TextBlock
        {
            Text = _loc["LiquidGlass.ExclusiveHint"],
            FontSize = 9,
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };
        exclusiveTb.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "Brush.Text.Tertiary");
        Grid.SetRow(exclusiveTb, row++);
        grid.Children.Add(exclusiveTb);

        // Original hint (moved down)
        var hintTb = new TextBlock
        {
            Text = _loc["LiquidGlass.Hint"],
            FontSize = 9,
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };
        hintTb.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "Brush.Text.Tertiary");
        Grid.SetRow(hintTb, row++);
        grid.Children.Add(hintTb);

        // Buttons
        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        var cancelBtn = new Button
        {
            Content = _loc["LiquidGlass.Cancel"],
            Width = 80, Height = 32,
            Style = (Style)Application.Current.FindResource("OutlineBtn"),
            FontSize = 12, Cursor = System.Windows.Input.Cursors.Hand,
            Margin = new Thickness(0, 0, 8, 0)
        };
        var saveBtn = new Button
        {
            Content = _loc["LiquidGlass.Save"],
            Width = 80, Height = 32,
            Style = (Style)Application.Current.FindResource("FillBtn"),
            FontSize = 12, Cursor = System.Windows.Input.Cursors.Hand,
            FontWeight = FontWeights.SemiBold
        };

        bool saved = false;
        saveBtn.Click += (_, _) =>
        {
            localColorMode = usePreset ? ColorPresetNames[presetCombo.SelectedIndex] : customColor;
            saved = true;
            dlg.Close();
        };
        cancelBtn.Click += (_, _) =>
        {
            // Restore original values
            localBlur = blurSaved;
            localTintOpacity = opacitySaved;
            localTintLuminosity = luminositySaved;
            localColorMode = colorModeSaved;
            FirePreview(); // revert preview to original values
            dlg.Close();
        };

        btnRow.Children.Add(cancelBtn);
        btnRow.Children.Add(saveBtn);
        Grid.SetRow(btnRow, row++);
        grid.Children.Add(btnRow);

        dlg.ShowDialog();

        // Copy locals back to ref params
        blurAmount = localBlur;
        tintOpacity = localTintOpacity;
        tintLuminosity = localTintLuminosity;
        colorMode = localColorMode;

        return saved;
    }

    private static Grid BuildSliderRow(string labelText, double min, double max, double value,
        Brush t1, Brush t2, Action<double, TextBlock> onChanged)
    {
        var grid = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });

        var label = new TextBlock
        {
            Text = labelText + ":",
            Foreground = t2, FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(label, 0);
        grid.Children.Add(label);

        var slider = new Slider
        {
            Minimum = min, Maximum = max, Value = value,
            TickFrequency = 5,
            Background = System.Windows.Media.Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.Hand,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(slider, 1);
        grid.Children.Add(slider);

        var valueLabel = new TextBlock
        {
            Text = max <= 60 ? $"{(int)value}" : $"{(int)value}%",
            Foreground = t1, FontSize = 12,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        };
        Grid.SetColumn(valueLabel, 2);
        grid.Children.Add(valueLabel);

        slider.ValueChanged += (s, _) => onChanged(slider.Value, valueLabel);

        return grid;
    }

    /// <summary>Backward-compat overload: delegates to <see cref="EnableBlur(Window, int, int, int, string)"/> with liquid glass defaults.</summary>
    [Obsolete("Use EnableBlur(window, blurAmount, tintOpacity, tintLuminosity, colorMode) instead.")]
    public static void EnableBlur(Window window, int __unused)
    {
        EnableBlur(window, 18, 50, 100, "Default");
    }

    /// <summary>Backward-compat wrapper: calls <see cref="EnableBlur(Window, int, int, int, string)"/> with default parameters. The hex argument is ignored; use the 5-arg overload to pass a color.</summary>
    public static void EnableAcrylicFromHex(Window window, string argbHex)
    {
        EnableBlur(window, 18, 50, 100, "Default");
    }

    // ── ContextMenu acrylic (Win11 视觉样式统一在 Resources/Controls/ContextMenu.xaml) ──
    //  这里只做一件事:菜单 Popup 打开时给 HWND 开 DWM 毛玻璃,关闭时关闭。
    //  不再在这里用代码构建 Style/Template —— 代码里 new DynamicResourceExtension()
    //  不会经过 XAML 解析器,是无效值,会把整套菜单样式在运行期静默丢弃。
    //  管理窗口(ManagementWindow)的菜单是自绘 Popup,不经过这里;IsManagementWindowMenu
    //  是最后一道保险。

    public static readonly DependencyProperty EnableContextMenuAcrylicProperty =
        DependencyProperty.RegisterAttached(
            "EnableContextMenuAcrylic",
            typeof(bool),
            typeof(AcrylicHelper),
            new PropertyMetadata(false, OnEnableContextMenuAcrylicChanged));

    public static void SetEnableContextMenuAcrylic(DependencyObject obj, bool value)
        => obj.SetValue(EnableContextMenuAcrylicProperty, value);

    public static bool GetEnableContextMenuAcrylic(DependencyObject obj)
        => (bool)obj.GetValue(EnableContextMenuAcrylicProperty);

    // ── 全局 ContextMenu 生命周期挂钩 ──
    // 之前的实现靠隐式样式里的 attached property 去订阅 Opened —— 但代码 new 出来、
    // 没有 PlacementTarget 的 ContextMenu(托盘菜单等)拿不到隐式样式,attached
    // property 永远不会被设置,兜底逻辑永远不执行 → 托盘菜单一直是系统默认浅色。
    // 改成全局 class handler:任何 ContextMenu 打开/关闭都会经过这里(先于实例
    // 事件,且不设置 Handled,不影响现有菜单自己的 Opened 处理)。
    // EnableContextMenuAcrylic 保留为样式标记,不再负责订阅。

    static void OnEnableContextMenuAcrylicChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // no-op: 全部工作由 EnsureGlobalContextMenuHook 的 class handler 完成。
    }

    static bool _globalContextMenuHookInstalled;

    /// <summary>应用启动时调用一次;对所有 ContextMenu(含托盘/代码创建)生效。</summary>
    public static void EnsureGlobalContextMenuHook()
    {
        if (_globalContextMenuHookInstalled) return;
        _globalContextMenuHookInstalled = true;
        EventManager.RegisterClassHandler(
            typeof(ContextMenu),
            ContextMenu.OpenedEvent,
            new RoutedEventHandler(OnAnyContextMenuOpened));
        EventManager.RegisterClassHandler(
            typeof(ContextMenu),
            ContextMenu.ClosedEvent,
            new RoutedEventHandler(OnAnyContextMenuClosed));
    }

    static void OnAnyContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;

        // 管理界面菜单不动:其页面用自绘 Popup(PageHelpers.RowContextMenu /
        // ManagementWindow.ShowMergedGroupContextMenuImpl),本就不走内置 ContextMenu;
        // 此处兜底跳过,防止未来管理窗口内部出现内置菜单时被误开毛玻璃。
        bool isManagement = IsManagementWindowMenu(menu);
#if DEBUG
        var firstItem = menu.Items.OfType<MenuItem>().FirstOrDefault();
        var firstSep = menu.Items.OfType<Separator>().FirstOrDefault();
        System.Diagnostics.Debug.WriteLine(
            $"[ContextMenu] opened cmStyle={(menu.Style != null)} cmTemplate={(menu.Template != null)} " +
            $"mgmt={isManagement} items={menu.Items.Count} " +
            $"miStyle={(firstItem?.Style != null)} miTemplate={(firstItem?.Template != null)} " +
            $"sepStyle={(firstSep?.Style != null)} sepTemplate={(firstSep?.Template != null)}");
#endif
        if (isManagement) return;

        // 每次打开都把调色板对一遍注册表(幂等、极轻):系统主题在菜单打开前
        // 恰好切换过也能保证这一屏就是正确主题。
        MenuThemeService.Apply();

        // ponytail 2026-08-28: 偶发旧色的根因 — WPF 复用已实现过的 ContextMenu
        // Popup 内容;上一次打开建立的 DynamicResource 引用在 Popup 关闭(断开)期间
        // 收不到资源变化通知,再打开时不一定重新解析。这里每次打开(菜单已连接)强制
        // 重挂样式/重设画刷 DP,让全部 DynamicResource 重新解析到刚写入的调色板。
        ForceRethemeMenu(menu);

        // 子菜单(新建 ▸ 等)的 PART_Popup 也要挂同样的圆角 + 毛玻璃。
        HookSubmenuPopups(menu);

        // 圆角 + 毛玻璃推迟到下一帧(Loaded 优先级)再挂:实例级 Opened 处理器
        // (如 ZoneWindow.ZoneMenu_Opened 会在打开时按映射状态切换 CtxPaste/
        // CtxPasteSep 的 Visibility)在 class handler 之后才跑 —— 立刻算区域会用
        // 尚未显示粘贴项的旧布局,首次右键时 Popup 已被裁成小一号,粘贴项就变成
        // 一块露出来的液态玻璃。等实例处理器跑完、重新布局后再取 DesiredSize。
        menu.Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                var src = PresentationSource.FromVisual(menu) as HwndSource;
                if (src != null && src.Handle != IntPtr.Zero)
                {
                    // 强制按当前模板/可见项布局一遍:框架内置编辑菜单默认模板带阴影
                    // 边距,Popup 窗口会比实际内容大;DesiredSize 才是真实内容尺寸,
                    // 按它裁区域就不会在菜单外露出多余液态玻璃。
                    menu.UpdateLayout();
                    // 布局后再补一次子菜单 hook — 首次打开时 MenuItem 模板可能尚未应用,
                    // mi.Template?.FindName("PART_Popup") 会拿到 null,子菜单就漏挂毛玻璃。
                    HookSubmenuPopups(menu);
                    ApplyMenuPopupEffects(src.Handle, menu);
                }
            }
            catch { }
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// ponytail 2026-08-28: 强制把已实现过的菜单重挂到当前调色板。WPF 会复用
    /// ContextMenu 的 Popup 内容,上次打开建立的 DynamicResource 引用在 Popup
    /// 关闭(断开)期间收不到资源变化通知,再打开时可能仍指向旧画刷实例 → 偶发
    /// 停在旧色。菜单打开时(已连接)重挂样式/重设 brush DP 会强制重新解析。
    /// ContextMenu 自身不重建模板(避免打断正在打开的 Popup),只重设三个 brush
    /// DP;MenuItem/Separator 重挂样式以重建模板内箭头/悬停/子菜单等 DynamicResource。
    /// </summary>
    static void ForceRethemeMenu(ContextMenu menu)
    {
        // ContextMenu 表面/边框/前景:直接重设 resource reference,TemplateBinding 立即跟进。
        menu.SetResourceReference(Control.BackgroundProperty, "Menu.Bg.Surface");
        menu.SetResourceReference(Control.BorderBrushProperty, "Menu.Border.Subtle");
        menu.SetResourceReference(Control.ForegroundProperty, "Menu.Text.Primary");

        // 代码 new 出来、尚未挂到任何元素的 ContextMenu(如托盘菜单)可能错过隐式样式,
        // 补一次;已有样式的不覆盖(SetResourceReference 已覆盖表面三件套)。
        if (menu.Style == null)
            menu.Style = Application.Current.TryFindResource(typeof(ContextMenu)) as Style;

        ForceRethemeItems(menu.Items);
    }

    static void ForceRethemeItems(ItemCollection items)
    {
        var miStyle = Application.Current.TryFindResource(typeof(MenuItem)) as Style;
        var sepKeyStyle = Application.Current.TryFindResource(MenuItem.SeparatorStyleKey) as Style;
        foreach (var item in items)
        {
            if (item is MenuItem mi)
            {
                // null → 原样式 强制重挂,重建模板内 DynamicResource(箭头/悬停/子菜单)。
                if (miStyle != null)
                {
                    mi.Style = null;
                    mi.Style = miStyle;
                }
                ForceRethemeItems(mi.Items);
            }
            else if (item is Separator sep && sepKeyStyle != null)
            {
                // Separator 必须用 SeparatorStyleKey 那份(否则回到主题样式自带侧边距)。
                sep.Style = null;
                sep.Style = sepKeyStyle;
            }
        }
    }

    static void OnAnyContextMenuClosed(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        try
        {
            var src = PresentationSource.FromVisual(menu) as HwndSource;
            if (src != null && src.Handle != IntPtr.Zero)
                DisableBlur(src.Handle);
        }
        catch { }
    }

    // ── 菜单弹层的圆角与毛玻璃(主菜单 + 子菜单共用) ──
    // 之前「尖角裁剪不干净」的根因是:WCA_ACCENT_POLICY 的 acrylic 背板按整个
    // Popup 窗口矩形生效,圆角菜单外面会露出方形的模糊/染色角;XAML 里的
    // CornerRadius 只能裁 WPF 自己画的表面,裁不到 DWM 背板。这里三层保险:
    //   1) SetWindowRgn 把 Popup 窗口本身裁成圆角矩形 → DWM 背板跟着被裁;
    //   2) 模糊区域(DWM_BB_BLURREGION)用同样的圆角矩形;
    //   3) DWMWA_WINDOW_CORNER_PREFERENCE=ROUND 让 Win11 原生把窗口角切圆
    //      (旧系统该调用失败,自动忽略)。
    static void ApplyMenuPopupEffects(IntPtr hwnd, FrameworkElement? sizeHint = null)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            uint dpi = GetDpiForWindow(hwnd);
            if (dpi < 96) dpi = 96;
            double scale = dpi / 96.0;

            // 优先用内容 DesiredSize(真实菜单尺寸);拿不到再用整窗矩形兜底。
            int w = 0, h = 0;
            if (sizeHint != null)
            {
                w = (int)Math.Round(sizeHint.DesiredSize.Width * scale);
                h = (int)Math.Round(sizeHint.DesiredSize.Height * scale);
            }
            if (w <= 0 || h <= 0)
            {
                GetWindowRect(hwnd, out var r);
                w = r.Right - r.Left;
                h = r.Bottom - r.Top;
            }
            if (w <= 0 || h <= 0) return;
            int rad = (int)Math.Round(8 * dpi / 96.0);

            ApplyMenuSurfaceEffects(hwnd, w, h, rad);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AcrylicHelper] ApplyMenuPopupEffects: {ex}");
        }
    }

    /// <summary>
    /// 给「跟随系统深浅色」的普通 Window（重命名 / 导入系统项目 / 取色器等二级弹窗）
    /// 挂上跟分区右键菜单完全相同的圆角 + DWM 毛玻璃配方，保证菜单与弹窗的颜色和
    /// 透明度对齐。与 Popup 版共用同一套核心 <see cref="ApplyMenuSurfaceEffects"/>。
    /// </summary>
    public static void ApplyMenuSurface(Window window, int cornerRadius = 8)
    {
        window.Loaded += OnLoaded;
        void OnLoaded(object? s, RoutedEventArgs e)
        {
            window.Loaded -= OnLoaded;
            try
            {
                var src = PresentationSource.FromVisual(window) as HwndSource;
                if (src == null || src.Handle == IntPtr.Zero) return;

                uint dpi = GetDpiForWindow(src.Handle);
                if (dpi < 96) dpi = 96;
                double scale = dpi / 96.0;

                int w, h;
                if (window.ActualWidth > 0 && window.ActualHeight > 0)
                {
                    w = (int)Math.Round(window.ActualWidth * scale);
                    h = (int)Math.Round(window.ActualHeight * scale);
                }
                else
                {
                    GetWindowRect(src.Handle, out var r);
                    w = r.Right - r.Left;
                    h = r.Bottom - r.Top;
                }
                int rad = (int)Math.Round(cornerRadius * scale);
                ApplyMenuSurfaceEffects(src.Handle, w, h, rad);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AcrylicHelper] ApplyMenuSurface: {ex}");
            }
        }
    }

    /// <summary>
    /// 给「跟随系统深浅色」的 Popup（如便签颜色菜单）挂上与右键菜单完全相同的
    /// 圆角 + DWM 毛玻璃配方，消除「菜单是磨砂、弹出菜单是另一透明度」的差异。
    /// </summary>
    public static void ApplyMenuSurfaceToPopup(Popup popup, int cornerRadius = 6)
    {
        popup.Opened += OnOpened;
        void OnOpened(object? s, EventArgs e)
        {
            popup.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (!popup.IsOpen || popup.Child == null) return;
                    var src = PresentationSource.FromVisual(popup.Child) as HwndSource;
                    if (src == null || src.Handle == IntPtr.Zero) return;

                    uint dpi = GetDpiForWindow(src.Handle);
                    if (dpi < 96) dpi = 96;
                    double scale = dpi / 96.0;

                    var fe = popup.Child as FrameworkElement;
                    int w = 0, h = 0;
                    if (fe != null)
                    {
                        w = (int)Math.Round(fe.DesiredSize.Width * scale);
                        h = (int)Math.Round(fe.DesiredSize.Height * scale);
                    }
                    if (w <= 0 || h <= 0)
                    {
                        GetWindowRect(src.Handle, out var r);
                        w = r.Right - r.Left;
                        h = r.Bottom - r.Top;
                    }
                    int rad = (int)Math.Round(cornerRadius * scale);
                    ApplyMenuSurfaceEffects(src.Handle, w, h, rad);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[AcrylicHelper] ApplyMenuSurfaceToPopup: {ex}");
                }
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    /// <summary>圆角裁剪 + 模糊背板 + Win11 圆角偏好 + 磨砂 accent（主菜单 / 子菜单 / 系统弹窗共用）。</summary>
    static void ApplyMenuSurfaceEffects(IntPtr hwnd, int w, int h, int rad)
    {
        if (hwnd == IntPtr.Zero || w <= 0 || h <= 0) return;
        try
        {
            // 1) 窗口级圆角裁剪。SetWindowRgn 会接管 hRgn 的所有权,不能 DeleteObject。
            SetWindowRgn(hwnd, CreateRoundRectRgn(0, 0, w, h, rad, rad), true);

            // 2) 模糊背板用同样的圆角区域(调用返回后要自己 DeleteObject)。
            var bb = new DWM_BLURBEHIND
            {
                dwFlags = DWM_BB_ENABLE | DWM_BB_BLURREGION,
                fEnable = true,
                hRgnBlur = CreateRoundRectRgn(0, 0, w, h, rad, rad)
            };
            DwmEnableBlurBehindWindow(hwnd, ref bb);
            DeleteObject(bb.hRgnBlur);

            // 3) Win11 原生圆角窗口偏好。
            try
            {
                int pref = DWMWCP_ROUND;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
            }
            catch { }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AcrylicHelper] ApplyMenuSurfaceEffects: {ex}");
        }

        // 4) 保持原有的 acrylic accent(磨砂质感;tint 透明,只取模糊)。
        TrySetAccent(hwnd, ACCENT_ENABLE_ACRYLICBLURBEHIND, (30 << 8) | 0x100, 0);
    }

    // ── 子菜单 PART_Popup 生命周期挂钩 ──
    // MenuItem 的子菜单是模板里的 Popup(不是 ContextMenu),隐式样式管不到它的
    // DWM 效果;这里在菜单打开时把每个带子项的 MenuItem 的 PART_Popup 找出来,
    // 在 Popup 打开/关闭时套用与主菜单完全相同的圆角 + 毛玻璃配方。
    static void HookSubmenuPopups(ContextMenu menu)
    {
        foreach (var item in menu.Items)
            if (item is MenuItem mi && mi.HasItems)
                HookMenuItemSubmenuPopup(mi);
    }

    static void HookMenuItemSubmenuPopup(MenuItem mi)
    {
        var popup = mi.Template?.FindName("PART_Popup", mi) as Popup;
        if (popup == null) return;
        popup.Opened -= OnSubmenuPopupOpened;
        popup.Opened += OnSubmenuPopupOpened;
        popup.Closed -= OnSubmenuPopupClosed;
        popup.Closed += OnSubmenuPopupClosed;
    }

    static void OnSubmenuPopupOpened(object? sender, EventArgs e)
    {
        if (sender is not Popup popup) return;
        try
        {
            var src = PresentationSource.FromVisual(popup.Child) as HwndSource;
            if (src != null && src.Handle != IntPtr.Zero)
                ApplyMenuPopupEffects(src.Handle, popup.Child as FrameworkElement);
            else
                // Popup 刚打开时 HwndSource 偶尔还没就绪,下一帧补一次。
                popup.Dispatcher.BeginInvoke(new Action(() =>
                {
                    var src2 = PresentationSource.FromVisual(popup.Child) as HwndSource;
                    if (src2 != null && src2.Handle != IntPtr.Zero)
                        ApplyMenuPopupEffects(src2.Handle, popup.Child as FrameworkElement);
                }), System.Windows.Threading.DispatcherPriority.Loaded);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AcrylicHelper] OnSubmenuPopupOpened: {ex}");
        }

        // 更深层级的子菜单此时才实例化容器,递归再挂一层。
        if (popup.TemplatedParent is MenuItem parent)
            foreach (var item in parent.Items)
                if (item is MenuItem nested && nested.HasItems)
                    HookMenuItemSubmenuPopup(nested);
    }

    static void OnSubmenuPopupClosed(object? sender, EventArgs e)
    {
        if (sender is not Popup popup) return;
        try
        {
            var src = PresentationSource.FromVisual(popup.Child) as HwndSource;
            if (src != null && src.Handle != IntPtr.Zero)
                DisableBlur(src.Handle);
        }
        catch { }
    }

    /// <summary>判断菜单是否属于 ManagementWindow(含其内部页面/控件)。</summary>
    static bool IsManagementWindowMenu(ContextMenu menu)
    {
        try
        {
            var src = menu.PlacementTarget ?? VisualTreeHelper.GetParent(menu) as Visual;
            while (src != null)
            {
                if (src is Window w)
                    return w is Views.ManagementWindow;
                src = VisualTreeHelper.GetParent(src) as Visual;
            }
        }
        catch { }
        return false;
    }
}
