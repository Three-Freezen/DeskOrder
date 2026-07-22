using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using DesktopZones.Models;
using DesktopZones.Views;

namespace DesktopZones.Services;

public class ZoneManager
{
    private readonly ConfigService _configService;
    private readonly Dictionary<Guid, ZoneWindow> _zoneWindows = new();
    private AppConfig _config;

    // Guard flag to prevent re-entrant batch operations
    private bool _isBatchOperation;

    public ObservableCollection<Zone> Zones { get; } = new();

    public event Action? ZonesChanged;
    /// <summary>Fires when a zone's visibility changes (show/hide/close). Args: zoneId, isVisible</summary>
    public event Action<Guid, bool>? ZoneVisibilityChanged;

    /// <summary>Manually fire ZoneVisibilityChanged (for ZoneWindow internal state changes).</summary>
    public void FireZoneVisibilityChanged(Guid zoneId, bool isVisible)
        => ZoneVisibilityChanged?.Invoke(zoneId, isVisible);

    public ZoneManager(ConfigService configService)
    {
        _configService = configService;
        _config = configService.Load();
    }

    public void Initialize()
    {
        foreach (var zone in _config.Zones)
        {
            Zones.Add(zone);
            if (zone.IsVisible)
                ShowZone(zone);
        }
    }

    public Zone CreateZone(string name = "New Zone", double x = 200, double y = 200,
        double width = 400, double height = 300)
    {
        var zone = new Zone
        {
            Name = name,
            X = x,
            Y = y,
            Width = width,
            Height = height
        };

        _config.Zones.Add(zone);
        Zones.Add(zone);
        SaveConfig();
        ShowZone(zone);
        ZonesChanged?.Invoke();
        return zone;
    }

    public void DeleteZone(Guid zoneId)
    {
        var toDelete = Zones.FirstOrDefault(z => z.Id == zoneId);
        if (toDelete == null) return;

        // If this zone is part of a merged group, disband it first
        if (toDelete.MergedGroupId.HasValue)
        {
            if (toDelete.MergedSubZoneIds.Count > 0)
                DisbandMergedGroup(toDelete.MergedGroupId.Value);
            else
                RemoveFromMergedGroup(zoneId);
        }

        // Close and remove the window completely
        if (_zoneWindows.TryGetValue(zoneId, out var window))
        {
            window.Close();
            _zoneWindows.Remove(zoneId);
        }

        var zone = _config.Zones.FirstOrDefault(z => z.Id == zoneId);
        if (zone != null)
            _config.Zones.Remove(zone);

        var toRemove = Zones.FirstOrDefault(z => z.Id == zoneId);
        if (toRemove != null)
            Zones.Remove(toRemove);

        SaveConfig();
        ZonesChanged?.Invoke();
    }

    public void ShowZone(Zone zone)
    {
        // If this zone is a sub-zone of a merged group, show the master instead
        if (zone.MergedGroupId.HasValue && zone.MergedSubZoneIds.Count == 0)
        {
            var master = Zones.FirstOrDefault(z => z.MergedGroupId == zone.MergedGroupId && z.MergedSubZoneIds.Count > 0);
            if (master != null)
            {
                ShowZone(master);
                // Try to set the master to show the requested sub-zone's items
                if (_zoneWindows.TryGetValue(master.Id, out var masterWin) && masterWin?.IsLoaded == true)
                {
                    var vm = masterWin.DataContext as ViewModels.ZoneViewModel;
                    if (vm != null) vm.SelectedSubZoneId = zone.Id;
                }
                return;
            }
        }

        // Set IsVisible BEFORE creating the window to prevent constructor from calling ApplyHidden()
        zone.IsVisible = true;

        if (_zoneWindows.ContainsKey(zone.Id))
        {
            _zoneWindows[zone.Id].ShowZone();
        }
        else
        {
            var window = new ZoneWindow(zone, this, new ShellIconService());
            window.Show();
            _zoneWindows[zone.Id] = window;
        }
        SaveConfig();
        ZonesChanged?.Invoke();
        ZoneVisibilityChanged?.Invoke(zone.Id, true);
    }

