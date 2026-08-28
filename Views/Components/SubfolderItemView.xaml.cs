using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using DesktopZones.Models;
using DesktopZones.Services;
using DesktopZones.ViewModels;

namespace DesktopZones.Views.Components;

/// <summary>
/// ponytail: 单击/拖拽事件由 ZoneWindow 捕获(模板外层 Grid 接了 Item_MouseDown 等),
/// 本类只负责视觉。ZoneWindow.ItemsControl 绑定 ZoneItemViewModel,所以模板实例化时
/// DataContext 先是普通 VM — 这里检测到 SubFolder 后换成本类的
/// <see cref="SubfolderItemViewModel"/>,并把 2×2 缩略图的 Image.Source 用 code-behind
/// 直接赋值(不依赖 XAML 绑定,杜绝空集合索引绑定失效导致的"四格有但图标不显示")。
/// </summary>
public partial class SubfolderItemView : UserControl
{
    /// <summary>ponytail: set by ZoneWindow before any SubfolderItemView is
    /// instantiated. Needed because the DataTemplate fires DataContextChanged
    /// on the unbound VM and we must build the SubfolderItemViewModel which
    /// wraps SubItem ZoneItems into ZoneItemViewModels for thumbnail icons.</summary>
    public static ShellIconService? IconService { get; set; }

    /// <summary>磁贴/隐藏应用名时由外部绑定 — 为 true 时隐藏名称行(声明式,
    /// 不依赖 ZoneWindow 遍历容器,容器重生成后依然保持隐藏)。</summary>
    public static readonly DependencyProperty HideNameProperty =
        DependencyProperty.Register(nameof(HideName), typeof(bool), typeof(SubfolderItemView),
            new PropertyMetadata(false, OnHideNameChanged));
    public bool HideName
    {
        get => (bool)GetValue(HideNameProperty);
        set => SetValue(HideNameProperty, value);
    }

    static void OnHideNameChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SubfolderItemView v)
        {
            if (v._vm != null) v._vm.HideName = (bool)e.NewValue;
            if (v.SubNameText != null)
                v.SubNameText.Visibility = (bool)e.NewValue ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    SubfolderItemViewModel? _vm;

    public SubfolderItemView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is ZoneItemViewModel zvm && zvm.Source.Type == ItemType.SubFolder)
        {
            var svc = IconService;
            if (svc != null)
            {
                if (_vm != null) _vm.PropertyChanged -= OnVmPropertyChanged;
                _vm = new SubfolderItemViewModel(zvm.Source, svc);
                // ponytail 2026-08-28: 尺寸跟随分区网格 — 外层 VM 的 ItemSize 就是
                // Zone.GridSize;HideName 可能先于/晚于 DataContext 到达,两处都镜像。
                _vm.GridCellSize = zvm.ItemSize;
                _vm.HideName = HideName;
                _vm.PropertyChanged += OnVmPropertyChanged;
                // Replace the VM so the XAML bindings (CellLayout / Source.* / name) resolve.
                DataContext = _vm;
                UpdateThumbs();
                UpdateChrome();
            }
        }
    }

    void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm == null) return;
        if (e.PropertyName is nameof(SubfolderItemViewModel.Thumb0)
            or nameof(SubfolderItemViewModel.Thumb1)
            or nameof(SubfolderItemViewModel.Thumb2)
            or nameof(SubfolderItemViewModel.Thumb3))
            UpdateThumbs();
        if (e.PropertyName is nameof(SubfolderItemViewModel.BoxSize)
            or nameof(SubfolderItemViewModel.HideName))
            UpdateChrome();
    }

    /// <summary>把前 4 个内部图标的 ImageSource 直接写到四个 Image 上(空槽为 null)。</summary>
    void UpdateThumbs()
    {
        if (_vm == null) return;
        ThumbImg0.Source = _vm.Thumb0;
        ThumbImg1.Source = _vm.Thumb1;
        ThumbImg2.Source = _vm.Thumb2;
        ThumbImg3.Source = _vm.Thumb3;
    }

    /// <summary>盒子缩放后内部留白按比例收紧 — 固定 4/1.5/2 在小格子(如网格 40,
    /// 盒子仅 22px)里会把四个缩略图挤没。56px → 4/1.5/2(面板基线,与旧观感一致),
    /// 更大不再放宽(与旧观感一致),更小线性收小并设下限防归零。</summary>
    void UpdateChrome()
    {
        if (ThumbGrid == null) return;
        double box = _vm?.BoxSize ?? 56;
        double m = Math.Clamp(box / 14.0, 1.5, 4.0);
        ThumbGrid.Margin = new Thickness(m);
        foreach (var cell in ThumbGrid.Children.OfType<Border>())
        {
            cell.Margin = new Thickness(Math.Max(0.5, m * 0.375));       // 56 → 1.5
            if (cell.Child is Image img)
                img.Margin = new Thickness(Math.Max(0.5, m * 0.5));      // 56 → 2
        }
    }

    /// <summary>Wire-up helper for direct callers (preset preview, tests)
    /// that already hold the underlying ZoneItem.</summary>
    public void SetSource(ZoneItem item, ShellIconService iconService)
    {
        if (_vm != null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = new SubfolderItemViewModel(item, iconService);
        _vm.PropertyChanged += OnVmPropertyChanged;
        DataContext = _vm;
        UpdateThumbs();
        UpdateChrome();
    }
}
