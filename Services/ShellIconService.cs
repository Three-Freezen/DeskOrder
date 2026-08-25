using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopZones.Helpers;
using Microsoft.Win32;

namespace DesktopZones.Services;

public class ShellIconService
{
    /// <summary>SHIL_JUMBO — the 256px source resolution the desktop draws icons from.</summary>
    private const int JumboSize = 256;

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    private readonly ConcurrentDictionary<string, ImageSource?> _iconCache = new();
    private static readonly ImageSource? _folderIcon = GetSystemIcon(
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop), true);

    // ── Recycle Bin state-aware icon ──

    public const string RecycleBinSpec = "::{645FF040-5081-101B-9F08-00AA002F954E}";
    private const string RecycleBinDefaultIconKey = @"CLSID\{645FF040-5081-101B-9F08-00AA002F954E}\DefaultIcon";

    private static DateTime _recycleCheckedUtc = DateTime.MinValue;
    private static bool _recycleFull;

    public static bool IsRecycleBin(string target) =>
        string.Equals(target, RecycleBinSpec, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the Recycle Bin currently contains at least one item. Cached for ~2s.</summary>
    public static bool RecycleBinHasItems()
    {
        if (DateTime.UtcNow - _recycleCheckedUtc > TimeSpan.FromSeconds(2))
        {
            _recycleFull = QueryRecycleBinCount() > 0;
            _recycleCheckedUtc = DateTime.UtcNow;
        }
        return _recycleFull;
    }

    /// <summary>Force the next RecycleBinHasItems call to re-query the shell (used right after emptying the bin).</summary>
    public static void InvalidateRecycleBinState() => _recycleCheckedUtc = DateTime.MinValue;

    private static long QueryRecycleBinCount()
    {
        try
        {
            var info = new NativeMethods.SHQUERYRBINFO { cbSize = (uint)Marshal.SizeOf<NativeMethods.SHQUERYRBINFO>() };
            return NativeMethods.SHQueryRecycleBinW(null, ref info) == 0 ? info.i64NumItems : 0;
        }
        catch { return 0; }
    }

    /// <summary>
    /// Icon for a zone item. When the item carries a shortcut icon location
    /// ("file,index" — the exact icon the desktop shows for that shortcut), it is
    /// extracted first; otherwise the target's own icon is used.
    /// </summary>
    public ImageSource? GetIcon(string path, Models.ItemType type, string? iconLocation = null)
    {
        if (type == Models.ItemType.ShellLocation && IsRecycleBin(path))
            return GetRecycleBinStateIcon();

        string cacheKey = iconLocation ?? path;
        return _iconCache.GetOrAdd(cacheKey, _ =>
        {
            ImageSource? src = null;
            if (iconLocation != null)
                src = GetIconLocationImage(iconLocation);
            src ??= type switch
            {
                Models.ItemType.ShellLocation => GetShellLocationIcon(path) ?? _folderIcon,
                Models.ItemType.Folder => GetSystemIcon(path, true) ?? _folderIcon,
                _ => GetSystemIcon(path, true)
            };
            return CropLetterboxed(src);
        });
    }

    /// <summary>
    /// Extract an icon from an explicit "file,index" icon location (a shortcut's custom
    /// icon). Index 0 .ico files are extracted via IShellItemImageFactory — this keeps
    /// alpha/rounded corners intact even for PNG-compressed frames, which
    /// SHDefExtractIconW cannot decode. Non-zero indices (dll/icl icons) go through
    /// SHDefExtractIconW first.
    /// </summary>
    private static ImageSource? GetIconLocationImage(string location)
    {
        try
        {
            int sep = location.LastIndexOf(',');
            if (sep <= 0 || sep >= location.Length - 1) return null;
            string file = location[..sep].Trim().Trim('"');
            if (!int.TryParse(location[(sep + 1)..].Trim(), out var index)) return null;
            file = Environment.ExpandEnvironmentVariables(file);
            if (!File.Exists(file)) return null;

            if (index == 0)
            {
                var hbm = ShellOle.GetItemIconBitmap(file, JumboSize);
                if (hbm != null)
                {
                    try
                    {
                        return System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                            hbm.Value, IntPtr.Zero, System.Windows.Int32Rect.Empty,
                            BitmapSizeOptions.FromEmptyOptions());
                    }
                    finally { NativeMethods.DeleteObject(hbm.Value); }
                }
            }

            uint sz = (uint)(JumboSize | (JumboSize << 16));
            if (NativeMethods.SHDefExtractIconW(file, index, 0, out var hl, out var hs, sz) > 0 && hl != IntPtr.Zero)
            {
                try
                {
                    return System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                        hl, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                }
                finally
                {
                    NativeMethods.DestroyIcon(hl);
                    if (hs != IntPtr.Zero) NativeMethods.DestroyIcon(hs);
                }
            }

            var hbm2 = ShellOle.GetItemIconBitmap(file, JumboSize);
            if (hbm2 != null)
            {
                try
                {
                    return System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                        hbm2.Value, IntPtr.Zero, System.Windows.Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                }
                finally { NativeMethods.DeleteObject(hbm2.Value); }
            }
            return null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Safety net: whatever extraction path produced the bitmap, if the actual icon art
    /// only occupies a small region of a much larger canvas, crop to the art bounds so
    /// WPF fills the display box with the real art.
    /// IMPORTANT: the art bound is measured at <see cref="CropAlphaThreshold"/> — many
    /// icons (e.g. small 60px art inside a 256px canvas) carry a wide soft glow/shadow
    /// whose alpha spans the whole canvas (verified: BCUT ioc / Riffstation have glow
    /// pixels up to alpha ~47 reaching the canvas edges). Measuring at alpha > 0 there
    /// sees a full canvas and never crops — the "small icon with a glow ring" symptom.
    /// Threshold 96 lands just outside the glow band and captures the solid art.
    /// Normal icons are unaffected (cmd 97%×80%, DeskOrder 88% at the same threshold).
    /// </summary>
    private static ImageSource? CropLetterboxed(ImageSource? src)
    {
        try
        {
            if (src is not BitmapSource bmp) return src;
            if (bmp.Format != PixelFormats.Bgra32 && bmp.Format != PixelFormats.Pbgra32
                && bmp.Format != PixelFormats.Bgr32)
                return src;
            int w = bmp.PixelWidth, h = bmp.PixelHeight;
            if (w < 16 || h < 16) return src;

            int stride = w * 4;
            var px = new byte[stride * h];
            bmp.CopyPixels(px, stride, 0);

            int minX = w, minY = h, maxX = -1, maxY = -1;
            for (int y = 0; y < h; y++)
            {
                int row = y * stride;
                for (int x = 0; x < w; x++)
                {
                    if (px[row + x * 4 + 3] >= CropAlphaThreshold)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }
            if (maxX < minX || maxY < minY) return src; // no solid art — return as-is

            int cw = maxX - minX + 1, ch = maxY - minY + 1;
            if (cw >= w * 0.85 && ch >= h * 0.85) return src; // art fills at least 85% of the canvas
            return new CroppedBitmap(bmp, new System.Windows.Int32Rect(minX, minY, cw, ch));
        }
        catch { return src; }
    }

    /// <summary>Alpha threshold separating solid icon art from its soft glow/shadow band.</summary>
    private const byte CropAlphaThreshold = 96;

    /// <summary>
    /// State-aware Recycle Bin icon: reads the shell's own Full/Empty icon locations from
    /// HKCR\CLSID\{645FF040-...}\DefaultIcon (exactly what the desktop switches on) and
    /// extracts the current system icon at 256px (desktop jumbo quality).
    /// </summary>
    private ImageSource? GetRecycleBinStateIcon()
    {
        bool full = RecycleBinHasItems();
        return _iconCache.GetOrAdd(RecycleBinSpec + (full ? "|full" : "|empty"),
            _ => BuildRecycleBinIcon(full) ?? GetShellLocationIcon(RecycleBinSpec) ?? _folderIcon);
    }

    private static ImageSource? BuildRecycleBinIcon(bool full)
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(RecycleBinDefaultIconKey);
            if (key == null) return null;
            string? raw = key.GetValue(full ? "Full" : "Empty") as string
                ?? key.GetValue(null) as string;
            if (string.IsNullOrWhiteSpace(raw)) return null;
            raw = Environment.ExpandEnvironmentVariables(raw.Trim());
            int sep = raw.LastIndexOf(',');
            if (sep <= 0 || sep >= raw.Length - 1) return null;
            var file = raw[..sep].Trim('"');
            if (!int.TryParse(raw[(sep + 1)..], out var index)) return null;
            if (!File.Exists(file)) return null;

            uint size = (uint)(JumboSize | (JumboSize << 16)); // MAKELONG(256, 256) — desktop jumbo pixel level
            if (NativeMethods.SHDefExtractIconW(file, index, 0, out var hLarge, out var hSmall, size) <= 0
                || hLarge == IntPtr.Zero)
                return null;
            try
            {
                return System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                    hLarge, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            }
            finally
            {
                NativeMethods.DestroyIcon(hLarge);
                if (hSmall != IntPtr.Zero) NativeMethods.DestroyIcon(hSmall);
            }
        }
        catch { return null; }
    }

    /// <summary>
    /// Icon for a virtual shell object ("::{GUID}"): primary path asks
    /// IShellItemImageFactory at 256px (preserves alpha/rounded corners); the jumbo
    /// system image list is the fallback — it can render legacy masked icons as opaque
    /// blocks, which breaks rounded corners. Further fallbacks: SHGetFileInfo, then the
    /// resolved known-folder path, then the generic folder icon.
    /// </summary>
    private static ImageSource? GetShellLocationIcon(string spec)
    {
        // Primary: real system icon bitmap straight from the shell item itself
        // (IShellItemImageFactory — correct alpha, SCALEUP fills the canvas).
        var hbm = ShellOle.GetItemIconBitmap(spec, JumboSize);
        if (hbm != null)
        {
            try
            {
                // Convert the DIB section directly to a BitmapSource. Roundtripping
                // through Image.FromHbitmap/GetHbitmap drops the alpha channel (the
                // black frame around the icon) and loses pixel fidelity. This is the
                // same conversion Explorer-style icon samples use.
                return System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                    hbm.Value, IntPtr.Zero, System.Windows.Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
            }
            catch { }
            finally { NativeMethods.DeleteObject(hbm.Value); }
        }

        // Jumbo system image list.
        var hIcon = ShellOle.GetJumboIcon(spec);
        if (hIcon != null)
        {
            try
            {
                return System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                    hIcon.Value, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            }
            catch { }
            finally { NativeMethods.DestroyIcon(hIcon.Value); }
        }

        var direct = GetSystemIcon(spec, true);
        if (direct != null) return direct;

        try
        {
            var guidText = spec.StartsWith("::{", StringComparison.Ordinal)
                ? spec.Substring(3).TrimEnd('}') : string.Empty;
            if (Guid.TryParse(guidText, out var guid) &&
                NativeMethods.SHGetKnownFolderPath(ref guid, 0, IntPtr.Zero, out var pPath) == 0 && pPath != IntPtr.Zero)
            {
                string folderPath;
                try { folderPath = Marshal.PtrToStringUni(pPath) ?? string.Empty; }
                finally { NativeMethods.CoTaskMemFree(pPath); }
                if (!string.IsNullOrEmpty(folderPath))
                {
                    var icon = GetSystemIcon(folderPath, true);
                    if (icon != null) return icon;
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Icon for a file/folder/shortcut target. Primary path asks IShellItemImageFactory
    /// at 256px with SCALEUP — this preserves alpha (rounded corners stay rounded,
    /// verified against the jumbo list which renders legacy masked icons as opaque
    /// blocks). Letterboxing remnants are removed by <see cref="CropLetterboxed"/> in
    /// <see cref="GetIcon"/>. Falls back to the jumbo system image list, then the legacy
    /// 32px system icon.
    /// </summary>
    private static ImageSource? GetSystemIcon(string path, bool large)
    {
        // Primary: IShellItemImageFactory at 256px.
        try
        {
            var hbm = ShellOle.GetItemIconBitmap(path, JumboSize);
            if (hbm != null)
            {
                try
                {
                    // Convert the DIB section directly — roundtrips through GetHbitmap
                    // drop the alpha channel (black corners). Native 256px source; the
                    // Image element scales it with HighQuality.
                    return System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                        hbm.Value, IntPtr.Zero, System.Windows.Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                }
                finally { NativeMethods.DeleteObject(hbm.Value); }
            }
        }
        catch
        {
            // fall through to the jumbo list
        }

        // Fallback: jumbo system image list.
        var hIcon = ShellOle.GetJumboIcon(path);
        if (hIcon != null)
        {
            try
            {
                return System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                    hIcon.Value, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            }
            catch { }
            finally { NativeMethods.DestroyIcon(hIcon.Value); }
        }

        try
        {
            using var icon = NativeMethods.ExtractIcon(path, large);
            if (icon == null) return null;

            return System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        }
        catch
        {
            return null;
        }
    }

    public void ClearCache()
    {
        _iconCache.Clear();
    }
}
