using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using DeskOrder.PluginApi;
using DesktopZones.Helpers;

namespace DesktopZones.Services;

/// <summary>每个已加载插件一份的宿主门面。</summary>
public sealed class PluginHostContext : IPluginHost
{
    public required string PluginId { get; init; }
    public required Version HostVersion { get; init; }
    public int ApiVersion => PluginApiVersion.Current;
    public required IPluginStorage Storage { get; init; }
    public required IPluginUi Ui { get; init; }
    public required IPluginWidgets Widgets { get; init; }
    public required IPluginZoneBays ZoneBays { get; init; }
    public required IPluginTools Tools { get; init; }
    public required IPluginIntegrations Integrations { get; init; }
    /// <summary>插件设置注册入口（API v5）。</summary>
    public required IPluginSettings Settings { get; init; }
    public required IPluginLog Log { get; init; }

    public PluginWidgetRegistry Registry => (PluginWidgetRegistry)Widgets;
    public PluginZoneBayRegistry BayRegistry => (PluginZoneBayRegistry)ZoneBays;
    public PluginToolRegistry ToolRegistry => (PluginToolRegistry)Tools;
    public PluginIntegrationRegistry IntegrationRegistry => (PluginIntegrationRegistry)Integrations;
    public PluginSettingsRegistry SettingsRegistry => (PluginSettingsRegistry)Settings;
}

/// <summary>收集 <see cref="PluginZoneBayDescriptor"/>，拒绝重复/非法 id。</summary>
public sealed class PluginZoneBayRegistry : IPluginZoneBays
{
    public const int MaxBays = 4;
    readonly Dictionary<string, PluginZoneBayDescriptor> _descriptors = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, PluginZoneBayDescriptor> Descriptors => _descriptors;

    public void Register(PluginZoneBayDescriptor descriptor)
    {
        if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
        if (string.IsNullOrWhiteSpace(descriptor.Id) || descriptor.Id.Length > 64)
            throw new ArgumentException("zone bay descriptor id is required (max 64 chars)");
        if (string.IsNullOrWhiteSpace(descriptor.DisplayName))
            throw new ArgumentException("zone bay descriptor display name is required");
        if (_descriptors.Count >= MaxBays)
            throw new InvalidOperationException($"a plugin may register at most {MaxBays} zone bays");
        if (!_descriptors.TryAdd(descriptor.Id, descriptor))
            throw new InvalidOperationException($"duplicate zone bay id: {descriptor.Id}");
    }

    internal void Clear() => _descriptors.Clear();
}

/// <summary>收集 <see cref="PluginWidgetDescriptor"/>，拒绝重复/非法 id。</summary>
public sealed class PluginWidgetRegistry : IPluginWidgets
{
    public const int MaxWidgets = 8;
    readonly Dictionary<string, PluginWidgetDescriptor> _descriptors = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, PluginWidgetDescriptor> Descriptors => _descriptors;

    public void Register(PluginWidgetDescriptor descriptor)
    {
        if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
        if (string.IsNullOrWhiteSpace(descriptor.Id) || descriptor.Id.Length > 64)
            throw new ArgumentException("widget descriptor id is required (max 64 chars)");
        if (string.IsNullOrWhiteSpace(descriptor.DisplayName))
            throw new ArgumentException("widget descriptor display name is required");
        if (descriptor.CreateContent == null)
            throw new ArgumentException("widget descriptor CreateContent is required");
        if (_descriptors.Count >= MaxWidgets)
            throw new InvalidOperationException($"a plugin may register at most {MaxWidgets} widgets");
        if (!_descriptors.TryAdd(descriptor.Id, descriptor))
            throw new InvalidOperationException($"duplicate widget id: {descriptor.Id}");
    }

    internal void Clear() => _descriptors.Clear();
}

/// <summary>收集 <see cref="PluginToolCommand"/>（API v3），拒绝重复/非法 id 与超量注册。</summary>
public sealed class PluginToolRegistry : IPluginTools
{
    public const int MaxCommands = 16;
    /// <summary>命令 id 会进配置（热键分配）与菜单，只允许稳定的小写片段。</summary>
    static readonly Regex IdPattern = new("^[a-z0-9._-]{1,64}$", RegexOptions.Compiled);

    readonly List<PluginToolCommand> _commands = new();

    public IReadOnlyList<PluginToolCommand> Commands => _commands;

    public void Register(PluginToolCommand command)
    {
        if (command == null) throw new ArgumentNullException(nameof(command));
        if (string.IsNullOrEmpty(command.Id) || !IdPattern.IsMatch(command.Id))
            throw new ArgumentException("tool command id must be 1..64 chars of [a-z0-9._-]");
        if (string.IsNullOrWhiteSpace(command.DisplayName))
            throw new ArgumentException("tool command display name is required");
        if (command.LocalizedDisplayName != null && !command.LocalizedDisplayName.HasAny)
            throw new ArgumentException("tool command localized display name has no text (zh / en)");
        if (command.Execute == null)
            throw new ArgumentException("tool command Execute is required");
        if (_commands.Count >= MaxCommands)
            throw new InvalidOperationException($"a plugin may register at most {MaxCommands} tool commands");
        if (_commands.Any(c => string.Equals(c.Id, command.Id, StringComparison.Ordinal)))
            throw new InvalidOperationException($"duplicate tool command id: {command.Id}");
        _commands.Add(command);
    }

