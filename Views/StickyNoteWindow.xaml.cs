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
    private bool _pendingUnderline;
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
        ApplyTitleBar();
        if (note.PinnedTop) Topmost = true;

        LocationChanged += (_, _) => { _note.X = Left; _note.Y = Top; };
        SizeChanged += (_, _) => { if (MainContent.Visibility == Visibility.Visible) { _note.Width = Width; _note.Height = Height; NativeMethods.UpdateRoundedCorners(this, 10); } };

        Loaded += OnLoad;
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
        ApplyTitleBar();
    }

    private void LoadContent(string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            ContentBox.Document = new FlowDocument(new Paragraph(new Run("")));
            return;
        }
        // Detect format tags → use rich loader
        if (content.Contains("[size=") || content.Contains("[bold]") || content.Contains("[color="))
        {
            LoadFormatted(content);
            return;
        }
        var doc = new FlowDocument { LineHeight = double.NaN };
        var para = new Paragraph();
        para.Inlines.Add(new Run(content));
        doc.Blocks.Add(para);
        ContentBox.Document = doc;
    }

    private string SaveContent()
    {
        var tr = new TextRange(ContentBox.Document.ContentStart, ContentBox.Document.ContentEnd);
        return tr.Text;
    }

    // ── Save to file ──

    void SaveBtn_Click(object s, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var saveItem = new MenuItem { Header = "保存" };
        saveItem.Click += (_, _) => SaveToFile();
        var saveAsItem = new MenuItem { Header = "另存为" };
        saveAsItem.Click += (_, _) => SaveAsToFile();
        menu.Items.Add(saveItem);
        menu.Items.Add(saveAsItem);
        menu.Items.Add(new Separator());
        var openItem = new MenuItem { Header = "打开文件" };
        openItem.Click += (_, _) => OpenFile();
        menu.Items.Add(openItem);
        SaveBtn.ContextMenu = menu;
        SaveBtn.ContextMenu.IsOpen = true;
        e.Handled = true;
    }

    void SaveToFile()
    {
        if (!string.IsNullOrEmpty(_note.LastSavePath) && File.Exists(_note.LastSavePath))
        {
            File.WriteAllText(_note.LastSavePath, SaveFormatted(), System.Text.Encoding.UTF8);
        }
        else
        {
            SaveAsToFile();
        }
    }

    void SaveAsToFile()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "保存便签",
            Filter = "Text Files|*.txt|All Files|*.*",
            DefaultExt = ".txt",
            FileName = string.IsNullOrEmpty(_note.LastSavePath)
                ? (_note.Title ?? "Note") + ".txt"
                : Path.GetFileName(_note.LastSavePath),
            InitialDirectory = string.IsNullOrEmpty(_note.LastSavePath)
                ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                : Path.GetDirectoryName(_note.LastSavePath)
        };
        if (dlg.ShowDialog() == true)
        {
            _note.LastSavePath = dlg.FileName;
            File.WriteAllText(dlg.FileName, SaveFormatted(), System.Text.Encoding.UTF8);
            Save();
        }
    }

    void OpenFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "打开便签文件",
            Filter = "Text Files|*.txt|All Files|*.*",
            DefaultExt = ".txt",
            InitialDirectory = string.IsNullOrEmpty(_note.LastSavePath)
                ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                : Path.GetDirectoryName(_note.LastSavePath)
        };
        if (dlg.ShowDialog() == true)
        {
            string content = File.ReadAllText(dlg.FileName, System.Text.Encoding.UTF8);
            LoadFormatted(content);
            _note.LastSavePath = dlg.FileName;
            Save();
        }
    }

    string SaveFormatted()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var block in ContentBox.Document.Blocks)
        {
            if (block is Paragraph para)
            {
                foreach (var inline in para.Inlines)
                {
                    WriteInline(sb, inline);
                }
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }

    void WriteInline(System.Text.StringBuilder sb, Inline inline)
    {
        if (inline is Run run)
        {
            // Collect formatting from parent elements
            var formats = new List<string>();
            var parent = run.Parent;
            while (parent is Span span)
            {
                if (HasDecoration(span, TextDecorations.Underline))
                    formats.Add("underline");
                if (span.Style != null)
                {
                    // Check bold/italic from style setters
                    foreach (var setter in span.Style.Setters.OfType<Setter>())
                    {
                        if (setter.Property == Inline.FontWeightProperty && (FontWeight)setter.Value == FontWeights.Bold)
                            formats.Add("bold");
                        if (setter.Property == Inline.FontStyleProperty && (FontStyle)setter.Value == FontStyles.Italic)
                            formats.Add("italic");
                    }
                }
                parent = span.Parent;
            }

            // Get font size and color from the Run itself or inherited
            double fs = run.FontSize > 0 ? run.FontSize : ContentBox.FontSize;
            var fg = run.Foreground as SolidColorBrush;
            if (fg == null && parent is Paragraph p)
                fg = p.Foreground as SolidColorBrush;
            if (fg == null) fg = ContentBox.Foreground as SolidColorBrush;

            string colorHex = fg != null ? $"#{fg.Color.R:X2}{fg.Color.G:X2}{fg.Color.B:X2}" : "#E0E0E0";

            // Check if Run itself has TextDecorations
            if (run.TextDecorations != null && run.TextDecorations.Count > 0)
                if (!formats.Contains("underline")) formats.Add("underline");

            // Write format tags
            sb.Append($"[size={fs:F0}][color={colorHex}]");
            foreach (var f in formats.Distinct())
                sb.Append($"[{f}]");

            sb.Append(run.Text);

            foreach (var f in formats.Distinct().Reverse())
                sb.Append($"[/{f}]");
            sb.Append("[/color][/size]");
        }
        else if (inline is Span span2)
        {
            foreach (var child in span2.Inlines)
                WriteInline(sb, child);
        }
    }

    static bool HasDecoration(Span span, TextDecorationCollection decoration)
    {
        return span.TextDecorations != null && span.TextDecorations.Count > 0;
    }

    void LoadFormatted(string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            ContentBox.Document = new FlowDocument(new Paragraph(new Run("")));
            return;
        }

        var doc = new FlowDocument { LineHeight = double.NaN };
        var para = new Paragraph();
        int i = 0;

        while (i < content.Length)
        {
            if (content[i] == '\r' || content[i] == '\n')
            {
                // End of line → new paragraph
                if (para.Inlines.Count > 0 || content[i] == '\n')
                {
                    doc.Blocks.Add(para);
                    para = new Paragraph();
                }
                if (content[i] == '\r' && i + 1 < content.Length && content[i + 1] == '\n') i++;
                i++;
                continue;
            }

            if (content[i] == '[')
            {
                // Parse tag
                int close = content.IndexOf(']', i);
                if (close < 0) { i++; continue; }
                string tag = content[(i + 1)..close];

                if (tag.StartsWith("size="))
                {
                    if (double.TryParse(tag[5..], out var fs))
                        para.FontSize = fs;
                }
                else if (tag.StartsWith("color="))
                {
                    try
                    {
                        var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(tag[6..]);
                        para.Foreground = new SolidColorBrush(color);
                    }
                    catch { }
                }
                else if (tag == "bold" || tag == "italic" || tag == "underline")
                {
                    // Find matching close tag
                    string closeTag = $"[/{tag}]";
                    int closeIdx = content.IndexOf(closeTag, close + 1, StringComparison.Ordinal);
                    string inner;
                    if (closeIdx > 0)
                    {
                        inner = content[(close + 1)..closeIdx];
                        i = closeIdx + closeTag.Length;
                    }
                    else
                    {
                        inner = content[(close + 1)..];
                        i = content.Length;
                    }

                    var run = new Run(inner);
                    if (tag == "bold") run.FontWeight = FontWeights.Bold;
                    if (tag == "italic") run.FontStyle = FontStyles.Italic;
                    if (tag == "underline") run.TextDecorations = TextDecorations.Underline;
                    run.FontSize = para.FontSize > 0 ? para.FontSize : ContentBox.FontSize;
                    if (para.Foreground != null) run.Foreground = para.Foreground;
                    para.Inlines.Add(run);
                    continue;
                }
                i = close + 1;
            }
            else
            {
                // Plain text → accumulate until next tag or newline
                int next = content.IndexOf('[', i);
                if (next < 0) next = content.Length;
                int end = next;
                while (end > i && (content[end - 1] == '\r' || content[end - 1] == '\n')) end--;
                string text = content[i..end];
                if (text.Length > 0)
                {
                    var run = new Run(text);
                    run.FontSize = para.FontSize > 0 ? para.FontSize : ContentBox.FontSize;
                    if (para.Foreground != null) run.Foreground = para.Foreground;
                    para.Inlines.Add(run);
                }
                i = next;
            }
        }

        if (para.Inlines.Count > 0)
            doc.Blocks.Add(para);

        ContentBox.Document = doc;
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
        // Save dimensions before any reference swap can occur
        var savedW = _note.Width; var savedH = _note.Height;
        if (!IsVisible) Show();
        ApplyAcrylic();
        Left = _note.X; Top = _note.Y;
        MainContent.Visibility = Visibility.Visible; RestoreButton.Visibility = Visibility.Collapsed;
        MinWidth = 180; MinHeight = 120;
        _note.IsVisible = true; NativeMethods.PinToDesktop(this);
        NativeMethods.SetRoundedCorners(this, 10);
        _notesService.UpdateNote(_note);
        // Restore dimensions AFTER UpdateNote (which may trigger OnNotesChanged / reference swap)
        Width = savedW; Height = savedH;
        Topmost = true;
        Activate();
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
            RestoreButton.ReleaseMouseCapture();
            try { DragMove(); } catch { }
            _note.X = Left; _note.Y = Top;
        }
    }

    void Restore_MouseUp(object s, MouseButtonEventArgs e)
    {
        RestoreButton.ReleaseMouseCapture();
        ShowNote();
    }

    void Restore_Enter(object s, MouseEventArgs e) { RestoreButton.Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x2A, 0x2A, 0x4E)); }
    void Restore_Leave(object s, MouseEventArgs e) { RestoreButton.Background = new SolidColorBrush(Color.FromArgb(0xDD, 0x1A, 0x1A, 0x2E)); }

    // ── Acrylic / frosted glass ──

    void ApplyAcrylic()
    {
        var config = _notesService.GetConfig();
        string fillColorStr = _note.UseGlobalAppearance ? config.GlobalFillColor : _note.FillColor;
        string borderColorStr = _note.UseGlobalAppearance ? config.GlobalBorderColor : _note.BorderColor;
        double borderThickness = _note.UseGlobalAppearance ? config.GlobalBorderThickness : _note.BorderThickness;

        if (_note.EnableAcrylic)
        {
            AcrylicHelper.EnableBlur(this, _note.GlassBlurAmount, _note.GlassTintOpacity,
                _note.GlassTintLuminosity, _note.GlassColorMode);
            try
            {
                // Use fillColor directly — its ARGB alpha controls transparency
                var fillColor = (Color)ColorConverter.ConvertFromString(fillColorStr)!;
                NoteBorder.Background = new SolidColorBrush(fillColor);
            }
            catch { }
        }
        else
        {
            try
            {
                var fillColor = (Color)ColorConverter.ConvertFromString(fillColorStr)!;
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

    void ApplyTitleBar()
    {
        try
        {
            // Apply title bar fill with ARGB alpha controlling background transparency
            var tbColor = (Color)ColorConverter.ConvertFromString(_note.TitleBarFillColor);
            TitleBarBorder.Background = new SolidColorBrush(tbColor);
            // Apply title text color
            if (!string.IsNullOrEmpty(_note.TitleTextColor))
            {
                var tc = (Color)ColorConverter.ConvertFromString(_note.TitleTextColor);
                TitleBox.Foreground = new SolidColorBrush(tc);
                TitleBox.CaretBrush = new SolidColorBrush(tc);
            }
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

    /// <summary>Refresh all visual styles from the current _note model (for live preview).</summary>
    public void RefreshAppearance()
    {
        if (MainContent.Visibility == Visibility.Visible)
            ApplyAcrylic();
        ApplyBackgroundImage();
        ApplyStyle();
        ApplyTitleBar();
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

    void PinBtn_Enter(object s, MouseEventArgs e)
    {
        PinBtn.Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
        PinBtn.Foreground = Brushes.White;
    }

    void PinBtn_Leave(object s, MouseEventArgs e)
    {
        PinBtn.Background = Brushes.Transparent;
        PinBtn.Foreground = _vm.PinnedTop
            ? new SolidColorBrush(Color.FromRgb(0x7C, 0x3A, 0xED))
            : new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));
    }

    void SaveBtn_Enter(object s, MouseEventArgs e)
    {
        SaveBtn.Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
        SaveBtn.Foreground = Brushes.White;
    }

    void SaveBtn_Leave(object s, MouseEventArgs e)
    {
        SaveBtn.Background = Brushes.Transparent;
        SaveBtn.Foreground = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));
    }

    void PinBtn_Click(object s, RoutedEventArgs e)
    {
        _vm.PinnedTop = !_vm.PinnedTop;
        Topmost = _vm.PinnedTop;
        if (!_vm.PinnedTop) NativeMethods.PinToDesktop(this);
        PinBtn.Foreground = _vm.PinnedTop
            ? new SolidColorBrush(Color.FromRgb(0x7C, 0x3A, 0xED))
            : new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));
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
                var sel = ContentBox.Selection;
                if (sel != null && !sel.IsEmpty)
                    sel.ApplyPropertyValue(TextElement.FontSizeProperty, fs);
                else
                    ContentBox.FontSize = fs;
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
            _pendingUnderline = !_pendingUnderline;
            if (_pendingUnderline)
                ContentBox.Selection.ApplyPropertyValue(Inline.TextDecorationsProperty, TextDecorations.Underline);
            ApplyUnderlinePadding();
            UpdateFormatButtons();
            return;
        }
        var currentDeco = sel.GetPropertyValue(Inline.TextDecorationsProperty);
        var newDeco = (currentDeco is TextDecorationCollection td && td.Count > 0)
            ? null : TextDecorations.Underline;
        sel.ApplyPropertyValue(Inline.TextDecorationsProperty, newDeco);
        _pendingUnderline = newDeco != null;
        ApplyUnderlinePadding();
        UpdateFormatButtons();
    }

    void ApplyUnderlinePadding()
    {
        // Add bottom padding to paragraphs when underline is active,
        // creating a gap between text and underline (matching reference design).
        var pad = _pendingUnderline ? new Thickness(0, 0, 0, 6) : new Thickness(0);
        foreach (var block in ContentBox.Document.Blocks)
        {
            if (block is Paragraph p)
                p.Padding = pad;
        }
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
                var brush = new SolidColorBrush(color);
                var sel = ContentBox.Selection;
                if (sel != null && !sel.IsEmpty)
                    sel.ApplyPropertyValue(Inline.ForegroundProperty, brush);
                else
                    ContentBox.Foreground = brush;
                FontColorBtn.Foreground = brush;
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
                var sel = ContentBox.Selection;
                if (sel != null && !sel.IsEmpty)
                    sel.ApplyPropertyValue(Inline.ForegroundProperty, brush);
                else
                    ContentBox.Foreground = brush;
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

    void ContentBox_TextInput(object s, TextCompositionEventArgs e)
    {
        // Apply underline at caret BEFORE text is inserted — new text inherits it
        if (_pendingUnderline && ContentBox.Selection != null && ContentBox.Selection.IsEmpty)
        {
            ContentBox.Selection.ApplyPropertyValue(Inline.TextDecorationsProperty, TextDecorations.Underline);
        }
    }

    void ContentBox_TextChanged(object s, TextChangedEventArgs e)
    {
        if (_initializing || !_pendingUnderline) return;
        // Handle paste: apply underline to pasted ranges
        foreach (var change in e.Changes)
        {
            if (change.AddedLength > 0)
            {
                try
                {
                    var start = ContentBox.Document.ContentStart;
                    for (int i = 0; i < change.Offset; i++)
                        start = start.GetNextInsertionPosition(LogicalDirection.Forward);
                    var end = start;
                    for (int i = 0; i < change.AddedLength; i++)
                        end = end.GetNextInsertionPosition(LogicalDirection.Forward);
                    if (start != null && end != null)
                        new TextRange(start, end).ApplyPropertyValue(Inline.TextDecorationsProperty, TextDecorations.Underline);
                }
                catch { }
            }
        }
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
            UnderlineBtn.Background = _pendingUnderline
                ? new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)) : Brushes.Transparent;
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
