using System;
using System.Collections.Generic;
using System.Linq;
using DesktopZones.Models;

namespace DesktopZones.Helpers;

/// <summary>
/// Collision-aware free-cell placement for imported zone items. The item footprint is
/// one grid cell — GridSize wide × (GridSize + <see cref="LabelArea"/>) tall for the
/// Windows-native "icon on top, name below" layout. Horizontal pitch is
/// GridSize + <see cref="CellGap"/>, vertical pitch adds the name area, so icons keep
/// a small constant gap between them (the insertion caret sits in that gap).
/// </summary>
public static class ZoneLayout
{
    public const double Pad = 10;

    /// <summary>Extra spacing between two adjacent grid cells (on top of the 4px inset each icon already has).</summary>
    public const double CellGap = 12;

    /// <summary>Height of the name area below the icon (Windows-native icon style: icon on top, name below).</summary>
    public const double LabelArea = 16;

    public static double Pitch(int gridSize) => gridSize + CellGap;

    /// <summary>Vertical pitch: one cell plus the name area below the icon, plus the gap.</summary>
    public static double VPitch(int gridSize) => gridSize + LabelArea + CellGap;

    /// <summary>Snap a horizontal coordinate to the grid (origin Pad, pitch GridSize + CellGap).</summary>
    public static double Snap(double value, int gridSize)
    {
        double pitch = Pitch(gridSize);
        return Math.Round((value - Pad) / pitch) * pitch + Pad;
    }

    /// <summary>Snap a vertical coordinate to the grid (pitch includes the name area).</summary>
    public static double SnapY(double value, int gridSize)
    {
        double pitch = VPitch(gridSize);
        return Math.Round((value - Pad) / pitch) * pitch + Pad;
    }

    public static (double X, double Y) FindFreeSpot(IEnumerable<ZoneItem> items, double zoneWidth, double zoneHeight, double itemW, double itemH)
    {
        var list = items.ToList();
        double pitchX = itemW + CellGap;
        double pitchY = itemH + CellGap;
        int cols = Math.Max(1, (int)Math.Ceiling((zoneWidth - Pad) / pitchX));
        int rows = Math.Max(1, (int)Math.Ceiling((zoneHeight - Pad) / pitchY) + 1);

        for (int r = 0; r < rows; r++)
        {
            double y = Pad + r * pitchY;
            if (y + itemH > zoneHeight) break;
            for (int c = 0; c < cols; c++)
            {
                double x = Pad + c * pitchX;
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

    /// <summary>
    /// Re-packs a zone's items onto the current grid pitch in reading order. Idempotent,
    /// and only touches auto-arrange zones (free-form zones keep their manual layout).
    /// Returns true when any position changed.
    /// </summary>
    public static bool NormalizeZone(Zone zone)
    {
        if (!zone.AutoArrange || zone.Items.Count == 0) return false;

        double pitch = Pitch(zone.GridSize);
        double vpitch = VPitch(zone.GridSize);
        double zw = double.IsNaN(zone.Width) ? pitch + Pad : Math.Max(pitch + Pad, zone.Width);
        double x = Pad, y = Pad;
        bool changed = false;

        foreach (var it in zone.Items.OrderBy(i => i.Y).ThenBy(i => i.X))
        {
            double nx = Snap(x, zone.GridSize);
            double ny = SnapY(y, zone.GridSize);
            if (Math.Abs(it.X - nx) > 0.01 || Math.Abs(it.Y - ny) > 0.01) changed = true;
            it.X = nx;
            it.Y = ny;
            x += pitch;
            if (x > zw - zone.GridSize) { x = Pad; y += vpitch; }
        }
        return changed;
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