    internal void Clear() => _commands.Clear();
}

/// <summary>收集 <see cref="PluginMediaSourceDescriptor"/>（API v4），拒绝重复/非法 id 与超量注册。</summary>
public sealed class PluginIntegrationRegistry : IPluginIntegrations
{
    public const int MaxSources = 4;
    /// <summary>数据源 id 会进配置（播放器的 DataSourceKey），只允许稳定的小写片段。</summary>
    static readonly Regex IdPattern = new("^[a-z0-9._-]{1,64}$", RegexOptions.Compiled);

    readonly List<PluginMediaSourceDescriptor> _sources = new();

    public IReadOnlyList<PluginMediaSourceDescriptor> Sources => _sources;

    public void RegisterMediaSource(PluginMediaSourceDescriptor descriptor)
    {
        if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
        if (string.IsNullOrEmpty(descriptor.Id) || !IdPattern.IsMatch(descriptor.Id))
            throw new ArgumentException("media source id must be 1..64 chars of [a-z0-9._-]");
        if (string.IsNullOrWhiteSpace(descriptor.DisplayName))
            throw new ArgumentException("media source display name is required");
        if (descriptor.LocalizedDisplayName != null && !descriptor.LocalizedDisplayName.HasAny)
            throw new ArgumentException("media source localized display name has no text (zh / en)");
        if (descriptor.Poll == null)
            throw new ArgumentException("media source Poll is required");
        if (_sources.Count >= MaxSources)
            throw new InvalidOperationException($"a plugin may register at most {MaxSources} media sources");
        if (_sources.Any(s => string.Equals(s.Id, descriptor.Id, StringComparison.Ordinal)))
            throw new InvalidOperationException($"duplicate media source id: {descriptor.Id}");
        _sources.Add(descriptor);
    }

    internal void Clear() => _sources.Clear();
}

/// <summary>收集 <see cref="PluginSettingsSchema"/>（API v5），校验字段定义并拒绝重复注册。</summary>
public sealed class PluginSettingsRegistry : IPluginSettings
{
    /// <summary>一个插件最多能声明的设置字段数。</summary>
    public const int MaxFields = 20;

    /// <summary>字段键会进插件级存储（<c>settings.&lt;key&gt;</c>）与设置窗口，只允许稳定的小写片段。</summary>
    static readonly Regex KeyPattern = new("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.Compiled);

    /// <summary>已注册的 schema；null = 这个插件没注册设置。</summary>
    public PluginSettingsSchema? Schema { get; private set; }

    public void Register(PluginSettingsSchema schema)
    {
        if (schema == null) throw new ArgumentNullException(nameof(schema));
        if (Schema != null)
            throw new InvalidOperationException("a plugin may register at most one settings schema");
        if (schema.Title == null || !schema.Title.HasAny)
            throw new ArgumentException("settings schema title is required");
        if (schema.Fields.Count > MaxFields)
            throw new InvalidOperationException($"a plugin may register at most {MaxFields} settings fields");

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in schema.Fields)
        {
            if (field == null) throw new ArgumentException("settings field is required");
            if (string.IsNullOrEmpty(field.Key) || !KeyPattern.IsMatch(field.Key))
                throw new ArgumentException("settings field key must be 1..64 chars of [a-z0-9._-] starting with a letter or digit");
            if (!keys.Add(field.Key))
                throw new InvalidOperationException($"duplicate settings field key: {field.Key}");
            if (field.Label == null || !field.Label.HasAny)
                throw new ArgumentException($"settings field '{field.Key}' label is required");

            switch (field.Kind)
            {
                case PluginSettingKind.Toggle:
                    if (field.DefaultValue is not ("true" or "false"))
                        throw new ArgumentException(
                            $"settings field '{field.Key}' is a toggle: DefaultValue must be \"true\" or \"false\"");
                    break;

                case PluginSettingKind.Text:
                    break;

                case PluginSettingKind.Int:
                {
                    if (field.Min.HasValue && field.Max.HasValue && field.Min.Value > field.Max.Value)
                        throw new InvalidOperationException(
                            $"settings field '{field.Key}' has Min greater than Max");
                    if (!string.IsNullOrEmpty(field.DefaultValue))
                    {
                        if (!int.TryParse(field.DefaultValue, NumberStyles.Integer,
                                CultureInfo.InvariantCulture, out var defaultValue))
                            throw new ArgumentException(
                                $"settings field '{field.Key}' DefaultValue must be an integer");
                        if (field.Min.HasValue && defaultValue < field.Min.Value)
                            throw new InvalidOperationException(
                                $"settings field '{field.Key}' DefaultValue is below Min");
                        if (field.Max.HasValue && defaultValue > field.Max.Value)
                            throw new InvalidOperationException(
                                $"settings field '{field.Key}' DefaultValue is above Max");
                    }
                    break;
                }

                case PluginSettingKind.Choice:
                {
                    if (field.Choices == null || field.Choices.Count == 0)
                        throw new ArgumentException(
                            $"settings field '{field.Key}' is a choice: Choices must not be empty");
                    var values = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var choice in field.Choices)
                    {
                        if (choice == null || string.IsNullOrEmpty(choice.Value))
                            throw new ArgumentException(
                                $"settings field '{field.Key}' has a choice without a value");
                        if (!values.Add(choice.Value))
                            throw new InvalidOperationException(
                                $"settings field '{field.Key}' has a duplicate choice value: {choice.Value}");
                        if (choice.Label == null || !choice.Label.HasAny)
                            throw new ArgumentException(
                                $"settings field '{field.Key}' choice '{choice.Value}' label is required");
                    }
                    if (!string.IsNullOrEmpty(field.DefaultValue) && !values.Contains(field.DefaultValue))
                        throw new InvalidOperationException(
                            $"settings field '{field.Key}' DefaultValue is not one of its choices");
                    break;
                }

                default:
                    throw new ArgumentException($"settings field '{field.Key}' has an unknown kind");
            }
        }

