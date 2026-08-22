using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DesktopZones.Helpers;
using DesktopZones.Models;

namespace DesktopZones.Views.Components;

/// <summary>
/// ponytail: Hover-to-show helper for the four floating windows (ZoneWindow /
/// ClockWidget / CalendarWidget / StickyNoteWindow). Wired to the RestoreButton
/// (the 36×36 circle visible when the zone is hidden) and driven by the host
/// widget's EnableRestoreButton flag — no separate HoverAutoExpand toggle.
///
/// Behaviour matrix (all animations read live from
/// <see cref="HoverExpandAnimationKind"/> / HoverExpandSpeed /
/// <see cref="HoverExpandOrigin"/> getters so a live dialog change takes
/// effect on the next expand):
///
///  | gesture              | delay | persist after cursor leaves window?        |
///  |---------------------|-------|---------------------------------------------|
///  | hover RestoreButton | 0.5 s | no — auto-collapse after 2 s cursor outside |
///  | click RestoreButton | none  | yes — stays expanded until next Hide         |
///
/// Window Width/Height is never touched (spec §7.2) — only the inner content's
/// ScaleTransform / Opacity animates. RestoreButton stays visible at top-left
/// while collapsed; ToggleExpandBtn (property-panel opener) only appears when
/// expanded.
///
/// ponytail: scale-around-point uses an explicit TransformGroup
/// [TranslateBack, Scale, TranslateToOrigin] — relying on ScaleTransform
/// .CenterX/Y was unreliable on chrome-less transparent windows. The math
/// composition `Translate(c) * Scale(s) * Translate(-c)` is what we want for
/// "scale s around point c", with c=(18,18) for ButtonCenter and c=(0,0) for
/// ButtonCorner.
///
/// ponytail: 2026-08-21 rewrite. Three problems with the previous design:
///   1. <c>StartAnimation</c> invoked <c>onComplete</c> synchronously after
///      <c>BeginAnimation</c>, so <c>Collapse</c>'s visibility-flip lambda
///      cancelled the animation on the first frame. Moved all <c>onComplete</c>
///      wiring into animation Completed handlers.
///   2. <c>NormalizeStateForKind</c> inferred baseline from the <c>_isExpanded</c>
///      field — fragile because three callers (Expand, ApplyCollapsed, ApplyExpanded)
///      set that field at different times. Renamed to <c>NormalizeFor(isExpanded)</c>
///      which takes the target state as an argument.
///   3. <c>MotionSettingsDialog</c> mutated <c>Zone.HoverExpandAnimation/Origin/Speed</c>
///      but no live <c>HoverExpandBehavior</c> instance was notified. Added the
///      <c>Zone.HoverExpandSettingsChanged</c> event hook so widgets can call
///      <see cref="SetEnabled"/> (which re-runs ApplyOrigin + NormalizeFor) on
///      dialog OK.
/// </summary>
public class HoverExpandBehavior : IDisposable
{
    Window? _window;
    FrameworkElement? _collapsedButton;
    FrameworkElement? _expandedModeElement;
    readonly FrameworkElement _expandedContent;
    readonly Func<HoverExpandAnimationKind> _animationGetter;
    readonly Func<double> _speedGetter;
    readonly Func<HoverExpandOrigin> _originGetter;
    readonly ScaleTransform _scale;
    readonly TranslateTransform _translateBack;
    readonly TranslateTransform _translateToOrigin;
    readonly TransformGroup _transformGroup;
    readonly DispatcherTimer _enterTimer;
    readonly DispatcherTimer _exitTimer;
    readonly DispatcherTimer _pollTimer;
    bool _isExpanded;
    bool _permanent;        // true after Expand(permanent) — suppresses auto-collapse
    bool _disposed;

    public bool IsEnabled { get; set; }

    readonly MouseEventHandler _enterHandler;
    readonly MouseEventHandler _exitHandler;

