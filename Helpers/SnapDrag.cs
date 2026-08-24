using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace DesktopZones.Helpers;

/// <summary>
/// 手动窗口拖拽循环（替代同步阻塞的 Window.DragMove），配合
/// <see cref="SnapAlignmentService"/> 在拖拽过程中实时吸附 + 绘制对齐线。
/// 使用鼠标捕获 + PointToScreen 计算屏幕位移（经 DPI 换算回 DIP），
/// 窗口移动由 PreviewMouseMove / PreviewMouseLeftButtonUp 驱动。
/// </summary>
public sealed class SnapDrag
{
    readonly Window _window;
    Point _startScreen;   // 抓取时鼠标的屏幕像素坐标
    Point _startWin;      // 抓取时窗口的 DIP 位置
    bool _active;
    Action? _onComplete;

    /// <summary>Fired on every drag move with the cursor's current screen position
    /// (pixels). Used by ZoneWindow to hit-test drop targets during a title-bar drag.</summary>
    public event Action<Point>? DragMoved;
    public Point CurrentScreenPos { get; private set; }

    public SnapDrag(Window window)
    {
        _window = window;
        _window.PreviewMouseMove += OnPreviewMove;
        _window.PreviewMouseLeftButtonUp += OnPreviewUp;
        _window.LostMouseCapture += OnLostCapture;
    }

    public bool IsActive => _active;

    /// <summary>开始一次拖拽。可传入本次拖拽结束时的回调。</summary>
    public void Start(MouseEventArgs e, Action? onCompleted = null)
    {
        if (_active) return;
        try
        {
            _startScreen = _window.PointToScreen(e.GetPosition(_window));
        }
        catch
        {
            _startScreen = default;
        }
        _startWin = new Point(_window.Left, _window.Top);
        _onComplete = onCompleted;
        _active = true;
        try { _window.CaptureMouse(); } catch { }
        SnapAlignmentService.BeginDrag(_window);
    }

    void OnPreviewMove(object sender, MouseEventArgs e)
    {
        if (!_active) return;
        try
        {
            var cur = _window.PointToScreen(e.GetPosition(_window));
            CurrentScreenPos = cur;
            DragMoved?.Invoke(cur);
            var dpi = VisualTreeHelper.GetDpi(_window);
            double nx = _startWin.X + (cur.X - _startScreen.X) / dpi.DpiScaleX;
            double ny = _startWin.Y + (cur.Y - _startScreen.Y) / dpi.DpiScaleY;
            var adj = SnapAlignmentService.Adjust(_window, nx, ny);
            _window.Left = adj.X;
            _window.Top = adj.Y;
        }
        catch { /* window may be closing mid-drag */ }
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
        SnapAlignmentService.EndDrag(_window);
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
