using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopZones.Helpers;
using Windows.Foundation;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace DesktopZones.Services;

/// <summary>播放状态的粗粒度映射（够 UI 用，不暴露 WinRT 类型）。</summary>
public enum MediaPlaybackState { None, Playing, Paused, Other }

/// <summary>一份"当前正在播放"的快照。UI 只读它，不直接碰 WinRT 对象。</summary>
public sealed class MediaNowPlaying
{
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Album { get; init; } = "";
    public string SourceApp { get; init; } = "";
    public MediaPlaybackState State { get; init; } = MediaPlaybackState.None;

    /// <summary>这份快照是不是"音频会话 + 窗口标题"兜底识别出来的（没有 SMTC 通道、没有缩略图）。</summary>
    public bool FromFallback { get; init; }

    public bool HasSession => State != MediaPlaybackState.None;
    public bool IsPlaying => State == MediaPlaybackState.Playing;

    /// <summary>标题行：没有标题时回落到"未知曲目"。</summary>
    public string TitleText => string.IsNullOrWhiteSpace(Title) ? "" : Title;
    public string ArtistText => string.IsNullOrWhiteSpace(Artist) ? "" : Artist;
    public string AlbumText => string.IsNullOrWhiteSpace(Album) ? "" : Album;

    public static readonly MediaNowPlaying Empty = new();
}

/// <summary>属性面板「跟随的应用」下拉里的一项（AUMID + 友好名 + 当前是否在播）。</summary>
public sealed class MediaSessionChoice
{
    public string Aumid { get; init; } = "";
    public string AppLabel { get; init; } = "";
    public bool IsPlaying { get; init; }

    /// <summary>下拉里显示的文字：应用名（在播）。</summary>
    public override string ToString() => AppLabel;
}

/// <summary>跟随指定应用时的一轮读数（不进 <see cref="MediaSessionService.Now"/>，互不干扰）。</summary>
public sealed class MediaSessionSnapshot
{
    public MediaNowPlaying Now { get; init; } = MediaNowPlaying.Empty;
    public BitmapSource? Artwork { get; init; }
}

/// <summary>
/// 系统媒体会话（SMTC / Windows.Media.Control）的封装。
///
/// - 单例，进程内所有播放器组件共用一份会话与封面缓存；
/// - 通过 <see cref="GlobalSystemMediaTransportControlsSessionManager"/> 拿到
///   "系统正在播放"的会话，读取标题/歌手/专辑/缩略图/播放状态，并转发
///   上一曲 / 暂停 / 下一曲；
/// - WinRT 事件可能来自任意线程 → 统一 Post 回 UI 线程再改状态；
/// - 另加 1s 轮询兜底：部分应用只更新状态不发事件，封面也常常晚 1~2 秒才到。
/// </summary>
public sealed class MediaSessionService
{
    public static MediaSessionService Instance { get; } = new();

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private DispatcherTimer? _poll;
    private bool _initStarted;
    private bool _refreshing;
    private string _artKey = "";

    /// <summary>当前画面是否来自「音频会话 + 窗口标题」兜底（而不是 SMTC）。
    /// 这种状态下没有封面、也没有 SMTC 控制通道，控制按钮改走系统媒体键。</summary>
    private bool _usingFallback;

    /// <summary>兜底模式下最后一次识别到的进程，用来区分"暂停"和"没媒体"。</summary>
    private int _fallbackPid;
    private string _fallbackTitle = "";
    private string _fallbackArtist = "";
    private string _fallbackApp = "";

    /// <summary>兜底封面尝试过的曲目 key（同一首歌只试一次，别每秒重试）。</summary>
    private string _coverTryKey = "";

    /// <summary>上一次兜底封面尝试失败的时间点，失败后隔一会儿再试（网络抖动可自愈）。</summary>
    private long _coverRetryAfter;

    /// <summary>当前画面是不是"有声音但认不出是谁"（只播状态、不带曲目）。</summary>
    private bool _audioOnly;

    /// <summary>整机"确实有声音"的起始时间（TickCount64）。声音连续响够
    /// <see cref="AudioSustainMs"/> 才算在播放 —— 系统提示音只有一两百毫秒，靠这条挡掉。</summary>
    private long _audibleSince;

    /// <summary>声音停止后继续按"在播放"显示的截止时间（TickCount64）。
    /// 换歌、缓冲、暂停后的一小会儿都不会闪成"没媒体"。</summary>
    private long _audibleHoldUntil;

    /// <summary>最后一个走的分支（诊断日志用，只在变化时写一行）。</summary>
    private string _lastBranch = "";

    /// <summary>设备主音量表认为"有声音"的阈值（约 -58dB，比会话峰值阈值宽松一点点）。</summary>
    private const float SystemAudibleThreshold = 0.0012f;

    /// <summary>声音要连续响多久才切到"在播放"（挡提示音）。</summary>
    private const long AudioSustainMs = 1200;

    /// <summary>声音停止后继续显示"在播放"多久（挡换歌 / 缓冲间隙）。</summary>
    private const long AudioHoldMs = 3000;

    /// <summary>当前兜底对象没有曲名 → 每轮去网易云的播放缓存里看有没有换歌。</summary>
    private bool _pollNetEaseTrack;

