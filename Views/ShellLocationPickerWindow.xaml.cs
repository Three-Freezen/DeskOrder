using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DesktopZones.Helpers;
using DesktopZones.Services;

namespace DesktopZones.Views;

/// <summary>
/// Picker for virtual shell locations (Recycle Bin, This PC, ...) that file dialogs
/// can't select. Offers the common presets plus a custom input accepting GUIDs,
/// "::{GUID}" and "shell:" names.
/// </summary>
public partial class ShellLocationPickerWindow : Window
{
    private readonly LocalizationService _loc = LocalizationService.Instance;

    /// <summary>User-selected items: (display name, "::{GUID}" spec).</summary>
    public List<(string Name, string Spec)> SelectedItems { get; } = new();

    public ShellLocationPickerWindow()
    {
        InitializeComponent();
        AcrylicHelper.ApplyMenuSurface(this, 10);
        TitleLabel.Text = _loc["ShellPicker.Title"];
        OkBtn.Content = _loc["ShellPicker.Confirm"];
        CancelBtn.Content = _loc["ShellPicker.Cancel"];
        AddBtn.Content = _loc["ShellPicker.Add"];
        CustomHintLabel.Text = _loc["ShellPicker.CustomHint"];
        CustomNameLabel.Text = _loc["ShellPicker.CustomName"];

        foreach (var preset in ShellLocationResolver.Presets)
            ListPanel.Children.Add(MakeCheck(_loc[preset.NameKey], ShellLocationResolver.SpecOf(preset.Guid)));
    }

    private CheckBox MakeCheck(string name, string spec)
    {
        var cb = new CheckBox
        {
            Content = name,
            Tag = spec,
            FontSize = 13,
            Padding = new Thickness(0, 3, 0, 3),
            Style = (Style)FindResource("SystemAccentCheckBox")
        };
        cb.SetResourceReference(Control.ForegroundProperty, "Menu.Text.Primary");
        return cb;
    }

    private void AddCustom_Click(object sender, RoutedEventArgs e) => AddCustom();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { DialogResult = false; }
    }

    private void NameInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) AddCustom();
    }

    private void AddCustom()
    {
        var input = SpecInput.Text.Trim();
        if (input.Length == 0) return;
        var spec = ShellLocationResolver.Normalize(input);
        if (spec == null)
        {
            MessageBox.Show(string.Format(_loc["ShellPicker.InvalidSpec"], input),
                _loc["ShellPicker.InvalidSpec.Title"], MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Already in the list — just tick it.
        foreach (var child in ListPanel.Children)
        {
            if (child is CheckBox cb && string.Equals(cb.Tag as string, spec, StringComparison.OrdinalIgnoreCase))
            {
                cb.IsChecked = true;
                SpecInput.Clear();
                NameInput.Clear();
                return;
            }
        }

        var name = NameInput.Text.Trim();
        if (name.Length == 0) name = ShellLocationResolver.GetDisplayName(spec) ?? input;
        var item = MakeCheck(name, spec);
        item.IsChecked = true;
        ListPanel.Children.Add(item);
        SpecInput.Clear();
        NameInput.Clear();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        foreach (var child in ListPanel.Children)
        {
            if (child is CheckBox { IsChecked: true } cb && cb.Tag is string spec)
                SelectedItems.Add((cb.Content?.ToString() ?? spec, spec));
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
