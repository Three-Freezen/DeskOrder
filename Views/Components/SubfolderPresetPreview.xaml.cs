using System.Windows.Controls;
using DesktopZones.Models;

namespace DesktopZones.Views.Components;

/// <summary>
/// ponytail: inline preview of a saved <see cref="SubfolderPreset"/> for the
/// Group E preset list in <see cref="PropertyPanel"/>. DataContext is set via
/// <see cref="SetPreset"/> by the parent list — the visual is just preset name
/// on a corner-rounded border (Q9: no thumbnail rendering).
/// </summary>
public partial class SubfolderPresetPreview : UserControl
{
    public SubfolderPresetPreview()
    {
        InitializeComponent();
    }

    public void SetPreset(SubfolderPreset preset)
    {
        DataContext = preset;
    }
}
