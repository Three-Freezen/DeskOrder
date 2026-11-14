using System;
using System.Collections.Generic;

namespace DesktopZones.Helpers;

/// <summary>
/// 插件版本比较。SemVer 允许 <c>1.0.0-beta</c> 这种预发布串，而
/// <see cref="Version.TryParse"/> 不认它——直接用它比较会让「装了 beta 收不到正式版」
/// 和「minAppVersion 写成 1.1.0-rc1 就静默跳过门禁」。
/// </summary>
public static class PluginVersion
{
    /// <summary>解析失败返回 null。支持可选的 <c>v</c> 前缀与 <c>+build</c> 元数据。</summary>
    public static Version? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var text = raw.Trim();
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];
        var plus = text.IndexOf('+');
        if (plus > 0) text = text[..plus];

        string? prerelease = null;
        var dash = text.IndexOf('-');
        if (dash > 0)
        {
            prerelease = text[(dash + 1)..];
            text = text[..dash];
        }

        // 补齐到 4 段：Version.TryParse 要求至少 2 段，而 "1" 这种在 manifest 里不会出现。
        var parts = text.Split('.');
        if (parts.Length is < 2 or > 4) return null;
        text = string.Join('.', parts);

        if (!Version.TryParse(text, out var version)) return null;
        // 用预发布标识塞进 Revision 之外的信息：改为在比较时单独处理，这里只记录。
        return version;
    }

    /// <summary>提取预发布标识（无则 null）。</summary>
    public static string? PrereleaseOf(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var text = raw.Trim();
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];
        var plus = text.IndexOf('+');
        if (plus > 0) text = text[..plus];
        var dash = text.IndexOf('-');
        return dash > 0 ? text[(dash + 1)..] : null;
    }

    /// <summary><paramref name="available"/> 是否比 <paramref name="installed"/> 新。</summary>
    public static bool IsNewer(string? installed, string? available)
    {
        var left = Parse(installed);
        var right = Parse(available);
        if (left == null || right == null)
        {
            // 解析不了时退回序数比较，至少不要因为预发布串就永远不提示更新。
            return !string.Equals(installed?.Trim(), available?.Trim(), StringComparison.OrdinalIgnoreCase)
                   && string.CompareOrdinal(available, installed) > 0;
        }
        if (right != left) return right > left;

        // 版本号相同：正式版 > 预发布版；两个预发布按序数比较。
        var leftPre = PrereleaseOf(installed);
        var rightPre = PrereleaseOf(available);
        if (leftPre == null && rightPre == null) return false;
        if (leftPre == null) return false;          // 已装正式版，不降级到预发布
        if (rightPre == null) return true;          // 已装预发布，正式版算更新
        return string.CompareOrdinal(rightPre, leftPre) > 0;
    }

    /// <summary>
    /// 宿主是否满足 <paramref name="minAppVersion"/>。解析不了按「不满足」处理：
    /// 门禁宁可挡住也不要静默放行。
    /// </summary>
    public static bool HostSatisfies(string? minAppVersion, Version hostVersion)
    {
        if (string.IsNullOrWhiteSpace(minAppVersion)) return true;
        var required = Parse(minAppVersion);
        return required != null && hostVersion >= required;
    }

    public static string Format(IEnumerable<string> versions) => string.Join(", ", versions);
}
