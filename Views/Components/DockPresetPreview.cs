using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using DesktopZones.Models;

namespace DesktopZones.Views.Components;

/// <summary>
/// 快捷栏预设卡的缩略图：**一根长条**（不是分区那种方块）。
///
/// 为什么自绘：卡片是 DataTemplate，模板里既不能按预设的画法去实例化一堆真实图标，
/// 也不该为了缩略图启动图标缓存。这里只按预设里的填充/边框/圆角/液态玻璃/倒影/
/// 磁贴（全透明）画一张"长得像"的静态图 —— 一排占位方格代表图标，0 依赖、0 帧调度。
///
/// 层序与真实条体一致：填充 → 液态玻璃 → 图标占位 → 倒影 → 边框（边框最后画，
/// 玻璃不该把它吃掉）。磁贴模式下前两项直接跳过 —— 条体是全透明的。
/// </summary>
public sealed class DockPresetPreview : FrameworkElement
{
    public static readonly DependencyProperty ZoneProperty = DependencyProperty.Register(
        nameof(Zone), typeof(Zone), typeof(DockPresetPreview),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public Zone? Zone
    {
        get => (Zone?)GetValue(ZoneProperty);
        set => SetValue(ZoneProperty, value);
    }

    /// <summary>缩略图里画几个占位方格（真实图标数不进预设，这里只表达"排成一行"）。</summary>
    const int PlaceholderCount = 5;

    // 液态玻璃笔刷按"颜色模式"缓存（卡片是静态的，OnRender 可能因尺寸变化重跑）
    static Brush? _glassBrush;
    static string _glassMode = "";

    public DockPresetPreview()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double w = double.IsInfinity(availableSize.Width) ? 160 : availableSize.Width;
        double h = double.IsInfinity(availableSize.Height) ? 40 : availableSize.Height;
        return new Size(w, h);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 2 || h <= 2) return;
        var z = Zone;
        if (z == null) return;

        // 条体比例：真实条体 = 24 + n×格 + 间距（5 格 48 → 272×60，约 4.5:1）。
        // 缩略图按这个比例定高，缩到卡片宽度里不会变成一块正方形。
        double barH = Math.Min(h - 6, Math.Max(14, w / 4.6));
        double top = Math.Max(0, (h - barH) / 2 - 1);
        var rect = new Rect(0, top, w, barH);

        double radius = z.CornerRadius <= 0
            ? 0
            : Math.Clamp(z.CornerRadius * (barH / Math.Max(24.0, z.GridSize)), 0, barH / 2);

        // ① 填充（磁贴模式 = 全透明，跳过）
        if (!z.TileMode)
        {
            var fill = ParseHex(z.FillColor, Color.FromArgb(0x14, 0x00, 0x00, 0x00));
            dc.DrawRoundedRectangle(new SolidColorBrush(fill), null, rect, radius, radius);
            // ② 液态玻璃（与分区/播放器预设卡同一套配方）
            DrawGlass(dc, z, rect, radius);
        }

        // ③ 图标占位：一排圆角方格，居中
        double icon = barH * 0.66;
        double gap = icon * 0.14;
        double rowW = PlaceholderCount * icon + (PlaceholderCount - 1) * gap;
        double x0 = (w - rowW) / 2;
        double y = top + (barH - icon) / 2;
        double iconR = icon * 0.26;
        var iconBrush = new SolidColorBrush(Color.FromArgb(0xD8, 0xFF, 0xFF, 0xFF));
        iconBrush.Freeze();
        for (int i = 0; i < PlaceholderCount; i++)
        {
            double x = x0 + i * (icon + gap);
            dc.DrawRoundedRectangle(iconBrush, null, new Rect(x, y, icon, icon), iconR, iconR);
        }

        // ④ 倒影：一排压扁 + 向下渐隐的镜像，画在条体下方
        if (z.DockReflection)
        {
            double rh = Math.Max(2, barH * 0.34);
            double mirrorTop = top + barH + 1;
            if (mirrorTop + rh <= h)
            {
                dc.PushOpacity(Math.Clamp(z.DockReflectionOpacity / 100.0, 0, 1) * 0.5);
                var faded = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
                faded.Freeze();
                for (int i = 0; i < PlaceholderCount; i++)
                {
                    double x = x0 + i * (icon + gap);
                    dc.DrawRoundedRectangle(faded, null, new Rect(x, mirrorTop, icon, rh), iconR, iconR);
                }
                dc.Pop();
            }
        }

        // ⑤ 边框最后画（磁贴模式没有边框）
        if (!z.TileMode)
        {
            double bt = Math.Clamp(z.BorderThickness, 0, 3);
            if (bt > 0)
            {
                var borderColor = ParseHex(z.BorderColor, Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
                var pen = new Pen(new SolidColorBrush(borderColor), bt);
                double half = bt / 2;
                dc.DrawRoundedRectangle(null, pen,
                    new Rect(half, top + half, Math.Max(0, w - bt), Math.Max(0, barH - bt)),
                    Math.Max(0, radius - half), Math.Max(0, radius - half));
            }
        }
    }

    static void DrawGlass(DrawingContext dc, Zone z, Rect rect, double radius)
    {
        if (!z.EnableLiquidGlass) return;
        double opacity = Math.Clamp(z.GlassTintOpacity / 100.0, 0, 1);
        if (opacity <= 0.01) return;
        var brush = GetGlassBrush(z.GlassColorMode);
        if (brush == null) return;
        dc.PushOpacity(opacity);
        dc.PushClip(new RectangleGeometry(rect, radius, radius));
        dc.DrawRectangle(brush, null, rect);
        dc.Pop();
        dc.Pop();
    }

    /// <summary>玻璃笔刷：复用分区预设卡的转换器，保证两处颜色配方一致。</summary>
    static Brush? GetGlassBrush(string? mode)
    {
        string key = string.IsNullOrWhiteSpace(mode) ? "Default" : mode!;
        if (_glassBrush != null && string.Equals(_glassMode, key, StringComparison.Ordinal))
            return _glassBrush;
        try
        {
            var brush = new LiquidGlassBrushConverter().Convert(
                key, typeof(Brush), null, CultureInfo.InvariantCulture) as Brush;
            if (brush != null && brush.CanFreeze) brush.Freeze();
            _glassBrush = brush;
            _glassMode = key;
            return brush;
        }
        catch
        {
            _glassBrush = null;
            _glassMode = key;
            return null;
        }
    }

    static Color ParseHex(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try { return (Color)ColorConverter.ConvertFromString(hex)!; }
        catch { return fallback; }
    }
}
