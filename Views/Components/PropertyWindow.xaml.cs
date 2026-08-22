using System.ComponentModel;
using System.Windows;
using DesktopZones.Helpers;
using DesktopZones.Models;
using DesktopZones.Services;

namespace DesktopZones.Views.Components;

/// <summary>
/// Floating, topmost shell that hosts the same PropertyPanel + tab strip used in the
/// docked panel area. Exposes Body / Tabs so callers (e.g. zone right-click undock)
/// can drive content; Closing is re-exposed so the host can persist window placement.
/// ponytail: no drag/resize logic here — the caller owns placement and lifetime, this
/// is just a content host. Drag a Border over the title bar in Task 13+ if needed.
/// </summary>
public partial class PropertyWindow : Window
{
    public static readonly DependencyProperty TargetProperty = DependencyProperty.Register(
        nameof(Target), typeof(object), typeof(PropertyWindow),
        new PropertyMetadata(null, (d, _) => ((PropertyWindow)d).OnTargetChanged()));

    public PropertyPanel Body => BodyPanel;
    public PropertyTabStrip Tabs => TabStrip;

    public object? Target
    {
        get => GetValue(TargetProperty);
        set => SetValue(TargetProperty, value);
    }

    public PropertyWindow(object target, ConfigService configService)
    {
        InitializeComponent();
        Target = target;
        Title = target is Zone z ? z.Name : target?.GetType().Name;
    }

    public PropertyWindow()
    {
        InitializeComponent();
    }

    void OnTargetChanged() { Body.Target = Target; }

    // ponytail: re-expose Closing verbatim so subscribers can save Left/Top/Width/Height
    // without touching Window's protected base event API.
    public new event CancelEventHandler? Closing
    {
        add => base.Closing += value;
        remove => base.Closing -= value;
    }
}