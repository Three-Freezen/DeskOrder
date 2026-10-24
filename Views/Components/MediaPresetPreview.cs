using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using DesktopZones.Models;
using DesktopZones.Views;

namespace DesktopZones.Views.Components;

/// <summary>
/// 播放器预设卡的静态缩略图（律动条 / 唱片机两种形态共用）。
///
/// 为什么单独做一个自绘元素：预设卡是 <c>DataTemplate</c>，模板里既不能循环生成
/// N 根条子（条数是预设的一部分），也不该为了一张缩略图去启动真实的采集/动画。
/// 这里只按预设里的颜色/条数/圆角/液态玻璃画一张"长得像"的静态图，0 依赖、0 帧调度。
///
/// 层序和真实窗口一致：填充色 → 液态玻璃（分区预设卡同款 <see cref="LiquidGlassBrushConverter"/>
/// + <c>GlassTintOpacity</c>）→ 内容（条子 / 唱盘）→ 边框。
/// </summary>
public sealed class MediaPresetPreview : FrameworkElement
{
    public static readonly DependencyProperty PlayerProperty = DependencyProperty.Register(
        nameof(Player), typeof(DesktopMediaPlayer), typeof(MediaPresetPreview),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(PresetKind), typeof(MediaPresetPreview),
        new FrameworkPropertyMetadata(PresetKind.MediaPlayerBars, FrameworkPropertyMetadataOptions.AffectsRender));

    public DesktopMediaPlayer? Player
    {
        get => (DesktopMediaPlayer?)GetValue(PlayerProperty);
        set => SetValue(PlayerProperty, value);
    }

    public PresetKind Kind
    {
        get => (PresetKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    // 液态玻璃笔刷按"颜色模式"缓存（卡片是静态的，但 OnRender 可能因尺寸变化重跑）
    private static Brush? _glassBrush;
    private static string _glassMode = "";

    public MediaPresetPreview()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double w = double.IsInfinity(availableSize.Width) ? 160 : availableSize.Width;
        double h = double.IsInfinity(availableSize.Height) ? 96 : availableSize.Height;
        return new Size(w, h);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 2 || h <= 2) return;
        var p = Player;
        if (p == null) return;

        double radius = Math.Clamp(p.CornerRadius, 0, 14);
        var rect = new Rect(0, 0, w, h);
        var fill = ParseHex(p.FillColor, Color.FromArgb(0x08, 0, 0, 0));
        var borderColor = ParseHex(p.BorderColor, Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));

        // ① 填充
        dc.DrawRoundedRectangle(new SolidColorBrush(fill), null, rect, radius, radius);

        // ② 液态玻璃（分区预设卡同一套：按 GlassColorMode 取渐变，按 GlassTintOpacity 定浓淡）
        DrawGlass(dc, p, rect, radius);

        // ③ 内容
        if (Kind == PresetKind.MediaPlayerVinyl) DrawVinyl(dc, p, w, h);
        else DrawBars(dc, p, w, h);

