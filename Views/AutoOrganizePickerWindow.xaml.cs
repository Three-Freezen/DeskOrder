using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DesktopZones.Helpers;
using DesktopZones.Models;
using DesktopZones.Services;
using DesktopZones.Views.Components;

namespace DesktopZones.Views;

/// <summary>选择器类型：扩展名（含预设）或文件名要素（全自定义）。</summary>
public enum AutoOrganizePickerKind
{
    Extension,
    Token,
}

/// <summary>自动整理的 chip 选择器二级窗口。构造时传入 Zone + 类型，
/// OK 时把选择写回 Zone 对应集合；Cancel 还原快照。</summary>
public partial class AutoOrganizePickerWindow : Window
{
    readonly Zone _zone;
    readonly AutoOrganizePickerKind _kind;
    readonly List<string> _snapshot;
    readonly LocalizationService _loc = LocalizationService.Instance;

    List<string> Target => _kind == AutoOrganizePickerKind.Extension
        ? _zone.AutoOrganizeExtensions
        : _zone.AutoOrganizeNameTokens;

    public AutoOrganizePickerWindow(Zone z, AutoOrganizePickerKind kind)
    {
        InitializeComponent();
        _zone = z;
        _kind = kind;
        _snapshot = new List<string>(Target);
        ApplyLoc();
        RenderChips();
    }

    void ApplyLoc()
    {
        bool isExt = _kind == AutoOrganizePickerKind.Extension;
        TitleText.Text = isExt
            ? _loc["ZoneProp.AutoOrganize.Picker.Title"]
            : _loc["ZoneProp.AutoOrganize.Picker.TokenTitle"];
        PresetGroupLabel.Text = _loc["ZoneProp.AutoOrganize.Picker.PresetGroup"];
        CustomGroupLabel.Text = _loc["ZoneProp.AutoOrganize.Picker.CustomGroup"];
        AddBtn.ToolTip = isExt
            ? _loc["ZoneProp.AutoOrganize.Picker.AddCustom"]
            : _loc["ZoneProp.AutoOrganize.Picker.AddToken"];
        OkBtn.Content = _loc["ZoneProp.AutoOrganize.Picker.Confirm"];
        CancelBtn.Content = _loc["ZoneProp.AutoOrganize.Picker.Cancel"];
    }

    void RenderChips()
    {
        PresetChips.Children.Clear();
        CustomChips.Children.Clear();

        bool isExt = _kind == AutoOrganizePickerKind.Extension;
        if (isExt)
        {
            PresetGroupLabel.Visibility = Visibility.Visible;
            PresetChips.Visibility = Visibility.Visible;
            foreach (var ext in AutoOrganizePresets.Extensions)
                PresetChips.Children.Add(MakeChip(ext, isPreset: true));
        }
        else
        {
            PresetGroupLabel.Visibility = Visibility.Collapsed;
            PresetChips.Visibility = Visibility.Collapsed;
        }

        var customs = Target
            .Where(e => !(isExt && AutoOrganizePresets.IsPreset(e)))
            .OrderBy(e => e, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var text in customs)
            CustomChips.Children.Add(MakeChip(text, isPreset: false));
    }

