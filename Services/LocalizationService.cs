using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DesktopZones.Helpers;
using DesktopZones.Models;

namespace DesktopZones.Services;

/// <summary>
/// 界面文案。内置 <c>zh</c>/<c>en</c> + 已安装的语言补充包（<see cref="LanguagePackService"/>）。
///
/// 回退链（命中即停）：
/// 用户 <c>lang\&lt;code&gt;.json</c> 覆盖 → 已安装包（同 code）→ 内置同 code →
/// 包声明的 base（默认 en）→ en → zh → key。
///
/// 语言列表是动态的：安装/卸载/停用语言包后调用 <see cref="ReloadLanguagePacks"/>，
/// 设置页与托盘据此重建。
/// </summary>
public sealed class LocalizationService : INotifyPropertyChanged
{
    public static LocalizationService Instance { get; } = new();
    private LocalizationService() { }

    readonly Dictionary<string, Dictionary<string, string>> _builtin =
        new(StringComparer.OrdinalIgnoreCase);

    // 语言包（code → 词条表 / 解析出的包）。同 code 多包时由 LanguagePackService.Resolve 决定。
    readonly Dictionary<string, Dictionary<string, string>> _packTables =
        new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, LanguagePackEntry> _packEntries =
        new(StringComparer.OrdinalIgnoreCase);

