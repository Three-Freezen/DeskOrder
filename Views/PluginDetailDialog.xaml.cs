using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopZones.Helpers;
using DesktopZones.Models;
using DesktopZones.Services;

namespace DesktopZones.Views;

public partial class PluginDetailDialog : Window
{
    static readonly LocalizationService Loc = LocalizationService.Instance;

    /// <summary>详情页最多轮播几张预览图：再多也没人翻，而且每张都要下载。</summary>
    const int MaxPreviewImages = 8;

    /// <summary>外观包详情里材质 / 预设名称各最多列几个：超出用「…」收尾，避免详情页变成清单。</summary>
    const int MaxListedAppearanceNames = 8;

    /// <summary>从 Release 标签里抠出版本号的宽松匹配：<c>v1.2.3</c> / <c>plugin-1.2.3</c> / <c>release/v1.2.3-beta</c>。</summary>
    static readonly Regex VersionInTagRe = new(
        @"(?<![0-9A-Za-z])v?[0-9]+(?:\.[0-9]+){1,3}(?:-[0-9A-Za-z.-]+)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public PluginCatalogEntry Entry { get; }
    public PluginRecord? Record { get; private set; }

    public event Action? InstallRequested;
    public event Action? UninstallRequested;
    public event Action? ToggleRequested;
    /// <summary>点「新建组件」：由调用方打开共用的创建菜单（菜单需要页面级锚点与刷新回调）。</summary>
    public event Action? NewWidgetRequested;

    bool _busy;
    readonly bool _delisted;

    // ── 预览图轮播状态 ──
    readonly List<string> _previewUrls = new();
    int _previewIndex;
    bool _previewBusy;
    bool _closed;

    public PluginDetailDialog(PluginCatalogEntry entry, PluginRecord? record, bool delisted = false)
    {
        InitializeComponent();
        Entry = entry;
        Record = record;
        _delisted = delisted;
        Apply();
        InitPreviewUrls();
        Loaded += async (_, _) => await LoadImagesAsync();
    }

    /// <summary>自测台只读访问器：可轮播的预览图张数（0..8）。</summary>
    internal int PreviewCountForLab => _previewUrls.Count;

    /// <summary>自测台只读访问器：「内容/能力」区是否显示（只有多类型包才显示）。</summary>
    internal bool CapabilitiesVisibleForLab => CapabilitiesBlock.Visibility == Visibility.Visible;

    /// <summary>自测台只读访问器：「内容/能力」区的最终文本。</summary>
    internal string CapabilitiesTextForLab => CapabilitiesText.Text;

    /// <summary>自测台只读访问器：左右箭头 / 计数是否显示（>1 张才显示）。</summary>
    internal bool PreviewNavVisibleForLab => PreviewPrevButton.Visibility == Visibility.Visible;

    /// <summary>自测台只读访问器：计数文本（形如 "1 / 3"）。</summary>
    internal string PreviewCounterForLab => PreviewCountText.Text;

    /// <summary>自测台只读访问器：更新日志区块是否显示（无 changelog 时整块不占位）。</summary>
    internal bool ChangelogVisibleForLab => ChangelogBlock.Visibility == Visibility.Visible;

    /// <summary>自测台只读访问器：更新日志区块的最终文本。</summary>
    internal string ChangelogTextForLab => ChangelogText.Text;

    /// <summary>自测台只读访问器：更新日志标题（有更新时应点明新版本号）。</summary>
    internal string ChangelogTitleForLab => ChangelogTitle.Text;

    /// <summary>自测台只读访问器：「插件设置」按钮是否显示。</summary>
    internal bool SettingsVisibleForLab => SettingsButton.Visibility == Visibility.Visible;

    /// <summary>自测台只读访问器：「检查更新」按钮是否显示（仅 GitHub 直装的记录）。</summary>
    internal bool CheckUpdateVisibleForLab => CheckUpdateButton.Visibility == Visibility.Visible;

    /// <summary>自测台只读访问器：外观包详情里的材质 / 预设名称行（未显示时为空串）。</summary>
    internal string AppearanceNamesForLab => AppearanceNamesText.Text;

    /// <summary>自测台只读访问器：材质 / 预设名称行是否显示。</summary>
    internal bool AppearanceNamesVisibleForLab => AppearanceNamesText.Visibility == Visibility.Visible;

    public void SetRecord(PluginRecord? record)
    {
        Record = record;
        Apply();
    }

    void Apply()
    {
        NameText.Text = Entry.Name.Get(Loc.CurrentLanguage);
        // 官方标记以「安装时的记录」为准：本地包可以在 plugin.json 里自填
        // author.github/repo，但只有签名验过的才算官方（记录里带 Official）。
        var official = Record?.Official ?? Entry.Official;
        OfficialPill.Visibility = official ? Visibility.Visible : Visibility.Collapsed;
        // 每次重算先收起提示条：下面「禁用名单 / 已下架 / 不兼容」三类原因各自决定要不要再显示，
        // 否则切过一次禁用名单后、原因已解除时旧提示会一直挂在那儿。
        NoticeBar.Visibility = Visibility.Collapsed;
        var incompatible = PluginDisplay.IncompatibilityReason(Entry);
        var authorName = string.IsNullOrWhiteSpace(Entry.Author?.Github)
            ? (Entry.Author?.Name ?? "")
            : Loc.Get("Plugin.AuthorBy", Entry.Author!.Github);
        MetaText.Text = $"{PluginDisplay.DescribeKinds(Entry.KindValues)} · {authorName} · v{Entry.Version} · {Entry.License}";
        SummaryText.Text = Entry.Summary.Get(Loc.CurrentLanguage);
        var description = Entry.Description?.Get(Loc.CurrentLanguage);
        DescriptionText.Text = string.IsNullOrWhiteSpace(description) ? "" : description;
        DescriptionText.Visibility = string.IsNullOrWhiteSpace(description) ? Visibility.Collapsed : Visibility.Visible;
        ApplyChangelog();

        var permissions = Entry.Permissions;
        var described = PluginDisplay.DescribePermissions(permissions);
        PermissionsText.Text = string.IsNullOrEmpty(described)
            ? Loc["Plugin.Permissions.None"]
            : described;
        PermissionsBlock.Visibility = Entry.KindValues.Any(kind => kind.IsCode()) ? Visibility.Visible : Visibility.Collapsed;

        if (Entry.KindValue == PluginKind.Language && Entry.Language != null)
        {
            LanguageBlock.Visibility = Visibility.Visible;
            var installed = Record == null ? null : Loc.GetLanguagePack(Entry.Language.Code);
            var coverage = installed?.CoveragePercent ?? (int)Math.Round(Entry.Language.Coverage * 100);
            var text = $"{Entry.Language.NativeName} ({Entry.Language.Code}) · "
                       + Loc.Get("Plugin.Language.Coverage", coverage) + " · "
                       + Loc.Get("Plugin.Language.Base", Entry.Language.Base)
                       + " · " + Loc["Plugin.Language.Choose"];
            if (installed != null
                && string.Equals(Loc.CurrentLanguage, Entry.Language.Code, StringComparison.OrdinalIgnoreCase))
                text += " · " + Loc["Plugin.Language.Active"];
            LanguageText.Text = text;
        }
        else
        {
            LanguageBlock.Visibility = Visibility.Collapsed;
        }

        if (Entry.KindValue == PluginKind.Appearance)
        {
            AppearanceBlock.Visibility = Visibility.Visible;
            var summary = Record == null ? null : AppearancePackService.Instance.GetSummary(Record.Id);
            AppearanceText.Text = summary is { } counts
                ? Loc.Get("Plugin.Contents.Appearance", counts.Materials, counts.Presets, counts.Faces)
                : Loc["Plugin.Appearance.Hint"];
            // 已安装外观包：在数量下面列出具体材质 / 预设名称（读安装目录，纯本地）。
            var names = Record == null ? "" : DescribeAppearanceContents(Record);
            AppearanceNamesText.Text = names;
            AppearanceNamesText.Visibility = string.IsNullOrWhiteSpace(names)
                ? Visibility.Collapsed : Visibility.Visible;
        }
        else
        {
            AppearanceBlock.Visibility = Visibility.Collapsed;
        }

        ApplyCapabilities();

        if (Record == null)
        {
            PrimaryButton.Visibility = Visibility.Visible;
            ToggleButton.Visibility = Visibility.Collapsed;
            UninstallButton.Visibility = Visibility.Collapsed;
            NewWidgetButton.Visibility = Visibility.Collapsed;
            SettingsButton.Visibility = Visibility.Collapsed;
            CheckUpdateButton.Visibility = Visibility.Collapsed;
            StateText.Text = "";
            // 六类插件（含数据源）都能安装；不再有「后续版本」的占位按钮。
            PrimaryButton.Content = Loc["Plugin.Install"];
            // apiVersion / minAppVersion 不满足时提前置灰，别等点下去才在安装流程里报错。
            PrimaryButton.IsEnabled = incompatible == null;
            PrimaryButton.Opacity = incompatible == null ? 1.0 : 0.45;
            PrimaryButton.ToolTip = incompatible;
        }
        else
        {
            var update = IsUpdateAvailable(Record, Entry);
            PrimaryButton.Content = update ? Loc["Plugin.Update"] : Loc["Plugin.Install"];
            PrimaryButton.Visibility = update ? Visibility.Visible : Visibility.Collapsed;
            PrimaryButton.IsEnabled = update && incompatible == null;
            PrimaryButton.Opacity = PrimaryButton.IsEnabled ? 1.0 : 0.45;
            PrimaryButton.ToolTip = incompatible;
            ToggleButton.Content = Record.Enabled ? Loc["Plugin.Disable"] : Loc["Plugin.Enable"];
            ToggleButton.Visibility = Entry.KindValue is PluginKind.Widget or PluginKind.ZoneBar
                or PluginKind.Tool or PluginKind.Language or PluginKind.Appearance or PluginKind.Integration
            ? Visibility.Visible : Visibility.Collapsed;
            UninstallButton.Visibility = Visibility.Visible;
            UpdateNewWidgetButton();
            // 插件设置入口：只有「已启用 + 当前真的注册了 schema」才有可调的内容。
            SettingsButton.Visibility =
                Record.Enabled && PluginSettingsService.Instance.Find(Record.Id) != null
                    ? Visibility.Visible : Visibility.Collapsed;
            // 从 GitHub 直装的插件不在 registry 里，只能按仓库去查最新 Release。
            CheckUpdateButton.Visibility =
                string.Equals(Record.Source, "github", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(Record.Repo)
                    ? Visibility.Visible : Visibility.Collapsed;
            var source = PluginDisplay.DescribeSource(Record.Source);
            var widgets = PluginService.Instance.CountWidgets(Record.Id);
            var state = $"v{Record.Version} · {source} · {Record.InstalledAtUtc.ToLocalTime():yyyy-MM-dd}";
            if (Record.KindValues.Contains(PluginKind.Widget) && widgets > 0)
                state += " · " + Loc.Plural("Plugin.WidgetCount", widgets);
            if (PluginService.Instance.LoadErrors.TryGetValue(Record.Id, out var error))
                state += "\n" + error;
            StateText.Text = state;

            // 禁用名单：开关置灰，用户只能卸载，不能重新启用。
            if (PluginBlocklist.Find(Record.Id, Record.Version) is { } blocked)
            {
                ToggleButton.IsEnabled = false;
                ToggleButton.Opacity = 0.45;
                ShowNotice(Loc.Get("Plugin.Blocked.Banner",
                    PluginBlocklist.ReasonOf(blocked, Loc.CurrentLanguage)));
            }
            else if (_delisted)
            {
                // 条目从 registry 消失 = 已下架：文件保留、能用，但不再有更新。
                ShowNotice(Loc["Plugin.Delisted"]);
            }
        }

        // 每次重算都把「检查更新」文案复位：忙的时候它被改成「检查中…」，失败/取消后不能一直停在忙态。
        CheckUpdateButton.Content = Loc["Plugin.GitHub.CheckUpdate"];

        // 兼容性门禁：主按钮置灰 + 顶部说明原因。禁用名单 / 已下架的原因更严重也更具体，
        // 已经显示时不再让「需要更高版本」把它盖掉。
        if (incompatible != null && PrimaryButton.Visibility == Visibility.Visible
            && NoticeBar.Visibility != Visibility.Visible)
            ShowNotice(incompatible);

        // 签名状态：官方的必须签名，声明式可选。
        SignatureText.Text = DescribeSignature();
        SignatureText.Visibility = string.IsNullOrEmpty(SignatureText.Text)
            ? Visibility.Collapsed : Visibility.Visible;

        RepoText.Text = string.IsNullOrWhiteSpace(Entry.Repo) ? "" : $"github.com/{Entry.Repo}";
        GithubButton.IsEnabled = !string.IsNullOrWhiteSpace(Entry.Repo);
        GithubButton.Opacity = GithubButton.IsEnabled ? 1 : 0.4;
        ReportButton.IsEnabled = !string.IsNullOrWhiteSpace(Entry.Repo);
        ReportButton.Opacity = ReportButton.IsEnabled ? 1 : 0.4;
    }

    /// <summary>记录里有、但当前 registry 里已经没有这条 → 已下架。</summary>
    void ShowNotice(string text)
    {
        NoticeText.Text = text;
        NoticeBar.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// 更新日志区块：当前语言缺内容时 <see cref="LocalizedText.Get"/> 会回退另一语言；
    /// 两语都空就整块收起，不留一块空标题占位。
    /// </summary>
    void ApplyChangelog()
    {
        var text = Entry.Changelog?.Get(Loc.CurrentLanguage);
        if (string.IsNullOrWhiteSpace(text))
        {
            ChangelogBlock.Visibility = Visibility.Collapsed;
            return;
        }

        var update = Record != null && IsUpdateAvailable(Record, Entry);
        ChangelogTitle.Text = update
            ? Loc.Get("Plugin.Detail.Changelog.UpdateTitle", Entry.Version)
            : Loc["Plugin.Detail.Changelog"];
        ChangelogText.Text = text!.Trim();
        ChangelogBlock.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// 已安装外观包的材质 / 预设名称：从安装目录读 manifest + payload 再解析一遍。
    /// 纯本地、几毫秒；读不出来（包损坏 / 已卸载）返回空串，详情页不显示这一行。
    /// </summary>
    static string DescribeAppearanceContents(PluginRecord record)
    {
        try
        {
            var dir = PluginPaths.VersionDir(record.Id, record.Version);
            var manifest = PluginPackages.ReadManifest(dir, out _);
            if (manifest == null) return "";
            var validation = AppearancePackService.Instance.Validate(dir, manifest);
            if (!validation.Ok) return "";

            var chinese = Loc.CurrentLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
            var materials = validation.Materials
                .Select(recipe => chinese ? recipe.ZhName : recipe.EnName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!.Trim())
                .Take(MaxListedAppearanceNames)
                .ToList();
            var presets = validation.Presets
                .Select(preset =>
                {
                    try { return PresetService.For(preset.Kind).ValidateFile(preset.Path, out _)?.Name; }
                    catch { return null; }
                })
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!.Trim())
                .Take(MaxListedAppearanceNames)
                .ToList();

            var faces = validation.Faces
                .Select(face => chinese ? face.Name.Zh : face.Name.En)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!.Trim())
                .Take(MaxListedAppearanceNames)
                .ToList();

            var lines = new List<string>(3);
            if (materials.Count > 0)
                lines.Add(Loc.Get("Plugin.Appearance.Materials", string.Join(" · ", materials)));
            if (presets.Count > 0)
                lines.Add(Loc.Get("Plugin.Appearance.Presets", string.Join(" · ", presets)));
            if (faces.Count > 0)
                lines.Add(Loc.Get("Plugin.Appearance.Faces", string.Join(" · ", faces)));
            return string.Join("\n", lines);
        }
        catch
        {
            // 详情页是只读展示：读包失败不能让它打不开，静默不显示名称即可。
            return "";
        }
    }

    /// <summary>
    /// 已安装的 widget 插件才有「新建组件」：插件启用但描述符还没加载（装了没重启/加载失败）
    /// 时按钮置灰并提示重启 —— 菜单本身也只列已加载的描述符。
    /// </summary>
    void UpdateNewWidgetButton()
    {
        var descriptors = Record is { Enabled: true } && Record.KindValues.Contains(PluginKind.Widget)
            ? PluginService.Instance.GetDescriptors(Record.Id)
            : null;
        NewWidgetButton.Visibility = descriptors == null ? Visibility.Collapsed : Visibility.Visible;
        if (descriptors == null) return;
        var available = descriptors.Count > 0;
        NewWidgetButton.IsEnabled = available;
        NewWidgetButton.Opacity = available ? 1 : 0.45;
        NewWidgetButton.ToolTip = available
            ? Loc["PluginWidget.New"]
            : Loc["PluginWidget.NeedsRestart"];
    }

    /// <summary>
    /// 一个包多类型：列出这个包提供的每种能力。已安装且已加载时补运行时计数
    /// （组件类型 / 分区坞 / 工具命令 / 数据源），拿不到计数就只列类型名。
    /// 单类型包不显示这个区（MetaText 已经写明类型）。
    /// </summary>
    void ApplyCapabilities()
    {
        var kinds = Entry.KindValues;
        CapabilitiesBlock.Visibility = kinds.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (kinds.Count <= 1) return;

        var pluginId = Record?.Id ?? Entry.Id;
        var lines = new List<string>(kinds.Count);
        foreach (var kind in kinds)
        {
            var label = Loc[kind.I18nKey()];
            var count = CapabilityCount(kind, pluginId);
            lines.Add(count > 0 ? $"{label} · {CapabilityCountText(kind, count)}" : label);
        }
        CapabilitiesText.Text = string.Join("\n", lines);
    }

    /// <summary>运行时计数：插件没加载 / 没有注册这类能力时返回 0（只显示类型名）。</summary>
    static int CapabilityCount(PluginKind kind, string pluginId)
    {
        var service = PluginService.Instance;
        return kind switch
        {
            PluginKind.Widget => service.GetDescriptors(pluginId).Count,
            PluginKind.ZoneBar => service.GetLoaded(pluginId)?.BayRegistry.Descriptors.Count ?? 0,
            PluginKind.Tool => PluginToolService.Instance.Commands.Count(entry =>
                string.Equals(entry.PluginId, pluginId, StringComparison.OrdinalIgnoreCase)),
            PluginKind.Integration => PluginIntegrationService.Instance.MediaSources.Count(entry =>
                string.Equals(entry.PluginId, pluginId, StringComparison.OrdinalIgnoreCase)),
            _ => 0
        };
    }

    static string CapabilityCountText(PluginKind kind, int count) => kind switch
    {
        PluginKind.Widget => Loc.Get("Plugin.Contents.WidgetTypes", count),
        PluginKind.ZoneBar => Loc.Get("Plugin.Contents.ZoneBars", count),
        PluginKind.Tool => Loc.Get("Plugin.Contents.ToolCommands", count),
        PluginKind.Integration => Loc.Get("Plugin.Contents.MediaSources", count),
        _ => count.ToString()
    };

    string DescribeSignature()
    {
        var status = Record?.Signature ?? "unsigned";
        var required = PluginTrust.RequiresSignature(Entry.KindValue, Entry.Official);
        return status switch
        {
            "verified" => Loc.Get("Plugin.Signature.Verified", Record?.SignedBy ?? "—"),
            "invalid" => Loc["Plugin.Signature.Invalid"],
            _ when required => Loc["Plugin.Signature.Missing"],
            // 非官方的代码包没签名是常态，不必刷一行灰字。
            _ => Entry.KindValue.IsCode() ? "" : Loc["Plugin.Signature.DeclarativeOptional"]
        };
    }

    public static bool IsUpdateAvailable(PluginRecord record, PluginCatalogEntry entry)
        => PluginVersion.IsNewer(record.Version, entry.Version);

    // ── GitHub 直装插件的更新检查（纯函数部分不联网，自测台直接断言）──

    /// <summary>记录的 <c>Repo</c>（owner/repo）能否解析成 GitHub 仓库。复用直装时的同一套解析规则。</summary>
    internal static bool TryParseGitHubRepo(string? repo, out string owner, out string repoName)
        => GitHubReleaseClient.TryParseRepo(repo, out owner, out repoName, out _);

    /// <summary>
    /// 把 Release 标签规范成版本号：GitHub 的 tag 可能是 <c>v1.2.3</c>、<c>plugin-1.2.3</c>
    /// 甚至 <c>releases/v1.2.3</c>，直接丢给版本比较会被当成「不认识的字符串」走序数比较。
    /// 解析不出形如 SemVer 的片段时返回空串。
    /// </summary>
    internal static string NormalizeReleaseVersion(string? tag)
    {
        var text = tag?.Trim() ?? "";
        if (text.Length == 0) return "";
        // 先按路径取最后一段（有些仓库用 release/v1.2.3 这种 tag）。
        var slash = text.LastIndexOf('/');
        if (slash >= 0) text = text[(slash + 1)..];
        for (var match = VersionInTagRe.Match(text); match.Success; match = match.NextMatch())
        {
            if (PluginVersion.Parse(match.Value) != null) return match.Value;
        }
        return "";
    }

    /// <summary>
    /// Release 是否比已装版本新。标签解析不出数字版本（如滚动标签 <c>latest</c>）时一律视为
    /// 「没有可确认的新版」—— 不能因为字符串比较把 <c>latest</c> 判成更新。
    /// </summary>
    internal static bool IsGitHubUpdateAvailable(string? installedVersion, string? releaseTag)
    {
        var latest = NormalizeReleaseVersion(releaseTag);
        if (string.IsNullOrEmpty(latest) || PluginVersion.Parse(latest) == null) return false;
        return PluginVersion.IsNewer(installedVersion, latest);
    }

    void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || Record == null) return;
        try
        {
            // Owner 用详情弹窗自己（详情是模态窗口），设置窗口关掉后回到详情页。
            new PluginSettingsDialog(Record.Id) { Owner = this }.ShowDialog();
        }
        catch (Exception ex)
        {
            DzTrace.Always("[Plugin] cannot open the settings dialog from the detail page: " + ex.Message);
        }
    }

