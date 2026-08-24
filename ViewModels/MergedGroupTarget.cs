using System;
using System.Runtime.CompilerServices;
using DesktopZones.Models;

namespace DesktopZones.ViewModels;

/// <summary>
/// Runtime wrapper that makes a merged group addressable as its own settings
/// target — distinct from the master <see cref="Zone"/>, whose editor remains
/// the per-zone one. Not persisted; only used as the PropertyPanel /
/// PropertyWindow target and as the docked/floating dedup identity.
///
/// Identity is cached per master (<see cref="For"/>), so object reference
/// equality stays stable across call sites — PropertyWindowManager dedups
/// floating windows by reference.
/// </summary>
public sealed class MergedGroupTarget
{
    public Zone Master { get; }

    /// <summary>Stable group identity — survives master promotion on detach
    /// (DetachZoneAt keeps the same GroupId on the new master).</summary>
    public Guid GroupId => Master.MergedGroupMembership.GroupId ?? Master.Id;

    MergedGroupTarget(Zone master) => Master = master;

    static readonly ConditionalWeakTable<Zone, MergedGroupTarget> _cache = new();

    public static MergedGroupTarget For(Zone master) =>
        _cache.GetValue(master, z => new MergedGroupTarget(z));
}
