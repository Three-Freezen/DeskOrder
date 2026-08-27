using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DesktopZones.Helpers;

/// <summary>
/// 图标字符串 ↔ 原生矢量图标的统一解析。
///
/// 图标字段（Zone.IconChar / MergedGroupMembership.Icon / 三个小组件.IconChar）
/// 现在同时承载两类值：
///   1. 普通 emoji —— 原样存储，如 "🎮"、"📁"（用户手输，最多 2 个字符）；
///   2. 原生图标 —— 以 "@" 前缀的 token 存储，如 "@zones"、"@merged"，
///      运行时解析为 Resources/Icons.xaml 里的同名 Geometry。
///
/// 这样既保留旧的纯字符串存储（JSON 无迁移），又能让管理界面那套
/// 矢量图标（田字/组合/面板/时钟/日历/便签/设置）作为预设或默认值使用。
/// </summary>
public static class IconGlyph
{
    public const string Prefix = "@";

    public const string Zones    = "@zones";
    public const string Merged   = "@merged";
    public const string Panel    = "@panel";
    public const string Clock    = "@clock";
    public const string Calendar = "@calendar";
    public const string Sticky   = "@sticky";
    public const string Settings = "@settings";

    /// <summary>图标预设（25 个）：前 7 个为软件原生矢量图标，后 18 个为常用 emoji。</summary>
    public static readonly string[] PresetIcons =
    {
        Zones, Merged, Panel, Clock, Calendar, Sticky, Settings,
        "🎮", "💬", "🌐", "📁", "📝", "📊", "📈", "🎵", "🎬", "📷",
        "🛒", "✉️", "🔒", "⚙️", "🧮", "🗂️", "💻", "📱",
    };

    /// <summary>是否为原生矢量图标 token（"@zones" 等）。</summary>
    public static bool IsNative(string? icon)
        => !string.IsNullOrEmpty(icon) && icon.StartsWith(Prefix, System.StringComparison.Ordinal);

    /// <summary>原生 token → XAML Geometry 资源键；未知/非原生返回 null。</summary>
    public static string? ResourceKey(string? icon) => icon switch
    {
        Zones    => "Icon.Zones",
        Merged   => "Icon.Merged",
        Panel    => "Icon.Panel",
        Clock    => "Icon.Clock",
        Calendar => "Icon.Calendar",
        Sticky   => "Icon.Sticky",
        Settings => "Icon.Settings",
        _ => null,
    };

    /// <summary>解析原生图标的 Geometry；解析失败返回 null。</summary>
    public static Geometry? GetGeometry(string? icon)
    {
        var key = ResourceKey(icon);
        if (key == null || Application.Current == null) return null;
        return Application.Current.TryFindResource(key) as Geometry;
    }

    /// <summary>分区/组合/小组件的默认原生图标（图标为空时的兜底）。</summary>
    public static string DefaultFor(string kind) => kind switch
    {
        "zone"   => Zones,
        "merged" => Merged,
        "clock"  => Clock,
        "calendar" => Calendar,
        "sticky" => Sticky,
        _ => Zones,
    };

    /// <summary>
    /// 把图标内容刷到一个 TextBlock + Path 组合上（XAML 里的成对宿主）。
    /// 原生图标走 Path(Stroke)，emoji 走 TextBlock；两者只显示其一。
    /// 颜色二选一：<paramref name="brush"/> 为一次性本地画刷；<paramref name="colorResourceKey"/>
    /// 为动态资源键（如 Menu.Text.Primary），用 SetResourceReference 挂上以便随系统主题实时切换。
    /// </summary>
    public static void Apply(TextBlock? textHost, Path? pathHost, string? icon, Brush? brush, double pathSize, string? colorResourceKey = null)
    {
        var geo = GetGeometry(icon);
        if (geo != null)
        {
            if (textHost != null)
            {
                textHost.Text = "";
                textHost.Visibility = Visibility.Collapsed;
            }
            if (pathHost != null)
            {
                pathHost.Data = geo;
                pathHost.Width = pathHost.Height = pathSize;
                ApplyColor(pathHost, Shape.StrokeProperty, brush, colorResourceKey);
                pathHost.Visibility = Visibility.Visible;
            }
        }
        else
        {
            if (textHost != null)
            {
                textHost.Text = icon ?? "";
                ApplyColor(textHost, TextBlock.ForegroundProperty, brush, colorResourceKey);
                textHost.Visibility = string.IsNullOrEmpty(icon) ? Visibility.Collapsed : Visibility.Visible;
            }
            if (pathHost != null) pathHost.Visibility = Visibility.Collapsed;
        }
    }

    static void ApplyColor(FrameworkElement host, DependencyProperty dp, Brush? brush, string? colorResourceKey)
    {
        if (colorResourceKey != null) host.SetResourceReference(dp, colorResourceKey);
        else if (brush != null) host.SetValue(dp, brush);
    }

    /// <summary>
    /// 生成一个用于代码构建列表/按钮的图标元素（原生 → Path，emoji → TextBlock）。
    /// 空图标返回 null，让调用方决定是否显示兜底。
    /// </summary>
    public static FrameworkElement? CreateIcon(string? icon, Brush brush, double fontSize = 14, double pathSize = 14)
    {
        var geo = GetGeometry(icon);
        if (geo != null)
        {
            return new Path
            {
                Data = geo,
                Width = pathSize,
                Height = pathSize,
                Stretch = Stretch.Uniform,
                Stroke = brush,
                StrokeThickness = 1.5,
                StrokeLineJoin = PenLineJoin.Round,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
            };
        }
        if (string.IsNullOrEmpty(icon)) return null;
        return new TextBlock
        {
            Text = icon,
            FontSize = fontSize,
            Foreground = brush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        };
    }
}
