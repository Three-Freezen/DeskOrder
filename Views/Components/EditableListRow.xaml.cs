using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DesktopZones.Helpers;

namespace DesktopZones.Views.Components;

/// <summary>
/// One row in an instance list (zone / panel / widget / sticky / clock / calendar).
/// 3-column grid: 28x28 icon | title + subtitle | hover ops (lock / eye / trash).
/// Double-click title → inline rename → fires RenameCommand with the new name.
///
/// ponytail 2026-08-25: drag-to-reorder (arm-on-move, mirrors the tab strips).
/// The drag arms as soon as the cursor moves >5 px from the press point — no
/// long-press delay. The row follows the cursor's grab point; crossing a
/// neighbour's midpoint with the row's LEADING edge (bottom edge dragging down,
/// top edge dragging up) fires ReorderRequested, and the page handlers move the
/// model collection + the live row collection. Release settles the row back
/// into its slot. Win32 GetCursorPos + GetAsyncKeyState keep the loop firing
/// and detect release even outside the row (same trick as PropertyTabStrip).
/// </summary>
public partial class EditableListRow : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(EditableListRow), new PropertyMetadata(""));
    public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(
        nameof(Subtitle), typeof(string), typeof(EditableListRow), new PropertyMetadata(""));
    public static readonly DependencyProperty IconKeyProperty = DependencyProperty.Register(
        nameof(IconKey), typeof(string), typeof(EditableListRow), new PropertyMetadata(""));
    public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.Register(
        nameof(IsSelected), typeof(bool), typeof(EditableListRow), new PropertyMetadata(false));
    public static readonly DependencyProperty IsLockedProperty = DependencyProperty.Register(
        nameof(IsLocked), typeof(bool), typeof(EditableListRow), new PropertyMetadata(false));
    public static readonly DependencyProperty IsVisibleProperty = DependencyProperty.Register(
        nameof(IsVisible), typeof(bool), typeof(EditableListRow), new PropertyMetadata(true));
    public static readonly DependencyProperty IconTextProperty = DependencyProperty.Register(
        nameof(IconText), typeof(string), typeof(EditableListRow), new PropertyMetadata(""));
    public static readonly DependencyProperty StatusBadgeProperty = DependencyProperty.Register(
        nameof(StatusBadge), typeof(string), typeof(EditableListRow), new PropertyMetadata(""));
    public static readonly DependencyProperty HasStatusBadgeProperty = DependencyProperty.Register(
        nameof(HasStatusBadge), typeof(bool), typeof(EditableListRow), new PropertyMetadata(false));
    public static readonly DependencyProperty StatusBadgeBrushProperty = DependencyProperty.Register(
        nameof(StatusBadgeBrush), typeof(Brush), typeof(EditableListRow));
    // ponytail: drag-source / drop-target DPs from the old indicator-style design
    // were removed in the live-shift rewrite — siblings slide via DragTranslate.Y now.

    public string Title    { get => (string)GetValue(TitleProperty);    set => SetValue(TitleProperty, value); }
    public string Subtitle { get => (string)GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }
    public string IconKey  { get => (string)GetValue(IconKeyProperty);  set => SetValue(IconKeyProperty, value); }
    public string IconText { get => (string)GetValue(IconTextProperty); set => SetValue(IconTextProperty, value); }
    public bool   IsSelected { get => (bool)GetValue(IsSelectedProperty); set => SetValue(IsSelectedProperty, value); }
    public bool   IsLocked   { get => (bool)GetValue(IsLockedProperty);   set => SetValue(IsLockedProperty, value); }
    public bool   IsVisible  { get => (bool)GetValue(IsVisibleProperty);  set => SetValue(IsVisibleProperty, value); }
    public string StatusBadge      { get => (string)GetValue(StatusBadgeProperty);      set => SetValue(StatusBadgeProperty, value); }
    public bool   HasStatusBadge   { get => (bool)GetValue(HasStatusBadgeProperty);   set => SetValue(HasStatusBadgeProperty, value); }
    public Brush? StatusBadgeBrush { get => (Brush?)GetValue(StatusBadgeBrushProperty); set => SetValue(StatusBadgeBrushProperty, value); }

    public ICommand? EditCommand       { get; set; }
    public ICommand? LockCommand       { get; set; }
    public ICommand? VisibilityCommand { get; set; }
    public ICommand? DeleteCommand     { get; set; }
    public ICommand? RenameCommand     { get; set; }

    /// <summary>Fires on drag reorder. <c>targetIndex</c> is the final insertion
    /// index in the source ItemsControl (use ObservableCollection.Move).</summary>
    public event Action<EditableListRow, int>? ReorderRequested;

    // ── Drag state (arm-on-move — mirrors the tab strips, no long-press) ──
    const int DragArmPx = 5;
    Point _mouseDownPos;

    public EditableListRow()
    {
        InitializeComponent();
        PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
        PreviewMouseLeftButtonUp += OnPreviewMouseLeftButtonUp;
    }

    void LockBtn_Click(object sender, RoutedEventArgs e) => LockCommand?.Execute(null);

    void EyeBtn_Click(object sender, RoutedEventArgs e)
    {
        // Flip visibility locally then notify; consumer decides what "hidden" means.
        IsVisible = !IsVisible;
        VisibilityCommand?.Execute(IsVisible);
    }

    void TrashBtn_Click(object sender, RoutedEventArgs e) => DeleteCommand?.Execute(null);

    void TitleText_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;

        EditCommand?.Execute(this);

        var tb = (TextBlock)sender;
        var parent = (StackPanel)tb.Parent;

        var box = new TextBox
        {
            Text = Title ?? "",
            FontFamily = tb.FontFamily,
            FontSize = tb.FontSize,
            FontWeight = tb.FontWeight,
            Foreground = (Brush)FindResource("Brush.Text.Primary"),
            Background = (Brush)FindResource("Brush.Bg.Input"),
            BorderBrush = (Brush)FindResource("Brush.Accent"),
            BorderThickness = new Thickness(1),
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(4, 1, 4, 1),
            MinWidth = Math.Max(tb.ActualWidth + 8, 120),
        };

        parent.Children.Remove(tb);
        parent.Children.Insert(0, box);
        box.SelectAll();
        box.Focus();

        bool committing = false;
        void Commit()
        {
            if (committing) return;
            committing = true;
            var raw = box.Text?.Trim() ?? "";
            // ponytail 2026-08-28: 同名自动加数字的功能已移除 — 用户自己改重名即可,
            // 这里不再做任何冲突解析;重命名成原名字则视为未修改。
            if (!string.IsNullOrEmpty(raw) && raw != Title && RenameCommand != null)
                RenameCommand.Execute(raw);
            parent.Children.Remove(box);
            parent.Children.Insert(0, tb);
            tb.Text = Title;
        }

        box.KeyDown += (_, ke) =>
        {
            if (ke.Key == Key.Enter)
            {
                Commit();
                ke.Handled = true;
            }
            else if (ke.Key == Key.Escape)
            {
                committing = true;
                parent.Children.Remove(box);
                parent.Children.Insert(0, tb);
                ke.Handled = true;
            }
        };
        box.LostFocus += (_, _) => Commit();
    }

    // ── Drag-to-reorder (live shift, arm-on-move — mirrors PropertyTabStrip) ──
    //
    // ponytail: the drag arms as soon as the cursor moves >5 px from the press
    // point (same Euclidean arm rule as the tab strips — no 350 ms long-press).
    //   • Source row's DragTranslate.Y tracks the cursor's grab point.
    //   • The row's LEADING edge (bottom edge dragging down, top edge dragging up)
    //     crossing a neighbour's midpoint fires ReorderRequested, which the page
    //     handles by moving the model collection + the live row OC — 拖过一半即换位.
    //   • Sibling rows that shifted are captured BEFORE the Move (their visual Y
    //     at that moment) and animated back to 0 AFTER it — the "腾出位置" slide.
    //   • On release the source row settles back into its slot; a release without
    //     arming is a plain click and flows through to the page's select handler.
    //
    // Win32 GetCursorPos keeps the loop firing when the cursor crosses into the
    // management window's chrome, and GetAsyncKeyState detects the release even
    // outside the row — same tricks the tab strip uses.

    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X; public int Y; }
    const int VK_LBUTTON = 0x01;

    DispatcherTimer? _dragTimer;
    bool _dragArmed;
    bool _dragConsumed;            // a drag just ended — suppress the next click-select
    Point _dragStartScreen;
    double _dragLastCursorY = double.NaN; // previous cursor Y — drives the leading-edge probe
    int _dragFromIndex = -1;
    int _currentIndex = -1;
    double _dragGrabOffsetY;       // cursor's Y offset within the source row at drag start
    readonly Dictionary<EditableListRow, double> _rowPositionsBeforeMove = new();

    void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        // Skip ops-button presses (lock/eye/trash) — those own their own click semantics.
        if (IsClickOnButton(e.OriginalSource as DependencyObject)) return;

        var ic = FindItemsControl();
        if (ic == null) return;
        _dragFromIndex = ic.Items.IndexOf(this);
        if (_dragFromIndex < 0) return;
        _currentIndex = _dragFromIndex;
        _dragArmed = false;
        _dragConsumed = false;

        _mouseDownPos = e.GetPosition(this);
        _dragStartScreen = PointToScreen(_mouseDownPos);
        _dragLastCursorY = _dragStartScreen.Y;

        // _dragGrabOffsetY is the cursor's Y offset within the source row at press
        // time — used in OnDragTick to compute DragTranslate.Y so the grab point
        // stays under the cursor even after the row's natural Y shifts due to
        // live reorders.
        var cursorInIc = ic.PointFromScreen(_dragStartScreen);
        var sourceTopInIc = TransformToAncestor(ic).Transform(new Point(0, 0));
        _dragGrabOffsetY = cursorInIc.Y - sourceTopInIc.Y;
        if (DragTranslate != null)
        {
            DragTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            DragTranslate.Y = 0;
        }
        // Bring the source row above its siblings for the duration of the gesture.
        Panel.SetZIndex(this, 10);

        StartDragTimer();
    }

    void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // The tick loop also detects release via Win32 (release outside the row),
        // so EndDrag may already have run — _dragArmed/_dragConsumed cover both.
        if (_dragArmed)
        {
            EndDrag();
            _dragConsumed = true;
        }
        if (_dragConsumed)
        {
            _dragConsumed = false;
            // Suppress the page's click-select handler — the drag consumed this gesture.
            e.Handled = true;
            return;
        }
        // Plain click — tear the scaffolding down and let the click flow through.
        CancelDragScaffolding();
    }

    /// <summary>Release the timer + z-index without the settle animation
    /// (used when the gesture ended as a plain click, not a drag).</summary>
    void CancelDragScaffolding()
    {
        StopDragTimer();
        Panel.SetZIndex(this, 0);
        _dragArmed = false;
        _dragFromIndex = -1;
        _currentIndex = -1;
        _dragLastCursorY = double.NaN;
    }

    void StartDragTimer()
    {
        if (_dragTimer != null) return;
        _dragTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _dragTimer.Tick += OnDragTick;
        _dragTimer.Start();
    }

    void StopDragTimer()
    {
        if (_dragTimer == null) return;
        _dragTimer.Stop();
        _dragTimer.Tick -= OnDragTick;
        _dragTimer = null;
    }

    void OnDragTick(object? sender, EventArgs e)
    {
        if (_dragTimer == null) return;

        GetCursorPos(out var pt);
        var screen = new Point(pt.X, pt.Y);

        // LButton release via Win32 — fires even when the button is released
        // outside the row/window (same trick as the tab strips). An armed drag
        // settles; a plain click just tears the scaffolding down and flows through.
        if ((GetAsyncKeyState(VK_LBUTTON) & 0x8000) == 0)
        {
            bool wasArmed = _dragArmed;
            if (wasArmed) EndDrag(); else CancelDragScaffolding();
            _dragConsumed = wasArmed;
            return;
        }

        // Arm once the cursor moves >5 px from the press point (Euclidean) — same
        // arm rule as the tab strips, no long-press delay.
        if (!_dragArmed)
        {
            double dx = screen.X - _dragStartScreen.X;
            double dy = screen.Y - _dragStartScreen.Y;
            if (dx * dx + dy * dy > DragArmPx * DragArmPx) _dragArmed = true;
            else return;
        }

        bool movingDown = screen.Y >= _dragLastCursorY;
        _dragLastCursorY = screen.Y;

        var ic = FindItemsControl();
        if (ic == null) return;
        var cursorInIc = ic.PointFromScreen(screen);

        // 1. Source follows the cursor (grab point stays under cursor).
        if (DragTranslate != null)
        {
            var sourceTopInIc = TransformToAncestor(ic).Transform(new Point(0, 0));
            DragTranslate.Y = cursorInIc.Y - sourceTopInIc.Y - _dragGrabOffsetY;
        }

        // 2. Leading-edge probe, direction-aware: the swap fires once the row's
        // leading edge (bottom edge dragging down, top edge dragging up) crosses
        // a neighbour's midpoint — 拖过一半即换位, symmetric in both directions
        // (probing the fixed top edge made downward drags fire a whole row late).
        // ComputeDropIndex returns the slot whose center is BELOW the probe.
        // _currentIndex is the slot where source currently lives in the collection.
        double probeY = cursorInIc.Y - _dragGrabOffsetY; // top edge
        if (movingDown) probeY += ActualHeight;          // bottom edge

        int hoverSlot = ComputeDropIndex(ic, probeY);
        if (hoverSlot == _currentIndex || hoverSlot == _currentIndex + 1) return;

        // Translate slot to collection index. List[i] is at slot i; "slot k" means
        // "the slot between row k-1 and row k" — collection index for source is k-1
        // when k > _currentIndex, k when k < _currentIndex.
        int targetCollectionIdx = hoverSlot > _currentIndex ? hoverSlot - 1 : hoverSlot;
        if (targetCollectionIdx == _currentIndex) return;

        // 3. Capture sibling visual positions BEFORE the Move (so the slide anim
        // back to 0 from the same Y they were at before the swap).
        CaptureRowPositions(ic);

        // 4. Fire the reorder — page handler calls manager.MoveZone / MoveClock / etc.
        ReorderRequested?.Invoke(this, targetCollectionIdx);
        _currentIndex = targetCollectionIdx;

        // 5. After layout updates with the new collection order, animate each shifted
        // sibling from its old visual Y back to 0 (160ms ease-out, matches tab strip).
        Dispatcher.BeginInvoke(new Action(PlayShiftAnimations), DispatcherPriority.Loaded);
    }

    /// <summary>Slot index whose center is just below the cursor. Returns Count when the
    /// cursor is below the last row's center.</summary>
    int ComputeDropIndex(ItemsControl ic, double yInIc)
    {
        for (int i = 0; i < ic.Items.Count; i++)
        {
            if (ic.Items[i] is EditableListRow row)
            {
                var p = ((FrameworkElement)row).TransformToAncestor(ic).Transform(new Point(0, 0));
                double center = p.Y + row.ActualHeight / 2.0;
                if (yInIc < center) return i;
            }
        }
        return ic.Items.Count;
    }

    void CaptureRowPositions(ItemsControl ic)
    {
        _rowPositionsBeforeMove.Clear();
        for (int i = 0; i < ic.Items.Count; i++)
        {
            if (ic.Items[i] is EditableListRow row && row != this)
            {
                var p = ((FrameworkElement)row).TransformToAncestor(ic).Transform(new Point(0, 0));
                _rowPositionsBeforeMove[row] = p.Y;
            }
        }
    }

    void PlayShiftAnimations()
    {
        var ic = FindItemsControl();
        if (ic == null) { _rowPositionsBeforeMove.Clear(); return; }
        foreach (var kv in _rowPositionsBeforeMove)
        {
            var row = kv.Key;
            if (row?.DragTranslate == null) continue;
            // New visual Y AFTER the Move (collection order changed).
            var newY = ((FrameworkElement)row).TransformToAncestor(ic).Transform(new Point(0, 0)).Y;
            var delta = kv.Value - newY; // shift = where the row appeared before - where it sits now
            if (Math.Abs(delta) < 0.5) continue;
            // Start the row at its old visual position (delta offset from new natural),
            // animate the translate back to 0 so it slides into the new slot.
            var anim = new DoubleAnimation(delta, 0, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            row.DragTranslate.BeginAnimation(TranslateTransform.YProperty, anim);
        }
        _rowPositionsBeforeMove.Clear();
    }

    void EndDrag()
    {
        StopDragTimer();

        // Settle the source row from wherever the cursor left it back into its slot.
        if (DragTranslate != null)
        {
            var anim = new DoubleAnimation(DragTranslate.Y, 0, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            DragTranslate.BeginAnimation(TranslateTransform.YProperty, anim);
        }
        // Also settle any leftover shift on siblings (defensive — PlayShiftAnimations
        // already clears most, but a fast release before Loaded fires can leave one).
        var ic = FindItemsControl();
        if (ic != null)
        {
            foreach (var item in ic.Items)
            {
                if (item is EditableListRow row && row != this && row.DragTranslate != null &&
                    Math.Abs(row.DragTranslate.Y) > 0.5)
                {
                    var anim = new DoubleAnimation(row.DragTranslate.Y, 0, TimeSpan.FromMilliseconds(160))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                    };
                    row.DragTranslate.BeginAnimation(TranslateTransform.YProperty, anim);
                }
            }
        }
        Panel.SetZIndex(this, 0);
        _dragArmed = false;
        _dragFromIndex = -1;
        _currentIndex = -1;
        _dragLastCursorY = double.NaN;
    }

    ItemsControl? FindItemsControl()
    {
        DependencyObject? d = this;
        while (d != null)
        {
            if (d is ItemsControl ic) return ic;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    static bool IsClickOnButton(DependencyObject? d)
    {
        while (d != null && d is not UserControl)
        {
            if (d is ButtonBase) return true;
            d = VisualTreeHelper.GetParent(d);
        }
        return false;
    }
}
