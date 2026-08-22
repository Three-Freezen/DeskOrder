using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DesktopZones.Helpers;

namespace DesktopZones.Views.Components;

/// <summary>
/// One row in an instance list (zone / panel / widget / sticky / clock / calendar).
/// 3-column grid: 28x28 icon | title + subtitle | hover ops (lock / eye / trash).
/// Double-click title → inline rename → fires RenameCommand with the new name.
/// ponytail: ops-button hover uses Root.IsMouseOver via Style trigger, not event handlers.
/// Selected highlight also via Style trigger; order in trigger list = precedence (selected wins over hover).
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

    public EditableListRow()
    {
        InitializeComponent();
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
            // Resolve collisions: don't collide with current Title; if user left it unchanged, skip.
            if (!string.IsNullOrEmpty(raw))
            {
                var final = NameCollisionResolver.ResolveName(raw, new[] { Title ?? "" });
                if (RenameCommand != null && final != Title)
                    RenameCommand.Execute(final);
            }
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
}
