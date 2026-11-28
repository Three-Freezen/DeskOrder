using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace DesktopZones.Helpers;

/// <summary>
/// 右键菜单图标（方案 B · 简笔 LESS）的挂载器 —— 分区 / 快捷栏 / 项目 / 次级分区 /
/// 时钟 / 日历 / 媒体播放器 / 便签 / 面板 / 插件组件，所有右键菜单共用这一份映射。
///
/// 规矩（改之前先读一遍）：
///  · 同一个菜单里图标要么全给、要么全不给，否则文字左边缘会参差；
///  · 「设置」「锁定」「置顶」不画几何 —— 直接用各组件标题栏上那几颗按钮的字符
///    （⚙ / 🔓 🔒 / 📌），保证菜单和标题栏一眼是同一个东西；
///  · 「12 / 24 小时制」用七段数码管画的「12」「24」，跟着 Header 一起换
///    （画的是图片不是字，不会和标签读成一串）；
///  · 删除项走 MenuItemIconDanger（#FF6666，与菜单里现有的 Foreground 一致）。
///
/// 挂载方式有两种，按菜单怎么打开来选：
///  · ContextMenuService 打开的（元素上挂着 ContextMenu，右键自动弹）→ <see cref="Hook"/>；
///  · 代码里手动 IsOpen = true 的 → 打开前调一次 <see cref="Apply"/>，或建项时就给 Icon。
///
/// 幂等：已经有图标的项不动，所以菜单反复打开、被多条路径同时触发都不会越叠越多。
/// </summary>
public static class MenuIconBinder
{
    /// <summary>菜单项 x:Name → 图标几何键。键名取菜单项的 x:Name，是因为同一批
    /// 菜单项会在多个 DataTemplate 里重复出现（分区 / 快捷栏 / 次级分区各一套，
    /// 各组件窗口也各有一份同名项），按名字查表正好一次覆盖全部实例。</summary>
    static readonly Dictionary<string, string> Map = new(StringComparer.Ordinal)
    {
        // ── 分区菜单 ──
        ["CtxDockAddApp"] = "Menu.AddApp",
        ["CtxImport"] = "Menu.Import",
        ["CtxImportFiles"] = "Menu.File",
        ["CtxImportFolder"] = "Menu.Folder",
        ["CtxImportShell"] = "Menu.Monitor",
        ["CtxNewSubZone"] = "Menu.NewSubZone",
        ["CtxNew"] = "Menu.New",
        ["CtxNewFolder"] = "Menu.Folder",
        ["CtxNewTxt"] = "Menu.NewTxt",
        ["CtxNewDocx"] = "Menu.NewDocx",
        ["CtxNewPptx"] = "Menu.NewPptx",
        ["CtxNewXlsx"] = "Menu.NewXlsx",
        ["CtxMapFolderNew"] = "Menu.MapFolder",
        ["CtxReleaseFromBay"] = "Menu.Release",
        ["CtxDisbandAll"] = "Menu.DisbandAll",
        ["CtxDisbandThis"] = "Menu.DisbandSingle",
        ["CtxMinimize"] = "Menu.Minimize",
        ["CtxFullHide"] = "Menu.Hide",
        ["CtxHide"] = "Menu.Hide",
        ["CtxDelete"] = "Menu.Delete",
        ["CtxPaste"] = "Menu.Paste",
        // ── 项目菜单 ──
        ["CtxOpen"] = "Menu.Open",
        ["CtxOpenLocation"] = "Menu.ShowInFolder",
        ["CtxEmptyRecycle"] = "Menu.EmptyRecycle",
        ["CtxRename"] = "Menu.Rename",
        // ── 次级分区菜单 ──
        ["CtxSubOpen"] = "Menu.Open",
        ["CtxSubRename"] = "Menu.Rename",
        ["CtxSubDissolve"] = "Menu.Dissolve",
        ["CtxSubDelete"] = "Menu.Delete",
        // ── 标题栏「＋」弹出的同款菜单（多一套 *2 的名字）──
        ["CtxImport2"] = "Menu.Import",
        ["CtxImportFiles2"] = "Menu.File",
        ["CtxImportFolder2"] = "Menu.Folder",
        ["CtxImportShell2"] = "Menu.Monitor",
        ["CtxNewSubZone2"] = "Menu.NewSubZone",
        ["CtxNew2"] = "Menu.New",
        ["CtxNewFolder2"] = "Menu.Folder",
        ["CtxNewTxt2"] = "Menu.NewTxt",
        ["CtxNewDocx2"] = "Menu.NewDocx",
        ["CtxNewPptx2"] = "Menu.NewPptx",
        ["CtxNewXlsx2"] = "Menu.NewXlsx",
        ["CtxMapFolder2"] = "Menu.MapFolder",
        // ── 面板「＋」菜单（结构同分区，但项名与分区不同）──
        ["CtxNewTextFile"] = "Menu.NewTxt",
        ["CtxNewWordFile"] = "Menu.NewDocx",
        ["CtxNewPptFile"] = "Menu.NewPptx",
        ["CtxNewExcelFile"] = "Menu.NewXlsx",
        // ── 时钟 ──
        ["CtxSwitchMode"] = "Menu.ClockMode",
        ["CtxToggleSeconds"] = "Menu.ClockSeconds",
        // ── 日历 ──
        ["CtxStartOnMonday"] = "Menu.CalendarMonday",
        // ── 便签 ──
        ["CtxSave"] = "Menu.Save",
        ["CtxSaveAs"] = "Menu.SaveAs",
        ["CtxOpenFile"] = "Menu.Open",
    };

