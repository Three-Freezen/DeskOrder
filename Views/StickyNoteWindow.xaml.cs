using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopZones.Helpers;
using DesktopZones.Models;
using DesktopZones.Services;
using DesktopZones.ViewModels;

namespace DesktopZones.Views;

public partial class StickyNoteWindow : Window
{
    const uint WM_NCLBUTTONDOWN = 0x00A1;
    const int HTTOPLEFT = 13, HTTOPRIGHT = 14, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    private StickyNote _note;
    private readonly NotesService _notesService;
    private readonly StickyNoteViewModel _vm;
    private readonly LocalizationService _loc = LocalizationService.Instance;
    private bool _initializing = true;
    private bool _restoreDragging;
    private Point _restoreDown;
    public Action? OnStateChanged { get; set; }

    public StickyNoteWindow(StickyNote note, NotesService notesService)
    {
        InitializeComponent();
        ComboBoxHelper.ApplyDarkTheme(FontSizeCombo);
        _note = note;
        _notesService = notesService;
        _vm = new StickyNoteViewModel(note);
        DataContext = _vm;

        Left = note.X; Top = note.Y;
        Width = note.Width; Height = note.Height;
        if (Width < 200) Width = 260;
        if (Height < 150) Height = 200;
        MinWidth = 180; MinHeight = 120;
        TitleBox.Text = note.Title;

        // Load content into RichTextBox
        LoadContent(note.Content);

        ApplyStyle();
        if (note.PinnedTop) Topmost = true;

        LocationChanged += (_, _) => { _note.X = Left; _note.Y = Top; };
        SizeChanged += (_, _) => { _note.Width = Width; _note.Height = Height; if (MainContent.Visibility == Visibility.Visible) NativeMethods.UpdateRoundedCorners(this, 10); };

        Loaded += OnLoad;
        Activated += (_, _) => { Topmost = true; };
        _notesService.NotesChanged += OnNotesChanged;

        _initializing = false;
    }

    void OnNotesChanged()
    {
        if (!IsLoaded) return;
        var latest = _notesService.Notes.FirstOrDefault(n => n.Id == _note.Id);
        if (latest != null) _note = latest;
        if (MainContent.Visibility == Visibility.Visible)
            ApplyAcrylic();
        ApplyBackgroundImage();
        ApplyStyle();
    }

