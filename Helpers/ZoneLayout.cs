using System;
using System.Collections.Generic;
using System.Linq;
using DesktopZones.Models;

namespace DesktopZones.Helpers;

/// <summary>
/// Collision-aware free-cell placement for imported zone items. The item footprint is
/// one square grid cell — GridSize × GridSize — matching the panel's 80×80 card
/// (icon on top, name below, both inside the cell). Horizontal and vertical pitch are
/// both GridSize + <see cref="CellGap"/> so icons keep a constant gap between them
/// (the insertion caret sits in that gap).
/// </summary>
public static class ZoneLayout
{
    public const double Pad = 10;

    /// <summary>Extra spacing between two adjacent grid cells (panel-aligned: 80 cell + 8 gap = 88 pitch).</summary>
    public const double CellGap = 8;

    /// <summary>Name area below the sub-folder 2×2 box (the box is a fixed 56×56; the
    /// name hangs below it inside the 80×80 cell). Main-zone item names live inside
    /// the square cell now, so this no longer contributes to the main grid pitch.</summary>
    public const double LabelArea = 16;

    public static double Pitch(int gridSize) => gridSize + CellGap;

    /// <summary>Vertical pitch: one square cell plus the gap (same as horizontal pitch).</summary>
    public static double VPitch(int gridSize) => gridSize + CellGap;

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
        // 按窗口宽度计算列数并把整块水平居中 — 左右留白相等（与 ZoneWindow.RearrangeAll 一致）。
        double avail = Math.Max(0, zw - 2 * Pad);
        int fitCols = Math.Max(1, (int)Math.Floor((avail - zone.GridSize) / pitch) + 1);
        // 只按实际用到的列数居中：若窗口能容纳 7 列但只有 2 个图标，按 7 列居中会把
        // 图标挤到左侧、右侧留一大片空白（"左侧小右侧大"）。改为 min(可容纳列数, 图标数)
        // 后整块真正居中，左右留白相等。
        int cols = Math.Min(fitCols, zone.Items.Count);
        double blockWidth = (cols - 1) * pitch + zone.GridSize;
        double offsetX = Math.Max(Pad, (zw - blockWidth) / 2);
        bool changed = false;

        int idx = 0;
        foreach (var it in zone.Items.OrderBy(i => i.Y).ThenBy(i => i.X))
        {
            int col = idx % cols;
            int row = idx / cols;
            double nx = offsetX + col * pitch;
            double ny = SnapY(Pad + row * vpitch, zone.GridSize);
            if (Math.Abs(it.X - nx) > 0.01 || Math.Abs(it.Y - ny) > 0.01) changed = true;
            it.X = nx;
            it.Y = ny;
            idx++;
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
