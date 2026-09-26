using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopZones.Models;

public class AppConfig
{
    private List<Zone> _zones = new();
    public List<Zone> Zones
    {
        get => _zones;
        set => _zones = value ?? new();
    }
    public bool StartWithWindows { get; set; } = true;
    public bool StartMinimized { get; set; } = true;
    public bool ShowAllOnStartup { get; set; } = true;

    // ── 自动对齐：拖动组件时显示对齐线并自动吸附（面板除外） ──
    public bool AutoAlign { get; set; } = true;

    // ── 逆向同步：原文件消失/变更时自动删除分区图标 ──
    public bool ReverseSyncEnabled { get; set; } = true;

    // ── 分区图片预览：导入的图片文件显示内容缩略图而非默认图标 ──
    public bool ImagePreviewEnabled { get; set; } = true;

    // ── 更新 ──
    // 启动后台检查更新（24h 节流，UpdateService.AutoCheckIfDueAsync 消费）。
    public bool AutoCheckUpdate { get; set; } = true;
    /// <summary>更新完成后自动删除下载文件夹里的安装包（UpdateService 待清理标记消费）。</summary>
    public bool DeleteSetupAfterUpdate { get; set; } = false;
    /// <summary>上次更新检查时间（UTC）；default = 从未检查。失败也记录，避免断网时反复撞接口。</summary>
    public DateTime LastUpdateCheckUtc { get; set; }

    // ── Theme selection (three-valued; replaces ambiguous single `Theme`) ──
    /// <summary>"System" / "Light" / "Dark". Defaults to "Light" — 新用户首启给浅色
    /// 管理界面（分区主体深浅不受此影响）；已有配置保留用户自己的选择。</summary>
    public string ThemeMode { get; set; } = "Light";

    // Legacy single-value theme field — kept [Obsolete] + [JsonIgnore] so old
    // config files still parse (the value lands in ExtensionData and is dropped
    // silently; ThemeMode is the live field). Do not reference in new code.
    [Obsolete("Use ThemeMode instead")]
    [JsonIgnore]
    public string Theme { get; set; } = "default";

    // ── Language ──
    public string Language { get; set; } = "zh"; // "zh" / "en"

    // ── Liquid Glass (ZenDesktop-style) ──
    public bool EnableLiquidGlass { get; set; } = true;
    public int GlassBlurAmount { get; set; } = 18;       // 0-60, default 18 = ZenDesktop standard
    public int GlassTintOpacity { get; set; } = 50;       // 0-100%
    public int GlassTintLuminosity { get; set; } = 100;   // 0-150%
    public string GlassColorMode { get; set; } = "Default"; // color preset name
    /// <summary>ponytail 2026-09-26: 材质预设 key(历史遗留层,面板用 PanelGlassMaterial)。
    /// 空串 = 从没选过(渲染同「默认」= 历史亚克力背板);见 AcrylicHelper.CustomMaterialKey。</summary>
    public string GlassMaterial { get; set; } = "";

    /// <summary>ponytail 2026-09-26(二期): 全局渲染方案开关 —— true = 新方案(壁纸采样自绘
    /// 背板,模糊半径真正生效,但要多花一次壁纸采样/预模糊的开销);false = 原 DWM 方案(默认)。
    /// **渲染侧以本开关为唯一判据**(经 <see cref="WallpaperSource.Enabled"/>);
    /// 早期版本让每个窗口读自己的 <c>UseWallpaperRenderer</c> 快照,开关打开后新建的对象拿到
    /// 构造默认值 false,永远走 DWM —— 现已改为全局判据,逐对象字段仅作兼容保留。
    /// 本开关同时决定是否做壁纸检测/抓屏:关掉时零开销。</summary>
    public bool UseWallpaperRenderer { get; set; } = false;

    // ── Panel (POCOs, was 19 inline fields) ──
    public PanelConfig Panel { get; set; } = new();
    public PanelHotkeyConfig PanelHotkey { get; set; } = new();

