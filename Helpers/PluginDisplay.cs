using System.Collections.Generic;
using System.Linq;
using DeskOrder.PluginApi;
using DesktopZones.Models;
using DesktopZones.Services;

namespace DesktopZones.Helpers;

/// <summary>
/// 插件「安装来源」与「权限名」的展示映射。
///
/// 这三处（市场页、详情弹窗、更新弹窗）都要把 <c>network</c> 之类的标识翻成人类可读文本，
/// 之前各写一份 switch，加一种权限就要改三个地方、漏一个就显示原始标识。
/// </summary>
public static class PluginDisplay
{
    static LocalizationService Loc => LocalizationService.Instance;

    /// <summary>market / local / dev → 本地化来源名。</summary>
    public static string DescribeSource(string? source) => source?.Trim().ToLowerInvariant() switch
    {
        "local" => Loc["Plugin.LocalSource"],
        "dev" => Loc["Plugin.Dev.Badge"],
        _ => Loc["Plugin.MarketSource"]
    };

    /// <summary>权限标识 → 本地化名称；未知标识原样返回（便于发现新权限漏翻译）。</summary>
    public static string DescribePermission(string permission) => permission switch
    {
        "network" => Loc["Plugin.Permission.Network"],
        "storage" => Loc["Plugin.Permission.Storage"],
        "notifications" => Loc["Plugin.Permission.Notifications"],
        "hotkey" => Loc["Plugin.Permission.Hotkey"],
        "files" => Loc["Plugin.Permission.Files"],
        "zones.read" => Loc["Plugin.Permission.ZonesRead"],
        "zones.control" => Loc["Plugin.Permission.ZonesCtrl"],
        _ => permission
    };

    /// <summary>一组权限 → 「A · B · C」；空集合由调用方决定文案（「不申请任何权限」）。</summary>
    public static string DescribePermissions(IEnumerable<string>? permissions)
        => permissions == null || !permissions.Any()
            ? ""
            : string.Join(" · ", permissions.Select(DescribePermission));

    /// <summary>
    /// 这个条目能不能在当前宿主上安装 / 更新；能则返回 null，否则返回本地化原因。
    ///
    /// 两条门禁都在「下载之前」判掉，避免用户点完安装、等完下载才看到一句英文报错：
    /// <list type="bullet">
    /// <item><c>apiVersion</c> 高于宿主支持的 <see cref="PluginApiVersion.Current"/>：插件会调用
    /// 宿主还没有的接口，装上也会在加载时抛 MissingMethod。apiVersion=0 是声明式包（不加载 DLL），
    /// 不受这条约束。</item>
    /// <item><c>minAppVersion</c> 高于当前宿主版本：manifest 校验本来就拒绝安装，提前在卡片上置灰。</item>
    /// </list>
    /// 卡片、详情弹窗、市场安装入口共用这一个判定，避免三处规则各写一份后逐渐漂移。
    /// </summary>
    internal static string? IncompatibilityReason(PluginCatalogEntry entry)
    {
        if (entry.ApiVersion > PluginApiVersion.Current && entry.ApiVersion > 0)
            return Loc.Get("Plugin.Requires.Api", entry.ApiVersion);
        if (!string.IsNullOrWhiteSpace(entry.MinAppVersion)
            && !PluginVersion.HostSatisfies(entry.MinAppVersion, PluginInstaller.HostVersion))
            return Loc.Get("Plugin.Update.Requires", entry.MinAppVersion);
        return null;
    }
}
