using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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
    public required IPluginLog Log { get; init; }

    public PluginWidgetRegistry Registry => (PluginWidgetRegistry)Widgets;
    public PluginZoneBayRegistry BayRegistry => (PluginZoneBayRegistry)ZoneBays;
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
