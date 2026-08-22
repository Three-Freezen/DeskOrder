using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace DesktopZones.Helpers;

public static class Motion
{
    public static readonly Duration Fast   = new(TimeSpan.FromMilliseconds(120));
    public static readonly Duration Normal = new(TimeSpan.FromMilliseconds(200));
    public static readonly Duration Exit   = new(TimeSpan.FromMilliseconds(150));
    public static readonly Duration Slow   = new(TimeSpan.FromMilliseconds(320));

    // standard cubic-bezier(.4,0,.2,1)
    public static readonly KeySpline StandardSpline = new(0.4, 0, 0.2, 1);
    // decelerate cubic-bezier(0,0,.2,1) — used for enter
    public static readonly KeySpline DecelerateSpline = new(0, 0, 0.2, 1);
    // accelerate cubic-bezier(.4,0,1,1) — used for exit
    public static readonly KeySpline AccelerateSpline = new(0.4, 0, 1, 1);

    public static SplineDoubleKeyFrame Frame(double value, Duration d, KeySpline spline)
    {
        KeyTime kt = d.TimeSpan; // implicit TimeSpan -> KeyTime
        return new SplineDoubleKeyFrame(value, kt, spline);
    }

    /// <summary>Read Windows "Show animations" reduced-motion preference.</summary>
    public static bool IsReducedMotion()
    {
        try { return !SystemParameters.ClientAreaAnimation; }
        catch { return false; }
    }

    public static Duration ResolveDuration(Duration d)
        => IsReducedMotion() ? new Duration(TimeSpan.Zero) : d;
}