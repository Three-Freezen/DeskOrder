using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using DeskOrder.PluginApi;
using DesktopZones.Helpers;
using DesktopZones.Models;

namespace DesktopZones.Services;

/// <summary>一条已发布的媒体数据源：描述符本身 + 来源插件（下拉显示名 / 日志兜底都按插件走）。</summary>
public sealed class PluginMediaSourceEntry
{
    public required PluginRecord Record { get; init; }
    public required PluginManifest Manifest { get; init; }
    public required PluginMediaSourceDescriptor Source { get; init; }
    public required string PluginId { get; init; }
    /// <summary>manifest.Name 的当前语言写法；为空时退化为插件 id（作者没填时也不能显示空白项）。</summary>
    public required string PluginDisplayName { get; init; }

    /// <summary>
    /// 数据源显示名（按宿主当前语言解析）：API v5 的
    /// <see cref="PluginMediaSourceDescriptor.LocalizedDisplayName"/> 优先，为 null / 解析为空时
    /// 回退 <see cref="PluginMediaSourceDescriptor.DisplayName"/>（旧插件零改动）。
    /// 属性面板「数据源」下拉统一用它。
    /// </summary>
    public string DisplayName
    {
        get
        {
            var localized = Source.LocalizedDisplayName?.Get(LocalizationService.Instance.CurrentLanguage);
            return string.IsNullOrWhiteSpace(localized) ? Source.DisplayName : localized!;
        }
    }
}

/// <summary>
/// 集成插件（API v4）的媒体数据源中心：收集插件注册的源 → 给属性面板的「数据源」下拉查表 →
/// 后台轮询（2 秒超时）并把 <see cref="PluginMediaSnapshot"/> 映射成宿主既有的
/// <see cref="MediaSessionSnapshot"/>（播放器组件和系统媒体会话共用同一套渲染）。
/// 单例，UI 线程使用。
/// </summary>
public sealed class PluginIntegrationService
{
    public static PluginIntegrationService Instance { get; } = new();

    /// <summary>一次 Poll 的等待上限。插件约定必须快速返回；超时只记日志，不弹窗。</summary>
    internal static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(2);

    /// <summary>封面字节的防御上限：manifest 只保证是字节，不保证是图片。1.5MB 是给作者的建议值，
    /// 超过这个量级直接当没有封面（解码 512px 也用不到）。
    /// ponytail 2026-11-21: 与 MediaSessionService 读取系统封面时的 16MB 防御上限保持一致。</summary>
    const int MaxArtworkBytes = 16 * 1024 * 1024;

    readonly List<PluginMediaSourceEntry> _entries = new();

    /// <summary>当前所有已加载插件的数据源（插件加载顺序 + 插件内注册顺序）。</summary>
    public IReadOnlyList<PluginMediaSourceEntry> MediaSources => _entries;

    /// <summary>数据源集合变化（插件加载 / 卸载 / 重新发布）。数据源下拉与播放器靠它按需刷新。</summary>
    public event Action? SourcesChanged;

    PluginIntegrationService() { }

    // ── 发布 / 解除 ──

    /// <summary>
    /// 插件 Initialize 成功后由 <see cref="PluginService"/> 调用。重复发布同一插件先移除旧源；
    /// 源数为 0 也要调——加载重试/重载路径不能留着上一轮的源。
    /// </summary>
    internal void Publish(PluginRecord record, PluginManifest manifest,
        IReadOnlyList<PluginMediaSourceDescriptor> sources)
    {
        _entries.RemoveAll(e => string.Equals(e.PluginId, record.Id, StringComparison.OrdinalIgnoreCase));
        var display = manifest.Name?.Get(LocalizationService.Instance.CurrentLanguage);
        if (string.IsNullOrWhiteSpace(display)) display = manifest.Id;
        foreach (var source in sources)
        {
            _entries.Add(new PluginMediaSourceEntry
            {
                Record = record,
                Manifest = manifest,
                Source = source,
                PluginId = record.Id,
                PluginDisplayName = display
            });
        }
        SourcesChanged?.Invoke();
    }

    /// <summary>插件卸载/被禁用时调用：数据源下拉与正在使用它的播放器都不应再看到它。</summary>
    internal void RemovePlugin(string pluginId)
    {
        if (_entries.RemoveAll(e => string.Equals(e.PluginId, pluginId, StringComparison.OrdinalIgnoreCase)) > 0)
            SourcesChanged?.Invoke();
    }

    internal void Clear()
    {
        if (_entries.Count == 0) return;
        _entries.Clear();
        SourcesChanged?.Invoke();
    }

    // ── 查询 ──

    /// <summary>数据源键：<c>&lt;pluginId&gt;/&lt;sourceId&gt;</c>。两个 id 都不含 '/'，拼接无歧义。</summary>
    public string KeyOf(PluginMediaSourceEntry entry) => entry.PluginId + "/" + entry.Source.Id;

