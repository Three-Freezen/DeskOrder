using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace DesktopZones.Helpers;

/// <summary>
/// Windows 10/11 右键菜单调色板服务。
/// 让「非管理界面」的右键菜单 100% 跟随 Windows 系统主题(浅色/深色/高对比),
/// 而不是跟随应用自己的主题 —— 应用主题切换(Task 5)只影响管理界面,
/// 桌面右键菜单保持和系统资源管理器一致的原生观感。
///
/// 实现: 运行期改写 Resources/Controls/ContextMenu.xaml 里的 Menu.* 画刷。
/// 样式里全部用 {DynamicResource Menu.*},字典条目替换后 WPF 自动重解析,
/// 无需重建任何菜单实例。
///
/// 监听(三保险,确保深/浅色切换实时同步):
///   1) SystemEvents.UserPreferenceChanged — General/Accessibility/VisualStyle/Color。
///      Win11 深浅色切换广播 "ImmersiveColorSet",映射为 VisualStyle 类别;
///      之前只监听 General/Accessibility,切深色后菜单不会实时刷新。
///   2) SystemParameters.StaticPropertyChanged(HighContrast 切换)。
///   3) 每 2 秒对一次注册表 — 事件漏发也能在 2 秒内补上。
/// 另外 AcrylicHelper 在每个菜单打开时也会调一次 Apply()(幂等),保证
/// 菜单打开那一刻一定是当前系统主题的调色板。
/// </summary>
public static class MenuThemeService
{
    public enum MenuPalette { Light, Dark, HighContrast }

    static MenuPalette _current = MenuPalette.Light;
    static System.Windows.Threading.DispatcherTimer? _pollTimer;

    /// <summary>启动时调用一次;之后监听系统主题变化自动刷新。</summary>
    public static void Start()
    {
        Apply();

        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General
                or UserPreferenceCategory.Accessibility
                or UserPreferenceCategory.VisualStyle
                or UserPreferenceCategory.Color)
                MarshalAndApply();
        };

        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemParameters.HighContrast))
                MarshalAndApply();
        };

        // 兜底轮询:事件链路在个别 Windows 版本/情况下可能不触发,
        // 每 2 秒读一次注册表,主题一变立刻跟上。
        _pollTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _pollTimer.Tick += (_, _) => { try { Apply(); } catch { } };
        _pollTimer.Start();
    }

    static void MarshalAndApply()
    {
        var app = Application.Current;
        if (app == null) return;
        if (app.Dispatcher.CheckAccess()) Apply();
        else app.Dispatcher.BeginInvoke(new Action(Apply));
    }

    /// <summary>读取 Windows 当前主题状态:高对比优先,其次 AppsUseLightTheme。</summary>
    static MenuPalette ReadWindowsPalette()
    {
        if (SystemParameters.HighContrast) return MenuPalette.HighContrast;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is 0
                ? MenuPalette.Dark
                : MenuPalette.Light;
        }
        catch
        {
            return MenuPalette.Light;
        }
    }

    public static void Apply()
    {
        var palette = ReadWindowsPalette();
        if (palette == _current && AlreadyApplied()) return;
        _current = palette;

        var dict = FindContextMenuDictionary();
        if (dict == null) return;

        // Windows 11 原生菜单近似调色板(浅色/深色/高对比三套)。
        // 表面保留 ~75% 不透明度,配合 OnCmOpened 的 DWM acrylic 呈现磨砂。
        (Color surface, Color hover, Color border, Color textPrimary,
         Color textSecondary, Color textTertiary, Color textDisabled, Color separator) =
            palette switch
            {
                MenuPalette.Dark => (
                    Color.FromArgb(0xC0, 0x2B, 0x2B, 0x2B),
                    Color.FromArgb(0xFF, 0x3A, 0x3A, 0x3A),
                    Color.FromArgb(0xFF, 0x45, 0x45, 0x45),
                    Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
                    Color.FromArgb(0xFF, 0xC0, 0xC0, 0xC0),
                    Color.FromArgb(0xFF, 0x8A, 0x8A, 0x8A),
                    Color.FromArgb(0xFF, 0x6E, 0x6E, 0x6E),
                    Color.FromArgb(0xFF, 0x33, 0x33, 0x33)),   // 分割线 = 样式设置界面同款 Color.Border.Subtle
                MenuPalette.HighContrast => (
                    Color.FromArgb(0xFF, 0x00, 0x00, 0x00),
                    Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A),
                    Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
                    Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
                    Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
                    Color.FromArgb(0xFF, 0xC0, 0xC0, 0xC0),
                    Color.FromArgb(0xFF, 0x80, 0x80, 0x80),
                    Color.FromArgb(0xFF, 0x80, 0x80, 0x80)),   // 分割线 = 样式设置界面同款 Color.Border.Subtle
                _ => (
                    Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF),
                    Color.FromArgb(0xFF, 0xF5, 0xF5, 0xF5),
                    Color.FromArgb(0xFF, 0xE8, 0xE8, 0xE8),
                    Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A),
                    Color.FromArgb(0xFF, 0x66, 0x66, 0x66),
                    Color.FromArgb(0xFF, 0x9A, 0x9A, 0x9A),
                    Color.FromArgb(0xFF, 0xAB, 0xAB, 0xAB),
                    Color.FromArgb(0xFF, 0xE4, 0xE7, 0xEC)),   // 分割线 = 样式设置界面同款 Color.Border.Subtle
            };

        dict["Menu.Bg.Surface"]     = new SolidColorBrush(surface);
        dict["Menu.Bg.Hover"]       = new SolidColorBrush(hover);
        dict["Menu.Border.Subtle"]  = new SolidColorBrush(border);
        dict["Menu.Text.Primary"]   = new SolidColorBrush(textPrimary);
        dict["Menu.Text.Secondary"] = new SolidColorBrush(textSecondary);
        dict["Menu.Text.Tertiary"]  = new SolidColorBrush(textTertiary);
        dict["Menu.Text.Disabled"]  = new SolidColorBrush(textDisabled);
        dict["Menu.Separator"]      = new SolidColorBrush(separator);

        System.Diagnostics.Debug.WriteLine($"[MenuTheme] palette={palette}");
    }

    static bool AlreadyApplied()
    {
        var dict = FindContextMenuDictionary();
        return dict != null && dict.Contains("Menu.Bg.Surface");
    }

    static ResourceDictionary? FindContextMenuDictionary()
    {
        var app = Application.Current;
        if (app == null) return null;
        return app.Resources.MergedDictionaries
            .Cast<ResourceDictionary>()
            .FirstOrDefault(d => d.Source?.OriginalString
                .EndsWith("ContextMenu.xaml", StringComparison.OrdinalIgnoreCase) == true);
    }
}
