using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DeskOrder.PluginApi;
using DesktopZones.Helpers;
using DesktopZones.Models;

namespace DesktopZones.Services;

/// <summary>一条已发布的工具命令：命令本身 + 来源插件（菜单显示名 / 日志 / 数据目录都按插件走）。</summary>
public sealed class PluginToolEntry
{
    public required PluginRecord Record { get; init; }
    public required PluginManifest Manifest { get; init; }
    public required PluginToolCommand Command { get; init; }
    public required string PluginId { get; init; }
    /// <summary>manifest.Name 的当前语言写法；为空时退化为插件 id（作者没填时也不能显示空白项）。</summary>
    public required string PluginDisplayName { get; init; }
    public required IPluginLog Log { get; init; }

    /// <summary>
    /// 命令显示名（按宿主当前语言解析）：API v5 的 <see cref="PluginToolCommand.LocalizedDisplayName"/>
    /// 优先，为 null / 解析为空时回退 <see cref="PluginToolCommand.DisplayName"/>（旧插件零改动）。
    /// 菜单、插件工具页、执行失败提示统一用它，语言切换后重新取值即可。
    /// </summary>
    public string DisplayName
    {
        get
        {
            var localized = Command.LocalizedDisplayName?.Get(LocalizationService.Instance.CurrentLanguage);
            return string.IsNullOrWhiteSpace(localized) ? Command.DisplayName : localized!;
        }
    }
}

/// <summary>
/// 工具插件（API v3）的命令中心：收集命令 → 构建三处菜单 → 执行 → 给热键分配/分发查表。
/// 单例，UI 线程使用。
/// </summary>
public sealed class PluginToolService
{
    public static PluginToolService Instance { get; } = new();

    /// <summary>子菜单标记。菜单对象会被 Opened 反复触发，靠它先清旧项再插，避免叠出多层。</summary>
    internal const string MenuTag = "PluginToolMenu";
    internal const string MenuSeparatorTag = "PluginToolMenuSep";

    readonly List<PluginToolEntry> _entries = new();

    /// <summary>当前所有已加载插件的命令（插件加载顺序 + 插件内注册顺序）。</summary>
    public IReadOnlyList<PluginToolEntry> Commands => _entries;

    /// <summary>命令集合变化（插件加载 / 卸载 / 重新发布）。热键表靠它重算。</summary>
    public event Action? CommandsChanged;

    PluginToolService() { }

    // ── 发布 / 解除 ──

    /// <summary>
    /// 插件 Initialize 成功后由 <see cref="PluginService"/> 调用。重复发布同一插件先移除旧命令；
    /// 命令数为 0 也要调——加载重试/重载路径不能留着上一轮的命令。
    /// </summary>
    internal void Publish(PluginRecord record, PluginManifest manifest, IReadOnlyList<PluginToolCommand> commands)
    {
        _entries.RemoveAll(e => string.Equals(e.PluginId, record.Id, StringComparison.OrdinalIgnoreCase));
        var display = manifest.Name?.Get(LocalizationService.Instance.CurrentLanguage);
        if (string.IsNullOrWhiteSpace(display)) display = manifest.Id;
        var log = new PluginLogger(record.Id);
        foreach (var command in commands)
        {
            _entries.Add(new PluginToolEntry
            {
                Record = record,
                Manifest = manifest,
                Command = command,
                PluginId = record.Id,
                PluginDisplayName = display,
                Log = log
            });
        }
        CommandsChanged?.Invoke();
    }

    /// <summary>插件卸载/被禁用时调用：菜单与热键页里都不应再出现它的命令。</summary>
    internal void RemovePlugin(string pluginId)
    {
        if (_entries.RemoveAll(e => string.Equals(e.PluginId, pluginId, StringComparison.OrdinalIgnoreCase)) > 0)
            CommandsChanged?.Invoke();
    }

    internal void Clear()
    {
        if (_entries.Count == 0) return;
        _entries.Clear();
        CommandsChanged?.Invoke();
    }

    // ── 查询 ──

    public PluginToolEntry? Find(string pluginId, string commandId)
        => _entries.FirstOrDefault(e =>
            string.Equals(e.PluginId, pluginId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(e.Command.Id, commandId, StringComparison.Ordinal));

    public bool HasCommands(PluginToolSurface surface)
        => _entries.Any(e => (e.Command.Menus & surface) != 0);

    // ── 执行 ──

    /// <summary>
    /// 执行一条命令。宿主保证在 UI 线程调用（必要时切回 Dispatcher），
    /// 内部 try/catch：失败只记日志 + 托盘提示，返回值告诉调用方是否成功。
    /// </summary>
    public Task<bool> InvokeAsync(PluginToolEntry entry, PluginToolSurface source)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
            return dispatcher.InvokeAsync(() => InvokeCoreAsync(entry, source)).Task.Unwrap();
        return InvokeCoreAsync(entry, source);
    }

