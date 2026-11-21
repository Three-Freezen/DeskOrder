using System;
using System.Threading.Tasks;

namespace DeskOrder.PluginApi;

/// <summary>
/// 集成/数据源插件（API v4）的注册入口：在 <see cref="IDeskOrderPlugin.Initialize"/> 里调用。
/// 只能在 Initialize 里调用；抛异常 = 该插件加载失败。
/// </summary>
public interface IPluginIntegrations
{
    /// <summary>
    /// 注册一个媒体数据源。同一个插件最多 <c>4</c> 个源，id 在插件内唯一；
    /// 非法 id / 空显示名 / 空 Poll / 超量 / 重名都会抛异常（宿主记为该插件加载失败）。
    /// </summary>
    void RegisterMediaSource(PluginMediaSourceDescriptor descriptor);
}

/// <summary>
/// 一个「当前正在播放」数据源。宿主把它接进播放器组件的「数据源」下拉：
/// 选中后播放器改用这里的快照渲染（标题 / 歌手 / 专辑 / 封面 / 播放状态），
/// 不再跟随系统媒体会话。
/// </summary>
public sealed class PluginMediaSourceDescriptor
{
    /// <summary>插件内唯一，1..64 个字符，只允许 <c>[a-z0-9._-]</c>。最终键是 <c>&lt;pluginId&gt;/&lt;id&gt;</c>。</summary>
    public required string Id { get; init; }

    /// <summary>属性面板「数据源」下拉里显示的名字（宿主原样显示，不做翻译；请按语言自己挑字符串）。</summary>
    public required string DisplayName { get; init; }

    /// <summary>可选说明（暂未展示，留给后续版本）。</summary>
    public string? Description { get; init; }

    /// <summary>
    /// 宿主在**后台线程**按需轮询（约 1 秒一次）；实现必须快速返回（宿主 2 秒超时，
    /// 超时会话按「本次没有播放内容」处理并写一行日志）。返回 null = 当前没有播放内容。
    /// 抛异常也不会影响宿主，按没有播放内容处理。
    /// </summary>
    public required Func<PluginMediaSnapshot?> Poll { get; init; }
}

/// <summary>某个媒体数据源在某一刻的「正在播放」快照（不可变）。</summary>
public sealed class PluginMediaSnapshot
{
    public string Title { get; init; } = "";

    public string Artist { get; init; } = "";

    public string Album { get; init; } = "";

    /// <summary>来源应用名（可空）；播放器可以显示来源。</summary>
    public string SourceApp { get; init; } = "";

    public bool IsPlaying { get; init; }

    public TimeSpan Position { get; init; }

    public TimeSpan Duration { get; init; }

    /// <summary>
    /// 可选封面（PNG/JPEG 字节，建议不超过 1.5 MB）。宿主在 UI 线程解码并冻结，
    /// 解码失败按没有封面处理；返回 null 就没有封面。
    /// </summary>
    public byte[]? Artwork { get; init; }
}
