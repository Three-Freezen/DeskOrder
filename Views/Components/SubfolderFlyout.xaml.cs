using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using DesktopZones.Models;
using DesktopZones.ViewModels;

namespace DesktopZones.Views.Components;

public partial class SubfolderFlyout : UserControl
{
    public SubfolderFlyoutViewModel? ViewModel
    {
        get => DataContext as SubfolderFlyoutViewModel;
        set
        {
            DataContext = value;
            if (value != null)
            {
                // ItemsControl 用新的 ItemsSource 重建 ItemsPanel 后才设 Rows/Columns
                // (Loaded 只触发一次,后续 reopen 不会重跑,所以这里每次赋值都延后重排)。
                Dispatcher.BeginInvoke(RefreshGrid, System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }
    }

    public event Action<SubfolderFlyout>? EditStyleRequested;
    /// <summary>Fired when the user starts dragging one of the flyout's inner items.
    /// Carries the dragged item VM + the source host SubFolder so ZoneWindow can move
    /// it back into the owning zone (drag-out).</summary>
    public event Action<ZoneItem, ZoneItemViewModel>? ItemDragOutRequested;

    private Point _dragStart;
    private ZoneItemViewModel? _dragVm;
    private bool _dragArmed;

    public SubfolderFlyout()
    {
        InitializeComponent();
        Loaded += (_, _) => SizeInnerGrid();
    }

    void SizeInnerGrid()
    {
        // Adaptive grid sizing:
        //   1-4   items → 2×2
        //   5-9   items → 3×3
        //   10+   items → 4×4  (>16 wraps / scrolls on ItemsControl)
        if (ViewModel == null) return;
        int count = ViewModel.ItemVms.Count;
        int cols = count <= 4 ? 2 : count <= 9 ? 3 : 4;
        var grid = (UniformGrid?)FindName("InnerGrid");
        if (grid != null) { grid.Rows = cols; grid.Columns = cols; }
    }

    public void RefreshGrid() => SizeInnerGrid();

    void StyleBtn_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        EditStyleRequested?.Invoke(this);
    }

    void StyleBtn_Enter(object sender, MouseEventArgs e)
    {
        if (sender is Border b) b.Background = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
    }
    void StyleBtn_Leave(object sender, MouseEventArgs e)
    {
        if (sender is Border b) b.Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
    }

    // ── Drag-out of flyout inner items (delegated to ZoneWindow) ──
    void Item_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ZoneItemViewModel vm)
        {
            _dragVm = vm;
            _dragStart = e.GetPosition(this);
            _dragArmed = true;
            fe.CaptureMouse();
        }
    }

    void Item_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed || _dragVm == null || ViewModel == null) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _dragArmed = false; _dragVm = null;
            return;
        }
        var d = e.GetPosition(this) - _dragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragArmed = false;
        if (sender is FrameworkElement fe) { try { fe.ReleaseMouseCapture(); } catch { } }
        ItemDragOutRequested?.Invoke(ViewModel.HostSubItem, _dragVm);
        _dragVm = null;
    }

    void Item_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragArmed = false;
        _dragVm = null;
    }

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
}
