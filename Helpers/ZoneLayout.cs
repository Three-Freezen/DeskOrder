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

    // ═══════════════════════════════════════════════════════════════════════
    //  快捷栏（Dock）几何
    //  与分区那套「方格折行 + 水平居中」完全分开：快捷栏是单行、Y 固定、X 按序号递推，
    //  宽高都由图标数算出来（窗口 = 条体，见 ZoneWindow.ApplyDockMode 的说明）。
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>左右内边距。</summary>
    public const double DockPadH = 12;

    /// <summary>上下内边距。</summary>
    public const double DockPadV = 6;

    /// <summary>格间距（快捷栏比分区紧得多：图标本来就是连着排的）。</summary>
    public const double DockGap = 2;

    /// <summary>没有图标时条体本体的固定宽度（里面没有文字，就是一条空胶囊）。</summary>
    public const double DockEmptyWidth = 120;

    /// <summary>开始菜单按钮与图标区之间分隔条的占宽（1px 竖线 + 两侧留白）。</summary>
    public const double DockStartSepWidth = 13;

    /// <summary>图标自身的绘制边长：隐藏名称时几乎占满一格，显示名称时给下方名字留 18px。</summary>
    public static double DockIconSize(Zone zone) =>
        Math.Max(8, zone.GridSize - (zone.HideAppName ? 6 : 18));

    /// <summary>可见胶囊（条体本体）的高度 = 一格 + 上下内边距。
    /// **放大余量不在里面**（那是窗口上方的一条透明带，见 <see cref="DockOverflowTop"/>）。</summary>
    public static double DockBarHeight(Zone zone) => zone.GridSize + DockPadV * 2;

    /// <summary>倒影带高度：按图标自身尺寸算（而不是按格），保证镜像永远放得下。
    /// 0.55（早先是 0.40）：0.40 压得太扁，看着只有薄薄一条（用户反馈"倒影太短"）。</summary>
    public static double DockReflectHeight(Zone zone) =>
        zone.DockReflection ? Math.Round(DockIconSize(zone) * 0.55) : 0;

    /// <summary>窗口下方实际留出的倒影带：倒影挂在图标容器里，会跟着悬停放大一起放大，
    /// 所以按最大倍数留够，否则放大时倒影会被窗口下边界切掉（渐隐画到一半断掉）。</summary>
    public static double DockReflectBand(Zone zone)
    {
        double rh = DockReflectHeight(zone);
        if (rh <= 0) return 0;
        double m = zone.DockAnimate ? Math.Clamp(zone.DockMagnify, 1.0, 2.0) : 1.0;
        return Math.Ceiling(rh * m);
    }

    /// <summary>
    /// 窗口上方的透明余量（放大专用）。
    ///
    /// 缩放原点是**格的底边**（模板 `RenderTransformOrigin=0.5,1`），放大到 m 倍后图标顶边
    /// 到格底的距离是 `m × (格 + 图标) / 2`；可见胶囊只有 `格 + 2×内边距` 高，两者的差值
    /// 必须留在窗口里，否则图标顶会被窗口边界切掉。第一轮把原点当成了「图标底边」，
    /// 少算了图标距格底的几个像素 —— 用户看到的就是「放大后顶上被切」。这里换算回去，
    /// 并 +1px 给抗锯齿留余量。
    /// </summary>
    public static double DockOverflowTop(Zone zone)
    {
        if (!zone.DockAnimate) return 0;
        double m = Math.Clamp(zone.DockMagnify, 1.0, 2.0);
        double need = m * (zone.GridSize + DockIconSize(zone)) / 2.0 - DockPadV - zone.GridSize;
        return need <= 0 ? 0 : Math.Ceiling(need) + 2;
    }

    /// <summary>可见胶囊的顶边（相对窗口顶部）。上方那条就是放大用的透明余量。</summary>
    public static double DockBarTop(Zone zone) => DockOverflowTop(zone);

    /// <summary>图标带（一格的顶边）在窗口里的 Y —— 一格在胶囊里垂直居中。</summary>
    public static double DockBandTop(Zone zone) => DockBarTop(zone) + DockPadV;

    /// <summary>开始菜单块（按钮 + 分隔线）的占宽；关掉时为 0。</summary>
    public static double DockStartBlock(Zone zone) =>
        zone.DockStartButton ? zone.GridSize + DockStartSepWidth : 0;

    /// <summary>
    /// 悬停时每一格横向让位的距离 = 格 ×（倍数 − 1）/ 2。
    ///
    /// 放大只作用在悬停那一格上（缩放原点是它的底边中点），所以它左右各多占 grow；
    /// 同一侧的其余格整体平移 grow，格间距就恒定不变 —— 既不重合，也不会被推开一条缝。
    /// </summary>
    public static double DockHoverShift(Zone zone) =>
        zone.DockAnimate
            ? Math.Max(0, zone.GridSize * (Math.Clamp(zone.DockMagnify, 1.0, 2.0) - 1.0) / 2.0)
            : 0;

    /// <summary>
    /// 窗口左右两侧的透明余量。放大后整排向两边撑开，胶囊宽度不变，多出来的部分留在
    /// 窗口里（layered window 的透明像素不参与命中测试，不吃桌面点击）。
    /// 需要多少：最外侧那格放大后左边缘 = 内边距 − grow，越界就补，另加 2px 抗锯齿余量。
    /// </summary>
    public static double DockSideOverflow(Zone zone)
    {
        double need = DockHoverShift(zone) - DockPadH + 2;
        return need <= 0 ? 0 : Math.Ceiling(need);
    }

    /// <summary>开始菜单按钮的左边（= 左右内边距 + 两侧透明余量）。</summary>
    public static double DockStartX(Zone zone) => DockSideOverflow(zone) + DockPadH;

    /// <summary>分隔竖线的左边（按钮右侧留一半、图标区左侧留一半）。</summary>
    public static double DockStartSepX(Zone zone) =>
        DockSideOverflow(zone) + DockPadH + zone.GridSize + (DockStartSepWidth - 1) / 2.0;

    /// <summary>第 <paramref name="index"/> 个图标的 X（Canvas.Left）。</summary>
    public static double DockX(Zone zone, int index) =>
        DockSideOverflow(zone) + DockPadH + DockStartBlock(zone) + index * (zone.GridSize + DockGap);

    /// <summary>条体（可见胶囊）本身的宽度，不含两侧透明余量。</summary>
    public static double DockPillWidth(Zone zone, int count)
    {
        if (count <= 0) return DockEmptyWidth + DockStartBlock(zone);
        return DockPadH * 2 + DockStartBlock(zone) + count * zone.GridSize + (count - 1) * DockGap;
    }

    /// <summary>窗口总宽 = 两侧透明余量 + 可见胶囊。</summary>
    public static double DockWidth(Zone zone, int count) =>
        DockSideOverflow(zone) * 2 + DockPillWidth(zone, count);

    /// <summary>窗口高度 = 上方放大余量 + 可见胶囊 + 倒影带。</summary>
    public static double DockHeight(Zone zone) =>
        DockOverflowTop(zone) + DockBarHeight(zone) + DockReflectBand(zone);

    /// <summary>把一条快捷栏的所有图标按序号排好（只写 X/Y，不动顺序）。</summary>
    public static void ArrangeDock(Zone zone)
    {
        double y = DockBandTop(zone);
        for (int i = 0; i < zone.Items.Count; i++)
        {
            var it = zone.Items[i];
            double x = DockX(zone, i);
            if (Math.Abs(it.X - x) > 0.01) it.X = x;
            if (Math.Abs(it.Y - y) > 0.01) it.Y = y;
        }
    }

    /// <summary>根据图标数把宽高写回模型（窗口尺寸的唯一来源）。</summary>
    public static void ApplyDockSize(Zone zone)
    {
        zone.Width = DockWidth(zone, zone.Items.Count);
        zone.Height = DockHeight(zone);
    }

    /// <summary>新建快捷栏的落地位置：主屏工作区底部居中（下边距 12）。</summary>
    public static (double X, double Y) DockSpawnPoint(Zone zone)
    {
        var wa = System.Windows.SystemParameters.WorkArea;
        double w = DockWidth(zone, zone.Items.Count);
        double h = DockHeight(zone);
        double x = wa.Left + Math.Max(0, (wa.Width - w) / 2);
        double y = wa.Top + Math.Max(0, wa.Height - h - 12);
        return (x, y);
    }
}
