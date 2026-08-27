using System.Windows.Controls;
using System.Windows.Input;
using DesktopZones.Services;

namespace DesktopZones.Helpers;

/// <summary>
/// ponytail 2026-08-27: 替换 WPF TextBox/RichTextBox 默认 ContextMenu 的内置字符串。
/// 默认右键菜单的 Header 取自 PresentationFramework 资源,不跟用户的 LocalizationService。
/// 自定义版订阅 Opened 事件 — 每次右键打开时重读 <c>_loc[key]</c>,等价于"切语言后下次右键取新 i18n"。
/// <see cref="ApplicationCommands.Cut/Copy/Paste"/> 命令路由自动命中当前焦点输入框,
/// 调用方无需挂 CommandBinding。
/// </summary>
public static class TextBoxContextMenuBuilder
{
    /// <summary>构造剪贴三键 ContextMenu(Cut / Copy / Paste)。</summary>
    public static ContextMenu Build(LocalizationService loc)
    {
        var cm = new ContextMenu();
        var cut = new MenuItem { Header = loc["Common.Cut"], Command = ApplicationCommands.Cut };
        var copy = new MenuItem { Header = loc["Common.Copy"], Command = ApplicationCommands.Copy };
        var paste = new MenuItem { Header = loc["Common.Paste"], Command = ApplicationCommands.Paste };
        cm.Opened += (_, _) =>
        {
            cut.Header = loc["Common.Cut"];
            copy.Header = loc["Common.Copy"];
            paste.Header = loc["Common.Paste"];
        };
        cm.Items.Add(cut);
        cm.Items.Add(copy);
        cm.Items.Add(paste);
        return cm;
    }
}