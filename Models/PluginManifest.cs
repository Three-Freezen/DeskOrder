using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DeskOrder.PluginApi;
using DesktopZones.Helpers;

namespace DesktopZones.Models;

/// <summary>插件类型。类型决定「是否执行代码 / 是否需重启 / 是否可撤销」。</summary>
public enum PluginKind
{
    Widget,
    ZoneBar,
    Appearance,
    Language,
    Tool,
    Integration
}

public static class PluginKinds
{
    public static bool TryParse(string? value, out PluginKind kind)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "widget": kind = PluginKind.Widget; return true;
            case "zonebar": kind = PluginKind.ZoneBar; return true;
            case "appearance": kind = PluginKind.Appearance; return true;
            case "language": kind = PluginKind.Language; return true;
            case "tool": kind = PluginKind.Tool; return true;
            case "integration": kind = PluginKind.Integration; return true;
            default: kind = PluginKind.Widget; return false;
        }
    }

    public static string ToId(this PluginKind kind) => kind switch
    {
        PluginKind.Widget => "widget",
        PluginKind.ZoneBar => "zonebar",
        PluginKind.Appearance => "appearance",
        PluginKind.Language => "language",
        PluginKind.Tool => "tool",
        PluginKind.Integration => "integration",
        _ => "widget"
    };

    /// <summary>代码类（加载 DLL）→ true；声明式（纯数据）→ false。</summary>
    public static bool IsCode(this PluginKind kind)
        => kind is PluginKind.Widget or PluginKind.ZoneBar or PluginKind.Tool or PluginKind.Integration;

    public static string I18nKey(this PluginKind kind) => kind switch
    {
        PluginKind.Widget => "Plugin.Type.Widget",
        PluginKind.ZoneBar => "Plugin.Type.ZoneBar",
        PluginKind.Appearance => "Plugin.Type.Appearance",
        PluginKind.Language => "Plugin.Type.Language",
        PluginKind.Tool => "Plugin.Type.Tool",
        PluginKind.Integration => "Plugin.Type.Integration",
        _ => "Plugin.Type.Widget"
    };

    public static string FilterI18nKey(this PluginKind kind) => kind switch
    {
        PluginKind.Widget => "Plugin.Filter.Widget",
        PluginKind.ZoneBar => "Plugin.Filter.ZoneBar",
        PluginKind.Appearance => "Plugin.Filter.Appearance",
        PluginKind.Language => "Plugin.Filter.Language",
        PluginKind.Tool => "Plugin.Filter.Tool",
        PluginKind.Integration => "Plugin.Filter.Integration",
        _ => "Plugin.Filter.Widget"
    };
}

/// <summary>多语言短文本（manifest / registry 共用）。</summary>
public sealed class LocalizedText
{
    [JsonPropertyName("zh")] public string? Zh { get; set; }
    [JsonPropertyName("en")] public string? En { get; set; }

    public string Get(string lang)
    {
        if (string.Equals(lang, "zh", StringComparison.OrdinalIgnoreCase))
            return !string.IsNullOrWhiteSpace(Zh) ? Zh! : (En ?? "");
        return !string.IsNullOrWhiteSpace(En) ? En! : (Zh ?? "");
    }

    public bool HasAny => !string.IsNullOrWhiteSpace(Zh) || !string.IsNullOrWhiteSpace(En);

    public override string ToString() => Get("zh");
}

public sealed class PluginAuthor
{
    public string Name { get; set; } = "";
    public string Github { get; set; } = "";
    public string? Url { get; set; }
}

public sealed class PluginEntry
{
    /// <summary>包内相对路径，如 <c>lib/Weather.dll</c>。</summary>
    public string Assembly { get; set; } = "";

    /// <summary>实现 <see cref="IDeskOrderPlugin"/> 的完整类型名。</summary>
    public string Type { get; set; } = "";
}

public sealed class PluginWidgetDeclaration
{
    public string Id { get; set; } = "";
    public LocalizedText Name { get; set; } = new();
    public double DefaultWidth { get; set; } = 320;
    public double DefaultHeight { get; set; } = 180;
    public string? Icon { get; set; }
}

public sealed class PluginZoneBarDeclaration
{
    public string Id { get; set; } = "";
    public LocalizedText Name { get; set; } = new();
    public string? Icon { get; set; }
}

