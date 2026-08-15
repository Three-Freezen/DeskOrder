using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using DesktopZones.Models;
using DesktopZones.Services;

namespace DesktopZones.Views;

public partial class SavePresetDialog : Window
{
    private readonly LocalizationService _loc = LocalizationService.Instance;
    private readonly PresetService _service;
    private readonly object _payload;

    /// <summary>The preset that was saved (set when DialogResult is true).</summary>
    public PresetRecord? SavedPreset { get; private set; }

    private DispatcherTimer? _savedHintTimer;

    public SavePresetDialog(PresetService service, object payload)
    {
        InitializeComponent();
        _service = service;
        _payload = payload;
        ApplyLoc();
        NameBox.Text = _service.SuggestNextName();
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void ApplyLoc()
    {
        var cn = _loc.CurrentLanguage == Services.Language.Chinese;
        // Window/Dialog title — derived per-kind so Clock/Calendar/Note/MergedGroup/Panel
        // no longer share Zone's hardcoded "保存分区预设".
        var titleKey = $"Preset.SaveTitle.{_service.Kind}";
        Title = _loc[titleKey];
        DialogTitle.Text = _loc[titleKey];
        LabelName.Text = _loc["Preset.NameLabel"];
        EmptyHint.Text = _loc["Preset.EmptyNameHint"];
        SaveButton.Content = _loc["Preset.Save"];
        CancelButton.Content = _loc["Preset.Cancel"];
        SavedHint.Text = _loc["Preset.Saved"];
        RefreshEmptyHint();
    }

    private void RefreshEmptyHint()
    {
        EmptyHint.Visibility = string.IsNullOrEmpty(NameBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        SaveButton.IsEnabled = !string.IsNullOrWhiteSpace(NameBox.Text);
    }

    private void NameBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        RefreshEmptyHint();
    }

    private void NameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && SaveButton.IsEnabled)
        {
            SaveButton_Click(sender, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            NameBox.Focus();
            return;
        }

        // Conflict check — if a preset with the same name exists, ask before overwriting.
        if (_service.ExistsByName(name))
        {
            var confirm = new OverwriteConfirmDialog(name) { Owner = this };
            if (confirm.ShowDialog() != true)
            {
                // User chose Cancel on the overwrite dialog — keep editing the name.
                NameBox.Focus();
                NameBox.SelectAll();
                return;
            }
        }

        try
        {
            SavedPreset = _service.Save(name, _payload);
            ShowSavedHint();
            DialogResult = true;
            Close();
        }
        catch (System.Exception ex)
        {
            MessageBox.Show($"Failed to save preset:\n{ex.Message}", "DeskOrder",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Briefly flash the "saved" hint. Used to live here only on ZoneSettingsDialog;
    /// moved into the dialog itself so every caller benefits from the same UX.</summary>
    void ShowSavedHint()
    {
        SavedHint.Visibility = Visibility.Visible;
        _savedHintTimer?.Stop();
        _savedHintTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _savedHintTimer.Tick += (_, _) =>
        {
            SavedHint.Visibility = Visibility.Collapsed;
            _savedHintTimer!.Stop();
        };
        _savedHintTimer.Start();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}