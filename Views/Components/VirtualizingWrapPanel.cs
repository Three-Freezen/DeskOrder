using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace DesktopZones.Views.Components;

/// <summary>
/// Virtualizing wrap panel for icon-grid listings (the mapped-folder view).
/// Realizes only the item rows visible in the viewport — fixed item cell size
/// (ItemWidth × ItemHeight) + spacing — and implements IScrollInfo so the host
/// ListBox ScrollViewer scrolls the full pixel extent.
/// </summary>
public class VirtualizingWrapPanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty ItemWidthProperty = DependencyProperty.Register(
        nameof(ItemWidth), typeof(double), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(76.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));
    public static readonly DependencyProperty ItemHeightProperty = DependencyProperty.Register(
        nameof(ItemHeight), typeof(double), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(84.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));
    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(4.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public double ItemWidth { get => (double)GetValue(ItemWidthProperty); set => SetValue(ItemWidthProperty, value); }
    public double ItemHeight { get => (double)GetValue(ItemHeightProperty); set => SetValue(ItemHeightProperty, value); }
    public double Spacing { get => (double)GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }

    private Size _viewport;
    private Size _extent;
    private Point _offset;
    private int _itemsPerRow = 1;
    private int _rowCount;
    private int _itemCount;

    /// <summary>Total item count, refreshed from the owning ItemsControl on every
    /// extent pass. NOTE: <see cref="OnItemsChanged"/> args.ItemCount is the count
    /// of THAT change action only (1 per ObservableCollection Add), so it cannot
    /// be used as the running total.</summary>
    int ItemCount => _itemCount;

    double RowPitch => ItemHeight + Spacing;

    int FirstVisibleRow => (int)Math.Floor(_offset.Y / RowPitch);
    int LastVisibleRow => (int)Math.Ceiling((_offset.Y + Math.Max(_viewport.Height, ItemHeight)) / RowPitch) - 1;

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        base.OnItemsChanged(sender, args);
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _viewport = availableSize;
        UpdateExtent();

        if (double.IsInfinity(availableSize.Height))
            availableSize.Height = _extent.Height;

        var itemSize = new Size(ItemWidth, ItemHeight);
        if (InternalChildren.Count > 0)
        {
            var first = (UIElement)InternalChildren[0];
            first.Measure(itemSize);
            itemSize = first.DesiredSize;
        }

        if (ItemCount > 0)
        {
            int firstRow = Math.Max(0, FirstVisibleRow);
            int lastRow = Math.Min(LastVisibleRow, _rowCount - 1);
            int firstIndex = firstRow * _itemsPerRow;
            int lastIndex = Math.Min(ItemCount - 1, (lastRow + 1) * _itemsPerRow - 1);
            CleanUpItems(firstIndex, lastIndex);

            var generator = ItemContainerGenerator;
            var startPos = generator.GeneratorPositionFromIndex(firstIndex);
            int childIndex = startPos.Offset == 0 ? startPos.Index : startPos.Index + 1;
            using (generator.StartAt(startPos, GeneratorDirection.Forward, true))
            {
                for (int i = firstIndex; i <= lastIndex; i++, childIndex++)
                {
                    bool newlyRealized;
                    var child = (UIElement)generator.GenerateNext(out newlyRealized);
                    if (newlyRealized)
                    {
                        if (childIndex >= InternalChildren.Count) AddInternalChild(child);
                        else InsertInternalChild(childIndex, child);
                        generator.PrepareItemContainer(child);
                    }
                    else if (child != null && !InternalChildren.Contains(child))
                    {
                        // Container exists but isn't our child yet (re-parent).
                        InsertInternalChild(childIndex, child);
                        generator.PrepareItemContainer(child);
                    }
                    child?.Measure(itemSize);
                }
            }
        }
        else
        {
            CleanUpAll();
        }

        return new Size(Math.Max(0, availableSize.Width - 2), Math.Max(0, availableSize.Height - 2));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            int itemIndex = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            if (itemIndex < 0) continue;
            int row = itemIndex / _itemsPerRow;
            int col = itemIndex % _itemsPerRow;
            var child = (UIElement)InternalChildren[i];
            child.Arrange(new Rect(
                col * (ItemWidth + Spacing) - _offset.X,
                row * RowPitch - _offset.Y,
                Math.Max(child.DesiredSize.Width, ItemWidth),
                Math.Max(child.DesiredSize.Height, ItemHeight)));
        }
        return finalSize;
    }

    // ── Realization bookkeeping ──

    void CleanUpItems(int firstIndex, int lastIndex)
    {
        var generator = ItemContainerGenerator;
        for (int i = InternalChildren.Count - 1; i >= 0; i--)
        {
            int itemIndex = generator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            if (itemIndex >= firstIndex && itemIndex <= lastIndex) continue;
            generator.Remove(new GeneratorPosition(i, 0), 1);
            RemoveInternalChildRange(i, 1);
        }
    }

    void CleanUpAll()
    {
        var generator = ItemContainerGenerator;
        for (int i = InternalChildren.Count - 1; i >= 0; i--)
        {
            generator.Remove(new GeneratorPosition(i, 0), 1);
            RemoveInternalChildRange(i, 1);
        }
    }

    // ── IScrollInfo ──

    void UpdateExtent()
    {
        _itemCount = ItemsControl.GetItemsOwner(this)?.Items?.Count ?? 0;

        if (ItemCount <= 0)
        {
            _itemsPerRow = 1;
            _rowCount = 0;
            _extent = new Size(0, 0);
        }
        else
        {
            _itemsPerRow = Math.Max(1, (int)Math.Floor((Math.Max(_viewport.Width, ItemWidth) + Spacing) / (ItemWidth + Spacing)));
            _rowCount = (ItemCount + _itemsPerRow - 1) / _itemsPerRow;
            _extent = new Size(
                Math.Min(Math.Max(_viewport.Width, 0), _itemsPerRow * (ItemWidth + Spacing)),
                _rowCount * RowPitch);
        }

        // Clamp the offset when the extent shrank (e.g. after a refresh).
        SetVerticalOffsetCore(_offset.Y);

        ScrollOwner?.InvalidateScrollInfo();
    }

    public ScrollViewer? ScrollOwner { get; set; }

    public bool CanHorizontallyScroll { get => false; set { } }
    public bool CanVerticallyScroll { get => true; set { } }

    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => _offset.X;
    public double VerticalOffset => _offset.Y;

    public void SetHorizontalOffset(double offset) { }

    public void SetVerticalOffset(double offset) => SetVerticalOffsetCore(offset);

    void SetVerticalOffsetCore(double offset)
    {
        double max = Math.Max(0, _extent.Height - _viewport.Height);
        offset = Math.Max(0, Math.Min(offset, max));
        if (Math.Abs(offset - _offset.Y) < 0.5) return;
        _offset.Y = offset;
        InvalidateMeasure();
    }

    public void LineUp() => SetVerticalOffset(_offset.Y - RowPitch);
    public void LineDown() => SetVerticalOffset(_offset.Y + RowPitch);
    public void PageUp() => SetVerticalOffset(_offset.Y - _viewport.Height);
    public void PageDown() => SetVerticalOffset(_offset.Y + _viewport.Height);
    public void MouseWheelUp() => SetVerticalOffset(_offset.Y - RowPitch * 1.5);
    public void MouseWheelDown() => SetVerticalOffset(_offset.Y + RowPitch * 1.5);

    // Horizontal scrolling is disabled (CanHorizontallyScroll = false) — these are
    // interface requirements only and never get called by the ScrollViewer.
    public void LineLeft() { }
    public void LineRight() { }
    public void PageLeft() { }
    public void PageRight() { }
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        // Bring the visual's row fully into the viewport.
        double top = _offset.Y;
        double bottom = _offset.Y + Math.Max(_viewport.Height, ItemHeight);
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            if (!ReferenceEquals(InternalChildren[i], visual)) continue;
            int itemIndex = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            if (itemIndex < 0) break;
            int row = itemIndex / _itemsPerRow;
            double rowTop = row * RowPitch;
            if (rowTop < top) SetVerticalOffset(rowTop);
            else if (rowTop + ItemHeight > bottom) SetVerticalOffset(rowTop + ItemHeight - Math.Max(_viewport.Height, ItemHeight));
            break;
        }
        return rectangle;
    }
}
