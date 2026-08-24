using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DesktopZones.Views.Components;

/// <summary>
/// One segment entry. Plain string payload; styling comes from SegmentItem.
/// </summary>
public class SegmentItem
{
    public string Text { get; set; } = "";
}

/// <summary>
/// Wraps an ItemsControl of RadioButton segments inside a bordered container.
/// The selected segment's accent fill is a Border that SLIDES under the target
/// segment (200ms ease-out) instead of repainting in place — see
/// SegmentItem.Slide for the segment template.
/// </summary>
public partial class Segmented : UserControl
{
    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(
        nameof(Items), typeof(ObservableCollection<SegmentItem>), typeof(Segmented),
        new PropertyMetadata(null, OnItemsChanged));

    public static readonly DependencyProperty SelectedIndexProperty = DependencyProperty.Register(
        nameof(SelectedIndex), typeof(int), typeof(Segmented),
        new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedIndexChanged));

    public ObservableCollection<SegmentItem> Items
    {
        get => (ObservableCollection<SegmentItem>)GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public int SelectedIndex
    {
        get => (int)GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    /// <summary>Raised after SelectedIndex changes (user click or programmatic).</summary>
    public event EventHandler? SelectedIndexChanged;

    public Segmented()
    {
        InitializeComponent();
        if (Items == null)
        {
            Items = new ObservableCollection<SegmentItem>();
        }
        Loaded += (_, _) => PositionHighlight(animate: false);
    }

    static void OnItemsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var s = (Segmented)d;
        if (e.OldValue is ObservableCollection<SegmentItem> oldColl)
            oldColl.CollectionChanged -= s.OnCollectionChanged;
        if (e.NewValue is ObservableCollection<SegmentItem> newColl)
            newColl.CollectionChanged += s.OnCollectionChanged;
        s.Rebuild();
    }

    static void OnSelectedIndexChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var s = (Segmented)d;
        s.ApplySelected();
        s.PositionHighlight(animate: true);
        s.SelectedIndexChanged?.Invoke(s, EventArgs.Empty);
    }

    void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    void HostGrid_SizeChanged(object sender, SizeChangedEventArgs e) => PositionHighlight(animate: false);

    void Rebuild()
    {
        ItemsHost.Items.Clear();
        if (Items == null) return;
        for (int i = 0; i < Items.Count; i++)
        {
            var rb = new RadioButton
            {
                Style = (Style)FindResource("SegmentItem.Slide"),
                Tag = i,
                Content = Items[i].Text,
                IsChecked = i == SelectedIndex,
            };
            rb.Checked += Segment_Checked;
            ItemsHost.Items.Add(rb);
        }
        PositionHighlight(animate: false);
    }

    void ApplySelected()
    {
        for (int i = 0; i < ItemsHost.Items.Count; i++)
        {
            if (ItemsHost.Items[i] is RadioButton rb)
            {
                rb.IsChecked = i == SelectedIndex;
            }
        }
    }

    /// <summary>Slide the accent highlight under the selected segment. Each
    /// segment is exactly 1/N of the track width (UniformGrid).</summary>
    void PositionHighlight(bool animate)
    {
        if (Highlight == null || HostGrid == null || ItemsHost == null) return;
        int n = ItemsHost.Items.Count;
        bool hasSelection = n > 0 && SelectedIndex >= 0 && SelectedIndex < n;
        Highlight.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        if (!hasSelection || HostGrid.ActualWidth <= 0)
        {
            if (HighlightTranslate != null)
                HighlightTranslate.X = 0;
            return;
        }

        double segW = HostGrid.ActualWidth / n;
        Highlight.Width = Math.Max(0, segW - 4); // track the segments' 2px margins
        double x = SelectedIndex * segW;

        if (HighlightTranslate == null) return;
        if (!animate)
        {
            HighlightTranslate.X = x;
            return;
        }

        // ponytail: same motion family as the rest of the settings interface —
        // 200ms cubic ease-out (Motion.StandardSpline equivalent).
        var anim = new DoubleAnimation(HighlightTranslate.X, x, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        HighlightTranslate.BeginAnimation(TranslateTransform.XProperty, anim);
    }

    void Segment_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is int idx && idx != SelectedIndex)
        {
            SelectedIndex = idx;
        }
    }
}
