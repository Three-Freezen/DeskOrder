using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using DesktopZones.Helpers;
using DesktopZones.Models;
using DesktopZones.Services;
using DesktopZones.ViewModels;

namespace DesktopZones.Views;

public partial class ClockWidget : Window
{
    const uint WM_NCLBUTTONDOWN = 0x00A1;
    const int HTTOPLEFT = 13, HTTOPRIGHT = 14, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    private DesktopClock _clock;
    private readonly WidgetService _widgetService;
    private readonly ClockViewModel _vm;
    private readonly DispatcherTimer _timer;
    private readonly LocalizationService _loc = LocalizationService.Instance;

    private bool _restoreDragging;
    private Point _restoreDown;

    public ClockWidget(DesktopClock clock, WidgetService widgetService)
    {
        InitializeComponent();
        _clock = clock;
        _widgetService = widgetService;
        _vm = new ClockViewModel(clock);
        DataContext = _vm;

        Left = clock.X; Top = clock.Y;
        Opacity = clock.Opacity;
        MinWidth = 140; MinHeight = 80;
        Width = 320; Height = 140;

        ApplyMode();
        ApplyStyle();
        UpdateContextMenuLabels();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += Tick;
        _timer.Start();
        Tick(null, EventArgs.Empty);

        Loaded += OnLoad;
        SizeChanged += OnSizeChanged;
        LocationChanged += (_, _) => { _clock.X = Left; _clock.Y = Top; };
        Activated += (_, _) => { Topmost = true; };
        _langChanged = _ => UpdateContextMenuLabels();
        _loc.LanguageChanged += _langChanged;
        _widgetService.ClocksChanged += OnClocksChanged;
    }
    private Action<Services.Language>? _langChanged;

    void OnClocksChanged()
    {
        if (!IsLoaded) return;
        // Re-fetch latest clock from collection (UpdateClock replaces the object)
        var latest = _widgetService.Clocks.FirstOrDefault(c => c.Id == _clock.Id);
        if (latest != null) _clock = latest;
        if (MainContent.Visibility == Visibility.Visible)
            ApplyAcrylic();
        ApplyBackgroundImage();
        ApplyDigitalBackgroundImage();
        ApplyStyle();
    }

    void OnLoad(object s, RoutedEventArgs e)
    {
        NativeMethods.PinToDesktop(this);
        NativeMethods.SetToolWindow(this);
        if (_clock.Mode == ClockDisplayMode.Digital)
            NativeMethods.RemoveThickFrame(this);
        ApplyAcrylic();
        GenerateMarkers();
        if (_clock.Mode == ClockDisplayMode.Digital)
        {
            Width = 320; Height = 140;
        }
        ApplyBackgroundImage();
        ApplyDigitalBackgroundImage();
        // Set rounded corners LAST after all sizing is complete
        NativeMethods.SetRoundedCorners(this, 10);
        if (!_clock.IsVisible) ApplyHidden();
    }

    // Keep OS-level rounded corners in sync on resize
    void OnSizeChanged(object s, SizeChangedEventArgs e)
    {
        if (MainContent.Visibility != Visibility.Visible) return;
        _clock.Width = Width; _clock.Height = Height;
        NativeMethods.UpdateRoundedCorners(this, 10);
    }

    // ── Background image (analog clock face) ──