    public HoverExpandBehavior(
        Window window,
        FrameworkElement collapsedButton,
        FrameworkElement expandedContent,
        FrameworkElement? expandedModeElement,
        Func<HoverExpandAnimationKind> animationGetter,
        Func<double> speedGetter,
        Func<HoverExpandOrigin> originGetter)
    {
        _window = window;
        _collapsedButton = collapsedButton;
        _expandedModeElement = expandedModeElement;
        _expandedContent = expandedContent;
        _animationGetter = animationGetter;
        _speedGetter = speedGetter;
        _originGetter = originGetter;

        // ponytail: explicit composition to scale around (cx, cy):
        //   TransformGroup applies children last-to-first.
        //   Order [TranslateBack, Scale, TranslateToOrigin] →
        //     point p → TranslateToOrigin → Scale → TranslateBack →
        //     (p - c) → s*(p - c) → s*(p - c) + c
        //   i.e. scale s around point c.
        _scale = new ScaleTransform(1, 1);
        _translateBack = new TranslateTransform();
        _translateToOrigin = new TranslateTransform();
        _transformGroup = new TransformGroup();
        _transformGroup.Children.Add(_translateBack);
        _transformGroup.Children.Add(_scale);
        _transformGroup.Children.Add(_translateToOrigin);
        _expandedContent.RenderTransform = _transformGroup;
        ApplyOrigin();

        _enterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _enterTimer.Tick += (_, _) => { _enterTimer.Stop(); ExpandAnimated(permanent: false); };
        _exitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2000) };
        _exitTimer.Tick += (_, _) => { _exitTimer.Stop(); CollapseAnimated(); };
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _pollTimer.Tick += (_, _) => CheckMouseState();

        _enterHandler = (_, _) => { if (IsEnabled) _enterTimer.Start(); };
        _exitHandler = (_, _) => _enterTimer.Stop();
        collapsedButton.MouseEnter += _enterHandler;
        collapsedButton.MouseLeave += _exitHandler;

        // ponytail: no ApplyInitialState here — host widget calls SnapToExpanded/
        // SnapToCollapsed from its Show/ApplyHidden paths to avoid double-state.
        _pollTimer.Start();
    }

    void ApplyOrigin()
    {
        double cx = 0, cy = 0;
        switch (_originGetter())
        {
            case HoverExpandOrigin.ButtonCenter:
                cx = 18; cy = 18;
                break;
            case HoverExpandOrigin.ButtonCorner:
                cx = 0; cy = 0;
                break;
        }
        _translateBack.X = cx;
        _translateBack.Y = cy;
        _translateToOrigin.X = -cx;
        _translateToOrigin.Y = -cy;
    }

    /// <summary>
    /// Live-toggle entry point for EnableRestoreButton. Called by widgets when
    /// EnableRestoreButton flips, and via <c>Zone.HoverExpandSettingsChanged</c>
    /// after MotionSettingsDialog OK. Re-runs ApplyOrigin + NormalizeFor so a
    /// dialog change to kind/origin takes effect on the next expand/collapse.
    /// Does NOT force a state change; the host widget's visibility
    /// (ShowZone/ApplyHidden) is the source of truth.
    /// </summary>
    public void SetEnabled(bool on)
    {
        IsEnabled = on;
        _enterTimer.Stop();
        _exitTimer.Stop();
        ApplyOrigin();
        NormalizeFor(_isExpanded);
    }

    /// <summary>
    /// Non-animated expanded state — called from ShowZone / ShowClock / etc.
    /// at zone-open time. Sets MainContent.Visible, RestoreButton.Collapsed,
    /// scale=1, opacity=1.
    /// </summary>
    public void SnapToExpanded()
    {
        _enterTimer.Stop();
        _exitTimer.Stop();
        _isExpanded = true;
        _permanent = false;
        ApplyOrigin();
        // ponytail: SnapToExpanded needs explicit scale=1, opacity=1 because
        // NormalizeFor(isExpanded: true) only sets stable axes — for
        // ScaleExpand/BounceExpand it leaves ScaleX/Y at whatever the last
        // animation left them at (could be 0). Set the full expanded baseline
        // here so the zone is visible without animation.
        _scale.ScaleX = 1;
        _scale.ScaleY = 1;
        _expandedContent.Opacity = 1;
        _expandedContent.Visibility = Visibility.Visible;
        _collapsedButton!.Visibility = Visibility.Collapsed;
        if (_expandedModeElement != null) _expandedModeElement.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Non-animated collapsed state — called from ApplyHidden when
    /// EnableRestoreButton is true. The next hover/click on RestoreButton
    /// triggers an animated Expand.
    /// </summary>
    public void SnapToCollapsed()
    {
        _enterTimer.Stop();
        _exitTimer.Stop();
        _isExpanded = false;
        _permanent = false;
        ApplyOrigin();
        // ponytail: explicit full collapse baseline. NormalizeFor(isExpanded: false)
        // only sets stable axes now — for ScaleExpand/BounceExpand it leaves
        // ScaleX/Y/Opacity untouched, so we'd otherwise keep them at expanded
        // values and the zone would stay visible.
        _scale.ScaleX = 0;
        _scale.ScaleY = 0;
        _expandedContent.Opacity = 0;
        _expandedContent.Visibility = Visibility.Collapsed;
        _collapsedButton!.Visibility = Visibility.Visible;
        if (_expandedModeElement != null) _expandedModeElement.Visibility = Visibility.Collapsed;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _enterTimer.Stop();
        _exitTimer.Stop();
        _pollTimer.Stop();
        if (_collapsedButton != null)
        {
            _collapsedButton.MouseEnter -= _enterHandler;
            _collapsedButton.MouseLeave -= _exitHandler;
        }
        _window = null;
        _collapsedButton = null;
        _expandedModeElement = null;
    }

    /// <summary>
    /// ponytail: snap the **stable** axes to the value expected by the current
    /// HoverExpandAnimationKind. Animated axes are left untouched so
    /// <see cref="StartAnimation"/> can use them as the `from` value.
    ///
    /// 2026-08-21 rewrite: the previous version set animated axes to the target
    /// value synchronously, which made the subsequent animation a
    /// <c>from == to</c> no-op. WPF does not fire Completed on no-op animations,
    /// so <see cref="CollapseAnimated"/>'s <c>onComplete</c> lambda (which sets
    /// RestoreButton.Visibility = Visible) never ran — the RestoreButton never
    /// reappeared. It also explained the "opacity drops sharply" symptom:
    /// Opacity was being written to 0 synchronously instead of animated 1→0.
    ///
    /// Stable axes (left as-is for animated kinds):
    ///  | VerticalExpand    | ScaleX = 1 (stable)                       |
    ///  | DirectionalExpand | ScaleY = 1 (stable)                       |
    ///  | Fade              | ScaleX = 1, ScaleY = 1 (stable)           |
    ///  | ScaleExpand       | (no stable axis — both axes animated)     |
    ///  | BounceExpand      | (no stable axis)                          |
    ///  | None              | (no animation — full sync via ApplyFinal) |
    /// </summary>
    void NormalizeFor(bool isExpanded)
    {
        // Stop any in-flight animation — we're snapping to baseline.
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _expandedContent.BeginAnimation(UIElement.OpacityProperty, null);

        var kind = _animationGetter();
        switch (kind)
        {
            case HoverExpandAnimationKind.VerticalExpand:
                _scale.ScaleX = 1; // stable axis only — ScaleY is animated
                break;
            case HoverExpandAnimationKind.DirectionalExpand:
                _scale.ScaleY = 1; // stable axis only — ScaleX is animated
                break;
            case HoverExpandAnimationKind.Fade:
                _scale.ScaleX = 1;
                _scale.ScaleY = 1; // stable — Opacity is animated
                break;
            case HoverExpandAnimationKind.None:
                // ponytail: no animation runs in this branch; snap everything to
                // the final state. StartAnimation handles the visibility flip.
                if (isExpanded) { _scale.ScaleX = 1; _scale.ScaleY = 1; _expandedContent.Opacity = 1; }
                else { _scale.ScaleX = 0; _scale.ScaleY = 0; _expandedContent.Opacity = 0; }
                break;
            default: // ScaleExpand, BounceExpand
                // ponytail: no stable axes — both axes are animated and Opacity
                // stays at 1 throughout (the visible "collapse" comes from
                // Scale→0, not from fading). Don't touch ScaleX/Y/Opacity here;
                // let StartAnimation drive them from current value to 0/1.
                break;
        }
    }

    void CheckMouseState()
    {
        if (!IsEnabled || !_isExpanded || _permanent || _window == null) return;
        var pos = Mouse.GetPosition(_window);
        var inside = pos.X >= 0 && pos.Y >= 0
                     && pos.X <= _window.ActualWidth && pos.Y <= _window.ActualHeight;
        if (!inside) _exitTimer.Start();
        else _exitTimer.Stop();
    }

    /// <summary>
    /// Animated expand. Triggered by hover (after 0.5 s) or directly by
    /// RestoreButton click. <paramref name="permanent"/> = true disables the
    /// auto-collapse poll so the window stays open until next Hide.
    /// </summary>
    public void ExpandAnimated(bool permanent)
    {
        if (!IsEnabled || _isExpanded) return;
        _isExpanded = true;
        _permanent = permanent;
        _exitTimer.Stop();
        _enterTimer.Stop();
        ApplyOrigin();                                    // re-apply in case origin changed
        NormalizeFor(isExpanded: true);                   // snap stable axes BEFORE visibility flip
        _expandedContent.Visibility = Visibility.Visible;
        _collapsedButton!.Visibility = Visibility.Collapsed;
        if (_expandedModeElement != null) _expandedModeElement.Visibility = Visibility.Visible;
        StartAnimation(isExpand: true);
    }

    /// <summary>
    /// Animated collapse. The <paramref name="onComplete"/> lambda runs only
    /// when WPF fires the animation Completed event — not synchronously.
    /// </summary>
    public void CollapseAnimated(Action? onComplete = null)
    {
        if (!_isExpanded) return;
        _isExpanded = false;
        _permanent = false;
        _enterTimer.Stop();
        _exitTimer.Stop();
        ApplyOrigin();                                    // re-apply in case origin changed
        NormalizeFor(isExpanded: false);                  // snap stable axes BEFORE animation
        StartAnimation(isExpand: false, onComplete: () =>
        {
            _expandedContent.Visibility = Visibility.Collapsed;
            _collapsedButton!.Visibility = Visibility.Visible;
            if (_expandedModeElement != null) _expandedModeElement.Visibility = Visibility.Collapsed;
            onComplete?.Invoke();
        });
    }

    /// <summary>
    /// ponytail: from values are read from the CURRENT scale/opacity so
    /// switching animation kind mid-state animates from whatever the previous
    /// kind left behind. Stable axes are already set by NormalizeFor before
    /// this is called. <paramref name="onComplete"/> is invoked from the
    /// animation's Completed event — never synchronously.
    /// </summary>
    void StartAnimation(bool isExpand, Action? onComplete = null)
    {
        var kind = _animationGetter();
        var speed = Math.Max(0.1, _speedGetter());
        var duration = new Duration(TimeSpan.FromMilliseconds(200.0 / speed));

        switch (kind)
        {
            case HoverExpandAnimationKind.None:
                ApplyFinal(isExpand);
                onComplete?.Invoke();
                return;
            case HoverExpandAnimationKind.Fade:
                AnimateOpacity(_expandedContent.Opacity, isExpand ? 1 : 0, duration,
                    isExpand ? EasingMode.EaseOut : EasingMode.EaseIn, onComplete);
                return;
            case HoverExpandAnimationKind.VerticalExpand:
                AnimateScaleY(_scale.ScaleY, isExpand ? 1 : 0, duration,
                    isExpand ? EasingMode.EaseOut : EasingMode.EaseIn, onComplete);
                return;
            case HoverExpandAnimationKind.DirectionalExpand:
                AnimateScaleX(_scale.ScaleX, isExpand ? 1 : 0, duration,
                    isExpand ? EasingMode.EaseOut : EasingMode.EaseIn, onComplete);
                return;
            case HoverExpandAnimationKind.BounceExpand:
                AnimateBounce(isExpand, duration, onComplete);
                return;
            default: // ScaleExpand
                AnimateScaleXY(_scale.ScaleX, isExpand ? 1 : 0, duration,
                    isExpand ? EasingMode.EaseOut : EasingMode.EaseIn, onComplete);
                return;
        }
    }

    void ApplyFinal(bool isExpand)
    {
        if (isExpand) { _scale.ScaleX = 1; _scale.ScaleY = 1; _expandedContent.Opacity = 1; }
        else { _scale.ScaleX = 0; _scale.ScaleY = 0; _expandedContent.Opacity = 0; }
    }

    void AnimateScaleXY(double from, double to, Duration duration, EasingMode ease, Action? onComplete)
    {
        // ponytail: from == to means a no-op animation — WPF does not fire
        // Completed on no-op, so onComplete would never run. Short-circuit
        // synchronously to guarantee the visibility flip after Collapse.
        if (Math.Abs(from - to) < 1e-9)
        {
            _scale.ScaleX = to;
            _scale.ScaleY = to;
            onComplete?.Invoke();
            return;
        }
        var ax = new DoubleAnimation(from, to, duration) { EasingFunction = new CubicEase { EasingMode = ease } };
        var ay = new DoubleAnimation(from, to, duration) { EasingFunction = new CubicEase { EasingMode = ease } };
        // ponytail: only fire onComplete when BOTH axes complete. Use a guard so the
        // faster axis doesn't fire it twice. WPF Completed can fire once per axis.
        bool done = false;
        Action fireOnce = () => { if (done) return; done = true; onComplete?.Invoke(); };
        ax.Completed += (_, _) => fireOnce();
        ay.Completed += (_, _) => fireOnce();
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, ax);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, ay);
    }

    void AnimateScaleX(double from, double to, Duration duration, EasingMode ease, Action? onComplete)
    {
        if (Math.Abs(from - to) < 1e-9)
        {
            _scale.ScaleX = to;
            onComplete?.Invoke();
            return;
        }
        var ax = new DoubleAnimation(from, to, duration) { EasingFunction = new CubicEase { EasingMode = ease } };
        ax.Completed += (_, _) => onComplete?.Invoke();
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, ax);
    }

    void AnimateScaleY(double from, double to, Duration duration, EasingMode ease, Action? onComplete)
    {
        if (Math.Abs(from - to) < 1e-9)
        {
            _scale.ScaleY = to;
            onComplete?.Invoke();
            return;
        }
        var ay = new DoubleAnimation(from, to, duration) { EasingFunction = new CubicEase { EasingMode = ease } };
        ay.Completed += (_, _) => onComplete?.Invoke();
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, ay);
    }

    void AnimateOpacity(double from, double to, Duration duration, EasingMode ease, Action? onComplete)
    {
        if (Math.Abs(from - to) < 1e-9)
        {
            _expandedContent.Opacity = to;
            onComplete?.Invoke();
            return;
        }
        var anim = new DoubleAnimation(from, to, duration) { EasingFunction = new CubicEase { EasingMode = ease } };
        anim.Completed += (_, _) => onComplete?.Invoke();
        _expandedContent.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    void AnimateBounce(bool isExpand, Duration duration, Action? onComplete)
    {
        // ponytail: short-circuit when current scale already equals the target keyframe
        // start. BounceExpand expand collapses to "first keyframe = current" + "ends at
        // 1", collapse = "first keyframe = current" + "ends at 0" — if current is
        // already at the endpoint we still need to fire the keyframes (bounce easing),
        // so this only short-circuits the degenerate current==0 collapse case where
        // there'd be nothing to bounce. For expand (target=1) we always want the bounce.
        if (!isExpand && Math.Abs(_scale.ScaleX) < 1e-9)
        {
            _scale.ScaleX = 0;
            _scale.ScaleY = 0;
            onComplete?.Invoke();
            return;
        }
        var bounce = new DoubleAnimationUsingKeyFrames();
        var ease = new BounceEase { Bounces = 2, Bounciness = 2, EasingMode = EasingMode.EaseOut };
        if (isExpand)
        {
            bounce.KeyFrames.Add(new EasingDoubleKeyFrame(_scale.ScaleX, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            bounce.KeyFrames.Add(new EasingDoubleKeyFrame(1.08, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120.0 / speed())), ease));
            bounce.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(duration.TimeSpan)));
        }
        else
        {
            bounce.KeyFrames.Add(new EasingDoubleKeyFrame(_scale.ScaleX, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            bounce.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(duration.TimeSpan), ease));
        }
        // ponytail: BounceExpand drives both ScaleX and ScaleY with the same keyframe
        // animation. Completed fires once per property — guard so onComplete fires once.
        bool done = false;
        Action fireOnce = () => { if (done) return; done = true; onComplete?.Invoke(); };
        bounce.Completed += (_, _) => fireOnce();
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, bounce);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, bounce);
    }

    // ponytail: BounceExpand needs its own speed read here because it's not threaded
    // through the same AnimateXxx signature.
    double speed() => Math.Max(0.1, _speedGetter());
}
