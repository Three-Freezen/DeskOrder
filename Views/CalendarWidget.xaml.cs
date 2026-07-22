using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using DesktopZones.Helpers;
using DesktopZones.Models;
using DesktopZones.Services;
using DesktopZones.ViewModels;

namespace DesktopZones.Views;

public partial class CalendarWidget : Window
{
    const uint WM_NCLBUTTONDOWN = 0x00A1;
    const int HTTOPLEFT = 13, HTTOPRIGHT = 14, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
    private bool _restoreDragging;
    private Point _restoreDown;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    private DesktopCalendar _calendar;
    private readonly WidgetService _widgetService;
    private readonly CalendarViewModel _vm;
    private readonly LocalizationService _loc = LocalizationService.Instance;

    public CalendarWidget(DesktopCalendar calendar, WidgetService widgetService)
    {
        InitializeComponent();
        _calendar = calendar;
        _widgetService = widgetService;
        _vm = new CalendarViewModel(calendar);
        DataContext = _vm;

        Left = calendar.X; Top = calendar.Y;
        Opacity = calendar.Opacity;
        MinWidth = 260; MinHeight = 340;

        _vm.DisplayYear = DateTime.Now.Year;
        _vm.DisplayMonth = DateTime.Now.Month;
        RebuildDisplay();

        ApplyStyle();

        Loaded += OnLoad;
        SizeChanged += OnSizeChanged;
        LocationChanged += (_, _) => { _calendar.X = Left; _calendar.Y = Top; };
        Activated += (_, _) => { Topmost = true; };
        _langChanged = _ => ApplyLoc();
        _loc.LanguageChanged += _langChanged;
        _widgetService.CalendarsChanged += OnCalendarsChanged;
        ApplyLoc();
    }
    private Action<Services.Language>? _langChanged;

    void OnCalendarsChanged()
    {
        if (!IsLoaded) return;
        var latest = _widgetService.Calendars.FirstOrDefault(c => c.Id == _calendar.Id);
        if (latest != null) _calendar = latest;
        if (MainContent.Visibility == Visibility.Visible)
            ApplyAcrylic();
        ApplyBackgroundImage();
        ApplyStyle();
    }

    void OnLoad(object s, RoutedEventArgs e)
    {
        NativeMethods.PinToDesktop(this);
        NativeMethods.SetToolWindow(this);
        ApplyAcrylic();
        ApplyBackgroundImage();
        // Set rounded corners LAST after all sizing is complete
        NativeMethods.SetRoundedCorners(this, 10);
        NativeMethods.UpdateRoundedCorners(this, 10);
        if (!_calendar.IsVisible) ApplyHidden();
    }

    void OnSizeChanged(object s, SizeChangedEventArgs e)
    {
        if (MainContent.Visibility != Visibility.Visible) return;
        NativeMethods.UpdateRoundedCorners(this, 10);
    }

    // ── Acrylic / frosted glass ──