    private void LoadContent(string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            ContentBox.Document = new FlowDocument(new Paragraph(new Run("") { Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)) }));
            return;
        }
        // Simple format: plain text with line breaks preserved
        var doc = new FlowDocument { LineHeight = double.NaN };
        var para = new Paragraph { Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)) };
        para.Inlines.Add(new Run(content));
        doc.Blocks.Add(para);
        ContentBox.Document = doc;
    }

    private string SaveContent()
    {
        var tr = new TextRange(ContentBox.Document.ContentStart, ContentBox.Document.ContentEnd);
        return tr.Text;
    }

    void OnLoad(object s, RoutedEventArgs e)
    {
        NativeMethods.PinToDesktop(this);
        NativeMethods.SetToolWindow(this);
        ApplyAcrylic();
        ApplyBackgroundImage();
        NativeMethods.SetRoundedCorners(this, 10);
        NativeMethods.UpdateRoundedCorners(this, 10);
        if (!_note.IsVisible) ApplyHidden();
    }

    // ── Show / Hide (minimize-restore) ──

    public void ShowNote()
    {
        if (!IsVisible) Show();
        ApplyAcrylic();
        Left = _note.X; Top = _note.Y;
        MainContent.Visibility = Visibility.Visible; RestoreButton.Visibility = Visibility.Collapsed;
        MinWidth = 180; MinHeight = 120;
        Width = _note.Width; Height = _note.Height;
        _note.IsVisible = true; NativeMethods.PinToDesktop(this);
        NativeMethods.SetRoundedCorners(this, 10);
        OnStateChanged?.Invoke();
    }

    public void HideNote()
    {
        _note.X = Left; _note.Y = Top; _note.Width = Width; _note.Height = Height;
        // Always disable blur and clean up state before hiding
        AcrylicHelper.DisableBlur(this);
        MainContent.Visibility = Visibility.Collapsed;
        MinWidth = 36; MinHeight = 36;
        Width = 36; Height = 36;
        NativeMethods.DisableRoundedCorners(this);
        if (!_note.EnableRestoreButton)
        {
            Hide();
        }
        else
        {
            RestoreButton.Visibility = Visibility.Visible;
            NativeMethods.PinToDesktop(this);
        }
        _note.IsVisible = false;
        // Update AFTER Hide() to ensure correct state when event fires
        _notesService.UpdateNote(_note);
        OnStateChanged?.Invoke();
    }

    void ApplyHidden()
    {
        AcrylicHelper.DisableBlur(this);
        NativeMethods.DisableRoundedCorners(this);
        MainContent.Visibility = Visibility.Collapsed;
        MinWidth = 36; MinHeight = 36;
        Width = 36; Height = 36;
        if (!_note.EnableRestoreButton)
        {
            Hide();
        }
        else
        {
            RestoreButton.Visibility = Visibility.Visible;
        }
    }

    // ── Restore button drag handlers ──

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
            _note.X = Left; _note.Y = Top;
        }
    }

    void Restore_MouseUp(object s, MouseButtonEventArgs e)
    {
        RestoreButton.ReleaseMouseCapture();
        if (!_restoreDragging) { ShowNote(); _notesService.UpdateNote(_note); }
    }

    void Restore_Enter(object s, MouseEventArgs e) { RestoreButton.Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x2A, 0x2A, 0x4E)); }
    void Restore_Leave(object s, MouseEventArgs e) { RestoreButton.Background = new SolidColorBrush(Color.FromArgb(0xDD, 0x1A, 0x1A, 0x2E)); }

    // ── Acrylic / frosted glass ──

    void ApplyAcrylic()
    {
        if (_note.EnableAcrylic)
        {
            AcrylicHelper.EnableBlur(this, _note.GlassBlurAmount, _note.GlassTintOpacity,
                _note.GlassTintLuminosity, _note.GlassColorMode);
            try
            {
                var fillColor = (Color)ColorConverter.ConvertFromString(_note.FillColor)!;
                byte bgAlpha = (byte)(_note.GlassBlurAmount > 0 ? 0x06 : 0x0F);
                NoteBorder.Background = new SolidColorBrush(Color.FromArgb(bgAlpha, fillColor.R, fillColor.G, fillColor.B));
            }
            catch { }
        }
        else
        {
            try
            {
                var fillColor = (Color)ColorConverter.ConvertFromString(_note.FillColor)!;
                NoteBorder.Background = new SolidColorBrush(fillColor);
            }
            catch { }
        }
    }

    void ApplyStyle()
    {
        try
        {
            var bc = (Color)ColorConverter.ConvertFromString(_note.BorderColor);
            NoteBorder.BorderBrush = new SolidColorBrush(bc);
            NoteBorder.BorderThickness = new Thickness(_note.BorderThickness);
        }
        catch { }
    }

    // ── Background image ──

    void ApplyBackgroundImage()
    {
        try
        {
            if (!string.IsNullOrEmpty(_note.BackgroundImagePath) && System.IO.File.Exists(_note.BackgroundImagePath))
            {
                var bi = new System.Windows.Media.Imaging.BitmapImage();
                bi.BeginInit();
                bi.UriSource = new Uri(_note.BackgroundImagePath);
                bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bi.EndInit();
                NoteBgImage.Source = bi;
                NoteBgImage.Stretch = Stretch.UniformToFill;
                double nw = NoteBgBorder.ActualWidth > 0 ? NoteBgBorder.ActualWidth : Width;
                double nh = NoteBgBorder.ActualHeight > 0 ? NoteBgBorder.ActualHeight : Height;

                // UniformToFill — fill target area maintaining aspect ratio
                double imgW = bi.PixelWidth;
                double imgH = bi.PixelHeight;
                double utfScale = Math.Max((nw * _note.BgImageZoom) / imgW, (nh * _note.BgImageZoom) / imgH);
                double displayedW = imgW * utfScale;
                double displayedH = imgH * utfScale;

                NoteBgImage.Width = displayedW;
                NoteBgImage.Height = displayedH;

                // Position image: center at container center + offset (matches preview positioning)
                double zoneCenterX = nw / 2;
                double zoneCenterY = nh / 2;
                double imgCenterX = displayedW / 2;
                double imgCenterY = displayedH / 2;
                double ox = _note.BgImageOffsetX;
                double oy = _note.BgImageOffsetY;

                NoteBgImage.Margin = new Thickness(
                    zoneCenterX - imgCenterX + ox,
                    zoneCenterY - imgCenterY + oy, 0, 0);
                NoteBgImage.HorizontalAlignment = HorizontalAlignment.Left;
                NoteBgImage.VerticalAlignment = VerticalAlignment.Top;
                NoteBgImage.Opacity = Math.Max(0.01, _note.BackgroundImageOpacity / 100.0);
            }
            else
            {
                NoteBgImage.Source = null;
                NoteBgImage.Opacity = 0;
            }
        }
        catch { if (NoteBgImage != null) { NoteBgImage.Source = null; NoteBgImage.Opacity = 0; } }
    }

    // ── Title bar ──

    void TitleBar_Drag(object s, MouseButtonEventArgs e)
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
        try { SendMessage(new WindowInteropHelper(this).Handle, WM_NCLBUTTONDOWN, (IntPtr)d, IntPtr.Zero); } catch { }
        e.Handled = true;
    }

    void PinBtn_Click(object s, RoutedEventArgs e)
    {
        _vm.PinnedTop = !_vm.PinnedTop;
        Topmost = _vm.PinnedTop;
        if (!_vm.PinnedTop) NativeMethods.PinToDesktop(this);
        Save();
    }

    void HideBtn_Click(object s, RoutedEventArgs e)
    {
        HideNote();
    }

    void ToggleRestore_Click(object s, RoutedEventArgs e)
    {
        _note.EnableRestoreButton = !_note.EnableRestoreButton;
        var cn = _loc.CurrentLanguage == DesktopZones.Services.Language.Chinese;
        if (s is MenuItem mi)
            mi.Header = _note.EnableRestoreButton
                ? (cn ? "关闭恢复按钮" : "Disable Restore")
                : (cn ? "启用恢复按钮" : "Enable Restore");
    }

    void Delete_Click(object s, RoutedEventArgs e)
    {
        _notesService.DeleteNote(_note.Id);
        Close();
    }

    void TitleBox_LostFocus(object s, RoutedEventArgs e)
    {
        _vm.Title = TitleBox.Text;
        Save();
    }

    void TitleBox_KeyDown(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _vm.Title = TitleBox.Text;
            Save();
            ContentBox.Focus();
            e.Handled = true;
        }
    }

    void Save()
    {
        _vm.Content = SaveContent();
        _vm.ApplyToModel();
        _notesService.UpdateNote(_note);
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm.Content = SaveContent();
        _vm.ApplyToModel();
        _notesService.UpdateNote(_note);
        base.OnClosed(e);
    }

    // ── Bring to Front (called by global hotkey) ──

    private DispatcherTimer? _bringToFrontTimer;

    public void BringToFront()
    {
        if (!IsVisible) Show();
        Topmost = true;
        Activate();
        if (_bringToFrontTimer != null)
        {
            _bringToFrontTimer.Stop();
            _bringToFrontTimer = null;
        }
        _bringToFrontTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _bringToFrontTimer.Tick += (s, _) =>
        {
            Topmost = false;
            NativeMethods.PinToDesktop(this);
            _bringToFrontTimer?.Stop();
            _bringToFrontTimer = null;
        };
        _bringToFrontTimer.Start();
    }

    // ── Formatting toolbar handlers (per-character via RichTextBox) ──

    void FontSizeCombo_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        if (FontSizeCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            if (double.TryParse(tag, out var fs))
            {
                ApplyToSelection(TextElement.FontSizeProperty, fs);
            }
        }
    }

    void BoldBtn_Click(object s, RoutedEventArgs e)
    {
        var sel = ContentBox.Selection;
        if (sel == null || sel.IsEmpty)
        {
            // Toggle for new text
            ContentBox.FontWeight = ContentBox.FontWeight == FontWeights.Bold
                ? FontWeights.Normal : FontWeights.Bold;
        }
        else
        {
            var currentWeight = sel.GetPropertyValue(Inline.FontWeightProperty);
            var newWeight = (currentWeight is FontWeight fw && fw.ToOpenTypeWeight() > 400)
                ? FontWeights.Normal : FontWeights.Bold;
            sel.ApplyPropertyValue(Inline.FontWeightProperty, newWeight);
        }
        UpdateFormatButtons();
    }

    void ItalicBtn_Click(object s, RoutedEventArgs e)
    {
        var sel = ContentBox.Selection;
        if (sel == null || sel.IsEmpty)
        {
            ContentBox.FontStyle = ContentBox.FontStyle == FontStyles.Italic
                ? FontStyles.Normal : FontStyles.Italic;
        }
        else
        {
            var currentStyle = sel.GetPropertyValue(Inline.FontStyleProperty);
            var newStyle = (currentStyle is FontStyle fs && fs == FontStyles.Italic)
                ? FontStyles.Normal : FontStyles.Italic;
            sel.ApplyPropertyValue(Inline.FontStyleProperty, newStyle);
        }
        UpdateFormatButtons();
    }

    void UnderlineBtn_Click(object s, RoutedEventArgs e)
    {
        var sel = ContentBox.Selection;
        if (sel == null || sel.IsEmpty)
        {
            // Can't easily toggle underline on empty selection for new text in RichTextBox
            return;
        }
        var currentDeco = sel.GetPropertyValue(Inline.TextDecorationsProperty);
        var newDeco = (currentDeco is TextDecorationCollection td && td.Count > 0)
            ? null : TextDecorations.Underline;
        sel.ApplyPropertyValue(Inline.TextDecorationsProperty, newDeco);
        UpdateFormatButtons();
    }

    void FontColorBtn_Click(object s, RoutedEventArgs e)
    {
        FontColorPopup.IsOpen = !FontColorPopup.IsOpen;
    }

    void ColorPreset_Click(object s, MouseButtonEventArgs e)
    {
        if (s is Border b && b.Tag is string colorHex)
        {
            try
            {
                var color = (Color)ColorConverter.ConvertFromString(colorHex);
                ApplyToSelection(Inline.ForegroundProperty, new SolidColorBrush(color));
                FontColorBtn.Foreground = new SolidColorBrush(color);
            }
            catch { }
        }
        FontColorPopup.IsOpen = false;
    }

    void ColorCustomBtn_Click(object s, RoutedEventArgs e)
    {
        FontColorPopup.IsOpen = false;
        var dlg = new ColorPickerDialog("FFFFFF") { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            try
            {
                var color = (Color)ColorConverter.ConvertFromString("#" + dlg.SelectedColor);
                var brush = new SolidColorBrush(color);
                ApplyToSelection(Inline.ForegroundProperty, brush);
                FontColorBtn.Foreground = brush;
            }
            catch { }
        }
    }

    private void ApplyToSelection(DependencyProperty property, object value)
    {
        var sel = ContentBox.Selection;
        if (sel != null && !sel.IsEmpty)
        {
            sel.ApplyPropertyValue(property, value);
        }
    }

    void ContentBox_SelectionChanged(object s, RoutedEventArgs e)
    {
        if (_initializing) return;
        UpdateFormatButtons();
    }

    void UpdateFormatButtons()
    {
        var sel = ContentBox.Selection;
        bool hasSelection = sel != null && !sel.IsEmpty;

        // Bold
        if (hasSelection)
        {
            var w = sel!.GetPropertyValue(Inline.FontWeightProperty);
            BoldBtn.Background = (w is FontWeight fw && fw.ToOpenTypeWeight() > 400)
                ? new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)) : Brushes.Transparent;
        }
        else
        {
            BoldBtn.Background = ContentBox.FontWeight == FontWeights.Bold
                ? new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)) : Brushes.Transparent;
        }

        // Italic
        if (hasSelection)
        {
            var st = sel!.GetPropertyValue(Inline.FontStyleProperty);
            ItalicBtn.Background = (st is FontStyle fs && fs == FontStyles.Italic)
                ? new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)) : Brushes.Transparent;
        }
        else
        {
            ItalicBtn.Background = ContentBox.FontStyle == FontStyles.Italic
                ? new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)) : Brushes.Transparent;
        }

        // Underline
        if (hasSelection)
        {
            var td = sel!.GetPropertyValue(Inline.TextDecorationsProperty);
            UnderlineBtn.Background = (td is TextDecorationCollection tdc && tdc.Count > 0)
                ? new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)) : Brushes.Transparent;
        }
        else
        {
            UnderlineBtn.Background = Brushes.Transparent;
        }

        // Font size combo
        if (hasSelection)
        {
            var fs = sel!.GetPropertyValue(TextElement.FontSizeProperty);
            if (fs is double fontSize)
            {
                foreach (ComboBoxItem item in FontSizeCombo.Items)
                {
                    if (item.Tag is string tag && double.TryParse(tag, out var val)
                        && Math.Abs(val - fontSize) < 0.1)
                    {
                        FontSizeCombo.SelectedItem = item;
                        break;
                    }
                }
            }
        }

        // Font color button
        if (hasSelection)
        {
            var fg = sel!.GetPropertyValue(Inline.ForegroundProperty);
            if (fg is SolidColorBrush scb)
                FontColorBtn.Foreground = scb;
        }
    }
}
