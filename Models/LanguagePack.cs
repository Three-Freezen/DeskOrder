using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DesktopZones.Models;

/// <summary>
/// 已安装语言包的索引条目（<c>Plugins\.state\languages.json</c>）。
/// 索引是缓存：每次启动按 <see cref="PluginRecord"/> 重新校验生成，手改索引不会生效。
/// </summary>
public sealed class LanguagePackEntry
{
    /// <summary>BCP-47，如 <c>ja</c> / <c>zh-Hant</c>。</summary>
    public string Code { get; set; } = "";

    /// <summary>该语言自己的写法（<c>日本語</c>）。</summary>
    public string NativeName { get; set; } = "";

    /// <summary>缺词条时的回退语言，默认 <c>en</c>。</summary>
    public string Base { get; set; } = "en";

    public string PackId { get; set; } = "";
    public string Version { get; set; } = "";

    /// <summary>实际覆盖率（宿主按词条重算，不信任 manifest 声明）。</summary>
    public double Coverage { get; set; }

    /// <summary>manifest 里声明的覆盖率（仅用于展示偏差）。</summary>
    public double DeclaredCoverage { get; set; }

    /// <summary>相对 <c>Plugins</c> 根目录的 payload 路径（正斜杠，便于便携模式整体搬移）。</summary>
    public string Path { get; set; } = "";

    public string AuthorName { get; set; } = "";
    public string AuthorGithub { get; set; } = "";
    public bool Official { get; set; }
    public DateTime InstalledAtUtc { get; set; } = DateTime.UtcNow;

    [JsonIgnore] public int CoveragePercent => (int)Math.Round(Coverage * 100);
}

/// <summary><c>languages.json</c> 的磁盘格式。</summary>
public sealed class LanguagePackIndexFile
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>每种语言上次被用户选中时生效的包。用于「同 code 多包」时保持稳定。</summary>
    public Dictionary<string, string> Chosen { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public List<LanguagePackEntry> Packs { get; set; } = new();
}
