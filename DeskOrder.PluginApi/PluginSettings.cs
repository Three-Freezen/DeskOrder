using System;
using System.Collections.Generic;

namespace DeskOrder.PluginApi;

/// <summary>
/// 插件设置注册入口（API v5）。只能在 <see cref="IDeskOrderPlugin.Initialize"/> 里调用一次；
/// 注册后宿主会把这些设置项画进「设置 → 插件 → 插件设置…」窗口，并把用户改动的值写进
/// 插件级存储（<c>settings.&lt;fieldKey&gt;</c>，与 <see cref="IPluginHost.Storage"/> 同一份文件）。
/// </summary>
public interface IPluginSettings
{
    /// <summary>
    /// 注册本插件的设置 schema。每个插件最多 1 个 schema、最多 20 个字段；
    /// 非法字段（重复 Key / 非法 Key / 空标签 / Choice 为空 / Int 的 Min &gt; Max /
    /// Toggle 默认值不是 true/false 等）会抛异常，宿主按该插件加载失败处理。
    /// </summary>
    void Register(PluginSettingsSchema schema);
}

/// <summary>一个插件在设置窗口里的一块：标题 + 字段列表 + 保存回调。</summary>
public sealed class PluginSettingsSchema
{
    /// <summary>这块设置的标题（随宿主语言显示）。</summary>
    public required PluginLocalizedText Title { get; init; }

    /// <summary>字段，最多 20 个；Key 在 schema 内唯一。</summary>
    public List<PluginSettingField> Fields { get; } = new();

    /// <summary>
    /// 用户在设置窗口点「保存」后，宿主在 UI 线程按字段回调（key, newValue）。
    /// 只对值确实变化的字段触发；宿主会在回调前把值落盘。
    /// 插件也可以在命令里用 <see cref="IPluginHost.Storage"/> 读
    /// <c>settings.&lt;key&gt;</c> 的当前值。
    /// </summary>
    public Action<string, string>? Changed { get; init; }
}

/// <summary>一个设置字段。渲染方式由 <see cref="Kind"/> 决定。</summary>
public sealed class PluginSettingField
{
    /// <summary>存储键（会拼成 <c>settings.&lt;Key&gt;</c>），1..64 个 <c>[a-z0-9._-]</c>。</summary>
    public required string Key { get; init; }

    public required PluginSettingKind Kind { get; init; }

    /// <summary>字段标签（随宿主语言显示）。</summary>
    public required PluginLocalizedText Label { get; init; }

    /// <summary>可选说明，显示在控件下方。</summary>
    public PluginLocalizedText? Hint { get; init; }

    /// <summary>默认值（存字符串）。Toggle 必须是 <c>"true"</c> / <c>"false"</c>；Int 可留空 = 0 / Min。</summary>
    public string DefaultValue { get; init; } = "";

    /// <summary>仅 <see cref="PluginSettingKind.Int"/>：最小 / 最大值（可空 = 不限）。</summary>
    public int? Min { get; init; }

    /// <summary>仅 <see cref="PluginSettingKind.Int"/>：最大 / 最小值（可空 = 不限）。</summary>
    public int? Max { get; init; }

    /// <summary>仅 <see cref="PluginSettingKind.Choice"/>：非空，Value 在字段内唯一。</summary>
    public List<PluginSettingChoice>? Choices { get; init; }
}

/// <summary>设置字段的渲染类型（API v5）。</summary>
public enum PluginSettingKind
{
    /// <summary>勾选框，值只可能是 <c>"true"</c> / <c>"false"</c>。</summary>
    Toggle,

    /// <summary>单行文本框。</summary>
    Text,

    /// <summary>整数文本框，可配 <see cref="PluginSettingField.Min"/> / <see cref="PluginSettingField.Max"/>。</summary>
    Int,

    /// <summary>下拉框，候选来自 <see cref="PluginSettingField.Choices"/>。</summary>
    Choice
}

/// <summary>Choice 字段的一个候选项。</summary>
public sealed class PluginSettingChoice
{
    /// <summary>写进存储的值（存字符串）。</summary>
    public required string Value { get; init; }

    /// <summary>下拉里显示的文字（随宿主语言显示）。</summary>
    public required PluginLocalizedText Label { get; init; }
}

/// <summary>
/// 插件 API 的多语言文本（API v5）。用于工具命令名、数据源名与设置项的标题/标签/提示。
/// 只是「显示名」：宿主按当前语言解析，<see cref="Get"/> 的未知语言（ja / ar …）回退英文 → 中文 → 空串。
/// </summary>
public sealed class PluginLocalizedText
{
    public string? Zh { get; init; }

    public string? En { get; init; }

    /// <summary>至少一种语言有非空文本。</summary>
    public bool HasAny => !string.IsNullOrWhiteSpace(Zh) || !string.IsNullOrWhiteSpace(En);

    /// <summary>
    /// 按宿主语言解析：中文（zh / zh-CN …）优先中文、否则英文；
    /// 其它语言优先英文、再回退中文；两种都没有返回空串。
    /// </summary>
    public string Get(string language)
    {
        if (!string.IsNullOrWhiteSpace(language)
            && language.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            return !string.IsNullOrWhiteSpace(Zh) ? Zh! : (En ?? "");
        }
        return !string.IsNullOrWhiteSpace(En) ? En! : (Zh ?? "");
    }

    /// <summary>便捷构造：<c>PluginLocalizedText.From("中文", "English")</c>。</summary>
    public static PluginLocalizedText From(string? zh, string? en) => new() { Zh = zh, En = en };

    public override string ToString() => Get("zh");
}
