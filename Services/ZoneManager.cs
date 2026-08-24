using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using DesktopZones.Helpers;
using DesktopZones.Models;
using DesktopZones.Views;
using DesktopZones.Views.Components;

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
    /// <summary>Fires when a zone's lock state changes. Args: zoneId (string), isLocked.</summary>
    public event Action<string, bool>? LockChanged;

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
        if (toDelete.MergedGroupMembership.GroupId.HasValue)
        {
            if (toDelete.MergedGroupMembership.SubZoneIds.Count > 0)
                DisbandMergedGroup(toDelete.MergedGroupMembership.GroupId.Value);
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

    public void ShowZone(Zone zone, double waveDelayMs = 0)
    {
        // If this zone is a sub-zone of a merged group, show the master instead
        if (zone.MergedGroupMembership.GroupId.HasValue && zone.MergedGroupMembership.SubZoneIds.Count == 0)
        {
            var master = Zones.FirstOrDefault(z => z.MergedGroupMembership.GroupId == zone.MergedGroupMembership.GroupId && z.MergedGroupMembership.SubZoneIds.Count > 0);
            if (master != null)
            {
                master.IsVisible = true;
                ShowZone(master, waveDelayMs);
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
            _zoneWindows[zone.Id].ShowZone(waveDelayMs);
        }
        else
        {
            var window = new ZoneWindow(zone, this, new ShellIconService());
            window.Show();
            _zoneWindows[zone.Id] = window;
            // ponytail: batch "Show All" wave — a freshly created window starts expanded
            // from the ctor; re-collapse it and play its own entrance animation at the
            // stagger slot so new windows join the cascade.
            if (waveDelayMs > 0) window.PlayEntranceAnimation(waveDelayMs);
        }
        SaveConfig();
        ZonesChanged?.Invoke();
        ZoneVisibilityChanged?.Invoke(zone.Id, true);
    }

    public void HideZone(Guid zoneId, double waveDelayMs = 0)
    {
        var zone = Zones.FirstOrDefault(z => z.Id == zoneId);
        if (_zoneWindows.TryGetValue(zoneId, out var window))
        {
            window.HideZone(waveDelayMs);
            // If EnableRestoreButton is false, remove window from dictionary (like FullHideZone)
            if (zone != null && !zone.EnableRestoreButton)
            {
                _zoneWindows.Remove(zoneId);
                if (waveDelayMs <= 0)
                {
                    // ponytail: 2026-08-23 — close the removed window instead of leaking it.
                    // A hidden-but-alive window keeps its HoverExpandBehavior poll timer and
                    // ZonesChanged/LockChanged handlers running, and its stale state could
                    // re-enable the DWM glass on the hidden HWND (ghost glass bug).
                    // The batch-wave path (waveDelayMs > 0) closes itself after its collapse
                    // animation finishes — closing now would kill the animation.
                    window.Close();
                }
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
            // ponytail: 2026-08-23 batch wave — sort by screen position (row-major)
            // and stagger each zone by BatchStaggerMs so "Show All" opens as a
            // left-to-right / top-to-bottom cascade; each zone plays its OWN
            // configured animation kind/speed/origin.
            int i = 0;
            foreach (var zone in Zones
                         .Where(z => !(z.MergedGroupMembership.GroupId.HasValue
                                       && z.MergedGroupMembership.SubZoneIds.Count == 0))
                         .OrderBy(z => z.Y).ThenBy(z => z.X))
            {
                ShowZone(zone, i * HoverExpandBehavior.BatchStaggerMs);
                i++;
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
            // ponytail: batch wave — mirror of ShowAll: each zone collapses with its
            // own animation at its stagger slot (see ShowAll for the sort rationale).
            int i = 0;
            foreach (var zone in Zones.OrderBy(z => z.Y).ThenBy(z => z.X))
            {
                HideZone(zone.Id, i * HoverExpandBehavior.BatchStaggerMs);
                i++;
            }
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
    public ZoneWindow? GetZoneWindow(Guid zoneId) => _zoneWindows.TryGetValue(zoneId, out var w) ? w : null;
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

    public void SaveConfig() => ConfigSaver.SavePreservingPanelSettings(_configService, cfg =>
    {
        cfg.Zones = Zones.ToList();
    });

    public AppConfig GetConfig() => _config;

    /// <summary>Notify all listeners that zones have changed (for item-level changes).</summary>
    public void NotifyChanged() => ZonesChanged?.Invoke();

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

        if (zoneA.MergedGroupMembership.GroupId.HasValue)
        {
            groupId = zoneA.MergedGroupMembership.GroupId.Value;
            master = Zones.FirstOrDefault(z => z.MergedGroupMembership.GroupId == groupId && z.MergedGroupMembership.SubZoneIds.Count > 0)
                     ?? zoneA;
        }
        else
        {
            groupId = Guid.NewGuid();
            master = zoneA;
            zoneA.MergedGroupMembership.GroupId = groupId;
        }

        // If zoneB is already in a merged group, remove it first
        if (zoneB.MergedGroupMembership.GroupId.HasValue)
            RemoveFromMergedGroup(zoneB.Id);

        zoneB.MergedGroupMembership.GroupId = groupId;
        master.MergedGroupMembership.SubZoneIds.Add(zoneB.Id);
        master.MergedGroupMembership.DisplayName = BuildMergedGroupName(master);
        zoneB.MergedGroupMembership.DisplayName = master.MergedGroupMembership.DisplayName;

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
        var members = Zones.Where(z => z.MergedGroupMembership.GroupId == groupId).ToList();
        // Close the master window first
        foreach (var z in members)
        {
            if (z.MergedGroupMembership.SubZoneIds.Count > 0 && _zoneWindows.TryGetValue(z.Id, out var win))
            {
                z.Width = win.Width; z.Height = win.Height; z.X = win.Left; z.Y = win.Top;
                win.Close();
                _zoneWindows.Remove(z.Id);
            }
        }
        // Clear merge fields on all members
        foreach (var z in members)
        {
            z.MergedGroupMembership.GroupId = null;
            z.MergedGroupMembership.SubZoneIds.Clear();
            z.MergedGroupMembership.TabOrder.Clear();
            z.MergedGroupMembership.DisplayName = "";
            z.MergedGroupMembership.Icon = "";
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
        if (zone == null || !zone.MergedGroupMembership.GroupId.HasValue) return;

        var groupId = zone.MergedGroupMembership.GroupId.Value;
        var master = Zones.FirstOrDefault(z => z.MergedGroupMembership.GroupId == groupId && z.MergedGroupMembership.SubZoneIds.Count > 0);
        if (master != null)
        {
            master.MergedGroupMembership.SubZoneIds.Remove(zoneId);
            master.MergedGroupMembership.TabOrder.Remove(zoneId);
            if (master.MergedGroupMembership.SubZoneIds.Count == 0)
            {
                // Only master remains — clear its merge state
                master.MergedGroupMembership.GroupId = null;
                master.MergedGroupMembership.DisplayName = "";
                master.MergedGroupMembership.Icon = "";
                master.MergedGroupMembership.TabOrder.Clear();
                if (_zoneWindows.TryGetValue(master.Id, out var win))
                    win.RefreshZone(master);
            }
            else
            {
                master.MergedGroupMembership.DisplayName = BuildMergedGroupName(master);
                if (_zoneWindows.TryGetValue(master.Id, out var win))
                    win.RefreshZone(master);
            }
        }

        zone.MergedGroupMembership.GroupId = null;
        zone.MergedGroupMembership.DisplayName = "";
        zone.MergedGroupMembership.Icon = "";
        zone.MergedGroupMembership.TabOrder.Clear();

        SaveConfig();
        ZonesChanged?.Invoke();
    }

    /// <summary>
    /// Merge the dragged zone into the target zone's merged group (creating one when
    /// the target is standalone). The target stays the group master and its name comes
    /// first; the dragged zone is appended, and when the dragged zone is itself a group
    /// master its whole group is folded in (order preserved). Fires a single save/refresh.
    /// </summary>
    public Guid MergeZoneInto(Guid targetZoneId, Guid draggedZoneId)
    {
        var target = Zones.FirstOrDefault(z => z.Id == targetZoneId);
        var dragged = Zones.FirstOrDefault(z => z.Id == draggedZoneId);
        if (target == null || dragged == null || target.Id == dragged.Id)
            return Guid.Empty;

        // Defensive: a hidden sub-zone target can't be dropped onto (its window is
        // closed), but redirect it to its master just in case.
        if (target.MergedGroupMembership.GroupId.HasValue && target.MergedGroupMembership.SubZoneIds.Count == 0)
        {
            var tm = Zones.FirstOrDefault(z => z.MergedGroupMembership.GroupId == target.MergedGroupMembership.GroupId
                                               && z.MergedGroupMembership.SubZoneIds.Count > 0);
            if (tm != null) target = tm;
        }

        // Payload = dragged first, then its subs in order when it's a master.
        var payload = new List<Zone> { dragged };
        if (dragged.MergedGroupMembership.SubZoneIds.Count > 0)
        {
            foreach (var subId in dragged.MergedGroupMembership.SubZoneIds.ToList())
            {
                var sub = Zones.FirstOrDefault(z => z.Id == subId);
                if (sub != null && sub.Id != target.Id) payload.Add(sub);
            }
        }

        // Detach payload zones from their current groups, subs first so a dragged
        // master never needs a mid-flight promotion. No per-zone events here — the
        // single SaveConfig/ZonesChanged at the end is the only refresh.
        for (int i = payload.Count - 1; i >= 0; i--)
            DetachZoneFromGroup(payload[i]);

        bool targetWasMerged = target.MergedGroupMembership.GroupId.HasValue;

        // Ensure the target is a master (new group when standalone).
        Guid groupId;
        Zone master;
        if (target.MergedGroupMembership.GroupId.HasValue)
        {
            groupId = target.MergedGroupMembership.GroupId.Value;
            master = Zones.FirstOrDefault(z => z.MergedGroupMembership.GroupId == groupId
                                               && z.MergedGroupMembership.SubZoneIds.Count > 0) ?? target;
        }
        else
        {
            groupId = Guid.NewGuid();
            target.MergedGroupMembership.GroupId = groupId;
            master = target;
        }

        // Normalize the master's display order BEFORE the fold so an existing
        // user-arranged order survives, then append the incoming members at the end.
        var order = master.MergedGroupMembership.TabOrder;
        if (order.Count != master.MergedGroupMembership.SubZoneIds.Count + 1
            || !order.Contains(master.Id)
            || master.MergedGroupMembership.SubZoneIds.Any(id => !order.Contains(id)))
        {
            order.Clear();
            order.Add(master.Id);
            order.AddRange(master.MergedGroupMembership.SubZoneIds);
        }

        foreach (var z in payload)
        {
            if (z.Id == master.Id) continue;
            z.MergedGroupMembership.SubZoneIds.Clear();
            z.MergedGroupMembership.GroupId = groupId;
            master.MergedGroupMembership.SubZoneIds.Add(z.Id);
            order.Add(z.Id);
        }

        // New groups get the generated "target + sub + sub" default; adding to an
        // existing group appends the incoming names to its current (possibly
        // user-edited) display name instead of clobbering it.
        string groupName;
        if (targetWasMerged)
        {
            var added = payload.Where(z => z.Id != master.Id).Select(z => z.Name).ToList();
            var current = string.IsNullOrWhiteSpace(master.MergedGroupMembership.DisplayName)
                ? master.Name
                : master.MergedGroupMembership.DisplayName;
            groupName = added.Count > 0 ? current + " + " + string.Join(" + ", added) : current;
        }
        else
        {
            groupName = BuildMergedGroupName(master);
        }
        master.MergedGroupMembership.DisplayName = groupName;
        foreach (var m in Zones.Where(z => z.MergedGroupMembership.GroupId == groupId))
            m.MergedGroupMembership.DisplayName = groupName;

        // Hide payload windows (the dragged window closes; already-hidden subs are no-ops).
        foreach (var z in payload)
            if (z.Id != master.Id) FullHideZone(z.Id);

        if (_zoneWindows.TryGetValue(master.Id, out var masterWin))
            masterWin.RefreshZone(master);

        SaveConfig();
        ZonesChanged?.Invoke();
        return groupId;
    }

    /// <summary>Detach a zone from its current group without firing save/refresh events.
    /// Handles both sub-zones and masters (a detached master promotes its first sub).</summary>
    private void DetachZoneFromGroup(Zone zone)
    {
        if (!zone.MergedGroupMembership.GroupId.HasValue) return;
        var groupId = zone.MergedGroupMembership.GroupId.Value;
        var master = Zones.FirstOrDefault(m => m.MergedGroupMembership.GroupId == groupId && m.MergedGroupMembership.SubZoneIds.Count > 0);

        if (master != null)
        {
            if (master.Id == zone.Id)
            {
                var subs = zone.MergedGroupMembership.SubZoneIds.ToList();
                var newMaster = Zones.FirstOrDefault(m => m.Id == subs.FirstOrDefault());
                if (newMaster != null)
                {
                    newMaster.MergedGroupMembership.GroupId = groupId;
                    newMaster.MergedGroupMembership.SubZoneIds = subs.Skip(1).ToList();
                    newMaster.MergedGroupMembership.DisplayName = BuildMergedGroupName(newMaster);
                    newMaster.MergedGroupMembership.Icon = zone.MergedGroupMembership.Icon;
                    newMaster.MergedGroupMembership.TabOrder.Clear();
                    newMaster.MergedGroupMembership.TabOrder.Add(newMaster.Id);
                    newMaster.MergedGroupMembership.TabOrder.AddRange(newMaster.MergedGroupMembership.SubZoneIds);
                }
            }
            else
            {
                master.MergedGroupMembership.SubZoneIds.Remove(zone.Id);
                master.MergedGroupMembership.TabOrder.Remove(zone.Id);
                if (master.MergedGroupMembership.SubZoneIds.Count == 0)
                {
                    master.MergedGroupMembership.GroupId = null;
                    master.MergedGroupMembership.DisplayName = "";
                    master.MergedGroupMembership.Icon = "";
                    master.MergedGroupMembership.TabOrder.Clear();
                }
                else
                {
                    master.MergedGroupMembership.DisplayName = BuildMergedGroupName(master);
                }
            }
        }

        zone.MergedGroupMembership.GroupId = null;
        zone.MergedGroupMembership.SubZoneIds.Clear();
        zone.MergedGroupMembership.TabOrder.Clear();
        zone.MergedGroupMembership.DisplayName = "";
        zone.MergedGroupMembership.Icon = "";
    }

    private string BuildMergedGroupName(Zone master)
    {
        var names = new List<string> { master.Name };
        foreach (var subId in master.MergedGroupMembership.SubZoneIds)
        {
            var sub = Zones.FirstOrDefault(z => z.Id == subId);
            if (sub != null) names.Add(sub.Name);
        }
        return string.Join(" + ", names);
    }

    /// <summary>Get all zones in a merged group.</summary>
    public List<Zone> GetMergedGroupZones(Guid groupId)
        => Zones.Where(z => z.MergedGroupMembership.GroupId == groupId).ToList();

    /// <summary>
    /// Detach a zone from its merged group while keeping the group window alive.
    /// Sub-zones use the plain removal. Detaching the MASTER promotes the first
    /// remaining member in display order to the new master (or dissolves the group
    /// when only one member remains), transfers the group style to the new host and
    /// re-keys the current group window to it. The detached zone is not shown here —
    /// the caller positions and shows it (drag-out drops it at the cursor).
    /// </summary>
    public void DetachZoneAt(Guid zoneId)
    {
        var zone = Zones.FirstOrDefault(z => z.Id == zoneId);
        if (zone == null || !zone.MergedGroupMembership.GroupId.HasValue) return;

        // Sub-zone: plain detach (auto-dissolves when only the master remains).
        if (zone.MergedGroupMembership.SubZoneIds.Count == 0)
        {
            RemoveFromMergedGroup(zoneId);
            return;
        }

        var groupId = zone.MergedGroupMembership.GroupId.Value;

        // Remaining members in display order.
        var remaining = new List<Guid>(zone.MergedGroupMembership.TabOrder);
        if (remaining.Count != zone.MergedGroupMembership.SubZoneIds.Count + 1
            || !remaining.Contains(zone.Id)
            || zone.MergedGroupMembership.SubZoneIds.Any(id => !remaining.Contains(id)))
        {
            remaining.Clear();
            remaining.Add(zone.Id);
            remaining.AddRange(zone.MergedGroupMembership.SubZoneIds);
        }
        remaining.Remove(zone.Id);

        var host = Zones.FirstOrDefault(z => z.Id == remaining.FirstOrDefault());
        if (host == null) return;

        // Re-key the current group window to the successor so it stays on screen.
        _zoneWindows.Remove(zone.Id, out var window);
        if (window != null)
        {
            host.X = window.Left; host.Y = window.Top;
            host.Width = window.Width; host.Height = window.Height;
        }

        zone.MergedGroupMembership.GroupId = null;
        zone.MergedGroupMembership.SubZoneIds.Clear();
        zone.MergedGroupMembership.TabOrder.Clear();
        zone.MergedGroupMembership.DisplayName = "";
        zone.MergedGroupMembership.Icon = "";
        zone.IsVisible = false;

        if (remaining.Count == 1)
        {
            // Only one member left → the group dissolves; it keeps the window standalone.
            host.MergedGroupMembership.GroupId = null;
            host.MergedGroupMembership.SubZoneIds.Clear();
            host.MergedGroupMembership.TabOrder.Clear();
            host.MergedGroupMembership.DisplayName = "";
            host.MergedGroupMembership.Icon = "";
            host.IsVisible = true;
            AdoptWindow(host, window, merged: false);
        }
        else
        {
            // Promote the first remaining member to the new master.
            host.MergedGroupMembership.GroupId = groupId;
            host.MergedGroupMembership.SubZoneIds = remaining.Skip(1).ToList();
            host.MergedGroupMembership.TabOrder = new List<Guid>(remaining);
            host.MergedGroupMembership.DisplayName = BuildMergedGroupName(host);
            host.MergedGroupMembership.Icon = zone.MergedGroupMembership.Icon;
            // Keep the group's merged style on the new host.
            CloneHelper.CopyBaseProperties<MergedGroupStyle>(zone.MergedGroupStyle, host.MergedGroupStyle);
            host.IsVisible = true;
            AdoptWindow(host, window, merged: true);
        }

        SaveConfig();
        ZonesChanged?.Invoke();
    }

    /// <summary>Point the existing group window at the new host zone (or open one
    /// when the window wasn't open), refreshing items and selection accordingly.</summary>
    private void AdoptWindow(Zone host, ZoneWindow? window, bool merged)
    {
        if (window == null)
        {
            ShowZone(host);
            return;
        }
        _zoneWindows[host.Id] = window;
        if (window.DataContext is ViewModels.ZoneViewModel vm)
        {
            if (merged) vm.SelectedSubZoneId = host.Id; // refreshes merged items
            else { vm.RefreshZone(host); vm.SelectedSubZoneId = null; }
        }
        window.RefreshZone(host);
    }

    /// <summary>Persist a merged master's sub-zone order after a tab reorder and notify
    /// listeners. The combined display name is intentionally left untouched — it is
    /// user-editable once generated.</summary>
    public void SaveSubZoneOrder(Guid masterZoneId)
    {
        var master = Zones.FirstOrDefault(z => z.Id == masterZoneId);
        if (master == null || master.MergedGroupMembership.SubZoneIds.Count == 0) return;
        SaveConfig();
        ZonesChanged?.Invoke();
    }

    // ── Lock ──

    /// <summary>Set locked state for a zone by string id. Fires LockChanged after state update.</summary>
    public void SetLocked(string zoneId, bool locked)
    {
        if (!Guid.TryParse(zoneId, out var guid)) return;
        var zone = Zones.FirstOrDefault(z => z.Id == guid);
        if (zone == null) return;
        zone.IsLocked = locked;
        LockChanged?.Invoke(zoneId, locked);
    }
}
