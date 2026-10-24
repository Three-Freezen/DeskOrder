using System;
using System.Collections;
using System.Collections.ObjectModel;
using System.Linq;
using DesktopZones.Helpers;
using DesktopZones.Models;
using DesktopZones.Views;

namespace DesktopZones.Services;

public class WidgetService
{
    private readonly ConfigService _configService;
    private AppConfig? _appConfig;

    public ObservableCollection<DesktopClock> Clocks { get; } = new();
    public ObservableCollection<DesktopCalendar> Calendars { get; } = new();
    /// <summary>「播放器」组件（律动条 / 唱片机）。</summary>
    public ObservableCollection<DesktopMediaPlayer> MediaPlayers { get; } = new();
    public event Action? ClocksChanged;
    public event Action? CalendarsChanged;
    public event Action? MediaPlayersChanged;
    /// <summary>Fires when a widget's lock state changes. Args: widgetId (string), isLocked.</summary>
    public event Action<string, bool>? LockChanged;

    /// <summary>Open ClockWidget instances keyed by clock Id (was App._clockWindows before P5).</summary>
    public Dictionary<Guid, ClockWidget> ClockWindows { get; } = new();
    /// <summary>Open CalendarWidget instances keyed by calendar Id (was App._calendarWindows before P5).</summary>
    public Dictionary<Guid, CalendarWidget> CalendarWindows { get; } = new();
    /// <summary>Open MediaPlayerWidget instances keyed by player Id.</summary>
    public Dictionary<Guid, MediaPlayerWidget> MediaWindows { get; } = new();

    public ClockWidget? GetClockWindow(Guid id)
        => ClockWindows.TryGetValue(id, out var w) ? w : null;
    public CalendarWidget? GetCalendarWindow(Guid id)
        => CalendarWindows.TryGetValue(id, out var w) ? w : null;
    public MediaPlayerWidget? GetMediaPlayerWindow(Guid id)
        => MediaWindows.TryGetValue(id, out var w) ? w : null;

    public AppConfig GetConfig() => _appConfig!;

    public WidgetService(ConfigService configService)
    {
        _configService = configService;
    }

    public void Load(AppConfig config)
    {
        _appConfig = config;
        Clocks.Clear();
        Calendars.Clear();
        MediaPlayers.Clear();
        foreach (var c in config.Clocks) Clocks.Add(c);
        foreach (var c in config.Calendars) Calendars.Add(c);
        foreach (var m in config.MediaPlayers)
        {
            // 旧配置里幅度/灵敏度是倍率（0.2–2.0 / 0.5–4.0），这里迁到 1–100 档位。
            // 不依赖反序列化回调（STJ 不保证调 [OnDeserialized]），显式做，且幂等。
            m.MigrateBarsScale();
            MediaPlayers.Add(m);
        }
        // Release 也写：用户报"重启后播放器不见了"时，这一行直接说明是配置里没有
        // 还是窗口没恢复。
        DzTrace.Always($"[Widgets] 载入: 时钟 {Clocks.Count} / 日历 {Calendars.Count} / 播放器 {MediaPlayers.Count}");
    }

    // ── Media player（播放器）──

    /// <summary>创建播放器。<paramref name="mode"/> 创建后不可改；磁贴模式默认关闭
    /// （两种形态一致，想要"只留内容"的观感再在属性面板里打开）。</summary>
    public DesktopMediaPlayer CreateMediaPlayer(MediaDisplayMode mode, double x = 320, double y = 160)
    {
        var player = new DesktopMediaPlayer
        {
            Mode = mode,
            TileMode = false,
            BarsScaleV2 = true,     // 新对象直接用 1–100 档位，不做旧倍率迁移
            X = x,
            Y = y,
        };
        MediaPlayers.Add(player);
        Save();
        DzTrace.Always($"[Widgets] 新建播放器 {player.Id} mode={mode} 现有 {MediaPlayers.Count} 个");
        MediaPlayersChanged?.Invoke();
        return player;
    }

    public void UpdateMediaPlayer(DesktopMediaPlayer player)
    {
        var existing = MediaPlayers.FirstOrDefault(c => c.Id == player.Id);
        if (existing != null)
        {
            var idx = MediaPlayers.IndexOf(existing);
            MediaPlayers[idx] = player;
        }
        Save();
        MediaPlayersChanged?.Invoke();
    }

    public void DeleteMediaPlayer(Guid id)
    {
        var p = MediaPlayers.FirstOrDefault(x => x.Id == id);
        if (p != null)
        {
            Helpers.PropertyWindowService.CloseEditorsFor(p);
            MediaPlayers.Remove(p);
        }
        Save();
        DzTrace.Always($"[Widgets] 删除播放器 {id} 剩余 {MediaPlayers.Count} 个");
        MediaPlayersChanged?.Invoke();
    }

    /// <summary>拖动排序（与时钟一致，不改动窗口）。</summary>
    public void MoveMediaPlayer(Guid playerId, int newIndex)
    {
        var p = MediaPlayers.FirstOrDefault(x => x.Id == playerId);
        if (p == null) return;
        int oldIndex = MediaPlayers.IndexOf(p);
        if (oldIndex < 0 || oldIndex == newIndex) return;
        if (newIndex < 0) newIndex = 0;
        if (newIndex > MediaPlayers.Count - 1) newIndex = MediaPlayers.Count - 1;
        MediaPlayers.Move(oldIndex, newIndex);
        Save();
    }

    /// <summary>供 LoadPresetDialog 之类的预览用：最近添加的可见播放器。</summary>
    public DesktopMediaPlayer? GetActiveMediaPlayer()
        => MediaPlayers.LastOrDefault(p => p.IsVisible);