    void ApplyBackgroundImage()
    {
        try
        {
            if (!string.IsNullOrEmpty(_clock.BackgroundImagePath) && System.IO.File.Exists(_clock.BackgroundImagePath))
            {
                var bi = new System.Windows.Media.Imaging.BitmapImage();
                bi.BeginInit();
                bi.UriSource = new Uri(_clock.BackgroundImagePath);
                bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bi.EndInit();
                ClockBgImage.Source = bi;
                ClockBgImage.Stretch = Stretch.UniformToFill;

                // UniformToFill — fill target area maintaining aspect ratio
                double imgW = bi.PixelWidth;
                double imgH = bi.PixelHeight;
                double targetSize = 200;
                double utfScale = Math.Max((targetSize * _clock.BgImageZoom) / imgW, (targetSize * _clock.BgImageZoom) / imgH);
                double displayedW = imgW * utfScale;
                double displayedH = imgH * utfScale;

                ClockBgImage.Width = displayedW;
                ClockBgImage.Height = displayedH;

                // Offset: Canvas is resized to displayed size and centered by Grid,
                // so Image position within Canvas is simply the raw offset
                double ox = _clock.BgImageOffsetX;
                double oy = _clock.BgImageOffsetY;

                ClockBgImage.Margin = new Thickness(ox, oy, 0, 0);
                ClockBgImage.HorizontalAlignment = HorizontalAlignment.Left;
                ClockBgImage.VerticalAlignment = VerticalAlignment.Top;
                ClockBgImage.Opacity = Math.Max(0.01, _clock.BackgroundImageOpacity / 100.0);

                // Expand Canvas to match Image size so visual clipping works correctly
                AnalogBgClip.Width = displayedW;
                AnalogBgClip.Height = displayedH;
                AnalogBgClipGeo.Center = new System.Windows.Point(displayedW / 2, displayedH / 2);
                AnalogBgClipGeo.RadiusX = 100;
                AnalogBgClipGeo.RadiusY = 100;
            }
            else
            {
                ClockBgImage.Source = null;
                ClockBgImage.Opacity = 0;
                AnalogBgClip.Width = 200;
                AnalogBgClip.Height = 200;
                AnalogBgClipGeo.Center = new System.Windows.Point(100, 100);
                AnalogBgClipGeo.RadiusX = 100;
                AnalogBgClipGeo.RadiusY = 100;
            }
        }
        catch { if (ClockBgImage != null) { ClockBgImage.Source = null; ClockBgImage.Opacity = 0; } }
    }

    // ── Digital clock background image ──

    void ApplyDigitalBackgroundImage()
    {
        try
        {
            if (!string.IsNullOrEmpty(_clock.DigitalBackgroundImagePath) && System.IO.File.Exists(_clock.DigitalBackgroundImagePath))
            {
                var bi = new System.Windows.Media.Imaging.BitmapImage();
                bi.BeginInit();
                bi.UriSource = new Uri(_clock.DigitalBackgroundImagePath);
                bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bi.EndInit();
                DigitalBgImage.Source = bi;
                DigitalBgImage.Stretch = Stretch.UniformToFill;

                // UniformToFill — fill target area maintaining aspect ratio
                double imgW = bi.PixelWidth;
                double imgH = bi.PixelHeight;
                double dw = 320 * _clock.DigitalBgImageZoom;
                double dh = 140 * _clock.DigitalBgImageZoom;
                double utfScale = Math.Max(dw / imgW, dh / imgH);
                double displayedW = imgW * utfScale;
                double displayedH = imgH * utfScale;

                DigitalBgImage.Width = displayedW;
                DigitalBgImage.Height = displayedH;

                // Position image: center at container center + offset (matches preview positioning)
                double zoneCenterX = dw / 2;
                double zoneCenterY = dh / 2;
                double imgCenterX = displayedW / 2;
                double imgCenterY = displayedH / 2;
                double ox = _clock.DigitalBgImageOffsetX;
                double oy = _clock.DigitalBgImageOffsetY;

                DigitalBgImage.Margin = new Thickness(
                    zoneCenterX - imgCenterX + ox,
                    zoneCenterY - imgCenterY + oy, 0, 0);
                DigitalBgImage.HorizontalAlignment = HorizontalAlignment.Left;
                DigitalBgImage.VerticalAlignment = VerticalAlignment.Top;
                DigitalBgImage.Opacity = Math.Max(0.01, _clock.DigitalBackgroundImageOpacity / 100.0);
            }
            else
            {
                DigitalBgImage.Source = null;
                DigitalBgImage.Opacity = 0;
            }
        }
        catch { if (DigitalBgImage != null) { DigitalBgImage.Source = null; DigitalBgImage.Opacity = 0; } }
    }

    // ── Generate proper clock tick marks using trigonometry ──

