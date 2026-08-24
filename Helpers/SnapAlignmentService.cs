using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using DesktopZones.Services;
using DesktopZones.Views;

namespace DesktopZones.Helpers;

/// <summary>
/// 组件自适应对齐：拖动桌面组件（分区 / 时钟 / 日历 / 便签，面板除外）时，
/// 与其他组件的边缘/中心线对齐，绘制半透明蓝色对齐线，并在接近时吸附。
/// 对齐线画在一个覆盖整个虚拟屏幕的透明置顶窗口上（WS_EX_TRANSPARENT +
/// WS_EX_NOACTIVATE，既不抢焦点也不挡鼠标）。
/// </summary>
public static class SnapAlignmentService
{
    /// <summary>吸附判定距离（DIP）。</summary>
    const double Threshold = 8.0;

    static readonly ConfigService Config = new();

    static Window? _overlay;
    static Canvas? _canvas;
    static bool _enabled;

    /// <summary>当前是否开启自动对齐（读取 config.AutoAlign）。</summary>
    public static bool IsEnabled
    {
        get
        {
            try { return Config.Load().AutoAlign; }
            catch { return true; }
        }
    }

    public static void BeginDrag(Window dragged)
    {
        // 折叠到「可恢复按钮」状态的组件不参与窗口对齐：此时拖动的是
        // RestoreButton，不是完整组件本体，不应吸附或绘制对齐线。
        _enabled = IsEnabled && IsExpanded(dragged);
    }

    public static void EndDrag(Window dragged)
    {
        HideOverlay();
        _enabled = false;
    }

    /// <summary>
    /// 计算拖拽目标位置并对齐。返回经过吸附修正后的窗口 Left/Top；
    /// 若有对齐则同时绘制对齐线，否则隐藏对齐线。
    /// </summary>
    public static Point Adjust(Window dragged, double left, double top)
    {
        if (!_enabled) return new Point(left, top);

        var targets = GetTargets(dragged);
        if (targets.Count == 0)
        {
            HideOverlay();
            return new Point(left, top);
        }

        double w = dragged.ActualWidth > 0 ? dragged.ActualWidth : dragged.Width;
        double h = dragged.ActualHeight > 0 ? dragged.ActualHeight : dragged.Height;
        if (w <= 0) w = 200;
        if (h <= 0) h = 100;

        double dLeft = left, dRight = left + w, dCenterX = left + w / 2;
        double dTop = top, dBottom = top + h, dCenterY = top + h / 2;

        double bestV = double.MaxValue, bestH = double.MaxValue;
        double bestDx = 0, bestDy = 0;
        double vx = 0, hy = 0;
        Rect vTarget = default, hTarget = default;
        bool hasV = false, hasH = false;

        void ConsiderV(Rect t, double targetX, double delta)
        {
            double ad = Math.Abs(delta);
            if (ad < Threshold && ad < bestV)
            {
                bestV = ad; bestDx = delta; vx = targetX; vTarget = t; hasV = true;
            }
        }
        void ConsiderH(Rect t, double targetY, double delta)
        {
            double ad = Math.Abs(delta);
            if (ad < Threshold && ad < bestH)
            {
                bestH = ad; bestDy = delta; hy = targetY; hTarget = t; hasH = true;
            }
        }

        foreach (var t in targets)
        {
            double tLeft = t.Left, tRight = t.Right, tTop = t.Top, tBottom = t.Bottom;
            double tCx = t.Left + t.Width / 2, tCy = t.Top + t.Height / 2;

            ConsiderV(t, tLeft, tLeft - dLeft);
            ConsiderV(t, tCx, tCx - dLeft);
            ConsiderV(t, tRight, tRight - dLeft);
            ConsiderV(t, tLeft, tLeft - dCenterX);
            ConsiderV(t, tCx, tCx - dCenterX);
            ConsiderV(t, tRight, tRight - dCenterX);
            ConsiderV(t, tLeft, tLeft - dRight);
            ConsiderV(t, tCx, tCx - dRight);
            ConsiderV(t, tRight, tRight - dRight);

            ConsiderH(t, tTop, tTop - dTop);
            ConsiderH(t, tCy, tCy - dTop);
            ConsiderH(t, tBottom, tBottom - dTop);
            ConsiderH(t, tTop, tTop - dCenterY);
            ConsiderH(t, tCy, tCy - dCenterY);
            ConsiderH(t, tBottom, tBottom - dCenterY);
            ConsiderH(t, tTop, tTop - dBottom);
            ConsiderH(t, tCy, tCy - dBottom);
            ConsiderH(t, tBottom, tBottom - dBottom);
        }

        double finalLeft = hasV ? left + bestDx : left;
        double finalTop = hasH ? top + bestDy : top;

        if (!hasV && !hasH)
        {
            HideOverlay();
            return new Point(finalLeft, finalTop);
        }

        double fLeft = finalLeft, fRight = finalLeft + w;
        double fTop = finalTop, fBottom = finalTop + h;

        double? lineX = null, lineY = null;
        double y1 = 0, y2 = 0, x1 = 0, x2 = 0;
        if (hasV)
        {
            lineX = vx;
            y1 = Math.Min(fTop, vTarget.Top);
            y2 = Math.Max(fBottom, vTarget.Bottom);
        }
        if (hasH)
        {
            lineY = hy;
            x1 = Math.Min(fLeft, hTarget.Left);
            x2 = Math.Max(fRight, hTarget.Right);
        }

        DrawLines(lineX, y1, y2, lineY, x1, x2);
        return new Point(finalLeft, finalTop);
    }

