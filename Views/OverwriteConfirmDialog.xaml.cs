using System.Windows;
using DesktopZones.Services;

namespace DesktopZones.Views;

public partial class OverwriteConfirmDialog : Window
{
    private readonly LocalizationService _loc = LocalizationService.Instance;
    private readonly string _presetName;

    public OverwriteConfirmDialog(string presetName)
    {
        InitializeComponent();
        _presetName = presetName;
        ApplyLoc();
    }

    private void ApplyLoc()
    {
        var cn = _loc.CurrentLanguage == "zh";
        Title = _loc["Preset.OverwriteTitle"];
        DialogTitle.Text = _loc["Preset.OverwriteTitle"];
        PromptText.Text = _loc.Get("Preset.OverwritePrompt", _presetName);
        CancelButton.Content = _loc["Preset.Cancel"];
        OverwriteButton.Content = _loc["Preset.Overwrite"];
    }

    private void OverwriteButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
