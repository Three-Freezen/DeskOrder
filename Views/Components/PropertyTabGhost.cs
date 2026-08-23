using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace DesktopZones.Views.Components;

/// <summary>Cursor-following ghost shown during a tab drag. Lives in the
/// AdornerLayer so it never steals activation or input — pure visual feedback.
/// Position is updated via <see cref="UpdatePosition"/> on every drag-move tick.</summary>
public class PropertyTabGhost : Adorner
{
    readonly UIElement _visual;
    Size _visualSize;
    Point _cursorScreen;

    public PropertyTabGhost(UIElement adornedElement, PropertyTab template) : base(adornedElement)
    {
        IsHitTestVisible = false;
        _visual = new PropertyTabGhostView { DataContext = template };
        AddVisualChild(_visual);
    }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _visual;

    protected override Size MeasureOverride(Size constraint)
    {
        _visual.Measure(constraint);
        _visualSize = _visual.DesiredSize;
        return _visualSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // ghost centered on cursor; convert screen coords to adorner-local
        var adornedOrigin = AdornedElement.PointToScreen(new Point(0, 0));
        var x = _cursorScreen.X - _visualSize.Width  / 2 - adornedOrigin.X;
        var y = _cursorScreen.Y - _visualSize.Height / 2 - adornedOrigin.Y;
        _visual.Arrange(new Rect(new Point(x, y), _visualSize));
        return finalSize;
    }

    /// <summary>Update the ghost's screen position. Called on every drag-move.</summary>
    public void UpdatePosition(Point screenPos)
    {
        _cursorScreen = screenPos;
        InvalidateArrange();
    }
}