    // ── Target enumeration ──

    static List<Rect> GetTargets(Window dragged)
    {
        var rects = new List<Rect>();
        foreach (Window w in Application.Current.Windows)
        {
            if (ReferenceEquals(w, dragged)) continue;
            if (w is not (ZoneWindow or ClockWidget or CalendarWidget or StickyNoteWindow)) continue;
            if (!w.IsVisible || w.WindowState != WindowState.Normal) continue;
            if (!IsExpanded(w)) continue;
            double ww = w.ActualWidth > 0 ? w.ActualWidth : w.Width;
            double wh = w.ActualHeight > 0 ? w.ActualHeight : w.Height;
            if (ww <= 0 || wh <= 0) continue;
            rects.Add(new Rect(w.Left, w.Top, ww, wh));
        }
        return rects;
    }

    static bool IsExpanded(Window w) => w switch
    {
        ZoneWindow z => z.MainContent.Visibility == Visibility.Visible,
        ClockWidget c => c.MainContent.Visibility == Visibility.Visible,
        CalendarWidget c => c.MainContent.Visibility == Visibility.Visible,
        StickyNoteWindow s => s.MainContent.Visibility == Visibility.Visible,
        _ => true,
    };

    // ── Overlay window + line drawing ──

    static Color AccentColor()
    {
        try
        {
            if (Application.Current?.Resources["Brush.Accent"] is SolidColorBrush sb)
                return sb.Color;
        }
        catch { }
        return Color.FromRgb(0x1E, 0x88, 0xE5);
    }

    static SolidColorBrush CreateLineBrush()
    {
        var c = AccentColor();
        var brush = new SolidColorBrush(Color.FromArgb(0x80, c.R, c.G, c.B));
        brush.Freeze();
        return brush;
    }

    static Canvas? EnsureOverlay()
    {
        if (_canvas != null) return _canvas;
        if (Application.Current == null) return null;

        _overlay = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            Topmost = true,
            ShowInTaskbar = false,
            ShowActivated = false,
            IsHitTestVisible = false,
            Focusable = false,
            ResizeMode = ResizeMode.NoResize,
        };
        _canvas = new Canvas { IsHitTestVisible = false, ClipToBounds = false };
        _overlay.Content = _canvas;
        return _canvas;
    }

    static void DrawLines(double? vx, double y1, double y2, double? hy, double x1, double x2)
    {
        var canvas = EnsureOverlay();
        if (canvas == null) return;

        canvas.Children.Clear();
        double ox = SystemParameters.VirtualScreenLeft;
        double oy = SystemParameters.VirtualScreenTop;
        var brush = CreateLineBrush();

        if (vx.HasValue)
        {
            canvas.Children.Add(new Line
            {
                X1 = vx.Value - ox, X2 = vx.Value - ox,
                Y1 = y1 - oy, Y2 = y2 - oy,
                Stroke = brush, StrokeThickness = 1,
                IsHitTestVisible = false
            });
        }
        if (hy.HasValue)
        {
            canvas.Children.Add(new Line
            {
                X1 = x1 - ox, X2 = x2 - ox,
                Y1 = hy.Value - oy, Y2 = hy.Value - oy,
                Stroke = brush, StrokeThickness = 1,
                IsHitTestVisible = false
            });
        }

        ShowOverlay();
    }

    static void ShowOverlay()
    {
        var ov = _overlay;
        if (ov == null) return;

        ov.Left = SystemParameters.VirtualScreenLeft;
        ov.Top = SystemParameters.VirtualScreenTop;
        ov.Width = SystemParameters.VirtualScreenWidth;
        ov.Height = SystemParameters.VirtualScreenHeight;

        if (!ov.IsVisible)
        {
            var helper = new WindowInteropHelper(ov);
            helper.EnsureHandle();
            try
            {
                int ex = NativeMethods.GetWindowLong(helper.Handle, NativeMethods.GWL_EXSTYLE);
                NativeMethods.SetWindowLong(helper.Handle, NativeMethods.GWL_EXSTYLE,
                    ex | NativeMethods.WS_EX_TRANSPARENT
                       | NativeMethods.WS_EX_NOACTIVATE
                       | NativeMethods.WS_EX_TOOLWINDOW);
            }
            catch { }
            ov.Show();
        }
    }

    static void HideOverlay()
    {
        var ov = _overlay;
        if (ov != null && ov.IsVisible)
        {
            try { ov.Hide(); } catch { }
        }
        _canvas?.Children.Clear();
    }
}
