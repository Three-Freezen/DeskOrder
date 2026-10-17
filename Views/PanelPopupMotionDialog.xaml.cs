using System;
using System.Windows;
using System.Windows.Controls;
using DesktopZones.Models;
using DesktopZones.Services;

namespace DesktopZones.Views;

/// <summary>
/// 面板弹出动效二级窗口。与其他窗口的 MotionSettingsDialog 同款液态玻璃弹窗,
/// 但展开原点是「桌面的四角之一」(左上/右上/左下/右下),不是按钮中心/按钮边角。
/// 读 PanelConfig.PanelPopupOrigin / PanelPopupMotion / PanelPopupSpeed,
/// 仅在保存时回写。
/// </summary>
public partial class PanelPopupMotionDialog : Window
{
    private readonly LocalizationService _loc = LocalizationService.Instance;
    private readonly PanelPopupOrigin _initialOrigin;
    private readonly HoverExpandAnimationKind _initialAnimation;

    public PanelPopupOrigin ResultOrigin { get; private set; }
    public HoverExpandAnimationKind ResultAnimation { get; private set; }
    public double ResultSpeed { get; private set; }

    public PanelPopupMotionDialog(PanelPopupOrigin origin, HoverExpandAnimationKind animation, double speed)
    {
        _initialOrigin = origin;
        _initialAnimation = animation;
        InitializeComponent();
        ApplyLoc();

        SelectOrigin(origin);
        SelectAnimation(animation);
        SpeedSlider.Value = Math.Clamp(speed, SpeedSlider.Minimum, SpeedSlider.Maximum);
        // ponytail: 滑块右侧数字去掉多余小数 —— 整数显示整数(1x / 2x), 非整数才带小数(0.25x / 1.5x)。
        SpeedValueText.Text = SpeedText(SpeedSlider.Value);

        SpeedSlider.ValueChanged += (_, _) =>
            SpeedValueText.Text = SpeedText(SpeedSlider.Value);
    }

    /// <summary>动效速度显示:整数不带小数点, 其余按最简小数(0.25 / 1.25 / 1.5)。</summary>
    static string SpeedText(double v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "x";

    void ApplyLoc()
    {
        Title = _loc["Motion.Title"];
        TitleLabel.Text = _loc["Motion.Title"];
        OriginLabel.Text = _loc["PanelPopupMotion.Origin"];
        OriginHint.Text = _loc["PanelPopupMotion.OriginHint"];
        AnimationKindLabel.Text = _loc["Motion.AnimationKind"];
        AnimationKindHint.Text = _loc["Motion.AnimationKindHint"];
        SpeedLabel.Text = _loc["Motion.Speed"];
        SpeedHint.Text = _loc["Motion.SpeedHint"];
        ItemOriginTopLeft.Content = _loc["PanelPopupMotionOriginTopLeft"];
        ItemOriginTopRight.Content = _loc["PanelPopupMotionOriginTopRight"];
        ItemOriginBottomLeft.Content = _loc["PanelPopupMotionOriginBottomLeft"];
        ItemOriginBottomRight.Content = _loc["PanelPopupMotionOriginBottomRight"];
        ItemScaleExpand.Content = _loc["MotionAnimationScaleExpand"];
        ItemFade.Content = _loc["MotionAnimationFade"];
        ItemBounceExpand.Content = _loc["MotionAnimationBounceExpand"];
        ItemDirectionalExpand.Content = _loc["MotionAnimationDirectionalExpand"];
        ItemVerticalExpand.Content = _loc["MotionAnimationVerticalExpand"];
        ItemNone.Content = _loc["MotionAnimationNone"];
        CancelBtn.Content = _loc["Common.Cancel"];
        OkBtn.Content = _loc["Common.Save"];
    }

    void SelectOrigin(PanelPopupOrigin kind)
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

    PanelPopupOrigin ReadOrigin()
    {
        if (OriginCombo.SelectedItem is ComboBoxItem ci && ci.Tag is string s &&
            Enum.TryParse<PanelPopupOrigin>(s, out var o)) return o;
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
        // 未知枚举(例如持久化 JSON 的前向兼容值)——保持未选中,直到用户真正选择。
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
        if (OriginCombo.SelectedIndex == -1 || AnimationCombo.SelectedIndex == -1)
        {
            MessageBox.Show(this, _loc["Motion.UnknownAnimation"], _loc["Motion.Title"],
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        ResultOrigin = ReadOrigin();
        ResultAnimation = ReadAnimation();
        ResultSpeed = SpeedSlider.Value;
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