    void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || Record == null) return;
        _ = CheckGitHubUpdateAsync();
    }

    /// <summary>
    /// 「检查更新」：只在用户点击时联网 —— 查仓库最新 Release → 比较版本 → 确认后用
    /// <see cref="PluginInstaller.InstallFromGitHubAsync"/> 走与直装完全相同的下载/校验管线。
    /// 无新版 / 限流 / 网络失败都用可读文案提示，不抛异常。
    /// </summary>
    async System.Threading.Tasks.Task CheckGitHubUpdateAsync()
    {
        var record = Record;
        if (record == null) return;
        if (!TryParseGitHubRepo(record.Repo, out var owner, out var repo))
        {
            ShowNotice(Loc["Plugin.GitHub.Error.InvalidInput"]);
            return;
        }

        void Inform(string text, MessageBoxImage icon) =>
            MessageBox.Show(this, text, Loc["Plugin.GitHub.CheckUpdate"], MessageBoxButton.OK, icon);

        SetBusy(true);
        var closeAfter = false;
        try
        {
            ShowNotice(Loc["Plugin.GitHub.Checking"]);
            using var client = new GitHubReleaseClient();
            var lookup = await client.ResolvePackageAsync(owner, repo, null);
            if (IsClosedForWrites()) return;
            if (!lookup.Success)
            {
                Inform(lookup.Error ?? Loc["Plugin.GitHub.Error.NotFound"], MessageBoxImage.Warning);
                return;
            }

            var tag = lookup.Tag;
            // 滚动标签（latest / nightly）不是版本号：不能拿它做比较，也不能报「已是最新」误导用户。
            if (NormalizeReleaseVersion(tag).Length == 0)
            {
                Inform(Loc.Get("Plugin.GitHub.RollingTag", tag), MessageBoxImage.Information);
                return;
            }
            if (!IsGitHubUpdateAvailable(record.Version, tag))
            {
                Inform(Loc.Get("Plugin.GitHub.AlreadyLatest", record.Version), MessageBoxImage.Information);
                return;
            }

            var confirm = MessageBox.Show(this,
                Loc.Get("Plugin.GitHub.UpdateConfirm", tag, record.Version),
                Loc["Plugin.GitHub.CheckUpdate"], MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            ShowNotice(Loc.Get("Plugin.GitHub.Updating", tag));
            var result = await PluginService.Instance.Installer!.InstallFromGitHubAsync(owner, repo, tag);
            PluginService.Instance.RefreshRecords();
            if (IsClosedForWrites()) return;
            if (!result.Success)
            {
                Inform(Loc.Get("Plugin.InstallFailed", result.Error ?? ""), MessageBoxImage.Warning);
                return;
            }

            // 代码类插件要重启才加载新版本：详情页里的 Entry 还是旧版本快照，关掉弹窗让页面重绘。
            Record = PluginService.Instance.GetRecord(record.Id) ?? record;
            closeAfter = true;
            MessageBox.Show(this, Loc.Get("Plugin.GitHub.UpdateDone", Record.Version),
                Loc["Plugin.GitHub.CheckUpdate"], MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ShowNotice(Loc.Get("Plugin.InstallFailed", ex.Message));
            Inform(Loc.Get("Plugin.InstallFailed", ex.Message), MessageBoxImage.Warning);
            DzTrace.Always($"[Plugin] GitHub update check failed: {ex}");
        }
        finally
        {
            SetBusy(false);
            if (closeAfter && !_closed) Close();
        }
    }

    // ── 预览图轮播 ──

    /// <summary>收集可轮播的预览图（过滤空串，最多 <see cref="MaxPreviewImages"/> 张）。</summary>
    void InitPreviewUrls()
    {
        _previewUrls.Clear();
        if (Entry.PreviewUrls != null)
        {
            foreach (var url in Entry.PreviewUrls)
            {
                if (string.IsNullOrWhiteSpace(url)) continue;
                _previewUrls.Add(url.Trim());
                if (_previewUrls.Count >= MaxPreviewImages) break;
            }
        }
        _previewIndex = 0;
        SyncPreviewChrome();
        // 有预览图就先占位：下载失败时保留一块稳定的骨架，弹窗高度不会随加载结果跳动。
        if (_previewUrls.Count > 0) PreviewHost.Visibility = Visibility.Visible;
    }

    /// <summary>按张数决定箭头 / 计数显隐，并刷新「当前 / 总数」。</summary>
    void SyncPreviewChrome()
    {
        var count = _previewUrls.Count;
        var nav = count > 1;
        PreviewPrevButton.Visibility = nav ? Visibility.Visible : Visibility.Collapsed;
        PreviewNextButton.Visibility = nav ? Visibility.Visible : Visibility.Collapsed;
        PreviewCounter.Visibility = nav ? Visibility.Visible : Visibility.Collapsed;
        PreviewCountText.Text = count > 0 ? $"{_previewIndex + 1} / {count}" : "";
    }

    /// <summary>关闭 / 退出期不再回写控件：异步续体可能比窗口活得久（详见 PluginsPage 的说明）。</summary>
    bool IsClosedForWrites() => _closed || !IsLoaded || AppLifetime.IsShuttingDown;

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        base.OnClosed(e);
    }

    async System.Threading.Tasks.Task LoadImagesAsync()
    {
        try
        {
            // 先加载预览图再加载小图标：详情页最显眼的是预览图，别让一个 404 的图标拖慢它。
            if (_previewUrls.Count > 0) await LoadPreviewAtAsync(0);

            var iconPath = await PluginService.Instance.Catalog.GetImageAsync(Entry.IconUrl);
            if (iconPath == null || IsClosedForWrites()) return;
            var icon = TryLoadBitmap(iconPath);
            if (icon == null) return;
            IconImage.Source = icon;
            IconImage.Visibility = Visibility.Visible;
            PlaceholderIcon.Visibility = Visibility.Collapsed;
        }
        catch { }
    }

    /// <summary>
    /// 只走 <see cref="PluginCatalogService.GetImageAsync"/>（自带域名白名单与本地缓存），
    /// 失败时保留当前已显示的图，不把轮播区清空。
    /// </summary>
    async System.Threading.Tasks.Task LoadPreviewAtAsync(int index)
    {
        if (index < 0 || index >= _previewUrls.Count) return;
        _previewBusy = true;
        try
        {
            var path = await PluginService.Instance.Catalog.GetImageAsync(_previewUrls[index]);
            if (path == null || IsClosedForWrites()) return;
            var bitmap = TryLoadBitmap(path);
            if (bitmap == null) return;
            PreviewImage.Source = bitmap;
            PreviewHost.Visibility = Visibility.Visible;
        }
        finally
        {
            _previewBusy = false;
        }
    }

    /// <summary>左右翻页；加载失败时计数退回上一张，避免「2 / 3 却还显示第 1 张」。</summary>
    async System.Threading.Tasks.Task NavigatePreviewAsync(int delta)
    {
        if (_previewBusy || _previewUrls.Count < 2) return;
        var previous = _previewIndex;
        _previewIndex = (_previewIndex + delta + _previewUrls.Count) % _previewUrls.Count;
        SyncPreviewChrome();
        var before = PreviewImage.Source;
        await LoadPreviewAtAsync(_previewIndex);
        if (IsClosedForWrites()) return;
        // 新图没换上（下载/解码失败）：计数回到上一张，界面保持自洽。
        if (ReferenceEquals(PreviewImage.Source, before))
        {
            _previewIndex = previous;
            SyncPreviewChrome();
        }
    }

    void PreviewPrev_Click(object sender, RoutedEventArgs e) => _ = NavigatePreviewAsync(-1);

    void PreviewNext_Click(object sender, RoutedEventArgs e) => _ = NavigatePreviewAsync(1);

    static BitmapImage? TryLoadBitmap(string path)
    {
        try
        {
            return LoadBitmap(path);
        }
        catch
        {
            // 缓存里的字节可能不是合法图片（下载被截断 / 服务端返回 HTML），解码失败只影响这一张。
            return null;
        }
    }

    static BitmapImage LoadBitmap(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        return image;
    }

    void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    void Github_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo($"https://github.com/{Entry.Repo}") { UseShellExecute = true }); }
        catch { }
    }

    /// <summary>
    /// 举报：没有后端，直接开 GitHub issue 表单（预填插件 id / 版本）。
    /// 这也让「谁被举报了什么」留在公开记录里，社区和作者都能看到处理过程。
    /// </summary>
    void Report_Click(object sender, RoutedEventArgs e)
    {
        var url = "https://github.com/Three-Freezen/DeskOrder-Plugins/issues/new?template=report.yml"
                  + "&title=" + Uri.EscapeDataString($"[report] {Entry.Id} {Entry.Version}")
                  + "&plugin=" + Uri.EscapeDataString(Entry.Id)
                  + "&version=" + Uri.EscapeDataString(Entry.Version);
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { DzTrace.Always("[Plugin] cannot open the report page: " + ex.Message); }
    }

    void Primary_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        InstallRequested?.Invoke();
    }

    void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        ToggleRequested?.Invoke();
    }

    void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        UninstallRequested?.Invoke();
    }

    void NewWidget_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        NewWidgetRequested?.Invoke();
    }

    public void SetBusy(bool busy)
    {
        _busy = busy;
        PrimaryButton.IsEnabled = !busy;
        ToggleButton.IsEnabled = !busy;
        UninstallButton.IsEnabled = !busy;
        NewWidgetButton.IsEnabled = !busy;
        GithubButton.IsEnabled = !busy && !string.IsNullOrWhiteSpace(Entry.Repo);
        ReportButton.IsEnabled = !busy && !string.IsNullOrWhiteSpace(Entry.Repo);
        SettingsButton.IsEnabled = !busy;
        CheckUpdateButton.IsEnabled = !busy;
        if (busy)
        {
            PrimaryButton.Content = Loc["Plugin.Installing"];
            CheckUpdateButton.Content = Loc["Plugin.GitHub.Checking"];
        }
        // 安装失败/取消后重算一次：被禁用名单置灰的开关不能被 SetBusy(false) 重新点亮。
        else Apply();
    }
}
