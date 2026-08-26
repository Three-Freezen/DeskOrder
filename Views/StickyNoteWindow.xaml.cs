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
using System.Windows.Media.Imaging;
using DesktopZones.Helpers;
using DesktopZones.Models;
using DesktopZones.Services;
using DesktopZones.ViewModels;
using DesktopZones.Views.Components;

namespace DesktopZones.Views;

public partial class StickyNoteWindow : Window
{
    // ponytail: frozen hover brushes — same color on every mouse-over.
    private static readonly SolidColorBrush RestoreHoverBrush = Freeze(new(Color.FromArgb(0xFF, 0x2A, 0x2A, 0x4E)));
    private static readonly SolidColorBrush RestoreIdleBrush  = Freeze(new(Color.FromArgb(0xDD, 0x1A, 0x1A, 0x2E)));
    private static readonly SolidColorBrush PinHoverBrush     = Freeze(new(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)));
    private static readonly SolidColorBrush LockHoverBrush    = Freeze(new(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)));
    static SolidColorBrush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    private StickyNote _note;
    private readonly NotesService _notesService;
    private StickyNoteViewModel _vm;
    private readonly LocalizationService _loc = LocalizationService.Instance;
    private bool _initializing = true;
    private bool _pendingUnderline;
    private Point _restoreDown;
    public Action? OnStateChanged { get; set; }
    // ponytail: cached button-color brush for title bar buttons. Set by ApplyTitleBar from
    // _note.ButtonColor; hover/click handlers read it so the hover→leave cycle doesn't clobber it.
    private SolidColorBrush? _buttonBrush;
    private HoverExpandBehavior? _hover;
    private SnapDrag? _snapDrag;
    private SnapResize? _snapResize;

    public StickyNoteWindow(StickyNote note, NotesService notesService)
    {
        InitializeComponent();
        ComboBoxHelper.ApplyDarkTheme(FontSizeCombo);
        _note = note;
        _notesService = notesService;
        _vm = new StickyNoteViewModel(note);
        // ponytail: VM starts at default false — pull persisted lock state from model so a
        // reloaded locked note shows 🔒, not 🔓. Matches ZoneWindow ctor pattern.
        _vm.IsLocked = note.IsLocked;
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
        SizeChanged += (_, _) => { if (MainContent.Visibility == Visibility.Visible) { _note.Width = Width; _note.Height = Height; NativeMethods.UpdateRoundedCorners(this, _note.CornerRadius); } };

        Loaded += OnLoad;
        _notesService.NotesChanged += OnNotesChanged;
        // ponytail: subscribe to LockChanged so management UI (or any other source) flipping
        // this note's lock state immediately syncs the open window.
        _notesService.LockChanged += OnServiceLockChanged;

        ApplyLockState();
        _initializing = false;
        // ponytail: hover-expand (Task 14d). Wired after InitializeComponent and
        // before any user interaction can occur.
        _hover = new HoverExpandBehavior(this, RestoreButton, MainContent, null,
            () => _note.HoverExpandAnimation,
            () => _note.HoverExpandSpeed,
            () => _note.HoverExpandOrigin,
            () => _note.HoverAutoExpand)
        { IsEnabled = _note.EnableRestoreButton };
        // ponytail 2026-08-25: pick up live changes from the 动效设置 dialog
        // (property panel) — mirrors ZoneWindow's subscription.
        _note.HoverExpandSettingsChanged += OnHoverExpandSettingsChanged;
        // ponytail: ghost-glass fix — see ZoneWindow. Acrylic follows the expand state so a
        // collapsed note shows ONLY the RestoreButton (no full-window glass rectangle).
        _hover.Expanded += ApplyAcrylic;
        _hover.Collapsed += () => AcrylicHelper.DisableBlur(this);
        // ponytail: bug fix — see ZoneWindow ctor. ShowNoteFromService / OpenNoteWindow
        // call window.Show() without going through the equivalent of ShowZone, so
        // SnapToExpanded never runs.
        if (_note.IsVisible) _hover.SnapToExpanded();

        // ponytail: 自适应对齐 — 替换 DragMove 的手动拖拽循环。
        _snapDrag = new SnapDrag(this);
        _snapResize = new SnapResize(this);
    }

    void OnHoverExpandSettingsChanged()
    {
        // Re-apply origin + snap baseline for the current kind without forcing
        // a state change (mirrors ZoneWindow.OnHoverExpandSettingsChanged).
        _hover?.SetEnabled(_note.EnableRestoreButton);
    }

    void OnNotesChanged()
    {
        if (!IsLoaded) return;
        var latest = _notesService.Notes.FirstOrDefault(n => n.Id == _note.Id);
        if (latest != null) _note = latest;
        // ponytail: ghost-stamp lock — see ZoneWindow.OnZonesChanged for full rationale.
        // 2026-08-23: only stamp when the behavior thinks it is still EXPANDED — during
        // a legitimate animated collapse this used to snap the animation away instantly
        // (see ClockWidget.OnClocksChanged); let the animation finish instead.
        if (!_note.IsVisible && _hover != null && _hover.IsExpanded
            && !_hover.IsCollapsePending
            && MainContent.Visibility == Visibility.Visible)
            _hover.SnapToCollapsed();
        // ponytail: pull refreshed lock state from model (e.g. when another window unlocked this note)
        _vm.IsLocked = _note.IsLocked;
        // ponytail 2026-08-25: 便签设置 live-sync — title text (便签名称) and
        // 置顶 state flow from the property panel through UpdateNote; re-apply
        // them so edits are visible immediately.
        _vm.PinnedTop = _note.PinnedTop;
        if (TitleBox != null) TitleBox.Text = _note.Title;
        // ponytail 2026-08-26: re-pin / Topmost only while the window is still up.
        // OnClosed → UpdateNote → NotesChanged re-enters this handler while
        // WmDestroy is tearing the window down; PinToDesktop would call
        // WindowInteropHelper.EnsureHandle → "关闭窗口后，无法设置可见性…"
        // InvalidOperationException (crash when 全部隐藏 closes note windows).
        if (IsVisible)
        {
            if (_note.PinnedTop) Topmost = true;
            else if (!_note.IsLocked) NativeMethods.PinToDesktop(this);
        }
        if (MainContent.Visibility == Visibility.Visible)
            ApplyAcrylic();
        ApplyBackgroundImage();
        ApplyStyle();
        ApplyTitleBar();
        RefreshTextColorAdaptive();
        ApplyLockState();
        _hover?.SetEnabled(_note.EnableRestoreButton);
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
        var saveItem = new MenuItem { Header = _loc["Note.Save"] };
        saveItem.Click += (_, _) => SaveToFile();
        var saveAsItem = new MenuItem { Header = _loc["Note.SaveAs"] };
        saveAsItem.Click += (_, _) => SaveAsToFile();
        menu.Items.Add(saveItem);
        menu.Items.Add(saveAsItem);
        menu.Items.Add(new Separator());
        var openItem = new MenuItem { Header = _loc["Note.OpenFile"] };
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
            try
            {
                File.WriteAllText(_note.LastSavePath, SaveFormatted(), System.Text.Encoding.UTF8);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"{LocalizationService.Instance["Note.SaveFailed"]}\n{ex.Message}",
                    LocalizationService.Instance["Note.SaveFailed.Title"],
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
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
            Title = _loc["Note.SaveNote"],
            Filter = $"{_loc["Filter.Txt"]}|*.txt|{_loc["Filter.All"]}|*.*",
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
            try
            {
                File.WriteAllText(dlg.FileName, SaveFormatted(), System.Text.Encoding.UTF8);
                _note.LastSavePath = dlg.FileName; // ponytail: only on success
                Save();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"{LocalizationService.Instance["Note.SaveFailed"]}\n{ex.Message}",
                    LocalizationService.Instance["Note.SaveFailed.Title"],
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    void OpenFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = _loc["Note.OpenNote"],
            Filter = $"{_loc["Filter.Txt"]}|*.txt|{_loc["Filter.All"]}|*.*",
            DefaultExt = ".txt",
            InitialDirectory = string.IsNullOrEmpty(_note.LastSavePath)
                ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                : Path.GetDirectoryName(_note.LastSavePath)
        };
        if (dlg.ShowDialog() == true)
        {
            try
            {
                string content = File.ReadAllText(dlg.FileName, System.Text.Encoding.UTF8);
                LoadFormatted(content);
                _note.LastSavePath = dlg.FileName;
                Save();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"{LocalizationService.Instance["Note.OpenFailed"]}\n{ex.Message}",
                    LocalizationService.Instance["Note.OpenFailed.Title"],
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
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
        if (_vm?.IsLocked != true) NativeMethods.PinToDesktop(this);
        NativeMethods.SetToolWindow(this);
        NativeMethods.DisableDwmFrameShadow(this);
        ApplyAcrylic();
        ApplyBackgroundImage();
        NativeMethods.SetRoundedCorners(this, _note.CornerRadius);
        NativeMethods.UpdateRoundedCorners(this, _note.CornerRadius);
        if (!_note.IsVisible) ApplyHidden();
    }

    // ── Show / Hide (minimize-restore) ──

    public void ShowNote(double waveDelayMs = 0)
    {
#if DEBUG
        DzTrace.Log($"[StickyNoteWindow] ShowNote(wave={waveDelayMs}) ENTRY winVisible={IsVisible} content={MainContent.Visibility} btn={RestoreButton.Visibility} hoverExpanded={_hover?.IsExpanded} modelVisible={_note.IsVisible} size={Width}x{Height}");
#endif
        // Save dimensions before any reference swap can occur
        var savedW = _note.Width; var savedH = _note.Height;
        if (!IsVisible) Show();
        Left = _note.X; Top = _note.Y;
        if (waveDelayMs > 0)
        {
            // ponytail: batch "Show All" wave — start collapsed and play the note's own
            // configured animation at its stagger slot (see ZoneWindow.ShowZone).
            MainContent.Visibility = Visibility.Visible; RestoreButton.Visibility = Visibility.Collapsed;
            _hover?.SnapToCollapsed();
            RestoreButton.Visibility = Visibility.Collapsed; // no button flash during the delay
            _hover?.ShowAfterDelay(waveDelayMs);
        }
        else
        {
            MainContent.Visibility = Visibility.Visible; RestoreButton.Visibility = Visibility.Collapsed;
            _hover?.SnapToExpanded();
        }
        // ponytail: ghost-glass fix — re-apply acrylic AFTER SnapToExpanded so the
        // expanded-state gate sees IsExpanded == true and re-enables liquid glass when
        // showing from the collapsed button.
        ApplyAcrylic();
        MinWidth = 180; MinHeight = 120;
        _note.IsVisible = true; if (_vm?.IsLocked != true) NativeMethods.PinToDesktop(this);
        NativeMethods.SetRoundedCorners(this, _note.CornerRadius);
        _notesService.UpdateNote(_note);
        // Restore dimensions AFTER UpdateNote (which may trigger OnNotesChanged / reference swap)
        Width = savedW; Height = savedH;
        // ponytail: locked notes stay below app windows — Topmost would re-pin above them and
        // defeat PinBelowProgman. PinnedTop notes still get Topmost via the constructor branch.
        if (!_vm.IsLocked) Topmost = true;
        Activate();
        OnStateChanged?.Invoke();
    }

    /// <summary>
    /// Batch-wave entrance for a freshly created window: collapse the just-shown
    /// content and play the note's own expand animation at the stagger slot.
    /// </summary>
    public void PlayEntranceAnimation(double waveDelayMs)
    {
        if (waveDelayMs <= 0) return;
        _hover?.SnapToCollapsed();
        RestoreButton.Visibility = Visibility.Collapsed;
        _hover?.ShowAfterDelay(waveDelayMs);
    }

    public void HideNote(double waveDelayMs = 0)
    {
#if DEBUG
        DzTrace.Log($"[StickyNoteWindow] HideNote(wave={waveDelayMs}) ENTRY winVisible={IsVisible} content={MainContent.Visibility} btn={RestoreButton.Visibility} hoverExpanded={_hover?.IsExpanded} modelVisible={_note.IsVisible} restoreEnabled={_note.EnableRestoreButton} size={Width}x{Height}");
#endif
        _note.X = Left; _note.Y = Top; _note.Width = Width; _note.Height = Height;
        NativeMethods.DisableRoundedCorners(this);
        if (!_note.EnableRestoreButton)
        {
            if (waveDelayMs > 0)
            {
                // ponytail: batch "Minimize All" wave — play the note's own collapse
                // animation first (staggered), then finalize the full hide.
                _hover?.CollapseAfterDelay(waveDelayMs, onComplete: () =>
                {
                    AcrylicHelper.DisableBlur(this);
                    _hover?.SnapToFullHidden();
                    MainContent.Visibility = Visibility.Collapsed;
                    MinWidth = 36; MinHeight = 36;
                    Width = 36; Height = 36;
                    Hide();
                });
            }
            else
            {
                // ponytail: 2026-08-23 — SnapToFullHidden resets the hover state so no
                // later ApplyAcrylic call can re-enable the DWM glass on the hidden
                // window (ghost "empty liquid glass" bug). See ZoneWindow.HideZone.
                AcrylicHelper.DisableBlur(this);
                _hover?.SnapToFullHidden();
                MainContent.Visibility = Visibility.Collapsed;
                MinWidth = 36; MinHeight = 36;
                Width = 36; Height = 36;
                Hide();
            }
        }
        else
        {
            // ponytail: minimized — let HoverExpandBehavior handle visibility/scale
            AcrylicHelper.DisableBlur(this);
            if (_vm?.IsLocked != true) NativeMethods.PinToDesktop(this);
            if (waveDelayMs > 0)
                _hover?.CollapseAfterDelay(waveDelayMs, null);
            else
                _hover?.CollapseAnimated();
        }
        _note.IsVisible = false;
        _notesService.UpdateNote(_note);
        OnStateChanged?.Invoke();
#if DEBUG
        DzTrace.Log($"[StickyNoteWindow] HideNote DONE winVisible={IsVisible} content={MainContent.Visibility} btn={RestoreButton.Visibility} hoverExpanded={_hover?.IsExpanded} size={Width}x{Height}");
#endif
    }

    void ApplyHidden()
    {
        AcrylicHelper.DisableBlur(this);
        NativeMethods.DisableRoundedCorners(this);
        if (!_note.EnableRestoreButton)
        {
            // ponytail: 2026-08-23 — see HideNote for the SnapToFullHidden rationale.
            _hover?.SnapToFullHidden();
            MainContent.Visibility = Visibility.Collapsed;
            MinWidth = 36; MinHeight = 36;
            Width = 36; Height = 36;
            Hide();
        }
        else
        {
            // ponytail: 2026-08-23 — restore the full window size after a previous
            // full-hide shrank it to 36×36 (collapsed mode keeps the window at full
            // size; mirror the ctor's own minimums).
            MinWidth = 180; MinHeight = 120;
            Width = _note.Width < 200 ? 260 : _note.Width;
            Height = _note.Height < 150 ? 200 : _note.Height;
            // ponytail: minimized — window stays at full size, content collapses
            _hover?.SnapToCollapsed();
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
            _snapDrag?.Start(e, () =>
            {
                if (_vm?.IsLocked != true) NativeMethods.PinToDesktop(this);
                _note.X = Left; _note.Y = Top;
            });
        }
    }

    void Restore_MouseUp(object s, MouseButtonEventArgs e)
    {
        RestoreButton.ReleaseMouseCapture();
        // ponytail: 2026-08-23 — mark the model visible before UpdateNote fires
        // NotesChanged so OnNotesChanged's ghost-stamp lock doesn't collapse the window
        // right back mid-expand (see ClockWidget.Restore_MouseUp for the full rationale;
        // without the persisted IsVisible=true, ANY later note edit would collapse this
        // expanded note again via the collection's stale IsVisible=false).
        _note.IsVisible = true;
        _hover?.ExpandAnimated(permanent: true);
        _notesService.UpdateNote(_note);
    }

    void Restore_Enter(object s, MouseEventArgs e) { RestoreButton.Background = RestoreHoverBrush; }
    void Restore_Leave(object s, MouseEventArgs e) { RestoreButton.Background = RestoreIdleBrush; }

    // ── Acrylic / frosted glass ──

    void ApplyAcrylic()
    {
        string fillColorStr = _note.FillColor;
        string borderColorStr = _note.BorderColor;
        double borderThickness = _note.BorderThickness;

        // ponytail: ghost-glass fix — see ZoneWindow. A collapsed note keeps its full-size
        // window, so enabling blur here would paint the tint across the whole window.
        // Only enable while content is expanded; collapsed / acrylic-off → disable.
        bool expanded = _hover?.IsExpanded ?? false;
        if (_note.EnableAcrylic && expanded)
        {
            var blurResult = AcrylicHelper.EnableBlur(this, _note.GlassBlurAmount, _note.GlassTintOpacity,
                _note.GlassTintLuminosity, _note.GlassColorMode);
            if (!blurResult.Success)
                System.Diagnostics.Debug.WriteLine($"[StickyNoteWindow] EnableBlur failed: {blurResult.Error}");
            // ponytail 2026-08-25: liquid-glass chromatic border branch — mirrors
            // ClockWidget.ApplyAcrylic (the only component that had it).
            if (_note.EnableLiquidGlass)
            {
                NoteBorder.BorderBrush = AcrylicHelper.CreateChromaticBorder();
                NoteBorder.BorderThickness = new Thickness(Math.Max(1.0, borderThickness));
            }
            try
            {
                // Use fillColor directly — its ARGB alpha controls transparency
                var fillColor = (Color)ColorConverter.ConvertFromString(fillColorStr)!;
                BodyFillRect.Fill = new SolidColorBrush(fillColor);
            }
            catch { }
        }
        else
        {
            // ponytail: ghost-glass fix — this branch previously never disabled blur, so
            // toggling acrylic off (or collapsing) could leave the full-window blur behind.
            AcrylicHelper.DisableBlur(this);
            try
            {
                var fillColor = (Color)ColorConverter.ConvertFromString(fillColorStr)!;
                BodyFillRect.Fill = new SolidColorBrush(fillColor);
            }
            catch { }
        }
        ApplyFillGeometry();
    }

    void ApplyFillGeometry()
    {
        if (BodyFillRect == null) return;
        if (_note.TitleBarFillIndependent)
        {
            // Two-row title bar (title + formatting toolbar) — body fill starts below both.
            Grid.SetRow(BodyFillRect, 2);
            Grid.SetRowSpan(BodyFillRect, 1);
        }
        else
        {
            Grid.SetRow(BodyFillRect, 0);
            Grid.SetRowSpan(BodyFillRect, 3);
        }
    }

    /// <summary>Two-row title bar height: 28px title row + the formatting toolbar row.</summary>
    double TitleBarLayerHeight() => 28 + (ToolbarBorder?.ActualHeight > 0 ? ToolbarBorder.ActualHeight : 28);

    void ApplyStyle()
    {
        try
        {
            var bc = (Color)ColorConverter.ConvertFromString(_note.BorderColor);
            NoteBorder.BorderBrush = new SolidColorBrush(bc);
            NoteBorder.BorderThickness = new Thickness(_note.BorderThickness);
        }
        catch { }

        // ponytail 2026-08-26: 圆角/尖角 switch — corner elements + DWM lockstep.
        int r = _note.CornerRadius;
        MainContent.CornerRadius = new CornerRadius(r);
        NoteBorder.CornerRadius = new CornerRadius(r);
        TitleBarBorder.CornerRadius = new CornerRadius(r, r, 0, 0);
        if (BodyFillRect != null)
            BodyFillRect.RadiusX = BodyFillRect.RadiusY = _note.TitleBarFillIndependent ? 0 : r;
        if (System.Windows.PresentationSource.FromVisual(this) != null)
            NativeMethods.SetRoundedCorners(this, r);
    }

    void ApplyTitleBar()
    {
        try
        {
            // Two-row title bar: title row keeps the fill, the formatting toolbar row is a
            // built-in 50%-alpha variant (the original #20/#10 XAML distinction, restored).
            var tbColor = (Color)ColorConverter.ConvertFromString(_note.TitleBarFillColor);
            TitleBarBorder.Background = new SolidColorBrush(tbColor);
            if (ToolbarBorder != null)
                ToolbarBorder.Background = new SolidColorBrush(Color.FromArgb((byte)(tbColor.A / 2), tbColor.R, tbColor.G, tbColor.B));

            // TitleBox 始终用用户设置的 TitleTextColor。
            if (!string.IsNullOrEmpty(_note.TitleTextColor))
            {
                var tc = (Color)ColorConverter.ConvertFromString(_note.TitleTextColor);
                var titleBrush = new SolidColorBrush(tc);
                TitleBox.Foreground = titleBrush;
                TitleBox.CaretBrush = titleBrush;
            }

            // Buttons — 按钮颜色 (title bar + formatting toolbar + restore).
            // FontColorBtn keeps its own selection-color logic and is NOT touched here.
            SolidColorBrush buttonBrush;
            try { buttonBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_note.ButtonColor)!); }
            catch { buttonBrush = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)); }
            _buttonBrush = buttonBrush; // cache for Leave/Click handlers
            if (SaveBtn != null) SaveBtn.Foreground = buttonBrush;
            if (HideBtn != null) HideBtn.Foreground = buttonBrush;
            // PinBtn: pinned color wins when pinned, otherwise button color
            if (PinBtn != null) PinBtn.Foreground = _vm.PinnedTop
                ? new SolidColorBrush(Color.FromRgb(0x7C, 0x3A, 0xED))
                : buttonBrush;
            if (LockBtn != null) LockBtn.Foreground = buttonBrush;
            if (BoldBtn != null) BoldBtn.Foreground = buttonBrush;
            if (ItalicBtn != null) ItalicBtn.Foreground = buttonBrush;
            if (UnderlineBtn != null) UnderlineBtn.Foreground = buttonBrush;
            if (RestoreIconChar != null) RestoreIconChar.Foreground = buttonBrush;
            // 字号数字锁定白色。
            if (FontSizeCombo != null)
                FontSizeCombo.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));

            // 按钮透明度 — 所有按钮 chrome 统一走 ControlOpacity。
            var ctl = Math.Max(0.05, _note.ControlOpacity / 100.0);
            if (SaveBtn != null) SaveBtn.Opacity = ctl;
            if (PinBtn != null) PinBtn.Opacity = ctl;
            if (HideBtn != null) HideBtn.Opacity = ctl;
            if (LockBtn != null) LockBtn.Opacity = ctl;
            if (BoldBtn != null) BoldBtn.Opacity = ctl;
            if (ItalicBtn != null) ItalicBtn.Opacity = ctl;
            if (UnderlineBtn != null) UnderlineBtn.Opacity = ctl;
            if (RestoreIconChar != null) RestoreIconChar.Opacity = ctl;
        }
        catch { }
    }

    /// <summary>Re-apply the fixed title-bar (2-row) + button colors.</summary>
    public void RefreshTextColorAdaptive()
    {
        ApplyTitleBar();
    }

    // ── Background image ──

    void ApplyBackgroundImage()
    {
        // 标题栏独立填充：背景图与 BodyFillRect 一样不铺到两行标题栏下方（顶部裁剪）。
        if (NoteBgBorder != null)
        {
            if (_note.TitleBarFillIndependent)
            {
                Grid.SetRow(NoteBgBorder, 2);
                Grid.SetRowSpan(NoteBgBorder, 1);
            }
            else
            {
                Grid.SetRow(NoteBgBorder, 0);
                Grid.SetRowSpan(NoteBgBorder, 3);
            }
        }
        double clipTop = _note.TitleBarFillIndependent ? TitleBarLayerHeight() : 0;
        try
        {
            if (!string.IsNullOrEmpty(_note.BackgroundImagePath) && System.IO.File.Exists(_note.BackgroundImagePath))
            {
                var bi = new System.Windows.Media.Imaging.BitmapImage();
                bi.BeginInit();
                bi.UriSource = new Uri(_note.BackgroundImagePath);
                bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bi.DecodePixelWidth = 1920;
                bi.EndInit();
                bi.Freeze();
                NoteBgImage.Source = bi;
                NoteBgImage.Stretch = Stretch.UniformToFill;
                double nw = Width;
                double nh = Height;

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
                    zoneCenterY - imgCenterY + oy - clipTop, 0, 0);
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
    public void RefreshAppearance(StickyNote? note = null)
    {
        if (note != null) _note = note;
        // ponytail: ApplyAcrylic guards on IntPtr.Zero internally — safe to run regardless
        // of MainContent visibility so live preview reaches the widget even when hidden.
        ApplyAcrylic();
        ApplyBackgroundImage();
        ApplyStyle();
        ApplyTitleBar();
        // ponytail: BP-A fix — RefreshAppearance was missing the body content color refresh.
        // Without this, FillColor changes update the background but the toolbar buttons (Bold,
        // Italic, Underline, FontColor, FontSizeCombo, RestoreIconChar) keep their old colors.
        RefreshTextColorAdaptive();
        _hover?.SetEnabled(_note.EnableRestoreButton);
    }

    // ── Title bar ──

    void TitleBar_Drag(object s, MouseButtonEventArgs e)
    {
        if (_vm?.IsLocked == true) return;
        _snapDrag?.Start(e, () =>
        {
            if (_vm?.IsLocked != true) NativeMethods.PinToDesktop(this);
            _note.X = Left; _note.Y = Top;
        });
    }

    // ponytail: 2026-08-23 — the note's title bar has no bare grab area (the title
    // TextBox stretches across the space between the left margin and the four
    // buttons, so at the default 260 px width every title-bar pixel belongs to an
    // interactive element) and this window-level preview handler had been emptied
    // ("drill-through removed") — leaving the note completely undraggable. Restore
    // a drill-through drag on every non-interactive surface (background, padding,
    // title-bar free area); editable text, buttons, combos and the resize grips keep
    // their own clicks, and a locked note stays unmovable.
    void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm?.IsLocked == true) return;
        // The RestoreButton drives its own press/drag/expand — never steal it.
        if (RestoreButton.Visibility == Visibility.Visible) return;
        var src = e.OriginalSource as System.Windows.DependencyObject;
        while (src != null && src != sender)
        {
            if (src is System.Windows.Controls.Button
                || src is System.Windows.Controls.Primitives.TextBoxBase
                || src is System.Windows.Controls.ComboBox
                || (src is FrameworkElement fe && fe.Tag is string tag
                    && (tag == "TL" || tag == "TR" || tag == "BL" || tag == "BR")))
                return;
            // 正文是 RichTextBox:点击内容时 OriginalSource 可能是 Paragraph/Run
            // (FrameworkContentElement,不是 Visual),VisualTreeHelper.GetParent
            // 会抛 InvalidOperationException —— 内容元素改走逻辑树,一样能爬到
            // TextBoxBase(TextBoxBase 检查在循环顶部,先命中再向上)。
            if (src is System.Windows.FrameworkContentElement fce)
                src = LogicalTreeHelper.GetParent(fce);
            else if (src is System.Windows.Media.Visual)
                src = System.Windows.Media.VisualTreeHelper.GetParent(src);
            else
                break;
        }
        _snapDrag?.Start(e, () =>
        {
            if (_vm?.IsLocked != true) NativeMethods.PinToDesktop(this);
            _note.X = Left; _note.Y = Top;
        });
        // Prevent the bubbling TitleBar_Drag from running a second move loop.
        e.Handled = true;
    }

    void ResizeGrip_Down(object s, MouseButtonEventArgs e)
    {
        if (_vm?.IsLocked == true) { e.Handled = true; return; }
        if (s is not Border g || g.Tag is not string tag) return;
        bool left = tag == "TL" || tag == "BL";
        bool top = tag == "TL" || tag == "TR";
        _snapResize?.Start(e, left, top, !left, !top, 180, 120);
        e.Handled = true;
    }

    void PinBtn_Enter(object s, MouseEventArgs e)
    {
        PinBtn.Background = PinHoverBrush;
        PinBtn.Foreground = Brushes.White;
    }

    void PinBtn_Leave(object s, MouseEventArgs e)
    {
        PinBtn.Background = Brushes.Transparent;
        // ponytail: prefer cached button-color brush; pinned color wins when pinned
        if (_vm.PinnedTop)
            PinBtn.Foreground = new SolidColorBrush(Color.FromRgb(0x7C, 0x3A, 0xED));
        else
            PinBtn.Foreground = _buttonBrush
                ?? new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));
    }

    void SaveBtn_Enter(object s, MouseEventArgs e)
    {
        SaveBtn.Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
        SaveBtn.Foreground = Brushes.White;
    }

    void SaveBtn_Leave(object s, MouseEventArgs e)
    {
        SaveBtn.Background = Brushes.Transparent;
        // ponytail: prefer cached button-color brush so hover→leave cycle doesn't clobber it
        SaveBtn.Foreground = _buttonBrush
            ?? new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));
    }

    void PinBtn_Click(object s, RoutedEventArgs e)
    {
        _vm.PinnedTop = !_vm.PinnedTop;
        Topmost = _vm.PinnedTop;
        if (!_vm.PinnedTop && _vm.IsLocked != true) NativeMethods.PinToDesktop(this);
        // ponytail: same logic as PinBtn_Leave — pinned color wins when pinned, else button color
        if (_vm.PinnedTop)
            PinBtn.Foreground = new SolidColorBrush(Color.FromRgb(0x7C, 0x3A, 0xED));
        else
            PinBtn.Foreground = _buttonBrush
                ?? new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));
        Save();
    }

    void HideBtn_Click(object s, RoutedEventArgs e)
    {
        HideNote();
    }

    void LockBtn_Click(object s, RoutedEventArgs e)
    {
        if (_vm == null) return;
        // ponytail: sync from model first — guards against double-click no-op when model and
        // view have drifted (e.g. management card toggled lock state, event arrived out of order).
        _vm.IsLocked = _note.IsLocked;
        _vm.IsLocked = !_vm.IsLocked;
        ApplyLockState();
        // StickyNote lives in NotesService (not WidgetService), so route through its SetLocked
        _notesService.SetLocked(_note.Id.ToString(), _vm.IsLocked);
        Save();
    }

    void OnServiceLockChanged(string id, bool locked)
    {
        if (id != _note.Id.ToString()) return;
        if (_vm.IsLocked == locked) return;
        _vm.IsLocked = locked;
        ApplyLockState();
    }

    void LockBtn_Enter(object s, MouseEventArgs e)
    {
        LockBtn.Background = LockHoverBrush;
        LockBtn.Foreground = Brushes.White;
    }

    // ponytail: same button-color-cache pattern as SaveBtn_Leave / PinBtn_Leave — don't clobber button color
    void LockBtn_Leave(object s, MouseEventArgs e)
    {
        LockBtn.Background = Brushes.Transparent;
        LockBtn.Foreground = _buttonBrush
            ?? new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));
    }

    void ApplyLockState()
    {
        if (_vm == null) return;
        LockBtn.Content = _vm.IsLocked ? "🔒" : "🔓";
        TitleBarBorder.Cursor = _vm.IsLocked
            ? System.Windows.Input.Cursors.Arrow
            : System.Windows.Input.Cursors.SizeAll;
        var gripVis = _vm.IsLocked ? Visibility.Collapsed : Visibility.Visible;
        GripTL.Visibility = gripVis;
        GripTR.Visibility = gripVis;
        GripBL.Visibility = gripVis;
        GripBR.Visibility = gripVis;
        // ponytail: locked notes stay under app windows — pin once at the desktop layer so the
        // first-time activation places the window correctly even if it's still HWND_TOP from load.
        if (_vm.IsLocked) NativeMethods.PinBelowProgman(this);
    }

    void ToggleRestore_Click(object s, RoutedEventArgs e)
    {
        _note.EnableRestoreButton = !_note.EnableRestoreButton;
        if (s is MenuItem mi)
            mi.Header = _note.EnableRestoreButton
                ? _loc["Note.DisableRestore"]
                : _loc["Note.EnableRestore"];
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
        _notesService.LockChanged -= OnServiceLockChanged;
        _notesService.NotesChanged -= OnNotesChanged;
        _note.HoverExpandSettingsChanged -= OnHoverExpandSettingsChanged;
        _snapDrag?.Detach();
        _snapResize?.Detach();
        _hover?.Dispose();
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
            if (_vm?.IsLocked != true) NativeMethods.PinToDesktop(this);
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
