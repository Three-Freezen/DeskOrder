using System;
using System.Collections.Generic;
using System.Linq;
using DesktopZones.Models;

namespace DesktopZones.Helpers;

/// <summary>
/// Collision-aware free-cell placement for imported zone items. The item footprint is
/// one grid cell (GridSize × GridSize), so import cells use the same pitch and origin
/// as the snap grid — this keeps imported icons on the same grid as manually-dragged
/// ones, centered in their cells and never overlapping (whatever GridSize is).
/// </summary>
public static class ZoneLayout
{
    public const double Pad = 10;

    public static (double X, double Y) FindFreeSpot(IEnumerable<ZoneItem> items, double zoneWidth, double zoneHeight, double itemW, double itemH)
    {
        var list = items.ToList();
        int cols = Math.Max(1, (int)Math.Floor((zoneWidth - Pad) / itemW));
        int rows = Math.Max(1, (int)Math.Floor((zoneHeight - Pad) / itemH) + 1);

        for (int r = 0; r < rows; r++)
        {
            double y = Pad + r * itemH;
            if (y + itemH > zoneHeight) break;
            for (int c = 0; c < cols; c++)
            {
                double x = Pad + c * itemW;
                if (x + itemW > zoneWidth) break;
                if (!OverlapsAny(list, x, y, itemW, itemH)) return (x, y);
            }
        }

        // Zone is effectively full of obstacles: append below the bottom-right item.
        double maxY = 0, maxX = 0;
        foreach (var i in list) if (i.Y > maxY) maxY = i.Y;
        foreach (var i in list) if (Math.Abs(i.Y - maxY) < itemH / 2 && i.X > maxX) maxX = i.X;
        return (
            Math.Min(maxX + itemW, Math.Max(0, zoneWidth - itemW)),
            maxY + itemH);
    }

    private static bool OverlapsAny(List<ZoneItem> items, double x, double y, double itemW, double itemH)
    {
        foreach (var i in items)
        {
            if (x < i.X + itemW && x + itemW > i.X && y < i.Y + itemH && y + itemH > i.Y)
                return true;
        }
        return false;
    }
}
