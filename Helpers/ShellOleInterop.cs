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

    // SHIL — system image list sizes
    public const int SHIL_JUMBO = 0x4;              // 256px — the list Explorer/desktop draw from
    private const int ILD_TRANSPARENT = 0x00000001;

    private static readonly Guid IID_IImageList = new("46EB5926-582E-4017-9FDF-E8998DAA0950");

    [DllImport("shell32.dll", PreserveSig = true)]
    private static extern int SHGetImageList(int iImageList, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IImageList ppv);

    // SIIGBF — IShellItemImageFactory.GetImage flags
    public const uint SIIGBF_BIGGERSIZEOK = 0x00000001;
    public const uint SIIGBF_ICONONLY = 0x00000004;
    public const uint SIIGBF_SCALEUP = 0x00000100;

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
            // BIGGERSIZEOK: when the shell lacks an exact 256px icon it may return a
            // larger one instead of a blurry 32px upscale.
            // SCALEUP: without it, icons that only exist at small sizes (old apps with
            // 16/32px icons) come back at native size CENTERED inside the 256×256 bitmap
            // — the icon then renders tiny with a transparent border. SCALEUP stretches
            // them to fill the requested size, matching how the desktop draws them.
            if (factory.GetImage(new SIZE { cx = sizePx, cy = sizePx }, SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK | SIIGBF_SCALEUP, out var hbm) != 0 || hbm == IntPtr.Zero) return null;
            return hbm;
        }
        catch { return null; }
        finally { if (pidl != IntPtr.Zero) NativeMethods.CoTaskMemFree(pidl); }
    }

    /// <summary>
    /// 256px HICON straight from the shell's jumbo system image list (SHIL_JUMBO) — the
    /// exact list Explorer and the desktop render icons from. The shell pre-renders every
    /// icon into this list at full canvas size (scaling up icons that only exist at 32px),
    /// so the result never letterboxes small icons into a 256px frame. The caller owns the
    /// returned HICON and must DestroyIcon it. Null when unavailable.
    /// Works for file paths, folders and "::{GUID}" shell specs.
    /// </summary>
    public static IntPtr? GetJumboIcon(string path)
    {
        try
        {
            var info = new NativeMethods.SHFILEINFO();
            if (NativeMethods.SHGetFileInfo(path, 0, ref info,
                    (uint)Marshal.SizeOf<NativeMethods.SHFILEINFO>(),
                    NativeMethods.SHGFI_SYSICONINDEX) == IntPtr.Zero)
                return null;
            var iid = IID_IImageList;
            if (SHGetImageList(SHIL_JUMBO, ref iid, out var list) != 0 || list == null)
                return null;
            if (list.GetIcon(info.iIcon, ILD_TRANSPARENT, out var hIcon) != 0 || hIcon == IntPtr.Zero)
                return null;
            return hIcon;
        }
        catch { return null; }
    }
}

/// <summary>
/// Shell system image list (IImageList). Only the vtable slots up to GetIcon are
/// declared — the service only ever calls GetIcon. Slots 0–7 keep the exact IImageList
/// vtable order; Draw's parameter is relaxed to IntPtr because it is never invoked.
/// </summary>
[ComImport, Guid("46EB5926-582E-4017-9FDF-E8998DAA0950"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IImageList
{
    [PreserveSig] int Add(IntPtr hbmImage, IntPtr hbmMask, out int pi);
    [PreserveSig] int ReplaceIcon(int i, IntPtr hicon, out int pi);
    [PreserveSig] int SetOverlayImage(int iImage, int iOverlay);
    [PreserveSig] int Replace(int i, IntPtr hbmImage, IntPtr hbmMask);
    [PreserveSig] int AddMasked(IntPtr hbmImage, int crMask, out int pi);
    [PreserveSig] int Draw(IntPtr pimldp); // never called — parameter type relaxed
    [PreserveSig] int Remove(int i);
    [PreserveSig] int GetIcon(int i, int flags, out IntPtr picon);
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