    /// <summary>按 <see cref="KeyOf"/> 的键查源；找不到（插件未加载 / 被禁用 / 版本换过）返回 null。</summary>
    public PluginMediaSourceEntry? Find(string key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        int slash = key.IndexOf('/');
        if (slash <= 0 || slash == key.Length - 1) return null;
        var pluginId = key[..slash];
        var sourceId = key[(slash + 1)..];
        return _entries.FirstOrDefault(e =>
            string.Equals(e.PluginId, pluginId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(e.Source.Id, sourceId, StringComparison.Ordinal));
    }

    // ── 读取 ──

    /// <summary>
    /// 后台调一次插件的 <see cref="PluginMediaSourceDescriptor.Poll"/>（2 秒超时），
    /// 映射成宿主快照。返回 null = 本次没有播放内容 / 超时 / 插件抛异常（只记一行日志，不弹窗）。
    /// </summary>
    public async Task<MediaSessionSnapshot?> ReadAsync(PluginMediaSourceEntry entry)
    {
        if (entry == null) return null;

        Task<PluginMediaSnapshot?> poll;
        try
        {
            // Poll 可能在后台线程碰它自己的 COM/WinRT/网络；宿主绝不把插件代码跑在 UI 线程上。
            poll = Task.Run(entry.Source.Poll);
        }
        catch (Exception ex)
        {
            LogPollFailure(entry, "could not be scheduled", ex);
            return null;
        }

        var finished = await Task.WhenAny(poll, Task.Delay(PollTimeout)).ConfigureAwait(true);
        if (!ReferenceEquals(finished, poll))
        {
            // 超时的任务之后仍可能抛异常：先观察掉，别让 TaskScheduler 认为它没人处理。
            _ = poll.ContinueWith(t => _ = t.Exception,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            DzTrace.Always($"[Plugin] media source '{KeyOf(entry)}' poll timed out after " +
                           $"{(int)PollTimeout.TotalMilliseconds} ms");
            return null;
        }

        PluginMediaSnapshot? snapshot;
        try
        {
            snapshot = await poll.ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            LogPollFailure(entry, "threw", ex);
            return null;
        }
        if (snapshot == null) return null;   // 约定：null = 当前没有播放内容

        // 映射/解码放在调用方线程（UI 线程）：BitmapImage 解码走 WIC，在 STA 上最稳；
        // 这也保证返回给播放器的 BitmapSource 已经 Freeze、可跨线程共享。
        return new MediaSessionSnapshot
        {
            Now = MapNowPlaying(snapshot),
            Artwork = DecodeArtwork(entry, snapshot.Artwork)
        };
    }

    /// <summary>插件快照 → 宿主既有的 UI 只读模型（不暴露 SDK 类型）。</summary>
    internal static MediaNowPlaying MapNowPlaying(PluginMediaSnapshot snapshot)
    {
        bool hasContent = !string.IsNullOrWhiteSpace(snapshot.Title)
                          || !string.IsNullOrWhiteSpace(snapshot.Artist)
                          || !string.IsNullOrWhiteSpace(snapshot.Album)
                          || !string.IsNullOrWhiteSpace(snapshot.SourceApp)
                          || snapshot.Duration > TimeSpan.Zero;
        return new MediaNowPlaying
        {
            Title = snapshot.Title ?? "",
            Artist = snapshot.Artist ?? "",
            Album = snapshot.Album ?? "",
            SourceApp = snapshot.SourceApp ?? "",
            // 插件返回了非 null 快照 = 「现在有内容」：在播显示 Playing，否则 Paused。
            // 连曲目信息都没有的暂停态视为「没有会话」——播放器会显示未检测到媒体。
            State = snapshot.IsPlaying
                ? MediaPlaybackState.Playing
                : hasContent ? MediaPlaybackState.Paused : MediaPlaybackState.None,
        };
    }

    static BitmapSource? DecodeArtwork(PluginMediaSourceEntry entry, byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0) return null;
        if (bytes.Length > MaxArtworkBytes)
        {
            DzTrace.Always($"[Plugin] media source '{entry.PluginId}/{entry.Source.Id}' artwork is too large " +
                           $"({bytes.Length} bytes); ignored");
            return null;
        }
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 512;   // 与系统媒体封面的解码尺寸一致，播放器用不到原图
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex)
        {
            DzTrace.Always($"[Plugin] media source '{entry.PluginId}/{entry.Source.Id}' artwork cannot be " +
                           $"decoded: {ex.GetType().Name} {ex.Message}");
            return null;
        }
    }

    static void LogPollFailure(PluginMediaSourceEntry entry, string what, Exception ex)
        => DzTrace.Always($"[Plugin] media source '{entry.PluginId}/{entry.Source.Id}' poll {what}: " +
                          $"{ex.GetType().Name} {ex.Message}");
}