    /// <summary>上一次从播放缓存解析到的歌曲 id（用来判断换歌）。</summary>
    private string _lastNetEaseTrackId = "";

    /// <summary>兜底模式是否允许去读播放器自己的队列缓存 + 下载封面（唱片机属性面板的开关）。</summary>
    public bool FetchExternalArtwork { get; set; } = true;

    /// <summary>当前快照。无媒体时是 <see cref="MediaNowPlaying.Empty"/>。</summary>
    public MediaNowPlaying Now { get; private set; } = MediaNowPlaying.Empty;

    private BitmapSource? _artwork;

    /// <summary>这一轮封面有没有变（<see cref="Apply"/> 用它决定要不要通知 UI）。
    ///
    /// 换歌时元数据先到、缩略图常常晚一两秒才到：那一两秒里曲目信息没变、只有封面变了，
    /// 如果只按"曲目/状态是否变化"决定要不要发 <see cref="Changed"/>，
    /// 唱片机会一直挂着上一首的图（或占位图），非要暂停再播一下才刷新。 */
    private bool _artChanged;

    /// <summary>封面（已 Freeze，可跨线程共享）。无封面时为 null。</summary>
    public BitmapSource? Artwork
    {
        get => _artwork;
        private set
        {
            if (ReferenceEquals(_artwork, value)) return;
            _artwork = value;
            _artChanged = true;
        }
    }

    /// <summary>上一次被采纳的 SMTC 缩略图内容哈希（FNV-1a）。
    /// 用来识破"歌名换了、缩略图还是上一首"的假封面 —— 见 <see cref="ReadSmtcAsync"/>。</summary>
    private ulong _artHash;

    /// <summary>换歌了、但缩略图内容和上一首一模一样：可能是同专辑同封面（正常），
    /// 也可能是播放器还没把新图挂上来（脏图）。接下来几秒继续复查，真图到了立刻换。</summary>
    private bool _artSuspicious;

    private int _artSuspiciousTicks;

    /// <summary>复查窗口（1s 轮询下约 10 秒）。</summary>
    private const int ArtSuspiciousMaxTicks = 10;

    /// <summary>状态或曲目变化时触发（UI 线程回调）。</summary>
    public event Action? Changed;

    /// <summary>懒启动：第一个播放器组件出现时才开始接 SMTC，不用播放器就不碰 WinRT。</summary>
    public void EnsureStarted()
    {
        if (_initStarted) return;
        _initStarted = true;
        _ = InitAsync();
    }

