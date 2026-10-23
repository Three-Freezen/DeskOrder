using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DesktopZones.Helpers;
using DesktopZones.Models;
using DesktopZones.Services;
using DesktopZones.ViewModels;

namespace DesktopZones.Views.Components;

public partial class SubfolderFlyout : UserControl
{
    public SubfolderFlyoutViewModel? ViewModel
    {
        get => DataContext as SubfolderFlyoutViewModel;
        set
        {
            if (ReferenceEquals(DataContext, value)) return;
            if (DataContext is SubfolderFlyoutViewModel oldVm && oldVm.HostSubItem != null)
                oldVm.HostSubItem.PropertyChanged -= OnHostItemChanged;
            DataContext = value;
            if (value != null)
            {
                value.HostSubItem.PropertyChanged += OnHostItemChanged;
                // ItemsControl 用新的 ItemsSource 重建 ItemsPanel 后才设 Rows/Columns
                // (Loaded 只触发一次,后续 reopen 不会重跑,所以这里每次赋值都延后重排)。
                Dispatcher.BeginInvoke(RefreshGrid, System.Windows.Threading.DispatcherPriority.Loaded);
            }
            // 正方形次级分区:每次换 VM 都按当前设置重断言一次(浮层在分区窗口里是复用
            // 的同一个控件,不重断言就会继承上一次打开的形态)。
            ApplySquareMode();
        }
    }

    // ── 设置 → 次级分区 的两项全局设置 ──
    // 每次用到时读一次 ConfigService(进程内静态缓存,零 IO):设置页改完立刻生效,
    // 不需要重启,也不需要把配置对象一路传进浮层。
    // 「默认展开位置」的读取见下方 PreferredCorner(AppConfig.SubfolderExpandCorner)。

    /// <summary>「正方形次级分区」= 展开后不显示名称与 ⚙ 按钮?</summary>
    public static bool SquareFlyout
    {
        get
        {
            try { return new DesktopZones.Services.ConfigService().Load().SubfolderSquare; }
            catch { return false; }
        }
    }