        // ④ 边框最后画：玻璃不该把边框吃掉
        double bt = Math.Clamp(p.BorderThickness, 0, 3);
        if (bt > 0)
        {
            var pen = new Pen(new SolidColorBrush(borderColor), bt);
            double half = bt / 2;
            dc.DrawRoundedRectangle(null, pen,
                new Rect(half, half, Math.Max(0, w - bt), Math.Max(0, h - bt)),
                Math.Max(0, radius - half), Math.Max(0, radius - half));
        }
    }

    private static void DrawGlass(DrawingContext dc, DesktopMediaPlayer p, Rect rect, double radius)
    {
        if (!p.EnableLiquidGlass) return;
        double opacity = Math.Clamp(p.GlassTintOpacity / 100.0, 0, 1);
        if (opacity <= 0.01) return;

        var brush = GetGlassBrush(p.GlassColorMode);
        if (brush == null) return;

        dc.PushOpacity(opacity);
        dc.PushClip(new RectangleGeometry(rect, radius, radius));
        dc.DrawRectangle(brush, null, rect);
        dc.Pop();
        dc.Pop();
    }

    /// <summary>玻璃笔刷：直接复用分区预设卡的转换器，保证两处颜色配方一致。</summary>
    private static Brush? GetGlassBrush(string? mode)
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
            // 颜色模式不认识 / 转换失败：干脆不画玻璃，别让缩略图炸掉
            _glassBrush = null;
            _glassMode = key;
            return null;
        }
    }

    // ── 律动条预览 ──

    private static void DrawBars(DrawingContext dc, DesktopMediaPlayer p, double w, double h)
    {
        // 缩略图里条数太多会糊成一片，限制上限（只影响预览，不影响真实设置）
        int n = Math.Clamp(p.BarsCount <= 0 ? 16 : p.BarsCount, 4, 22);
        double pad = Math.Max(8, h * 0.16);
        double areaW = w - pad * 2;
        double areaH = h - pad * 2;
        if (areaW <= 4 || areaH <= 4) return;

        double slot = areaW / n;
        double barW = Math.Clamp(p.BarsBarWidth, 1, 12);
        barW = Math.Min(barW, slot * 0.72);
        double baseY = h - pad;

        for (int i = 0; i < n; i++)
        {
            double t = n <= 1 ? 0.5 : (double)i / (n - 1);
            // 中间高两头低的钟形 + 一点确定性起伏，看起来像"正在放歌"
            double bell = Math.Exp(-Math.Pow((t - 0.45) / 0.34, 2));
            double wobble = 0.72 + 0.28 * Math.Sin(i * 1.27 + 0.8);
            double level = Math.Clamp(0.12 + 0.88 * bell * wobble, 0.08, 1.0);
            double barH = Math.Max(2, areaH * level);
            double x = pad + slot * i + (slot - barW) / 2;
            var rect = new Rect(x, baseY - barH, barW, barH);

            var brush = new SolidColorBrush(BarColorAt(p, t));
            brush.Freeze();
            if (p.BarsRounded)
                dc.DrawRoundedRectangle(brush, null, rect, barW / 2, barW / 2);
            else
                dc.DrawRectangle(brush, null, rect);

            if (p.BarsReflection)
            {
                var mirror = new Rect(x, baseY + 1, barW, Math.Min(barH * 0.45, areaH * 0.3));
                var faded = new SolidColorBrush(Color.FromArgb(0x40,
                    brush.Color.R, brush.Color.G, brush.Color.B));
                faded.Freeze();
                dc.PushOpacity(0.55);
                dc.DrawRectangle(faded, null, mirror);
                dc.Pop();
            }
        }
    }

    private static Color BarColorAt(DesktopMediaPlayer p, double t)
    {
        Color c1 = ParseHex(p.BarsColor, Color.FromRgb(0x7C, 0xF5, 0xFF));
        Color c2 = ParseHex(p.BarsColor2, Color.FromRgb(0xB5, 0x8C, 0xFF));
        return p.BarsColorMode switch
        {
            "Single" => c1,
            // 自动换色：预览给一条彩虹，暗示"会自己变"
            "Auto" => FromHsv((0.52 + t * 0.45) % 1.0, 0.72, 1.0),
            _ => Lerp(c1, c2, t),
        };
    }

    // ── 唱片机预览 ──

    private static void DrawVinyl(DrawingContext dc, DesktopMediaPlayer p, double w, double h)
    {
        double cx = w * 0.36, cy = h / 2;
        double r = Math.Min(w * 0.30, h * 0.42);
        if (r <= 4) return;

        var discBrush = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x16));
        dc.DrawEllipse(discBrush, null, new Point(cx, cy), r, r);

        // 纹路
        var groove = new Pen(new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)), 1);
        for (int i = 1; i <= 4; i++)
            dc.DrawEllipse(null, groove, new Point(cx, cy), r * (0.62 + i * 0.08), r * (0.62 + i * 0.08));

        // 封面圆（用条子的主色代替真实封面）
        double cr = r * 0.52;
        var coverBrush = new SolidColorBrush(BarColorAt(p, 0.15));
        coverBrush.Freeze();
        dc.DrawEllipse(coverBrush, null, new Point(cx, cy), cr, cr);

        // 中心孔
        dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x12)), null,
            new Point(cx, cy), r * 0.05, r * 0.05);

        // 右侧假信息条：暗示"预设带曲目区/控制按钮"
        double x = w * 0.66, top = cy - r * 0.55;
        var textBrush = new SolidColorBrush(Color.FromArgb(0xB4, 0xFF, 0xFF, 0xFF));
        var dimBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
        double maxW = w - x - 8;
        if (p.ShowVinylTrackPanel && maxW > 12)
        {
            dc.DrawRoundedRectangle(textBrush, null, new Rect(x, top, maxW * 0.86, 4), 2, 2);
            dc.DrawRoundedRectangle(dimBrush, null, new Rect(x, top + 9, maxW * 0.58, 3), 1.5, 1.5);
            dc.DrawRoundedRectangle(dimBrush, null, new Rect(x, top + 16, maxW * 0.42, 3), 1.5, 1.5);
        }
        if (p.ShowControls)
        {
            double by = top + 30;
            if (by + 6 < h - 4)
            {
                for (int i = 0; i < 3; i++)
                    dc.DrawEllipse(dimBrush, null, new Point(x + 5 + i * 13, by), 4, 4);
            }
        }
    }

    // ── 颜色小工具 ──

    private static Color ParseHex(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try { return (Color)ColorConverter.ConvertFromString(hex)!; }
        catch { return fallback; }
    }

    private static Color Lerp(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb(
            (byte)(a.A + (b.A - a.A) * t),
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }

    private static Color FromHsv(double h, double s, double v)
    {
        h = ((h % 1) + 1) % 1;
        int i = (int)(h * 6);
        double f = h * 6 - i;
        double p = v * (1 - s), q = v * (1 - f * s), t = v * (1 - (1 - f) * s);
        (double r, double g, double b) = (i % 6) switch
        {
            0 => (v, t, p),
            1 => (q, v, p),
            2 => (p, v, t),
            3 => (p, q, v),
            4 => (t, p, v),
            _ => (v, p, q),
        };
        return Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }
}
