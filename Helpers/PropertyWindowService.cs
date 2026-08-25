using System.Windows;
using DesktopZones.Views;

namespace DesktopZones.Helpers;

/// <summary>
/// ponytail: Static facade over PropertyWindowManager so any caller can open a
/// floating property window without holding a reference to ManagementWindow.
/// Initialized once by ManagementWindow's constructor. The optional requester
/// argument lets callers tell the manager "the gear button on THIS window was
/// the trigger" — used to anchor the popped-out window at the requester's
/// position (gear-button offset 24,24) so it visually pops from where the user
/// clicked instead of jumping to a remembered location.
/// </summary>
public static class PropertyWindowService
{
    static ManagementWindow? _main;

    public static void Init(ManagementWindow main) { _main = main; }

    /// <summary>Lazily create the ManagementWindow if it doesn't exist yet (StartMinimized
    /// startup keeps it null until first shown). Without this, opening a property editor
    /// from a zone/subfolder before the management UI was ever shown would no-op.</summary>
    static void EnsureMain()
    {
        if (_main != null) return;
        (System.Windows.Application.Current as App)?.EnsureManagementWindow();
    }

    public static void OpenOrFocus(object target)
    {
        EnsureMain();
        _main?.OpenFloatingProperty(target);
    }

    public static void OpenOrFocus(object target, Window? requester)
    {
        EnsureMain();
        _main?.OpenFloatingProperty(target, requester);
    }
}