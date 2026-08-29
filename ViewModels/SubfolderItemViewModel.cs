using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using DesktopZones.Helpers;
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
        // ponytail 2026-08-26: 填充相关字段变化 → 刷新填充层画刷/背景图。
        // (逐个属性判断太琐碎,Source 变化频率低,全量通知即可。)
        OnPropertyChanged(nameof(OverrideFill));
        OnPropertyChanged(nameof(BoxFill));
        OnPropertyChanged(nameof(BgImage));
        OnPropertyChanged(nameof(BgImageBrush));
        OnPropertyChanged(nameof(BgOpacity));
        OnPropertyChanged(nameof(GlassBrush));
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

    // ── 尺寸跟随分区网格 ──
    // ponytail 2026-08-28: 2×2 盒子曾锁死 56px(面板卡片基线),改网格后不缩放,
    // 小格子(默认 65)里盒子+名字 72px 超格被裁、大格子里偏小。现由宿主把格子
    // 边长写入 GridCellSize(ZoneItemViewModel.ItemSize = Zone.GridSize),盒子
    // 与普通图标等高(GridSize - 隐藏名字?6:18),名字行画在格内余量里。
    // 面板路径(SetSource)不写该值,默认 74-18 = 56,面板 80×80 卡片观感不变。

    /// <summary>宿主格子边长(DIP)。分区路径由 SubfolderItemView 从外层 VM 同步。</summary>
    double _gridCellSize = 74;
    public double GridCellSize
    {
        get => _gridCellSize;
        set { _gridCellSize = value; OnPropertyChanged(); OnPropertyChanged(nameof(BoxSize)); OnPropertyChanged(nameof(NameMaxWidth)); }
    }

    /// <summary>镜像视图的 HideName(隐藏应用名时名字行收起、盒子几乎占满整格)。</summary>
    bool _hideName;
    public bool HideName
    {
        get => _hideName;
        set { _hideName = value; OnPropertyChanged(); OnPropertyChanged(nameof(BoxSize)); }
    }

    /// <summary>2×2 缩略图盒子边长 — 与同格子普通图标的 Image 等高。</summary>
    public double BoxSize => Math.Max(8, GridCellSize - (HideName ? 6 : 18));

    /// <summary>名字最大宽度 — 与普通图标 NameMaxWidth 同比例(格子的 90%)。</summary>
    public double NameMaxWidth => Math.Max(32, GridCellSize * 0.9);

    // ── 填充跟随主分区 ──
    // 跟随(默认):图标格填充层为空 → 透明,主分区主体填充(颜色/液态玻璃/背景图)
    // 直接透出 — 这就是"同步主分区主体部分的填充"。不跟随时用 Source 的 override
    // 字段渲染自身填充。边框固定(#40FFFFFF),不参与同步。

    /// <summary>不跟随时解析出的自身填充;跟随主分区时为 null(透出主分区)。</summary>
    public SubfolderFill? OverrideFill => Source.FillFollowsZone ? null : SubfolderFill.FromOverride(Source);

    public System.Windows.Media.Brush? BoxFill => OverrideFill?.FillBrush;

    public System.Windows.Media.ImageSource? BgImage => OverrideFill?.BgImage;

    /// <summary>背景图 ImageBrush — 自动裁剪适应格子,不参与布局测量。</summary>
    public System.Windows.Media.Brush? BgImageBrush => OverrideFill?.BgImageBrush;

    public double BgOpacity => OverrideFill?.BgOpacity01 ?? 0;

    public System.Windows.Media.Brush? GlassBrush
    {
        get
        {
            var b = OverrideFill?.GlassBrush;
            DzTrace.Log($"[SubEdit] Icon.GlassBrush: name={Source.Name} follow={Source.FillFollowsZone} glassOn={Source.EnableLiquidGlass} mode={Source.GlassColorMode} brush={(b != null ? "gradient" : "null")}");
            return b;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
