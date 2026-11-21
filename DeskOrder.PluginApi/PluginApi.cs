using System;
using System.Windows;
using System.Windows.Media;

namespace DeskOrder.PluginApi;

/// <summary>
/// 插件 API 版本。只在破坏性变更时 +1；宿主拒绝 <c>apiVersion &gt; Current</c> 的插件。
/// </summary>
public static class PluginApiVersion
{
    /// <summary>
    /// 当前 API 版本。v5 只增不改：v1–v4 的全部成员保持原样，新增
    /// <see cref="IPluginHost.Settings"/>、<see cref="IPluginSettings"/> 与
    /// 工具命令 / 数据源的本地化显示名（<see cref="PluginLocalizedText"/>）。
    /// </summary>
    public const int Current = 5;
}

/// <summary>
/// 代码类插件（widget / zonebar / tool / integration）的唯一入口。
/// <see cref="Initialize"/> 在 UI 线程调用；抛异常 = 加载失败，宿主会记录并禁用。
/// </summary>
public interface IDeskOrderPlugin
{
    /// <summary>注册组件/工具/数据源。不要在这里创建 Window。</summary>
    void Initialize(IPluginHost host);

    /// <summary>退出或禁用时调用（已回到 UI 线程）。不要做 UI，尽快返回。</summary>
    void Shutdown();
}

/// <summary>宿主提供给单个插件的门面。</summary>
public interface IPluginHost
{
    /// <summary>当前插件 id（manifest 里的 <c>id</c>）。</summary>
    string PluginId { get; }

    /// <summary>宿主版本（如 1.0.27）。</summary>
    Version HostVersion { get; }

    /// <summary>宿主支持的 API 版本（当前 = 5）。</summary>
    int ApiVersion { get; }

    /// <summary>插件级数据目录（跨实例共享）。</summary>
    IPluginStorage Storage { get; }

    IPluginUi Ui { get; }

    IPluginWidgets Widgets { get; }

    /// <summary>分区坞注册入口（API v2）。</summary>
    IPluginZoneBays ZoneBays { get; }

    /// <summary>工具命令注册入口（API v3）。</summary>
    IPluginTools Tools { get; }

    /// <summary>数据源注册入口（API v4）。</summary>
    IPluginIntegrations Integrations { get; }

    /// <summary>插件设置入口（API v5）：注册后由宿主渲染设置页。</summary>
    IPluginSettings Settings { get; }

    IPluginLog Log { get; }
}

/// <summary>注册桌面组件的入口。</summary>
public interface IPluginWidgets
{
    /// <summary>
    /// 注册一种桌面组件。同一个插件的 Id 必须唯一；最多 8 种。
    /// 只能在 <see cref="IDeskOrderPlugin.Initialize"/> 里调用。
    /// </summary>
    void Register(PluginWidgetDescriptor descriptor);
}

/// <summary>一种可放置到桌面的插件组件。</summary>
public sealed class PluginWidgetDescriptor
{
    /// <summary>插件内唯一（建议小写 + 连字符）。</summary>
    public required string Id { get; init; }

    /// <summary>显示名（会在卡片/管理列表里出现）。</summary>
    public required string DisplayName { get; init; }

    public Size DefaultSize { get; init; } = new(320, 180);

    public Size MinSize { get; init; } = new(160, 90);

    public Size MaxSize { get; init; } = new(1200, 900);

    /// <summary>可选：包内相对路径的 16×16 / 32×32 图标。</summary>
    public string? IconPath { get; init; }

    /// <summary>
    /// 创建内容根元素。每次新建实例调用一次；宿主负责窗口、拖动、圆角与持久化。
    /// 抛异常时该组件显示错误占位，不影响其它组件。
    /// </summary>
    public required Func<PluginWidgetContext, FrameworkElement> CreateContent { get; init; }

    /// <summary>组件被删除时回调（可清理插件侧数据）。</summary>
    public Action<PluginWidget>? OnDelete { get; init; }
}

/// <summary>
/// 单个组件实例的上下文。所有属性在创建时固定；主题/磁贴切换通过事件通知。
/// </summary>
public sealed class PluginWidgetContext
{
    /// <summary>该实例的键值存储（持久化到 <see cref="PluginWidget.SettingsJson"/>）。</summary>
    public required IPluginStorage Settings { get; init; }

    /// <summary>
    /// 磁贴模式。属性面板里可实时切换：宿主会更新这个值并触发
    /// <see cref="TileModeChanged"/>，插件应重排内容而不是假设它永不变化。
    /// </summary>
    public bool IsTileMode { get; internal set; }

    /// <summary>锁定状态。宿主在属性面板/右键菜单切换时更新并触发 <see cref="LockedChanged"/>。</summary>
    public bool IsLocked { get; internal set; }

    /// <summary>主文字色。</summary>
    public required Brush Foreground { get; init; }

    /// <summary>强调色（跟随系统强调色 / 主题）。</summary>
    public required Brush Accent { get; init; }

    /// <summary>DPI / 尺寸缩放建议值（1.0 = 100%）。</summary>
    public required double Scale { get; init; }

    /// <summary>宿主词条（与界面同语言）。</summary>
    public required ILocalizer Loc { get; init; }

    /// <summary>主题（浅色/深色/强调色）变化。</summary>
    public event Action? ThemeChanged;

    /// <summary>磁贴模式切换。</summary>
    public event Action? TileModeChanged;

    /// <summary>锁定状态切换。</summary>
    public event Action? LockedChanged;

    /// <summary>请求宿主保存本实例设置（宿主已做 300ms 防抖，可频繁调用）。</summary>
    public void SaveSettings() => SaveSettingsCallback?.Invoke();

    /// <summary>发一条托盘通知（会带插件名）。</summary>
    public void Notify(string title, string body) => NotifyCallback?.Invoke(title, body);

    internal Action? SaveSettingsCallback { get; init; }
    internal Action<string, string>? NotifyCallback { get; init; }

    internal void RaiseThemeChanged() => ThemeChanged?.Invoke();
    internal void RaiseTileModeChanged() => TileModeChanged?.Invoke();
    internal void RaiseLockedChanged() => LockedChanged?.Invoke();
}

/// <summary>插件自己的键值存储。值为字符串，插件自行序列化。</summary>
public interface IPluginStorage
{
    /// <summary>读字符串；不存在返回空串。</summary>
    string Get(string key);

    void Set(string key, string value);

    /// <summary>读字符串；不存在返回 <paramref name="fallback"/>。</summary>
    string? Get(string key, string? fallback);

    /// <summary>立即落盘（宿主也会在合适时机自动 Flush）。</summary>
    void Flush();
}

/// <summary>宿主主题资源。</summary>
public interface IPluginUi
{
    /// <summary>取主题画刷：<c>Brush.Text.Primary</c> / <c>Brush.Text.Secondary</c> /
    /// <c>Brush.Text.Tertiary</c> / <c>Brush.Accent</c> / <c>Brush.Bg.Surface</c> /
    /// <c>Brush.Border.Subtle</c>。未知 key 返回主文字色。</summary>
    Brush GetBrush(string key);

    FontFamily FontFamily { get; }

    void Notify(string title, string body);
}

/// <summary>写宿主日志（前缀 <c>[Plugin] &lt;id&gt;</c>）。</summary>
public interface IPluginLog
{
    void Info(string message);

    void Warn(string message);

    void Error(string message, Exception? ex = null);
}

/// <summary>插件内取词条。key 与宿主 i18n 相同；缺词条返回 key 本身。</summary>
public interface ILocalizer
{
    string this[string key] { get; }
}
