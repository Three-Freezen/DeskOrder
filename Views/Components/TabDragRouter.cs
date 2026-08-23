using System.Collections.Generic;
using System.Linq;
using System.Windows;
using DesktopZones.Views;

namespace DesktopZones.Views.Components;

/// <summary>Hit-test a screen-space point against all open PropertyWindows'
/// tab strips plus the ManagementWindow's docked strip. Used by the cross-
/// window drag-merge flow in PropertyTabStrip.</summary>
public static class TabDragRouter
{
    /// <summary>Strip rect (relative to its containing window) that counts as a
    /// valid drop zone. The strip's hit zone excludes the scroll buttons and
    /// the bottom border; just the actual tab row.</summary>
    /// <remarks>Set by PropertyTabStrip on Loaded.</remarks>
    public static Rect GetStripHitRect(PropertyTabStrip strip)
    {
        // ponytail: this returns the strip's actual-size rect in WINDOW coords
        // (not parent), so the caller can PointFromScreen once and hit-test
        // directly. If strip not yet arranged, returns Rect.Empty.
        var win = Window.GetWindow(strip);
        if (win == null || !strip.IsLoaded) return Rect.Empty;
        var topLeft = strip.TransformToAncestor(win).Transform(new Point(0, 0));
        return new Rect(topLeft, new Size(strip.ActualWidth, strip.ActualHeight));
    }

    public static PropertyTabStrip? FindDropTarget(Point screenPos)
    {
        var strips = CollectStrips();
        foreach (var strip in strips)
        {
            var win = Window.GetWindow(strip);
            if (win == null || !win.IsVisible) continue;
            // ponytail: skip minimized windows — they have no usable surface
            if (win.WindowState == WindowState.Minimized) continue;
            var local = win.PointFromScreen(screenPos);
            var stripRect = GetStripHitRect(strip);
            if (stripRect.Contains(local)) return strip;
        }
        return null;
    }

    static IEnumerable<PropertyTabStrip> CollectStrips()
    {
        var app = Application.Current;
        if (app.MainWindow is ManagementWindow main && main.DockedTabs != null)
            yield return main.DockedTabs;
        foreach (var win in app.Windows.OfType<PropertyWindow>())
            if (win.Tabs != null) yield return win.Tabs;
    }
}