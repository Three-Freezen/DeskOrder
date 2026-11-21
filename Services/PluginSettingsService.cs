using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using DeskOrder.PluginApi;
using DesktopZones.Helpers;
using DesktopZones.Models;

namespace DesktopZones.Services;

/// <summary>
/// 一份已发布的插件设置：schema + 来源插件 + 插件级存储。
/// 显示名都按「当前语言」动态解析 —— 语言切换后设置窗口重开即是新语言。
/// </summary>
public sealed class PluginSettingsEntry
{
    public required PluginRecord Record { get; init; }
    public required PluginManifest Manifest { get; init; }
    public required PluginSettingsSchema Schema { get; init; }
    public required string PluginId { get; init; }
    /// <summary>发布时按当时语言算的插件名；语言切换后由 <see cref="DisplayPluginName"/> 重新解析。</summary>
    public required string PluginDisplayName { get; init; }

    /// <summary>插件级存储（与插件拿到的 <c>IPluginHost.Storage</c> 是同一个实例，
    /// 设置页保存后插件在本次进程里立刻能读到新值）。宿主实现细节，插件作者不用碰。</summary>
    public required IPluginStorage Storage { get; init; }

    /// <summary>设置块标题（按当前语言解析）。</summary>
    public string DisplayTitle => Schema.Title.Get(LocalizationService.Instance.CurrentLanguage);

    /// <summary>插件显示名（按当前语言解析 manifest.Name，回退发布时快照 / 插件 id）。</summary>
    public string DisplayPluginName
    {
        get
        {
            var name = Manifest.Name?.Get(LocalizationService.Instance.CurrentLanguage);
            return string.IsNullOrWhiteSpace(name) ? PluginDisplayName : name!;
        }
    }

    /// <summary>本插件提供的字段数（设置窗口 / 自测台用）。</summary>
    public int FieldCount => Schema.Fields.Count;
}

/// <summary>
/// 插件设置的宿主中心（API v5）：收集插件注册的 schema → 给设置窗口提供
/// 「读当前值 / 校验并保存」→ 保存后落盘并回调插件。单例，UI 线程使用。
///
/// 存储约定：所有值都写进插件级存储 <c>&lt;数据目录&gt;\Plugins\data\&lt;pluginId&gt;\settings.json</c>
/// （与 <c>IPluginHost.Storage</c> / 工具命令的 <c>ctx.Settings</c> 同一份文件），
/// 键统一加 <c>settings.</c> 前缀 —— 插件自己的其它键（如 tool-demo 的 runs）不受影响。
/// </summary>
public sealed class PluginSettingsService
{
    public static PluginSettingsService Instance { get; } = new();

    /// <summary>设置值在插件级存储里的键前缀。</summary>
    public const string StorageKeyPrefix = "settings.";

    /// <summary>Text 字段的长度上限（防御：设置文件是 JSON，不限制会被写爆）。</summary>
    public const int MaxTextLength = 4096;

    readonly List<PluginSettingsEntry> _entries = new();

    /// <summary>当前所有已加载插件注册的设置（插件加载顺序 + 插件内注册顺序；每插件最多 1 个）。</summary>
    public IReadOnlyList<PluginSettingsEntry> Schemas => _entries;

    /// <summary>有没有任何插件提供设置（设置页按钮据此启用 / 禁用）。</summary>
    public bool HasAny => _entries.Count > 0;

    /// <summary>schema 集合变化（插件加载 / 卸载 / 重新发布）。设置页按钮与打开的窗口靠它刷新。</summary>
    public event Action? SchemasChanged;

    /// <summary>某个插件的设置被成功保存（参数 = 插件 id）。</summary>
    public event Action<string>? SettingsChanged;

    PluginSettingsService() { }

    // ── 发布 / 解除 ──

