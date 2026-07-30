using System;
using System.Collections.Generic;
using DesktopZones.Models;

namespace DesktopZones.Services;

/// <summary>
/// Saves config while preserving fields that belong to other components
/// (currently Panel hotkey + Panel appearance + Panel background image).
/// Used by all widget services (ZoneManager, WidgetService, NotesService)
/// to avoid clobbering each other's settings when each saves its own subset
/// of AppConfig.
///
/// Pattern: load fresh snapshot, snapshot the Panel-owned fields BEFORE
/// mutation, let the caller mutate everything else, then restore the
/// preserved fields and write to disk. ZoneManager historically preserved
/// 10 fields (hotkey + appearance + bg image); the other two services
/// preserved 5 (hotkey + appearance only). ConfigSaver preserves the
/// strictest set (10 fields) so behavior matches the previous
/// ZoneManager.SaveConfig — the safest choice since losing any Panel
/// field could break a saved config silently.
/// </summary>
public static class ConfigSaver
{
    /// <summary>
    /// Load a fresh config, run <paramref name="mutate"/> on it (caller writes
    /// its own collections/state), then save — restoring all Panel-owned
    /// fields from the pre-mutation snapshot so they don't get clobbered.
    /// </summary>
    public static void SavePreservingPanelSettings(
        ConfigService configService,
        Action<AppConfig> mutate)
    {
        var cfg = configService.Load();

        // Snapshot the Panel-owned fields BEFORE mutation
        bool panelHotkeyEnabled = cfg.PanelHotkeyEnabled;
        int panelHotkeyModifiers = cfg.PanelHotkeyModifiers;
        int panelHotkeyKey = cfg.PanelHotkeyKey;
        List<CustomHotkey> panelCustomHotkeys = cfg.PanelCustomHotkeys;
        bool panelUseGlobalAppearance = cfg.PanelUseGlobalAppearance;
        string panelBackgroundImagePath = cfg.PanelBackgroundImagePath;
        double panelBackgroundImageOpacity = cfg.PanelBackgroundImageOpacity;
        double panelBgImageZoom = cfg.PanelBgImageZoom;
        double panelBgImageOffsetX = cfg.PanelBgImageOffsetX;
        double panelBgImageOffsetY = cfg.PanelBgImageOffsetY;

        // Caller mutates (e.g. assigns Zones / Clocks / Calendars / Notes)
        mutate(cfg);

        // Restore Panel-owned fields AFTER mutation
        cfg.PanelHotkeyEnabled = panelHotkeyEnabled;
        cfg.PanelHotkeyModifiers = panelHotkeyModifiers;
        cfg.PanelHotkeyKey = panelHotkeyKey;
        cfg.PanelCustomHotkeys = panelCustomHotkeys;
        cfg.PanelUseGlobalAppearance = panelUseGlobalAppearance;
        cfg.PanelBackgroundImagePath = panelBackgroundImagePath;
        cfg.PanelBackgroundImageOpacity = panelBackgroundImageOpacity;
        cfg.PanelBgImageZoom = panelBgImageZoom;
        cfg.PanelBgImageOffsetX = panelBgImageOffsetX;
        cfg.PanelBgImageOffsetY = panelBgImageOffsetY;

        configService.Save(cfg);
    }
}