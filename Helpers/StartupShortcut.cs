using System;
using System.IO;

namespace DesktopZones.Helpers;

/// <summary>
/// ponytail 2026-08-28: 开机自启动 = 启动文件夹里的 DeskOrder.lnk（WScript.Shell 创建，
/// 指向当前进程 exe）。此前只在设置页保存时写入——外力（升级卸载/清理工具/杀软）删掉
/// lnk 后配置仍显示「已开启」，自启动静默失效（本机实测 lnk 消失但 StartWithWindows=true）。
/// 现在 App 启动时 <see cref="Sync"/> 自愈：配置开 → lnk 缺失或指向旧路径就重建；
/// 配置关 → 清除。设置页/管理视图的开关也统一走这里，消除三份 WScript 重复代码。
/// </summary>
public static class StartupShortcut
{
    public static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup),
        "DeskOrder.lnk");

    /// <summary>lnk 存在且目标就是当前进程 exe（路径不区分大小写）。</summary>
    public static bool IsUpToDate()
    {
        try
        {
            if (!File.Exists(ShortcutPath)) return false;
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath)) return false;
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return false;
            dynamic? shell = Activator.CreateInstance(shellType);
            if (shell == null) return false;
            dynamic? lnk = shell.CreateShortcut(ShortcutPath);
            string? target = lnk?.TargetPath;
            return string.Equals(target, exePath, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>写入/刷新 lnk（指向当前 exe），写完回读校验（AV/权限可能让 Save 不抛
    /// 但文件没落盘）。成功返回 null，失败返回错误信息（供 UI toast）。</summary>
    public static string? Create()
    {
        try
        {
            var exePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("无法获取当前进程路径 (Environment.ProcessPath 为 null)");
            var shellType = Type.GetTypeFromProgID("WScript.Shell")
                ?? throw new InvalidOperationException("WScript.Shell 不可用 — 可能是企业策略禁用了 WSH");
            dynamic shell = Activator.CreateInstance(shellType)
                ?? throw new InvalidOperationException("无法创建 WScript.Shell 实例");
            dynamic shortcut = shell.CreateShortcut(ShortcutPath);
            shortcut.TargetPath = exePath;
            shortcut.WorkingDirectory = Path.GetDirectoryName(exePath)!;
            shortcut.Description = "DeskOrder";
            shortcut.Save();
            if (!File.Exists(ShortcutPath))
                throw new InvalidOperationException("快捷方式写入后未在磁盘上找到");
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    public static void Remove()
    {
        try { if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath); } catch { }
    }

    /// <summary>自愈同步：enabled=true 时确保 lnk 存在且指向当前 exe（已最新则不动）；
    /// false 时清除。返回 null = 成功/无需动作，否则为错误信息。</summary>
    public static string? Sync(bool enabled)
    {
        if (!enabled) { Remove(); return null; }
        if (IsUpToDate()) return null;
        return Create();
    }
}
