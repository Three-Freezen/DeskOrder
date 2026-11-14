using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DesktopZones.Models;
using DesktopZones.Services;

namespace DesktopZones.Views.Components;

public partial class PluginCard : UserControl
{
    static readonly LocalizationService Loc = LocalizationService.Instance;

    public PluginCatalogEntry Entry { get; private set; } = new();
    public PluginRecord? Record { get; private set; }

    public event Action<PluginCard>? ActionRequested;
    public event Action<PluginCard>? OpenRequested;

    public PluginCard()
    {
        InitializeComponent();
    }

    public void SetData(PluginCatalogEntry entry, PluginRecord? record, bool updateAvailable, bool busy)
    {
        Entry = entry;
        Record = record;

        NameText.Text = entry.Name.Get(Loc.CurrentLanguage);
        if (string.IsNullOrWhiteSpace(NameText.Text)) NameText.Text = entry.Id;
        OfficialPill.Visibility = entry.Official ? Visibility.Visible : Visibility.Collapsed;
        OfficialPill.ToolTip = Loc["Plugin.Official.Tip"];

        var kind = Loc[entry.KindValue.I18nKey()];
        var author = !string.IsNullOrWhiteSpace(entry.Author.Github)
            ? Loc.Get("Plugin.AuthorBy", entry.Author.Github)
            : (entry.Author.Name ?? "");
        MetaText.Text = $"{kind} · {author} · v{entry.Version}";
        SummaryText.Text = entry.Summary.Get(Loc.CurrentLanguage);
        DownloadsText.Text = entry.Downloads > 0
            ? Loc.Get("Plugin.Downloads", FormatCount(entry.Downloads))
            : "";

        ActionButton.IsEnabled = true;
        if (busy)
        {
            ActionButton.Content = Loc["Plugin.Installing"];
            ActionButton.IsEnabled = false;
            return;
        }
        if (record == null)
        {
            if (entry.KindValue is PluginKind.Widget or PluginKind.ZoneBar or PluginKind.Language or PluginKind.Appearance)
            {
                ActionButton.Content = Loc["Plugin.Install"];
            }
            else
            {
                // 工具/数据源还没开放：不要给用户看内部代号 "P1"。
                ActionButton.Content = Loc["Plugin.NotAvailable"];
                ActionButton.IsEnabled = false;
                ActionButton.ToolTip = Loc["Plugin.Type.Hint"];
            }
            return;
        }
        if (updateAvailable)
        {
            ActionButton.Content = Loc["Plugin.Update"];
            return;
        }
        ActionButton.Content = record.Enabled ? Loc["Plugin.Installed"] : Loc["Plugin.Enable"];
        ActionButton.IsEnabled = !record.Enabled;
    }

    public void SetPreview(ImageSource? source)
    {
        if (source == null)
        {
            PreviewImage.Visibility = Visibility.Collapsed;
            PlaceholderIcon.Visibility = Visibility.Visible;
            return;
        }
        PreviewImage.Source = source;
        PreviewImage.Visibility = Visibility.Visible;
        PlaceholderIcon.Visibility = Visibility.Collapsed;
    }

    public void SetBusy(bool busy)
    {
        if (!busy) { SetData(Entry, Record, false, false); return; }
        ActionButton.Content = Loc["Plugin.Installing"];
        ActionButton.IsEnabled = false;
    }

    static string FormatCount(long value)
        => value >= 10000 ? (value / 1000d).ToString("0.#") + "k" : value.ToString("N0");

    void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        ActionRequested?.Invoke(this);
    }

    void CardRoot_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && IsInsideButton(source)) return;
        OpenRequested?.Invoke(this);
    }

    static bool IsInsideButton(DependencyObject source)
    {
        for (var d = source; d != null; d = VisualTreeHelper.GetParent(d))
            if (d is System.Windows.Controls.Button) return true;
        return false;
    }

    void CardRoot_MouseEnter(object sender, MouseEventArgs e)
        => CardRoot.BorderBrush = (Brush)FindResource("Brush.Border.Default");

    void CardRoot_MouseLeave(object sender, MouseEventArgs e)
        => CardRoot.BorderBrush = (Brush)FindResource("Brush.Border.Subtle");
}
