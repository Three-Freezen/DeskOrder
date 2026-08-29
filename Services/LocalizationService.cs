using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace DesktopZones.Services;

public sealed class LocalizationService : INotifyPropertyChanged
{
    public static LocalizationService Instance { get; } = new();
    private LocalizationService() { }

    public IReadOnlyList<string> AvailableLanguages { get; } = new[] { "zh", "en" };

    public IReadOnlyDictionary<string, string> NativeLanguageNames { get; } =
        new Dictionary<string, string>
        {
            ["zh"] = "中文", ["en"] = "English"
        };

    private string _currentLang = "zh";
    public string CurrentLanguage
    {
        get => _currentLang;
        set
        {
            if (_currentLang == value) return;
            _currentLang = value;
            ReloadOverride();
            OnPropertyChanged();
            OnPropertyChanged("Item[]");
            LanguageChanged?.Invoke(value);
        }
    }

    private readonly Dictionary<string, Dictionary<string, string>> _tables = new();
    private bool _initialized;

    private void EnsureLoaded()
    {
        if (_initialized) return;
        _initialized = true;
        var i18nDir = GetI18nDir();
        _tables["zh"] = LoadFromDisk(Path.Combine(i18nDir, "source.zh.json"));
        _tables["en"] = LoadFromDisk(Path.Combine(i18nDir, "source.en.json"));
        _tables["override"] = LoadAppDataOverrides();
    }

    public string this[string key]
    {
        get
        {
            EnsureLoaded();
            if (_tables.TryGetValue("override", out var ov)
                && ov.TryGetValue($"{_currentLang}.{key}", out var ovv))
                return ovv;
            if (_tables.TryGetValue(_currentLang, out var t)
                && t.TryGetValue(key, out var v)) return v;
            if (_tables.TryGetValue("en", out var e)
                && e.TryGetValue(key, out var ev)) return ev;
            if (_tables.TryGetValue("zh", out var z)
                && z.TryGetValue(key, out var zv)) return zv;
            return key;
        }
    }

    public string Get(string key, params object[] args) => string.Format(this[key], args);

    [Obsolete("Use SetLanguage instead")]
    public void ToggleLanguage() => SetLanguage(_currentLang == "zh" ? "en" : "zh");

    public void SetLanguage(string lang)
    {
        if (!AvailableLanguages.Contains(lang))
            throw new ArgumentException($"Unsupported language: {lang}");
        CurrentLanguage = lang;
    }

    public void Reload()
    {
        _initialized = false;
        _tables.Clear();
        EnsureLoaded();
        OnPropertyChanged("Item[]");
        LanguageChanged?.Invoke(_currentLang);
    }

    private void ReloadOverride()
    {
        EnsureLoaded();
        _tables["override"] = LoadAppDataOverrides();
    }

    public event Action<string>? LanguageChanged;
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    // ── helpers ──
    public static Dictionary<string, string> LoadFromDiskForTest(string path)
        => LoadFromDisk(path);

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
