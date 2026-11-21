using System;
using System.Collections.Generic;
using DeskOrder.PluginApi;
using DesktopZones.Helpers;
using DesktopZones.Services;

namespace DesktopZones;

/// <summary>
/// 插件工具命令的全局热键（API v3）。分区/面板/托盘菜单负责发现命令，
/// 这里负责「用户给某条命令分配的键」：启动时注册、命令集合变化后重算、WM_HOTKEY 分发。
/// </summary>
public partial class App
{
    /// <summary>
    /// 插件工具热键 id 基址。现有 id：便签 0x4000 起、面板 0x3FFF、全部显示/隐藏/最小化
    /// 0x3FFB..0x3FFD；这里从 0x5000 起，最多 128 条（0x5000..0x507F），都不冲突。
    /// </summary>
    private const int PLUGIN_TOOL_HOTKEY_BASE = 0x5000;
    private const int MaxPluginToolHotkeys = 128;

    /// <summary>热键 id → (插件 id, 命令 id)。只在注册成功且命令仍存在时登记。</summary>
    private readonly Dictionary<int, (string PluginId, string CommandId)> _pluginToolHotkeyMap = new();

    /// <summary>自测台专用：读取当前已登记的热键表（不暴露可变字典）。</summary>
    internal IReadOnlyDictionary<int, (string PluginId, string CommandId)> PluginToolHotkeyMapForLab
        => _pluginToolHotkeyMap;

    /// <summary>
    /// 按配置重注册全部插件工具热键。先无条件注销旧的一批（改键/插件卸载后不能留着旧键），
    /// 再逐条注册「已启用 + 命令仍存在」的分配；注册失败（被别的程序占用）只跳过这一条。
    /// </summary>
    public void RefreshPluginToolHotkeys()
    {
        if (_mainHwnd == IntPtr.Zero) return;

        foreach (var id in _pluginToolHotkeyMap.Keys)
            NativeMethods.UnregisterHotKey(_mainHwnd, id);
        _pluginToolHotkeyMap.Clear();

        var config = _configService?.Load();
        if (config == null) return;

        int nextId = PLUGIN_TOOL_HOTKEY_BASE;
        foreach (var hotkey in config.PluginToolHotkeys)
        {
            if (hotkey == null || !hotkey.Enabled || hotkey.IsEmpty) continue;
            if (nextId >= PLUGIN_TOOL_HOTKEY_BASE + MaxPluginToolHotkeys) break;
            // 命令没注册（插件没装/没启用/加载失败）：不占号，等插件回来后自动恢复。
            if (PluginToolService.Instance.Find(hotkey.PluginId, hotkey.CommandId) == null) continue;
            int id = nextId++;
            if (!NativeMethods.RegisterHotKey(_mainHwnd, id, hotkey.Modifiers, hotkey.Key))
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[DeskOrder] plugin tool hotkey register failed: {hotkey.PluginId}/{hotkey.CommandId} " +
                    $"(mods=0x{hotkey.Modifiers:X}, key=0x{hotkey.Key:X})");
                continue;
            }
            _pluginToolHotkeyMap[id] = (hotkey.PluginId, hotkey.CommandId);
        }
    }

    /// <summary>
    /// WM_HOTKEY 分发。命中插件工具热键返回 true（调用方据此把消息标记为已处理）。
    /// 命令执行是 fire-and-forget：失败由 <see cref="PluginToolService.InvokeAsync"/> 内部兜住。
    /// </summary>
    internal bool TryHandlePluginToolHotkey(int id)
    {
        if (!_pluginToolHotkeyMap.TryGetValue(id, out var target)) return false;
        var entry = PluginToolService.Instance.Find(target.PluginId, target.CommandId);
        if (entry != null)
            _ = PluginToolService.Instance.InvokeAsync(entry, PluginToolSurface.Hotkey);
        return true;
    }

    /// <summary>退出/重新初始化前注销全部插件工具热键（幂等）。</summary>
    void UnregisterPluginToolHotkeys()
    {
        if (_mainHwnd != IntPtr.Zero)
        {
            foreach (var id in _pluginToolHotkeyMap.Keys)
                NativeMethods.UnregisterHotKey(_mainHwnd, id);
        }
        _pluginToolHotkeyMap.Clear();
    }
}
