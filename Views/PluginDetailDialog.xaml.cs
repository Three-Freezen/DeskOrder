using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using DesktopZones.Helpers;
using DesktopZones.Models;
using DesktopZones.Services;

namespace DesktopZones.Views;

public partial class PluginDetailDialog : Window
{
    static readonly LocalizationService Loc = LocalizationService.Instance;

    public PluginCatalogEntry Entry { get; }
    public PluginRecord? Record { get; private set; }

    public event Action? InstallRequested;
    public event Action? UninstallRequested;
    public event Action? ToggleRequested;

    bool _busy;
    readonly bool _delisted;

    public PluginDetailDialog(PluginCatalogEntry entry, PluginRecord? record, bool delisted = false)
    {
        InitializeComponent();
        Entry = entry;
        Record = record;
        _delisted = delisted;
        Apply();
        Loaded += async (_, _) => await LoadImagesAsync();
    }

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
        var authorName = string.IsNullOrWhiteSpace(Entry.Author?.Github)
            ? (Entry.Author?.Name ?? "")
            : Loc.Get("Plugin.AuthorBy", Entry.Author!.Github);
        MetaText.Text = $"{Loc[Entry.KindValue.I18nKey()]} · {authorName} · v{Entry.Version} · {Entry.License}";
        SummaryText.Text = Entry.Summary.Get(Loc.CurrentLanguage);
        var description = Entry.Description?.Get(Loc.CurrentLanguage);
        DescriptionText.Text = string.IsNullOrWhiteSpace(description) ? "" : description;
        DescriptionText.Visibility = string.IsNullOrWhiteSpace(description) ? Visibility.Collapsed : Visibility.Visible;

        var permissions = Entry.Permissions;
        var described = PluginDisplay.DescribePermissions(permissions);
        PermissionsText.Text = string.IsNullOrEmpty(described)
            ? Loc["Plugin.Permissions.None"]
            : described;
        PermissionsBlock.Visibility = Entry.KindValue.IsCode() ? Visibility.Visible : Visibility.Collapsed;

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
                ? Loc.Get("Plugin.Contents.Appearance", counts.Materials, counts.Presets)
                : Loc["Plugin.Appearance.Hint"];
        }
        else
        {
            AppearanceBlock.Visibility = Visibility.Collapsed;
        }

        if (Record == null)
        {
            PrimaryButton.Visibility = Visibility.Visible;
            ToggleButton.Visibility = Visibility.Collapsed;
            UninstallButton.Visibility = Visibility.Collapsed;
            StateText.Text = "";
            // 工具/数据源插件当前版本还不提供安装：市场卡片已经禁用，详情弹窗也别给一个
            // 「点下去、下载完才报错」的安装按钮。
            if (Entry.KindValue is PluginKind.Tool or PluginKind.Integration)
            {
                PrimaryButton.Content = Loc["Plugin.NotAvailable"];
                PrimaryButton.IsEnabled = false;
                PrimaryButton.Opacity = 0.45;
            }
            else
            {
                PrimaryButton.Content = Loc["Plugin.Install"];
                PrimaryButton.IsEnabled = true;
                PrimaryButton.Opacity = 1.0;
            }
        }
        else
        {
            var update = IsUpdateAvailable(Record, Entry);
            PrimaryButton.Content = update ? Loc["Plugin.Update"] : Loc["Plugin.Install"];
            PrimaryButton.Visibility = update ? Visibility.Visible : Visibility.Collapsed;
            ToggleButton.Content = Record.Enabled ? Loc["Plugin.Disable"] : Loc["Plugin.Enable"];
            ToggleButton.Visibility = Entry.KindValue is PluginKind.Widget or PluginKind.ZoneBar or PluginKind.Language or PluginKind.Appearance
            ? Visibility.Visible : Visibility.Collapsed;
            UninstallButton.Visibility = Visibility.Visible;
            var source = PluginDisplay.DescribeSource(Record.Source);
            var widgets = PluginService.Instance.CountWidgets(Record.Id);
            var state = $"v{Record.Version} · {source} · {Record.InstalledAtUtc.ToLocalTime():yyyy-MM-dd}";
            if (Record.KindValue == PluginKind.Widget && widgets > 0)
                state += " · " + Loc.Get("Plugin.WidgetCount", widgets);
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

    async System.Threading.Tasks.Task LoadImagesAsync()
    {
        try
        {
            var iconPath = await PluginService.Instance.Catalog.GetImageAsync(Entry.IconUrl);
            if (iconPath != null) IconImage.Source = LoadBitmap(iconPath);
            if (IconImage.Source != null)
            {
                IconImage.Visibility = Visibility.Visible;
                PlaceholderIcon.Visibility = Visibility.Collapsed;
            }

            var previewUrl = Entry.PreviewUrls is { Count: > 0 } ? Entry.PreviewUrls[0] : null;
            if (previewUrl != null)
            {
                var previewPath = await PluginService.Instance.Catalog.GetImageAsync(previewUrl);
                if (previewPath != null)
                {
                    PreviewImage.Source = LoadBitmap(previewPath);
                    PreviewHost.Visibility = Visibility.Visible;
                }
            }
        }
        catch { }
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

    public void SetBusy(bool busy)
    {
        _busy = busy;
        PrimaryButton.IsEnabled = !busy;
        ToggleButton.IsEnabled = !busy;
        UninstallButton.IsEnabled = !busy;
        if (busy) PrimaryButton.Content = Loc["Plugin.Installing"];
        // 安装失败/取消后重算一次：被禁用名单置灰的开关不能被 SetBusy(false) 重新点亮。
        else Apply();
    }
}
