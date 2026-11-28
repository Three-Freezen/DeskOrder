using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DesktopZones.Helpers;

/// <summary>
/// 右键菜单图标（方案 B · 简笔 LESS）的工厂 —— 只做三件事：从资源里取几何造一枚
/// 16×16 的 <see cref="Path"/>、造一枚文字字形图标、造一个撑列宽的透明占位。
///
/// 颜色一律由样式里的 <c>DynamicResource Menu.Text.Secondary</c> 决定：右键菜单是
/// **复刻 Win11 原生菜单**的（见 Resources/Controls/ContextMenu.xaml），底色跟着系统
/// 主题在深/浅之间翻转，不跟应用主题；所以图标不能写死颜色，也不能用品牌蓝。
///
/// 尺寸口径（对齐 ContextMenu.xaml 的 MenuItem 模板）：图标列 <b>24px</b> =
/// 图标 <b>16×16</b> + 间隔 <b>8px</b>，文字从 32 起。2026-11-28 之前模板里是
/// 20×20 + 8 = 28 > 24，图标会向右压 4px 到文字上，这次一并修了。
///
/// 设计稿与生成器：UI重构/menu-icons/（svg/02-B.svg、DESIGN.md）。
/// </summary>
public static class MenuIconFactory
{
    /// <summary>取 <c>Menu.*</c> 几何，套上菜单图标样式。<paramref name="styleKey"/> 默认
    /// <c>MenuItemIcon</c>；删除项传 <c>MenuItemIconDanger</c>（#FF6666，与菜单里现有的
    /// <c>Foreground="#FF6666"</c> 一致）。找不到资源时返回 null，调用方保持无图标即可。</summary>
    public static object? Icon(string geometryKey, string styleKey = "MenuItemIcon")
    {
        if (Application.Current?.TryFindResource(geometryKey) is not Geometry geo) return null;
        var path = new Path { Data = geo };
        if (Application.Current.TryFindResource(styleKey) is Style style) path.Style = style;
        return path;
    }

    /// <summary>文字字形图标：菜单里的「设置」用 ⚙、「锁定 / 解锁」用 🔓 / 🔒，
    /// 与分区标题栏那两颗按钮**同一个字符**（ZoneWindow 的 EditBtnText / LockBtnText），
    /// 这样菜单和标题栏一眼就是同一个东西。字号由 MenuItemGlyph 样式统一给。</summary>
    public static object Glyph(string text)
    {
        var tb = new TextBlock { Text = text };
        if (Application.Current?.TryFindResource("MenuItemGlyph") is Style style) tb.Style = style;
        return tb;
    }

    /// <summary>16×16 透明占位。同一个菜单里的图标要么全给、要么全不给，否则文字左边缘
    /// 会参差 —— 「语言」子菜单是整体无图标的，用它把图标列撑住（勾选标记独占该列）。</summary>
    public static object Blank() => new Border { Width = 16, Height = 16, Background = Brushes.Transparent };
}
