using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace DesktopZones.Helpers;

/// <summary>
/// 手动窗口缩放循环（替代同步阻塞的 WM_NCLBUTTONDOWN 系统缩放），配合
/// <see cref="SnapAlignmentService.AdjustResize"/> 在缩放过程中实时吸附
/// 移动的边缘并绘制对齐线。与 <see cref="SnapDrag"/> 一样使用鼠标捕获 +
/// PointToScreen 计算屏幕位移（经 DPI 换算回 DIP），由 PreviewMouseMove /
/// PreviewMouseLeftButtonUp 驱动。
/// </summary>
public sealed class SnapResize
{
    readonly Window _window;
    Point _startScreen;
    Rect _startBounds;
    bool _active;
    double _minW = 20, _minH = 20;
    bool _moveLeft, _moveTop, _moveRight, _moveBottom;
    Action? _onComplete;

    public SnapResize(Window window)
    {
        _window = window;
        _window.PreviewMouseMove += OnPreviewMove;
        _window.PreviewMouseLeftButtonUp += OnPreviewUp;
        _window.LostMouseCapture += OnLostCapture;
    }

    public bool IsActive => _active;

    /// <summary>开始一次缩放。<paramref name="moveLeft"/>/<paramref name="moveTop"/>/
    /// <paramref name="moveRight"/>/<paramref name="moveBottom"/> 标记哪些边缘跟随鼠标。</summary>
    public void Start(MouseButtonEventArgs e,
        bool moveLeft, bool moveTop, bool moveRight, bool moveBottom,
        double minWidth, double minHeight, Action? onCompleted = null)
    {
        if (_active) return;
        try { _startScreen = _window.PointToScreen(e.GetPosition(_window)); }
        catch { _startScreen = default; }
        _startBounds = new Rect(_window.Left, _window.Top, _window.Width, _window.Height);
        _moveLeft = moveLeft; _moveTop = moveTop; _moveRight = moveRight; _moveBottom = moveBottom;
        _minW = Math.Max(20, minWidth);
        _minH = Math.Max(20, minHeight);
        _onComplete = onCompleted;
        _active = true;
        try { _window.CaptureMouse(); } catch { }
        SnapAlignmentService.BeginResize(_window);
    }

    void OnPreviewMove(object sender, MouseEventArgs e)
    {
        if (!_active) return;
        try
        {
            var cur = _window.PointToScreen(e.GetPosition(_window));
            var dpi = VisualTreeHelper.GetDpi(_window);
            double dx = (cur.X - _startScreen.X) / dpi.DpiScaleX;
            double dy = (cur.Y - _startScreen.Y) / dpi.DpiScaleY;

            var r = _startBounds;
            double left = r.Left, top = r.Top, right = r.Right, bottom = r.Bottom;
            if (_moveLeft) left = r.Left + dx;
            if (_moveRight) right = r.Right + dx;
            if (_moveTop) top = r.Top + dy;
            if (_moveBottom) bottom = r.Bottom + dy;

            // 阻止移动边越过锚点边，保证候选矩形有效。
            if (right < left) { if (_moveLeft) left = right; else right = left; }
            if (bottom < top) { if (_moveTop) top = bottom; else bottom = top; }

            var candidate = new Rect(left, top, right - left, bottom - top);
            var snapped = SnapAlignmentService.AdjustResize(
                _window, candidate, _moveLeft, _moveTop, _moveRight, _moveBottom, _minW, _minH);

            _window.Left = snapped.Left;
            _window.Top = snapped.Top;
            _window.Width = snapped.Width;
            _window.Height = snapped.Height;
        }
        catch { /* 窗口可能在缩放过程中被关闭 */ }
    }

    void OnPreviewUp(object sender, MouseButtonEventArgs e)
    {
        if (_active) Finish();
    }

    void OnLostCapture(object sender, MouseEventArgs e)
    {
        if (_active) Finish();
    }

    void Finish()
    {
        if (!_active) return;
        _active = false;
        try { _window.ReleaseMouseCapture(); } catch { }
        SnapAlignmentService.EndResize(_window);
        var cb = _onComplete; _onComplete = null;
        cb?.Invoke();
    }

    public void Detach()
    {
        _window.PreviewMouseMove -= OnPreviewMove;
        _window.PreviewMouseLeftButtonUp -= OnPreviewUp;
        _window.LostMouseCapture -= OnLostCapture;
    }
}
