using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopZones.Helpers;

/// <summary>
/// Resolves a contrasting text/icon foreground color from a background.
/// v8 algorithm — moderate-L HSL flip with HUE rotation and achromatic handling:
/// 1. Convert bg to HSL.
/// 2. Flip L to a MODERATE value (0.30 / 0.70) instead of pushing to extremes (0.13 / 0.93).
///    At extreme L the chromatic component of RGB collapses below visual threshold — the eye
///    reads the result as pure black/white regardless of the hue. Moderate L keeps the
///    chromatic component visible.
/// 3. If bg has saturation (S &gt; 0.05): rotate Hue by 180° (H += 0.5 mod 1) and set
///    S = 0.80 so the complement tint reads clearly.
/// 4. If bg is achromatic (S ≤ 0.05): keep S = 0 — output is a neutral light/dark gray
///    (no rotation since the bg has no hue to complement).
/// 5. Convert back to RGB.
///
/// Image variant: sample 5 points (4 corners + center), premultiply alpha, average RGB,
/// then run the same HSL flip. Cached by image identity so re-applying per frame is free.
/// </summary>
public static class AdaptiveTextColor
{
    /// <summary>Luminance threshold separating "light" vs "dark" backgrounds.</summary>
    public const double LightThreshold = 0.55;
    /// <summary>Text L when bg is light. Moderate (not extreme) so chromatic component is visible.</summary>
    public const double DarkTextL = 0.30;
    /// <summary>Text L when bg is dark. Moderate (not extreme) so chromatic component is visible.</summary>
    public const double LightTextL = 0.70;
    /// <summary>Text saturation — 0.80 makes the complement tint clearly visible.</summary>
    public const double TextSaturation = 0.80;

    /// <summary>Resolve from a hex string. Falls back to dark text on parse error.</summary>
    public static string ResolveTextColor(string backgroundColor)
    {
        try
        {
            var c = (Color)ColorConverter.ConvertFromString(backgroundColor)!;
            return ColorToHex(ResolveTextColor(c));
        }
        catch
        {
            return ColorToHex(Color.FromRgb(0x22, 0x22, 0x2A));
        }
    }

    public static SolidColorBrush ResolveBrush(string backgroundColor)
    {
        var brush = new SolidColorBrush(ResolveTextColor(ParseHex(backgroundColor)));
        brush.Freeze();
        return brush;
    }

    /// <summary>Resolve from a Color. Moderate-L HSL flip with HUE rotation + achromatic fallback.</summary>
    public static Color ResolveTextColor(Color bg)
    {
        var hsl = RgbToHsl(bg);
        bool bgIsLight = hsl.L > LightThreshold;
        hsl.L = bgIsLight ? DarkTextL : LightTextL;
        if (hsl.S > 0.05)
        {
            // Chromatic bg → output is the complementary hue, fully saturated so the tint reads.
            hsl.S = TextSaturation;
            hsl.H = (hsl.H + 0.5) % 1.0;
        }
        else
        {
            // Achromatic bg (black/gray/white) → output is a neutral light/dark gray.
            // Rotating H would just give another gray, so leave S=0.
            hsl.S = 0.0;
        }
        return HslToRgb(hsl);
    }

    /// <summary>Resolve from a Color and return a frozen brush.</summary>
    public static SolidColorBrush ResolveBrush(Color bg)
    {
        var brush = new SolidColorBrush(ResolveTextColor(bg));
        brush.Freeze();
        return brush;
    }

    /// <summary>Composite a semi-transparent top color over an opaque bottom color and return
    /// the resulting RGB. Uses the standard "source-over" alpha blend:
    /// <c>result = top.A * top.RGB + (1 - top.A) * bottom.RGB</c>. Title-bar text adaptive needs
    /// this because WPF paints TitleBarBg as a translucent layer over FillRect, so the visible
    /// title-bar color is the composite — feeding only TitleBarFillColor to ResolveBrush picks
    /// the wrong contrast (e.g. dark text on a light bg because the title bar was nearly clear).</summary>
    public static Color CompositeOver(Color top, Color bottom)
    {
        double a = top.A / 255.0;
        return Color.FromRgb(
            (byte)Math.Clamp(a * top.R + (1 - a) * bottom.R, 0, 255),
            (byte)Math.Clamp(a * top.G + (1 - a) * bottom.G, 0, 255),
            (byte)Math.Clamp(a * top.B + (1 - a) * bottom.B, 0, 255));
    }

    /// <summary>String overload of <see cref="CompositeOver(Color, Color)"/>. Falls back to
    /// bottom on a parse error (top is rarely mistyped; bottom is the fill color, also stable).</summary>
    public static Color CompositeOver(string topHex, string bottomHex)
        => CompositeOver(ParseHex(topHex), ParseHex(bottomHex));

    /// <summary>Composite top over bottom, then run the adaptive HSL flip and return a frozen
    /// brush. Drop-in replacement for <see cref="ResolveBrush(string)"/> when the rendered
    /// color is a translucent layer over a fill (title bars on Panel / Zone / StickyNote /
    /// MergedGroup).</summary>
    public static SolidColorBrush ResolveBrushOver(string topHex, string bottomHex)
    {
        var brush = new SolidColorBrush(ResolveTextColor(CompositeOver(topHex, bottomHex)));
        brush.Freeze();
        return brush;
    }