    public void HideZone(Guid zoneId)
    {
        var zone = Zones.FirstOrDefault(z => z.Id == zoneId);
        if (_zoneWindows.TryGetValue(zoneId, out var window))
        {
            window.HideZone();
            // If EnableRestoreButton is false, remove window from dictionary (like FullHideZone)
            if (zone != null && !zone.EnableRestoreButton)
            {
                _zoneWindows.Remove(zoneId);
            }
        }
        if (zone != null)
            zone.IsVisible = false;
        SaveConfig();
        ZonesChanged?.Invoke();
        ZoneVisibilityChanged?.Invoke(zoneId, false);
    }

    public void ToggleZone(Guid zoneId)
    {
        var zone = Zones.FirstOrDefault(z => z.Id == zoneId);
        if (zone == null) return;

        if (zone.IsVisible)
            HideZone(zoneId);
        else
            ShowZone(zone);
    }

    public void ShowAll()
    {
        if (_isBatchOperation) return;
        _isBatchOperation = true;
        try
        {
            foreach (var zone in Zones)
            {
                // Skip sub-zones that belong to a merged group (handled by master)
                if (zone.MergedGroupId.HasValue && zone.MergedSubZoneIds.Count == 0)
                    continue;
                ShowZone(zone);
            }
        }
        finally { _isBatchOperation = false; }
    }

    public void HideAll()
    {
        if (_isBatchOperation) return;
        _isBatchOperation = true;
        try
        {
            foreach (var zone in Zones)
                HideZone(zone.Id);
        }
        finally { _isBatchOperation = false; }
    }

    /// <summary>Fully close the zone window (no restore button).</summary>
    public void FullHideZone(Guid zoneId)
    {
        if (_zoneWindows.TryGetValue(zoneId, out var window))
        {
            var z = Zones.FirstOrDefault(x => x.Id == zoneId);
            if (z != null) { z.Width = window.Width; z.Height = window.Height; z.X = window.Left; z.Y = window.Top; }
            window.Close();
            _zoneWindows.Remove(zoneId);
        }
        var zone = Zones.FirstOrDefault(z => z.Id == zoneId);
        if (zone != null) zone.IsVisible = false;
        SaveConfig();
        ZonesChanged?.Invoke();
        ZoneVisibilityChanged?.Invoke(zoneId, false);
    }

    /// <summary>Fully close all zone windows.</summary>
    public void FullHideAll()
    {
        if (_isBatchOperation) return;
        _isBatchOperation = true;
        try
        {
            foreach (var kv in _zoneWindows.ToList())
            {
                var z = Zones.FirstOrDefault(x => x.Id == kv.Key);
                if (z != null) { z.Width = kv.Value.Width; z.Height = kv.Value.Height; z.X = kv.Value.Left; z.Y = kv.Value.Top; }
                kv.Value.Close();
            }
            _zoneWindows.Clear();
            foreach (var zone in Zones) zone.IsVisible = false;
            SaveConfig();
            ZonesChanged?.Invoke();
        }
        finally { _isBatchOperation = false; }
    }

    public bool IsZoneShown(Guid zoneId) => _zoneWindows.ContainsKey(zoneId);
    public bool IsZoneMinimized(Guid zoneId) => _zoneWindows.TryGetValue(zoneId, out var w) && w.RestoreButton.Visibility == System.Windows.Visibility.Visible;

    public void ToggleAll()
    {
        bool anyVisible = Zones.Any(z => z.IsVisible);
        if (anyVisible)
            HideAll();
        else
            ShowAll();
    }