    /// <summary>给一个窗口挂上 ContextMenuOpening：凡是由 ContextMenuService 打开
    /// （右键元素上挂的 ContextMenu）的菜单，都在弹出前补一次图标。
    /// <paramref name="lockGlyph"/> 取当前锁定字符（🔓 / 🔒），
    /// <paramref name="formatKey"/> 取「12 / 24 制」该用的几何键。</summary>
    public static void Hook(Window window, Func<string?>? lockGlyph = null, Func<string?>? formatKey = null)
    {
        window.AddHandler(FrameworkElement.ContextMenuOpeningEvent,
            new ContextMenuEventHandler((_, e) =>
                Apply(ContextMenuService.GetContextMenu(e.Source as DependencyObject),
                      lockGlyph?.Invoke(), formatKey?.Invoke())));
    }

    /// <summary>给一条菜单（含子菜单）补齐图标。用在「手动 IsOpen」的打开点。</summary>
    public static void Apply(ContextMenu? menu, string? lockGlyph = null, string? formatKey = null)
    {
        if (menu == null) return;
        foreach (var entry in menu.Items) Walk(entry, lockGlyph, formatKey);
    }

    static void Walk(object? entry, string? lockGlyph, string? formatKey)
    {
        if (entry is not MenuItem mi) return;

        switch (mi.Name)
        {
            // 这两项的图标是「状态」，每次打开都要重刷，不能只建一次。
            case "CtxLock":
                SetGlyph(mi, lockGlyph ?? "🔓");
                break;
            case "CtxToggle24h":
                mi.Icon = MenuIconFactory.Icon(formatKey ?? "Menu.ClockFormat24");
                break;
            // 字形项：建过一次就够了（字符不变）。
            case "CtxSettings":
                if (mi.Icon == null) mi.Icon = MenuIconFactory.Glyph("⚙");
                break;
            case "CtxPinTop":
                if (mi.Icon == null) mi.Icon = MenuIconFactory.Glyph("📌");
                break;
            default:
                if (mi.Icon == null && Map.TryGetValue(mi.Name, out var key))
                    mi.Icon = MenuIconFactory.Icon(key,
                        mi.Name.EndsWith("Delete", StringComparison.Ordinal)
                            ? "MenuItemIconDanger" : "MenuItemIcon");
                break;
        }

        foreach (var child in mi.Items) Walk(child, lockGlyph, formatKey);
    }

    static void SetGlyph(MenuItem mi, string text)
    {
        if (mi.Icon is TextBlock tb) tb.Text = text;
        else mi.Icon = MenuIconFactory.Glyph(text);
    }
}
