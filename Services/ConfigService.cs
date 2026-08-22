using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
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

    private readonly object _lock = new();

    /// <summary>Raised when Save() fails. Subscribers can surface the error in the UI.</summary>
    public event Action<Exception>? SaveFailed;

    public AppConfig Load()
    {
        AppConfig config;
        try
        {
            if (!File.Exists(ConfigPath))
                return new AppConfig();

            var json = File.ReadAllText(ConfigPath);
            config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
        }
        catch (FileNotFoundException)
        {
            return new AppConfig();
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
            return new AppConfig();
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            Debug.WriteLine($"[ConfigService] Load failed (transient IO): {ex}");
            return new AppConfig();
        }

        MigrateGlobalAppearance(config);
        MigrateOrphanPanelFields(config);
        return config;
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
        Move<bool>("PanelUseGlobalAppearance", v => p.PanelUseGlobalAppearance = v);
        Move<string>("PanelTitleBarFillColor", v => p.PanelTitleBarFillColor = v);
        Move<string>("PanelFillColor", v => p.PanelFillColor = v);
        Move<bool>("PanelTextColorAdaptive", v => p.PanelTextColorAdaptive = v);
        Move<bool>("PanelTitleBarTextColorAdaptive", v => p.PanelTitleBarTextColorAdaptive = v);
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
            try
            {
                Directory.CreateDirectory(ConfigDir);
                var json = JsonSerializer.Serialize(config, JsonOptions);
                File.WriteAllText(ConfigPath, json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ConfigService] Save failed: {ex}");
                SaveFailed?.Invoke(ex);
            }
        }
    }

    // ── One-time global → per-instance migration (spec §7.1 #1) ──
    // Ponytail: reflection walks each instance for BorderColor/FillColor and
    // back-fills from the global value if empty. Idempotent: guarded by
    // GlobalAppearanceMigrated flag. Adds EnableRestoreButton=false to panel
    // presets (spec §7.2 removed the restore button).
    private void MigrateGlobalAppearance(AppConfig config)
    {
        if (config.GlobalAppearanceMigrated) return;

        var globalBorder = config.GlobalBorderColor;
        var globalFill = config.GlobalFillColor;

        foreach (var z in config.Zones)
        {
            EnsureAppearance(z, globalBorder, globalFill);
        }
        foreach (var n in config.Notes)
        {
            EnsureAppearance(n, globalBorder, globalFill);
        }
        foreach (var c in config.Clocks)
        {
            EnsureAppearance(c, globalBorder, globalFill);
        }
        foreach (var cal in config.Calendars)
        {
            EnsureAppearance(cal, globalBorder, globalFill);
        }

        // Panel never has a restore button (spec §7.2).
        if (config.Panel.PanelHoverExpandSpeed <= 0) config.Panel.PanelHoverExpandSpeed = 1.0;

        config.GlobalAppearanceMigrated = true;
        Save(config);
    }

    private static void EnsureAppearance(object model, string globalBorder, string globalFill)
    {
        if (model is null) return;
        var t = model.GetType();
        var borderProp = t.GetProperty("BorderColor", BindingFlags.Public | BindingFlags.Instance);
        var fillProp = t.GetProperty("FillColor", BindingFlags.Public | BindingFlags.Instance);
        if (borderProp?.CanWrite == true)
        {
            var current = borderProp.GetValue(model) as string;
            if (string.IsNullOrEmpty(current)) borderProp.SetValue(model, globalBorder);
        }
        if (fillProp?.CanWrite == true)
        {
            var current = fillProp.GetValue(model) as string;
            if (string.IsNullOrEmpty(current)) fillProp.SetValue(model, globalFill);
        }
    }
}
