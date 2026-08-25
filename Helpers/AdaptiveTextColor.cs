using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopZones.Helpers;

/// <summary>
/// Resolves a contrasting text/icon foreground color from a background.
///
/// v9 algorithm — WCAG contrast against pure black and pure white:
/// compute the contrast ratio between bg and #000, and between bg and #FFF,
/// pick whichever is higher. Always returns pure black or pure white; never
/// a chromatic tint. This guarantees the more readable of the two extremes
/// is always chosen, regardless of where bg sits on the luminance scale.
///
/// Replaces the old v8 HSL-flip algorithm which produced a chromatic
/// "contrast" color (e.g. dark blue on yellow) that read as a third color
/// rather than as readable text. Same algorithm as
/// ThemeService.ApplySystemAccentIfApplicable so the management UI and
/// the surface windows agree on what "readable" means.
/// </summary>
public static class AdaptiveTextColor
{
    /// <summary>Kept for API compatibility — no longer used internally; the algorithm
    /// is now contrast-based instead of threshold-based.</summary>
    public const double LightThreshold = 0.55;

    /// <summary>Resolve from a hex string. Falls back to white text on parse error.</summary>
    public static string ResolveTextColor(string backgroundColor)
    {
        try
        {
            var c = (Color)ColorConverter.ConvertFromString(backgroundColor)!;
            return ColorToHex(ResolveTextColor(c));
        }
        catch
        {
            return ColorToHex(Colors.White);
        }
    }

    public static SolidColorBrush ResolveBrush(string backgroundColor)
    {
        var brush = new SolidColorBrush(ResolveTextColor(ParseHex(backgroundColor)));
        brush.Freeze();
        return brush;
    }

    /// <summary>Resolve from a Color using WCAG contrast: black or white.</summary>
    public static Color ResolveTextColor(Color bg)
    {
        return Contrast(bg, Colors.Black) >= Contrast(bg, Colors.White)
            ? Colors.Black
            : Colors.White;
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

    /// <summary>Composite top over bottom, then run the WCAG contrast and return a frozen
    /// brush. Drop-in replacement for <see cref="ResolveBrush(string)"/> when the rendered
    /// color is a translucent layer over a fill (title bars on Panel / Zone / StickyNote /
    /// MergedGroup).</summary>
    public static SolidColorBrush ResolveBrushOver(string topHex, string bottomHex)
    {
        var brush = new SolidColorBrush(ResolveTextColor(CompositeOver(topHex, bottomHex)));
        brush.Freeze();
        return brush;
    }

    /// <summary>Sample 5 points (4 corners + center), average them, then run WCAG contrast.
    /// Half-transparent pixels are premultiplied so transparent areas don't bias toward "white".</summary>
    public static Color ResolveTextColorForImage(BitmapSource image)
    {
        if (image == null || image.PixelWidth <= 0 || image.PixelHeight <= 0)
            return Colors.White;

        int w = image.PixelWidth;
        int h = image.PixelHeight;
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
        // ponytail: no cache here — BitmapSource has no stable hash (Equals uses reference),
        // and BitmapImage.ToString/UriSource are non-deterministic across decodes. Callers
        // already memoize via the owning Zone's BackgroundImagePath, so the per-sample work
        // only runs when the image actually changes. Add when profiling shows hot path.
        return ResolveTextColor(avg);
    }

    /// <summary>
    /// Sample the given window-space points against a transformed background image,
    /// composite each source pixel over <paramref name="backdrop"/>, and return the average
    /// RGB. Returns null when no sample point lands on the image. The transform matches the
    /// one used to place the image on screen: <paramref name="scale"/> plus the image
    /// element's window-space top-left offset (<paramref name="offsetX"/>, <paramref name="offsetY"/>).
    /// </summary>
    public static Color? AverageImageOver(
        BitmapSource image,
        double scale,
        double offsetX,
        double offsetY,
        IEnumerable<(double wx, double wy)> points,
        Color backdrop)
    {
        if (image == null || image.PixelWidth <= 0 || image.PixelHeight <= 0 || scale <= 0)
            return null;

        double r = 0, g = 0, b = 0;
        int count = 0;
        foreach (var (wx, wy) in points)
        {
            double ix = (wx - offsetX) / scale;
            double iy = (wy - offsetY) / scale;
            if (ix < 0 || iy < 0 || ix >= image.PixelWidth || iy >= image.PixelHeight)
                continue;
            var px = GetPixel(image, (int)ix, (int)iy);
            var composite = CompositeOver(px, backdrop);
            r += composite.R;
            g += composite.G;
            b += composite.B;
            count++;
        }
        if (count == 0) return null;
        return Color.FromRgb(
            (byte)Math.Clamp(r / count, 0, 255),
            (byte)Math.Clamp(g / count, 0, 255),
            (byte)Math.Clamp(b / count, 0, 255));
    }

    /// <summary>No-op kept for API compatibility — see <see cref="ResolveTextColorForImage"/>.</summary>
    public static void ClearImageCache() { }

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

    static Color GetPixel(BitmapSource src, int x, int y)
    {
        try
        {
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
        catch { return Colors.White; }
    }

    static string ColorToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    // ── WCAG contrast ──

    static double Linear(byte v)
    {
        var s = v / 255.0;
        return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }

    static double Luminance(Color c)
        => 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);

    static double Contrast(Color a, Color b)
    {
        var la = Luminance(a); var lb = Luminance(b);
        var hi = Math.Max(la, lb); var lo = Math.Min(la, lb);
        return (hi + 0.05) / (lo + 0.05);
    }
}