    public void UpdateZone(Zone updatedZone)
    {
        var zone = _config.Zones.FirstOrDefault(z => z.Id == updatedZone.Id);
        if (zone == null) return;

        var index = _config.Zones.IndexOf(zone);
        _config.Zones[index] = updatedZone;

        var listIndex = -1;
        for (int i = 0; i < Zones.Count; i++)
        {
            if (Zones[i].Id == updatedZone.Id)
            {
                listIndex = i;
                break;
            }
        }
        if (listIndex >= 0)
            Zones[listIndex] = updatedZone;

        // Refresh the window
        if (_zoneWindows.TryGetValue(updatedZone.Id, out var window))
        {
            window.RefreshZone(updatedZone);
            if (updatedZone.IsVisible)
                window.ShowZone();
            else
                window.HideZone();
        }
        else if (updatedZone.IsVisible)
        {
            ShowZone(updatedZone);
        }

        SaveConfig();
        ZonesChanged?.Invoke();
    }

    public void SaveConfig()
    {
        // Reload config to preserve settings managed by other components (e.g., hotkeys)
        var latestConfig = _configService.Load();
        _config.Zones = Zones.ToList();
        // Preserve hotkey and other settings from the latest config
        _config.PanelHotkeyEnabled = latestConfig.PanelHotkeyEnabled;
        _config.PanelHotkeyModifiers = latestConfig.PanelHotkeyModifiers;
        _config.PanelHotkeyKey = latestConfig.PanelHotkeyKey;
        _config.PanelCustomHotkeys = latestConfig.PanelCustomHotkeys;
        _config.PanelBackgroundImagePath = latestConfig.PanelBackgroundImagePath;
        // PanelBgImageStretch: always UniformToFill
        _config.PanelBackgroundImageOpacity = latestConfig.PanelBackgroundImageOpacity;
        _config.PanelBgImageZoom = latestConfig.PanelBgImageZoom;
        _config.PanelBgImageOffsetX = latestConfig.PanelBgImageOffsetX;
        _config.PanelBgImageOffsetY = latestConfig.PanelBgImageOffsetY;
        _configService.Save(_config);
    }

    public AppConfig GetConfig() => _config;

    /// <summary>Notify all listeners that zones have changed (for item-level changes).</summary>
    public void NotifyChanged() => ZonesChanged?.Invoke();

    /// <summary>Save global appearance and sync all open zone windows.</summary>
    public void SaveGlobalAppearance(string borderColor, string fillColor, double borderThickness)
    {
        _config.GlobalBorderColor = borderColor;
        _config.GlobalFillColor = fillColor;
        _config.GlobalBorderThickness = borderThickness;
        _config.UseGlobalAppearance = true;
        SaveConfig();

        // Sync all open windows
        foreach (var kv in _zoneWindows)
        {
            kv.Value.ApplyStyle();
        }
    }

    /// <summary>Toggle whether zones use global or per-zone appearance.</summary>
    public void SetUseGlobalAppearance(bool useGlobal)
    {
        _config.UseGlobalAppearance = useGlobal;
        SaveConfig();
        foreach (var kv in _zoneWindows)
        {
            kv.Value.ApplyStyle();
        }
    }

    public void UpdateConfig(AppConfig config)
    {
        _config = config;
        SaveConfig();
    }

    public void Shutdown()
    {
        foreach (var window in _zoneWindows.Values)
        {
            window.Close();
        }
        _zoneWindows.Clear();
    }

    // ── Zone Merge ──