public sealed class PluginLanguageDeclaration
{
    public string Code { get; set; } = "";
    public string NativeName { get; set; } = "";
    public string Base { get; set; } = "en";
    public double Coverage { get; set; }
}

public sealed class PluginPresetDeclaration
{
    /// <summary>zone / panel / sticky / clock / calendar / dock / media。</summary>
    public string Kind { get; set; } = "";
    public string File { get; set; } = "";
}

public sealed class PluginPayload
{
    public List<string>? Materials { get; set; }
    public List<PluginPresetDeclaration>? Presets { get; set; }

    /// <summary>声明式单文件载荷（语言包的 <c>i18n/ja.json</c>）。</summary>
    public string? File { get; set; }
}

/// <summary><c>plugin.json</c> 的 POCO + 校验。</summary>
public sealed class PluginManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = "";
    public LocalizedText Name { get; set; } = new();
    public LocalizedText Summary { get; set; } = new();
    public LocalizedText? Description { get; set; }
    public LocalizedText? Changelog { get; set; }
    public string Version { get; set; } = "";
    public PluginAuthor Author { get; set; } = new();
    public string Repo { get; set; } = "";
    public string License { get; set; } = "";
    public string? Homepage { get; set; }
    public string Kind { get; set; } = "widget";
    public int ApiVersion { get; set; }
    public string MinAppVersion { get; set; } = "1.0.0";
    public PluginEntry? Entry { get; set; }
    public List<PluginWidgetDeclaration>? Widgets { get; set; }
    public PluginZoneBarDeclaration? ZoneBar { get; set; }
    public PluginLanguageDeclaration? Language { get; set; }
    public PluginPayload? Payload { get; set; }
    public List<string>? Permissions { get; set; }
    public string Icon { get; set; } = "icon.png";
    public List<string>? Previews { get; set; }

    [JsonIgnore] public PluginKind KindValue => PluginKinds.TryParse(Kind, out var k) ? k : PluginKind.Widget;

    // internal：registry（PluginCatalogService）必须用同一套规则校验 id / version，
    // 否则市场卡片能展示一条 manifest 装不上的条目（点安装才发现）。
    internal static readonly Regex IdRe = new("^[a-z0-9][a-z0-9.-]{2,63}$", RegexOptions.Compiled);
    internal static readonly Regex SemVerRe = new(@"^\d+\.\d+\.\d+([-+][0-9A-Za-z.-]+)?$", RegexOptions.Compiled);
    static readonly Regex RepoRe = new(@"^[A-Za-z0-9][A-Za-z0-9-]*/[A-Za-z0-9._-]+$", RegexOptions.Compiled);
    static readonly Regex LangRe = new(@"^[a-z]{2,3}(-[A-Za-z0-9]{2,8})*$", RegexOptions.Compiled);

    /// <summary>返回全部错误；空列表 = 通过。</summary>
    public List<string> Validate(Version hostVersion, bool forLocalImport)
    {
        var errors = new List<string>();
        void Err(string msg) => errors.Add(msg);

        if (SchemaVersion != 1) Err("schemaVersion must be 1");
        if (!IdRe.IsMatch(Id ?? "")) Err("id is not a valid plugin id");
        if (!Name.HasAny) Err("name.zh/en is required");
        if (!Summary.HasAny) Err("summary.zh/en is required");
        else if (Summary.Get("zh").Length > 80) Err("summary is longer than 80 characters");
        if (!SemVerRe.IsMatch(Version ?? "")) Err("version is not SemVer");
        // 版本号会成为安装目录名：尾随点/双点在 Win32 下会被规范化，
        // 目录名与记录里的版本对不上，更新时会把自己刚装好的目录删掉。
        else if (Version.EndsWith('.') || Version.Contains(".."))
            Err("version must not end with a dot or contain '..'");
        if (string.IsNullOrWhiteSpace(Author?.Name)) Err("author.name is required");
        if (string.IsNullOrWhiteSpace(Author?.Github)) Err("author.github is required");
        if (!RepoRe.IsMatch(Repo ?? "")) Err("repo must be owner/name");
        if (string.IsNullOrWhiteSpace(License)) Err("license is required");
        if (!PluginKinds.TryParse(Kind, out var kind)) Err("kind is invalid");
        if (!SemVerRe.IsMatch(MinAppVersion ?? "")) Err("minAppVersion is not SemVer");
        else if (!PluginVersion.HostSatisfies(MinAppVersion, hostVersion))
            Err($"requires DeskOrder {MinAppVersion} or later");

        if (PluginKinds.TryParse(Kind, out var parsedKind))
        {
            if (parsedKind.IsCode())
            {
                if (ApiVersion < 1 || ApiVersion > PluginApiVersion.Current)
                    Err($"apiVersion {ApiVersion} is not supported (host supports 1)");
                if (Entry == null) Err("entry is required for code plugins");
                else
                {
                    if (!PluginPaths.IsSafeRelativePath(Entry.Assembly))
                        Err("entry.assembly must be a relative path inside the package");
                    if (string.IsNullOrWhiteSpace(Entry.Type)) Err("entry.type is required");
                }
                if (parsedKind == PluginKind.Widget)
                {
                    if (Widgets == null || Widgets.Count == 0) Err("widgets must declare at least one widget");
                    else if (Widgets.Count > 8) Err("widgets may declare at most 8 widgets");
                    else if (Widgets.Any(w => string.IsNullOrWhiteSpace(w.Id)))
                        Err("widgets[].id is required");
                    else if (Widgets.Select(w => w.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Widgets.Count)
                        Err("widgets[].id must be unique");
                }
                else if (parsedKind == PluginKind.ZoneBar)
                {
                    if (ApiVersion < 2) Err("zonebar plugins require apiVersion 2");
                    if (ZoneBar == null) Err("zoneBar is required for zonebar plugins");
                    else
                    {
                        if (string.IsNullOrWhiteSpace(ZoneBar.Id)) Err("zoneBar.id is required");
                        else if (!IdRe.IsMatch(ZoneBar.Id)) Err("zoneBar.id is invalid");
                        if (!ZoneBar.Name.HasAny) Err("zoneBar.name.zh/en is required");
                        if (!string.IsNullOrWhiteSpace(ZoneBar.Icon) && !PluginPaths.IsSafeRelativePath(ZoneBar.Icon))
                            Err("zoneBar.icon must be a relative path inside the package");
                    }
                }
            }
            else
            {
                if (ApiVersion != 0) Err("declarative packages must use apiVersion 0");
                if (parsedKind == PluginKind.Language)
                {
                    if (Language == null) Err("language is required for language packs");
                    else
                    {
                        if (!LangRe.IsMatch(Language.Code ?? "")) Err("language.code is not a BCP-47 code");
                        if (string.IsNullOrWhiteSpace(Language.NativeName)) Err("language.nativeName is required");
                        if (Language.Coverage < 0 || Language.Coverage > 1) Err("language.coverage must be 0..1");
                    }
                    if (string.IsNullOrWhiteSpace(Payload?.File)) Err("payload.file is required for language packs");
                    else if (!PluginPaths.IsSafeRelativePath(Payload!.File!)) Err("payload.file is not a safe relative path");
                }
                else if (parsedKind == PluginKind.Appearance)
                {
                    if ((Payload?.Materials?.Count ?? 0) == 0 && (Payload?.Presets?.Count ?? 0) == 0)
                        Err("payload must contain materials or presets");
                }
            }
        }

        if (string.IsNullOrWhiteSpace(Icon) || !PluginPaths.IsSafeRelativePath(Icon))
            Err("icon must be a relative path inside the package");

        if (Previews?.Any(p => !PluginPaths.IsSafeRelativePath(p)) ?? false)
            Err("previews[] must be relative paths");
        return errors;
    }
}