    Border MakeChip(string text, bool isPreset)
    {
        bool selected = Target.Contains(text, StringComparer.OrdinalIgnoreCase);
        var bg = selected
            ? (SolidColorBrush)FindResource("Brush.Accent.Solid")
            : (SolidColorBrush)FindResource("Brush.Bg.Hover");
        // 自适应文字：按 chip 背景的黑白对比度选前景色（跟随主题/强调色变化）。
        var fg = AdaptiveTextColor.ResolveBrush(bg.Color);

        var chip = new Border
        {
            CornerRadius = new CornerRadius(14),
            MinWidth = 48,
            MaxWidth = 120,
            Height = 28,
            Margin = new Thickness(0, 0, 6, 6),
            Padding = new Thickness(10, 0, 10, 0),
            Background = bg,
            BorderBrush = selected
                ? (Brush)FindResource("Brush.Accent.Solid")
                : (Brush)FindResource("Brush.Border.Subtle"),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Tag = text,
        };

        var stack = new StackPanel { Orientation = Orientation.Horizontal };
        if (selected)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "✓",
                FontSize = 11,
                Foreground = fg,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0),
            });
        }
        stack.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 11,
            Foreground = fg,
            VerticalAlignment = VerticalAlignment.Center,
        });
        chip.Child = stack;
        chip.MouseLeftButtonDown += (_, _) => ToggleChip(chip, text);

        if (!isPreset)
        {
            var menu = new ContextMenu();
            var del = new MenuItem { Header = _loc["ZoneProp.AutoOrganize.Picker.Delete"] };
            del.Click += (_, _) => DeleteCustom(text);
            menu.Items.Add(del);
            chip.ContextMenu = menu;
        }
        return chip;
    }

    void ToggleChip(Border chip, string text)
    {
        PlayBounce(chip);
        bool has = Target.Contains(text, StringComparer.OrdinalIgnoreCase);
        if (has)
            Target.RemoveAll(e => string.Equals(e, text, StringComparison.OrdinalIgnoreCase));
        else
            Target.Add(_kind == AutoOrganizePickerKind.Extension ? text.ToLowerInvariant() : text);
        RenderChips();
    }

    void DeleteCustom(string text)
    {
        var ok = MessageBox.Show(
            string.Format(System.Globalization.CultureInfo.InvariantCulture,
                _loc["ZoneProp.AutoOrganize.Picker.DeleteConfirm"], text),
            _loc["ZoneProp.AutoOrganize.Picker.Delete"],
            MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (ok != MessageBoxResult.OK) return;
        Target.RemoveAll(e => string.Equals(e, text, StringComparison.OrdinalIgnoreCase));
        RenderChips();
    }

    void Add_Click(object sender, RoutedEventArgs e)
    {
        bool isExt = _kind == AutoOrganizePickerKind.Extension;
        var tokenInvalid = _loc["AutoOrganizePicker.TokenLength"];
        var input = new ChipInputPopup(
            isExt ? _loc["ZoneProp.AutoOrganize.Picker.AddCustom"] : _loc["ZoneProp.AutoOrganize.Picker.AddToken"],
            isExt ? _loc["ZoneProp.AutoOrganize.Picker.CustomPlaceholder"] : _loc["ZoneProp.AutoOrganize.NameLabel"],
            isExt ? _loc["ZoneProp.AutoOrganize.Picker.Invalid"] : tokenInvalid,
            _loc["ZoneProp.AutoOrganize.Picker.Duplicate"],
            Target,
            isExt ? ChipInputKind.Extension : ChipInputKind.Token)
        {
            Owner = this,
        };
        if (input.ShowDialog() == true)
        {
            var v = isExt ? input.Value.ToLowerInvariant() : input.Value;
            if (!string.IsNullOrWhiteSpace(v))
                Target.Add(v);
            RenderChips();
        }
    }

    void PlayBounce(FrameworkElement el)
    {
        if (el.RenderTransform is not ScaleTransform st)
        {
            st = new ScaleTransform(1, 1);
            el.RenderTransform = st;
            el.RenderTransformOrigin = new Point(0.5, 0.5);
        }
        st.ScaleX = 1.0;
        st.ScaleY = 1.0;
        var anim = new DoubleAnimation(1.0, 1.1, TimeSpan.FromMilliseconds(100))
        {
            AutoReverse = true,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    void CloseDialog(bool ok)
    {
        if (!ok) RestoreSnapshot();
        DialogResult = ok;
    }

    void RestoreSnapshot()
    {
        Target.Clear();
        Target.AddRange(_snapshot);
    }

    void TitleBar_Down(object sender, MouseButtonEventArgs e)
    {
        try { DragMove(); } catch { }
    }

    void Ok_Click(object sender, RoutedEventArgs e) => CloseDialog(true);
    void Cancel_Click(object sender, RoutedEventArgs e) => CloseDialog(false);
}
