using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;

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
/// Selected segment gets the accent fill via the SegmentItem style.
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

    public Segmented()
    {
        InitializeComponent();
        if (Items == null)
        {
            Items = new ObservableCollection<SegmentItem>();
        }
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
        ((Segmented)d).ApplySelected();
    }

    void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    void Rebuild()
    {
        ItemsHost.Items.Clear();
        if (Items == null) return;
        for (int i = 0; i < Items.Count; i++)
        {
            var rb = new RadioButton
            {
                Style = (Style)FindResource("SegmentItem"),
                Tag = i,
                Content = Items[i].Text,
                IsChecked = i == SelectedIndex,
            };
            rb.Checked += Segment_Checked;
            rb.Unchecked += Segment_Unchecked;
            ItemsHost.Items.Add(rb);
        }
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

    void Segment_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is int idx && idx != SelectedIndex)
        {
            SelectedIndex = idx;
        }
    }

    void Segment_Unchecked(object sender, RoutedEventArgs e)
    {
        // RadioButton group keeps one checked; ignore Unchecked.
    }
}