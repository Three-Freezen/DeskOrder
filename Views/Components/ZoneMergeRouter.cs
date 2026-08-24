using System.Linq;
using System.Windows;
using DesktopZones.Views;

namespace DesktopZones.Views.Components;

/// <summary>
/// Hit-test a screen-space point against every open ZoneWindow's title bar. Used by
/// the drag-to-merge flow: while dragging a zone by its title bar, the source window
/// asks this router which (other) zone's title bar is under the cursor so it can play
/// the enlarge animation and commit the merge on release.
/// </summary>
public static class ZoneMergeRouter
{
    public static ZoneWindow? FindTitleBarTarget(ZoneWindow source, Point screenPos)
    {
        foreach (var win in Application.Current.Windows.OfType<ZoneWindow>())
        {
            if (ReferenceEquals(win, source)) continue;
            if (!win.CanAcceptMerge) continue;
            var local = win.PointFromScreen(screenPos);
            if (win.TitleBarHitRect().Contains(local)) return win;
        }
        return null;
    }
}
