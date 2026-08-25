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
                _vm.PropertyChanged += OnVmPropertyChanged;
                // Replace the VM so the XAML bindings (CellLayout / Source.* / name) resolve.
                DataContext = _vm;
                UpdateThumbs();
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

    /// <summary>Wire-up helper for direct callers (preset preview, tests)
    /// that already hold the underlying ZoneItem.</summary>
    public void SetSource(ZoneItem item, ShellIconService iconService)
    {
        if (_vm != null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = new SubfolderItemViewModel(item, iconService);
        _vm.PropertyChanged += OnVmPropertyChanged;
        DataContext = _vm;
        UpdateThumbs();
    }
}