    /// <summary>
    /// 插件 Initialize 成功后由 <see cref="PluginService"/> 调用。重复发布同一插件先移除旧 schema；
    /// schema 为 null 也要调 —— 加载重试 / 重载路径不能留着上一轮的设置页。
    /// <paramref name="storage"/> 传插件拿到的同一个存储实例，保证保存后插件读到的就是新值。
    /// </summary>
    internal void Publish(PluginRecord record, PluginManifest manifest, PluginSettingsSchema? schema,
        IPluginStorage? storage = null)
    {
        bool removed = _entries.RemoveAll(e =>
            string.Equals(e.PluginId, record.Id, StringComparison.OrdinalIgnoreCase)) > 0;
        if (schema == null)
        {
            if (removed) SchemasChanged?.Invoke();
            return;
        }

        var display = manifest.Name?.Get(LocalizationService.Instance.CurrentLanguage);
        if (string.IsNullOrWhiteSpace(display)) display = manifest.Id;
        _entries.Add(new PluginSettingsEntry
        {
            Record = record,
            Manifest = manifest,
            Schema = schema,
            PluginId = record.Id,
            PluginDisplayName = display!,
            Storage = storage ?? PluginStorage.Create(record.Id)
        });
        SchemasChanged?.Invoke();
    }

    /// <summary>插件卸载 / 被禁用 / 加载失败时调用：设置窗口里不应再出现它的设置块。</summary>
    internal void RemovePlugin(string pluginId)
    {
        if (_entries.RemoveAll(e => string.Equals(e.PluginId, pluginId, StringComparison.OrdinalIgnoreCase)) > 0)
            SchemasChanged?.Invoke();
    }

    internal void Clear()
    {
        if (_entries.Count == 0) return;
        _entries.Clear();
        SchemasChanged?.Invoke();
    }

    // ── 查询 ──

    public PluginSettingsEntry? Find(string pluginId)
        => _entries.FirstOrDefault(e => string.Equals(e.PluginId, pluginId, StringComparison.OrdinalIgnoreCase));

    // ── 读取 / 写入 ──

