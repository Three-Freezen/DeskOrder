using System;
using System.Collections.Generic;

namespace DesktopZones.Models;

/// <summary>
/// Group-membership state for a Zone that's part of a merged group: which
/// group, which sub-zones, combined display name + icon. Lifted out of
/// <see cref="Zone"/> so the model focuses on layout + per-zone style.
///
/// <see cref="GroupId"/> is the canonical "in a group?" flag (non-null = part
/// of a group; master vs sub-zone is decided by <see cref="SubZoneIds"/>
/// being non-empty).
/// </summary>
public class MergedGroupMembership
{
    public Guid? GroupId { get; set; }
    private List<Guid> _subZoneIds = new();
    public List<Guid> SubZoneIds
    {
        get => _subZoneIds;
        set => _subZoneIds = value ?? new();
    }
    public string DisplayName { get; set; } = "";       // combined display name
    public string Icon { get; set; } = "";              // combined icon
}