using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DesktopZones.Helpers;
using DesktopZones.Models;

namespace DesktopZones.Views;

/// <summary>创建播放器时的形态选择框（律动条 / 唱片机）。形态创建后不可改，
/// 所以这里必须选一个才能点创建。</summary>
public partial class MediaModePickerDialog : Window
{
    public MediaDisplayMode SelectedMode { get; private set; } = MediaDisplayMode.Bars;

    public MediaModePickerDialog()
    {
        InitializeComponent();
        AcrylicHelper.ApplyMenuSurface(this, 12);
        UpdateSelection();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    // 卡片用 Preview 鼠标按下：窗口的 OnMouseLeftButtonDown 里的 DragMove 会吞掉
    // 抬起事件（MouseLeftButtonUp 永远收不到），所以选中放在按下这一拍，
    // 并 Handled=true 把这次交互从「拖窗口」里摘出来。
    private void BarsCard_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Select(MediaDisplayMode.Bars);
    }

    private void VinylCard_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Select(MediaDisplayMode.Vinyl);
    }

    private void Select(MediaDisplayMode mode)
    {
        SelectedMode = mode;
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        Apply(BarsCard, SelectedMode == MediaDisplayMode.Bars);
        Apply(VinylCard, SelectedMode == MediaDisplayMode.Vinyl);
    }

    private void Apply(Border card, bool selected)
    {
        if (card == null) return;
        // 厚度固定 2px：只在选中态换画刷，避免 1px↔2px 切换时内容跳 1 像素。
        card.BorderThickness = new Thickness(2);
        card.BorderBrush = selected
            ? (Brush)FindResource("Brush.Accent")
            : (Brush)FindResource("Menu.Border.Subtle");
        card.Background = selected
            ? (Brush)FindResource("Brush.Accent.Wash")
            : (Brush)FindResource("Menu.Bg.Hover");
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            DialogResult = true;
            Close();
            e.Handled = true;
        }
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        try { DragMove(); } catch { }
    }
}
