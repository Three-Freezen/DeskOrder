using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using DesktopZones.Models;

namespace DesktopZones.Services;

public class ConfigService
{
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DesktopZones");

    private static readonly string ConfigPath = Path.Combine(ConfigDir, "config.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        // AppConfig.PropertyWindowX / Y default to double.NaN — "no saved position".
        // System.Text.Json refuses to serialize NaN/Inf by default; without this flag
        // every Save() throws ArgumentException and the catch in Save() silently
        // swallows it, so no setting (theme, liquid glass, language, start-with-windows,
        // zone layout, …) ever reaches disk. Allow named FP literals: NaN/Infinity round-trip.
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    // ponytail 2026-08-28: 进程内缓存最近一次 Load/Save 的 AppConfig。本应用是该
    // 文件的唯一写者，缓存即真相：SavePreservingPanelSettings 每次都要"全量读盘+
    // 反序列化→改→序列化→写盘"(72 个调用点，热路径 400ms~1s 防抖)，缓存砍掉其中
    // 读盘+解析的一半；SnapAlignmentService 等自建 ConfigService 实例的调用方也
    // 共享同一份。static 锁保证跨实例互斥。
    private static readonly object _lock = new();
    private static AppConfig? _cached;

    /// <summary>Raised when Save() fails. Subscribers can surface the error in the UI.</summary>
    public event Action<Exception>? SaveFailed;

    public AppConfig Load()
    {
        lock (_lock)
        {
            if (_cached != null) return _cached;
            var (config, cacheable) = LoadFromDiskCore();
            if (cacheable) _cached = config;
            return config;
        }
    }

    private static (AppConfig Config, bool Cacheable) LoadFromDiskCore()
    {
        AppConfig config;
        try
        {
            if (!File.Exists(ConfigPath))
                return (new AppConfig(), true);

            var json = File.ReadAllText(ConfigPath);
            config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
        }
        catch (FileNotFoundException)
        {
            return (new AppConfig(), true);
        }
        catch (JsonException ex)
        {
            // Corrupt JSON — rename the file so the next Save() can't silently
            // overwrite the user's settings with defaults, then start fresh.
            Debug.WriteLine($"[ConfigService] Load failed (corrupt JSON): {ex}");
            try
            {
                var brokenPath = $"{ConfigPath}.broken.{DateTime.Now:yyyyMMddHHmmssfff}";
                File.Move(ConfigPath, brokenPath);
            }
            catch (Exception moveEx)
            {
                Debug.WriteLine($"[ConfigService] Failed to quarantine corrupt config: {moveEx}");
            }
            return (new AppConfig(), true);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            // 瞬时 IO 故障(文件被备份/同步软件锁住等) — 返回默认值但不缓存，
            // 下次 Load 重试磁盘，避免把默认配置固化成本会话的"真相"。
            Debug.WriteLine($"[ConfigService] Load failed (transient IO): {ex}");
            return (new AppConfig(), false);
        }

        MigrateOrphanPanelFields(config);
        MigratePanelGlass(config);
        return (config, true);
    }

    // ── One-time migration: AppConfig-level liquid glass → Panel POCO ──
    // Legacy configs stored the panel's liquid-glass knobs on AppConfig
    // (EnableLiquidGlass / GlassBlurAmount / GlassTintOpacity /
    // GlassTintLuminosity / GlassColorMode). They now live on PanelConfig so
    // the 面板设置 property editor can drive them per-instance like every
    // other component. Copy the legacy values once (PanelGlassMigrated flag
    // prevents re-copying over user edits on later loads).
    private static void MigratePanelGlass(AppConfig config)
    {
        if (config.Panel.PanelGlassMigrated) return;
        config.Panel.PanelEnableLiquidGlass = config.EnableLiquidGlass;
        config.Panel.PanelGlassBlurAmount = config.GlassBlurAmount;
        config.Panel.PanelGlassTintOpacity = config.GlassTintOpacity;
        config.Panel.PanelGlassTintLuminosity = config.GlassTintLuminosity;
        config.Panel.PanelGlassColorMode = config.GlassColorMode;
        config.Panel.PanelGlassMigrated = true;
    }

    // ── One-time migration of legacy flat Panel*/PanelHotkey* fields ──
    // Old config files store these at the top level (e.g. "PanelX": 100).
    // After AppConfig refactor (Panel moved to a nested POCO) System.Text.Json
    // drops them into ExtensionData. Lift them back into Panel/PanelHotkey
    // once, then clear ExtensionData so subsequent saves don't rewrite stale keys.
    private static void MigrateOrphanPanelFields(AppConfig config)
    {
        if (config.ExtensionData == null || config.ExtensionData.Count == 0) return;
        var d = config.ExtensionData;
        var p = config.Panel;
        var h = config.PanelHotkey;
        bool any = false;

        void Move<T>(string key, Action<T> setter)
        {
            if (!d.TryGetValue(key, out var el)) return;
            try { setter(JsonSerializer.Deserialize<T>(el.GetRawText())!); any = true; }
            catch { /* malformed value — ignore */ }
            d.Remove(key);
        }

        Move<double>("PanelX", v => p.PanelX = v);
        Move<double>("PanelY", v => p.PanelY = v);
        Move<double>("PanelWidth", v => p.PanelWidth = v);
        Move<double>("PanelHeight", v => p.PanelHeight = v);
        Move<bool>("PanelEnabled", v => p.PanelEnabled = v);
        Move<string>("PanelTitleBarFillColor", v => p.PanelTitleBarFillColor = v);
        Move<string>("PanelFillColor", v => p.PanelFillColor = v);
        Move<string>("PanelBorderColor", v => p.PanelBorderColor = v);
        Move<double>("PanelControlOpacity", v => p.PanelControlOpacity = v);
        Move<string>("PanelBackgroundImagePath", v => p.PanelBackgroundImagePath = v);
        Move<string>("PanelBgImageStretch", v => p.PanelBgImageStretch = v);
        Move<double>("PanelBackgroundImageOpacity", v => p.PanelBackgroundImageOpacity = v);
        Move<double>("PanelBgImageZoom", v => p.PanelBgImageZoom = v);
        Move<double>("PanelBgImageOffsetX", v => p.PanelBgImageOffsetX = v);
        Move<double>("PanelBgImageOffsetY", v => p.PanelBgImageOffsetY = v);
        Move<double>("PanelHoverExpandSpeed", v => p.PanelHoverExpandSpeed = v);

        Move<bool>("PanelHotkeyEnabled", v => h.PanelHotkeyEnabled = v);
        Move<int>("PanelHotkeyModifiers", v => h.PanelHotkeyModifiers = v);
        Move<int>("PanelHotkeyKey", v => h.PanelHotkeyKey = v);

        // Drop legacy "Theme" so it doesn't get re-serialized (ThemeMode is live).
        d.Remove("Theme");

        if (any) config.ExtensionData = d.Count > 0 ? d : null;
    }

    public void Save(AppConfig config)
    {
        lock (_lock)
        {
            // 先推进内存真相再落盘：写盘失败时内存态仍一致，错误经 SaveFailed 通知 UI。
            _cached = config;
            try
            {
                Directory.CreateDirectory(ConfigDir);
                var json = JsonSerializer.Serialize(config, JsonOptions);
                // temp + Move(overwrite) 原子替换：崩溃/断电不会留下半截 JSON
                // (旧实现直接 WriteAllText，只能靠 Load 侧 quarantine 兜底)。
                var tmpPath = ConfigPath + ".tmp";
                File.WriteAllText(tmpPath, json);
                File.Move(tmpPath, ConfigPath, overwrite: true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ConfigService] Save failed: {ex}");
                SaveFailed?.Invoke(ex);
            }
        }
    }
}