    void GenerateMarkers()
    {
        MarkerCanvas.Children.Clear();
        double cx = 100, cy = 100; // center of 200×200 face

        for (int i = 0; i < 60; i++)
        {
            double angleDeg = i * 6.0; // degrees clockwise from 12 o'clock
            double rad = angleDeg * Math.PI / 180.0;

            bool isHour = i % 5 == 0;
            bool isMajor = i % 15 == 0;

            double innerR = isHour ? 84 : 92;
            double outerR = 98;

            double x1 = cx + innerR * Math.Sin(rad);
            double y1 = cy - innerR * Math.Cos(rad);
            double x2 = cx + outerR * Math.Sin(rad);
            double y2 = cy - outerR * Math.Cos(rad);

            var line = new Line
            {
                X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
                Stroke = new SolidColorBrush(Color.FromArgb(
                    (byte)(isMajor ? 0xCC : (isHour ? 0x90 : 0x40)),
                    0xFF, 0xFF, 0xFF)),
                StrokeThickness = isMajor ? 3.5 : (isHour ? 2.5 : 1.2),
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };
            MarkerCanvas.Children.Add(line);
        }
    }

    // ── Acrylic / frosted glass ──

    void ApplyAcrylic()
    {
        var config = _widgetService.GetConfig();
        string fillColorStr = _clock.UseGlobalAppearance ? config.GlobalFillColor : _clock.FillColor;
        string borderColorStr = _clock.UseGlobalAppearance ? config.GlobalBorderColor : _clock.BorderColor;
        double borderThickness = _clock.UseGlobalAppearance ? config.GlobalBorderThickness : _clock.BorderThickness;

        if (_clock.EnableAcrylic)
        {
            AcrylicHelper.EnableBlur(this, _clock.GlassBlurAmount, _clock.GlassTintOpacity,
                _clock.GlassTintLuminosity, _clock.GlassColorMode);
            try
            {
                // Use fillColor directly — its ARGB alpha controls transparency
                var fillColor = (Color)ColorConverter.ConvertFromString(fillColorStr)!;
                ClockBorder.Background = new SolidColorBrush(fillColor);
            }
            catch
            {
                ClockBorder.Background = new SolidColorBrush(Color.FromArgb(0x08, 0x00, 0x00, 0x00));
            }
            if (_clock.EnableLiquidGlass)
            {
                ClockBorder.BorderBrush = AcrylicHelper.CreateChromaticBorder();
                ClockBorder.BorderThickness = new Thickness(Math.Max(1.0, borderThickness));
            }
        }
        else
        {
            AcrylicHelper.DisableBlur(this);
            try
            {
                ClockBorder.Background = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString(fillColorStr)!);
            }
            catch { }
        }
    }

    // ── Style (border / fill) ──

    void ApplyStyle()
    {
        // Always apply user's border color (overrides chromatic border from LiquidGlass if needed)
        try
        {
            ClockBorder.BorderBrush = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(_clock.BorderColor)!);
        }
        catch { }
        ClockBorder.BorderThickness = new Thickness(_clock.BorderThickness);
    }

    private string _lastDateText = "";

    void Tick(object? s, EventArgs e)
    {
        try
        {
            var now = DateTime.Now;
            _vm.UpdateTime(now);

            // Update digital display
            if (TimeText != null) TimeText.Text = _vm.DisplayText;

            // Only update DateText when the date actually changes (perf guard)
            if (DateText != null && _vm.DateText != _lastDateText)
            {
                _lastDateText = _vm.DateText;
                DateText.Text = _vm.DateText;
            }

            // Analog mode: only update hands when analog is visible
            if (_clock.Mode == ClockDisplayMode.Analog)
            {
                if (AnalogDateText != null) AnalogDateText.Text = now.Day.ToString();
                if (HourHand?.RenderTransform is RotateTransform hrt) hrt.Angle = _vm.HourAngle;
                if (MinuteHand?.RenderTransform is RotateTransform mrt) mrt.Angle = _vm.MinuteAngle;
                if (SecondHand?.RenderTransform is RotateTransform srt) srt.Angle = _vm.SecondAngle;
                if (SecondHand != null) SecondHand.Visibility = _vm.ShowSeconds ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        catch { /* Ignore timer tick errors during window teardown */ }
    }

    void ApplyMode()
    {
        bool isAnalog = _clock.Mode == ClockDisplayMode.Analog;
        DigitalPanel.Visibility = isAnalog ? Visibility.Collapsed : Visibility.Visible;
        AnalogPanel.Visibility = isAnalog ? Visibility.Visible : Visibility.Collapsed;
        if (isAnalog)
        {
            Width = 240; Height = 260;
            ResizeMode = ResizeMode.NoResize;
            foreach (var grip in FindResizeGrips(this))
                grip.Visibility = Visibility.Collapsed;
            PreviewMouseLeftButtonDown -= AnalogDrag;
            PreviewMouseLeftButtonDown += AnalogDrag;
            ApplyBackgroundImage();
        }
        else
        {
            Width = 320; Height = 140;
            ResizeMode = ResizeMode.CanResize;
            NativeMethods.RemoveThickFrame(this);
            PreviewMouseLeftButtonDown -= AnalogDrag;
            foreach (var grip in FindResizeGrips(this))
                grip.Visibility = Visibility.Visible;
            ApplyDigitalBackgroundImage();
        }
    }

    void AnalogDrag(object s, MouseButtonEventArgs e)
    {
        // Skip drag when minimized (restore button is showing)
        if (RestoreButton.Visibility == Visibility.Visible) return;

        if (e.OriginalSource is DependencyObject src)
        {
            while (src != null)
            {
                if (src is System.Windows.Controls.ContextMenu) return;
                src = VisualTreeHelper.GetParent(src);
            }
        }
        try { DragMove(); NativeMethods.PinToDesktop(this); } catch { }
    }

    static IEnumerable<Border> FindResizeGrips(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Border b && b.Tag is string tag
                && (tag == "TL" || tag == "TR" || tag == "BL" || tag == "BR"))
                yield return b;
            foreach (var nested in FindResizeGrips(child))
                yield return nested;
        }
    }

    void UpdateContextMenuLabels()
    {
        var cn = _loc.CurrentLanguage == Services.Language.Chinese;
        bool isAnalog = _clock.Mode == ClockDisplayMode.Analog;
        CtxSwitchMode.Header = isAnalog ? _loc["Clock.DigitalMode"] : _loc["Clock.AnalogMode"];
        CtxToggleSeconds.Header = _clock.ShowSeconds ? _loc["Clock.HideSeconds"] : _loc["Clock.ShowSeconds"];
        CtxToggle24h.Header = _clock.Use24Hour ? _loc["Clock.Format12h"] : _loc["Clock.Format24h"];
        CtxToggleRestore.Header = _clock.EnableRestoreButton
            ? (cn ? "关闭恢复按钮" : "Disable Restore")
            : (cn ? "启用恢复按钮" : "Enable Restore");
        CtxDelete.Header = _loc["Clock.Delete"];
    }

    void Window_Drag(object s, MouseButtonEventArgs e)
    {
        try { DragMove(); NativeMethods.PinToDesktop(this); } catch { }
    }

    void Window_PreviewMouseLeftButtonDown(object s, MouseButtonEventArgs e)
    {
        // Allow resize grips and buttons to handle their own clicks
        if (e.OriginalSource is System.Windows.Controls.Button) return;
        // Allow restore button to handle its own clicks
        if (RestoreButton.Visibility == Visibility.Visible) return;
        if (s is FrameworkElement fe && fe.Parent is Window)
        {
            try { DragMove(); NativeMethods.PinToDesktop(this); } catch { }
        }
    }

    void Window_PreviewMouseRightButtonDown(object s, MouseButtonEventArgs e)
    {
        // Context menu is on root Grid, no special handling needed
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

    void SwitchMode_Click(object s, RoutedEventArgs e)
    {
        _clock.Mode = _clock.Mode == ClockDisplayMode.Digital ? ClockDisplayMode.Analog : ClockDisplayMode.Digital;
        _vm.Mode = _clock.Mode;
        ApplyMode();
        GenerateMarkers();
        UpdateContextMenuLabels();
        _widgetService.UpdateClock(_clock);
    }

    void ToggleSeconds_Click(object s, RoutedEventArgs e)
    {
        _clock.ShowSeconds = !_clock.ShowSeconds;
        _vm.ShowSeconds = _clock.ShowSeconds;
        UpdateContextMenuLabels();
        _widgetService.UpdateClock(_clock);
    }

    void Toggle24h_Click(object s, RoutedEventArgs e)
    {
        _clock.Use24Hour = !_clock.Use24Hour;
        _vm.Use24Hour = _clock.Use24Hour;
        UpdateContextMenuLabels();
        _widgetService.UpdateClock(_clock);
    }

    void ToggleRestore_Click(object s, RoutedEventArgs e)
    {
        _clock.EnableRestoreButton = !_clock.EnableRestoreButton;
        var cn = _loc.CurrentLanguage == Services.Language.Chinese;
        if (s is MenuItem mi)
            mi.Header = _clock.EnableRestoreButton
                ? (cn ? "关闭恢复按钮" : "Disable Restore")
                : (cn ? "启用恢复按钮" : "Enable Restore");
    }

    void HideBtn_Click(object s, RoutedEventArgs e)
    {
        HideClock();
    }

    void Delete_Click(object s, RoutedEventArgs e)
    {
        _timer.Stop();
        _widgetService.DeleteClock(_clock.Id);
        Close();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        double delta = e.Delta > 0 ? 0.05 : -0.05;
        _clock.Opacity = Math.Clamp(_clock.Opacity + delta, 0.1, 1.0);
        _vm.Opacity = _clock.Opacity;
        _widgetService.UpdateClock(_clock);
        e.Handled = true;
    }

    public void ShowClock()
    {
        if (!IsVisible) Show();
        ApplyAcrylic();
        Left = _clock.X; Top = _clock.Y;
        MainContent.Visibility = Visibility.Visible; RestoreButton.Visibility = Visibility.Collapsed;
        MinWidth = 140; MinHeight = 80;
        Width = _clock.Width > 140 ? _clock.Width : 320;
        Height = _clock.Height > 80 ? _clock.Height : 140;
        _clock.IsVisible = true; NativeMethods.PinToDesktop(this);
        NativeMethods.SetRoundedCorners(this, 10);
    }

    public void HideClock()
    {
        _clock.X = Left; _clock.Y = Top; _clock.Width = Width; _clock.Height = Height;
        // Always disable blur and clean up state before hiding
        AcrylicHelper.DisableBlur(this);
        MainContent.Visibility = Visibility.Collapsed;
        MinWidth = 36; MinHeight = 36;
        Width = 36; Height = 36;
        NativeMethods.DisableRoundedCorners(this);
        if (!_clock.EnableRestoreButton)
        {
            Hide();
        }
        else
        {
            RestoreButton.Visibility = Visibility.Visible;
            NativeMethods.PinToDesktop(this);
        }
        _clock.IsVisible = false;
        // Update AFTER Hide() to ensure correct state when event fires
        _widgetService.UpdateClock(_clock);
    }

    void ApplyHidden()
    {
        AcrylicHelper.DisableBlur(this);
        NativeMethods.DisableRoundedCorners(this);
        MainContent.Visibility = Visibility.Collapsed;
        MinWidth = 36; MinHeight = 36;
        Width = 36; Height = 36;
        if (!_clock.EnableRestoreButton)
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
            _clock.X = Left; _clock.Y = Top;
        }
    }

    void Restore_MouseUp(object s, MouseButtonEventArgs e)
    {
        RestoreButton.ReleaseMouseCapture();
        if (!_restoreDragging) { ShowClock(); _widgetService.UpdateClock(_clock); }
    }

    void Restore_Enter(object s, MouseEventArgs e) { RestoreButton.Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x2A, 0x2A, 0x4E)); }
    void Restore_Leave(object s, MouseEventArgs e) { RestoreButton.Background = new SolidColorBrush(Color.FromArgb(0xDD, 0x1A, 0x1A, 0x2E)); }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        if (_langChanged != null) _loc.LanguageChanged -= _langChanged;
        _langChanged = null;
        base.OnClosed(e);
    }
}
