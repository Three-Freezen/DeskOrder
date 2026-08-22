using DesktopZones.Views;

namespace DesktopZones.Helpers;

/// <summary>
/// ponytail: Static facade over PropertyWindowManager so any caller can open a
/// floating property window without holding a reference to ManagementWindow.
/// Initialized once by ManagementWindow's constructor.
/// </summary>
public static class PropertyWindowService
{
    static ManagementWindow? _main;

    public static void Init(ManagementWindow main) { _main = main; }

    public static void OpenOrFocus(object target) =>
        _main?.OpenFloatingProperty(target);
}