using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopZones.Helpers;

namespace DesktopZones.Services;

public class ShellIconService
{
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    private readonly ConcurrentDictionary<string, ImageSource?> _iconCache = new();
    private static readonly ImageSource? _folderIcon = GetSystemIcon(
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop), true);

    public ImageSource? GetIcon(string path, Models.ItemType type)
    {
        return _iconCache.GetOrAdd(path, _ =>
        {
            if (type == Models.ItemType.Folder)
                return GetSystemIcon(path, true) ?? _folderIcon;

            return GetSystemIcon(path, true);
        });
    }

    private static ImageSource? GetSystemIcon(string path, bool large)
    {
        try
        {
            using var icon = NativeMethods.ExtractIcon(path, large);
            if (icon == null) return null;

            using var bitmap = icon.ToBitmap();
            var hBitmap = bitmap.GetHbitmap();
            try
            {
                return System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap, IntPtr.Zero, System.Windows.Int32Rect.Empty,
                    BitmapSizeOptions.FromWidthAndHeight(large ? 48 : 24, large ? 48 : 24));
            }
            finally
            {
                DeleteObject(hBitmap);
            }
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
