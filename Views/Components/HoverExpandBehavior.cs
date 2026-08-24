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
/// (the 36×36 circle visible when the zone is hidden). Two gates:
///   1. <c>IsEnabled</c> (driven by <c>EnableRestoreButton</c>) — master switch;
///      when off the button is hidden and nothing animates.
///   2. <c>hoverAutoExpandGetter()</c> (driven by <c>HoverAutoExpand</c>) — when
///      false, cursor-hover on the RestoreButton does nothing; direct clicks
///      still expand. Both are read live so a PropertyPanel toggle takes
///      effect on the next hover/click.
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
/// ScaleTransform / Opacity animates. RestoreButton stays visible while
/// collapsed — at the window's top-left corner for ButtonCorner origin, at the
/// window's center for ButtonCenter (ApplyOrigin repositions it live); the
/// ToggleExpandBtn (property-panel opener) only appears when expanded.
///
/// ponytail: scale-around-point uses an explicit TransformGroup — relying on
/// ScaleTransform.CenterX/Y was unreliable on chrome-less transparent windows.
/// WPF TransformGroup applies children FIRST-TO-LAST (probe-verified: the
/// pre-2026-08-23 comment claimed the opposite, so ButtonCenter collapsed to
/// a point OUTSIDE the top-left corner instead of the button). The correct
/// order for "scale s around point c" is
/// [TranslateToOrigin(-c), Scale, TranslateBack(+c)]:
///   p → p−c → s(p−c) → s(p−c)+c.
/// Anchor c is set by ApplyOrigin per <see cref="HoverExpandOrigin"/>:
/// ButtonCorner → the RestoreButton's top-left corner at the window corner;
/// ButtonCenter → the window's center (the centered button's center).
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
    readonly Func<bool> _hoverAutoExpandGetter;
    readonly ScaleTransform _scale;
    readonly TranslateTransform _translateBack;
    readonly TranslateTransform _translateToOrigin;
    readonly TransformGroup _transformGroup;
    readonly DispatcherTimer _enterTimer;
    readonly DispatcherTimer _exitTimer;
    readonly DispatcherTimer _pollTimer;
    // ponytail: 2026-08-23 batch wave — single-shot timer that delays one animated
    // expand/collapse so "Show All" / "Minimize All" play as a staggered cascade.
    // Cancelled by any direct state change (snap/expand/collapse/SetEnabled/Dispose)
    // so a queued wave action can never fight a newer user interaction.
    DispatcherTimer? _waveTimer;
    // ponytail: true while a staggered collapse is queued by CollapseAfterDelay.
    // The widgets' "ghost-stamp" model-sync (model hidden + still expanded + content
    // visible → SnapToCollapsed) reads this so a queued collapse is not instantly
    // snapped mid-delay — that killed the batch minimize animation (zones have no
    // such stamp, which is why only the three widgets lost their animation).
    bool _waveCollapsePending;
    bool _isExpanded;
    bool _permanent;        // true after Expand(permanent) — suppresses auto-collapse
    bool _disposed;

    /// <summary>Per-window start delay between adjacent windows in the batch wave.</summary>
    public const double BatchStaggerMs = 70;

    public bool IsEnabled { get; set; }

    /// <summary>
    /// ponytail: ghost-glass fix — true while content is expanded (set by SnapToExpanded /
    /// ExpandAnimated). Host windows read this to gate DWM acrylic so a collapsed window
    /// (full-size, content scaled to 0) never re-enables the full-window glass tint.
    /// </summary>
    public bool IsExpanded => _isExpanded;

    /// <summary>
    /// True while a staggered batch collapse is queued (CollapseAfterDelay) — the widgets'
    /// model-sync stamps skip their instant SnapToCollapsed while this is set so the
    /// queued collapse animation actually plays.
    /// </summary>
    public bool IsCollapsePending => _waveCollapsePending;

    /// <summary>Fired when content becomes visible (animated expand, hover or click).</summary>
    public event Action? Expanded;

    /// <summary>Fired when content finishes collapsing to the RestoreButton.</summary>
    public event Action? Collapsed;

    readonly MouseEventHandler _enterHandler;
    readonly MouseEventHandler _exitHandler;

    public HoverExpandBehavior(
        Window window,
        FrameworkElement collapsedButton,
        FrameworkElement expandedContent,
        FrameworkElement? expandedModeElement,
        Func<HoverExpandAnimationKind> animationGetter,
        Func<double> speedGetter,
        Func<HoverExpandOrigin> originGetter,
        Func<bool> hoverAutoExpandGetter)
    {
        _window = window;
        _collapsedButton = collapsedButton;
        _expandedModeElement = expandedModeElement;
        _expandedContent = expandedContent;
        _animationGetter = animationGetter;
        _speedGetter = speedGetter;
        _originGetter = originGetter;
        _hoverAutoExpandGetter = hoverAutoExpandGetter;

        // ponytail: explicit composition to scale around (cx, cy).
        // WPF TransformGroup applies children FIRST-TO-LAST: with children
        // [A, B, C] a point p maps to C(B(A(p))). So for "scale s around c" the
        // order must be [TranslateToOrigin(-c), Scale, TranslateBack(+c)] —
        // p → p−c → s(p−c) → s(p−c)+c. The previous [TranslateBack, Scale,
        // TranslateToOrigin] order computed s·p+(s−1)·c, whose collapse point
        // (s=0) is −c — for ButtonCenter the content collapsed to a point
        // 18px OUTSIDE the window's top-left corner (a corner grow with the
        // RestoreButton parked in the middle of the window).
        _scale = new ScaleTransform(1, 1);
        _translateBack = new TranslateTransform();
        _translateToOrigin = new TranslateTransform();
        _transformGroup = new TransformGroup();
        _transformGroup.Children.Add(_translateToOrigin);
        _transformGroup.Children.Add(_scale);
        _transformGroup.Children.Add(_translateBack);
        _expandedContent.RenderTransform = _transformGroup;
        ApplyOrigin();

        _enterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _enterTimer.Tick += (_, _) => { _enterTimer.Stop(); ExpandAnimated(permanent: false); };
        _exitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2000) };
        _exitTimer.Tick += (_, _) => { _exitTimer.Stop(); CollapseAnimated(); };
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _pollTimer.Tick += (_, _) => CheckMouseState();

        // ponytail: IsEnabled gates the entire feature; hoverAutoExpandGetter further
        // disables ONLY the hover trigger. Direct clicks on RestoreButton bypass
        // the enter handler and go through ExpandAnimated(permanent: true), so they
        // still work when HoverAutoExpand=false. The getter is invoked on every
        // MouseEnter so toggling the PropertyPanel checkbox takes effect immediately.
        _enterHandler = (_, _) => { if (IsEnabled && _hoverAutoExpandGetter()) _enterTimer.Start(); };
        _exitHandler = (_, _) => _enterTimer.Stop();
        collapsedButton.MouseEnter += _enterHandler;
        collapsedButton.MouseLeave += _exitHandler;

        // ponytail: no ApplyInitialState here — host widget calls SnapToExpanded/
        // SnapToCollapsed from its Show/ApplyHidden paths to avoid double-state.
        _pollTimer.Start();
    }

    /// <summary>
    /// Mode-specific animation anchor + RestoreButton placement. The anchor is in
    /// MainContent coordinates (MainContent fills the window in all four hosts):
    ///   • ButtonCorner — RestoreButton parked at the window's top-left corner;
    ///     anchor = the button's top-left corner (margin, margin). Axis kinds
    ///     unfold downward / rightward from the top / left edge (classic corner
    ///     look), Scale/Bounce grow radially from the corner.
    ///   • ButtonCenter — RestoreButton parked at the window's center; anchor =
    ///     the button's center = (contentW/2, contentH/2). Axis kinds split open
    ///     from the middle line (top half up, bottom half down / left half left,
    ///     right half right), Scale/Bounce grow radially from the center point —
    ///     the "expand from the middle" feel. Kinds/easing/effects are identical
    ///     to the corner mode; only the anchor moves.
    /// The size is read live (the window keeps its full size while collapsed,
    /// spec §7.2), so a resize or a MotionSettingsDialog origin change takes
    /// effect on the next state change / animation start.
    /// </summary>
    void ApplyOrigin()
    {
        double cx, cy;
        switch (_originGetter())
        {
            case HoverExpandOrigin.ButtonCorner:
                PositionRestoreButton(topLeft: true);
                cx = CornerButtonMargin;
                cy = CornerButtonMargin;
                break;
            default: // HoverExpandOrigin.ButtonCenter
                PositionRestoreButton(topLeft: false);
                cx = ResolveContentWidth() / 2.0;
                cy = ResolveContentHeight() / 2.0;
                break;
        }
        _translateBack.X = cx;
        _translateBack.Y = cy;
        _translateToOrigin.X = -cx;
        _translateToOrigin.Y = -cy;
    }

    /// <summary>RestoreButton offset from the window's top-left corner in ButtonCorner mode.</summary>
    const double CornerButtonMargin = 4;

    void PositionRestoreButton(bool topLeft)
    {
        if (_collapsedButton == null) return;
        if (topLeft)
        {
            _collapsedButton.HorizontalAlignment = HorizontalAlignment.Left;
            _collapsedButton.VerticalAlignment = VerticalAlignment.Top;
            _collapsedButton.Margin = new Thickness(CornerButtonMargin, CornerButtonMargin, 0, 0);
        }
        else
        {
            _collapsedButton.HorizontalAlignment = HorizontalAlignment.Center;
            _collapsedButton.VerticalAlignment = VerticalAlignment.Center;
            _collapsedButton.Margin = new Thickness(0);
        }
    }

    /// <summary>
    /// Content size in layout pixels for the center anchor. MainContent.ActualWidth
    /// is 0 while collapsed (Visibility=Collapsed skips measure), so prefer the
    /// window — which deliberately keeps its full size while collapsed. Falls back
    /// to the window's declared Width/Height before first layout, then to a sane
    /// constant (only ever used when no animation can run yet anyway).
    /// </summary>
    double ResolveContentWidth()
    {
        if (_window != null && _window.ActualWidth > 1) return _window.ActualWidth;
        if (_expandedContent.ActualWidth > 1) return _expandedContent.ActualWidth;
        if (_window != null && !double.IsNaN(_window.Width) && _window.Width > 1) return _window.Width;
        return 400;
    }

    double ResolveContentHeight()
    {
        if (_window != null && _window.ActualHeight > 1) return _window.ActualHeight;
        if (_expandedContent.ActualHeight > 1) return _expandedContent.ActualHeight;
        if (_window != null && !double.IsNaN(_window.Height) && _window.Height > 1) return _window.Height;
        return 300;
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
        // ponytail: 2026-08-26 — do NOT cancel a queued batch wave. The widgets'
        // model-sync handlers (CalendarWidget.OnCalendarsChanged / StickyNoteWindow.
        // OnNotesChanged) call SetEnabled on EVERY UpdateCalendar/UpdateNote —
        // including the one fired by HideCalendar/HideNote right AFTER
        // CollapseAfterDelay armed the stagger timer. CancelWave() there killed the
        // queued collapse, so "Minimize All" silently skipped calendar + sticky note
        // (the clock was immune because OnClocksChanged has no SetEnabled call), and
        // it killed the queued "Show All" expand — stranding the window as a
        // full-size invisible Topmost ghost with only the RestoreButton visible
        // (the reported "周围有透明边框" ring). The queued wave's tick reads all
        // getters live, so re-applying settings during the delay must not disturb it.
        bool wavePending = _waveTimer != null;
        if (!wavePending) CancelWave();
        IsEnabled = on;
        _enterTimer.Stop();
        _exitTimer.Stop();
        ApplyOrigin();
        NormalizeFor(_isExpanded);

        // ponytail: 2026-08-26 — while a wave is queued, leave the visual baseline as
        // the wave's caller set it (collapsed + hidden button for Show, expanded for
        // Hide). Snapping here would flash the RestoreButton during the stagger delay
        // and, for a queued collapse, hide the content instantly — bypassing the very
        // animation the batch wave exists to play.
        if (wavePending) return;

        // ponytail: 2026-08-23 residual-frame fix — a settings change
        // (MotionSettingsDialog OK / EnableRestoreButton toggle / RefreshZone) can
        // land mid-animation. NormalizeFor kills the in-flight animation, which would
        // FREEZE the content at the interrupted partial scale/opacity — e.g. a
        // collapse killed at scale≈0.1 leaves a faint rounded-rect outline hugging
        // the RestoreButton (the reported "一圈边框" ghost around the button).
        // Instead of freezing a partial frame, snap to the nearest consistent
        // end-state: expanded → full-size content, collapsed → fully hidden content
        // with the button visible. Both are visual no-ops when no animation was
        // in flight (values already at the endpoints), so the existing "don't force
        // a state change" contract is preserved.
        if (_isExpanded)
        {
            _scale.ScaleX = 1;
            _scale.ScaleY = 1;
            _expandedContent.Opacity = 1;
            _expandedContent.Visibility = Visibility.Visible;
            if (_collapsedButton != null) _collapsedButton.Visibility = Visibility.Collapsed;
            if (_expandedModeElement != null) _expandedModeElement.Visibility = Visibility.Visible;
        }
        else
        {
            _scale.ScaleX = 0;
            _scale.ScaleY = 0;
            _expandedContent.Opacity = 0;
            _expandedContent.Visibility = Visibility.Collapsed;
            // ponytail: 2026-08-23 — only show the RestoreButton when the feature is
            // actually enabled. The previous unconditional Visible left a dead button
            // behind when EnableRestoreButton was toggled off on a hidden window
            // (SetEnabled(false) after ApplyHidden's full-hide).
            if (_collapsedButton != null)
                _collapsedButton.Visibility = IsEnabled ? Visibility.Visible : Visibility.Collapsed;
            if (_expandedModeElement != null) _expandedModeElement.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Non-animated expanded state — called from ShowZone / ShowClock / etc.
    /// at zone-open time. Sets MainContent.Visible, RestoreButton.Collapsed,
    /// scale=1, opacity=1.
    /// </summary>
    public void SnapToExpanded()
    {
        CancelWave();
        _enterTimer.Stop();
        _exitTimer.Stop();
        _isExpanded = true;
        // ponytail: 2026-08-23 auto-minimize fix — a window made visible by a Show
        // path is PERMANENT. The previous value (false) armed CheckMouseState's
        // 2 s exit timer, so every zone/widget shown at startup or via "Show All"
        // silently collapsed to the RestoreButton ~2 s later as soon as the cursor
        // was elsewhere — the reported "自动最小化" that preceded the ghost-ring bug.
        // Only the hover-triggered expand (ExpandAnimated(permanent:false)) may
        // auto-collapse; that path re-arms _permanent=false itself.
        _permanent = true;
        ApplyOrigin();
        // ponytail: stale-base fix — drop any hold-end animation FIRST, otherwise the
        // setters below only write the BASE value and the held animation (e.g. a
        // finished collapse holding 0) keeps the effective value, leaving the zone
        // invisible after "hide → management UI show".
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _expandedContent.BeginAnimation(UIElement.OpacityProperty, null);
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
        CancelWave();
        _enterTimer.Stop();
        _exitTimer.Stop();
        bool wasExpanded = _isExpanded;
        _isExpanded = false;
        _permanent = false;
        ApplyOrigin();
        // ponytail: stale-base fix — drop any hold-end animation FIRST (see
        // SnapToExpanded), then write the full collapsed baseline.
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _expandedContent.BeginAnimation(UIElement.OpacityProperty, null);
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
        // ponytail: 2026-08-23 — notify hosts when collapsing from an expanded state so
        // their acrylic gate disables the DWM glass. A model-driven SnapToCollapsed
        // (the widgets' ghost-stamp lock) used to leave the glass enabled — the
        // "button in the middle + liquid glass around" ghost after a restore-click.
        if (wasExpanded) Collapsed?.Invoke();
    }

    /// <summary>
    /// Non-animated fully-hidden state — called from ApplyHidden / HideZone /
    /// HideClock / HideCalendar / HideNote when EnableRestoreButton is FALSE
    /// (the window itself disappears: content collapsed, window shrunk to 36×36,
    /// then Window.Hide()). Unlike <see cref="SnapToCollapsed"/>, the RestoreButton
    /// is NOT shown — nothing remains on the desktop.
    ///
    /// ponytail: 2026-08-23 ghost-glass fix — the old full-hide paths only wrote
    /// <c>MainContent.Visibility = Collapsed</c> and left <c>_isExpanded == true</c>
    /// with scale/opacity at 1. Any later ApplyStyle/ApplyAcrylic call (settings
    /// change, RefreshZone, live preview) then read <c>IsExpanded == true</c> and
    /// re-enabled the whole-window DWM acrylic on the hidden window — the reported
    /// "空白内容的液态玻璃" / "一圈透明的框" ghosts. Resetting the state here keeps
    /// the acrylic gate honest and gives the next Show path a clean baseline.
    /// </summary>
    public void SnapToFullHidden()
    {
        CancelWave();
        _enterTimer.Stop();
        _exitTimer.Stop();
        bool wasExpanded = _isExpanded;
        _isExpanded = false;
        _permanent = false;
        ApplyOrigin();
        // ponytail: stale-base fix — drop hold-end animations FIRST, then write the
        // full hidden baseline (see SnapToExpanded / SnapToCollapsed).
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _expandedContent.BeginAnimation(UIElement.OpacityProperty, null);
        _scale.ScaleX = 0;
        _scale.ScaleY = 0;
        _expandedContent.Opacity = 0;
        _expandedContent.Visibility = Visibility.Collapsed;
        if (_collapsedButton != null) _collapsedButton.Visibility = Visibility.Collapsed;
        if (_expandedModeElement != null) _expandedModeElement.Visibility = Visibility.Collapsed;
        // Hosts gate DWM acrylic on IsExpanded — notify them so a full-hide can
        // never leave the full-window glass tint behind.
        if (wasExpanded) Collapsed?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelWave();
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
        // ponytail: stale-base fix (2026-08-23). BeginAnimation(null) reverts a
        // property to its BASE value, which was last written by SnapToCollapsed /
        // SnapToExpanded — NOT updated after animated transitions. So the first
        // transition after a snap animated fine, but the NEXT one read a stale
        // from-value equal to its target → the `from == to` short-circuits fired →
        // instant flip with no animation. Observed as: kinds whose last transition
        // had been animated lost the NEXT animation (alternating per cycle) —
        // e.g. a zone collapsed at startup expanded fine (from base 0) but its
        // collapse was a no-op (reverted to base 0); a zone visible at startup
        // collapsed fine but its expand was a no-op (reverted to base 1).
        // Fix: capture the CURRENT effective values (animated value while an
        // animation holds) and write them back as the new base BEFORE removing the
        // animation, so every transition starts from the real current state —
        // including mid-flight interruptions (expand at 0.4 → collapse 0.4→0).
        double sx = _scale.ScaleX, sy = _scale.ScaleY, op = _expandedContent.Opacity;
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _expandedContent.BeginAnimation(UIElement.OpacityProperty, null);
        _scale.ScaleX = sx;
        _scale.ScaleY = sy;
        _expandedContent.Opacity = op;

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

        // ponytail: ghost-content fix — Opacity is ONLY animated by Fade. Every other
        // kind keeps Opacity=1 permanently. SnapToCollapsed sets Opacity=0 and, without
        // this restore, the next expand would scale the content up with Opacity still 0:
        // an empty full-window glass block after "settings change while collapsed →
        // click restore". (Fade expands from the stored 0 → fade-in; None syncs its own.)
        if (kind != HoverExpandAnimationKind.Fade && kind != HoverExpandAnimationKind.None)
            _expandedContent.Opacity = 1;
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
    public void ExpandAnimated(bool permanent) => ExpandAnimated(permanent, force: false);

    /// <summary>
    /// Animated expand with an optional <paramref name="force"/> that bypasses the
    /// IsEnabled (EnableRestoreButton) gate — used by the batch "Show All" wave so a
    /// window with the restore-button feature disabled still plays its configured
    /// kind/speed/origin entrance animation.
    /// </summary>
    public void ExpandAnimated(bool permanent, bool force)
    {
        if ((!IsEnabled && !force) || _isExpanded) return;
        CancelWave();
        _isExpanded = true;
        _permanent = permanent;
        _exitTimer.Stop();
        _enterTimer.Stop();
        ApplyOrigin();                                    // re-apply in case origin changed
        NormalizeFor(isExpanded: true);                   // snap stable axes BEFORE visibility flip
        _expandedContent.Visibility = Visibility.Visible;
        _collapsedButton!.Visibility = Visibility.Collapsed;
        if (_expandedModeElement != null) _expandedModeElement.Visibility = Visibility.Visible;
        // ponytail: notify hosts AFTER the state flip so their acrylic re-apply sees
        // IsExpanded == true (ghost-glass fix: liquid glass only while expanded).
        Expanded?.Invoke();
        StartAnimation(isExpand: true);
    }

    /// <summary>
    /// Animated collapse. The <paramref name="onComplete"/> lambda runs only
    /// when WPF fires the animation Completed event — not synchronously.
    /// </summary>
    public void CollapseAnimated(Action? onComplete = null)
    {
        if (!_isExpanded) return;
        CancelWave();
        _isExpanded = false;
        _permanent = false;
        _enterTimer.Stop();
        _exitTimer.Stop();
        ApplyOrigin();                                    // re-apply in case origin changed
        NormalizeFor(isExpanded: false);                  // snap stable axes BEFORE animation

        // ponytail: safety net — WPF silently cancels in-flight animations when
        // another BeginAnimation(null) / BeginAnimation(newAnim) runs (SetEnabled,
        // NormalizeFor, MotionSettingsDialog OK path). When that happens the
        // Completed event never fires and MainContent.Visibility stays Visible,
        // producing the "ghost rectangle" symptom. Queue an idempotent flip on a
        // delayed DispatcherTimer so even if Completed is lost, Visibility gets
        // corrected after the animation's worst-case duration.
        int delayMs = (int)Math.Max(260, 260.0 / Math.Max(0.1, _speedGetter()));
        var safetyTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(delayMs)
        };
        safetyTimer.Tick += (_, _) =>
        {
            safetyTimer.Stop();
            if (!_isExpanded)
            {
                // ponytail: residual-frame fix — if the collapse animation was killed
                // mid-flight (SetEnabled/NormalizeFor), the content is frozen at a
                // partial scale/opacity. Visibility is flipped here, but also zero the
                // scale/opacity so no partial frame can survive a later state change
                // (the faint "一圈边框" ring hugging the RestoreButton).
                _scale.ScaleX = 0;
                _scale.ScaleY = 0;
                _expandedContent.Opacity = 0;
                _expandedContent.Visibility = Visibility.Collapsed;
                _collapsedButton!.Visibility = Visibility.Visible;
                if (_expandedModeElement != null) _expandedModeElement.Visibility = Visibility.Collapsed;
                // ponytail: ghost-glass fix — hosts disable DWM acrylic once collapsed.
                Collapsed?.Invoke();
            }
        };
        safetyTimer.Start();

        StartAnimation(isExpand: false, onComplete: () =>
        {
            // ponytail: also zero scale/opacity on the normal completion path —
            // idempotent belt-and-braces with the safety timer above.
            _scale.ScaleX = 0;
            _scale.ScaleY = 0;
            _expandedContent.Opacity = 0;
            _expandedContent.Visibility = Visibility.Collapsed;
            _collapsedButton!.Visibility = Visibility.Visible;
            if (_expandedModeElement != null) _expandedModeElement.Visibility = Visibility.Collapsed;
            Collapsed?.Invoke();
            onComplete?.Invoke();
        });
    }

    // ── Batch wave (Show All / Minimize All stagger) ──

    /// <summary>
    /// Staggered animated show for batch "Show All": the window plays its own
    /// configured expand animation after <paramref name="delayMs"/>, so a batch of
    /// windows opens as a left-to-right / top-to-bottom cascade. Works even when the
    /// restore-button feature is disabled (force). Any newer state change cancels
    /// the pending wave.
    /// </summary>
    public void ShowAfterDelay(double delayMs)
    {
        CancelWave();
        if (delayMs <= 0) { ExpandAnimated(permanent: true, force: true); return; }
        _waveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delayMs) };
        _waveTimer.Tick += (_, _) =>
        {
            CancelWave();
            ExpandAnimated(permanent: true, force: true);
        };
        _waveTimer.Start();
    }

    /// <summary>
    /// Staggered animated collapse for batch "Minimize All". The host's
    /// <paramref name="onComplete"/> runs once the collapse finishes — e.g. the
    /// full-hide finalize (shrink to 36×36 + Window.Hide) for windows whose
    /// EnableRestoreButton is off. If the window is already collapsed when the
    /// delay elapses, the finalize still runs so a full-hide can never be stranded.
    /// </summary>
    public void CollapseAfterDelay(double delayMs, Action? onComplete)
    {
        CancelWave();
        if (delayMs <= 0)
        {
            if (_isExpanded) CollapseAnimated(onComplete);
            else onComplete?.Invoke();
            return;
        }
        _waveCollapsePending = true;
        _waveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delayMs) };
        _waveTimer.Tick += (_, _) =>
        {
            CancelWave();
            if (_isExpanded) CollapseAnimated(onComplete);
            else onComplete?.Invoke();
        };
        _waveTimer.Start();
    }

    void CancelWave()
    {
        _waveCollapsePending = false;
        if (_waveTimer != null)
        {
            _waveTimer.Stop();
            _waveTimer = null;
        }
    }

    /// <summary>
    /// ponytail: from values are read from the CURRENT scale/opacity so
    /// switching animation kind mid-state animates from whatever the previous
    /// kind left behind. Stable axes are already set by NormalizeFor before
    /// this is called. <paramref name="onComplete"/> is invoked from the
    /// animation's Completed event — never synchronously.
    ///
    /// Collapse is the per-kind mirror of the expand (no separate collapse
    /// setting — it follows the kind/origin/speed chosen for the expand):
    ///  | kind            | expand                      | collapse (收起)                          |
    ///  |-----------------|-----------------------------|------------------------------------------|
    ///  | Fade            | opacity 0→1, EaseOut        | opacity 1→0, EaseIn                      |
    ///  | VerticalExpand  | ScaleY 0→1, EaseOut         | ScaleY 1→0, EaseIn                       |
    ///  | DirectionalExpand (横向展开) | ScaleX 0→1, EaseOut | ScaleX 1→0, EaseIn             |
    ///  | ScaleExpand     | ScaleXY 0→1, EaseOut        | ScaleXY 1→0, EaseIn                      |
    ///  | BounceExpand    | 0→1.08 (bounce)→1           | squash-bounce 1→0.85, then 0.85→0 EaseOut |
    ///  | None            | instant                     | instant                                  |
    /// CubicEase EaseIn/EaseOut are exact time-mirrors, so the simple kinds
    /// collapse as the frame-exact reversal of their expand.
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
        // Also write the final value into the BASE value (hold-end keeps showing the
        // animated value either way) so a later BeginAnimation(null) can never revert
        // to a stale snap value — belt-and-braces with NormalizeFor's capture.
        bool done = false;
        Action fireOnce = () => { if (done) return; done = true; onComplete?.Invoke(); };
        ax.Completed += (_, _) => { _scale.ScaleX = to; fireOnce(); };
        ay.Completed += (_, _) => { _scale.ScaleY = to; fireOnce(); };
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
        // ponytail: sync the final value into the base before firing onComplete —
        // see AnimateScaleXY for the stale-base rationale.
        ax.Completed += (_, _) => { _scale.ScaleX = to; onComplete?.Invoke(); };
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
        // ponytail: sync the final value into the base before firing onComplete —
        // see AnimateScaleXY for the stale-base rationale.
        ay.Completed += (_, _) => { _scale.ScaleY = to; onComplete?.Invoke(); };
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
        // ponytail: sync the final value into the base before firing onComplete —
        // see AnimateScaleXY for the stale-base rationale.
        anim.Completed += (_, _) => { _expandedContent.Opacity = to; onComplete?.Invoke(); };
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
            // ponytail: suitable 弹性收起 — a bounce applied to the tail of a 1→0
            // scale strobes the whole window between "gone" and "half size" (the
            // BounceEase curve hits f=1 at 40%/80%/100% of its span, so scale
            // pulses 0↔0.5↔0 — a mirror-free but very flickery exit). Instead the
            // bounce lives at the START, near full size: a quick springy squash
            // 1→0.85 with BounceEase EaseOut (pulses only within 0.85…0.925, never
            // negative → no mirrored frame), then a CubicEase EaseOut 0.85→0
            // vanish — fast continuation of the squash's velocity, decelerating
            // into the anchor point. Same Bounces/Bounciness and same total
            // duration as the expand, so speed settings apply uniformly.
            var squashTime = TimeSpan.FromMilliseconds(duration.TimeSpan.TotalMilliseconds * 0.45);
            bounce.KeyFrames.Add(new EasingDoubleKeyFrame(_scale.ScaleX, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            bounce.KeyFrames.Add(new EasingDoubleKeyFrame(0.85, KeyTime.FromTimeSpan(squashTime), ease));
            bounce.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(duration.TimeSpan),
                new CubicEase { EasingMode = EasingMode.EaseOut }));
        }
        // ponytail: BounceExpand drives both ScaleX and ScaleY with the same keyframe
        // animation. Completed fires once per property — guard so onComplete fires once.
        // Sync the final keyframe value into the base on every completion — see
        // AnimateScaleXY for the stale-base rationale.
        double final = isExpand ? 1 : 0;
        bool done = false;
        Action fireOnce = () => { if (done) return; done = true; onComplete?.Invoke(); };
        bounce.Completed += (_, _) => { _scale.ScaleX = final; _scale.ScaleY = final; fireOnce(); };
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, bounce);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, bounce);
    }

    // ponytail: BounceExpand needs its own speed read here because it's not threaded
    // through the same AnimateXxx signature.
    double speed() => Math.Max(0.1, _speedGetter());
}
