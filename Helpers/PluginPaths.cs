using System;
using System.IO;
using System.Linq;
using DesktopZones.Services;

namespace DesktopZones.Helpers;

/// <summary>
/// 插件相关目录。全部经 <see cref="DataLocator.Root"/> —— 便携模式自动跟随安装目录。
/// </summary>
public static class PluginPaths
{
    public static string Root => Path.Combine(DataLocator.Root, "Plugins");
    public static string CatalogDir => Path.Combine(Root, "catalog");
    public static string ImagesDir => Path.Combine(CatalogDir, "images");
    public static string CacheDir => Path.Combine(Root, ".cache");
    public static string StateDir => Path.Combine(Root, ".state");
    public static string DataRoot => Path.Combine(Root, "data");

    public static string RegistryCachePath => Path.Combine(CatalogDir, "registry.json");
    public static string RegistryMetaPath => Path.Combine(CatalogDir, "registry.meta.json");
    public static string PendingDeletePath => Path.Combine(StateDir, "pending-delete.json");

    /// <summary>紧急禁用名单的本地副本（registry 顶层 blocked[] 的落地）。</summary>
    public static string BlockedListPath => Path.Combine(StateDir, "blocked.json");

    /// <summary>已安装语言包索引（声明式包，安装即生效）。</summary>
    public static string LanguageIndexPath => Path.Combine(StateDir, "languages.json");

    /// <summary>插件 id 只能是一段安全目录名（配置可能被改，不能直接拿去 Path.Combine）。</summary>
    public static bool IsSafePluginId(string? pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId) || pluginId.Length > 80) return false;
        if (pluginId is "." or "..") return false;
        if (pluginId.Any(ch => ch is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|')) return false;
        return !pluginId.Any(char.IsControl);
    }

    public static string PluginDir(string id) => Path.Combine(Root, id);
    public static string VersionDir(string id, string version) => Path.Combine(PluginDir(id), version);
    public static string InstallMetaPath(string id) => Path.Combine(PluginDir(id), "install.json");
    public static string PluginDataDir(string id) => Path.Combine(DataRoot, id);

    public static string LoadingMarker(string id) => Path.Combine(StateDir, id + ".loading");
    public static string CrashCountPath(string id) => Path.Combine(StateDir, id + ".crashcount");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(CatalogDir);
        Directory.CreateDirectory(ImagesDir);
        Directory.CreateDirectory(CacheDir);
        Directory.CreateDirectory(StateDir);
        Directory.CreateDirectory(DataRoot);
    }

    /// <summary>
    /// 包内相对路径安全校验：拒绝绝对路径、盘符、<c>..</c>、空段与控制字符。
    /// 用于 manifest 字段与 zip 条目（与 <see cref="Zip"/> 的规范化配合）。
    /// </summary>
    public static bool IsSafeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.Length > 240) return false;
        if (Path.IsPathRooted(path)) return false;
        if (path.IndexOf(':') >= 0) return false;
        if (path.Any(ch => ch < 32)) return false;
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;
        foreach (var part in parts)
        {
            if (part == "." || part == "..") return false;
            if (part.Trim().Length == 0) return false;
            // Windows 设备名（CON / NUL / COM1 …）无论扩展名都会打到设备层。
            var stem = part.Split('.')[0].TrimEnd(' ');
            if (IsReservedDeviceName(stem)) return false;
        }
        return true;
    }

    static bool IsReservedDeviceName(string stem)
    {
        var upper = stem.ToUpperInvariant();
        if (upper is "CON" or "PRN" or "AUX" or "NUL") return true;
        if (upper.Length == 4 && upper.StartsWith("COM", StringComparison.Ordinal) && char.IsDigit(upper[3])) return true;
        if (upper.Length == 4 && upper.StartsWith("LPT", StringComparison.Ordinal) && char.IsDigit(upper[3])) return true;
        return false;
    }

    /// <summary>把候选路径限制在 <paramref name="root"/> 之下，返回规范化绝对路径；越界返回 null。</summary>
    public static string? ResolveInside(string root, string relative)
    {
        if (!IsSafeRelativePath(relative)) return null;
        try
        {
            var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            var full = Path.GetFullPath(Path.Combine(rootFull, relative.Replace('\\', '/')));
            if (!full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return null;
            return full;
        }
        catch
        {
            return null;
        }
    }

    public static string ToPackagePath(string absolute, string root)
        => Path.GetRelativePath(root, absolute).Replace('\\', '/');
}
