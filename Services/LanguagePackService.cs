using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DesktopZones.Helpers;
using DesktopZones.Models;

namespace DesktopZones.Services;

/// <summary>语言包 payload 的校验结果。空 <see cref="Errors"/> = 可安装。</summary>
public sealed class LanguagePackValidation
{
    public List<string> Errors { get; } = new();
    public Dictionary<string, string> Table { get; } = new(StringComparer.Ordinal);
    public int HostKeyCount { get; set; }
    public string PayloadRelativePath { get; set; } = "";

    public int TranslatedCount => Table.Count;
    public double Coverage => HostKeyCount <= 0 ? 0 : (double)Table.Count / HostKeyCount;
    public bool Ok => Errors.Count == 0;
}

/// <summary>
/// 语言补充包（<c>kind=language</c>）的校验、索引与解析。
///
/// 纯数据：宿主只做「校验 + 落盘 + 刷新」，不执行任何脚本。索引写在
/// <c>Plugins\.state\languages.json</c>，是缓存 —— 每次启动按 <see cref="PluginRecord"/>
/// 重新校验生成，因此手改索引不会生效，卸载/停用后也不会残留。
///
/// 契约见方案 §7.10：扁平 JSON、key 必须存在、占位符集合必须与源词条完全一致、
/// 允许部分翻译（缺词条走回退链）、不允许 RTL（P1）。
/// </summary>
public sealed class LanguagePackService
{
    public static LanguagePackService Instance { get; } = new();

    LanguagePackService() { }

    public const int MaxPayloadBytes = 2 * 1024 * 1024;
    public const int MaxValueLength = 4000;
    const int MaxReportedErrors = 20;

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // {0} / {0:N0} / {0,5} —— 跳过 {{ }} 转义（string.Format 里它们是字面花括号）。
    static readonly Regex PlaceholderRe = new(@"(?<!\{)\{(\d+)(?:[^}]*)?\}(?!\})", RegexOptions.Compiled);

