using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

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

    // AccentState values
    private const int ACCENT_DISABLED = 0;
    private const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;

    // ── Color presets ──

    public static readonly IReadOnlyList<string> ColorPresetNames = new[]
    {
        "Default", "Accent", "GlassWhite", "MistGrey", "DeepBlack",
        "OceanBlue", "AuroraCyan", "RosePink", "BordeauxRed", "ForestGreen",
        "RoyalPurple", "SunsetOrange", "ChampagneGold", "MorandiSage"
    };

    public static readonly IReadOnlyDictionary<string, string> ColorPresetNamesCN = new Dictionary<string, string>
    {
        ["Default"] = "默认",
        ["Accent"] = "跟随系统",
        ["GlassWhite"] = "玻璃白",
        ["MistGrey"] = "薄雾灰",
        ["DeepBlack"] = "深邃黑",
        ["OceanBlue"] = "海洋蓝",
        ["AuroraCyan"] = "极光青",
        ["RosePink"] = "玫瑰粉",
        ["BordeauxRed"] = "波尔多红",
        ["ForestGreen"] = "森林绿",
        ["RoyalPurple"] = "皇家紫",
        ["SunsetOrange"] = "日落橙",
        ["ChampagneGold"] = "香槟金",
        ["MorandiSage"] = "莫兰迪灰绿"
    };

    /// <summary>
    /// Resolve a color mode name to an ARGB tint color (0xAARRGGBB).
    /// The caller will apply tintOpacity (alpha) and tintLuminosity (brightness).
    /// </summary>
    private static uint ResolveBaseColorARGB(string colorMode)
    {
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
    private static readonly Dictionary<Window, (int blur, int opacity, int lum, string mode)> _registered = new();

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
                EnableBlur(window, settings.blur, settings.opacity, settings.lum, settings.mode);
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
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return BlurResult.Fail("Window handle not created yet");

        // ponytail: remember (window → settings) so OnSystemAccentChanged can re-apply
        // when the system accent changes. Override existing entry if EnableBlur is
        // called again with different params (e.g. user edited settings live).
        _registered[window] = (blurAmount, tintOpacity, tintLuminosity, colorMode);

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
            DisableBlur(window);
            return BlurResult.Ok;
        }

        // Calculate gradient color from color mode + tint settings
        int gradientColor = ResolveGlassTintColor(colorMode, tintOpacity, tintLuminosity);

        // AccentFlags encodes blur radius: bits 8-15 carry the radius, bits 0-7 carry style flags
        int accentFlags = (Math.Clamp(blurAmount, 1, 60) << 8) | 0x100;

        // Primary: DWM Blur Behind (works with AllowsTransparency)
        var primary = TryBlurBehind(hwnd, true);

        // Secondary: Win10+ acrylic accent for stronger / varied effect
        var secondary = TrySetAccent(hwnd, ACCENT_ENABLE_ACRYLICBLURBEHIND, accentFlags, gradientColor);

        // Blur is considered enabled if either path worked.
        if (primary.Success || secondary.Success) return BlurResult.Ok;
        return BlurResult.Fail(primary.Error ?? secondary.Error ?? "unknown");
    }

    /// <summary>Disable blur on a window.</summary>
    public static BlurResult DisableBlur(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return BlurResult.Fail("Window handle not created yet");

        // ponytail: drop the registry entry so OnSystemAccentChanged doesn't try to
        // re-apply to a torn-down window.
        _registered.Remove(window);

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

        var separator = new Border
        {
            Height = 1,
            Background = new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF)),
            Margin = new Thickness(12, 0, 12, 0)
        };

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
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // buttons

        var t1 = (Brush)Application.Current.FindResource("Brush.Text.Primary");
        var t2 = (Brush)Application.Current.FindResource("Brush.Text.Secondary");
        var accent = (Brush)Application.Current.FindResource("Brush.Accent.Solid");
        var ibg = (Brush)Application.Current.FindResource("Brush.Bg.Input");
        var ibd = (Brush)Application.Current.FindResource("Brush.Border.Subtle");

        int row = 0;

        // Title
        var titleTb = new TextBlock
        {
            Text = isChinese ? "液态玻璃设置" : "Liquid Glass Settings",
            FontSize = 16, FontWeight = FontWeights.SemiBold,
            Foreground = t1, Margin = new Thickness(0, 0, 0, 16)
        };
        Grid.SetRow(titleTb, row++);
        grid.Children.Add(titleTb);

        // Blur Amount slider (0-60)
        var blurSaved = localBlur;
        var blurLabelRow = BuildSliderRow(isChinese ? "模糊半径" : "Blur Radius", 0, 60, localBlur,
            t1, t2, (v, lbl) => { localBlur = (int)v; lbl.Text = $"{(int)v}"; FirePreview(); });
        Grid.SetRow(blurLabelRow, row++);
        grid.Children.Add(blurLabelRow);

        // Tint Opacity slider (0-100%)
        var opacitySaved = localTintOpacity;
        var opacityLabelRow = BuildSliderRow(isChinese ? "着色不透明度" : "Tint Opacity", 0, 100, localTintOpacity,
            t1, t2, (v, lbl) => { localTintOpacity = (int)v; lbl.Text = $"{localTintOpacity}%"; FirePreview(); });
        Grid.SetRow(opacityLabelRow, row++);
        grid.Children.Add(opacityLabelRow);

        // Tint Luminosity slider (0-150%)
        var luminositySaved = localTintLuminosity;
        var luminosityLabelRow = BuildSliderRow(isChinese ? "着色亮度" : "Tint Luminosity", 0, 150, localTintLuminosity,
            t1, t2, (v, lbl) => { localTintLuminosity = (int)v; lbl.Text = $"{localTintLuminosity}%"; FirePreview(); });
        Grid.SetRow(luminosityLabelRow, row++);
        grid.Children.Add(luminosityLabelRow);

        // Color preset dropdown
        var colorRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 12, 0, 0)
        };
        colorRow.Children.Add(new TextBlock
        {
            Text = isChinese ? "颜色预设:" : "Color Preset:",
            Foreground = t2, FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 100
        });
        var presetCombo = ComboBoxHelper.Create(width: 220, fontSize: 12,
            margin: new Thickness(8, 0, 0, 0));
        // ponytail: implicit Style from Controls/ComboBox.xaml sets Foreground to
        // {DynamicResource Brush.Text.Primary} — leave it alone, don't bypass with a
        // local brush reference (which would freeze the brush and skip theme follow).
        int selectedIdx = 0;
        for (int i = 0; i < ColorPresetNames.Count; i++)
        {
            string displayName = isChinese && ColorPresetNamesCN.TryGetValue(ColorPresetNames[i], out var cnName)
                ? $"{ColorPresetNames[i]} ({cnName})"
                : ColorPresetNames[i];
            presetCombo.Items.Add(displayName);
            if (ColorPresetNames[i] == colorMode) selectedIdx = i;
        }
        presetCombo.SelectedIndex = selectedIdx;
        presetCombo.SelectionChanged += (_, _) => { localColorMode = ColorPresetNames[presetCombo.SelectedIndex]; FirePreview(); };
        colorRow.Children.Add(presetCombo);
        Grid.SetRow(colorRow, row++);
        grid.Children.Add(colorRow);

        // Hint text
        var hintTb = new TextBlock
        {
            Text = isChinese
                ? "模糊半径=0 关闭效果 | 亮度100%=原始色彩 | 预设决定基础色调"
                : "Blur=0 disables | Luminosity 100%=original | Preset selects base tint",
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
            Content = isChinese ? "取消" : "Cancel",
            Width = 80, Height = 32,
            Background = ibg, Foreground = t2,
            BorderBrush = ibd, BorderThickness = new Thickness(1),
            FontSize = 12, Cursor = System.Windows.Input.Cursors.Hand,
            Margin = new Thickness(0, 0, 8, 0)
        };
        var saveBtn = new Button
        {
            Content = isChinese ? "保存" : "Save",
            Width = 80, Height = 32,
            Background = accent, Foreground = System.Windows.Media.Brushes.White,
            BorderThickness = new Thickness(0),
            FontSize = 12, Cursor = System.Windows.Input.Cursors.Hand,
            FontWeight = FontWeights.SemiBold
        };

        bool saved = false;
        saveBtn.Click += (_, _) =>
        {
            localColorMode = ColorPresetNames[presetCombo.SelectedIndex];
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
}
