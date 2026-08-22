using System;
using DesktopZones.Models;

namespace DesktopZones.Services;

/// <summary>
/// Saves config while preserving fields owned by other components
/// (Panel config + Panel hotkey + custom hotkeys list).
/// Used by all widget services (ZoneManager, WidgetService, NotesService)
/// to avoid clobbering each other's settings when each saves its own
/// subset of AppConfig.
///
/// Pattern: load fresh snapshot, snapshot the Panel-owned POCOs + orphan
/// list BEFORE mutation, let the caller mutate everything else, then
/// restore the preserved references and write to disk.
/// ponytail: single-line reference copies instead of listing every field —
/// adding a new Panel* field can't silently get clobbered by a Save call.
/// </summary>
public static class ConfigSaver
{
    /// <summary>
    /// Load a fresh config, run <paramref name="mutate"/> on it (caller writes
    /// its own collections/state), then save — restoring the Panel POCOs from
    /// the pre-mutation snapshot so they don't get clobbered.
    /// </summary>
    public static void SavePreservingPanelSettings(
        ConfigService configService,
        Action<AppConfig> mutate)
    {
        var cfg = configService.Load();

        // Snapshot the Panel-owned POCOs + orphan hotkey list BEFORE mutation
        var panelSnapshot = cfg.Panel;
        var hotkeySnapshot = cfg.PanelHotkey;
        var customHotkeysSnapshot = cfg.PanelCustomHotkeys;

        // Caller mutates (e.g. assigns Zones / Clocks / Calendars / Notes)
        mutate(cfg);

        // Restore Panel-owned state AFTER mutation
        cfg.Panel = panelSnapshot;
        cfg.PanelHotkey = hotkeySnapshot;
        cfg.PanelCustomHotkeys = customHotkeysSnapshot;

        configService.Save(cfg);
    }
}
