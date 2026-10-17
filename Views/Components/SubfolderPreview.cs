using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using DesktopZones.Models;
using DesktopZones.Services;

namespace DesktopZones.Views.Components;

/// <summary>
/// 次级分区的 2×2 缩略图预览 —— <b>面板列表视图</b>和<b>分区列表视图</b>共用的唯一实现。
///
/// ponytail 2026-10: 这段视觉原本只以 <c>PanelWindow.BuildSubfolderPreview</c> 的形式存在
/// （面板列表模式用），分区列表视图当时另起炉灶用了 <see cref="SubfolderItemView"/>，
/// 于是同一个「次级分区图标」在两个窗口长得不一样：分区那边圆角跟宿主分区走
/// （<c>ZoneItem.CornerRounded</c>，20px 的盒子上就是个大圆角，底边框看起来像被啃掉），
/// 面板这边是写死的 5px。现在收口成这一个控件，两处调同一份代码，只允许传尺寸。
///
/// 尺寸口径：盒子 <see cref="BoxSize"/>（面板列表 28、分区列表 22），缩略图
/// <see cref="ThumbSize"/>（12 / 9）。盒子外框 = 1px #40FFFFFF、圆角 5、ClipToBounds；
/// 内部是 3px 外边距的 2×2 网格，每格 #14FFFFFF 圆角 3、间距 1。
/// </summary>
public class SubfolderPreview : ContentControl
{
    static readonly Brush BoxBorderBrush = Freeze(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
    static readonly Brush SlotBrush = Freeze(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));

    static SolidColorBrush Freeze(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    /// <summary>缩略图来源（与两个窗口一致，走同一个 shell 图标单例）。</summary>
    static ShellIconService Icons => ShellIconService.Instance;

    public static readonly DependencyProperty SourceProperty =
        DependencyProperty.Register(nameof(Source), typeof(ZoneItem), typeof(SubfolderPreview),
            new PropertyMetadata(null, OnLayoutInputChanged));

    /// <summary>要预览的次级分区（<c>ZoneItem.Type == SubFolder</c>）。</summary>
    public ZoneItem? Source
    {
        get => (ZoneItem?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public static readonly DependencyProperty BoxSizeProperty =
        DependencyProperty.Register(nameof(BoxSize), typeof(double), typeof(SubfolderPreview),
            new PropertyMetadata(28.0, OnLayoutInputChanged));

    /// <summary>方框边长（DIP）。</summary>
    public double BoxSize
    {
        get => (double)GetValue(BoxSizeProperty);
        set => SetValue(BoxSizeProperty, value);
    }

    public static readonly DependencyProperty ThumbSizeProperty =
        DependencyProperty.Register(nameof(ThumbSize), typeof(double), typeof(SubfolderPreview),
            new PropertyMetadata(12.0, OnLayoutInputChanged));

    /// <summary>每个缩略图的最大边长（DIP）。</summary>
    public double ThumbSize
    {
        get => (double)GetValue(ThumbSizeProperty);
        set => SetValue(ThumbSizeProperty, value);
    }

    public SubfolderPreview()
    {
        HorizontalContentAlignment = HorizontalAlignment.Center;
        VerticalContentAlignment = VerticalAlignment.Center;
        IsTabStop = false;
        Focusable = false;
        Rebuild();
    }

    /// <summary>进可视树后才知道真实 DPI（125% 缩放下 1 DIP = 1.25 物理像素），
    /// 边框粗细要按 DPI 折回「正好 1 物理像素」，否则那圈 1px 框会被摊到半像素上，
    /// 底边看起来像被裁掉一截。同理换屏/改缩放后要重建一次。</summary>
    protected override void OnVisualParentChanged(DependencyObject oldParent)
    {
        base.OnVisualParentChanged(oldParent);
        Rebuild();
    }

    static void OnLayoutInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => (d as SubfolderPreview)?.Rebuild();

    void Rebuild()
    {
        var source = Source;
        double boxSize = Math.Max(8, BoxSize);
        double thumbSize = Math.Max(2, ThumbSize);

        // 1 物理像素的边框厚度（DIP）。取不到 DPI 时按 1 DIP。
        double dpi = 1.0;
        try
        {
            var d = VisualTreeHelper.GetDpi(this);
            if (d.DpiScaleX > 0) dpi = d.DpiScaleX;
        }
        catch { }
        double frameThickness = Math.Max(0.5, 1.0 / dpi);

        // 边框不再用 Border.BorderBrush（WPF 的描边永远骑在元素边界上，一半跑到外面）：
        // 125% 缩放下这一半会落在半像素上、甚至被上一层裁掉，看起来就是「底边框缺一截」。
        // 改成 Rectangle + 半个线宽的外边距 —— 描边整条落在盒子内侧，正好 1 物理像素。
        var frame = new System.Windows.Shapes.Rectangle
        {
            Stroke = BoxBorderBrush,
            StrokeThickness = frameThickness,
            RadiusX = 4.5,
            RadiusY = 4.5,
            Fill = null,
            Margin = new Thickness(frameThickness / 2),
            IsHitTestVisible = false
        };

        var box = new Grid
        {
            Width = boxSize,
            Height = boxSize,
            ClipToBounds = false
        };
        box.Children.Add(frame);

        var grid = new UniformGrid { Rows = 2, Columns = 2, Margin = new Thickness(3) };
        var inners = source?.SubItems?.Take(4).ToList() ?? new List<ZoneItem>();
        for (int i = 0; i < 4; i++)
        {
            var slot = new Border
            {
                Background = SlotBrush,
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(1)
            };
            if (i < inners.Count)
            {
                var img = new Image
                {
                    Source = ResolveIcon(inners[i]),
                    Width = thumbSize,
                    Height = thumbSize,
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                slot.Child = img;
            }
            grid.Children.Add(slot);
        }

        box.Children.Add(grid);
        Content = box;
    }

    /// <summary>图标解析与分区/面板的条目图标同一套口径（图片文件优先给内容缩略图）。</summary>
    static ImageSource? ResolveIcon(ZoneItem item)
    {
        var svc = Icons;
        if (svc == null) return null;
        if (ShellIconService.IsImageFile(item.TargetPath) && ShellIconService.ImagePreviewEnabled)
            return svc.GetImageThumbnail(item.TargetPath)
                ?? svc.GetIcon(item.TargetPath, item.Type, item.IconPath);
        return svc.GetIcon(item.TargetPath, item.Type, item.IconPath);
    }
}
