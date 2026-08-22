using System;
using System.Collections.Generic;
using System.Linq;

namespace DesktopZones.Helpers;

/// <summary>
/// Resolves name collisions by appending " 2", " 3", ... until a unique name is found.
/// Used by every instance-list page that supports "New" / "Import" / duplicate-from-templates.
/// ponytail: O(n) scan per call, fine for human-scale item lists (&lt;1k). Upgrade to bucketed
/// suffix index if a list ever pushes 10k+ items; not yet needed.
/// </summary>
public static class NameCollisionResolver
{
    public static string ResolveName(string desired, IEnumerable<string> existing)
    {
        if (desired == null) throw new ArgumentNullException(nameof(desired));
        if (existing == null) throw new ArgumentNullException(nameof(existing));

        var set = new HashSet<string>(existing, StringComparer.Ordinal);
        if (!set.Contains(desired)) return desired;
        int n = 2;
        while (set.Contains($"{desired} {n}")) n++;
        return $"{desired} {n}";
    }
}
