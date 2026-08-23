namespace DesktopZones.Models;

/// <summary>
/// ponytail: Where the hover/click restore animation scales out from — AND where
/// the RestoreButton sits while collapsed. HoverExpandBehavior.ApplyOrigin repositions
/// the button live when this changes (via MotionSettingsDialog / SetEnabled), so the
/// two modes are visually distinct.
/// </summary>
public enum HoverExpandOrigin
{
    /// <summary>
    /// 按钮中心: RestoreButton parks at the window's center; the content collapses to
    /// and grows from the button's center (the window's middle). Axis kinds split open
    /// from the middle line (top half up / bottom half down, or left/right), Scale and
    /// Bounce grow radially from the center point.
    /// </summary>
    ButtonCenter,
    /// <summary>
    /// 按钮边角: RestoreButton parks at the window's top-left corner; the content
    /// collapses to and grows from the button's top-left corner. Axis kinds unfold
    /// downward / rightward from the top / left edge.
    /// </summary>
    ButtonCorner,
}