    async Task<bool> InvokeCoreAsync(PluginToolEntry entry, PluginToolSurface source)
    {
        // 每次调用重新构造存储：插件可能在两次调用之间被重装，缓存会读到旧目录。
        // 调用前 / 后都 Flush：命令里写的设置一定落盘，插件自己忘了 Flush 也不会丢。
        JsonPluginStorage? storage = null;
        try
        {
            storage = PluginStorage.Create(entry.PluginId);
            storage.Flush();
            var context = new PluginToolContext
            {
                PluginId = entry.PluginId,
                CommandId = entry.Command.Id,
                Source = source,
                Settings = storage,
                Ui = new PluginUiFacade(NotifyWithPluginName(entry)),
                Log = entry.Log,
                NotifyCallback = NotifyWithPluginName(entry)
            };
            await entry.Command.Execute(context);
            return true;
        }
        catch (Exception ex)
        {
            try { entry.Log.Error($"tool command '{entry.Command.Id}' threw", ex); } catch { }
            var loc = LocalizationService.Instance;
            try
            {
                PluginService.Instance.Notify(loc["PluginTool.Menu"],
                    loc.Get("PluginTool.RunFailed", entry.DisplayName));
            }
            catch { }
            return false;
        }
        finally
        {
            try { storage?.Flush(); } catch { }
        }
    }

    /// <summary>托盘通知统一带插件名前缀（与桌面组件的通知保持一致）。</summary>
    static Action<string, string> NotifyWithPluginName(PluginToolEntry entry)
        => (title, body) => PluginService.Instance.Notify($"{entry.PluginDisplayName} · {title}", body);

    // ── 菜单 ──

    /// <summary>构建「插件工具」子菜单；该入口没有任何命令时返回 null（调用方连分割线都不要加）。</summary>
    public MenuItem? BuildMenu(PluginToolSurface surface)
    {
        var entries = _entries.Where(e => (e.Command.Menus & surface) != 0).ToList();
        if (entries.Count == 0) return null;
        var menu = new MenuItem
        {
            Header = LocalizationService.Instance["PluginTool.Menu"],
            // 菜单复用与去重全靠这个标记（见 Attach）。
            Tag = MenuTag
        };
        foreach (var entry in entries)
        {
            var item = new MenuItem { Header = entry.PluginDisplayName + " · " + entry.DisplayName };
            if (!string.IsNullOrWhiteSpace(entry.Command.Description))
                item.ToolTip = entry.Command.Description;
            var captured = entry;
            // 菜单点击在 UI 线程；执行失败由 InvokeAsync 内部兜住，绝不把异常抛回 WPF。
            item.Click += (_, _) => { _ = InvokeAsync(captured, surface); };
            menu.Items.Add(item);
        }
        return menu;
    }

    /// <summary>
    /// 把「插件工具」子菜单挂到 <paramref name="menu"/> 上。菜单对象会被 Opened 反复触发，
    /// 所以每次都先清掉上一次插入的项再插（不会越开越多）。
    /// <paramref name="insertIndex"/> 按「调用方当前看到的 Items 下标」解释，默认追加到末尾。
    /// </summary>
    public void Attach(ContextMenu menu, PluginToolSurface surface, int? insertIndex = null)
    {
        if (menu == null) return;

        int requested = insertIndex ?? menu.Items.Count;
        int removedBefore = 0;
        for (int i = menu.Items.Count - 1; i >= 0; i--)
        {
            if (menu.Items[i] is not FrameworkElement element) continue;
            if (!IsOwnMenuItem(element)) continue;
            if (i < requested) removedBefore++;
            menu.Items.RemoveAt(i);
        }

        var built = BuildMenu(surface);
        if (built == null) return;
        int index = Math.Clamp(requested - removedBefore, 0, menu.Items.Count);
        menu.Items.Insert(index, new Separator { Tag = MenuSeparatorTag });
        menu.Items.Insert(index + 1, built);
    }

    static bool IsOwnMenuItem(FrameworkElement element)
        => element.Tag is string tag
           && (string.Equals(tag, MenuTag, StringComparison.Ordinal)
               || string.Equals(tag, MenuSeparatorTag, StringComparison.Ordinal));
}
