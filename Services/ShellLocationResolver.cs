using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using DesktopZones.Helpers;
using DesktopZones.Models;

namespace DesktopZones.Services;

/// <summary>
/// Virtual shell objects (Recycle Bin, This PC, Control Panel, ...) are not file-system
/// paths, so OpenFileDialog can't pick them. They are stored as desktop-absolute
/// "::{GUID}" specs, rendered with SHGetFileInfo, and opened via "shell:::{GUID}".
/// </summary>
public static class ShellLocationResolver
{
    public sealed record Preset(string Guid, string NameKey);

    /// <summary>Common virtual locations offered by the import picker.</summary>
    public static readonly Preset[] Presets =
    {
        new("645FF040-5081-101B-9F08-00AA002F954E", "ShellItem.RecycleBin"),
        new("20D04FE0-3AEA-1069-A2D8-08002B30309D", "ShellItem.ThisPc"),
        new("26EE0668-A00A-44D7-9371-BEB064C98683", "ShellItem.ControlPanel"),
        new("F02C1A0D-BE21-4350-88B0-7367FC96EF3C", "ShellItem.Network"),
        new("679f85cb-0220-4080-b29b-5540cc05aab6", "ShellItem.QuickAccess"),
        new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641", "ShellItem.Desktop"),
        new("59031a47-3f72-44a7-89c5-5595fe6b30ee", "ShellItem.UserProfile"),
        new("FDD39AD0-238F-46AF-ADB4-6C85480369C7", "ShellItem.Documents"),
        new("374DE290-123F-4565-9164-39C4925E467B", "ShellItem.Downloads"),
        new("33E28130-4E1E-4676-835A-98395C3BC3BB", "ShellItem.Pictures"),
        new("4BD8D571-6D19-48D3-BE97-422220080E43", "ShellItem.Music"),
        new("18989B1D-99B5-455B-841C-AB7C74E4DDFC", "ShellItem.Videos"),
    };

    public static string SpecOf(string guid) => "::{" + guid + "}";

    public static bool IsShellLocation(string target) => ShellOle.IsShellSpec(target);

    /// <summary>
    /// Canonicalize user input (GUID / ::{GUID} / shell:::{GUID} / shell:RecycleBinFolder)
    /// to a "::{GUID}" spec; null when it doesn't resolve to a virtual shell object.
    /// </summary>
    public static string? Normalize(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var s = input.Trim();
        string candidate;
        if (s.StartsWith("shell:::", StringComparison.OrdinalIgnoreCase)) candidate = "::" + s.Substring(8);
        else if (s.StartsWith("shell::", StringComparison.OrdinalIgnoreCase)) candidate = "::" + s.Substring(7);
        else if (s.StartsWith("{", StringComparison.Ordinal) && s.EndsWith("}", StringComparison.Ordinal)) candidate = "::" + s;
        else if (Guid.TryParse(s, out _)) candidate = "::{" + s + "}";
        else candidate = s;

        IntPtr pidl = IntPtr.Zero;
        try
        {
            if (ShellOle.SHParseDisplayName(candidate, IntPtr.Zero, out pidl, 0, out _) != 0 || pidl == IntPtr.Zero)
            {
                // 已知文件夹(FOLDERID_*)的 "::{GUID}" 无法被 SHParseDisplayName 解析 —
                // 用已知文件夹路径确认并接受该 spec(导入时再转成真实文件夹)。
                return ResolveKnownFolderPath(candidate) != null ? candidate : null;
            }
            var iidItem = ShellOle.IID_IShellItem;
            if (ShellOle.SHCreateItemFromIDList(pidl, ref iidItem, out var item) != 0 || item == null) return null;
            if (item.GetDisplayName(ShellOle.SIGDN_DESKTOPABSOLUTEPARSING, out var p) != 0 || p == IntPtr.Zero) return null;
            string parsed;
            try { parsed = Marshal.PtrToStringUni(p) ?? string.Empty; }
            finally { Marshal.FreeCoTaskMem(p); }
            return ShellOle.IsShellSpec(parsed) ? parsed : null;
        }
        finally { if (pidl != IntPtr.Zero) NativeMethods.CoTaskMemFree(pidl); }
    }

    /// <summary>OS-localized display name ("回收站" etc.) for a "::{GUID}" spec, or null.</summary>
    public static string? GetDisplayName(string spec)
    {
        try
        {
            var info = new NativeMethods.SHFILEINFO();
            NativeMethods.SHGetFileInfo(spec, 0, ref info, (uint)Marshal.SizeOf<NativeMethods.SHFILEINFO>(), NativeMethods.SHGFI_DISPLAYNAME);
            return string.IsNullOrWhiteSpace(info.szDisplayName) ? null : info.szDisplayName;
        }
        catch { return null; }
    }

    /// <summary>
    /// Open a zone item: virtual shell objects open via "shell:::{GUID}",
    /// everything else via ShellExecute on its path.
    /// </summary>
    public static void Open(string target, ItemType type)
    {
        if (type == ItemType.ShellLocation && IsShellLocation(target))
        {
            // 已知文件夹(文档/图片/音乐/视频等)的 "::{GUID}" 无法被 shell 解析,
            // 直接打开真实文件夹路径;纯虚拟对象(回收站/此电脑等)走 shell: URI。
            var path = ResolveKnownFolderPath(target);
            if (path != null)
            {
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
                return;
            }
        }
        var fileName = type == ItemType.ShellLocation && IsShellLocation(target) ? "shell:" + target : target;
        Process.Start(new ProcessStartInfo { FileName = fileName, UseShellExecute = true });
    }

    /// <summary>
    /// Resolve a "::{GUID}" spec to a real folder path when the GUID is a known folder
    /// (FOLDERID_* — Documents, Pictures, Music, Videos, Downloads, Desktop, ...) that
    /// carries a file-system location; null for pure virtual objects (Recycle Bin,
    /// This PC, Control Panel, Quick Access, ...). Empirically the shell cannot
    /// SHParseDisplayName most FOLDERID specs ("::{FDD39AD0-...}" fails with
    /// E_INVALIDARG), so such items would render/behave as empty shells unless they
    /// are associated with the real folder path instead.
    /// </summary>
    public static string? ResolveKnownFolderPath(string spec)
    {
        if (!IsShellLocation(spec)) return null;
        var guidText = spec.StartsWith("::{", StringComparison.Ordinal)
            ? spec.Substring(3).TrimEnd('}') : null;
        if (guidText == null || !Guid.TryParse(guidText, out var guid)) return null;
        try
        {
            if (NativeMethods.SHGetKnownFolderPath(ref guid, 0, IntPtr.Zero, out var pPath) == 0 && pPath != IntPtr.Zero)
            {
                try
                {
                    var path = Marshal.PtrToStringUni(pPath) ?? string.Empty;
                    return Directory.Exists(path) ? path : null;
                }
                finally { NativeMethods.CoTaskMemFree(pPath); }
            }
        }
        catch { }
        return null;
    }
}
