using System;
using System.Reflection;

namespace DesktopZones.Helpers;

/// <summary>
/// Reflection-based field-copy helpers used by Clone() methods that need
/// to satisfy an inheritance contract without manually enumerating every
/// field declared on a base class.
///
/// Ponytail note: this exists because P1 lifted ~14 appearance fields into
/// AppearanceModel and 4 derived Clone() methods would otherwise need to
/// manually re-list them (and miss new ones when they're added later).
/// Reflection is the smallest tool that prevents the silent-miss failure
/// mode the spec called out.
/// </summary>
public static class CloneHelper
{
    /// <summary>
    /// Copies every public read/write property declared on
    /// <typeparamref name="TBase"/> from <paramref name="src"/> to
    /// <paramref name="dst"/>. Both objects must be assignable to
    /// <typeparamref name="TBase"/> (typically the same derived type).
    /// </summary>
    public static void CopyBaseProperties<TBase>(object src, object dst)
    {
        var t = typeof(TBase);
        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!p.CanRead || !p.CanWrite) continue;
            if (p.GetSetMethod(true) == null) continue;
            if (p.GetIndexParameters().Length > 0) continue;
            p.SetValue(dst, p.GetValue(src));
        }
    }
}