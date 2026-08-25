using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using DesktopZones.Models;
using DesktopZones.Services;

namespace DesktopZones.ViewModels;

/// <summary>
/// ponytail: view state for a SubFolder ZoneItem rendered in the main zone.
/// Wraps the underlying <see cref="ZoneItem"/> (Type=SubFolder) + derived
/// CellLayout (1×1 if ≤4 SubItems or IconSizeAutoGrow=false; 2×2 otherwise).
/// SubFolder 专属字段直接走 ZoneItem 双向 binding,本类只持有派生状态 + 前 4 个
/// SubItem 的缩略图图标 (Thumb0..Thumb3 — 显式属性而非 XAML 索引绑定,避免空集合
/// 下索引绑定失效导致 2×2 预览不刷新的问题)。
/// </summary>
public class SubfolderItemViewModel : INotifyPropertyChanged
{
    private readonly ShellIconService _iconService;

    public ZoneItem Source { get; }

    /// <summary>Wrapped ZoneItemViewModels for the first 4 SubItems (thumbnail slots).</summary>
    public ObservableCollection<ZoneItemViewModel> ThumbnailVms { get; } = new();

    public SubfolderItemViewModel(ZoneItem source, ShellIconService iconService)
    {
        _iconService = iconService;
        Source = source;
        Source.PropertyChanged += OnSourcePropertyChanged;
        RebuildThumbnails();
    }

    private void OnSourcePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ZoneItem.SubItems))
        {
            RebuildThumbnails();
        }
        OnPropertyChanged(nameof(CellLayout));
        OnPropertyChanged(nameof(SubItemCount));
    }

    private void RebuildThumbnails()
    {
        ThumbnailVms.Clear();
        foreach (var item in Source.SubItems.Take(4))
        {
            ThumbnailVms.Add(new ZoneItemViewModel(item, _iconService));
        }
        // 显式通知四个缩略图属性 — 拖入/拖出 SubItems 后 2×2 预览即时刷新。
        OnPropertyChanged(nameof(Thumb0));
        OnPropertyChanged(nameof(Thumb1));
        OnPropertyChanged(nameof(Thumb2));
        OnPropertyChanged(nameof(Thumb3));
    }

    /// <summary>前 4 个 SubItem 的图标(空槽返回 null,由视图的占位底色兜底)。</summary>
    public ImageSource? Thumb0 => ThumbnailVms.Count > 0 ? ThumbnailVms[0].Icon : null;
    public ImageSource? Thumb1 => ThumbnailVms.Count > 1 ? ThumbnailVms[1].Icon : null;
    public ImageSource? Thumb2 => ThumbnailVms.Count > 2 ? ThumbnailVms[2].Icon : null;
    public ImageSource? Thumb3 => ThumbnailVms.Count > 3 ? ThumbnailVms[3].Icon : null;

    /// <summary>SubFolder 图标在分区里锁死 1×1(用户取消尺寸自适应;2×2 缩略图只
    /// 是视觉预览,不改变占位大小)。</summary>
    public int CellLayout => 1;

    public int SubItemCount => Source.SubItems.Count;

    public event PropertyChangedEventHandler? PropertyChanged;
    void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
