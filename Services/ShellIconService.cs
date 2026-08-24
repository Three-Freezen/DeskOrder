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

    public ImageSource? GetIcon(string path, Models.ItemType type)
    {
        if (type == Models.ItemType.ShellLocation && IsRecycleBin(path))
            return GetRecycleBinStateIcon();

        return _iconCache.GetOrAdd(path, _ =>
        {
            if (type == Models.ItemType.ShellLocation)
                return GetShellLocationIcon(path) ?? _folderIcon;

            if (type == Models.ItemType.Folder)
                return GetSystemIcon(path, true) ?? _folderIcon;

            return GetSystemIcon(path, true);
        });
    }

    /// <summary>
    /// State-aware Recycle Bin icon: reads the shell's own Full/Empty icon locations from
    /// HKCR\CLSID\{645FF040-...}\DefaultIcon (exactly what the desktop switches on) and
    /// extracts the current system icon at 48px.
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

            uint size = (uint)(48 | (48 << 16)); // MAKELONG(48, 48) — desktop icon pixel level
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
    /// IShellItemImageFactory — the exact API Explorer/desktop use to draw these icons —
    /// so the current system icon comes back at 48px. Falls back to SHGetFileInfo, then
    /// to the resolved known-folder path, then to the generic folder icon.
    /// </summary>
    private static ImageSource? GetShellLocationIcon(string spec)
    {
        // Primary: real system icon bitmap straight from the shell item itself.
        var hbm = ShellOle.GetItemIconBitmap(spec, 48);
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

    private static ImageSource? GetSystemIcon(string path, bool large)
    {
        try
        {
            using var icon = NativeMethods.ExtractIcon(path, large);
            if (icon == null) return null;

            // Convert the HICON directly — ToBitmap/GetHbitmap roundtrips drop the
            // alpha channel (black corners) and force a 48px upscale of a 32px icon.
            // The native icon size is preserved; WPF scales it with HighQuality.
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
