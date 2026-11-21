using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using DesktopZones.Models;

namespace DesktopZones.Helpers;

/// <summary>
/// <c>.dopkg</c>（zip）的安全校验与解压。
/// 这是卫生检查，不是安全边界 —— 真正的边界是用户是否同意安装。
/// </summary>
public static class PluginPackages
{
    public const long MaxPackageBytes = 50L * 1024 * 1024;
    public const long MaxExtractedBytes = 200L * 1024 * 1024;
    public const int MaxEntries = 512;
    public const long MaxSingleFileBytes = 100L * 1024 * 1024;
    public const int MaxCompressionRatio = 200;

    static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dll", ".png", ".jpg", ".jpeg", ".json", ".md", ".txt", ".ico"
    };

    public static bool IsAllowedFileName(string fileName)
    {
        if (string.Equals(fileName, "LICENSE", StringComparison.OrdinalIgnoreCase)) return true;
        var ext = Path.GetExtension(fileName);
        return AllowedExtensions.Contains(ext);
    }

    /// <summary>
    /// sha256 是否是规范的 64 位十六进制（大小写不敏感）。
    /// registry 是外部数据：缺字段、写个 "TODO"、截断成 8 位都出现过。安装前必须能拿它比对字节，
    /// 否则「跳过哈希校验直接安装」和「没写 sha256」没区别。
    /// </summary>
    public static bool IsValidSha256(string? value)
    {
        var text = value?.Trim();
        if (text is not { Length: 64 }) return false;
        foreach (var ch in text)
        {
            if (!char.IsAsciiHexDigit(ch)) return false;
        }
        return true;
    }

    public static string Sha256OfFile(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    public static string Sha256OfStream(Stream stream)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    /// <summary>校验 zip 条目（路径 / 扩展名 / 大小 / 条目数 / 压缩比）。返回错误列表。</summary>
    public static List<string> ValidateEntries(ZipArchive archive)
    {
        var errors = new List<string>();
        if (archive.Entries.Count > MaxEntries)
            errors.Add($"too many files ({archive.Entries.Count} > {MaxEntries})");

        long total = 0;
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.EndsWith('/'))
            {
                var dirPart = name.TrimEnd('/');
                if (dirPart.Length > 0 && !PluginPaths.IsSafeRelativePath(dirPart))
                    errors.Add($"unsafe path: {entry.FullName}");
                continue;
            }

            if (!PluginPaths.IsSafeRelativePath(name))
            {
                errors.Add($"unsafe path: {entry.FullName}");
                continue;
            }
            var leaf = Path.GetFileName(name);
            if (!IsAllowedFileName(leaf))
            {
                errors.Add($"file type not allowed: {entry.FullName}");
                continue;
            }
            if (entry.Length > MaxSingleFileBytes)
            {
                errors.Add($"file too large: {entry.FullName}");
                continue;
            }
            total += entry.Length;
            if (entry.CompressedLength > 0 && entry.Length > 0
                && entry.Length / Math.Max(1, entry.CompressedLength) > MaxCompressionRatio)
            {
                errors.Add($"suspicious compression ratio: {entry.FullName}");
            }
        }

        if (total > MaxExtractedBytes)
            errors.Add($"package expands to more than {MaxExtractedBytes / (1024 * 1024)} MB");
        if (archive.Entries.Count > 0
            && !archive.Entries.Any(e => string.Equals(e.FullName.Replace('\\', '/'), "plugin.json",
                StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add("plugin.json is missing at the package root");
        }
        return errors;
    }

    /// <summary>
    /// 解压到 <paramref name="destDir"/>（必须不存在或为空）。失败返回错误文案且清理半成品。
    /// </summary>
    public static bool Extract(ZipArchive archive, string destDir, out string? error,
        out long extractedBytes)
    {
        extractedBytes = 0;
        error = null;
        var errors = ValidateEntries(archive);
        if (errors.Count > 0)
        {
            error = string.Join("; ", errors.Take(3));
            return false;
        }

        var destFull = Path.GetFullPath(destDir).TrimEnd(Path.DirectorySeparatorChar);
        Directory.CreateDirectory(destFull);
        try
        {
            foreach (var entry in archive.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                if (name.EndsWith('/')) continue;

                var target = PluginPaths.ResolveInside(destFull, name);
                if (target == null)
                {
                    error = $"unsafe path: {entry.FullName}";
                    return false;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using (var input = entry.Open())
                using (var output = File.Create(target))
                {
                    input.CopyTo(output);
                    extractedBytes += output.Length;
                    if (extractedBytes > MaxExtractedBytes)
                    {
                        error = "package expands beyond the size limit";
                        return false;
                    }
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            if (error != null)
            {
                try { Directory.Delete(destFull, recursive: true); } catch { }
            }
        }
    }

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static PluginManifest? ReadManifest(string dir, out string? error)
    {
        error = null;
        var path = Path.Combine(dir, "plugin.json");
        if (!File.Exists(path))
        {
            error = "plugin.json not found";
            return null;
        }
        try
        {
            var json = File.ReadAllText(path);
            if (json.Length > 1024 * 1024)
            {
                error = "plugin.json is larger than 1 MB";
                return null;
            }
            var manifest = JsonSerializer.Deserialize<PluginManifest>(json, JsonOptions);
            if (manifest == null)
            {
                error = "plugin.json is empty";
                return null;
            }
            return manifest;
        }
        catch (JsonException ex)
        {
            error = "plugin.json is not valid JSON: " + ex.Message;
            return null;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    public static void WriteInstallInfo(string pluginDir, PluginInstallInfo info)
    {
        Directory.CreateDirectory(pluginDir);
        var path = Path.Combine(pluginDir, "install.json");
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(info, JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }

    public static PluginInstallInfo? ReadInstallInfo(string pluginDir)
    {
        var path = Path.Combine(pluginDir, "install.json");
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<PluginInstallInfo>(File.ReadAllText(path), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>把目录打包成 <c>.dopkg</c>（示例插件 / 自测台使用）。</summary>
    public static void CreatePackage(string sourceDir, string packagePath)
    {
        var full = Path.GetFullPath(sourceDir).TrimEnd(Path.DirectorySeparatorChar);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(packagePath))!);
        if (File.Exists(packagePath)) File.Delete(packagePath);
        using var zip = ZipFile.Open(packagePath, ZipArchiveMode.Create);
        foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(full, file).Replace('\\', '/');
            zip.CreateEntryFromFile(file, rel, CompressionLevel.Optimal);
        }
    }
}

/// <summary>安装元数据（<c>Plugins\&lt;id&gt;\install.json</c>）。</summary>
public sealed class PluginInstallInfo
{
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    public string Kind { get; set; } = "widget";
    public string Source { get; set; } = "market";
    /// <summary>从 GitHub 直装时的 <c>owner/repo</c>（其它来源为空）。</summary>
    public string Repo { get; set; } = "";
    public bool Official { get; set; }
    public string Sha256 { get; set; } = "";
    public DateTime InstalledAtUtc { get; set; } = DateTime.UtcNow;
    [JsonPropertyName("manifest")] public string ManifestFile { get; set; } = "plugin.json";
}