    List<string> _available = new() { "zh", "en" };
    readonly Dictionary<string, string> _nativeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zh"] = "中文", ["en"] = "English"
    };

    Dictionary<string, string> _override = new(StringComparer.Ordinal);
    List<Dictionary<string, string>> _chain = new();

    private string _currentLang = "zh";
    private bool _initialized;
    private bool _loading;

    /// <summary>内置 zh/en + 已安装并启用的语言包 code。</summary>
    public IReadOnlyList<string> AvailableLanguages => _available;

    /// <summary>语言 → 该语言自己的写法（内置写死，语言包读 manifest）。</summary>
    public IReadOnlyDictionary<string, string> NativeLanguageNames => _nativeNames;

    public string CurrentLanguage
    {
        get => _currentLang;
        set
        {
            if (!TrySetLanguage(value) && !string.Equals(_currentLang, value, StringComparison.OrdinalIgnoreCase))
                DzTrace.Always($"[i18n] unsupported language ignored: {value}");
        }
    }

    private void EnsureLoaded()
    {
        if (_initialized || _loading) return;
        _loading = true;
        try
        {
            var i18nDir = GetI18nDir();
            _builtin["zh"] = LoadFromDisk(Path.Combine(i18nDir, "source.zh.json"));
            _builtin["en"] = LoadFromDisk(Path.Combine(i18nDir, "source.en.json"));
            _override = LoadAppDataOverrides();
            LanguagePackService.Instance.Load();
            ApplyPacks();
            RebuildChain();
        }
        finally
        {
            _loading = false;
            _initialized = true;
        }
    }

    public string this[string key]
    {
        get
        {
            EnsureLoaded();
            if (_override.TryGetValue($"{_currentLang}.{key}", out var ov) && ov.Length > 0)
                return ov;
            foreach (var table in _chain)
            {
                if (table.TryGetValue(key, out var value) && value.Length > 0) return value;
            }
            return key;
        }
    }

    public string Get(string key, params object[] args) => string.Format(this[key], args);

    [Obsolete("Use SetLanguage instead")]
    public void ToggleLanguage() => SetLanguage(_currentLang == "zh" ? "en" : "zh");

    /// <summary>切换语言（语言包列表里的 code 也接受）。不支持时抛异常。</summary>
    public void SetLanguage(string lang)
    {
        EnsureLoaded();
        if (!TrySetLanguage(lang))
            throw new ArgumentException($"Unsupported language: {lang}");
    }

    /// <summary>切换语言；不支持的 code 返回 false（启动时读旧配置用）。</summary>
    public bool TrySetLanguage(string lang)
    {
        if (string.IsNullOrWhiteSpace(lang)) return false;
        EnsureLoaded();
        var match = _available.FirstOrDefault(code =>
            string.Equals(code, lang, StringComparison.OrdinalIgnoreCase));
        if (match == null) return false;
        if (string.Equals(match, _currentLang, StringComparison.Ordinal)) return true;

        _currentLang = match;
        // 切语言时重读用户覆盖（P0 行为：改完 lang\<code>.json 不用重启）。
        _override = LoadAppDataOverrides();
        RebuildChain();
        OnPropertyChanged(nameof(CurrentLanguage));
        OnPropertyChanged("Item[]");
        try { LanguagePackService.Instance.MarkChosen(match); } catch { }
        LanguageChanged?.Invoke(match);
        return true;
    }

    // ── 语言包 ──

    /// <summary>
    /// 语言包安装/卸载/停用后调用：重建包表与可用语言列表。
    /// 当前语言若已不存在 → 回退 zh 并触发 <see cref="LanguageFallback"/>。
    /// </summary>
    public void ReloadLanguagePacks(bool notifyFallback = true)
    {
        EnsureLoaded();
        var previous = _currentLang;
        var fallback = ApplyPacks();
        RebuildChain();
        OnPropertyChanged(nameof(CurrentLanguage));
        OnPropertyChanged("Item[]");
        if (fallback != null)
        {
            OnPropertyChanged(nameof(AvailableLanguages));
            OnPropertyChanged(nameof(NativeLanguageNames));
            if (notifyFallback) LanguageFallback?.Invoke(previous, fallback);
        }
        LanguagesChanged?.Invoke();
        LanguageChanged?.Invoke(_currentLang);
    }

    /// <summary>某语言当前生效的语言包（内置 zh/en 且没装覆盖包时为 null）。</summary>
    public LanguagePackEntry? GetLanguagePack(string code)
    {
        EnsureLoaded();
        return _packEntries.TryGetValue(code, out var entry) ? entry : null;
    }

    public double? GetLanguageCoverage(string code) => GetLanguagePack(code)?.Coverage;

    public bool IsPackLanguage(string code)
    {
        EnsureLoaded();
        return _packEntries.ContainsKey(code);
    }

    /// <summary>设置页/托盘显示用：<c>日本語 · 92% · @haru-dev</c>。</summary>
    public string GetLanguageDisplay(string code)
    {
        EnsureLoaded();
        var name = _nativeNames.TryGetValue(code, out var native) ? native : code;
        var entry = GetLanguagePack(code);
        if (entry == null) return name;
        var author = string.IsNullOrWhiteSpace(entry.AuthorGithub)
            ? entry.AuthorName
            : "@" + entry.AuthorGithub;
        return string.IsNullOrWhiteSpace(author)
            ? $"{name} · {entry.CoveragePercent}%"
            : $"{name} · {entry.CoveragePercent}% · {author}";
    }

    // ── 内部：包表 ──

    /// <returns>当前语言因包消失而回退到的 code；没有回退时 null。</returns>
    string? ApplyPacks()
    {
        _packTables.Clear();
        _packEntries.Clear();
        var service = LanguagePackService.Instance;
        foreach (var entry in service.Entries)
        {
            if (_packEntries.ContainsKey(entry.Code)) continue;
            var resolved = service.Resolve(entry.Code);
            if (resolved == null) continue;
            var table = service.GetTable(entry.Code);
            if (table == null) continue;
            _packEntries[entry.Code] = resolved;
            _packTables[entry.Code] = table;
        }

        var list = new List<string> { "zh", "en" };
        foreach (var code in _packEntries.Keys.OrderBy(c => c, StringComparer.OrdinalIgnoreCase))
        {
            if (!list.Contains(code, StringComparer.OrdinalIgnoreCase)) list.Add(code);
        }
        _available = list;
        _nativeNames.Clear();
        _nativeNames["zh"] = "中文";
        _nativeNames["en"] = "English";
        foreach (var pair in _packEntries)
        {
            // 内置 zh/en 的显示名固定为「中文 / English」，语言包覆盖它们时只改词条、不改名字。
            if (pair.Key is "zh" or "en") continue;
            _nativeNames[pair.Key] = pair.Value.NativeName;
        }

        string? fallback = null;
        if (!_available.Contains(_currentLang, StringComparer.OrdinalIgnoreCase))
        {
            fallback = "zh";
            _currentLang = fallback;
        }
        return fallback;
    }

    void RebuildChain()
    {
        var chain = new List<Dictionary<string, string>>();
        void Add(Dictionary<string, string>? table)
        {
            if (table == null || table.Count == 0) return;
            if (chain.Any(t => ReferenceEquals(t, table))) return;
            chain.Add(table);
        }

        _packTables.TryGetValue(_currentLang, out var pack);
        Add(pack);
        _builtin.TryGetValue(_currentLang, out var builtin);
        Add(builtin);

        var baseCode = _packEntries.TryGetValue(_currentLang, out var entry) ? entry.Base : "en";
        if (!string.IsNullOrWhiteSpace(baseCode)
            && !string.Equals(baseCode, _currentLang, StringComparison.OrdinalIgnoreCase))
        {
            _packTables.TryGetValue(baseCode, out var basePack);
            Add(basePack);
            _builtin.TryGetValue(baseCode, out var baseBuiltin);
            Add(baseBuiltin);
        }

        _builtin.TryGetValue("en", out var english);
        Add(english);
        _builtin.TryGetValue("zh", out var chinese);
        Add(chinese);
        _chain = chain;
    }

    // ── 其它 ──

    public void Reload()
    {
        _initialized = false;
        _builtin.Clear();
        _packTables.Clear();
        _packEntries.Clear();
        _chain.Clear();
        _override.Clear();
        EnsureLoaded();
        OnPropertyChanged("Item[]");
        LanguagesChanged?.Invoke();
        LanguageChanged?.Invoke(_currentLang);
    }

    public event Action<string>? LanguageChanged;
    /// <summary>可用语言列表变化（安装/卸载语言包）。</summary>
    public event Action? LanguagesChanged;
    /// <summary>当前语言包被移除，界面已回退。参数：(原 code, 新 code)。</summary>
    public event Action<string, string>? LanguageFallback;
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    // ── helpers ──
    public static Dictionary<string, string> LoadFromDiskForTest(string path)
        => LoadFromDisk(path);

    /// <summary>语言包校验用的宿主词条（zh ∪ en，值取 zh 优先）。</summary>
    internal static Dictionary<string, string> ReadHostSource()
    {
        var i18nDir = GetI18nDir();
        var source = LoadFromDisk(Path.Combine(i18nDir, "source.zh.json"));
        foreach (var pair in LoadFromDisk(Path.Combine(i18nDir, "source.en.json")))
        {
            if (!source.ContainsKey(pair.Key)) source[pair.Key] = pair.Value;
        }
        return source;
    }

    internal static string GetI18nDir()
    {
        // 1. exe 目录/i18n (发布时 JSON 跟随 exe)
        var exeDir = AppContext.BaseDirectory;
        var localI18n = Path.Combine(exeDir, "i18n");
        if (Directory.Exists(localI18n)) return localI18n;

        // 2. 项目根/i18n (开发时)
        var devI18n = Path.GetFullPath(Path.Combine(exeDir, "..", "..", "..", "..", "i18n"));
        if (Directory.Exists(devI18n)) return devI18n;

        return localI18n; // 兜底 (虽然不存在)
    }

    internal static Dictionary<string, string> LoadFromDisk(string path)
    {
        var dict = new Dictionary<string, string>();
        if (!File.Exists(path)) return dict;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Name.StartsWith("_")) continue;
                dict[prop.Name] = prop.Value.GetString() ?? "";
            }
        }
        catch (JsonException) { /* skip corrupt file */ }
        return dict;
    }

    private static Dictionary<string, string> LoadAppDataOverrides()
    {
        var dict = new Dictionary<string, string>();
        // ponytail 2026-08-29: 落点随 DataLocator(AppData / 便携 Data)。
        var langDir = Path.Combine(DataLocator.Root, "lang");
        if (!Directory.Exists(langDir)) return dict;
        foreach (var file in Directory.EnumerateFiles(langDir, "*.json"))
        {
            var lang = Path.GetFileNameWithoutExtension(file);
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Name.StartsWith("_")) continue;
                    dict[$"{lang}.{prop.Name}"] = prop.Value.GetString() ?? "";
                }
            }
            catch (JsonException) { /* skip corrupt file */ }
        }
        return dict;
    }
}
