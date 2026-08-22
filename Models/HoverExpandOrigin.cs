namespace DesktopZones.Models;

/// <summary>
/// ponytail: Where the hover/click restore animation scales out from. Spec
/// §7.2 lets the user pick the visual feel.
/// </summary>
public enum HoverExpandOrigin
{
    /// <summary>Content collapses to the RestoreButton's center and expands radially outward.</summary>
    ButtonCenter,
    /// <summary>Content scales from the button's top-left corner (0,0) — button stays as the zone's top-left.</summary>
    ButtonCorner,
}