    private async Task InitAsync()
    {
        // 轮询与 SMTC 是否可用无关：SMTC 起不来时还要靠它驱动「音频会话兜底」
        StartPolling();
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += OnManagerSessionChanged;
            _manager.SessionsChanged += OnManagerSessionChanged;
        }
        catch (Exception ex)
        {
            DzTrace.Log($"[MediaSession] SMTC 初始化失败（改用音频会话兜底）: {ex.GetType().Name} {ex.Message}");
        }
        await RefreshAsync(force: true);
    }

    /// <summary>1s 轮询：SMTC 事件不保证齐全（部分应用只改状态不发事件、缩略图常常晚到），
    /// 兜底识别也只能靠轮询发现。</summary>
    private void StartPolling()
    {
        if (_poll != null) return;
        if (Application.Current?.Dispatcher is not Dispatcher d) return;
        _poll = new DispatcherTimer(DispatcherPriority.Background, d)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _poll.Tick += (_, _) => _ = RefreshAsync(force: false);
        _poll.Start();
    }

    private void OnManagerSessionChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        object args)
        => Post(() => _ = RefreshAsync(force: false));

    // ── 控制 ──

    /// <summary>播放/暂停。<paramref name="aumid"/> 非空 = 只控制那个会话（跟随指定应用）。</summary>
    public async Task TogglePlayPauseAsync(string? aumid = null)
    {
        EnsureStarted();
        var pinned = string.IsNullOrEmpty(aumid) ? null : FindSession(aumid);
        if (pinned != null)
        {
            try { await pinned.TryTogglePlayPauseAsync(); }
            catch (Exception ex) { LogCmd("TogglePlayPause(pinned)", ex); }
            await RefreshAsync(force: true);
            return;
        }

        var s = _session;
        if (_usingFallback)
        {
            // 兜底模式（播放器没注册 SMTC）：只能靠系统媒体键，最佳努力
            AudioNowPlayingProbe.TogglePlayPause();
            await Task.Delay(280);   // 等播放器真的切换完状态再读，别立刻回读成旧状态
        }
        else if (s != null)
        {
            try { await s.TryTogglePlayPauseAsync(); } catch (Exception ex) { LogCmd("TogglePlayPause", ex); }
        }
        await RefreshAsync(force: true);
    }

    public async Task NextAsync(string? aumid = null)
    {
        EnsureStarted();
        var pinned = string.IsNullOrEmpty(aumid) ? null : FindSession(aumid);
        if (pinned != null)
        {
            try { await pinned.TrySkipNextAsync(); }
            catch (Exception ex) { LogCmd("SkipNext(pinned)", ex); }
            await RefreshAsync(force: true);
            return;
        }

        var s = _session;
        if (_usingFallback)
        {
            AudioNowPlayingProbe.SkipNext();
            await Task.Delay(280);
        }
        else if (s != null)
        {
            try { await s.TrySkipNextAsync(); } catch (Exception ex) { LogCmd("SkipNext", ex); }
        }
        await RefreshAsync(force: true);
    }

    public async Task PreviousAsync(string? aumid = null)
    {
        EnsureStarted();
        var pinned = string.IsNullOrEmpty(aumid) ? null : FindSession(aumid);
        if (pinned != null)
        {
            try { await pinned.TrySkipPreviousAsync(); }
            catch (Exception ex) { LogCmd("SkipPrevious(pinned)", ex); }
            await RefreshAsync(force: true);
            return;
        }

        var s = _session;
        if (_usingFallback)
        {
            AudioNowPlayingProbe.SkipPrevious();
            await Task.Delay(280);
        }
        else if (s != null)
        {
            try { await s.TrySkipPreviousAsync(); } catch (Exception ex) { LogCmd("SkipPrevious", ex); }
        }
        await RefreshAsync(force: true);
    }

    // ── P2：会话列表 / 跟随指定应用 ──

    /// <summary>系统里能看到的 SMTC 会话，给属性面板的「跟随的应用」下拉用。</summary>
    public IReadOnlyList<MediaSessionChoice> ListSessions()
    {
        var list = new List<MediaSessionChoice>();
        if (_manager == null) return list;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var s in _manager.GetSessions())
            {
                string aumid;
                try { aumid = s.SourceAppUserModelId ?? ""; } catch { continue; }
                if (aumid.Length == 0) continue;
                // 同一个应用可能有多条会话（两个浏览器窗口）：下拉里只留一条，免得看起来像重复项
                if (!seen.Add(aumid)) continue;
                list.Add(new MediaSessionChoice
                {
                    Aumid = aumid,
                    AppLabel = FriendlyAppLabel(aumid),
                    IsPlaying = MapState(s) == MediaPlaybackState.Playing,
                });
            }
        }
        catch (Exception ex) { LogCmd("ListSessions", ex); }
        return list;
    }

    /// <summary>只读某个会话的快照（跟随指定应用时用；不参与自动兜底判定）。</summary>
    public async Task<MediaSessionSnapshot?> ReadSessionAsync(string aumid)
    {
        EnsureStarted();
        var session = FindSession(aumid);
        if (session == null) return null;

        string title = "", artist = "", album = "";
        BitmapSource? art = null;
        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            if (props != null)
            {
                title = props.Title ?? "";
                artist = props.Artist ?? "";
                album = props.AlbumTitle ?? "";
                if (props.Thumbnail != null)
                    art = await GetPinnedArtAsync(aumid, title, artist, album, props);
            }
        }
        catch (Exception ex) { LogCmd("ReadSession", ex); }

        return new MediaSessionSnapshot
        {
            Now = new MediaNowPlaying
            {
                Title = title,
                Artist = artist,
                Album = album,
                SourceApp = aumid,
                State = MapState(session),
            },
            Artwork = art,
        };
    }

    /// <summary>按 AUMID 找会话（找不到返回 null，调用方按"这个应用没在放"处理）。</summary>
    private GlobalSystemMediaTransportControlsSession? FindSession(string aumid)
    {
        if (_manager == null) return null;
        try
        {
            foreach (var s in _manager.GetSessions())
            {
                try
                {
                    if (string.Equals(s.SourceAppUserModelId, aumid, StringComparison.OrdinalIgnoreCase))
                        return s;
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    // 跟随指定应用时的封面缓存（同一首歌每秒都会被读到，别重复解码）
    private readonly System.Collections.Generic.Dictionary<string, BitmapSource> _pinnedArt = new();

    private async Task<BitmapSource?> GetPinnedArtAsync(
        string aumid, string title, string artist, string album,
        GlobalSystemMediaTransportControlsSessionMediaProperties props)
    {
        var key = $"{aumid}|{title}|{artist}|{album}";
        if (_pinnedArt.TryGetValue(key, out var cached)) return cached;
        if (_pinnedArt.Count > 8) _pinnedArt.Clear();

        var (bytes, _) = await ReadArtBytesAsync(props.Thumbnail!);
        if (bytes == null) return null;
        var art = DecodeArt(bytes);
        if (art != null) _pinnedArt[key] = art;
        return art;
    }

    /// <summary>AUMID → 给人看的应用名。
    /// AUMID 形如 <c>Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic</c> /
    /// <c>MSEdge</c> / <c>SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify</c>：
    /// 去掉 <c>!</c> 之后的部分与发行者哈希，再给常见应用换成中文/友好名。</summary>
    internal static string FriendlyAppLabel(string aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid)) return "";
        var s = aumid;

        int bang = s.IndexOf('!');
        if (bang > 0) s = s[..bang];
        int underscore = s.LastIndexOf('_');
        if (underscore > 0 && s.Length - underscore >= 13) s = s[..underscore];
        // 有些应用直接把 exe 名当 AUMID（实测网易云：cloudmusic.exe）
        if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s[..^4];

        return s.ToLowerInvariant() switch
        {
            "msedge" or "microsoft.microsoftedge" or "microsoftedge" => "Edge",
            "microsoft.zunemusic" => "媒体播放器",
            "spotifyab.spotifymusic" or "spotify" => "Spotify",
            "chrome" or "google.chrome" => "Chrome",
            "firefox" or "mozilla.firefox" => "Firefox",
            "cloudmusic" or "netease.cloudmusic" => "网易云音乐",
            "qqmusic" or "tencent.qqmusic" => "QQ音乐",
            "kugou" => "酷狗音乐",
            "potplayermini64" or "potplayermini" or "daum.potplayer" => "PotPlayer",
            "foobar2000" => "foobar2000",
            "vlc" or "videolan.vlc" => "VLC",
            _ => s,
        };
    }

    private static void LogCmd(string what, Exception ex)
        => DzTrace.Log($"[MediaSession] {what} 失败: {ex.GetType().Name} {ex.Message}");

    // ── 刷新 ──

    private async Task RefreshAsync(bool force)
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var session = PickSession();
            if (!ReferenceEquals(session, _session))
                AttachSession(session);

            var state = session == null ? MediaPlaybackState.None : MapState(session);
            bool smtcPlaying = session != null && state == MediaPlaybackState.Playing;

            // 先看一眼"到底谁真的在出声"：有些应用会留下 PlaybackStatus=Playing 但根本没声音的
            // 僵尸会话（比如关掉网页后浏览器没清会话），只信 SMTC 的话就会一直显示错的那个，
            // 网易云这种不发 SMTC 的反而永远轮不到。
            AudioNowPlayingProbe.ScanResult scan;
            try { scan = (await ProbeTickAsync().ConfigureAwait(true)).Scan; }
            catch (Exception ex) { LogCmd("ProbeAudible", ex); scan = new AudioNowPlayingProbe.ScanResult(); }

            bool othersAudible = scan.AudibleProcesses.Count > 0;
            var detected = scan.Best;
            bool smtcIsAudibleApp = smtcPlaying && SmtcMatchesAudible(session!, scan.AudibleProcesses);

            // 整机可听度（设备主表峰值）：这条**不需要把声音归因给某个进程**。
            // 子进程混音、拿不到会话的应用、被排除的系统组件都可能让进程列表是空的，
            // 但设备表照样有读数 —— 于是"确实在放声音"这件事永远能拿到。
            bool systemAudible = scan.SystemPeak >= SystemAudibleThreshold;
            long nowMs = Environment.TickCount64;
            if (systemAudible || othersAudible)
            {
                if (_audibleSince == 0) _audibleSince = nowMs;
                _audibleHoldUntil = nowMs + AudioHoldMs;
            }
            else
            {
                _audibleSince = 0;
            }
            bool audibleSustained = _audibleSince != 0 && nowMs - _audibleSince >= AudioSustainMs;
            bool audibleHeld = nowMs < _audibleHoldUntil;

            // "有声音、但认不出是谁"这一档：
            // - 声音已经连续响够 SustainMs（或者上一轮就已经在这个状态，靠它撑过换歌的静音间隙）；
            // - 而且这声音不是 SMTC 那条会话发出来的（SMTC 在播但声音不是它的 = 僵尸会话）。
            // 注意用**上一轮**的 _audioOnly 判断，然后立刻清掉 —— 否则换歌的那一两秒会闪回"暂停"。
            bool unknownPlaying = audibleHeld
                                  && (audibleSustained || _audioOnly)
                                  && (!smtcPlaying || (othersAudible && !smtcIsAudibleApp));
            _audioOnly = false;

            // ① SMTC 在播，而且没有别的程序在出声 / 出声的就是它自己 → 用 SMTC
            if (smtcPlaying && (!othersAudible || smtcIsAudibleApp))
            {
                TraceBranch("smtc-playing", scan);
                await ReadSmtcAsync(session!, state, force);
                return;
            }

            // ② 兜底探针认出了播放器（有歌名最好，最少也有应用名）→ 用它
            if (detected != null)
            {
                TraceBranch("fallback-app", scan);
                ApplyFallback(detected, playing: true, force);
                return;
            }

            // ②b 换歌 / 缓冲的静音间隙：上一刻还是那个播放器，现在声音断了但还没到
            //     "算暂停"的时候 → 继续按"在播放"显示原曲目，别闪成暂停/空。
            if (audibleHeld && _fallbackPid > 0)
            {
                int holdPid = _fallbackPid;
                var recent = await Task.Run(() => AudioNowPlayingProbe.FindByPid(holdPid));
                if (recent != null)
                {
                    TraceBranch("fallback-hold", scan);
                    ApplyFallback(recent, playing: true, force);
                    return;
                }
            }

            // ③ 有声音，但认不出是谁 → 只显示"在播放"：条子照动、唱片照转，不显示曲目内容。
            //    连续响够 SustainMs 才切（提示音不够长，进不来），声音停了再保持 HoldMs
            //    （换歌 / 缓冲 / 暂停的瞬间不会闪回"没媒体"）。
            //    SMTC 说自己在播、但出声的不是它（僵尸会话）也走这里。
            if (unknownPlaying)
            {
                TraceBranch("audio-only", scan);
                ApplyAudioOnly(force);
                return;
            }

            // ④ 刚才那个兜底播放器还在（只是暂停了）→ 显示"已暂停"，别闪回"没媒体"。
            //    进程还在、会话还在就算暂停（曲名可能拿不到 —— 那就只显示暂停状态）。
            if (!audibleHeld && _fallbackPid > 0)
            {
                int pid = _fallbackPid;
                var paused = await Task.Run(() => AudioNowPlayingProbe.FindByPid(pid));
                if (paused != null)
                {
                    TraceBranch("fallback-paused", scan);
                    ApplyFallback(paused, playing: false, force);
                    return;
                }
                ClearFallbackState();
            }

            // ⑤ SMTC 有会话但没在播（用户暂停）→ 照旧显示它的曲目
            if (session != null)
            {
                TraceBranch("smtc-paused", scan);
                await ReadSmtcAsync(session, state, force);
                return;
            }

            // ⑥ 什么媒体都没有了
            _usingFallback = false;
            ClearFallbackState();
            if (Artwork != null) Artwork = null;
            _artKey = "";
            TraceBranch("idle", scan);
            Apply(MediaNowPlaying.Empty, force);
        }
        catch (Exception ex)
        {
            DzTrace.Log($"[MediaSession] 刷新失败: {ex.GetType().Name} {ex.Message}");
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>SMTC 会话 → 快照（曲目、封面、播放状态）。</summary>
    private async Task ReadSmtcAsync(
        GlobalSystemMediaTransportControlsSession session, MediaPlaybackState state, bool force)
    {
        _usingFallback = false;
        ClearFallbackState();
        _coverTryKey = "";

        if (_artSuspicious && ++_artSuspiciousTicks > ArtSuspiciousMaxTicks)
            _artSuspicious = false;

        string title = Now.Title, artist = Now.Artist, album = Now.Album, app = Now.SourceApp;
        IAsyncOperation<GlobalSystemMediaTransportControlsSessionMediaProperties>? propsOp = null;
        try { propsOp = session.TryGetMediaPropertiesAsync(); } catch { }

        GlobalSystemMediaTransportControlsSessionMediaProperties? props = null;
        if (propsOp != null)
        {
            try { props = await propsOp; } catch { }
        }

        if (props != null)
        {
            title = props.Title ?? "";
            artist = props.Artist ?? "";
            album = props.AlbumTitle ?? "";
            app = session.SourceAppUserModelId ?? "";
            var key = $"{app}|{title}|{artist}|{album}";

            // 复查窗口里即使 key 没变也要重读缩略图：换歌那一两秒播放器常常还在给旧图，
            // 只按 key 判"已经取过封面"就会把脏图钉死在唱片机上。
            if (props.Thumbnail != null && (key != _artKey || Artwork == null || _artSuspicious))
            {
                var (bytes, hash) = await ReadArtBytesAsync(props.Thumbnail);
                if (bytes != null)
                {
                    bool trackChanged = key != _artKey;
                    bool sameBytes = _artHash != 0 && hash == _artHash;

                    if (sameBytes && Artwork != null)
                    {
                        // 内容没变（同专辑同封面，或播放器还没换图）：图不用重解码，
                        // 只认下新 key；"换歌了但图没变"要留复查窗口。
                        _artKey = key;
                        if (trackChanged) MarkArtSuspicious();
                    }
                    else
                    {
                        var art = DecodeArt(bytes);
                        if (art != null)
                        {
                            Artwork = art;
                            _artKey = key;
                            _artHash = hash;
                            if (trackChanged && sameBytes) MarkArtSuspicious();
                        }
                    }
                }
            }
            else if (props.Thumbnail == null && key != _artKey)
            {
                // 换歌但新歌还没有封面：先清掉旧封面，等缩略图补上再换。
                Artwork = null;
                _artKey = "";
            }
        }

        Apply(new MediaNowPlaying
        {
            Title = title,
            Artist = artist,
            Album = album,
            SourceApp = app,
            State = state,
        }, force);
    }

    /// <summary>换歌但缩略图内容没变 → 开一个复查窗口（真图到了下一轮就换上）。</summary>
    private void MarkArtSuspicious()
    {
        _artSuspicious = true;
        _artSuspiciousTicks = 0;
    }

    private sealed class TickProbe
    {
        public AudioNowPlayingProbe.ScanResult Scan = new();

        /// <summary>本 tick 从网易云播放缓存里解析到的当前曲目（没开轮询 / 没换歌时为 null）。</summary>
        public MediaArtworkFallback.NetEaseTrack? Track;

        public BitmapSource? Cover;
    }

    /// <summary>
    /// 一轮后台探测：谁在出声（+ 能认出谁），以及在"兜底对象没有曲名"时顺便看一眼
    /// 网易云播放缓存里有没有换歌（文件名里的 songId 变了就是换歌，顺手把封面也下好）。
    /// 全部在后台线程完成，UI 线程只接收结果。
    /// </summary>
    private async Task<TickProbe> ProbeTickAsync()
    {
        bool pollNetEase = _pollNetEaseTrack;
        string lastId = _lastNetEaseTrackId;
        var result = await Task.Run(async () =>
        {
            var tick = new TickProbe { Scan = AudioNowPlayingProbe.Scan() };
            if (!pollNetEase) return tick;
            try
            {
                var track = MediaArtworkFallback.FindRecentTrack();
                if (track != null && track.Id != lastId)
                {
                    tick.Track = track;
                    tick.Cover = await MediaArtworkFallback.TryFetchAsync(track).ConfigureAwait(false);
                }
            }
            catch (Exception ex) { DzTrace.Log($"[MediaSession] 反查网易云曲目失败: {ex.Message}"); }
            return tick;
        }).ConfigureAwait(true);

        if (result.Track != null)
        {
            // UI 线程上更新记忆（换歌了）
            _lastNetEaseTrackId = result.Track.Id;
            _fallbackTitle = result.Track.Title;
            _fallbackArtist = result.Track.Artist;
            if (result.Cover != null)
            {
                Artwork = result.Cover;
                _artKey = _fallbackTitle + "|" + _fallbackArtist;
            }
        }
        return result;
    }

    // ── 兜底：没有 SMTC 会话的播放器（网易云音乐等）──

    /// <summary>音频会话 + 窗口标题识别出来的播放器 → 快照。这种状态没有封面，
    /// 也没有 SMTC 控制通道（控制按钮改发系统媒体键）。</summary>
    private void ApplyFallback(AudioNowPlayingProbe.Snapshot snapshot, bool playing, bool force)
    {
        _usingFallback = true;
        _fallbackPid = snapshot.Pid;
        if (snapshot.Title.Length > 0)
        {
            _fallbackTitle = snapshot.Title;
            _fallbackArtist = snapshot.Artist;
        }
        if (snapshot.AppName.Length > 0) _fallbackApp = snapshot.AppName;

        // 没有曲名（最小化到托盘 / 迷你播放器）且像是网易云 → 下一轮开始按播放缓存轮询换歌
        _pollNetEaseTrack = snapshot.Title.Length == 0
                            && MediaArtworkFallback.LooksLikeNetEase(snapshot.ProcessName, _fallbackApp);

        if (playing) MaybeEnrichFallback(snapshot);
        PublishFallback(playing, force);
    }

    /// <summary>把当前兜底状态发布成快照（曲目信息可能为空 → 只发"在播放"）。</summary>
    private void PublishFallback(bool playing, bool force)
    {
        // 兜底默认拿不到缩略图：换歌就把上一首的封面清掉，别让唱片机挂着别人的图。
        // 但一旦兜底封面下载回来（_artKey 已等于这首歌的 key），就别再每轮清空。
        var trackKey = _fallbackTitle + "|" + _fallbackArtist;
        if (_artKey != trackKey && Artwork != null)
        {
            Artwork = null;
            _artKey = "";
        }

        var app = _fallbackApp.Length > 0 ? _fallbackApp : "";
        Apply(new MediaNowPlaying
        {
            // 认不出曲目（迷你播放器 / 新 UI）就**空着**，不要拿应用名占位 ——
            // 界面按"有播放状态但不显示曲目内容"处理（MediaPlayerWidget.RefreshMediaState）。
            // 一旦播放缓存 / 队列反查出歌名，下一轮就会补上。
            Title = _fallbackTitle,
            Artist = _fallbackArtist,
            Album = "",
            SourceApp = app,
            State = playing ? MediaPlaybackState.Playing : MediaPlaybackState.Paused,
            FromFallback = true,
        }, force);
    }

    /// <summary>有声音但认不出是谁：只报"在播放"，不显示任何曲目内容。</summary>
    private void ApplyAudioOnly(bool force)
    {
        // 控制按钮继续走系统媒体键（这时候本来就只能这样）
        _usingFallback = true;
        _audioOnly = true;
        if (Artwork != null) Artwork = null;
        _artKey = "";
        Apply(new MediaNowPlaying
        {
            Title = "",
            Artist = "",
            Album = "",
            SourceApp = "",
            State = MediaPlaybackState.Playing,
            FromFallback = true,
        }, force);
    }

    /// <summary>
    /// 分支诊断（Release 也写）：只在"走的哪条路"变化时写一行，正常播放时不会刷屏。
    /// 用户报"识别不出来"时，这一行足以判断是 SMTC 没会话、探针没认出、还是压根没声音。
    /// </summary>
    private void TraceBranch(string branch, AudioNowPlayingProbe.ScanResult scan)
    {
        if (branch == _lastBranch) return;
        _lastBranch = branch;
        DzTrace.Always(
            $"[Media] 分支={branch} 设备峰值={scan.SystemPeak:0.0000} " +
            $"在出声=[{string.Join(",", scan.AudibleProcesses)}] " +
            $"认出={(scan.Best == null ? "无" : $"{scan.Best.ProcessName}/'{scan.Best.Title}'")}"
            + (scan.Failed ? $" 探针失败={scan.Failure}" : ""));
    }

    /// <summary>SMTC 会话对应的应用，是不是刚才探到的"在出声"的某个进程。
    /// 名字能从一方的字符串里找到对方就算匹配（AUMID 常是 exe 名或包名，进程名是 exe 名不带扩展名）。</summary>
    private static bool SmtcMatchesAudible(
        GlobalSystemMediaTransportControlsSession session, List<string> audibleProcesses)
    {
        if (audibleProcesses.Count == 0) return false;
        string aumid;
        try { aumid = session.SourceAppUserModelId ?? ""; }
        catch { return true; }                    // 读不到就当作它在播，别抢
        if (aumid.Length == 0) return true;
        foreach (var proc in audibleProcesses)
        {
            if (proc.Length == 0) continue;
            if (aumid.Contains(proc, StringComparison.OrdinalIgnoreCase)
                || proc.Contains(aumid, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>退出兜底模式时清掉记忆（进程关了 / 切回 SMTC 会话）。</summary>
    private void ClearFallbackState()
    {
        _fallbackPid = 0;
        _fallbackTitle = "";
        _fallbackArtist = "";
        _fallbackApp = "";
        _pollNetEaseTrack = false;
        _lastNetEaseTrackId = "";
    }

    /// <summary>
    /// 兜底模式的"补全"：
    /// - **曲目名缺失**（窗口标题没有歌名，比如最小化到托盘 / 迷你播放器）：读网易云的播放缓存 ——
    ///   `Cache\&lt;songId&gt;-320-….uc` 里最新被写的那个文件名就是当前歌曲 id，
    ///   再回它的播放队列 JSON 里查歌名/歌手/封面（细节见 <see cref="MediaArtworkFallback"/>）；
    /// - **缺封面**：拿队列里的 picUrl 下载。
    /// 同一个 key 只试一次，失败 30 秒后再试（网络抖动可自愈）。
    /// </summary>
    private void MaybeEnrichFallback(AudioNowPlayingProbe.Snapshot snapshot)
    {
        if (!FetchExternalArtwork) return;
        bool needTitle = _fallbackTitle.Length == 0;
        bool needCover = Artwork == null;
        if (!needTitle && !needCover) return;
        if (Environment.TickCount64 < _coverRetryAfter) return;

        var isNetEase = MediaArtworkFallback.LooksLikeNetEase(snapshot.ProcessName, _fallbackApp);
        var key = $"{(isNetEase ? "ne" : "x")}|{_fallbackTitle}|{_fallbackArtist}|{(needTitle ? "t" : "")}{(needCover ? "c" : "")}";
        if (key == _coverTryKey) return;
        _coverTryKey = key;

        var title = _fallbackTitle;
        var artist = _fallbackArtist;
        _ = Task.Run(async () =>
        {
            string? newTitle = null;
            string? newArtist = null;
            BitmapSource? art = null;
            try
            {
                MediaArtworkFallback.NetEaseTrack? track = null;
                if (isNetEase)
                {
                    track = title.Length == 0
                        ? MediaArtworkFallback.FindRecentTrack()
                        : MediaArtworkFallback.FindByTitle(title, artist);
                }

                if (track != null)
                {
                    if (title.Length == 0)
                    {
                        newTitle = track.Title;
                        newArtist = track.Artist;
                    }
                    if (needCover) art = await MediaArtworkFallback.TryFetchAsync(track);
                }
                else if (needCover && title.Length > 0)
                {
                    art = await MediaArtworkFallback.TryFetchAsync(title, artist);
                }
            }
            catch (Exception ex) { LogCmd("EnrichFallback", ex); }

            if (newTitle == null && art == null)
            {
                // 这一轮没拿到：清 key、隔 30s 再试，别把这首歌永久判死
                Post(() =>
                {
                    _coverTryKey = "";
                    _coverRetryAfter = Environment.TickCount64 + 30_000;
                });
                return;
            }

            Post(() =>
            {
                if (Now.State != MediaPlaybackState.Playing || !_usingFallback) return;   // 期间切走了 → 丢弃
                if (newTitle != null)
                {
                    _fallbackTitle = newTitle;
                    _fallbackArtist = newArtist ?? "";
                }
                if (art != null)
                {
                    Artwork = art;
                    _artKey = _fallbackTitle + "|" + _fallbackArtist;
                }
                PublishFallback(playing: true, force: true);
                Changed?.Invoke();
            });
        });
    }

    /// <summary>会话选择：优先"正在播放"的会话；否则退回系统当前会话（暂停态也照常显示曲目）。</summary>
    private GlobalSystemMediaTransportControlsSession? PickSession()
    {
        if (_manager == null) return null;
        try
        {
            var current = _manager.GetCurrentSession();
            if (current != null && MapState(current) == MediaPlaybackState.Playing)
                return current;

            foreach (var s in _manager.GetSessions())
            {
                if (MapState(s) == MediaPlaybackState.Playing) return s;
            }
            return current;
        }
        catch
        {
            return _session;
        }
    }

    private static MediaPlaybackState MapState(GlobalSystemMediaTransportControlsSession s)
    {
        try
        {
            var status = s.GetPlaybackInfo()?.PlaybackStatus;
            return status switch
            {
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => MediaPlaybackState.Playing,
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => MediaPlaybackState.Paused,
                _ => MediaPlaybackState.Other,
            };
        }
        catch
        {
            return MediaPlaybackState.Other;
        }
    }

    private void AttachSession(GlobalSystemMediaTransportControlsSession? session)
    {
        if (_session != null)
        {
            try { _session.MediaPropertiesChanged -= OnSessionMediaPropertiesChanged; } catch { }
            try { _session.PlaybackInfoChanged -= OnSessionPlaybackInfoChanged; } catch { }
        }
        _session = session;
        if (_session != null)
        {
            try { _session.MediaPropertiesChanged += OnSessionMediaPropertiesChanged; } catch { }
            try { _session.PlaybackInfoChanged += OnSessionPlaybackInfoChanged; } catch { }
        }
        _artKey = "";
        Artwork = null;
    }

    private void OnSessionMediaPropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender, object args)
        => Post(() => _ = RefreshAsync(force: false));

    private void OnSessionPlaybackInfoChanged(
        GlobalSystemMediaTransportControlsSession sender, object args)
        => Post(() => _ = RefreshAsync(force: false));

    private void Apply(MediaNowPlaying next, bool force)
    {
        var prev = Now;
        Now = next;

        // 封面换了也要通知：换歌时元数据先到、缩略图晚到，那时候曲目/状态都没变
        // （StateEquals 为真），只按状态判断的话唱片机就不会重画，非要暂停再播一下。
        bool artChanged = _artChanged;
        _artChanged = false;

        if (!force && !artChanged && StateEquals(prev, next)) return;
        Post(() => Changed?.Invoke());
    }

    private static bool StateEquals(MediaNowPlaying a, MediaNowPlaying b)
        => a.Title == b.Title && a.Artist == b.Artist && a.Album == b.Album
           && a.State == b.State && a.SourceApp == b.SourceApp;

    // ── 封面 ──

    /// <summary>缩略图流 →（原始字节, FNV-1a 内容哈希）。用 DataReader 分块读，避免依赖
    /// WindowsRuntimeStreamExtensions（.NET 10 的 CsWinRT 投影里没有那个扩展）。
    ///
    /// 分成"读字节"和"解码"两步，是为了在"字节没变"的时候干脆不解码 ——
    /// 换歌后要复查缩略图（见 <see cref="ReadSmtcAsync"/>），复查期间同一张图会被读到好几次。</summary>
    private static async Task<(byte[]? Bytes, ulong Hash)> ReadArtBytesAsync(
        IRandomAccessStreamReference reference)
    {
        try
        {
            using var stream = await reference.OpenReadAsync();
            using var reader = new DataReader(stream) { InputStreamOptions = InputStreamOptions.Partial };
            using var ms = new MemoryStream();
            ulong hash = 14695981039346656037UL;      // FNV-1a offset basis
            uint loaded;
            while ((loaded = await reader.LoadAsync(8192)) > 0)
            {
                var buffer = new byte[loaded];
                reader.ReadBytes(buffer);
                ms.Write(buffer, 0, buffer.Length);
                foreach (var b in buffer)
                {
                    hash ^= b;
                    hash *= 1099511628211UL;
                }
                if (ms.Length > 16 * 1024 * 1024) break; // 防御：异常大的流直接放弃
            }
            return ms.Length == 0 ? (null, 0) : (ms.ToArray(), hash);
        }
        catch (Exception ex)
        {
            DzTrace.Log($"[MediaSession] 封面读取失败: {ex.GetType().Name} {ex.Message}");
            return (null, 0);
        }
    }

    /// <summary>字节 → 冻结的 BitmapSource（可跨线程共享）。</summary>
    private static BitmapSource? DecodeArt(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes, writable: false);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 512;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex)
        {
            DzTrace.Log($"[MediaSession] 封面解码失败: {ex.GetType().Name} {ex.Message}");
            return null;
        }
    }

    // ── 线程 ──

    private static void Post(Action action)
    {
        var d = Application.Current?.Dispatcher;
        if (d == null || d.CheckAccess()) action();
        else d.BeginInvoke(action, DispatcherPriority.Background);
    }

#if DEBUG
    /// <summary>调试/截图用：直接注入一份假的"正在播放"快照。
    /// 离屏渲染或本机没有音乐在播时，用它拿到确定的预览效果。</summary>
    public void InjectPreview(MediaNowPlaying now, BitmapSource? art)
    {
        Now = now;
        Artwork = art;
        Post(() => Changed?.Invoke());
    }

    /// <summary>调试/截图用：暂停/恢复 1s 轮询，避免注入的状态被真实会话覆盖。</summary>
    public void SuspendRefresh(bool suspend)
    {
        if (_poll == null) return;
        if (suspend) _poll.Stop();
        else _poll.Start();
    }
#endif

    /// <summary>进程退出前断开事件（App.OnExit 调用即可，不调也不会泄漏）。</summary>
    public void Shutdown()
    {
        _poll?.Stop();
        _poll = null;
        if (_manager != null)
        {
            try { _manager.CurrentSessionChanged -= OnManagerSessionChanged; } catch { }
            try { _manager.SessionsChanged -= OnManagerSessionChanged; } catch { }
        }
        AttachSession(null);
        _manager = null;
    }
}
