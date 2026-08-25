using System.Windows;
using System.Windows.Controls;
using DesktopZones.Models;
using DesktopZones.ViewModels;

namespace DesktopZones.Views.Components;

/// <summary>
/// ponytail: routes ZoneItem rendering in ZoneWindow.ItemsControl.
/// Type=SubFolder → SubfolderItemView DataTemplate (defined in ZoneWindow.xaml
/// resources as "SubfolderItemTemplate"); everything else → the original item
/// template (key "ZoneItemTemplate"). Falls back to the original template if
/// the resource is missing (e.g. in tests).
///
/// ponytail 2026-08-26: ItemsControl binds to ObservableCollection&lt;ZoneItemViewModel&gt;
/// (see ZoneViewModel.RefreshItems), so <c>item</c> at runtime is a VM, not the
/// raw ZoneItem. Unwrap via the VM's Source property before checking Type.
/// Original ZoneItem-direct match kept for callers that bind the raw model
/// (e.g. SubfolderFlyout preview).
/// </summary>
public class SubfolderItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate? ZoneItemTemplate { get; set; }
    public DataTemplate? SubfolderItemTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        var zoneItem = (item as ZoneItemViewModel)?.Source ?? item as ZoneItem;
        if (zoneItem != null && zoneItem.Type == ItemType.SubFolder)
            return SubfolderItemTemplate ?? ZoneItemTemplate;
        return ZoneItemTemplate;
    }
}