        Schema = schema;
    }

    internal void Clear() => Schema = null;
}

/// <summary>JSON 键值存储：插件级落盘 / 组件级写回 SettingsJson。</summary>
public sealed class JsonPluginStorage : IPluginStorage
{
    readonly object _gate = new();
    readonly Dictionary<string, string> _data;
    readonly string? _path;
    readonly Action? _onFlush;

    public JsonPluginStorage(string? path, string? initialJson, Action? onFlush)
    {
        _path = path;
        _onFlush = onFlush;
        _data = Parse(initialJson);
        if (path != null && File.Exists(path))
        {
            try { _data = Parse(File.ReadAllText(path)); }
            catch { /* 损坏的插件数据按空处理，不阻塞加载 */ }
        }
    }

    static Dictionary<string, string> Parse(string? json)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json)) return dict;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return dict;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                    dict[prop.Name] = prop.Value.GetString() ?? "";
            }
        }
        catch (JsonException) { }
        return dict;
    }

    public string Get(string key)
    {
        lock (_gate) return _data.TryGetValue(key, out var value) ? value : "";
    }

    public string? Get(string key, string? fallback)
    {
        lock (_gate) return _data.TryGetValue(key, out var value) ? value : fallback;
    }

    public void Set(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        lock (_gate)
        {
            if (value == null)
            {
                if (!_data.Remove(key)) return;
            }
            else
            {
                _data[key] = value;
            }
        }
        Flush();
    }

    public void Flush()
    {
        string json;
        lock (_gate)
        {
            json = JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = false });
        }
        try
        {
            if (_path != null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, _path, overwrite: true);
            }
            _onFlush?.Invoke();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Plugin] storage flush failed: {ex.Message}");
        }
    }

    public string ToJson()
    {
        lock (_gate) return JsonSerializer.Serialize(_data);
    }
}

/// <summary>宿主主题资源门面。</summary>
public sealed class PluginUiFacade : IPluginUi
{
    readonly Action<string, string>? _notify;

    public PluginUiFacade(Action<string, string>? notify) => _notify = notify;

    public Brush GetBrush(string key)
    {
        try
        {
            if (Application.Current?.TryFindResource(key) is Brush brush) return brush;
        }
        catch { }
        return Application.Current?.TryFindResource("Brush.Text.Primary") as Brush ?? Brushes.White;
    }

    public FontFamily FontFamily => SystemFonts.MessageFontFamily;

    public void Notify(string title, string body) => _notify?.Invoke(title, body);
}

public sealed class PluginLogger : IPluginLog
{
    readonly string _pluginId;

    public PluginLogger(string pluginId) => _pluginId = pluginId;

    public void Info(string message) => Write("INFO", message, null);
    public void Warn(string message) => Write("WARN", message, null);
    public void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    void Write(string level, string message, Exception? ex)
    {
        var line = $"[Plugin] {_pluginId} {level}: {message}";
        if (ex != null) line += Environment.NewLine + ex;
        DzTrace.Always(line);
        System.Diagnostics.Debug.WriteLine(line);
    }
}

/// <summary>插件词条门面：直接复用宿主语言表（语言包生效后插件也能拿到同语言）。</summary>
public sealed class PluginLocalizer : ILocalizer
{
    public static readonly PluginLocalizer Instance = new();
    public string this[string key] => LocalizationService.Instance[key];
}