    /// <summary>Merge zoneB into zoneA's group. If neither is merged, creates a new group.
    /// If zoneA is already a merged master, adds zoneB as a sub-zone.</summary>
    public Guid MergeZones(Guid zoneAId, Guid zoneBId)
    {
        var zoneA = Zones.FirstOrDefault(z => z.Id == zoneAId);
        var zoneB = Zones.FirstOrDefault(z => z.Id == zoneBId);
        if (zoneA == null || zoneB == null || zoneAId == zoneBId)
            return Guid.Empty;

        Guid groupId;
        Zone master;

        if (zoneA.MergedGroupId.HasValue)
        {
            groupId = zoneA.MergedGroupId.Value;
            master = Zones.FirstOrDefault(z => z.MergedGroupId == groupId && z.MergedSubZoneIds.Count > 0)
                     ?? zoneA;
        }
        else
        {
            groupId = Guid.NewGuid();
            master = zoneA;
            zoneA.MergedGroupId = groupId;
        }

        // If zoneB is already in a merged group, remove it first
        if (zoneB.MergedGroupId.HasValue)
            RemoveFromMergedGroup(zoneB.Id);

        zoneB.MergedGroupId = groupId;
        master.MergedSubZoneIds.Add(zoneB.Id);
        master.MergedGroupName = BuildMergedGroupName(master);
        zoneB.MergedGroupName = master.MergedGroupName;

        // Hide sub-zone window
        FullHideZone(zoneB.Id);

        // Refresh master window
        if (_zoneWindows.TryGetValue(master.Id, out var masterWin))
        {
            masterWin.RefreshZone(master);
        }

        SaveConfig();
        ZonesChanged?.Invoke();
        return groupId;
    }

    /// <summary>Disband all zones in a merged group.</summary>
    public void DisbandMergedGroup(Guid groupId)
    {
        var members = Zones.Where(z => z.MergedGroupId == groupId).ToList();
        // Close the master window first
        foreach (var z in members)
        {
            if (z.MergedSubZoneIds.Count > 0 && _zoneWindows.TryGetValue(z.Id, out var win))
            {
                z.Width = win.Width; z.Height = win.Height; z.X = win.Left; z.Y = win.Top;
                win.Close();
                _zoneWindows.Remove(z.Id);
            }
        }
        // Clear merge fields on all members
        foreach (var z in members)
        {
            z.MergedGroupId = null;
            z.MergedSubZoneIds.Clear();
            z.MergedGroupName = "";
            z.MergedGroupIcon = "";
        }
        // Re-show individual windows
        foreach (var z in members)
        {
            ShowZone(z);
        }
        SaveConfig();
        ZonesChanged?.Invoke();
    }

    /// <summary>Remove a single zone from its merged group.</summary>
    public void RemoveFromMergedGroup(Guid zoneId)
    {
        var zone = Zones.FirstOrDefault(z => z.Id == zoneId);
        if (zone == null || !zone.MergedGroupId.HasValue) return;

        var groupId = zone.MergedGroupId.Value;
        var master = Zones.FirstOrDefault(z => z.MergedGroupId == groupId && z.MergedSubZoneIds.Count > 0);
        if (master != null)
        {
            master.MergedSubZoneIds.Remove(zoneId);
            if (master.MergedSubZoneIds.Count == 0)
            {
                // Only master remains — clear its merge state
                master.MergedGroupId = null;
                master.MergedGroupName = "";
                master.MergedGroupIcon = "";
                if (_zoneWindows.TryGetValue(master.Id, out var win))
                    win.RefreshZone(master);
            }
            else
            {
                master.MergedGroupName = BuildMergedGroupName(master);
                if (_zoneWindows.TryGetValue(master.Id, out var win))
                    win.RefreshZone(master);
            }
        }

        zone.MergedGroupId = null;
        zone.MergedGroupName = "";
        zone.MergedGroupIcon = "";

        SaveConfig();
        ZonesChanged?.Invoke();
    }

    private string BuildMergedGroupName(Zone master)
    {
        var names = new List<string> { master.Name };
        foreach (var subId in master.MergedSubZoneIds)
        {
            var sub = Zones.FirstOrDefault(z => z.Id == subId);
            if (sub != null) names.Add(sub.Name);
        }
        return string.Join(" + ", names);
    }

    /// <summary>Get all zones in a merged group.</summary>
    public List<Zone> GetMergedGroupZones(Guid groupId)
        => Zones.Where(z => z.MergedGroupId == groupId).ToList();
}
