using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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

    // 点开详情：按下点 + 拖动阈值 + 重入闩（见 CardRoot_MouseLeftButtonUp 的注释）。
    const double DragThreshold = 4.0;
    Point? _pressPoint;
    bool _opening;

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
            // 六类插件都能从市场安装（widget / zonebar / tool / language / appearance / integration）。
            ActionButton.Content = Loc["Plugin.Install"];
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

    void CardRoot_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _pressPoint = null;
        if (e.OriginalSource is DependencyObject source && IsInteractive(source)) return;
        _pressPoint = e.GetPosition(this);
    }

    void CardRoot_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var press = _pressPoint;
        _pressPoint = null;
        // 没有在卡片上按下过（在按钮上按下、或从卡片外拖进来松手）不算点击。
        if (press == null) return;
        if (e.OriginalSource is DependencyObject source && IsInteractive(source)) return;
        // 只判 Up 会把「按住卡片拖动、仍在本卡片上松手」当成点开；用按下点算位移。
        if ((e.GetPosition(this) - press.Value).Length > DragThreshold) return;
        // 详情是模态的，ShowDialog() 里跑嵌套消息泵；这个闩锁防重入（也给将来改非模态兜底）。
        if (_opening) return;
        _opening = true;
        try
        {
            e.Handled = true;
            OpenRequested?.Invoke(this);
        }
        finally
        {
            _opening = false;
        }
    }

    /// <summary>按钮 / 下拉框 / 滚动条 / 滑块等自带交互的控件：交给它们自己处理，不要打开详情。</summary>
    static bool IsInteractive(DependencyObject source)
    {
        for (var d = source; d != null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is ButtonBase or ComboBox or ScrollBar or Thumb) return true;
        }
        return false;
    }

    void CardRoot_MouseEnter(object sender, MouseEventArgs e)
        => CardRoot.BorderBrush = (Brush)FindResource("Brush.Border.Default");

    void CardRoot_MouseLeave(object sender, MouseEventArgs e)
    {
        // 按下后移出卡片（拖出去再松手）不该留下一个过期的按下点，否则之后从卡片外
        // 拖回来松手会被误判成点击。
        _pressPoint = null;
        CardRoot.BorderBrush = (Brush)FindResource("Brush.Border.Subtle");
    }
}
