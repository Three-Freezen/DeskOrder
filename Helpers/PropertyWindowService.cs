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

    public static void OpenOrFocus(object target) =>
        _main?.OpenFloatingProperty(target);

    public static void OpenOrFocus(object target, Window? requester) =>
        _main?.OpenFloatingProperty(target, requester);
}