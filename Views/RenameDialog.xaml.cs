using DesktopZones.Helpers;
using DesktopZones.Services;
using System.Windows;
using System.Windows.Input;

namespace DesktopZones.Views;

public partial class RenameDialog : Window
{
    public string NewName { get; private set; }
    private readonly LocalizationService _loc = LocalizationService.Instance;
    private readonly string? _titleOverride;

    /// <summary>
    /// Styled rename prompt shared by single- and batch-rename flows. Pass an optional
    /// title (already localized by the caller) and an optional hint line so the batch
    /// dialog keeps the exact same look as the single-icon rename dialog.
    /// </summary>
    public RenameDialog(string currentName, string? title = null, string? prompt = null)
    {
        InitializeComponent();
        AcrylicHelper.ApplyMenuSurface(this, 10);
        _titleOverride = title;
        NameInput.Text = currentName;
        NameInput.SelectAll();
        NameInput.Focus();
        NewName = currentName;
        if (!string.IsNullOrEmpty(prompt))
        {
            PromptText.Text = prompt;
            PromptText.Visibility = Visibility.Visible;
        }
        ApplyLoc();
    }

    void ApplyLoc()
    {
        TitleLabel.Text = _titleOverride ?? _loc["Rename.Title"];
        OkBtn.Content = _loc["Rename.Ok"];
        CancelBtn.Content = _loc["Rename.Cancel"];
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    { NewName = NameInput.Text.Trim(); DialogResult = true; Close(); }

    private void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }

    private void NameInput_KeyDown(object sender, KeyEventArgs e)
    { if (e.Key == Key.Enter) Ok_Click(sender, e); else if (e.Key == Key.Escape) Cancel_Click(sender, e); }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    { base.OnMouseLeftButtonDown(e); try { DragMove(); } catch { } }
}