    // ponytail 2026-08-27: 全部显示 / 全部隐藏 / 全部最小化 全局热键。
    public CustomHotkey ShowAllHotkey { get; set; } = new() { Modifiers = 0x0006, Key = 0x41 }; // Ctrl+Shift+A
    public CustomHotkey HideAllHotkey { get; set; } = new() { Modifiers = 0x0006, Key = 0x48 }; // Ctrl+Shift+H
    public CustomHotkey MinimizeAllHotkey { get; set; } = new() { Modifiers = 0x0006, Key = 0x4D }; // Ctrl+Shift+M

    // ponytail 2026-08-27: 双击桌面切换全部显示/隐藏。仅在桌面区有效,其他窗口不拦截。
    public bool DoubleClickToggleShowHide { get; set; } = true;
    /// <summary>Orphan: user-added extra hotkeys (not the primary toggle).
    /// Stays on AppConfig because it is rarely touched and the primary hotkey
    /// state lives in <see cref="PanelHotkey"/>.</summary>
    public List<CustomHotkey> PanelCustomHotkeys { get; set; } = new();

    // ── Widgets ──
    private List<StickyNote> _notes = new();
    public List<StickyNote> Notes
    {
        get => _notes;
        set => _notes = value ?? new();
    }
    private List<DesktopClock> _clocks = new();
    public List<DesktopClock> Clocks
    {
        get => _clocks;
        set => _clocks = value ?? new();
    }
    private List<DesktopCalendar> _calendars = new();
    public List<DesktopCalendar> Calendars
    {
        get => _calendars;
        set => _calendars = value ?? new();
    }

    // ── Property window (per-instance design system, spec §7.1 #6) ──
    public double PropertyWindowX { get; set; } = double.NaN;
    public double PropertyWindowY { get; set; } = double.NaN;
    public double PropertyWindowWidth { get; set; } = 360;
    public double PropertyWindowHeight { get; set; } = 600;
    public bool PropertyWindowTopmost { get; set; } = true;
    public bool PropertyPanelCollapsed { get; set; } = false;

    // ponytail: per-instance position persistence — spec §2.4 says "位置与尺寸
    // 持久化,按实例 Id 记忆". Keys are stable target Ids (Zone.Id, Calendar.Id,
    // Clock.Id, Note.Id, MergedGroup.Id). Kept here (not nested in Panel POCO)
    // because it's keyed by domain instance, not panel chrome.
    // Migration: on first load, copy the legacy global X/Y/Width/Height into
    // PropertyWindowRects["__default__"] so existing single-window layouts
    // survive the upgrade.
    public Dictionary<string, RectLite> PropertyWindowRects { get; set; } = new();

    /// <summary>
    /// Captures top-level JSON fields with no matching property — used by
    /// <c>ConfigService</c> for one-time migration of legacy flat Panel* fields
    /// into the new nested <see cref="Panel"/> POCO. Cleared after migration.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public class CustomHotkey
{
    public int Modifiers { get; set; }
    public int Key { get; set; }
}

/// <summary>
/// Plain-data rectangle used in JSON-serialised config (no WPF dependency).
/// Fields are public + settable so System.Text.Json round-trips them; an
/// implicit conversion to/from <c>System.Windows.Rect</c> lives in
/// <c>PropertyWindowManager</c> where the WPF ref is already present.
/// ponytail: kept as a POCO instead of <c>[Serializable] struct</c> so
/// older .NET config files (no struct ctor) still load via property setters.
/// </summary>
public class RectLite
{
    public double X { get; set; } = double.NaN;
    public double Y { get; set; } = double.NaN;
    public double Width { get; set; } = 360;
    public double Height { get; set; } = 600;

    public bool IsValid => !double.IsNaN(X) && !double.IsNaN(Y) && Width > 0 && Height > 0;
}
