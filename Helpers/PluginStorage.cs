using System.IO;
using DesktopZones.Services;

namespace DesktopZones.Helpers;

/// <summary>
/// 插件级键值存储的构造入口（插件数据目录下的 <c>settings.json</c>）。
/// 宿主里加载期（<c>IPluginHost.Storage</c>）、工具命令执行期、插件设置页读写期
/// 都用这同一份路径规则 —— 三处各写一遍 <c>Path.Combine(..., "settings.json")</c>
/// 迟早会漂移（例如设置页写到别的文件名，插件就永远读不到）。
/// </summary>
public static class PluginStorage
{
    /// <summary>插件级存储文件名。</summary>
    public const string FileName = "settings.json";

    /// <summary>插件级存储的绝对路径（目录可能还不存在，写入时由 JsonPluginStorage 创建）。</summary>
    public static string FilePath(string pluginId)
        => Path.Combine(PluginPaths.PluginDataDir(pluginId), FileName);

    /// <summary>打开插件级存储（加载已有内容；不存在按空处理）。</summary>
    public static JsonPluginStorage Create(string pluginId)
        => new(FilePath(pluginId), initialJson: null, onFlush: null);
}
