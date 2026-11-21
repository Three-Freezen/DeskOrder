using System;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using DesktopZones.Helpers;
using DesktopZones.Models;
using DesktopZones.Services;

namespace DesktopZones.Views;

/// <summary>
/// 「从 GitHub 仓库直接安装」对话框。
///
/// 流程：粘贴 owner/repo（或仓库 / Release URL）→「查找」拉最新 Release、下载 .dopkg、
/// 读 manifest 与验签 → 预览区确认 →「安装」走现有的安全校验/落盘管线。
/// 查找/下载/安装期间禁用输入与按钮并把进度写在状态行；失败只把可读原因显示在对话框里，
/// 不用 MessageBox 刷屏（安装成功才弹一句提示）。
/// </summary>
public partial class PluginGitHubInstallDialog : Window
{
    static readonly LocalizationService Loc = LocalizationService.Instance;

    readonly PluginService _service = PluginService.Instance;
    readonly CancellationTokenSource _cts = new();

    PluginInstaller.GitHubInstallPreview? _preview;
    bool _busy;

    /// <summary>本次对话框是否真的装上了（父页面据此决定要不要重绘）。</summary>
    public bool Installed { get; private set; }

    public PluginGitHubInstallDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => RepoBox.Focus();
        Closed += (_, _) =>
        {
            // 关窗即取消下载：否则后台下载完还会往已关闭的对话框里写状态、留下临时文件。
            try { _cts.Cancel(); } catch { }
            DiscardPreview();
        };
        UpdateActionState();
    }

    void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    void RepoBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => RepoHint.Visibility = string.IsNullOrEmpty(RepoBox.Text) ? Visibility.Visible : Visibility.Collapsed;

    async void RepoBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await FindAsync();
    }

    async void Find_Click(object sender, RoutedEventArgs e) => await FindAsync();

    internal async System.Threading.Tasks.Task FindAsync()
    {
        if (_busy) return;
        DiscardPreview();
        if (!GitHubReleaseClient.TryParseRepo(RepoBox.Text, out var owner, out var repo, out var tag))
        {
            ShowStatus(Loc["Plugin.GitHub.Error.InvalidInput"], error: true);
            return;
        }

        SetBusy(true);
        try
        {
            var progress = new Progress<string>(text => ShowStatus(text));
            var prepared = await _service.Installer!.PrepareFromGitHubAsync(owner, repo, tag, progress, _cts.Token);
            if (!IsLoaded)
            {
                // 查找期间用户把窗口关了：别把预览挂到已关闭的窗口上（临时文件由预览自己清理）。
                prepared.Dispose();
                return;
            }
            _preview = prepared;
            if (_preview.Error != null)
            {
                ShowStatus(_preview.Error, error: true);
                DiscardPreview();
                return;
            }
            ShowPreview(_preview);
            // 进度行收尾：预览区已经给出「找到什么、从哪里来」，不需要再留一行「正在校验…」。
            StatusText.Text = "";
        }
        catch (OperationCanceledException)
        {
            // 关窗取消：窗口都没了，不用再报错。
        }
        catch (Exception ex)
        {
            // 对话框是 async void 入口：异常漏出去会进全局未处理异常弹「DeskOrder 错误」。
            ShowStatus(Loc.Get("Plugin.InstallFailed", ex.Message), error: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _preview is not { Error: null, Manifest: not null }) return;
        var preview = _preview;
        SetBusy(true);
        var installed = false;
        try
        {
            var progress = new Progress<string>(text => ShowStatus(text));
            var result = await _service.Installer!.InstallPreparedAsync(preview, progress, _cts.Token);
            _service.RefreshRecords();
            if (!result.Success)
            {
                ShowStatus(Loc.Get("Plugin.InstallFailed", result.Error ?? ""), error: true);
                return;
            }

            installed = true;
            Installed = true;
            var name = preview.Manifest!.Name.Get(Loc.CurrentLanguage);
            // 声明式包安装即生效；代码类沿用「默认未启用、重启后生效」的既有文案。
            var headline = preview.Manifest.KindValue.IsCode()
                ? Loc.Get("Plugin.InstallDone", name)
                : Loc["Plugin.Installed"] + " · " + name;
            if (IsLoaded)
            {
                MessageBox.Show(this, headline + "\n\n" + Loc["Plugin.GitHub.Source"],
                    Loc["Plugin.GitHub.Title"], MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (OperationCanceledException)
        {
            if (!_cts.IsCancellationRequested)
                ShowStatus(Loc.Get("Plugin.InstallFailed", "cancelled"), error: true);
        }
        catch (Exception ex)
        {
            ShowStatus(Loc.Get("Plugin.InstallFailed", ex.Message), error: true);
        }
        finally
        {
            SetBusy(false);
        }

        if (!installed) return;
        DiscardPreview();
        Close();
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    void ShowPreview(PluginInstaller.GitHubInstallPreview preview)
    {
        var manifest = preview.Manifest!;
        var language = Loc.CurrentLanguage;
        PreviewName.Text = manifest.Name.Get(language);
        var kind = Loc[manifest.KindValue.I18nKey()];
        var author = string.IsNullOrWhiteSpace(manifest.Author.Github)
            ? manifest.Author.Name
            : $"{manifest.Author.Name} · @{manifest.Author.Github}";
        PreviewMeta.Text = $"{kind} · v{manifest.Version} · {author} · {FormatSize(preview.Size)}";
        PreviewRelease.Text = $"Release {preview.Tag}" + (preview.Official ? " · " + Loc["Plugin.Official"] : "");
        PreviewSignature.Text = preview.Trust.Level == TrustLevel.Verified
            ? Loc.Get("Plugin.Signature.Verified", preview.Trust.KeyId ?? "—")
            : Loc["Plugin.GitHub.Signature.Unsigned"];
        PreviewSignature.Foreground = preview.Trust.Level == TrustLevel.Verified
            ? ThemeBrushes.TextSecondaryModern
            : ThemeBrushes.TextTertiaryModern;
        var hash = "SHA-256 " + preview.Sha256;
        if (!string.IsNullOrWhiteSpace(preview.Contents)) hash += "\n" + preview.Contents;
        PreviewHash.Text = hash;
        PreviewBox.Visibility = Visibility.Visible;
        UpdateActionState();
    }

    void ShowStatus(string text, bool error = false)
    {
        StatusText.Text = text;
        StatusText.Foreground = error ? ThemeBrushes.DangerModern : ThemeBrushes.TextSecondaryModern;
    }

    void SetBusy(bool busy)
    {
        _busy = busy;
        RepoBox.IsEnabled = !busy;
        FindButton.IsEnabled = !busy;
        FindButton.Opacity = busy ? 0.5 : 1;
        UpdateActionState();
    }

    /// <summary>安装按钮只在「预览成功且没在忙」时可点。</summary>
    void UpdateActionState()
    {
        var enabled = !_busy && _preview is { Error: null, Manifest: not null };
        InstallButton.IsEnabled = enabled;
        InstallButton.Opacity = enabled ? 1 : 0.5;
    }

    void DiscardPreview()
    {
        _preview?.Dispose();
        _preview = null;
        PreviewBox.Visibility = Visibility.Collapsed;
        UpdateActionState();
    }

    static string FormatSize(long bytes)
        => bytes >= 1024 * 1024
            ? (bytes / 1024d / 1024d).ToString("0.0") + " MB"
            : (bytes / 1024d).ToString("0") + " KB";
}