/// <summary>已安装插件记录（持久化到 <c>AppConfig.Plugins</c>）。</summary>
public sealed class PluginRecord
{
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    public string Kind { get; set; } = "widget";
    public bool Enabled { get; set; }
    public bool Official { get; set; }
    /// <summary>market / local / dev。</summary>
    public string Source { get; set; } = "market";
    public string Sha256 { get; set; } = "";
    /// <summary>安装时的签名状态：verified / unsigned / invalid。</summary>
    public string Signature { get; set; } = "unsigned";
    /// <summary>签发者公钥 id（8 字节十六进制），用于在详情页显示「由哪把钥匙签的」。</summary>
    public string? SignedBy { get; set; }
    public DateTime InstalledAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoadedUtc { get; set; }
    /// <summary>连续加载失败次数（≥2 自动禁用）。</summary>
    public int CrashCount { get; set; }

    [JsonIgnore] public PluginKind KindValue => PluginKinds.TryParse(Kind, out var k) ? k : PluginKind.Widget;
}

// ── registry（市场索引） ──

public sealed class PluginCatalogFile
{
    public int SchemaVersion { get; set; } = 1;
    public DateTime UpdatedAt { get; set; }
    public List<PluginCatalogEntry> Plugins { get; set; } = new();

    /// <summary>
    /// 紧急禁用名单。只增不减地放在 registry 顶层：应用拉到就本地落盘，
    /// 之后即使断网、registry 又被改回去，禁用状态也不会自己消失。
    /// </summary>
    public List<BlockedPlugin>? Blocked { get; set; }
}

