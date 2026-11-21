using System;
using System.Threading.Tasks;

namespace DeskOrder.PluginApi;

/// <summary>
/// 工具命令可以出现在哪些入口。<see cref="Menus"/> 三项由宿主画成子菜单项（作者声明用）；
/// <see cref="Hotkey"/> 只作为执行上下文里的「来源」取值出现——命令**不需要**声明它，
/// 任意命令都可以由用户在「插件工具」页分配全局热键。
/// </summary>
[Flags]
public enum PluginToolSurface
{
    None = 0,
    /// <summary>托盘右键菜单</summary>
    Tray = 1,
    /// <summary>面板（快捷面板）背景右键菜单</summary>
    Panel = 2,
    /// <summary>分区右键菜单</summary>
    Zone = 4,
    /// <summary>全局热键触发。只用于 <see cref="PluginToolContext.Source"/>，菜单声明里忽略。</summary>
    Hotkey = 8,
    Menus = Tray | Panel | Zone
}

/// <summary>
/// 工具命令注册入口（API v3）。只能在 <see cref="IDeskOrderPlugin.Initialize"/> 里调用。
/// </summary>
public interface IPluginTools
{
    /// <summary>
    /// 注册一条工具命令。同一个插件最多 16 条，id 在插件内唯一；
    /// 非法 id / 空显示名 / 空 Execute 会抛异常（宿主记为该插件加载失败）。
    /// </summary>
    void Register(PluginToolCommand command);
}

/// <summary>一条用户可触发的工具命令。</summary>
public sealed class PluginToolCommand
{
    /// <summary>插件内唯一，1..64 个字符，只允许 <c>[a-z0-9._-]</c>。</summary>
    public required string Id { get; init; }

    /// <summary>
    /// 菜单显示名（旧插件 / 未提供 <see cref="LocalizedDisplayName"/> 时的回退值，宿主原样显示、不翻译）。
    /// 需要中英双语时请填 <see cref="LocalizedDisplayName"/>，宿主会优先用解析后的本地化名。
    /// </summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// 可选本地化显示名（API v5）：填了之后菜单 / 插件工具页 / 执行失败提示都按宿主当前语言解析；
    /// 只填一种语言时另一种回退到它；为 null（默认）时回退 <see cref="DisplayName"/>，旧插件零改动。
    /// </summary>
    public PluginLocalizedText? LocalizedDisplayName { get; init; }

    /// <summary>可选说明，作为菜单项的 ToolTip。</summary>
    public string? Description { get; init; }

    /// <summary>出现在哪些菜单，默认只上托盘。菜单与热键互相独立：任意命令都能在「插件工具」页分配热键，不要求在这里声明任何东西。</summary>
    public PluginToolSurface Menus { get; init; } = PluginToolSurface.Tray;

    /// <summary>宿主在 UI 线程调用，内部 try/catch：抛异常不会冒到界面，只记日志 + 托盘提示。</summary>
    public required Func<PluginToolContext, Task> Execute { get; init; }
}

/// <summary>工具命令执行上下文。所有属性在调用时固定。</summary>
public sealed class PluginToolContext
{
    public required string PluginId { get; init; }

    public required string CommandId { get; init; }

    /// <summary>这次是从哪个入口触发的（Tray / Panel / Zone / Hotkey）。</summary>
    public required PluginToolSurface Source { get; init; }

    /// <summary>插件级数据（与 <see cref="IPluginHost.Storage"/> 同一份文件；命令返回后宿主会落盘）。</summary>
    public required IPluginStorage Settings { get; init; }

    public required IPluginUi Ui { get; init; }

    public required IPluginLog Log { get; init; }

    /// <summary>发一条托盘通知（标题自动带插件名前缀，和桌面组件一致）。</summary>
    public void Notify(string title, string body) => NotifyCallback?.Invoke(title, body);

    internal Action<string, string>? NotifyCallback { get; init; }
}