    // ── Clock ──

    public DesktopClock CreateClock(double x = 300, double y = 100)
    {
        var clock = new DesktopClock { X = x, Y = y };
        Clocks.Add(clock);
        Save();
        ClocksChanged?.Invoke();
        return clock;
    }

    public void UpdateClock(DesktopClock clock)
    {
        var existing = Clocks.FirstOrDefault(c => c.Id == clock.Id);
        if (existing != null) { var idx = Clocks.IndexOf(existing); Clocks[idx] = clock; }
        Save();
        ClocksChanged?.Invoke();
    }

    public void DeleteClock(Guid id)
    {
        var c = Clocks.FirstOrDefault(x => x.Id == id);
        if (c != null)
        {
            // 同步关闭该时钟的样式设置界面(浮动 + 停靠),避免残留编辑器。
            PropertyWindowService.CloseEditorsFor(c);
            Clocks.Remove(c);
        }
        Save();
        ClocksChanged?.Invoke();
    }

    /// <summary>Move a clock to a new position (long-press drag reorder).
    /// ObservableCollection.Move fires CollectionChanged → ItemsControl reorders.
    /// Skips ClocksChanged so RefreshList doesn't rebuild rows mid-drag.</summary>
    public void MoveClock(Guid clockId, int newIndex)
    {
        var c = Clocks.FirstOrDefault(x => x.Id == clockId);
        if (c == null) return;
        int oldIndex = Clocks.IndexOf(c);
        if (oldIndex < 0 || oldIndex == newIndex) return;
        if (newIndex < 0) newIndex = 0;
        if (newIndex > Clocks.Count - 1) newIndex = Clocks.Count - 1;
        Clocks.Move(oldIndex, newIndex);
        Save();
    }

    /// <summary>
    /// The clock the user is most likely interacting with — currently the most
    /// recently added visible clock. Used by <see cref="Views.LoadPresetDialog"/>
    /// to live-track which mode (Digital / Analog) to show in the ClockCard preview.
    /// Returns null if no visible clock exists.
    /// </summary>
    public DesktopClock? GetActiveClock()
    {
        return Clocks.LastOrDefault(c => c.IsVisible);
    }

    // ── Calendar ──

    public DesktopCalendar CreateCalendar(double x = 400, double y = 100)
    {
        var cal = new DesktopCalendar();
        cal.X = x; cal.Y = y;
        Calendars.Add(cal);
        Save();
        CalendarsChanged?.Invoke();
        return cal;
    }

    public void UpdateCalendar(DesktopCalendar cal)
    {
        var existing = Calendars.FirstOrDefault(c => c.Id == cal.Id);
        if (existing != null) { var idx = Calendars.IndexOf(existing); Calendars[idx] = cal; }
        Save();
        CalendarsChanged?.Invoke();
    }

    public void DeleteCalendar(Guid id)
    {
        var c = Calendars.FirstOrDefault(x => x.Id == id);
        if (c != null)
        {
            // 同步关闭该日历的样式设置界面(浮动 + 停靠),避免残留编辑器。
            PropertyWindowService.CloseEditorsFor(c);
            Calendars.Remove(c);
        }
        Save();
        CalendarsChanged?.Invoke();
    }

    /// <summary>Move a calendar to a new position (long-press drag reorder).
    /// ObservableCollection.Move fires CollectionChanged → ItemsControl reorders.
    /// Skips CalendarsChanged so RefreshList doesn't rebuild rows mid-drag.</summary>
    public void MoveCalendar(Guid calendarId, int newIndex)
    {
        var c = Calendars.FirstOrDefault(x => x.Id == calendarId);
        if (c == null) return;
        int oldIndex = Calendars.IndexOf(c);
        if (oldIndex < 0 || oldIndex == newIndex) return;
        if (newIndex < 0) newIndex = 0;
        if (newIndex > Calendars.Count - 1) newIndex = Calendars.Count - 1;
        Calendars.Move(oldIndex, newIndex);
        Save();
    }

    public void Save()
    {
        if (_appConfig == null)
        {
            // 正常流程走不到这里（App 启动即 Load）；真出现了说明"新建了组件但没载入配置"，
            // 症状就是"重启后组件全丢" —— 记一行，别再静默返回。
            DzTrace.Always("[Widgets] Save 被忽略：配置尚未载入");
            return;
        }
        ConfigSaver.SavePreservingPanelSettings(_configService, cfg =>
        {
            cfg.Clocks = Clocks.ToList();
            cfg.Calendars = Calendars.ToList();
            cfg.MediaPlayers = MediaPlayers.ToList();
        });
    }

    // ── Lock ──

    /// <summary>Set locked state for a widget (Clock / Calendar / Media player) by string id.
    /// Fires LockChanged after state update.</summary>
    public void SetLocked(string widgetId, bool locked)
    {
        if (!Guid.TryParse(widgetId, out var guid)) return;
        var clock = Clocks.FirstOrDefault(c => c.Id == guid);
        if (clock != null)
        {
            clock.IsLocked = locked;
            LockChanged?.Invoke(widgetId, locked);
            return;
        }
        var cal = Calendars.FirstOrDefault(c => c.Id == guid);
        if (cal != null)
        {
            cal.IsLocked = locked;
            LockChanged?.Invoke(widgetId, locked);
            return;
        }
        var media = MediaPlayers.FirstOrDefault(c => c.Id == guid);
        if (media != null)
        {
            media.IsLocked = locked;
            LockChanged?.Invoke(widgetId, locked);
        }
    }
}
