using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;

namespace DesktopZones.Views.Components;

/// <summary>
/// ScrollViewer wrapper that shows ◀ ▶ buttons when tabs overflow the
/// available width. The button visibility is recomputed on
/// Tabs.CollectionChanged and SizeChanged.
/// </summary>
public partial class PropertyTabScroller : UserControl
{
    public static readonly DependencyProperty TabsProperty = DependencyProperty.Register(
        nameof(Tabs), typeof(ObservableCollection<PropertyTab>),
        typeof(PropertyTabScroller),
        new PropertyMetadata(null, OnTabsChanged));

    public ObservableCollection<PropertyTab> Tabs
    {
        get => (ObservableCollection<PropertyTab>)GetValue(TabsProperty);
        set => SetValue(TabsProperty, value);
    }

    public static readonly DependencyProperty ItemTemplateProperty = DependencyProperty.Register(
        nameof(ItemTemplate), typeof(DataTemplate), typeof(PropertyTabScroller),
        new PropertyMetadata(null, (d, e) => ((PropertyTabScroller)d).TabsHostInner.ItemTemplate = (DataTemplate)e.NewValue));

    public DataTemplate ItemTemplate
    {
        get => (DataTemplate)GetValue(ItemTemplateProperty);
        set => SetValue(ItemTemplateProperty, value);
    }

    public static readonly DependencyProperty MinTabWidthProperty = DependencyProperty.Register(
        nameof(MinTabWidth), typeof(double), typeof(PropertyTabScroller),
        new PropertyMetadata(100.0));

    public double MinTabWidth
    {
        get => (double)GetValue(MinTabWidthProperty);
        set => SetValue(MinTabWidthProperty, value);
    }

    public PropertyTabScroller()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateButtons();
        Loaded += (_, _) => UpdateButtons();
    }

    /// <summary>Inner ItemsControl that hosts the tabs. Exposed so the parent
    /// <see cref="PropertyTabStrip"/> can keep using a single <c>TabsHost</c>
    /// reference without caring whether the tabs live in a ScrollViewer or a
    /// PropertyTabScroller.</summary>
    public ItemsControl TabsHost => TabsHostInner;

    static void OnTabsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var s = (PropertyTabScroller)d;
        if (e.OldValue is ObservableCollection<PropertyTab> oldC)
            oldC.CollectionChanged -= s.OnTabsCollectionChanged;
        if (e.NewValue is ObservableCollection<PropertyTab> newC)
        {
            newC.CollectionChanged += s.OnTabsCollectionChanged;
            s.TabsHostInner.ItemsSource = newC;
        }
        s.UpdateButtons();
    }

    void OnTabsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => UpdateButtons();

    void UpdateButtons()
    {
        // ponytail: needScroll uses MinTabWidth × count — actual item widths
        // vary by DisplayTitle, but for overflow detection an estimate is
        // good enough; this avoids a measure pass on every CollectionChanged.
        bool needScroll = Tabs != null && Tabs.Count * MinTabWidth > ActualWidth - 48;
        BtnLeft.Visibility = needScroll ? Visibility.Visible : Visibility.Collapsed;
        BtnRight.Visibility = needScroll ? Visibility.Visible : Visibility.Collapsed;
    }

    void BtnLeft_Click(object sender, RoutedEventArgs e) => Scroller.LineLeft();

    void BtnRight_Click(object sender, RoutedEventArgs e) => Scroller.LineRight();

    /// <summary>Called after a transfer-drop to make the just-added tab visible.</summary>
    public void ScrollIntoView(PropertyTab tab)
    {
        if (tab == null) return;
        var container = (FrameworkElement)TabsHostInner.ItemContainerGenerator.ContainerFromItem(tab);
        container?.BringIntoView();
    }
}