    void ApplyAcrylic()
    {
        if (_calendar.EnableAcrylic)
        {
            AcrylicHelper.EnableBlur(this, _calendar.GlassBlurAmount, _calendar.GlassTintOpacity,
                _calendar.GlassTintLuminosity, _calendar.GlassColorMode);
            try
            {
                var fillColor = (Color)ColorConverter.ConvertFromString(_calendar.FillColor)!;
                byte bgAlpha = (byte)(_calendar.GlassBlurAmount > 0 ? 0x06 : 0x0F);
                CalendarBorder.Background = new SolidColorBrush(Color.FromArgb(bgAlpha, fillColor.R, fillColor.G, fillColor.B));
            }
            catch
            {
                CalendarBorder.Background = new SolidColorBrush(Color.FromArgb(0x06, 0x15, 0x15, 0x30));
            }
            // Liquid Glass: chromatic dispersion border
            if (_calendar.EnableLiquidGlass)
            {
                CalendarBorder.BorderBrush = AcrylicHelper.CreateChromaticBorder();
                CalendarBorder.BorderThickness = new Thickness(Math.Max(1.0, _calendar.BorderThickness));
            }
        }
        else
        {
            AcrylicHelper.DisableBlur(this);
            try
            {
                CalendarBorder.Background = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString(_calendar.FillColor)!);
            }
            catch { }
        }
    }

    // ── Background image ──

    void ApplyBackgroundImage()
    {
        try
        {
            if (!string.IsNullOrEmpty(_calendar.BackgroundImagePath) && System.IO.File.Exists(_calendar.BackgroundImagePath))
            {
                var bi = new System.Windows.Media.Imaging.BitmapImage();
                bi.BeginInit();
                bi.UriSource = new Uri(_calendar.BackgroundImagePath);
                bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bi.EndInit();
                CalBgImage.Source = bi;
                CalBgImage.Stretch = Stretch.UniformToFill;
                double cw = CalBgBorder.ActualWidth > 0 ? CalBgBorder.ActualWidth : 300;
                double ch = CalBgBorder.ActualHeight > 0 ? CalBgBorder.ActualHeight : 420;

                // UniformToFill — fill target area maintaining aspect ratio
                double imgW = bi.PixelWidth;
                double imgH = bi.PixelHeight;
                double utfScale = Math.Max((cw * _calendar.BgImageZoom) / imgW, (ch * _calendar.BgImageZoom) / imgH);
                double displayedW = imgW * utfScale;
                double displayedH = imgH * utfScale;

                CalBgImage.Width = displayedW;
                CalBgImage.Height = displayedH;

                // Position image: center at container center + offset (matches preview positioning)
                double zoneCenterX = cw / 2;
                double zoneCenterY = ch / 2;
                double imgCenterX = displayedW / 2;
                double imgCenterY = displayedH / 2;
                double ox = _calendar.BgImageOffsetX;
                double oy = _calendar.BgImageOffsetY;

                CalBgImage.Margin = new Thickness(
                    zoneCenterX - imgCenterX + ox,
                    zoneCenterY - imgCenterY + oy, 0, 0);
                CalBgImage.HorizontalAlignment = HorizontalAlignment.Left;
                CalBgImage.VerticalAlignment = VerticalAlignment.Top;
                CalBgImage.Opacity = Math.Max(0.01, _calendar.BackgroundImageOpacity / 100.0);
            }
            else
            {
                CalBgImage.Source = null;
                CalBgImage.Opacity = 0;
            }
        }
        catch { if (CalBgImage != null) { CalBgImage.Source = null; CalBgImage.Opacity = 0; } }
    }

    // ── Style (border / fill) ──

    void ApplyStyle()
    {
        // Liquid Glass: border handled by ApplyAcrylic (chromatic dispersion)
        if (_calendar.EnableAcrylic && _calendar.EnableLiquidGlass)
            return;

        try
        {
            CalendarBorder.BorderBrush = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(_calendar.BorderColor)!);
        }
        catch { }
        CalendarBorder.BorderThickness = new Thickness(_calendar.BorderThickness);
    }

    void ApplyLoc()
    {
        var cn = _loc.CurrentLanguage == Services.Language.Chinese;
        TodayBtn.Content = _loc["Calendar.Today"];
        AddNoteBtn.ToolTip = _loc["Calendar.AddNote"];
        NotesDateLabel.Text = cn ? "备注" : "Notes";
        CtxDelete.Header = _loc["Calendar.Delete"];
        Dow0.Text = cn ? "一" : "Mo"; Dow1.Text = cn ? "二" : "Tu"; Dow2.Text = cn ? "三" : "We"; Dow3.Text = cn ? "四" : "Th";
        Dow4.Text = cn ? "五" : "Fr"; Dow5.Text = cn ? "六" : "Sa";
        Dow6.Text = cn ? "日" : "Su";
        MonthTitleText.Text = cn ? $"{_vm.DisplayYear}年{_vm.DisplayMonth}月" : $"{_vm.DisplayMonth}/{_vm.DisplayYear}";
    }

    void ToggleRestore_Click(object s, RoutedEventArgs e)
    {
        _calendar.EnableRestoreButton = !_calendar.EnableRestoreButton;
        var cn = _loc.CurrentLanguage == DesktopZones.Services.Language.Chinese;
        if (s is MenuItem mi)
            mi.Header = _calendar.EnableRestoreButton
                ? (cn ? "关闭恢复按钮" : "Disable Restore")
                : (cn ? "启用恢复按钮" : "Enable Restore");
    }

    void RebuildDisplay()
    {
        _vm.RebuildCells();
        MonthTitleText.Text = _loc.CurrentLanguage == Services.Language.Chinese
            ? $"{_vm.DisplayYear}年{_vm.DisplayMonth}月"
            : $"{_vm.DisplayMonth}/{_vm.DisplayYear}";
    }

    void Window_Drag(object s, MouseButtonEventArgs e)
    {
        try { DragMove(); NativeMethods.PinToDesktop(this); } catch { }
    }

    void ResizeGrip_Down(object s, MouseButtonEventArgs e)
    {
        if (s is not Border g || g.Tag is not string tag) return;
        int d = tag switch
        {
            "TL" => HTTOPLEFT,
            "TR" => HTTOPRIGHT,
            "BL" => HTBOTTOMLEFT,
            _ => HTBOTTOMRIGHT
        };
        SendMessage(new WindowInteropHelper(this).Handle, WM_NCLBUTTONDOWN, (IntPtr)d, IntPtr.Zero);
        e.Handled = true;
    }

    void PrevMonth_Click(object s, RoutedEventArgs e)
    {
        if (_vm.DisplayMonth == 1) { _vm.DisplayMonth = 12; _vm.DisplayYear--; }
        else _vm.DisplayMonth--;
        RebuildDisplay();
    }

    void NextMonth_Click(object s, RoutedEventArgs e)
    {
        if (_vm.DisplayMonth == 12) { _vm.DisplayMonth = 1; _vm.DisplayYear++; }
        else _vm.DisplayMonth++;
        RebuildDisplay();
    }

    void Today_Click(object s, RoutedEventArgs e)
    {
        var now = DateTime.Now;
        _vm.DisplayYear = now.Year;
        _vm.DisplayMonth = now.Month;
        RebuildDisplay();
    }

    void DayCell_Click(object s, MouseButtonEventArgs e)
    {
        if (s is Border b && b.Tag is string dateKey)
        {
            // Parse date
            if (DateTime.TryParse(dateKey, out var dt))
            {
                // Check if date is in correct month range
                if (dt.Year == _vm.DisplayYear && dt.Month == _vm.DisplayMonth)
                {
                    _vm.SelectDate(dateKey);
                    NotesDateLabel.Text = (_loc.CurrentLanguage == Services.Language.Chinese ? "备注 - " : "Notes - ") + dateKey;
                }
            }
        }
        e.Handled = true;
    }

    void AddNote_Click(object s, RoutedEventArgs e)
    {
        var selectedDate = _vm.SelectedDate;
        if (string.IsNullOrEmpty(selectedDate)) return;
        var cn = _loc.CurrentLanguage == Services.Language.Chinese;

        // Dark themed input dialog (matching ZoneSettingsDialog style)
        var noteWindow = new Window
        {
            Title = _loc["Calendar.AddNote"],
            Width = 320, Height = 220,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent
        };
        var outerBorder = new System.Windows.Controls.Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x10, 0x11, 0x1A)),
            CornerRadius = new CornerRadius(12),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16)
        };
        outerBorder.Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 0, Color = Color.FromArgb(0xAA, 0x00, 0x00, 0x00), Opacity = 0.6 };

        var grid = new Grid { Margin = new Thickness(0) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Title
        grid.Children.Add(new TextBlock
        {
            Text = _loc["Calendar.AddNote"],
            FontSize = 14, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xF0)),
            Margin = new Thickness(0, 0, 0, 8)
        });
        Grid.SetRow(grid.Children[^1], 0);

        var textBox = new TextBox
        {
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = new SolidColorBrush(Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xF0)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            FontSize = 13, Padding = new Thickness(8, 6, 8, 6)
        };
        Grid.SetRow(textBox, 1);
        grid.Children.Add(textBox);

        var priorityPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        Grid.SetRow(priorityPanel, 2);
        var cmb = ComboBoxHelper.Create(width: 120);
        cmb.Items.Add(cn ? "无" : "None");
        cmb.Items.Add(cn ? "低" : "Low");
        cmb.Items.Add(cn ? "中" : "Normal");
        cmb.Items.Add(cn ? "高" : "High");
        cmb.SelectedIndex = 0;
        priorityPanel.Children.Add(new TextBlock
        {
            Text = cn ? "优先级:" : "Priority:",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0xA0)),
            FontSize = 11, Margin = new Thickness(0, 0, 6, 0)
        });
        priorityPanel.Children.Add(cmb);
        grid.Children.Add(priorityPanel);

        var btnPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        Grid.SetRow(btnPanel, 3);
        var cancelBtn = new Button
        {
            Content = _loc["Settings.Cancel"], Width = 70, Height = 30,
            Background = new SolidColorBrush(Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xF0)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1), FontSize = 12, Cursor = Cursors.Hand,
            Margin = new Thickness(0, 0, 8, 0)
        };
        var okBtn = new Button
        {
            Content = _loc["Rename.Ok"], Width = 70, Height = 30,
            Background = new SolidColorBrush(Color.FromRgb(0x7C, 0x3A, 0xED)),
            Foreground = System.Windows.Media.Brushes.White,
            BorderThickness = new Thickness(0), FontSize = 12,
            FontWeight = FontWeights.SemiBold, Cursor = Cursors.Hand
        };
        btnPanel.Children.Add(cancelBtn);
        btnPanel.Children.Add(okBtn);
        grid.Children.Add(btnPanel);

        outerBorder.Child = grid;
        noteWindow.Content = outerBorder;
        okBtn.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(textBox.Text))
            {
                var priority = cmb.SelectedIndex switch
                {
                    1 => NotePriority.Low,
                    2 => NotePriority.Normal,
                    3 => NotePriority.High,
                    _ => NotePriority.None
                };
                _vm.AddNote(selectedDate, textBox.Text.Trim(), priority);
                _vm.RebuildCells();
                _widgetService.UpdateCalendar(_calendar);
            }
            noteWindow.Close();
        };
        cancelBtn.Click += (_, _) => noteWindow.Close();
        noteWindow.ShowDialog();
    }

    void NoteCheckChanged(object s, RoutedEventArgs e)
    {
        if (s is CheckBox cb && cb.Tag is CalendarNoteViewModel nvm)
        {
            _vm.ToggleNoteComplete(nvm);
            _widgetService.UpdateCalendar(_calendar);
        }
    }

    void DeleteNote_Click(object s, RoutedEventArgs e)
    {
        if (s is Button btn && btn.Tag is CalendarNoteViewModel nvm)
        {
            _vm.DeleteNote(nvm);
            _vm.RebuildCells();
            _widgetService.UpdateCalendar(_calendar);
        }
    }

    void HideBtn_Click(object s, RoutedEventArgs e)
    {
        HideCalendar();
    }

    void DeleteCalendar_Click(object s, RoutedEventArgs e)
    {
        _widgetService.DeleteCalendar(_calendar.Id);
        Close();
    }

    public void ShowCalendar()
    {
        if (!IsVisible) Show();
        ApplyAcrylic();
        Left = _calendar.X; Top = _calendar.Y;
        MainContent.Visibility = Visibility.Visible; RestoreButton.Visibility = Visibility.Collapsed;
        MinWidth = 260; MinHeight = 340;
        Width = 320; Height = 440;
        _calendar.IsVisible = true; NativeMethods.PinToDesktop(this);
        NativeMethods.SetRoundedCorners(this, 10);
    }

    public void HideCalendar()
    {
        _calendar.X = Left; _calendar.Y = Top;
        // Always disable blur and clean up state before hiding
        AcrylicHelper.DisableBlur(this);
        MainContent.Visibility = Visibility.Collapsed;
        MinWidth = 36; MinHeight = 36;
        Width = 36; Height = 36;
        NativeMethods.DisableRoundedCorners(this);
        if (!_calendar.EnableRestoreButton)
        {
            Hide();
        }
        else
        {
            RestoreButton.Visibility = Visibility.Visible;
            NativeMethods.PinToDesktop(this);
        }
        _calendar.IsVisible = false;
        // Update AFTER Hide() to ensure correct state when event fires
        _widgetService.UpdateCalendar(_calendar);
    }

    void ApplyHidden()
    {
        AcrylicHelper.DisableBlur(this);
        NativeMethods.DisableRoundedCorners(this);
        MainContent.Visibility = Visibility.Collapsed;
        MinWidth = 36; MinHeight = 36;
        Width = 36; Height = 36;
        if (!_calendar.EnableRestoreButton)
        {
            Hide();
        }
        else
        {
            RestoreButton.Visibility = Visibility.Visible;
        }
    }

    void Restore_MouseDown(object s, MouseButtonEventArgs e)
    {
        _restoreDragging = false;
        _restoreDown = e.GetPosition(this);
        RestoreButton.CaptureMouse();
        e.Handled = true;
    }

    void Restore_MouseMove(object s, MouseEventArgs e)
    {
        if (!RestoreButton.IsMouseCaptured) return;
        var d = e.GetPosition(this) - _restoreDown;
        if (Math.Abs(d.X) > 3 || Math.Abs(d.Y) > 3)
        {
            _restoreDragging = true;
            RestoreButton.ReleaseMouseCapture();
            try { DragMove(); } catch { }
            _calendar.X = Left; _calendar.Y = Top;
        }
    }

    void Restore_MouseUp(object s, MouseButtonEventArgs e)
    {
        RestoreButton.ReleaseMouseCapture();
        if (!_restoreDragging) { ShowCalendar(); _widgetService.UpdateCalendar(_calendar); }
    }

    void Restore_Enter(object s, MouseEventArgs e) { RestoreButton.Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x2A, 0x2A, 0x4E)); }
    void Restore_Leave(object s, MouseEventArgs e) { RestoreButton.Background = new SolidColorBrush(Color.FromArgb(0xDD, 0x1A, 0x1A, 0x2E)); }

    protected override void OnClosed(EventArgs e)
    {
        if (_langChanged != null) _loc.LanguageChanged -= _langChanged;
        _langChanged = null;
        _widgetService.UpdateCalendar(_calendar);
        base.OnClosed(e);
    }
}
