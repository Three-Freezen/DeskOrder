using System.Text.Json.Serialization;

namespace DesktopZones.Models;

/// <summary>
/// 一条「插件工具」全局热键分配（持久化到 <see cref="AppConfig.PluginToolHotkeys"/>）。
/// 只存 (插件 id + 命令 id)，不存命令名/主办方：插件卸载后这里留一条死记录也无害，
/// 下次加载会自动跳过（见 <c>App.RefreshPluginToolHotkeys</c>）。
/// </summary>
public sealed class PluginToolHotkey
{
    public string PluginId { get; set; } = "";

    public string CommandId { get; set; } = "";

    /// <summary>Win32 MOD_* 组合（1=Alt 2=Ctrl 4=Shift 8=Win）。</summary>
    public uint Modifiers { get; set; }

    /// <summary>Win32 虚拟键码。</summary>
    public uint Key { get; set; }

    public bool Enabled { get; set; } = true;

    [JsonIgnore]
    public bool IsEmpty => string.IsNullOrWhiteSpace(PluginId) || string.IsNullOrWhiteSpace(CommandId);
}
