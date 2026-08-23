using System.Windows.Controls;

namespace DesktopZones.Views.Components;

/// <summary>Visual template for the drag ghost — pure visual, no behavior.
/// Hosted as the Adorner visual child by <see cref="PropertyTabGhost"/>.</summary>
public partial class PropertyTabGhostView : UserControl
{
    public PropertyTabGhostView() => InitializeComponent();
}
