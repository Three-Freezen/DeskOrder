using System;
using System.Collections.Generic;
using System.Diagnostics;
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

    /// <summary>自测台只读访问器：左右箭头 / 计数是否显示（>1 张才显示）。</summary>
    internal bool PreviewNavVisibleForLab => PreviewPrevButton.Visibility == Visibility.Visible;

    /// <summary>自测台只读访问器：计数文本（形如 "1 / 3"）。</summary>
    internal string PreviewCounterForLab => PreviewCountText.Text;

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
            NewWidgetButton.Visibility = Visibility.Collapsed;
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
            var source = PluginDisplay.DescribeSource(Record.Source);
            var widgets = PluginService.Instance.CountWidgets(Record.Id);
            var state = $"v{Record.Version} · {source} · {Record.InstalledAtUtc.ToLocalTime():yyyy-MM-dd}";
            if (Record.KindValue == PluginKind.Widget && widgets > 0)
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
    /// 已安装的 widget 插件才有「新建组件」：插件启用但描述符还没加载（装了没重启/加载失败）
    /// 时按钮置灰并提示重启 —— 菜单本身也只列已加载的描述符。
    /// </summary>
    void UpdateNewWidgetButton()
    {
        var descriptors = Record is { KindValue: PluginKind.Widget, Enabled: true }
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
        if (busy) PrimaryButton.Content = Loc["Plugin.Installing"];
        // 安装失败/取消后重算一次：被禁用名单置灰的开关不能被 SetBusy(false) 重新点亮。
        else Apply();
    }
}