    /// <summary>P1 不支持从右到左排版（WPF 需要整体镜像布局）。</summary>
    static readonly HashSet<string> RtlLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "ar", "arc", "ckb", "dv", "fa", "he", "iw", "khw", "ks", "ku", "ps", "sd", "ug", "ur", "yi"
    };

    Dictionary<string, LanguagePackEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    Dictionary<string, Dictionary<string, string>> _tables = new(StringComparer.OrdinalIgnoreCase);
    Dictionary<string, string> _errors = new(StringComparer.OrdinalIgnoreCase);
    Dictionary<string, string> _chosen = new(StringComparer.OrdinalIgnoreCase);
    bool _loaded;

    /// <summary>当前可用的语言包（已启用且校验通过）。</summary>
    public IReadOnlyList<LanguagePackEntry> Entries
        => _entries.Values.OrderBy(e => e.Code, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.PackId, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>包 id → 加载错误（启用了但校验失败）。</summary>
    public IReadOnlyDictionary<string, string> Errors => _errors;

    // ── 加载 / 刷新 ──

    /// <summary>读取索引缓存（不校验 payload）。可重复调用。</summary>
    public void Load()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            var path = PluginPaths.LanguageIndexPath;
            if (!File.Exists(path)) return;
            var json = ReadText(path);
            var index = JsonSerializer.Deserialize<LanguagePackIndexFile>(json, JsonOptions);
            if (index == null) return;
            _entries = new Dictionary<string, LanguagePackEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in index.Packs)
            {
                if (string.IsNullOrWhiteSpace(entry?.PackId) || string.IsNullOrWhiteSpace(entry.Code)) continue;
                _entries[entry.PackId] = entry;
            }
            _chosen = new Dictionary<string, string>(index.Chosen, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            _entries.Clear();
            _chosen.Clear();
        }
    }

    /// <summary>
    /// 按插件记录重建索引：启用的语言包重新读 manifest + payload、重算覆盖率。
    /// 返回是否有变化（调用方据此决定要不要刷新界面）。
    /// </summary>
    public bool Refresh(IEnumerable<PluginRecord> records, bool pluginsEnabled)
    {
        Load();
        var nextEntries = new Dictionary<string, LanguagePackEntry>(StringComparer.OrdinalIgnoreCase);
        var nextTables = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var nextErrors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (pluginsEnabled)
        {
            foreach (var record in records)
            {
                if (record.KindValue != PluginKind.Language || !record.Enabled) continue;
                var dir = PluginPaths.VersionDir(record.Id, record.Version);
                if (!Directory.Exists(dir))
                {
                    nextErrors[record.Id] = "files are missing; reinstall the language pack";
                    continue;
                }
                var manifest = PluginPackages.ReadManifest(dir, out var manifestError);
                if (manifest == null || manifest.KindValue != PluginKind.Language)
                {
                    nextErrors[record.Id] = manifestError ?? "plugin.json is not a language pack";
                    continue;
                }
                var validation = Validate(dir, manifest);
                if (!validation.Ok)
                {
                    nextErrors[record.Id] = string.Join("; ", validation.Errors.Take(3));
                    continue;
                }
                if (string.IsNullOrEmpty(validation.PayloadRelativePath))
                {
                    nextErrors[record.Id] = "payload.file is missing";
                    continue;
                }
                var fullPayload = PluginPaths.ResolveInside(dir, validation.PayloadRelativePath);
                if (fullPayload == null)
                {
                    nextErrors[record.Id] = "payload file is outside the package";
                    continue;
                }
                var relative = PluginPaths.ToPackagePath(fullPayload, PluginPaths.Root);
                if (relative.StartsWith("..", StringComparison.Ordinal))
                {
                    nextErrors[record.Id] = "payload path is outside the plugin folder";
                    continue;
                }
                nextEntries[record.Id] = new LanguagePackEntry
                {
                    Code = manifest.Language!.Code,
                    NativeName = manifest.Language.NativeName,
                    Base = string.IsNullOrWhiteSpace(manifest.Language.Base) ? "en" : manifest.Language.Base,
                    PackId = record.Id,
                    Version = record.Version,
                    Coverage = validation.Coverage,
                    DeclaredCoverage = manifest.Language.Coverage,
                    Path = relative,
                    AuthorName = manifest.Author?.Name ?? "",
                    AuthorGithub = manifest.Author?.Github ?? "",
                    Official = IsOfficial(manifest),
                    InstalledAtUtc = record.InstalledAtUtc
                };
                nextTables[record.Id] = validation.Table;
            }
        }

        var changed = !SameEntries(_entries, nextEntries) || !SameErrors(_errors, nextErrors);
        _entries = nextEntries;
        _tables = nextTables;
        _errors = nextErrors;

        // 被卸载/停用的包不再是「上次选择」。
        foreach (var code in _chosen.Keys.ToList())
        {
            if (_entries.ContainsKey(_chosen[code])) continue;
            _chosen.Remove(code);
            changed = true;
        }

        if (changed) SaveIndex();
        return changed;
    }

    static bool SameEntries(Dictionary<string, LanguagePackEntry> a, Dictionary<string, LanguagePackEntry> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var pair in a)
        {
            if (!b.TryGetValue(pair.Key, out var other)) return false;
            var x = pair.Value;
            if (!string.Equals(x.Code, other.Code, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(x.Version, other.Version, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(x.Path, other.Path, StringComparison.Ordinal)
                || Math.Abs(x.Coverage - other.Coverage) > 0.0001
                || x.Official != other.Official)
                return false;
        }
        return true;
    }

    static bool SameErrors(Dictionary<string, string> a, Dictionary<string, string> b)
        => a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var v) && v == p.Value);

    void SaveIndex()
    {
        try
        {
            PluginPaths.EnsureCreated();
            var index = new LanguagePackIndexFile
            {
                Chosen = new Dictionary<string, string>(_chosen, StringComparer.OrdinalIgnoreCase),
                Packs = _entries.Values.OrderBy(e => e.Code, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(e => e.PackId, StringComparer.OrdinalIgnoreCase).ToList()
            };
            var path = PluginPaths.LanguageIndexPath;
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(index, JsonOptions), new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            DzTrace.Always("[LanguagePack] cannot write index: " + ex.Message);
        }
    }

    // ── 解析 ──

    /// <summary>同 code 多包时的选择：上次选择 → 官方 → 版本高 → 安装早。</summary>
    public LanguagePackEntry? Resolve(string code)
    {
        Load();
        if (string.IsNullOrWhiteSpace(code)) return null;
        var candidates = _entries.Values
            .Where(e => string.Equals(e.Code, code, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (candidates.Count == 0) return null;
        if (_chosen.TryGetValue(code, out var chosen))
        {
            var hit = candidates.FirstOrDefault(e => string.Equals(e.PackId, chosen, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
        }
        return candidates
            .OrderByDescending(e => e.Official)
            .ThenByDescending(e => Version.TryParse(e.Version, out var v) ? v : new Version(0, 0))
            .ThenBy(e => e.InstalledAtUtc)
            .First();
    }

    /// <summary>取某语言的词条表（空值视为未翻译，已在校验时丢弃）。</summary>
    public Dictionary<string, string>? GetTable(string code)
    {
        Load();
        var entry = Resolve(code);
        if (entry == null) return null;
        if (_tables.TryGetValue(entry.PackId, out var cached)) return cached;
        var table = ReadTable(entry);
        if (table == null) return null;
        _tables[entry.PackId] = table;
        return table;
    }

    Dictionary<string, string>? ReadTable(LanguagePackEntry entry)
    {
        try
        {
            var full = PluginPaths.ResolveInside(PluginPaths.Root, entry.Path);
            if (full == null || !File.Exists(full)) return null;
            if (new FileInfo(full).Length > MaxPayloadBytes) return null;
            using var doc = JsonDocument.Parse(ReadText(full));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var table = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Name.StartsWith("_", StringComparison.Ordinal)) continue;
                if (prop.Value.ValueKind != JsonValueKind.String) continue;
                var value = prop.Value.GetString() ?? "";
                if (value.Trim().Length == 0) continue;
                table[prop.Name] = value;
            }
            return table;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>用户切到某语言时记下实际生效的包，避免下次安装新包时被换掉。</summary>
    public void MarkChosen(string code)
    {
        Load();
        var entry = Resolve(code);
        if (entry == null) return;
        if (_chosen.TryGetValue(code, out var current)
            && string.Equals(current, entry.PackId, StringComparison.OrdinalIgnoreCase)) return;
        _chosen[code] = entry.PackId;
        SaveIndex();
    }

    public double? CoverageOf(string code) => Resolve(code)?.Coverage;

    public static bool IsOfficial(PluginManifest manifest)
        => string.Equals(manifest.Author?.Github?.Trim(), "Three-Freezen", StringComparison.OrdinalIgnoreCase)
           && manifest.Repo.StartsWith("Three-Freezen/", StringComparison.OrdinalIgnoreCase);

    // ── 校验 ──

    /// <summary>校验 <paramref name="packageDir"/> 内的语言 payload。</summary>
    public LanguagePackValidation Validate(string packageDir, PluginManifest manifest)
    {
        var result = new LanguagePackValidation();
        var language = manifest.Language;
        if (language == null)
        {
            result.Errors.Add("language block is missing");
            return result;
        }
        if (IsRtl(language.Code))
        {
            result.Errors.Add("RTL languages are not supported yet: " + language.Code);
            return result;
        }

        var relative = manifest.Payload?.File ?? "";
        if (!PluginPaths.IsSafeRelativePath(relative))
        {
            result.Errors.Add("payload.file is not a safe relative path");
            return result;
        }
        result.PayloadRelativePath = relative;
        var full = PluginPaths.ResolveInside(packageDir, relative);
        if (full == null || !File.Exists(full))
        {
            result.Errors.Add("payload file is missing: " + relative);
            return result;
        }
        var info = new FileInfo(full);
        if (info.Length > MaxPayloadBytes)
        {
            result.Errors.Add("payload is larger than 2 MB");
            return result;
        }

        var source = LocalizationService.ReadHostSource();
        result.HostKeyCount = source.Count;

        string json;
        try
        {
            json = ReadText(full);
        }
        catch (Exception ex)
        {
            result.Errors.Add("cannot read payload: " + ex.Message);
            return result;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            result.Errors.Add("payload is not valid JSON: " + ex.Message);
            return result;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                result.Errors.Add("payload must be a flat JSON object");
                return result;
            }

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (result.Errors.Count >= MaxReportedErrors) break;

                if (prop.Name.StartsWith("_", StringComparison.Ordinal))
                {
                    result.Errors.Add("unknown key: " + prop.Name);
                    continue;
                }
                if (!source.TryGetValue(prop.Name, out var sourceText))
                {
                    result.Errors.Add("unknown key: " + prop.Name);
                    continue;
                }
                if (prop.Value.ValueKind != JsonValueKind.String)
                {
                    result.Errors.Add($"key '{prop.Name}': value must be a string");
                    continue;
                }
                var value = prop.Value.GetString() ?? "";
                if (value.Length > MaxValueLength)
                {
                    result.Errors.Add($"key '{prop.Name}': value is longer than {MaxValueLength} characters");
                    continue;
                }
                if (value.Trim().Length == 0) continue; // 视为未翻译，不计入覆盖率

                var expected = Placeholders(sourceText);
                var actual = Placeholders(value);
                if (!expected.SetEquals(actual))
                {
                    result.Errors.Add(
                        $"key '{prop.Name}': expected placeholders {FormatPlaceholders(expected)}, got {FormatPlaceholders(actual)}");
                    continue;
                }
                result.Table[prop.Name] = value;
            }
        }

        if (result.Ok && result.Table.Count == 0)
            result.Errors.Add("payload contains no translated entries");
        return result;
    }

    static bool IsRtl(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;
        var primary = code.Split('-')[0];
        return RtlLanguages.Contains(primary);
    }

    internal static HashSet<int> Placeholders(string? text)
    {
        var set = new HashSet<int>();
        if (string.IsNullOrEmpty(text)) return set;
        foreach (Match match in PlaceholderRe.Matches(text))
        {
            if (int.TryParse(match.Groups[1].Value, out var index)) set.Add(index);
        }
        return set;
    }

    static string FormatPlaceholders(HashSet<int> set)
        => set.Count == 0 ? "none" : string.Join(", ", set.OrderBy(i => i).Select(i => "{" + i + "}"));

    static string ReadText(string path)
    {
        var text = File.ReadAllText(path, Encoding.UTF8);
        return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
    }
}
