using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using DesktopZones.Models;

namespace DesktopZones.Helpers;

/// <summary>
/// Resolves .lnk shortcuts to their real targets AND their icon location. Imported
/// shortcuts are re-associated with the target path instead of the .lnk itself: icons
/// extracted from a .lnk carry the link-arrow overlay (redundant when the user explicitly
/// imported the file), and launching/opening-location should act on what the shortcut
/// points at. Many desktop shortcuts also set a custom high-resolution icon
/// (IconLocation, e.g. a 圆角 .ico) that the target executable does not contain — that
/// location is preserved too, so the zone icon looks exactly like the desktop's.
/// </summary>
public static class ShortcutResolver
{
    private static readonly Type? _wshShellType = GetWshShellType();

    private static Type? GetWshShellType()
    {
        try { return Type.GetTypeFromProgID("WScript.Shell"); }
        catch { return null; }
    }

    public static bool IsShortcut(string path) =>
        string.Equals(Path.GetExtension(path), ".lnk", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Resolve a .lnk to its final target, following chained shortcuts (depth ≤ 4).
    /// Returns null when the shortcut cannot be resolved.
    /// </summary>
    public static string? ResolveTarget(string lnkPath)
    {
        string current = lnkPath;
        for (int depth = 0; depth < 4; depth++)
        {
            string? next = ResolveOnce(current);
            if (next == null) return depth == 0 ? null : current;
            if (!IsShortcut(next)) return next;
            current = next;
        }
        return current;
    }

    /// <summary>
    /// Read the .lnk's WshShortcut.Arguments property (used to detect Microsoft Store /
    /// Steam pseudo-shortcuts), or null when unavailable.
    /// </summary>
    public static string? ResolveArguments(string lnkPath)
    {
        object? shell = null;
        object? sc = null;
        try
        {
            if (_wshShellType == null || !File.Exists(lnkPath)) return null;
            shell = Activator.CreateInstance(_wshShellType);
            if (shell == null) return null;
            sc = _wshShellType.InvokeMember("CreateShortcut",
                BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
            if (sc == null) return null;
            object? args = sc.GetType().InvokeMember("Arguments",
                BindingFlags.GetProperty, null, sc, null);
            return (args as string)?.Trim();
        }
        catch { return null; }
        finally
        {
            if (sc != null && Marshal.IsComObject(sc)) Marshal.FinalReleaseComObject(sc);
            if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }

    /// <summary>
    /// True when a .lnk is a Microsoft Store / Steam style pseudo-shortcut: the target is
    /// a shell activation string (explorer.exe shell:AppsFolder\…) or a protocol URI
    /// (steam://…), or the resolved target lives under the ACL-protected WindowsApps
    /// folder. For these the real icon file is buried deep and "open file location" does
    /// not work, so the item should keep the .lnk itself and read the icon directly from
    /// the desktop shortcut.
    /// </summary>
    static bool IsPseudoShortcut(string lnkPath, string resolved)
    {
        // Protocol URI targets (steam://, microsoft-store://, …) are not file-system paths.
        // Use "://" rather than Uri.TryCreate — the latter treats a Windows "C:\..." path
        // as a scheme named "c" and would misclassify every normal shortcut target.
        if (resolved.Contains("://", StringComparison.Ordinal))
            return true;

        // Buried / ACL-protected WindowsApps exe — the desktop .lnk is the safe icon source.
        if (resolved.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase))
            return true;

        string? args = ResolveArguments(lnkPath);
        if (string.IsNullOrWhiteSpace(args)) return false;
        return args.Contains("shell:AppsFolder", StringComparison.OrdinalIgnoreCase)
            || args.Contains("shell:appsfolder", StringComparison.OrdinalIgnoreCase)
            || args.Contains("-applaunch", StringComparison.OrdinalIgnoreCase)
            || args.Contains("steam://", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The shortcut's custom icon location ("file,index") when it points at a real file
    /// other than the shortcut's own target; null when the shortcut uses its target's
    /// default icon. Environment variables are expanded.
    /// </summary>
    public static string? ResolveIconLocation(string lnkPath)
    {
        object? shell = null;
        object? sc = null;
        try
        {
            if (_wshShellType == null || !File.Exists(lnkPath)) return null;
            shell = Activator.CreateInstance(_wshShellType);
            if (shell == null) return null;
            sc = _wshShellType.InvokeMember("CreateShortcut",
                BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
            if (sc == null) return null;
            object? loc = sc.GetType().InvokeMember("IconLocation",
                BindingFlags.GetProperty, null, sc, null);
            var s = (loc as string)?.Trim();
            if (string.IsNullOrWhiteSpace(s)) return null;
            int sep = s.LastIndexOf(',');
            if (sep <= 0 || sep >= s.Length - 1) return null;
            string file = s[..sep].Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(file)) return null;
            file = Environment.ExpandEnvironmentVariables(file);
            if (!File.Exists(file)) return null;
            return file + "," + s[(sep + 1)..].Trim();
        }
        catch { return null; }
        finally
        {
            if (sc != null && Marshal.IsComObject(sc)) Marshal.FinalReleaseComObject(sc);
            if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }

    /// <summary>
    /// For a shortcut item, re-associate the path to the shortcut's target:
    /// folder → <see cref="ItemType.Folder"/>, .exe → <see cref="ItemType.Application"/>,
    /// other files keep the Shortcut type (open-in-location semantics). The shortcut's
    /// custom icon location (when it differs from the target itself) is returned so the
    /// zone renders the exact same icon as the desktop. Non-shortcuts and shortcuts whose
    /// target no longer exists pass through unchanged.
    /// </summary>
    public static (string Target, ItemType Type, string? IconLocation) NormalizeItem(string path, ItemType type)
    {
        if (type != ItemType.Shortcut || !IsShortcut(path)) return (path, type, null);
        var resolved = ResolveTarget(path);
        if (resolved == null || (!File.Exists(resolved) && !Directory.Exists(resolved)))
            return (path, type, null);

        // Microsoft Store / Steam pseudo-shortcuts: keep the .lnk itself so the shell
        // reads the icon directly from the desktop shortcut instead of chasing a buried /
        // ACL-protected original icon file (and "open file location" wouldn't work anyway).
        if (IsPseudoShortcut(path, resolved))
            return (path, ItemType.Shortcut, null);

        string? iconLoc = ResolveIconLocation(path);
        if (iconLoc != null)
        {
            // A location pointing at the target itself is just the shortcut default.
            int sep = iconLoc.LastIndexOf(',');
            string iconFile = sep > 0 ? iconLoc[..sep] : iconLoc;
            if (string.Equals(iconFile, resolved, StringComparison.OrdinalIgnoreCase))
                iconLoc = null;
        }

        if (Directory.Exists(resolved)) return (resolved, ItemType.Folder, iconLoc);
        if (string.Equals(Path.GetExtension(resolved), ".exe", StringComparison.OrdinalIgnoreCase))
            return (resolved, ItemType.Application, iconLoc);
        return (resolved, ItemType.Shortcut, iconLoc);
    }

    private static string? ResolveOnce(string lnkPath)
    {
        object? shell = null;
        object? sc = null;
        try
        {
            if (_wshShellType == null || !File.Exists(lnkPath)) return null;
            shell = Activator.CreateInstance(_wshShellType);
            if (shell == null) return null;
            sc = _wshShellType.InvokeMember("CreateShortcut",
                BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
            if (sc == null) return null;
            object? target = sc.GetType().InvokeMember("TargetPath",
                BindingFlags.GetProperty, null, sc, null);
            var s = target as string;
            if (string.IsNullOrWhiteSpace(s)) return null;
            return Environment.ExpandEnvironmentVariables(s.Trim());
        }
        catch { return null; }
        finally
        {
            if (sc != null && Marshal.IsComObject(sc)) Marshal.FinalReleaseComObject(sc);
            if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }
}
