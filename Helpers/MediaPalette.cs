using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopZones.Helpers;

/// <summary>
/// 从专辑封面里提取一个"能当强调色用"的主色。
///
/// 用法与约定：
/// - 输入是 <see cref="MediaSessionService.Artwork"/> 那种已 Freeze 的位图；
/// - 只保留**鲜艳**的像素（饱和度/亮度都够），按色相分 24 桶加权，取权重最高的桶，
///   再把结果规整成"亮而饱和"的颜色 —— 封面里的暗部/灰底/白边不会被当成主色；
/// - 封面是灰白黑（没有可用的主色）时返回 null，调用方退回用户手设的颜色；
/// - 单条缓存按**引用相等**判重：同一张封面重复调用不会重复算（一首歌里会调很多次）。
/// </summary>
internal static class MediaPalette
{
    private static BitmapSource? _cachedFor;
    private static Color? _cached;

    /// <summary>取强调色；封面为空或没有可用主色时返回 null。</summary>
    public static Color? GetAccent(BitmapSource? art)
    {
        if (art == null)
        {
            _cachedFor = null;
            _cached = null;
            return null;
        }
        if (ReferenceEquals(art, _cachedFor)) return _cached;
        _cachedFor = art;
        _cached = Extract(art);
        return _cached;
    }

    /// <summary>强调色的"搭档色"（渐变第二档）：同色相略微偏移 + 提亮，保证和主色是一家人。</summary>
    public static Color Companion(Color accent)
    {
        ToHsv(accent, out double h, out double s, out double v);
        return FromHsv(h + 0.075, Math.Clamp(s * 0.92, 0.30, 0.95), Math.Clamp(v * 1.06, 0.55, 1.0));
    }

    /// <summary>把颜色整体换一个透明度（分割线/进度底槽用）。</summary>
    public static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    // ── 提取 ──

    private const int HueBuckets = 24;

    private static Color? Extract(BitmapSource art)
    {
        try
        {
            // 缩到 ≤48px 再统计：几百到几千个采样点足够判断主色，绝不会卡 UI 线程。
            const int target = 48;
            int maxSide = Math.Max(art.PixelWidth, art.PixelHeight);
            BitmapSource src = art;
            if (maxSide > target)
            {
                double scale = (double)target / maxSide;
                src = new TransformedBitmap(art, new ScaleTransform(scale, scale));
            }
            var bgra = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            int w = bgra.PixelWidth, h = bgra.PixelHeight;
            if (w <= 0 || h <= 0) return null;

            int stride = w * 4;
            var pixels = new byte[stride * h];
            bgra.CopyPixels(pixels, stride, 0);

            Span<double> weight = stackalloc double[HueBuckets];
            Span<double> sumR = stackalloc double[HueBuckets];
            Span<double> sumG = stackalloc double[HueBuckets];
            Span<double> sumB = stackalloc double[HueBuckets];
            double total = 0;

            for (int i = 0; i + 3 < pixels.Length; i += 4)
            {
                byte b = pixels[i], g = pixels[i + 1], r = pixels[i + 2], a = pixels[i + 3];
                if (a < 128) continue;

                double max = Math.Max(r, Math.Max(g, b));
                double min = Math.Min(r, Math.Min(g, b));
                double v = max / 255.0;
                double s = max <= 0 ? 0 : (max - min) / max;
                // 跳过纯黑/纯白/灰（那些是封面留白与文字，不是"专辑色"）
                if (v < 0.10 || v > 0.985 || s < 0.16) continue;

                double hue;
                double d = max - min;
                if (d <= 0) continue;
                if (max == r) hue = ((g - b) / d % 6 + 6) % 6;
                else if (max == g) hue = (b - r) / d + 2;
                else hue = (r - g) / d + 4;
                hue /= 6;

                int bucket = Math.Clamp((int)(hue * HueBuckets), 0, HueBuckets - 1);
                double wgt = s * s + 0.35;          // 越鲜艳越优先，灰一点也给点分
                weight[bucket] += wgt;
                sumR[bucket] += r * wgt;
                sumG[bucket] += g * wgt;
                sumB[bucket] += b * wgt;
                total += wgt;
            }

            if (total <= 0) return null;
            int best = 0;
            for (int i = 1; i < HueBuckets; i++)
                if (weight[i] > weight[best]) best = i;
            if (weight[best] <= 0) return null;
            // 主色只是零星几块（比如封面角落一个红点）→ 不算主色
            if (weight[best] < total * 0.08) return null;

            var avg = Color.FromRgb(
                (byte)Math.Clamp(sumR[best] / weight[best], 0, 255),
                (byte)Math.Clamp(sumG[best] / weight[best], 0, 255),
                (byte)Math.Clamp(sumB[best] / weight[best], 0, 255));

            // 规整：强制足够饱和 + 足够亮，暗色封面提取出来的颜色才在深色桌面上看得见
            ToHsv(avg, out double hh, out double ss, out double vv);
            return FromHsv(hh,
                Math.Clamp(ss, 0.48, 0.95),
                Math.Clamp(vv, 0.70, 0.98));
        }
        catch (Exception ex)
        {
            DzTrace.Log($"[MediaPalette] 取主色失败: {ex.GetType().Name} {ex.Message}");
            return null;
        }
    }

    // ── HSV ──

    private static void ToHsv(Color c, out double h, out double s, out double v)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double d = max - min;
        v = max;
        s = max <= 0 ? 0 : d / max;
        if (d <= 0) { h = 0; return; }
        if (max == r) h = ((g - b) / d % 6 + 6) % 6 / 6;
        else if (max == g) h = ((b - r) / d + 2) / 6;
        else h = ((r - g) / d + 4) / 6;
    }

    private static Color FromHsv(double h, double s, double v)
    {
        h = ((h % 1) + 1) % 1;
        s = Math.Clamp(s, 0, 1);
        v = Math.Clamp(v, 0, 1);
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
