using System;
using System.Runtime.InteropServices;

namespace DesktopZones.Helpers;

/// <summary>
/// Native shell interop shared by the shell-location resolver and the shell icon
/// service. OLE drag-drop is handled entirely by WPF's built-in AllowDrop target
/// (which shows the correct "not allowed" cursor for non-file drags).
/// </summary>
internal static class ShellOle
{
    public const uint SIGDN_DESKTOPABSOLUTEPARSING = 0x80028000;

    public static readonly Guid IID_IShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
    public static readonly Guid IID_IShellItemImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    // SIIGBF — IShellItemImageFactory.GetImage flags
    public const uint SIIGBF_BIGGERSIZEOK = 0x00000001;
    public const uint SIIGBF_ICONONLY = 0x00000004;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

    [DllImport("shell32.dll")]
    public static extern int SHCreateItemFromIDList(IntPtr pidl, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

    /// <summary>True when the string is a desktop-absolute virtual shell object spec ("::{GUID}").</summary>
    public static bool IsShellSpec(string target) =>
        !string.IsNullOrEmpty(target) && target.StartsWith("::{", StringComparison.Ordinal);

    /// <summary>
    /// Extract the real system icon bitmap for a shell object via IShellItemImageFactory —
    /// the same API Explorer/desktop use to draw these icons. Returns an HBITMAP the
    /// caller must free with DeleteObject, or null when unavailable.
    /// </summary>
    public static IntPtr? GetItemIconBitmap(string spec, int sizePx)
    {
        IntPtr pidl = IntPtr.Zero;
        try
        {
            if (SHParseDisplayName(spec, IntPtr.Zero, out pidl, 0, out _) != 0 || pidl == IntPtr.Zero) return null;
            var iidItem = IID_IShellItem;
            if (SHCreateItemFromIDList(pidl, ref iidItem, out var item) != 0 || item == null) return null;
            if (item is not IShellItemImageFactory factory) return null;
            // BIGGERSIZEOK: when the shell lacks an exact 48px icon it may return a
            // larger one (e.g. 256) which WPF downscales crisply, instead of a blurry
            // 32px upscale.
            if (factory.GetImage(new SIZE { cx = sizePx, cy = sizePx }, SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, out var hbm) != 0 || hbm == IntPtr.Zero) return null;
            return hbm;
        }
        catch { return null; }
        finally { if (pidl != IntPtr.Zero) NativeMethods.CoTaskMemFree(pidl); }
    }
}

[ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItem
{
    [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppvOut);
    [PreserveSig] int GetParent([MarshalAs(UnmanagedType.Interface)] out IShellItem ppsi);
    [PreserveSig] int GetDisplayName(uint sigdnName, out IntPtr ppszName);
    [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
    [PreserveSig] int Compare(IntPtr psi, uint hint, out int piOrder);
}

[StructLayout(LayoutKind.Sequential)]
internal struct SIZE { public int cx; public int cy; }

/// <summary>The API Explorer/desktop use to render shell object icons at any size.</summary>
[ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItemImageFactory
{
    [PreserveSig] int GetImage(SIZE size, uint flags, out IntPtr phbm);
}