    /// <summary>按「正方形次级分区」显示/隐藏标题行(名称 + ⚙)。
    /// 隐藏后浮层只剩正方形的图标网格。</summary>
    public void ApplySquareMode()
    {
        if (HeaderRow == null) return;
        HeaderRow.Visibility = SquareFlyout ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>ponytail 2026-08-29: 宿主 SubFolder 模型变化 — 圆角/尖角切换时同步
    /// Popup HWND 的 Win11 DWM 圆角偏好。只改内容 CornerRadius 不够:Popup 是独立
    /// 顶层窗口,Win11 DWM 会给它自己的窗口形状(默认圆角),与内容裁剪叠加后
    /// "尖角不生效"。DWM 偏好与内容裁剪一起改,窗口形状才真正跟随设置。</summary>
    void OnHostItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ZoneItem.CornerRounded))
            ApplyCornerPref();
    }

    /// <summary>把宿主 SubFolder 的圆角偏好写到 Popup HWND(DWMWCP_ROUND/DONOTROUND)。
    /// 幂等,可在打开后任意时刻调用;句柄未就绪时无害跳过。</summary>
    public void ApplyCornerPref()
    {
        try
        {
            var hs = System.Windows.Interop.HwndSource.FromVisual(this) as System.Windows.Interop.HwndSource;
            if (hs == null || hs.Handle == IntPtr.Zero) return;
            bool rounded = ViewModel?.HostSubItem.CornerRounded ?? true;
            int pref = rounded ? DesktopZones.Helpers.NativeMethods.DWMWCP_ROUND
                               : DesktopZones.Helpers.NativeMethods.DWMWCP_DONOTROUND;
            DesktopZones.Helpers.NativeMethods.DwmSetWindowAttribute(hs.Handle,
                DesktopZones.Helpers.NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
        }
        catch { /* 老系统/句柄未就绪 — 不阻断开层 */ }
    }

    public event Action<SubfolderFlyout>? EditStyleRequested;
    /// <summary>Fired when the user starts dragging one of the flyout's inner items.
    /// Carries the dragged item VM + the source host SubFolder so ZoneWindow can move
    /// it back into the owning zone (drag-out).</summary>
    public event Action<ZoneItem, ZoneItemViewModel>? ItemDragOutRequested;

    // ── ponytail 2026-08-26: 内层图标与主分区同款操作 ──
    /// <summary>双击/右键菜单"打开"内层图标。</summary>
    public event Action<ZoneItemViewModel>? ItemOpenRequested;
    /// <summary>右键菜单"打开所在位置"(与主分区一致)。</summary>
    public event Action<ZoneItemViewModel>? ItemOpenLocationRequested;
    /// <summary>右键菜单"重命名"内层图标。</summary>
    public event Action<ZoneItemViewModel>? ItemRenameRequested;
    /// <summary>右键菜单"删除"内层图标(ZoneWindow 侧支持多选批量确认)。</summary>
    public event Action<ZoneItemViewModel>? ItemDeleteRequested;
    /// <summary>ponytail 2026-08-31: 右键菜单"清空回收站"内层图标(仅回收站图标可见)。
    /// 与主分区 ItemEmptyRecycle_Click 同款 — 由宿主执行 SHEmptyRecycleBinW(需要
    /// 宿主窗口句柄),与其它菜单项一样走事件委托。</summary>
    public event Action<ZoneItemViewModel>? ItemEmptyRecycleBinRequested;
    /// <summary>flyout 内部换位/删除完成后触发(模型已写回 HostSubItem.SubItems),
    /// 供 ZoneWindow 保存配置。</summary>
    public event Action? ItemsChanged;

    private Point _dragStart;
    private ZoneItemViewModel? _dragVm;
    private bool _dragArmed;
    private bool _dragging;       // 已越过拖拽阈值
    private bool _dragOutStarted; // 已转交 DoDragDrop(拖出主分区)
    private bool _reordered;      // 本次拖拽在 flyout 内部发生过换位

    static readonly Brush CellHoverBrush = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));

    public SubfolderFlyout()
    {
        InitializeComponent();
        // ponytail 2026-08-28: 诊断 — ⚙ 点击时灵时不灵,看 MouseDown 是否到达浮层/源是什么。
        // 2026-08-28 修复:分区侧(桌面层窗口托管的 Popup)在混合 DPI 下,WPF 的输入坐标
        // 映射与真实几何矛盾(Win32 rect 与 PointToScreen/PointFromScreen 互相矛盾)——
        // 用户肉眼点 ⚙,命中测试却算到标题名字区域,⚙ 的 MouseLeftButtonDown 永远不触发
        // (实测分区侧按下 pos=(112.8,22.4) 落在名字区,面板侧同款 Popup 映射正常)。
        // 不再依赖 WPF 命中测试:用 GetCursorPos(物理光标)+ GetWindowRect(浮层真实矩形)
        // + ActualWidth/Height 比例把点击点反算成布局坐标,落在 ⚙ 区域内即触发打开。
        this.PreviewMouseDown += (_, e) =>
        {
            var src = e.OriginalSource as System.Windows.DependencyObject;
            string chain = "";
            while (src != null && chain.Length < 120)
            {
                chain += src.GetType().Name + "<";
                src = System.Windows.Media.VisualTreeHelper.GetParent(src);
            }
            System.Diagnostics.Trace.WriteLine($"[SubFlyout] PreviewMouseDown src={e.OriginalSource?.GetType().Name} chain={chain} pos={e.GetPosition(this)} captured={System.Windows.Input.Mouse.Captured is not null}");
            DzTrace.Log($"[SubFlyout] PreviewMouseDown src={e.OriginalSource?.GetType().Name} pos={e.GetPosition(this)} captured={System.Windows.Input.Mouse.Captured is not null} host={ViewModel?.HostSubItem.Name}");
            if (e.ChangedButton == System.Windows.Input.MouseButton.Left && TryHandleGearPress())
            {
                DzTrace.Log($"[SubFlyout] PreviewMouseDown: 物理命中 ⚙ → 打开样式设置(绕过 WPF 命中测试)");
                e.Handled = true; // 阻断冒泡,StyleBtn_Click 兜底路径不再重复触发
                OpenStyleEditor();
            }
        };
        Loaded += (_, _) => SizeInnerGrid();
        // ponytail 2026-08-26: 长按拖拽批量选择(与主分区 marquee 同款)。
        // 空白处按下 → 立即框选;单元格长按 350ms → 框选(快速拖动仍是换位)。
        InnerItems.MouseLeftButtonDown += InnerItems_MouseLeftButtonDown;
        MouseMove += Flyout_MouseMove;
        MouseLeftButtonUp += Flyout_MouseLeftButtonUp;
        // ponytail 2026-08-26: 右键菜单打开时,菜单 Popup 会拿到鼠标 → Flyout 收到
        // WM_MOUSELEAVE。ZoneWindow 的"移出 200ms 自动关闭"必须被抑制,否则右键后
        // 200ms flyout 就开始播关闭动画(右键图标层"触发关闭动画"的根源)。
        ContextMenuOpening += OnContextMenuOpening;
    }

    /// <summary>ponytail 2026-08-28: 用 Win32 物理真相判断本次按下是否落在 ⚙ 按钮上。
    /// 分区侧(桌面层窗口托管的 Popup)混合 DPI 下 WPF 的输入坐标映射与真实几何矛盾,
    /// 路由命中测试点不中 ⚙ — 这里把物理光标位置按 HWND 真实矩形比例反算成布局坐标,
    /// 再按 RenderTransform(打开动画的 scale + 锚点)反解,与 ⚙ 的布局矩形比对。
    /// ⚙ 布局矩形:内容 Margin=8,DockPanel 行高 26(Dock=Right,宽 32)→
    /// [ActualWidth-40, ActualWidth-8] × [8, 34](外扩 2px 容差)。</summary>
    bool TryHandleGearPress()
    {
        try
        {
            // 正方形次级分区:标题行(含 ⚙)整行隐藏 → 这块区域现在是图标网格,不能再
            // 按"⚙ 的固定布局矩形"抢命中,否则点右上角那颗图标会打开样式设置。
            if (HeaderRow == null || HeaderRow.Visibility != Visibility.Visible) return false;
            var hs = System.Windows.Interop.HwndSource.FromVisual(this) as System.Windows.Interop.HwndSource;
            if (hs == null || hs.Handle == IntPtr.Zero) return false;
            if (!GetWindowRect(hs.Handle, out var r)) return false;
            if (!GetCursorPos(out var cur)) return false;
            double wpx = r.Right - r.Left, hpx = r.Bottom - r.Top;
            if (wpx <= 0 || hpx <= 0 || ActualWidth <= 0 || ActualHeight <= 0) return false;
            // 物理偏移 → 布局空间(HWND 尺寸与布局尺寸的比值)。
            double kx = ActualWidth / wpx, ky = ActualHeight / hpx;
            double px = (cur.X - r.Left) * kx, py = (cur.Y - r.Top) * ky;
            // RenderTransform = [平移(-c), scale(s), 平移(+c)] → p' = c + s·(p − c);
            // 反解 p = c + (p' − c) / s(打开动画进行中按下也精确)。
            double sx = Math.Max(0.01, FlyoutScale.ScaleX), sy = Math.Max(0.01, FlyoutScale.ScaleY);
            double cx = FlyoutTranslateBack.X, cy = FlyoutTranslateBack.Y;
            double lx = cx + (px - cx) / sx;
            double ly = cy + (py - cy) / sy;
            var gear = new Rect(Math.Max(0, ActualWidth - 42), 6, 34, 30);
            return gear.Contains(new Point(lx, ly));
        }
        catch { return false; }
    }

    bool _ctxMenuOpen;
    /// <summary>Flyout 打开着右键菜单时返回 true(ZoneWindow 抑制自动关闭)。</summary>
    public bool IsContextMenuOpen => _ctxMenuOpen;

    void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // 从事件 Source 链上找持有 ContextMenu 的元素 — ContextMenuOpening 的
        // e.Source 可能是格子里的内层元素(Image/TextBlock),直接读 fe.ContextMenu
        // 会拿不到,导致 _ctxMenuOpen 不置位 → MouseLeave 启动的关闭 timer 不被抑制。
        var cm = FindContextMenu(e.Source as DependencyObject);
        if (cm != null)
        {
            cm.Closed -= OnCtxMenuClosed;
            cm.Closed += OnCtxMenuClosed;
            _ctxMenuOpen = true;
        }
    }

    /// <summary>ponytail 2026-08-31: 内层图标右键菜单打开 — 显隐「清空回收站」。
    /// 挂在 ContextMenu 自己的 Opened 上(模板里的 <c>Opened="CtxMenu_Opened"</c>)：
    /// 与主分区 ItemMenu_Opened 同一条路径；改之前走的是 SubfolderFlyout 的
    /// ContextMenuOpening，实测该事件到不了 UserControl(Popup 里的模板菜单)，
    /// 菜单项一直停在 Collapsed，右键看不到「清空回收站」。sender 是菜单本身，
    /// 其 DataContext 由模板沿格子继承到菜单项，即当前图标。</summary>
    void CtxMenu_Opened(object sender, RoutedEventArgs e)
    {
        // 子菜单(如"新建 ▸")也会冒泡到同一个 handler — 只认菜单根。
        if (sender is not ContextMenu cm || !ReferenceEquals(cm, e.OriginalSource)) return;
        var vm = cm.DataContext as ZoneItemViewModel;
        bool isRecycle = vm?.Type == ItemType.ShellLocation && ShellIconService.IsRecycleBin(vm.TargetPath);
        foreach (var entry in cm.Items)
        {
            if (entry is not MenuItem mi || mi.Name != "CtxEmptyRecycle") continue;
            mi.Visibility = isRecycle ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    static ContextMenu? FindContextMenu(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is FrameworkElement fe && fe.ContextMenu != null) return fe.ContextMenu;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    void OnCtxMenuClosed(object sender, RoutedEventArgs e)
    {
        _ctxMenuOpen = false;
        // 菜单 Popup 打开时抢走了鼠标捕获 — 关闭后立即还给 Flyout,保证
        // "点击外部关闭"判定基准不变(否则后续 Flyout 内部点击会被误判外部)。
        ReCaptureToFlyout();
    }

    void SizeInnerGrid()
    {
        // Adaptive grid sizing — 图标超出后网格依次扩大:2×2 → 3×3 → 4×4 → … → 9×9
        // (cols = ⌈√n⌉,最少 2;容量永远 ≥ 数量,不会溢出裁剪)。
        if (ViewModel == null) return;
        int count = ViewModel.ItemVms.Count;
        int cols = Math.Max(2, (int)Math.Ceiling(Math.Sqrt(count)));
        var grid = FindVisualChild<UniformGrid>(this);
        if (grid != null) { grid.Rows = cols; grid.Columns = cols; }
    }

    public void RefreshGrid() => SizeInnerGrid();

    // ── 真玻璃(DWM) — 与主分区同配方:优先给 Popup HWND 开模糊,失败才用渐变兜底 ──

    /// <summary>尝试给 Popup 子窗口开真玻璃。成功返回 true(调用方隐藏渐变兜底)。
    /// ponytail 2026-08-29: 只走 accent、跳过经典 blurbehind — 经典 blur 在 Popup 上
    /// 会把背景压暗 30%("浮层比分区深"的根源),accent 成功 = 与分区同款着色玻璃,
    /// 失败则由调用方显示渐变兜底。
    /// ponytail 2026-08-30: 一体化 — 填充并入玻璃 tint(EnableBlurComposite),成功时
    /// 背景层(UnifiedBackgroundBrush)为 null,DWM accent 统一携带填充+玻璃。</summary>
    public bool TryApplyRealGlass(SubfolderFill fill)
    {
        if (!fill.HasGlass) return false;
        try
        {
            var src = PresentationSource.FromVisual(this) as System.Windows.Interop.HwndSource;
            if (src == null || src.Handle == IntPtr.Zero) return false;

            // ponytail 2026-09-26 方案①: 材质 = 自绘壁纸背板(模糊半径真正生效)。浮层打开后
            // 不再移动,所以用一次性静态画刷(裁剪图+着色+噪点)即可,不必维护 Viewbox 绑定。
            if (AcrylicHelper.ResolveSelfDrawn())
            {
                var rect = NativeMethods.GetWindowRect(src.Handle, out var wr)
                    ? new Rect(wr.Left, wr.Top, Math.Max(1, wr.Right - wr.Left), Math.Max(1, wr.Bottom - wr.Top))
                    : Rect.Empty;
                var brush = rect.IsEmpty ? null : WallpaperBackdrop.CreateStaticBrush(
                    AcrylicHelper.ResolveSelfDrawnRadiusPx(fill.GlassBlur), rect,
                    AcrylicHelper.ResolveBackdropTint(fill.FillHex, fill.FillOpacity / 100.0, fill.GlassMode!,
                        fill.GlassTintOpacity, fill.GlassTintLuminosity),
                    AcrylicHelper.ResolveNoiseOpacity(fill.GlassMaterial));
                if (brush != null)
                {
                    ViewModel?.SetWallpaperBackdrop(brush);
                    AcrylicHelper.DisableBlur(src.Handle);
                    DzTrace.Log($"[SubFlyout] TryApplyRealGlass(wallpaper backdrop): host={ViewModel?.HostSubItem.Name} r={fill.GlassBlur} mat={fill.GlassMaterial}");
                    return true;
                }
            }
            ViewModel?.SetWallpaperBackdrop(null);

            var r = AcrylicHelper.EnableBlurComposite(src.Handle, fill.GlassBlur,
                fill.FillHex, fill.FillOpacity / 100.0, fill.GlassMode!,
                fill.GlassTintOpacity, fill.GlassTintLuminosity, skipClassicBlur: true,
                // ponytail 2026-09-26: 材质只取背板类型;经典 blurbehind 在 Popup 上会压暗背景
                // (见上),所以浮层恒 skipClassicBlur —— 材质的「经典模糊」维度在浮层上不生效。
                accentState: AcrylicHelper.ResolveAccentState(fill.GlassMaterial));
            DzTrace.Log($"[SubFlyout] TryApplyRealGlass(composite accent-only): host={ViewModel?.HostSubItem.Name} success={r.Success} err={r.Error} mode={fill.GlassMode}");
            return r.Success;
        }
        catch (Exception ex)
        {
            DzTrace.Log($"[SubFlyout] TryApplyRealGlass 异常: {ex.Message}");
            return false;
        }
    }

    /// <summary>关闭 Popup 子窗口上的玻璃(Closed 时调用,失败无害)。</summary>
    public void DisableGlass()
    {
        try
        {
            var src = PresentationSource.FromVisual(this) as System.Windows.Interop.HwndSource;
            if (src != null && src.Handle != IntPtr.Zero) AcrylicHelper.DisableBlur(src.Handle);
        }
        catch { }
    }

    // ── 内层图标拖拽:flyout 内部实时换位 + 拖出主分区(委托 ZoneWindow) ──

    void Item_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ZoneItemViewModel vm)
        {
            // 双击 = 打开(与主分区一致)。
            if (e.ClickCount == 2)
            {
                ItemOpenRequested?.Invoke(vm);
                e.Handled = true;
                return;
            }
            // Ctrl+点选切换多选(与主分区一致,不进入拖拽)。
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                vm.IsSelected = !vm.IsSelected;
                e.Handled = true;
                return;
            }
            // 普通点选:选中点击项(已选中则保持多选,与资源管理器一致)。
            if (!vm.IsSelected && ViewModel != null)
            {
                foreach (var o in ViewModel.ItemVms) o.IsSelected = false;
                vm.IsSelected = true;
            }
            _dragVm = vm;
            _dragStart = e.GetPosition(this);
            _dragArmed = true;
            _dragging = false;
            _dragOutStarted = false;
            _reordered = false;
            // 拖拽期间换位只暂存在 ItemVms 里,松手才写回 HostSubItem.SubItems —
            // 避免每次 Move 触发 SubItems INPC 重建集合(容器销毁 → 捕获丢失)。
            ViewModel?.BeginTransientReorder();
            fe.CaptureMouse();
            // ponytail: 单元格捕获会顶掉 Flyout 的子树捕获,导致后续点击被
            // "点击外部"误判 → 每次交互结束后把捕获还给 Flyout 自己。
            // 长按 350ms → 进入框选(与主分区 marquee 同款)。
            _marqueeStart = e.GetPosition(this);
            StartMarqueeHoldTimer();
        }
    }

    void Item_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed || _dragVm == null || ViewModel == null) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            CancelDrag(sender);
            return;
        }
        var local = e.GetPosition(this);
        var d = local - _dragStart;
        if (!_dragging
            && Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        if (!_dragging)
        {
            _dragging = true;
            StopMarqueeHoldTimer(); // 快速拖动胜出 → 取消长按框选
            if (sender is FrameworkElement fe)
            {
                fe.Opacity = 0.6;
                fe.RenderTransformOrigin = new Point(0.5, 0.5);
                fe.RenderTransform = new ScaleTransform(1.05, 1.05);
            }
        }

        // 光标离开 flyout 边界 → 一次性转交拖出(DoDragDrop 阻塞直到拖放结束)。
        if (!_dragOutStarted
            && (local.X < 0 || local.Y < 0 || local.X > ActualWidth || local.Y > ActualHeight))
        {
            _dragOutStarted = true;
            HideDropBar();
            StopMarqueeHoldTimer();
            // 还原拖拽期间的临时换位(拖出只移动被拖项,其余保持模型顺序)。
            ViewModel.CancelTransientReorder();
            if (sender is FrameworkElement fe2) { try { fe2.ReleaseMouseCapture(); } catch { } }
            ItemDragOutRequested?.Invoke(ViewModel.HostSubItem, _dragVm);
            return;
        }
        if (_dragOutStarted) return;

        // flyout 内部 → 实时换位 + 蓝色竖条指示(与主分区拖拽同款反馈)。
        int hover = HoveredIndex(e.GetPosition(InnerItems));
        int cur = ViewModel.ItemVms.IndexOf(_dragVm);
        UpdateDropBar(hover, cur);
        if (hover >= 0 && hover != cur)
        {
            ViewModel.ItemVms.Move(cur, hover); // 暂存换位,松手才写回模型
            _reordered = true;
        }
    }

    void Item_MouseUp(object sender, MouseButtonEventArgs e)
    {
        bool wasDragging = _dragging;
        bool wasDragOut = _dragOutStarted;
        bool reordered = _reordered;
        StopMarqueeHoldTimer();
        _dragArmed = false;
        _dragging = false;
        _dragOutStarted = false;
        _reordered = false;
        ResetDragVisual(sender);
        HideDropBar();
        _dragVm = null;
        if (wasDragging && !wasDragOut)
        {
            if (reordered)
            {
                // 一次性写回 HostSubItem.SubItems → 图标格缩略图 / 网格自动刷新。
                ViewModel?.CommitTransientReorder();
                ItemsChanged?.Invoke();
            }
            else
            {
                ViewModel?.CancelTransientReorder();
            }
        }
        else
        {
            ViewModel?.CancelTransientReorder();
        }
        ReCaptureToFlyout();
    }

    void CancelDrag(object sender)
    {
        _dragArmed = false;
        _dragging = false;
        _dragOutStarted = false;
        _reordered = false;
        StopMarqueeHoldTimer();
        ResetDragVisual(sender);
        HideDropBar();
        _dragVm = null;
        ViewModel?.CancelTransientReorder();
        ReCaptureToFlyout();
    }

    void ResetDragVisual(object sender)
    {
        if (sender is FrameworkElement fe)
        {
            fe.Opacity = 1.0;
            fe.RenderTransform = null;
        }
    }

    /// <summary>把鼠标子树捕获还给 Flyout 自己,保证"点击外部关闭"判定基准正确
    /// (单元格捕获会顶掉 Flyout 的捕获,让后续 Flyout 内部点击被误判为外部)。</summary>
    void ReCaptureToFlyout()
    {
        try { Mouse.Capture(this, CaptureMode.SubTree); } catch { }
    }

    // ── 长按拖拽批量选择(与主分区 marquee 同款) ──
    // 单元格长按 350ms → 进入框选;ItemsControl 空白处按下 → 立即框选。
    // 框选矩形与主分区 MarqueeRect 同款视觉;选中的项 IsSelected=true(Ctrl+A /
    // Delete 批量删除共享同一选中状态)。

    const double MarqueeHoldMs = 350;
    bool _marqueeArmed;   // 单元格长按计时中
    bool _marqueeActive;  // 框选拖拽进行中
    bool _marqueeMoved;
    Point _marqueeStart;
    HashSet<Guid>? _marqueeStartSel;
    System.Windows.Threading.DispatcherTimer? _marqueeHoldTimer;
    System.Windows.Shapes.Rectangle? _marqueeRect;

    /// <summary>框选进行中(供 ZoneWindow 抑制"鼠标移出自动关闭")。</summary>
    public bool IsMarqueeActive => _marqueeArmed || _marqueeActive;

    void StartMarqueeHoldTimer()
    {
        _marqueeArmed = true;
        _marqueeHoldTimer?.Stop();
        _marqueeHoldTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(MarqueeHoldMs)
        };
        _marqueeHoldTimer.Tick += (_, _) =>
        {
            _marqueeHoldTimer.Stop();
            if (!_marqueeArmed) return;
            _marqueeArmed = false;
            // 长按成立 → 拖拽脚手架让位给框选。
            _marqueeActive = true;
            _marqueeMoved = false;
            _marqueeStartSel = ViewModel?.ItemVms.Where(i => i.IsSelected).Select(i => i.Id).ToHashSet();
            _marqueeStart = Mouse.GetPosition(this);
            _dragVm = null;
            _dragArmed = false;
            _dragging = false;
            _dragOutStarted = false;
            _reordered = false;
            ViewModel?.CancelTransientReorder();
            ReCaptureToFlyout();
        };
        _marqueeHoldTimer.Start();
    }

    void StopMarqueeHoldTimer()
    {
        _marqueeArmed = false;
        _marqueeHoldTimer?.Stop();
        _marqueeHoldTimer = null;
    }

    /// <summary>ItemsControl 空白处(格间距/网格外空白)按下 → 立即框选;单元格按下
    /// 由单元格自己处理(点选/拖拽/长按)。</summary>
    void InnerItems_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsOnInnerItem(e.OriginalSource as System.Windows.DependencyObject)) return;
        StopMarqueeHoldTimer();
        _marqueeActive = true;
        _marqueeMoved = false;
        _marqueeStart = e.GetPosition(this);
        _marqueeStartSel = ViewModel?.ItemVms.Where(i => i.IsSelected).Select(i => i.Id).ToHashSet();
        try { Mouse.Capture(this, CaptureMode.SubTree); } catch { }
        e.Handled = true;
    }

    static bool IsOnInnerItem(System.Windows.DependencyObject? d)
    {
        while (d != null)
        {
            if (d is FrameworkElement { DataContext: ZoneItemViewModel }) return true;
            d = VisualTreeHelper.GetParent(d);
        }
        return false;
    }

    void Flyout_MouseMove(object sender, MouseEventArgs e)
    {
        if (_marqueeArmed)
        {
            // 长按计时期间快速移动 → 取消框选(交给拖拽换位)。
            var p = e.GetPosition(this);
            if (Math.Abs(p.X - _marqueeStart.X) >= SystemParameters.MinimumHorizontalDragDistance
                || Math.Abs(p.Y - _marqueeStart.Y) >= SystemParameters.MinimumVerticalDragDistance)
                StopMarqueeHoldTimer();
            return;
        }
        if (!_marqueeActive) return;
        var pt = e.GetPosition(this);
        if (!_marqueeMoved)
        {
            if (Math.Abs(pt.X - _marqueeStart.X) < 4 && Math.Abs(pt.Y - _marqueeStart.Y) < 4) return;
            _marqueeMoved = true;
        }
        UpdateMarqueeRect(pt);
    }

    void Flyout_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        bool active = _marqueeActive;
        bool moved = _marqueeMoved;
        StopMarqueeHoldTimer();
        _marqueeActive = false;
        _marqueeMoved = false;
        _marqueeStartSel = null;
        HideMarqueeRect();
        if (active && moved)
        {
            e.Handled = true; // 框选手势已消费(选择在拖动中实时应用)
        }
        else if (active && !moved)
        {
            // 空白处普通点击 → 清空选择(资源管理器行为,与主分区一致)。
            if (ViewModel != null)
                foreach (var i in ViewModel.ItemVms) i.IsSelected = false;
        }
    }

    void UpdateMarqueeRect(Point current)
    {
        double x1 = Math.Min(_marqueeStart.X, current.X);
        double y1 = Math.Min(_marqueeStart.Y, current.Y);
        double w = Math.Abs(current.X - _marqueeStart.X);
        double h = Math.Abs(current.Y - _marqueeStart.Y);
        var rect = EnsureMarqueeRect();
        rect.Visibility = Visibility.Visible;
        Canvas.SetLeft(rect, x1);
        Canvas.SetTop(rect, y1);
        rect.Width = w;
        rect.Height = h;
        if (ViewModel == null) return;
        var r = new Rect(x1, y1, w, h);
        for (int i = 0; i < InnerItems.Items.Count; i++)
        {
            if (InnerItems.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement fe) continue;
            if (fe.DataContext is not ZoneItemViewModel vm) continue;
            var p0 = fe.TransformToVisual(this).Transform(new Point(0, 0));
            bool inRect = r.IntersectsWith(new Rect(p0.X, p0.Y, Math.Max(1, fe.ActualWidth), Math.Max(1, fe.ActualHeight)));
            vm.IsSelected = inRect || (_marqueeStartSel?.Contains(vm.Id) ?? false);
        }
    }

    System.Windows.Shapes.Rectangle EnsureMarqueeRect()
    {
        if (_marqueeRect == null)
        {
            _marqueeRect = new System.Windows.Shapes.Rectangle
            {
                RadiusX = 3,
                RadiusY = 3,
                StrokeThickness = 1.2,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
                Fill = new SolidColorBrush(Color.FromArgb(0x26, 0x40, 0x90, 0xE2))
            };
            _marqueeRect.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Brush.Accent");
            DropLayer.Children.Add(_marqueeRect);
        }
        return _marqueeRect;
    }

    void HideMarqueeRect()
    {
        if (_marqueeRect != null) _marqueeRect.Visibility = Visibility.Collapsed;
    }

    /// <summary>命中检测:光标落在哪个内层格子里(返回 ItemVms 下标,-1 = 空白)。</summary>
    int HoveredIndex(Point pt)
    {
        for (int i = 0; i < InnerItems.Items.Count; i++)
        {
            if (InnerItems.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement fe)
            {
                var r = fe.TransformToVisual(InnerItems)
                          .TransformBounds(new Rect(0, 0, fe.ActualWidth, fe.ActualHeight));
                if (r.Contains(pt)) return i;
            }
        }
        return -1;
    }

    // ── 蓝色竖条指示(与主分区 UpdateDropIndicator 同款视觉) ──

    System.Windows.Shapes.Rectangle? _dropBar;
    System.Windows.Shapes.Rectangle EnsureDropBar()
    {
        if (_dropBar == null)
        {
            _dropBar = new System.Windows.Shapes.Rectangle
            {
                Width = 3,
                RadiusX = 1.5,
                RadiusY = 1.5,
                Opacity = 0.95,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            _dropBar.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Brush.Accent");
            DropLayer.Children.Add(_dropBar);
        }
        return _dropBar;
    }

    void UpdateDropBar(int hoverIdx, int curIdx)
    {
        if (hoverIdx < 0 || hoverIdx == curIdx)
        {
            HideDropBar();
            return;
        }
        if (InnerItems.ItemContainerGenerator.ContainerFromIndex(hoverIdx) is not FrameworkElement fe)
        {
            HideDropBar();
            return;
        }
        var r = fe.TransformToVisual(DropLayer)
                  .TransformBounds(new Rect(0, 0, fe.ActualWidth, fe.ActualHeight));
        var bar = EnsureDropBar();
        bar.Height = Math.Max(12, r.Height - 6);
        // 向后拖 → 竖条贴目标格右缘;向前拖 → 贴左缘。
        Canvas.SetLeft(bar, hoverIdx > curIdx ? r.Right + 1 : r.Left - 4);
        Canvas.SetTop(bar, r.Top + 3);
        bar.Visibility = Visibility.Visible;
    }

    void HideDropBar()
    {
        if (_dropBar != null) _dropBar.Visibility = Visibility.Collapsed;
    }

    // ── 悬停高光(与主分区 Item_Enter/Item_Leave 同款) ──

    void Cell_Enter(object sender, MouseEventArgs e)
    {
        if (sender is Grid g) g.Background = CellHoverBrush;
    }

    void Cell_Leave(object sender, MouseEventArgs e)
    {
        if (sender is Grid g) g.Background = Brushes.Transparent;
    }

    // ── 内层图标右键菜单(与主分区同款:打开/重命名/删除) ──

    void CtxOpen_Click(object sender, RoutedEventArgs e)
    {
        if (MenuVm(sender) is ZoneItemViewModel vm) ItemOpenRequested?.Invoke(vm);
    }
    void CtxOpenLocation_Click(object sender, RoutedEventArgs e)
    {
        if (MenuVm(sender) is ZoneItemViewModel vm) ItemOpenLocationRequested?.Invoke(vm);
    }
    void CtxRename_Click(object sender, RoutedEventArgs e)
    {
        if (MenuVm(sender) is ZoneItemViewModel vm) ItemRenameRequested?.Invoke(vm);
    }
    void CtxDelete_Click(object sender, RoutedEventArgs e)
    {
        if (MenuVm(sender) is ZoneItemViewModel vm) ItemDeleteRequested?.Invoke(vm);
    }
    /// <summary>ponytail 2026-08-31: 「清空回收站」— 与主分区同款,实际操作由宿主完成
    /// (SHEmptyRecycleBinW 要窗口句柄,且清空后要刷新本分区/浮层里所有回收站图标)。</summary>
    void CtxEmptyRecycle_Click(object sender, RoutedEventArgs e)
    {
        if (MenuVm(sender) is ZoneItemViewModel vm) ItemEmptyRecycleBinRequested?.Invoke(vm);
    }
    static ZoneItemViewModel? MenuVm(object s)
        => s is MenuItem mi && mi.DataContext is ZoneItemViewModel vm ? vm : null;

    /// <summary>ponytail 2026-08-28: ⚙ 点击点的屏幕 DIP 坐标 — 宿主弹出样式浮窗时
    /// 用作锚点,让窗口贴着点击点开而非落历史 rect(历史位置可能压住光标,导致
    /// ✕ 被下一次点击误关)。多屏混合 DPI 下 WPF Popup 的 PointToScreen 会给出与
    /// Win32 真实几何矛盾的坐标(实测 -1927 这类副屏错值),锚点因此只走
    /// GetCursorPos + 所在显示器 DPI 换算;失败(取光标失败)时为 null,宿主走旧路径。</summary>
    public Point? StyleBtnScreenDip { get; private set; }

    /// <summary>⚙ 打开样式设置的唯一入口 — PreviewMouseDown 物理命中检测与
    /// StyleBtn_Click 兜底路径共用。</summary>
    void OpenStyleEditor()
    {
        // 按下瞬间光标位置 == ⚙ 点击位置 — 用物理光标坐标,不再读 Popup 自身的
        // PointToScreen(那正是混合 DPI 下坐标错乱的来源)。
        StyleBtnScreenDip = MonitorHelper.CursorDip();
        DzTrace.Log($"[SubFlyout] OpenStyleEditor: host={ViewModel?.HostSubItem.Name} anchor={StyleBtnScreenDip?.ToString() ?? "null"} subscribers={(EditStyleRequested != null)}");
        EditStyleRequested?.Invoke(this);
    }

    /// <summary>兜底路径:WPF 路由命中测试正常时(面板侧等),预览层未拦截,气泡到
    /// ⚙ Border 的 MouseLeftButtonDown 才到这里。预览层已处理时 e.Handled=true,
    /// 这里直接返回避免重复打开。</summary>
    void StyleBtn_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled) return;
        e.Handled = true;
        DzTrace.Log($"[SubFlyout] StyleBtn_Click(兜底): host={ViewModel?.HostSubItem.Name}");
        OpenStyleEditor();
    }

    // ponytail: 悬停高光统一走 Helpers/HoverHighlight 那一个配方(#30FFFFFF)，离开时回到透明
    // （原来是常驻 #30FFFFFF + 悬停 #55FFFFFF 两级，与分区/面板/组件不一致）。
    // 不透明度也一并对齐统一值，浮层上的 ⚙ 不再比主界面亮一档。
    void StyleBtn_Enter(object sender, MouseEventArgs e)
    {
        if (sender is not Border b) return;
        b.Opacity = Helpers.HoverHighlight.ChromeOpacity;
        b.Background = Helpers.HoverHighlight.Brush;
    }
    void StyleBtn_Leave(object sender, MouseEventArgs e)
    {
        if (sender is not Border b) return;
        b.Opacity = Helpers.HoverHighlight.ChromeOpacity;
        b.Background = Brushes.Transparent;
    }

    // ── Drop-in (delegated to ZoneWindow) ──

    void Items_DragEnter(object sender, DragEventArgs e)
    {
        if (ViewModel == null) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
        e.Effects = DragDropEffects.Move; e.Handled = true;
    }
    void Items_DragOver(object sender, DragEventArgs e)
    {
        if (ViewModel == null) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
        e.Effects = DragDropEffects.Move; e.Handled = true;
    }
    void Items_Drop(object sender, DragEventArgs e) { /* delegated to ZoneWindow */ }

    static T? FindVisualChild<T>(DependencyObject parent) where T : FrameworkElement
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            var deeper = FindVisualChild<T>(child);
            if (deeper != null) return deeper;
        }
        return null;
    }

    // ── Open/close animation + position (ponytail 2026-08-26: 与分区和面板共用)
    // ZoneWindow 与 PanelWindow 各自管 popup 生命周期(IsOpen / token / IsContextMenuOpen
    // 抑制),但动画 + 定位 + click-outside 全在这一个 UserControl 上。 ──

    /// <summary>把 Flyout 复位到关闭态(scale 0,不透明 0),供打开前调用,避免残留动画帧。
    /// 不透明度取 0 而非 1:Fade 动效的打开要从 0 淡入(NormalizeFor 里非 Fade kind
    /// 会自行把 Opacity 抬回 1,只有 Fade 保留 0 作为 from)。
    ///
    /// ponytail 2026-10 —— 「浮层离图标老远」的真正根因就在这里:
    /// Popup 是**按子控件 RenderTransform 之后的包围盒**来定位窗口的。带着上一次打开
    /// 留下的缩放锚点 c 打开,窗口就落在 (offset − c) 而不是 offset;而两个宿主窗口随后
    /// 写回的 offset 往往与上次**相同**(同一个次级分区反复点开、位置一模一样),依赖属性
    /// 值没变 → WPF 不会重新定位 → 这个 −c 的偏差就固定留在那里。
    /// 分区窗口是**复用同一个** SubfolderFlyout 控件(锚点跨次残留),所以它每次都偏;
    /// 面板每次 new 一个(c 从 0 开始),所以面板一直是对的 —— 这就是"分区位置不对、
    /// 面板一直准"的由来。开层前把锚点清零,打开那一刻变换是恒等的,定位才精确。</summary>
    public void ResetToClosed()
    {
        var st = FlyoutScale;
        st.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        st.ScaleX = 0; st.ScaleY = 0;
        BeginAnimation(UIElement.OpacityProperty, null);
        Opacity = 0;
        SetAnchor(default);   // 必须:见上面这段说明(否则窗口被 −c 带偏)
    }

    /// <summary>把缩放锚点 c 写入 TransformGroup 的 [移至原点(-c), Scale, 移回(+c)] —
    /// 与 HoverExpandBehavior.ApplyOrigin 同款组合,动画以 c(图标中心)为原点缩放。</summary>
    public void SetAnchor(Point c)
    {
        FlyoutTranslateBack.X = c.X;
        FlyoutTranslateBack.Y = c.Y;
        FlyoutTranslateToOrigin.X = -c.X;
        FlyoutTranslateToOrigin.Y = -c.Y;
    }

    /// <summary>打开动画:按 HostSubItem.HoverAnimation 类型播 ScaleExpand/Fade/
    /// VerticalExpand/DirectionalExpand/BounceExpand/None。duration 200ms /
    /// HoverExpandSpeed,onComplete 走 Completed 事件(from==to 直接同步 fire)。</summary>
    public void AnimateOpen()
    {
        var vm = ViewModel;
        if (vm == null) return;
        var kind = vm.HostSubItem.HoverAnimation;
        NormalizeFlyoutFor(isExpanded: true, kind);
        var dur = new Duration(TimeSpan.FromMilliseconds(200.0 / Math.Max(0.1, vm.HostSubItem.HoverExpandSpeed)));
        switch (kind)
        {
            case HoverExpandAnimationKind.None:
                ApplyFlyoutFinal(isExpanded: true, kind);
                return;
            case HoverExpandAnimationKind.Fade:
                AnimateFlyoutOpacity(Opacity, 1, dur, EasingMode.EaseOut, null);
                return;
            case HoverExpandAnimationKind.VerticalExpand:
                AnimateFlyoutScaleY(FlyoutScale.ScaleY, 1, dur, EasingMode.EaseOut, null);
                return;
            case HoverExpandAnimationKind.DirectionalExpand:
                AnimateFlyoutScaleX(FlyoutScale.ScaleX, 1, dur, EasingMode.EaseOut, null);
                return;
            case HoverExpandAnimationKind.BounceExpand:
                AnimateFlyoutBounce(isExpand: true, dur, null);
                return;
            default: // ScaleExpand
                AnimateFlyoutScaleXY(FlyoutScale.ScaleX, 1, dur, EasingMode.EaseOut, null);
                return;
        }
    }

    /// <summary>关闭动画:与打开互为镜像(EaseIn vs EaseOut),flyout 缩回原点。</summary>
    public void AnimateClose(Action? onComplete)
    {
        var vm = ViewModel;
        var kind = vm != null ? vm.HostSubItem.HoverAnimation : HoverExpandAnimationKind.ScaleExpand;
        double speed = vm != null ? Math.Max(0.1, vm.HostSubItem.HoverExpandSpeed) : 1.0;
        NormalizeFlyoutFor(isExpanded: false, kind);
        var dur = new Duration(TimeSpan.FromMilliseconds(200.0 / speed));
        switch (kind)
        {
            case HoverExpandAnimationKind.None:
                ApplyFlyoutFinal(isExpanded: false, kind);
                onComplete?.Invoke();
                return;
            case HoverExpandAnimationKind.Fade:
                AnimateFlyoutOpacity(Opacity, 0, dur, EasingMode.EaseIn, onComplete);
                return;
            case HoverExpandAnimationKind.VerticalExpand:
                AnimateFlyoutScaleY(FlyoutScale.ScaleY, 0, dur, EasingMode.EaseIn, onComplete);
                return;
            case HoverExpandAnimationKind.DirectionalExpand:
                AnimateFlyoutScaleX(FlyoutScale.ScaleX, 0, dur, EasingMode.EaseIn, onComplete);
                return;
            case HoverExpandAnimationKind.BounceExpand:
                AnimateFlyoutBounce(isExpand: false, dur, onComplete);
                return;
            default: // ScaleExpand
                AnimateFlyoutScaleXY(FlyoutScale.ScaleX, 0, dur, EasingMode.EaseIn, onComplete);
                return;
        }
    }

    /// <summary>ponytail: 关闭时若 flyout 处于 None 之外的过渡态,目标值强制收敛,免得
    /// 动画 in-flight 时 BeginAnimation(null) 残留中间帧被下一次打开带走。</summary>
    void NormalizeFlyoutFor(bool isExpanded, HoverExpandAnimationKind kind)
    {
        var st = FlyoutScale;
        double sx = st.ScaleX, sy = st.ScaleY, op = Opacity;
        st.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        BeginAnimation(UIElement.OpacityProperty, null);
        st.ScaleX = sx; st.ScaleY = sy;
        Opacity = op;

        switch (kind)
        {
            case HoverExpandAnimationKind.VerticalExpand: st.ScaleX = 1; break; // stable axis
            case HoverExpandAnimationKind.DirectionalExpand: st.ScaleY = 1; break;
            case HoverExpandAnimationKind.Fade: st.ScaleX = 1; st.ScaleY = 1; break; // stable
            case HoverExpandAnimationKind.None:
                if (isExpanded) { st.ScaleX = 1; st.ScaleY = 1; Opacity = 1; }
                else { st.ScaleX = 0; st.ScaleY = 0; Opacity = 0; }
                break;
        }
        if (kind != HoverExpandAnimationKind.Fade && kind != HoverExpandAnimationKind.None)
            Opacity = 1;
    }

    /// <summary>Port of HoverExpandBehavior.ApplyFinal for the None kind.</summary>
    void ApplyFlyoutFinal(bool isExpanded, HoverExpandAnimationKind kind)
    {
        var st = FlyoutScale;
        double target = isExpanded ? 1 : 0;
        switch (kind)
        {
            case HoverExpandAnimationKind.VerticalExpand: st.ScaleX = 1; st.ScaleY = target; break;
            case HoverExpandAnimationKind.DirectionalExpand: st.ScaleX = target; st.ScaleY = 1; break;
            default: st.ScaleX = target; st.ScaleY = target; break;
        }
        Opacity = isExpanded ? 1 : (kind == HoverExpandAnimationKind.Fade ? 0 : 1);
    }

    void AnimateFlyoutScaleXY(double from, double to, Duration dur, EasingMode ease, Action? onComplete)
    {
        var st = FlyoutScale;
        if (Math.Abs(from - to) < 1e-9) { st.ScaleX = to; st.ScaleY = to; onComplete?.Invoke(); return; }
        var ax = new DoubleAnimation(from, to, dur) { EasingFunction = new CubicEase { EasingMode = ease } };
        var ay = new DoubleAnimation(from, to, dur) { EasingFunction = new CubicEase { EasingMode = ease } };
        bool done = false;
        Action fireOnce = () => { if (done) return; done = true; onComplete?.Invoke(); };
        ax.Completed += (_, _) => { st.ScaleX = to; fireOnce(); };
        ay.Completed += (_, _) => { st.ScaleY = to; fireOnce(); };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, ax);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, ay);
    }

    void AnimateFlyoutScaleX(double from, double to, Duration dur, EasingMode ease, Action? onComplete)
    {
        var st = FlyoutScale;
        if (Math.Abs(from - to) < 1e-9) { st.ScaleX = to; onComplete?.Invoke(); return; }
        var ax = new DoubleAnimation(from, to, dur) { EasingFunction = new CubicEase { EasingMode = ease } };
        ax.Completed += (_, _) => { st.ScaleX = to; onComplete?.Invoke(); };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, ax);
    }

    void AnimateFlyoutScaleY(double from, double to, Duration dur, EasingMode ease, Action? onComplete)
    {
        var st = FlyoutScale;
        if (Math.Abs(from - to) < 1e-9) { st.ScaleY = to; onComplete?.Invoke(); return; }
        var ay = new DoubleAnimation(from, to, dur) { EasingFunction = new CubicEase { EasingMode = ease } };
        ay.Completed += (_, _) => { st.ScaleY = to; onComplete?.Invoke(); };
        st.BeginAnimation(ScaleTransform.ScaleYProperty, ay);
    }

    void AnimateFlyoutOpacity(double from, double to, Duration dur, EasingMode ease, Action? onComplete)
    {
        if (Math.Abs(from - to) < 1e-9) { Opacity = to; onComplete?.Invoke(); return; }
        var anim = new DoubleAnimation(from, to, dur) { EasingFunction = new CubicEase { EasingMode = ease } };
        anim.Completed += (_, _) => { Opacity = to; onComplete?.Invoke(); };
        BeginAnimation(UIElement.OpacityProperty, anim);
    }

    void AnimateFlyoutBounce(bool isExpand, Duration dur, Action? onComplete)
    {
        var st = FlyoutScale;
        if (!isExpand && Math.Abs(st.ScaleX) < 1e-9)
        {
            st.ScaleX = 0; st.ScaleY = 0; onComplete?.Invoke(); return;
        }
        var bounce = new DoubleAnimationUsingKeyFrames();
        var ease = new BounceEase { Bounces = 2, Bounciness = 2, EasingMode = EasingMode.EaseOut };
        if (isExpand)
        {
            bounce.KeyFrames.Add(new EasingDoubleKeyFrame(st.ScaleX, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            bounce.KeyFrames.Add(new EasingDoubleKeyFrame(1.08, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(dur.TimeSpan.TotalMilliseconds * 0.6)), ease));
            bounce.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(dur.TimeSpan)));
        }
        else
        {
            var squashTime = TimeSpan.FromMilliseconds(dur.TimeSpan.TotalMilliseconds * 0.45);
            bounce.KeyFrames.Add(new EasingDoubleKeyFrame(st.ScaleX, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            bounce.KeyFrames.Add(new EasingDoubleKeyFrame(0.85, KeyTime.FromTimeSpan(squashTime), ease));
            bounce.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(dur.TimeSpan),
                new CubicEase { EasingMode = EasingMode.EaseOut }));
        }
        double final = isExpand ? 1 : 0;
        bool done = false;
        Action fireOnce = () => { if (done) return; done = true; onComplete?.Invoke(); };
        bounce.Completed += (_, _) => { st.ScaleX = final; st.ScaleY = final; fireOnce(); };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, bounce);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, bounce);
    }

    /// <summary>浮层与图标的间距(DIP) —— 贴着图标展开,只留一条缝。</summary>
    public const double FlyoutGap = 8;

    /// <summary>浮层贴在图标哪个方位展开。设置 → 次级分区 → 默认展开位置。</summary>
    public enum FlyoutCorner
    {
        TopLeft,
        BottomLeft,
        TopRight,
        BottomRight,
    }

    /// <summary>设置里的「默认展开位置」(默认左下;非法值也回落到左下)。</summary>
    public static FlyoutCorner PreferredCorner
    {
        get
        {
            try { return ParseCorner(new DesktopZones.Services.ConfigService().Load().SubfolderExpandCorner); }
            catch { return FlyoutCorner.BottomLeft; }
        }
    }

    /// <summary>配置字符串 → 方位。"TopLeft"/"BottomLeft"/"TopRight"/"BottomRight";
    /// 其余一律左下(默认值)。</summary>
    public static FlyoutCorner ParseCorner(string? raw) => raw switch
    {
        "TopLeft" => FlyoutCorner.TopLeft,
        "TopRight" => FlyoutCorner.TopRight,
        "BottomRight" => FlyoutCorner.BottomRight,
        _ => FlyoutCorner.BottomLeft,
    };

    /// <summary>方位 → 配置字符串(设置页保存用)。</summary>
    public static string CornerToString(FlyoutCorner c) => c switch
    {
        FlyoutCorner.TopLeft => "TopLeft",
        FlyoutCorner.TopRight => "TopRight",
        FlyoutCorner.BottomRight => "BottomRight",
        _ => "BottomLeft",
    };

    /// <summary>确定性展开定位 + 动画原点。返回 (屏幕位置 pos, 缩放锚点 c)。
    ///
    /// 四个方位(以图标为参照):左上 / 左下 / 右上 / 右下 —— 浮层整个落在图标那一角,
    /// 与图标间距恒为 gap。首选方位放不下工作区时**逐级换向**:
    ///   ① 首选方位
    ///   ② 先左右翻(例:右下放不下 → 左下)
    ///   ③ 再上下翻(例:右下放不下 → 右上)
    ///   ④ 最后斜对角(左上)
    /// 四个都放不下工作区时,取"可见面积最大"的那一个再夹进工作区(不会甩到很远)。
    /// c = 图标中心 - pos(浮层局部坐标,允许负值)。
    ///
    /// ponytail 2026-10:几个反复踩过的坑都写在这:
    /// ① 锚点必须是**图标元素本身**(名字 ItemIcon),不能是图标格/列表行/卡片容器 ——
    ///    容器宽度会被当成"图标宽度",格子越大浮层离图标越远。
    /// ② 工作区取**图标所在显示器**的(GetMonitorInfo.rcWork),不是只有主屏的
    ///    SystemParameters.WorkArea。
    /// ③ 调用方打开前必须先 ResetToClosed() 清掉缩放锚点,否则 Popup 会按上一次
    ///    变换后的包围盒定位,窗口被 −c 带偏(见 ResetToClosed 的说明)。
    /// 全程 DIP:PointToScreen 返回物理像素要除以 DPI 缩放;rcWork 也是物理像素,同样除;
    /// Popup 的 AbsolutePoint offset 本身就是 DIP。</summary>
    /// <param name="corner">首选方位。</param>
    /// <param name="gap">与图标的间距(DIP),四个方位一致。</param>
    /// <param name="alignToIconTop">true = "下方"方位改用「浮层顶边 = 图标顶边 + gap」
    /// 的历史口径(列表视图用它保持先前观感);false = 浮层整个在图标下方。</param>
    public static (Point pos, Point c) ComputePosAndAnchor(
        FrameworkElement? container, Size flyoutSize, FlyoutCorner corner, double gap, bool alignToIconTop = false)
    {
        const double margin = 8;  // 与工作区边缘的留白
        const double eps = 0.5;   // 浮点容差(算"放得下"时用)
        gap = Math.Max(0, gap);
        double sx = 1, sy = 1;
        try
        {
            var d = VisualTreeHelper.GetDpi((System.Windows.Media.Visual?)container!);
            if (d.DpiScaleX > 0) sx = d.DpiScaleX;
            if (d.DpiScaleY > 0) sy = d.DpiScaleY;
        }
        catch { }

        // 锚点矩形(DIP)。拿不到(未连 PresentationSource / 尺寸为 0)就退到光标处 ——
        // 绝不回落到"工作区中心"那种一眼可见的大跳。
        Rect a;
        if (!TryGetAnchorRect(container, sx, sy, out a))
        {
            var fallback = TryGetCursorDip(sx, sy, out var cur)
                ? cur
                : new Point(SystemParameters.WorkArea.Left + SystemParameters.WorkArea.Width / 2,
                            SystemParameters.WorkArea.Top + SystemParameters.WorkArea.Height / 2);
            a = new Rect(fallback.X, fallback.Y, 0, 0);
        }

        var wa = WorkAreaOf(a, sx, sy);
        double w = Math.Max(0, flyoutSize.Width), h = Math.Max(0, flyoutSize.Height);
        var bounds = new Rect(wa.Left + margin, wa.Top + margin,
                              Math.Max(0, wa.Width - margin * 2), Math.Max(0, wa.Height - margin * 2));

        // 某个方位的候选矩形(right = 图标右侧,down = 图标下方)。
        Rect Candidate(bool right, bool down)
        {
            double x = right ? a.Right + gap : a.Left - gap - w;
            double y = down
                ? (alignToIconTop ? a.Top + gap : a.Bottom + gap)
                : a.Top - gap - h;
            return new Rect(x, y, w, h);
        }

        bool preferRight = corner is FlyoutCorner.TopRight or FlyoutCorner.BottomRight;
        bool preferDown = corner is FlyoutCorner.BottomLeft or FlyoutCorner.BottomRight;
        // 顺序:首选 → 左右翻 → 上下翻 → 斜对角
        (bool right, bool down)[] order =
        {
            (preferRight, preferDown),
            (!preferRight, preferDown),
            (preferRight, !preferDown),
            (!preferRight, !preferDown),
        };

        Rect chosen = Candidate(preferRight, preferDown);
        double bestVisible = -1;
        bool fitted = false;
        foreach (var (right, down) in order)
        {
            var r = Candidate(right, down);
            bool inside = r.Left >= bounds.Left - eps && r.Top >= bounds.Top - eps
                       && r.Right <= bounds.Right + eps && r.Bottom <= bounds.Bottom + eps;
            if (inside) { chosen = r; fitted = true; break; }
            double visible = VisibleArea(r, bounds);
            if (visible > bestVisible) { bestVisible = visible; chosen = r; }
        }

        // 夹进工作区(四个方位都放不下时,这一步保证浮层仍然完整可见/尽量可见)
        double cx = Math.Min(Math.Max(chosen.Left, bounds.Left), Math.Max(bounds.Left, bounds.Right - w));
        double cy = Math.Min(Math.Max(chosen.Top, bounds.Top), Math.Max(bounds.Top, bounds.Bottom - h));

        var pos = new Point(cx, cy);
        var iconCenter = new Point(a.Left + a.Width / 2, a.Top + a.Height / 2);
        var c = new Point(iconCenter.X - pos.X, iconCenter.Y - pos.Y);
        DzTrace.Log($"[SubFlyout] 定位候选: corner={corner} gap={gap:F0} 图标=({a.Left:F0},{a.Top:F0}){a.Width:F0}x{a.Height:F0} " +
                    $"浮层={w:F0}x{h:F0} fitted={fitted} pos=({pos.X:F1},{pos.Y:F1})");
        return (pos, c);
    }

    /// <summary>两个矩形相交面积(全放不下时用来挑"最能看清"的那个方位)。</summary>
    static double VisibleArea(Rect r, Rect bounds)
    {
        double l = Math.Max(r.Left, bounds.Left), t = Math.Max(r.Top, bounds.Top);
        double rr = Math.Min(r.Right, bounds.Right), b = Math.Min(r.Bottom, bounds.Bottom);
        return rr <= l || b <= t ? 0 : (rr - l) * (b - t);
    }

    /// <summary>锚点元素在屏幕上的矩形(DIP)。用两个角点换算再相减,比
    /// "左上角 + ActualWidth"更贴近真实渲染尺寸(LayoutRounding/缩放取整)。
    /// 返回 false = 元素还没连上 PresentationSource 或尺寸为 0。</summary>
    static bool TryGetAnchorRect(FrameworkElement? el, double sx, double sy, out Rect rect)
    {
        rect = Rect.Empty;
        if (el == null || !el.IsVisible) return false;
        try
        {
            double w = el.ActualWidth, h = el.ActualHeight;
            if (w <= 0 || h <= 0) return false;
            var tl = el.PointToScreen(new Point(0, 0));
            var br = el.PointToScreen(new Point(w, h));
            double left = Math.Min(tl.X, br.X) / sx, top = Math.Min(tl.Y, br.Y) / sy;
            double right = Math.Max(tl.X, br.X) / sx, bottom = Math.Max(tl.Y, br.Y) / sy;
            if (right - left <= 0 || bottom - top <= 0) return false;
            rect = new Rect(left, top, right - left, bottom - top);
            return true;
        }
        catch { return false; }
    }

    /// <summary>锚点所在**显示器**的工作区(DIP,已排除任务栏)。多屏下
    /// SystemParameters.WorkArea 只描述主屏,拿它判翻侧会让副屏上的浮层永远判成
    /// "放不下";GetMonitorInfo 的 rcWork 是物理像素,按同一块屏的 DPI 缩放换成 DIP。
    /// 任何一步失败都退回 SystemParameters.WorkArea(至少不会比旧行为更差)。</summary>
    static Rect WorkAreaOf(Rect anchor, double sx, double sy)
    {
        try
        {
            var probe = new Win32Point
            {
                X = (int)Math.Round((anchor.Left + Math.Max(1, anchor.Width) / 2) * sx),
                Y = (int)Math.Round((anchor.Top + Math.Max(1, anchor.Height) / 2) * sy)
            };
            var mon = MonitorFromPoint(probe, MONITOR_DEFAULTTONEAREST);
            if (mon != IntPtr.Zero)
            {
                var mi = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(MONITORINFO)) };
                if (GetMonitorInfo(mon, ref mi))
                {
                    var r = mi.rcWork;
                    double rw = r.Right - r.Left, rh = r.Bottom - r.Top;
                    if (rw > 0 && rh > 0)
                        return new Rect(r.Left / sx, r.Top / sy, rw / sx, rh / sy);
                }
            }
        }
        catch { }
        return SystemParameters.WorkArea;
    }

    /// <summary>物理光标 → DIP 屏幕坐标(锚点不可用时的兜底定位)。</summary>
    static bool TryGetCursorDip(double sx, double sy, out Point pt)
    {
        pt = default;
        if (!GetCursorPos(out var p)) return false;
        pt = new Point(p.X / sx, p.Y / sy);
        return true;
    }

    // ── Click-outside(打开 flyout 后挂上,关闭时拆掉;捕获子树防止 Flyout 内按下被误判) ──

    /// <summary>Fired when a mouse-down is detected outside the flyout's captured subtree.
    /// Caller decides whether to close (ZoneWindow / PanelWindow 各有自己的关窗策略)。
    /// MouseButtonEventArgs 透传过去,让 caller 做 popup 源/坐标判定(IsContextMenuOpen
    /// 之类的 flyout 状态 caller 自己也能查;这里只透传 WPF 原始事件)。</summary>
    public event Action<System.Windows.Input.MouseButtonEventArgs>? ClickOutsideRequested;

    bool _clickOutsideHooked;
    public void HookClickOutside()
    {
        if (_clickOutsideHooked) return;
        _clickOutsideHooked = true;
        PreventActivation();
        DzTrace.Log($"[SubFlyout] HookClickOutside: host={ViewModel?.HostSubItem.Name} hwnd={PopupHwndText()}");
        try { System.Windows.Input.Mouse.Capture(this, System.Windows.Input.CaptureMode.SubTree); } catch { }
        System.Windows.Input.Mouse.AddPreviewMouseDownOutsideCapturedElementHandler(this, OnPreviewMouseDownOutsideCapturedElement);
        // ponytail 2026-08-28: 多屏混合 DPI 下 ⚙ 点击坐标错乱(pos 出现 -1927 这类副屏
        // 坐标) — 开层即自检:Win32 的 HWND 真实位置 vs WPF 认为的位置 vs 光标。
        DumpPopupGeometry("开层即检");
        Dispatcher.BeginInvoke(new Action(() => DumpPopupGeometry("开层+400ms")),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    string PopupHwndText()
    {
        var hs = System.Windows.Interop.HwndSource.FromVisual(this) as System.Windows.Interop.HwndSource;
        return hs != null && hs.Handle != IntPtr.Zero ? $"0x{hs.Handle.ToInt64():X}" : "无HWND";
    }

    /// <summary>ponytail 2026-08-28: 给 Popup 根 HWND 加 WS_EX_NOACTIVATE。实测点击浮层
    /// 内部会把宿主分区窗口顶到失活(WPF Popup 默认可激活),失活又触发宿主的层级回落
    /// (SetWindowPos)与整套输入状态翻搅 — ⚙ 按下命中后,抬起在翻搅中重新命中失败,
    /// 表现为"⚙ 时灵时不灵"。NOACTIVATE 后层内点击不再惊动宿主,按下/抬起走同一份
    /// 安静的输入状态。幂等,每次开层调用。</summary>
    public void PreventActivation()
    {
        try
        {
            var hs = System.Windows.Interop.HwndSource.FromVisual(this) as System.Windows.Interop.HwndSource;
            if (hs == null || hs.Handle == System.IntPtr.Zero) { DzTrace.Log("[SubFlyout] PreventActivation: 无HWND"); return; }
            int ex = DesktopZones.Helpers.NativeMethods.GetWindowLong(hs.Handle, DesktopZones.Helpers.NativeMethods.GWL_EXSTYLE);
            DesktopZones.Helpers.NativeMethods.SetWindowLong(hs.Handle,
                DesktopZones.Helpers.NativeMethods.GWL_EXSTYLE,
                ex | DesktopZones.Helpers.NativeMethods.WS_EX_NOACTIVATE);
            DzTrace.Log($"[SubFlyout] PreventActivation: hwnd=0x{hs.Handle.ToInt64():X} exStyle=0x{ex:X8} → 0x{(ex | DesktopZones.Helpers.NativeMethods.WS_EX_NOACTIVATE):X8}");
        }
        catch (Exception ex) { DzTrace.Log($"[SubFlyout] PreventActivation 异常: {ex.Message}"); }
    }

    /// <summary>物理光标当前是否落在浮层屏幕矩形内。判定失败一律返回 true
    /// (保守:调用方"光标在层外才关层"的逻辑不误关)。
    /// ponytail 2026-08-28: 矩形走 Win32 GetWindowRect(物理像素,真实几何)而非
    /// PointToScreen — 混合 DPI 下 Popup 的 PointToScreen 与 Win32 几何矛盾,会误判。
    /// ponytail 2026-08-29: margin 默认 24px 只用于"失活且光标在层外才关层"的防误伤
    /// 场景;悬停自动收回轮询必须用 0px(浮层就开在图标右 8px,24px 会把光标停在
    /// 图标上误判成"在层内",计时永不启动 → 不自动收回)。</summary>
    public bool ContainsScreenCursor() => ContainsScreenCursor(24);

    public bool ContainsScreenCursor(double margin)
    {
        try
        {
            if (!GetCursorPos(out var p)) return true;
            var hs = System.Windows.Interop.HwndSource.FromVisual(this) as System.Windows.Interop.HwndSource;
            if (hs == null || hs.Handle == IntPtr.Zero || !GetWindowRect(hs.Handle, out var r)) return true;
            return p.X >= r.Left - margin && p.X <= r.Right + margin
                && p.Y >= r.Top - margin && p.Y <= r.Bottom + margin;
        }
        catch { return true; }
    }

    /// <summary>ponytail 2026-08-28: 诊断 — 打印浮层 HWND 的 Win32 物理矩形、WPF 侧
    /// PointToScreen/PointFromScreen、DPI、光标物理位置与 WPF 相对坐标。若 GetWindowRect
    /// 与 PointToScreen 不一致,即为 WPF popup 定位/坐标翻译错乱的直接证据。</summary>
    void DumpPopupGeometry(string tag)
    {
        try
        {
            var hs = System.Windows.Interop.HwndSource.FromVisual(this) as System.Windows.Interop.HwndSource;
            string win32 = "无HWND";
            if (hs != null && hs.Handle != IntPtr.Zero && GetWindowRect(hs.Handle, out var r))
                win32 = $"({r.Left},{r.Top})-({r.Right},{r.Bottom})";
            string toScreen = "失败", fromScreen = "失败";
            try { var p = PointToScreen(new Point(0, 0)); toScreen = $"({p.X:F0},{p.Y:F0})"; } catch { }
            try { var p = PointFromScreen(new Point(0, 0)); fromScreen = $"({p.X:F0},{p.Y:F0})"; } catch { }
            var cur = GetCursorPos(out var cpt) ? $"{cpt.X},{cpt.Y}" : "失败";
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
            var host = ViewModel?.HostSubItem.Name ?? "?";
            DzTrace.Log(
                $"[SubFlyout] 几何[{tag}] host={host}: Win32={win32} PointToScreen(0,0)={toScreen} PointFromScreen(0,0)={fromScreen} " +
                $"DPI={dpi.DpiScaleX:F2} 光标物理=({cur}) MouseRel={Mouse.GetPosition(this)} 虚拟桌面=({SystemParameters.VirtualScreenLeft:F0},{SystemParameters.VirtualScreenTop:F0} {SystemParameters.VirtualScreenWidth:F0}x{SystemParameters.VirtualScreenHeight:F0})");
        }
        catch (Exception ex)
        {
            DzTrace.Log($"[SubFlyout] 几何[{tag}] 失败: {ex.Message}");
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(System.IntPtr hWnd, out Win32Rect rect);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Win32Point pt);

    // 浮层定位用「锚点所在显示器」的工作区(见 WorkAreaOf):SystemParameters.WorkArea
    // 只有主屏,多屏下会把副屏上的浮层判成"放不下"然后翻到错误一侧。
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Win32Point pt, uint dwFlags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public Win32Rect rcMonitor;
        public Win32Rect rcWork;
        public uint dwFlags;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Win32Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Win32Point
    {
        public int X, Y;
    }

    public void UnhookClickOutside()
    {
        if (!_clickOutsideHooked) return;
        _clickOutsideHooked = false;
        System.Windows.Input.Mouse.RemovePreviewMouseDownOutsideCapturedElementHandler(this, OnPreviewMouseDownOutsideCapturedElement);
        if (System.Windows.Input.Mouse.Captured == this)
        {
            try { ReleaseMouseCapture(); } catch { }
        }
    }

    void OnPreviewMouseDownOutsideCapturedElement(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        ClickOutsideRequested?.Invoke(e);
    }
}