/// <summary>一条禁用记录。<c>Version</c> 为空表示禁用该插件的所有版本。</summary>
public sealed class BlockedPlugin
{
    public string Id { get; set; } = "";
    public string? Version { get; set; }
    public LocalizedText? Reason { get; set; }
    public DateTime? Since { get; set; }

    /// <summary>
    /// 版本比对。registry 与 manifest 的版本串不一定字面一致（<c>1.0.0</c> / <c>1.0.00</c> /
    /// <c>1.0.0+build</c> / <c>v1.0.0</c>），按 <see cref="Version"/> 解析后比较，
    /// 解析不了再退回序数比较——不能让改写一个字符就绕过禁用。
    /// </summary>
    public bool Matches(string id, string version)
    {
        if (!string.Equals(Id, id, StringComparison.OrdinalIgnoreCase)) return false;
        var blockedVersion = Version?.Trim();
        if (string.IsNullOrEmpty(blockedVersion)) return true;

        var left = ParseLoose(blockedVersion);
        var right = ParseLoose(version);
        if (left != null && right != null) return left == right;
        return string.Equals(blockedVersion, version?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>宽松解析：去掉 <c>v</c> 前缀与 <c>+build</c> 元数据，只留数字段。</summary>
    internal static Version? ParseLoose(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var text = raw.Trim();
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];
        var plus = text.IndexOf('+');
        if (plus > 0) text = text[..plus];
        var dash = text.IndexOf('-');
        if (dash > 0) text = text[..dash];
        return System.Version.TryParse(text, out var version) ? version : null;
    }
}


/// <summary>registry.json 里的一条（市场卡片数据，不含包内 manifest 细节）。</summary>
public sealed class PluginCatalogEntry
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "widget";
    public LocalizedText Name { get; set; } = new();
    public LocalizedText Summary { get; set; } = new();
    public LocalizedText? Description { get; set; }
    public PluginAuthor Author { get; set; } = new();
    public string Repo { get; set; } = "";
    public string Version { get; set; } = "";
    public int ApiVersion { get; set; }
    public string MinAppVersion { get; set; } = "";
    public string License { get; set; } = "";
    public List<string>? Tags { get; set; }
    public string? IconUrl { get; set; }
    public List<string>? PreviewUrls { get; set; }
    public string DownloadUrl { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    public long Downloads { get; set; }
    public DateTime? PublishedAt { get; set; }
    public List<string>? Permissions { get; set; }
    public PluginLanguageDeclaration? Language { get; set; }

    /// <summary>这个版本改了什么（更新弹窗展示）。</summary>
    public LocalizedText? Changelog { get; set; }

    /// <summary>内联签名：整个 <c>.minisig</c> 文件内容（优先于 <see cref="SignatureUrl"/>）。</summary>
    public string? Signature { get; set; }

    /// <summary>签名文件地址（Release 资产里的 <c>&lt;id&gt;-&lt;version&gt;.dopkg.minisig</c>）。</summary>
    public string? SignatureUrl { get; set; }

    [JsonIgnore] public PluginKind KindValue => PluginKinds.TryParse(Kind, out var k) ? k : PluginKind.Widget;

    /// <summary>官方判定：作者 GitHub 账号 + 仓库归属都必须是 Three-Freezen（离线规则，不请求 API）。</summary>
    [JsonIgnore]
    public bool Official =>
        string.Equals(Author?.Github?.Trim(), "Three-Freezen", StringComparison.OrdinalIgnoreCase)
        && Repo.StartsWith("Three-Freezen/", StringComparison.OrdinalIgnoreCase);
}
