using System;
using System.Windows;
using System.Windows.Controls;
using DesktopZones.Models;
using DesktopZones.Services;

namespace DesktopZones.Views;

/// <summary>
/// ponytail: Small popup for tuning the hover/click restore animation on a
/// single instance. Reads from AppearanceModel.HoverExpandAnimation /
/// HoverExpandSpeed / HoverExpandOrigin on open, writes back only on OK.
/// Cancel discards edits.
/// HoverAutoExpand (the on/off for cursor-hover auto-expand) is a PropertyPanel
/// checkbox, not a dialog knob — it's boolean and a single click toggles it.
/// </summary>
public partial class MotionSettingsDialog : Window
{
    private readonly LocalizationService _loc = LocalizationService.Instance;
    private readonly HoverExpandAnimationKind _initialAnimation;
    private readonly HoverExpandOrigin _initialOrigin;

    public HoverExpandAnimationKind ResultHoverExpandAnimation { get; private set; }
    public HoverExpandOrigin ResultHoverExpandOrigin { get; private set; }
    public double ResultHoverExpandSpeed { get; private set; }

    public MotionSettingsDialog(HoverExpandAnimationKind animation, HoverExpandOrigin origin, double speed)
    {
        _initialAnimation = animation;
        _initialOrigin = origin;
        InitializeComponent();
        ApplyLoc();

        SelectOrigin(origin);
        SelectAnimation(animation);
        SpeedSlider.Value = Math.Clamp(speed, SpeedSlider.Minimum, SpeedSlider.Maximum);
        SpeedValueText.Text = $"{SpeedSlider.Value:F2}x";

        SpeedSlider.ValueChanged += (_, _) =>
            SpeedValueText.Text = $"{SpeedSlider.Value:F2}x";
    }

    void ApplyLoc()
    {
        Title = _loc["Motion.Title"];
        TitleLabel.Text = _loc["Motion.Title"];
        OriginLabel.Text = _loc["Motion.Origin"];
        OriginHint.Text = _loc["Motion.OriginHint"];
        AnimationKindLabel.Text = _loc["Motion.AnimationKind"];
        AnimationKindHint.Text = _loc["Motion.AnimationKindHint"];
        SpeedLabel.Text = _loc["Motion.Speed"];
        SpeedHint.Text = _loc["Motion.SpeedHint"];
        ItemOriginCenter.Content = _loc["MotionOriginButtonCenter"];
        ItemOriginCorner.Content = _loc["MotionOriginButtonCorner"];
        ItemScaleExpand.Content = _loc["MotionAnimationScaleExpand"];
        ItemFade.Content = _loc["MotionAnimationFade"];
        ItemBounceExpand.Content = _loc["MotionAnimationBounceExpand"];
        ItemDirectionalExpand.Content = _loc["MotionAnimationDirectionalExpand"];
        ItemVerticalExpand.Content = _loc["MotionAnimationVerticalExpand"];
        ItemNone.Content = _loc["MotionAnimationNone"];
        CancelBtn.Content = _loc["Common.Cancel"];
        OkBtn.Content = _loc["Common.Save"];
    }

    void SelectOrigin(HoverExpandOrigin kind)
    {
        var tag = kind.ToString();
        foreach (var item in OriginCombo.Items)
        {
            if (item is ComboBoxItem ci && (ci.Tag as string) == tag)
            {
                OriginCombo.SelectedItem = ci;
                return;
            }
        }
        OriginCombo.SelectedIndex = -1;
    }

    HoverExpandOrigin ReadOrigin()
    {
        if (OriginCombo.SelectedItem is ComboBoxItem ci && ci.Tag is string s &&
            Enum.TryParse<HoverExpandOrigin>(s, out var o)) return o;
        return _initialOrigin;
    }

    void SelectAnimation(HoverExpandAnimationKind kind)
    {
        var tag = kind.ToString();
        foreach (var item in AnimationCombo.Items)
        {
            if (item is ComboBoxItem ci && (ci.Tag as string) == tag)
            {
                AnimationCombo.SelectedItem = ci;
                return;
            }
        }
        // Unknown enum (e.g. forward-compat value from persisted JSON) — leave nothing
        // selected so the user must pick. ReadAnimation() then preserves the original
        // value until they make a real selection.
        AnimationCombo.SelectedIndex = -1;
    }

    HoverExpandAnimationKind ReadAnimation()
    {
        if (AnimationCombo.SelectedItem is ComboBoxItem ci && ci.Tag is string s &&
            Enum.TryParse<HoverExpandAnimationKind>(s, out var k)) return k;
        return _initialAnimation;
    }

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (AnimationCombo.SelectedIndex == -1 || OriginCombo.SelectedIndex == -1)
        {
            MessageBox.Show(this, _loc["Motion.UnknownAnimation"], _loc["Motion.Title"],
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        ResultHoverExpandOrigin = ReadOrigin();
        ResultHoverExpandAnimation = ReadAnimation();
        ResultHoverExpandSpeed = SpeedSlider.Value;
        DialogResult = true;
        Close();
    }

    void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    void TitleBar_Down(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        try { DragMove(); } catch { }
    }
}