    /// <summary>
    /// 读一个插件设置块的全部当前值（字段键 → 值）。没保存过的字段返回默认值；
    /// 存储里被手改成非法值的字段同样回退默认 —— 设置窗口不能因为一份坏数据打不开。
    /// </summary>
    public Dictionary<string, string> ReadValues(PluginSettingsEntry entry)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (entry == null) return result;
        foreach (var field in entry.Schema.Fields)
        {
            TryNormalize(field, entry.Storage.Get(StorageKeyPrefix + field.Key, null), out var value, out _);
            result[field.Key] = value;
        }
        return result;
    }

    /// <summary>
    /// 校验并保存一个插件的设置值（先整体校验，任何一个字段非法都不写盘，避免半套新值）。
    /// 写盘后对「确实变化」的字段在 UI 线程回调 <see cref="PluginSettingsSchema.Changed"/>，
    /// 再触发 <see cref="SettingsChanged"/>。失败返回 false 并给出可直接显示的错误文案。
    /// </summary>
    public bool Save(PluginSettingsEntry entry, IReadOnlyDictionary<string, string> values, out string? error)
    {
        error = null;
        if (entry == null)
        {
            error = "settings entry is required";
            return false;
        }

        var normalized = new List<(PluginSettingField Field, string Value)>(entry.Schema.Fields.Count);
        foreach (var field in entry.Schema.Fields)
        {
            string? raw = null;
            values?.TryGetValue(field.Key, out raw);
            if (!TryNormalize(field, raw, out var value, out error)) return false;
            normalized.Add((field, value));
        }

        try
        {
            var changed = new List<(string Key, string Value)>();
            foreach (var (field, value) in normalized)
            {
                var key = StorageKeyPrefix + field.Key;
                if (string.Equals(entry.Storage.Get(key, null), value, StringComparison.Ordinal)) continue;
                entry.Storage.Set(key, value);
                changed.Add((field.Key, value));
            }
            entry.Storage.Flush();
            if (changed.Count == 0) return true;

            InvokeChangedCallbacks(entry, changed);
            SettingsChanged?.Invoke(entry.PluginId);
            return true;
        }
        catch (Exception ex)
        {
            error = LocalizationService.Instance.Get("Plugin.Settings.SaveFailed", ex.Message);
            return false;
        }
    }

    /// <summary>保存后回调插件（宿主约定：UI 线程）。插件回调抛异常只记日志，不影响其它字段。</summary>
    static void InvokeChangedCallbacks(PluginSettingsEntry entry, List<(string Key, string Value)> changed)
    {
        void Raise()
        {
            foreach (var (key, value) in changed)
            {
                try { entry.Schema.Changed?.Invoke(key, value); }
                catch (Exception ex)
                {
                    DzTrace.Always($"[Plugin] settings changed callback for '{entry.PluginId}/{key}' threw: " +
                                   $"{ex.GetType().Name} {ex.Message}");
                }
            }
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            try { dispatcher.Invoke(Raise); } catch { /* 关闭中的 Dispatcher：回调丢失可接受 */ }
            return;
        }
        Raise();
    }

    /// <summary>
    /// 把值规范成存储格式；非法值返回 false 并给出本地化错误。
    /// <paramref name="raw"/> 为 null / 空串时对 Toggle / Int / Choice 使用字段默认值
    /// （Text 的空串是合法的「清空」值）。
    /// </summary>
    bool TryNormalize(PluginSettingField field, string? raw, out string value, out string? error)
    {
        error = null;
        var loc = LocalizationService.Instance;
        var label = field.Label.Get(loc.CurrentLanguage);

        switch (field.Kind)
        {
            case PluginSettingKind.Toggle:
            {
                var text = string.IsNullOrEmpty(raw) ? field.DefaultValue : raw;
                if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase))
                {
                    value = "true";
                    return true;
                }
                if (string.Equals(text, "false", StringComparison.OrdinalIgnoreCase))
                {
                    value = "false";
                    return true;
                }
                value = "";
                error = loc.Get("Plugin.Settings.InvalidToggle", label);
                return false;
            }

            case PluginSettingKind.Int:
            {
                var text = string.IsNullOrEmpty(raw)
                    ? (string.IsNullOrEmpty(field.DefaultValue)
                        ? (field.Min ?? 0).ToString(CultureInfo.InvariantCulture)
                        : field.DefaultValue)
                    : raw;
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                {
                    value = "";
                    error = loc.Get("Plugin.Settings.InvalidInt", label);
                    return false;
                }
                if (field.Min.HasValue && number < field.Min.Value)
                {
                    value = "";
                    error = loc.Get("Plugin.Settings.IntBelowMin", label, field.Min.Value);
                    return false;
                }
                if (field.Max.HasValue && number > field.Max.Value)
                {
                    value = "";
                    error = loc.Get("Plugin.Settings.IntAboveMax", label, field.Max.Value);
                    return false;
                }
                value = number.ToString(CultureInfo.InvariantCulture);
                return true;
            }

            case PluginSettingKind.Choice:
            {
                var choices = field.Choices ?? new List<PluginSettingChoice>();
                var candidate = string.IsNullOrEmpty(raw)
                    ? (string.IsNullOrEmpty(field.DefaultValue)
                        ? (choices.Count > 0 ? choices[0].Value : "")
                        : field.DefaultValue)
                    : raw;
                if (!choices.Any(c => string.Equals(c.Value, candidate, StringComparison.Ordinal)))
                {
                    value = "";
                    error = loc.Get("Plugin.Settings.InvalidChoice", label);
                    return false;
                }
                value = candidate;
                return true;
            }

            default:
            {
                var text = raw ?? field.DefaultValue ?? "";
                if (text.Length > MaxTextLength)
                {
                    value = "";
                    error = loc.Get("Plugin.Settings.TextTooLong", label, MaxTextLength);
                    return false;
                }
                value = text;
                return true;
            }
        }
    }
}
