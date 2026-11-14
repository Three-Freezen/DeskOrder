using System.Windows;
using System.Windows.Input;
using DesktopZones.Services;

namespace DesktopZones.Views;

/// <summary>安装确认弹窗需要展示的字段（来自 registry 或本地包 manifest）。</summary>
public sealed class PluginInstallPrompt
{
    public string Name { get; init; } = "";
    public string KindLine { get; init; } = "";
    public string Author { get; init; } = "";
    public string Version { get; init; } = "";
    public string Size { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public string Permissions { get; init; } = "";
    public string Contents { get; init; } = "";
    public bool Declarative { get; init; }
}

public partial class PluginInstallDialog : Window
{
    static readonly LocalizationService Loc = LocalizationService.Instance;

    public PluginInstallPrompt Prompt { get; }

    public PluginInstallDialog(PluginInstallPrompt prompt)
    {
        InitializeComponent();
        Prompt = prompt;

        TitleText.Text = Loc["Plugin.InstallConfirm.Title"];
        NameText.Text = prompt.Name;
        var meta = new System.Text.StringBuilder();
        if (!string.IsNullOrWhiteSpace(prompt.KindLine)) meta.AppendLine(prompt.KindLine);
        var line = "";
        if (!string.IsNullOrWhiteSpace(prompt.Author)) line += prompt.Author;
        if (!string.IsNullOrWhiteSpace(prompt.Version)) line += (line.Length > 0 ? " · " : "") + "v" + prompt.Version;
        if (!string.IsNullOrWhiteSpace(prompt.Size)) line += (line.Length > 0 ? " · " : "") + prompt.Size;
        if (line.Length > 0) meta.Append(line);
        MetaText.Text = meta.ToString().TrimEnd();
        ContentsText.Text = prompt.Contents;
        ContentsText.Visibility = string.IsNullOrWhiteSpace(prompt.Contents)
            ? Visibility.Collapsed
            : Visibility.Visible;
        PermissionsText.Text = string.IsNullOrWhiteSpace(prompt.Permissions)
            ? ""
            : Loc["Plugin.Permissions"] + ": " + prompt.Permissions;
        PermissionsText.Visibility = string.IsNullOrWhiteSpace(prompt.Permissions)
            ? Visibility.Collapsed
            : Visibility.Visible;

        HashRow.Visibility = string.IsNullOrWhiteSpace(prompt.Sha256) ? Visibility.Collapsed : Visibility.Visible;
        HashBox.Text = prompt.Sha256;

        WarningText.Text = prompt.Declarative
            ? Loc["Plugin.Warning.Declarative"] + "\n" + Loc["Plugin.Warning"]
            : Loc["Plugin.Warning"];
    }

    void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    void CopyHash_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(Prompt.Sha256); } catch { }
    }

    void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    void Install_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