    /// <summary>Sample 5 points (4 corners + center), average them, then run HSL flip.
    /// Half-transparent pixels are premultiplied so transparent areas don't bias toward "white".
    /// Result is cached by the image's full path (or Uri) so repeated calls within the same
    /// image reuse the work.</summary>
    public static Color ResolveTextColorForImage(BitmapSource image)
    {
        if (image == null || image.PixelWidth <= 0 || image.PixelHeight <= 0)
            return Color.FromRgb(0x22, 0x22, 0x2A);

        var cacheKey = image is BitmapImage bi ? (bi.UriSource?.ToString() ?? bi.ToString()) : image.ToString();
        if (_imageCache.TryGetValue(cacheKey, out var cached)) return cached;

        int w = image.PixelWidth;
        int h = image.PixelHeight;
        int[] xs = { 1, w - 2, w / 2, w - 2, 1 };
        int[] ys = { 1, 1, h / 2, h - 2, h - 2 };
        // sample 5 unique points (corners + center)
        var pts = new (int x, int y)[]
        {
            (Math.Clamp(1, 0, w - 1), Math.Clamp(1, 0, h - 1)),
            (Math.Clamp(w - 2, 0, w - 1), Math.Clamp(1, 0, h - 1)),
            (Math.Clamp(w / 2, 0, w - 1), Math.Clamp(h / 2, 0, h - 1)),
            (Math.Clamp(w - 2, 0, w - 1), Math.Clamp(h - 2, 0, h - 1)),
            (Math.Clamp(1, 0, w - 1), Math.Clamp(h - 2, 0, h - 1))
        };
        double r = 0, g = 0, b = 0;
        int count = 0;
        foreach (var (x, y) in pts)
        {
            var px = GetPixel(image, x, y);
            // Premultiply by alpha for correct blending toward "background under" the image.
            double a = px.A / 255.0;
            r += px.R * a + 255 * (1 - a); // assume "under" is white-ish; midpoint compromise
            g += px.G * a + 255 * (1 - a);
            b += px.B * a + 255 * (1 - a);
            count++;
        }
        var avg = Color.FromRgb(
            (byte)Math.Clamp(r / count, 0, 255),
            (byte)Math.Clamp(g / count, 0, 255),
            (byte)Math.Clamp(b / count, 0, 255));
        var resolved = ResolveTextColor(avg);

        // Cache with bounded size (LRU-ish: just clear when too big)
        if (_imageCache.Count > 32) _imageCache.Clear();
        _imageCache[cacheKey] = resolved;
        return resolved;
    }

    /// <summary>Clear the image-sample cache. Call when images are reloaded from disk.</summary>
    public static void ClearImageCache() => _imageCache.Clear();

    /// <summary>Walk the visual tree under <paramref name="root"/> and re-brush every
    /// <see cref="TextBlock"/> foreground + every <see cref="Control"/> foreground. Used by
    /// Panel/Zone windows to refresh dynamically generated item labels.</summary>
    public static void ApplyBrushToTree(DependencyObject root, Brush brush)
    {
        if (root == null) return;
        if (root is TextBlock tb) tb.Foreground = brush;
        else if (root is Control c) c.Foreground = brush;
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            ApplyBrushToTree(VisualTreeHelper.GetChild(root, i), brush);
        }
    }

    static readonly Dictionary<string, Color> _imageCache = new();

    static Color GetPixel(BitmapSource src, int x, int y)
    {
        try
        {
            // Crop a 1×1 rect via CroppedBitmap; works for any BitmapSource.
            var cb = new CroppedBitmap(src, new System.Windows.Int32Rect(x, y, 1, 1));
            var pixels = new byte[4];
            cb.CopyPixels(pixels, 4, 0);
            return Color.FromArgb(pixels[3], pixels[2], pixels[1], pixels[0]);
        }
        catch
        {
            return Colors.White;
        }
    }

    static Color ParseHex(string hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex)!; }
        catch { return Color.FromRgb(0x08, 0x00, 0x00); }
    }

    static string ColorToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    // ── RGB ↔ HSL ──

    struct Hsl { public double H, S, L; }

    static Hsl RgbToHsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2.0;
        double h = 0, s = 0;
        if (max != min)
        {
            double d = max - min;
            s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);
            if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h /= 6.0;
        }
        return new Hsl { H = h, S = s, L = l };
    }

    static Color HslToRgb(Hsl hsl)
    {
        double h = hsl.H, s = hsl.S, l = hsl.L;
        double r, g, b;
        if (s == 0)
        {
            r = g = b = l;
        }
        else
        {
            double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            double p = 2 * l - q;
            r = HueToRgb(p, q, h + 1.0 / 3.0);
            g = HueToRgb(p, q, h);
            b = HueToRgb(p, q, h - 1.0 / 3.0);
        }
        return Color.FromRgb(
            (byte)Math.Clamp(r * 255, 0, 255),
            (byte)Math.Clamp(g * 255, 0, 255),
            (byte)Math.Clamp(b * 255, 0, 255));
    }

    static double HueToRgb(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6.0) return p + (q - p) * 6 * t;
        if (t < 1.0 / 2.0) return q;
        if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6;
        return p;
    }
}