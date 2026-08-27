using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
    private static readonly SolidColorBrush PinHoverBrush     = Freeze(new(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)));
    private static readonly SolidColorBrush LockHoverBrush    = Freeze(new(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)));
    private static readonly SolidColorBrush FormatActiveBrush = Freeze(new(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)));
    static SolidColorBrush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    private StickyNote _note;
    private readonly NotesService _notesService;
    private StickyNoteViewModel _vm;
    private readonly LocalizationService _loc = LocalizationService.Instance;
    private Action<string>? _langChanged;
    private bool _initializing = true;
    private bool _deleted;
    private Point _restoreDown;
    public Action? OnStateChanged { get; set; }
    // ponytail: cached button-color brush for title bar buttons. Set by ApplyTitleBar from
    // _note.ButtonColor; hover/click handlers read it so the hover→leave cycle doesn't clobber it.
    private SolidColorBrush? _buttonBrush;
    private HoverExpandBehavior? _hover;
    private SnapDrag? _snapDrag;
    private SnapResize? _snapResize;

    // ponytail: 字体颜色弹层 — 最近一次应用的颜色(语言切换重建后恢复高亮环)。
    private string _lastTextColorHex = "#E0E0E0";

    // ── 待输入格式(Word 式光标格式)──
    // 无选区点格式按钮时写入;ContentBox_TextChanged 时应用到新插入的文本,
    // 规避 WPF 在空 Run 处打字格式失效/被重置的问题。
    private FontWeight? _pendingWeight;
    private FontStyle? _pendingStyle;
    private bool? _pendingUnderline;
    private double? _pendingSize;
    private SolidColorBrush? _pendingColor;
    private bool _applyingPendingFormat;
    // 程序化同步字号下拉时为 true,防止其 SelectionChanged 再触发应用逻辑(防循环)。
    private bool _updatingFormatButtons;
    private DispatcherTimer? _autoSaveTimer;

    // ponytail: 位置防抖保存 — 拖拽移动后持久化 X/Y（与分区 ZoneWindow 一致）。
    private readonly DispatcherTimer _positionSaveDebounce = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _positionSavePending;
    void SchedulePositionSave() { _positionSavePending = true; _positionSaveDebounce.Stop(); _positionSaveDebounce.Start(); }

    // ── Tile-mode title-bar cut ──
    // 磁贴模式砍掉最上面一层 28px 标题栏:窗口高度同步缩小 28px,格式化工具栏保留。
    const double NoteTileTitleBarCut = 28;

    double NoteWindowHeight() => _note.TileMode ? Math.Max(120, _note.Height - NoteTileTitleBarCut) : _note.Height;
    double NoteFullHeightFromWindow() => _note.TileMode ? Height + NoteTileTitleBarCut : Height;

    public StickyNoteWindow(StickyNote note, NotesService notesService)
    {
        InitializeComponent();
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

        // 优先从独立 JSON 恢复富文本(段落/run 级格式);旧便签/无文件时回退到模型纯文本/旧标签。
        LoadNoteContent();

        ApplyStyle();
        ApplyTitleBar();
        // 磁贴模式:构造期即砍掉最上面一层标题栏高度。
        Height = NoteWindowHeight();
        if (note.PinnedTop) Topmost = true;

        LocationChanged += (_, _) => { _note.X = Left; _note.Y = Top; SchedulePositionSave(); };
        _positionSaveDebounce.Tick += (_, _) => { _positionSaveDebounce.Stop(); if (_positionSavePending) { _positionSavePending = false; _notesService.Save(); } };
        SizeChanged += (_, _) => { if (MainContent.Visibility == Visibility.Visible) { _note.Width = Width; _note.Height = NoteFullHeightFromWindow(); NativeMethods.UpdateRoundedCorners(this, _note.CornerRadius); } };

        Loaded += OnLoad;
        _notesService.NotesChanged += OnNotesChanged;
        // ponytail 2026-08-27: 语言变化时刷新右键菜单 — XAML 静态绑定只读一次 i18n,
        // 菜单项 Header 必须手动同步(吸取时钟/日历的教训)。
        _langChanged = _ => ApplyLoc();
        _loc.LanguageChanged += _langChanged;
        ApplyLoc(); // 初次应用(包含右键菜单)
        // ponytail 2026-08-29: 颜色菜单 — 弹层打开时:①对一遍系统调色板 ②把弹层
        // HWND 提到顶层(便签窗口钉桌面/置底时,所属弹层可能被压到其它窗口下面,
        // 导致色块点击落到主窗口上被窗口级 Preview 误吞)。色块点击直接挂在每个
        // swatch 的 MouseLeftButtonDown 上(最可靠),不再依赖 Mouse.Capture /
        // click-outside 捕获链 — 捕获被谁偷走或弹层树路由变化都会让色块失灵。
        FontColorPopup.Opened += (_, _) =>
        {
            MenuThemeService.Apply();
            DzTrace.Log($"[FontColor] Popup Opened. panelChildren={ColorPresetsPanel?.Children.Count} panelActual={ColorPresetsPanel?.ActualWidth:F0}x{ColorPresetsPanel?.ActualHeight:F0}");
            Dispatcher.BeginInvoke(() =>
            {
                if (!FontColorPopup.IsOpen || FontColorPopup.Child == null) return;
                try
                {
                    if (PresentationSource.FromVisual(FontColorPopup.Child) is HwndSource hs && hs.Handle != IntPtr.Zero)
                        NativeMethods.SetWindowPos(hs.Handle, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
                }
                catch (Exception ex) { DzTrace.Log($"[FontColor] Opened: topmost FAILED {ex.Message}"); }
            }, DispatcherPriority.Loaded);
        };
        // ponytail: 字体字号下拉打开时同样对一遍系统调色板,避免 Popup 内 Menu.* 停在旧色。
        FontSizeCombo.DropDownOpened += (_, _) => MenuThemeService.Apply();

        // 自定义颜色按钮:直接挂点击(不再走根 Border 的 Preview 转发)。
        ColorCustomBtn.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            FontColorPopup.IsOpen = false;
            OpenCustomColorDialog();
        };
        // ponytail 2026-08-28: 兜底 — 切窗口(Alt-Tab 等无鼠标按下的失活)时关菜单。
        Deactivated += (_, _) =>
        {
            DzTrace.Log($"[FontColor] Window Deactivated. popupWasOpen={FontColorPopup?.IsOpen}");
            if (FontColorPopup != null) FontColorPopup.IsOpen = false;
        };
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
        // ponytail 2026-08-28 边框残影修复 — 与 ZoneWindow 同款:展开时恢复圆角,
        // 收起完成时重断言关闭全部 OS 层装饰(玻璃/圆角/DWM 框架阴影)。
        _hover.Expanded += ReapplyAcrylic;
        _hover.Collapsed += OnHoverCollapsed;
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
        // ponytail 2026-08-28: 模型同步路径只在 EnableRestoreButton 真正变化时才
        // SetEnabled；否则每次 UpdateNote(收起/展开)都会打断进行中的缩放动画。
        if (_hover != null && _hover.IsEnabled != _note.EnableRestoreButton)
            _hover.SetEnabled(_note.EnableRestoreButton);
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

    // ── 富文本 JSON 持久化 ──

    void LoadNoteContent()
    {
        var fileData = _notesService.LoadNoteFile(_note.Id);
        if (fileData != null)
        {
            LoadFromNoteFileData(fileData);
        }
        else
        {
            LoadContent(_note.Content);
        }
    }

    NoteFileData BuildNoteFileData()
    {
        var data = new NoteFileData();
        foreach (Block block in ContentBox.Document.Blocks)
        {
            if (block is not Paragraph para) continue;
            var pd = new NoteParagraphData();
            FlattenInlines(para.Inlines, pd.Runs);
            data.Paragraphs.Add(pd);
        }
        return data;
    }

    void FlattenInlines(InlineCollection inlines, List<NoteRunData> runs)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case Run run:
                    runs.Add(new NoteRunData
                    {
                        Text = run.Text,
                        Bold = IsBold(GetEffectiveInlineValue(run, TextElement.FontWeightProperty, FontWeights.Normal)),
                        Italic = IsItalic(GetEffectiveInlineValue(run, TextElement.FontStyleProperty, FontStyles.Normal)),
                        Underline = IsUnderlined(GetEffectiveInlineValue(run, Inline.TextDecorationsProperty, null)),
                        Size = GetEffectiveInlineValue(run, TextElement.FontSizeProperty, _note.FontSize) is double d ? d : _note.FontSize,
                        Color = EffectiveColorHex(run)
                    });
                    break;
                case Span span:
                    FlattenInlines(span.Inlines, runs);
                    break;
                case LineBreak:
                    if (runs.Count > 0) runs[^1].Text += "\n";
                    else runs.Add(new NoteRunData { Text = "\n" });
                    break;
            }
        }
    }

    object? GetEffectiveInlineValue(TextElement el, DependencyProperty prop, object? fallback)
    {
        for (TextElement? e = el; e != null; e = e.Parent as TextElement)
        {
            var v = e.ReadLocalValue(prop);
            if (v != DependencyProperty.UnsetValue) return v;
        }
        return fallback;
    }

    string EffectiveColorHex(TextElement el)
    {
        var v = GetEffectiveInlineValue(el, TextElement.ForegroundProperty, null);
        if (v is SolidColorBrush b)
            return $"#{b.Color.A:X2}{b.Color.R:X2}{b.Color.G:X2}{b.Color.B:X2}";
        if (ContentBox.Foreground is SolidColorBrush cb)
            return $"#{cb.Color.A:X2}{cb.Color.R:X2}{cb.Color.G:X2}{cb.Color.B:X2}";
        return "#FFE0E0E0";
    }

    void LoadFromNoteFileData(NoteFileData data)
    {
        var doc = new FlowDocument { LineHeight = double.NaN };
        foreach (var pd in data.Paragraphs)
        {
            var para = new Paragraph();
            foreach (var rd in pd.Runs)
            {
                var run = new Run(rd.Text);
                if (rd.Bold) run.FontWeight = FontWeights.Bold;
                if (rd.Italic) run.FontStyle = FontStyles.Italic;
                if (rd.Underline) run.TextDecorations = TextDecorations.Underline;
                if (rd.Size > 0) run.FontSize = rd.Size;
                try
                {
                    if (!string.IsNullOrWhiteSpace(rd.Color))
                        run.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(rd.Color));
                }
                catch { }
                para.Inlines.Add(run);
            }
            doc.Blocks.Add(para);
        }
        if (doc.Blocks.Count == 0)
            doc.Blocks.Add(new Paragraph(new Run("")));
        ContentBox.Document = doc;
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
                // ponytail 2026-08-28: 只写用户输入的文字内容 — 不再输出 [size=]/
                // [color=] 等格式标签(原 SaveFormatted 已删除)。
                File.WriteAllText(_note.LastSavePath, SaveContent(), System.Text.Encoding.UTF8);
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
                // ponytail 2026-08-28: 纯文字保存(同上,不写格式标签)。
                File.WriteAllText(dlg.FileName, SaveContent(), System.Text.Encoding.UTF8);
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
                // ponytail 2026-08-28: 统一走 LoadContent — 纯文本直接载入;旧版
                // 带格式标签的存档仍会被检测并走 LoadFormatted 兼容渲染。
                LoadContent(content);
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

        // ponytail 2026-08-27: 自定义 ContentBox 右键菜单 — 默认 WPF ContextMenu
        // 走 PresentationFramework 内置资源,不跟 LocalizationService。
        // 显式赋 ContextMenu + Opened 事件,切语言后下次右键取新 i18n。
        // ponytail 2026-08-28: 标题框与分区对齐 — 不再挂正文同款复制/粘贴菜单;
        // 右键标题由 Window_PreviewMouseRightButtonDown 弹便签自己的窗口菜单。
        // ContextMenuOpening 兜底拦掉 TextBox 默认编辑菜单,防止它盖掉窗口菜单。
        TitleBox.ContextMenuOpening += (_, e2) => e2.Handled = true;
        ContentBox.ContextMenu = BuildTextBoxContextMenu();
    }

    // ponytail 2026-08-27: 调用 helper 替换默认 TextBox/RichTextBox 右键菜单。
    System.Windows.Controls.ContextMenu BuildTextBoxContextMenu()
        => TextBoxContextMenuBuilder.Build(_loc);

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
        Width = savedW; Height = _note.TileMode ? Math.Max(120, savedH - NoteTileTitleBarCut) : savedH;
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
        FontColorPopup.IsOpen = false; // ponytail 2026-08-28: 收起时顺带关掉颜色菜单
        _note.X = Left; _note.Y = Top; _note.Width = Width; _note.Height = NoteFullHeightFromWindow();
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
            Height = _note.Height < 150 ? 200 : NoteWindowHeight();
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

    void Restore_Enter(object s, MouseEventArgs e) { RestoreButton.SetResourceReference(Border.BackgroundProperty, "Menu.Bg.Hover"); }
    void Restore_Leave(object s, MouseEventArgs e) { RestoreButton.SetResourceReference(Border.BackgroundProperty, "Menu.Bg.Surface"); }

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
        if (_note.EnableLiquidGlass && expanded)
        {
            var blurResult = AcrylicHelper.EnableBlur(this, _note.GlassBlurAmount, _note.GlassTintOpacity,
                _note.GlassTintLuminosity, _note.GlassColorMode);
            if (!blurResult.Success)
                System.Diagnostics.Debug.WriteLine($"[StickyNoteWindow] EnableBlur failed: {blurResult.Error}");
            // ponytail: additive liquid-glass overlay — the chromatic border rides a
            // separate overlay Border so it never replaces the user's base NoteBorder.
            if (NoteGlassBorder != null)
            {
                NoteGlassBorder.BorderBrush = AcrylicHelper.CreateChromaticBorder();
                NoteGlassBorder.BorderThickness = new Thickness(Math.Max(1.0, borderThickness));
                NoteGlassBorder.CornerRadius = new CornerRadius(_note.CornerRadius);
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
            // ponytail: additive overlay — clear the glass border when the effect is off.
            if (NoteGlassBorder != null)
                NoteGlassBorder.BorderThickness = new Thickness(0);
            try
            {
                var fillColor = (Color)ColorConverter.ConvertFromString(fillColorStr)!;
                BodyFillRect.Fill = new SolidColorBrush(fillColor);
            }
            catch { }
        }
        ApplyFillGeometry();
    }

    /// <summary>
    /// ponytail 2026-08-28 边框残影修复 — 展开(悬停/点击恢复按钮)时把 Win11 圆角
    /// 偏好一并恢复(收起时 OnHoverCollapsed 关掉了它),再走 ApplyAcrylic 恢复玻璃。
    /// </summary>
    void ReapplyAcrylic()
    {
        NativeMethods.SetRoundedCorners(this, _note.CornerRadius);
        ApplyAcrylic();
    }

    /// <summary>
    /// ponytail 2026-08-28 边框残影修复 — 收起完成时的最终保险(与 ZoneWindow 同款):
    /// 窗口收起后仍保持整窗大小,残留的丙烯酸玻璃 / Win11 圆角 / DWM 框架阴影
    /// 都会以「原窗口轮廓」的形式残留在恢复按钮周围,这里全部重断言关闭。
    /// </summary>
    void OnHoverCollapsed()
    {
        AcrylicHelper.DisableBlur(this);
        NativeMethods.DisableRoundedCorners(this);
        NativeMethods.DisableDwmFrameShadow(this);
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
        if (NoteGlassBorder != null)
            NoteGlassBorder.CornerRadius = new CornerRadius(r);
        TitleBarBorder.CornerRadius = new CornerRadius(r, r, 0, 0);
        // 磁贴模式:砍掉最上面一层标题栏(格式化工具栏保留)。
        TitleBarBorder.Visibility = _note.TileMode ? Visibility.Collapsed : Visibility.Visible;
        if (BodyFillRect != null)
            BodyFillRect.RadiusX = BodyFillRect.RadiusY = _note.TitleBarFillIndependent ? 0 : r;
        // ponytail 2026-08-28: 收起状态下跳过 DWM 圆角 — 设置面板显示开关 →
        // HideNote → UpdateNote → NotesChanged → OnNotesChanged → ApplyStyle
        // 这条链会在窗口收起后重新打开整窗大小的圆角描边(边框残影来源)。
        // 展开路径(ShowNote / ReapplyAcrylic)会各自恢复。
        bool collapsed = RestoreButton.Visibility == Visibility.Visible
                         || _hover is { IsExpanded: false };
        if (System.Windows.PresentationSource.FromVisual(this) != null && !collapsed)
            NativeMethods.SetRoundedCorners(this, r);
        // 磁贴模式高度同步:仅在展开且可见时应用,收起/隐藏流程不干预。
        if (!collapsed && MainContent.Visibility == Visibility.Visible)
            Height = NoteWindowHeight();
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
            ApplyIconVisual();
            // 字号数字跟随系统深浅色（Menu.Text.Primary），不再锁定白色。
            if (FontSizeCombo != null)
                FontSizeCombo.SetResourceReference(Control.ForegroundProperty, "Menu.Text.Primary");

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
            if (RestoreIconPath != null) RestoreIconPath.Opacity = ctl;
        }
        catch { }
    }

    /// <summary>标题栏图标 + 恢复按钮图标 — 独立 IconColor（空则回退标题文字色），内容支持 emoji 或原生矢量。</summary>
    void ApplyIconVisual()
    {
        // 标题栏图标 + 恢复按钮图标：都走「设置的颜色」（IconColor ?? 标题文字色），不随系统深浅色。
        Brush titleBrush;
        var titleColor = !string.IsNullOrEmpty(_note.IconColor) ? _note.IconColor : _note.TitleTextColor;
        try { titleBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(titleColor)!); }
        catch { titleBrush = _buttonBrush ?? Brushes.White; }

        var icon = string.IsNullOrEmpty(_note.IconChar) ? Helpers.IconGlyph.Sticky : _note.IconChar;
        Helpers.IconGlyph.Apply(TitleIconChar, TitleIconPath, icon, titleBrush, 12);
        Helpers.IconGlyph.Apply(RestoreIconChar, RestoreIconPath, icon, titleBrush, 18);
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
        // 磁贴模式砍掉最上面一层标题栏后,顶部只剩格式化工具栏。
        double topLayers = TitleBarLayerHeight() - (_note.TileMode ? NoteTileTitleBarCut : 0);
        double clipTop = _note.TitleBarFillIndependent ? topLayers : 0;
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
        // ponytail 2026-08-28: 只在开关真正变化时才 SetEnabled，避免外观实时预览
        // 打断正在播放的缩放动画。
        if (_hover != null && _hover.IsEnabled != _note.EnableRestoreButton)
            _hover.SetEnabled(_note.EnableRestoreButton);
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
        DzTrace.Log($"[FontColor] WindowPreviewLeftDown ENTRY popupIsOpen={FontColorPopup.IsOpen} src={e.OriginalSource?.GetType().Name}");
        // ponytail 2026-08-28: 颜色菜单的开关配合 — StaysOpen=True 后菜单不再自动
        // 关闭,由本窗口级 Preview 统一收口:菜单开着时任何窗内点击先关菜单;点的是
        // 颜色按钮本身则吞掉本次按下(否则它的 Click 会把刚关掉的菜单重新打开)。
        if (FontColorPopup.IsOpen)
        {
            var withinBtn = IsWithinElement(e.OriginalSource, FontColorBtn);
            DzTrace.Log($"[FontColor] WindowPreviewLeftDown: popup open → closing. withinFontColorBtn={withinBtn}");
            FontColorPopup.IsOpen = false;
            if (withinBtn)
            {
                e.Handled = true;
                return;
            }
        }
        // ponytail 2026-08-29: 点击来自任何弹层(字号下拉、颜色菜单等)→ 主窗口一律
        // 不介入。弹层可视树与主窗口是两棵树,OriginalSource 向上走永远到不了
        // ComboBox/Button,旧逻辑会把弹层里的点击误判成空白区,启动拖窗并
        // Handled=true 吞掉点击 → 字号下拉选不中、文字大小失效(用户报修)。
        if (e.OriginalSource is DependencyObject doSrc
            && PresentationSource.FromDependencyObject(doSrc)?.RootVisual?.GetType().Name == "PopupRoot")
            return;
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

    // ponytail 2026-08-28: 标题输入框 = 拖动手柄 + 改名入口(与分区标题栏体验对齐)。
    // 按住并移动超过阈值 → 交给 SnapDrag 拖动整窗;原地轻点(不动)→ 正常进入改名。
    Point? _titleBoxPress;

    void TitleBox_PreviewMouseLeftButtonDown(object s, MouseButtonEventArgs e)
    {
        if (_vm?.IsLocked == true) return;
        _titleBoxPress = e.GetPosition(this);
        // 不处理按下事件:静止点击仍需 TextBox 自己定位光标进入改名。
    }

    void TitleBox_PreviewMouseMove(object s, MouseEventArgs e)
    {
        if (_vm?.IsLocked == true || _titleBoxPress == null || e.LeftButton != MouseButtonState.Pressed)
            return;
        var pos = e.GetPosition(this);
        double min = Math.Max(4, SystemParameters.MinimumHorizontalDragDistance);
        if (Math.Abs(pos.X - _titleBoxPress.Value.X) < min
            && Math.Abs(pos.Y - _titleBoxPress.Value.Y) < min)
            return;
        // 超过阈值 = 拖窗而非改名点击。清掉按压基准防止重复触发;SnapDrag.Start
        // 会把鼠标捕获从 TextBox 收走(其内部 CaptureMouse),文本选择逻辑随之停止。
        _titleBoxPress = null;
        if (_snapDrag == null || _snapDrag.IsActive) return;
        _snapDrag.Start(e, () =>
        {
            if (_vm?.IsLocked != true) NativeMethods.PinToDesktop(this);
            _note.X = Left; _note.Y = Top;
        });
        e.Handled = true;
    }

    // ── Right-click (window menu, zone-aligned) ──

    /// <summary>
    /// ponytail 2026-08-28: 与 ZoneWindow.Window_PreviewMouseRightButtonDown 对齐 —
    /// 除正文(保留自己的复制/粘贴菜单)外,便签任何位置的右键都弹便签自己的窗口
    /// 菜单(置顶/设置/锁定/最小化/删除),标题框因此不再弹 TextBox 默认编辑菜单。
    /// </summary>
    void Window_PreviewMouseRightButtonDown(object s, MouseButtonEventArgs e)
    {
        if (MainContent.Visibility != Visibility.Visible) return;
        FontColorPopup.IsOpen = false; // 右键开窗口菜单时,顺带关掉颜色菜单
        if (IsWithinContentBox(e.OriginalSource)) return; // 正文保留编辑右键菜单
        NoteBorder.ContextMenu.IsOpen = true;
        e.Handled = true;
    }

    static bool IsWithinElement(object src, FrameworkElement target)
    {
        System.Windows.DependencyObject? c = src as System.Windows.DependencyObject;
        while (c != null)
        {
            if (ReferenceEquals(c, target)) return true;
            if (c is System.Windows.FrameworkContentElement fce)
                c = LogicalTreeHelper.GetParent(fce);
            else if (c is System.Windows.Media.Visual)
                c = System.Windows.Media.VisualTreeHelper.GetParent(c);
            else
                break;
        }
        return false;
    }

    static bool IsWithinContentBox(object src)
    {
        System.Windows.DependencyObject? c = src as System.Windows.DependencyObject;
        while (c != null)
        {
            if (c is System.Windows.FrameworkElement fe && fe.Name == "ContentBox") return true;
            // 正文是 RichTextBox:点击内容时 OriginalSource 可能是 Paragraph/Run
            // (FrameworkContentElement),走逻辑树往上爬(与 Window_PreviewMouseLeftButtonDown 同款)。
            if (c is System.Windows.FrameworkContentElement fce)
                c = LogicalTreeHelper.GetParent(fce);
            else if (c is System.Windows.Media.Visual)
                c = System.Windows.Media.VisualTreeHelper.GetParent(c);
            else
                break;
        }
        return false;
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
        // ponytail 2026-08-27: 锁定态变化时同步右键菜单 Header(吸取教训)。
        CtxLock.Header = _vm.IsLocked ? _loc["Common.Unlock"] : _loc["Common.Lock"];
        // ponytail: locked notes stay under app windows — pin once at the desktop layer so the
        // first-time activation places the window correctly even if it's still HWND_TOP from load.
        // Guard with IsVisible: OnClosed → UpdateNote → NotesChanged re-enters this handler while
        // WmDestroy is tearing the window down, and PinBelowProgman → EnsureHandle would throw
        // "关闭窗口后，无法设置可见性…" (same crash the PinToDesktop branch already guards for).
        if (_vm.IsLocked && IsVisible) NativeMethods.PinBelowProgman(this);
    }

    // ponytail 2026-08-27: 已从右键菜单移除 — 保留方法体以防外部旧代码仍引用。
    void ToggleRestore_Click(object s, RoutedEventArgs e)
    {
        _note.EnableRestoreButton = !_note.EnableRestoreButton;
        if (s is MenuItem mi)
            mi.Header = _note.EnableRestoreButton
                ? _loc["Note.DisableRestore"]
                : _loc["Note.EnableRestore"];
    }

    // ponytail 2026-08-27: 实时同步右键菜单 — 切语言/置顶态变化时刷新。
    // 必须 XAML 静态绑定 + 代码手动同步两路同步,避免"切语言后菜单文本不更新"(吸取时钟/日历教训)。
    void ApplyLoc()
    {
        CtxPinTop.Header = _vm.PinnedTop ? _loc["Note.Unpin"] : _loc["Note.PinTop"];
        // ponytail 2026-08-28: 保存组三项同步刷新。
        CtxSave.Header = _loc["Note.Save"];
        CtxSaveAs.Header = _loc["Note.SaveAs"];
        CtxOpenFile.Header = _loc["Note.OpenFile"];
        CtxSettings.Header = _loc["Note.Settings"];
        CtxMinimize.Header = _loc["Note.Minimize"];
        // ponytail 2026-08-27: 切语言时同步刷新 CtxLock。
        CtxLock.Header = _loc[_vm.IsLocked ? "Common.Unlock" : "Common.Lock"];
        CtxDelete.Header = _loc["Note.Delete"];
        // ponytail 2026-08-28: 预设色块「透明」提示跟着语言走。
        BuildColorPresets();
    }

    // ponytail 2026-08-28: 右键菜单 保存/另存为/打开文件 — 直接绑定 💾 按钮同款功能。
    void CtxSave_Click(object s, RoutedEventArgs e) => SaveToFile();
    void CtxSaveAs_Click(object s, RoutedEventArgs e) => SaveAsToFile();
    void CtxOpenFile_Click(object s, RoutedEventArgs e) => OpenFile();

    // ponytail 2026-08-27: 右键置顶切换 — 复用 PinBtn_Click 的 PinnedTop 流;
    // 菜单项标题在"置顶"/"取消置顶"间切换,ApplyLoc 也会刷新。
    void PinTop_Click(object s, RoutedEventArgs e)
    {
        _vm.PinnedTop = !_vm.PinnedTop;
        Topmost = _vm.PinnedTop;
        if (!_vm.PinnedTop && _vm.IsLocked != true) NativeMethods.PinToDesktop(this);
        if (s is MenuItem mi)
            mi.Header = _vm.PinnedTop ? _loc["Note.Unpin"] : _loc["Note.PinTop"];
        Save();
        ApplyLoc(); // 同步刷新其他菜单项
    }

    // ponytail 2026-08-27: 设置 — 与分区齿轮入口同款 PropertyWindowService 调用。
    void Settings_Click(object s, RoutedEventArgs e)
    {
        PropertyWindowService.OpenOrFocus(_note, this);
    }

    // ponytail 2026-08-27: 最小化 = 最小化到任务栏。
    void Minimize_Click(object s, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    void Delete_Click(object s, RoutedEventArgs e)
    {
        // ponytail 2026-08-27: 二级确认 — 删除便签不可恢复。标题优先用 Title,空则降级"便签"。
        var label = string.IsNullOrWhiteSpace(_note.Title) ? _loc["StickyNotePage.FallbackTitle"] : _note.Title;
        var confirm = string.Format(_loc["StickyNotePage.DeleteConfirm"], label);
        if (MessageBox.Show(confirm, _loc["StickyNotePage.DeleteTitle"], MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        _deleted = true;
        _notesService.DeleteNote(_note.Id);
        Close();
    }
    void TitleBox_LostFocus(object s, RoutedEventArgs e)
    {
        // ponytail 2026-08-28: 与分区标题对齐 — 去首尾空白;空名回退不保存;
        // 无变化不保存;提交后把显示文本还原为已保存值。
        var text = TitleBox.Text?.Trim() ?? "";
        if (!string.IsNullOrEmpty(text) && text != _vm.Title)
        {
            _vm.Title = text;
            Save();
        }
        TitleBox.Text = _vm.Title ?? "";
    }

    void TitleBox_KeyDown(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            var text = TitleBox.Text?.Trim() ?? "";
            if (!string.IsNullOrEmpty(text) && text != _vm.Title)
            {
                _vm.Title = text;
                Save();
            }
            TitleBox.Text = _vm.Title ?? "";
            ContentBox.Focus();
            e.Handled = true;
        }
    }

    void Save()
    {
        _vm.Content = SaveContent();
        _vm.ApplyToModel();
        _notesService.UpdateNote(_note);
        // 富文本正文独立写一份 JSON(自动保存)。
        _notesService.SaveNoteFile(_note.Id, BuildNoteFileData());
    }

    protected override void OnClosed(EventArgs e)
    {
        _autoSaveTimer?.Stop();
        _autoSaveTimer = null;
        // ponytail: 关窗前落盘未保存的位置。
        _positionSaveDebounce.Stop();
        if (_positionSavePending && !_deleted) { _positionSavePending = false; _notesService.Save(); }
        _vm.Content = SaveContent();
        _vm.ApplyToModel();
        if (!_deleted)
        {
            // 关窗前把富文本正文落盘一次(删除便签时不再写回,避免孤儿文件)。
            _notesService.SaveNoteFile(_note.Id, BuildNoteFileData());
        }
        // ponytail: unsubscribe BEFORE UpdateNote so this closing window doesn't
        // re-enter its own NotesChanged / LockChanged handlers while WmDestroy is
        // tearing the window down (those re-entrant calls hit EnsureHandle →
        // "关闭窗口后，无法设置可见性…" and crash the app on exit).
        _notesService.LockChanged -= OnServiceLockChanged;
        _notesService.NotesChanged -= OnNotesChanged;
        if (!_deleted)
            _notesService.UpdateNote(_note);
        _note.HoverExpandSettingsChanged -= OnHoverExpandSettingsChanged;
        if (_langChanged != null) _loc.LanguageChanged -= _langChanged;
        _langChanged = null;
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
        if (_initializing || _updatingFormatButtons) return;
        if (FontSizeCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag
            && double.TryParse(tag, out var fs))
        {
            var sel = ContentBox.Selection;
            if (sel != null && !sel.IsEmpty)
            {
                sel.ApplyPropertyValue(TextElement.FontSizeProperty, fs);
                _pendingSize = null;
            }
            else
            {
                _pendingSize = fs;
                ApplyToCaret(TextElement.FontSizeProperty, fs);
            }
            UpdateFormatButtons();
            // ComboBox 会抢焦点:应用完把焦点还给正文,用户可直接继续输入。
            RefocusContentBox();
        }
    }

    void BoldBtn_Click(object s, RoutedEventArgs e)
    {
        var sel = ContentBox.Selection;
        if (sel == null) return;
        bool hasSelection = !sel.IsEmpty;
        object? current;
        if (hasSelection) current = sel.GetPropertyValue(Inline.FontWeightProperty);
        else if (_pendingWeight is FontWeight pw) current = pw;
        else current = EffectiveValue(Inline.FontWeightProperty, ContentBox.FontWeight);
        var next = IsBold(current) ? FontWeights.Normal : FontWeights.Bold;
        if (hasSelection)
        {
            sel.ApplyPropertyValue(Inline.FontWeightProperty, next);
            _pendingWeight = null;
        }
        else
        {
            _pendingWeight = next;
            ApplyToCaret(Inline.FontWeightProperty, next);
        }
        UpdateFormatButtons();
        RefocusContentBox();
    }

    void ItalicBtn_Click(object s, RoutedEventArgs e)
    {
        var sel = ContentBox.Selection;
        if (sel == null) return;
        bool hasSelection = !sel.IsEmpty;
        object? current;
        if (hasSelection) current = sel.GetPropertyValue(Inline.FontStyleProperty);
        else if (_pendingStyle is FontStyle ps) current = ps;
        else current = EffectiveValue(Inline.FontStyleProperty, ContentBox.FontStyle);
        var next = IsItalic(current) ? FontStyles.Normal : FontStyles.Italic;
        if (hasSelection)
        {
            sel.ApplyPropertyValue(Inline.FontStyleProperty, next);
            _pendingStyle = null;
        }
        else
        {
            _pendingStyle = next;
            ApplyToCaret(Inline.FontStyleProperty, next);
        }
        UpdateFormatButtons();
        RefocusContentBox();
    }

    void UnderlineBtn_Click(object s, RoutedEventArgs e)
    {
        var sel = ContentBox.Selection;
        if (sel == null) return;
        bool hasSelection = !sel.IsEmpty;
        object? current;
        if (hasSelection) current = sel.GetPropertyValue(Inline.TextDecorationsProperty);
        else if (_pendingUnderline is bool pu) current = pu ? TextDecorations.Underline : null;
        else current = EffectiveValue(Inline.TextDecorationsProperty, null);
        var next = IsUnderlined(current) ? null : TextDecorations.Underline;
        if (hasSelection)
        {
            sel.ApplyPropertyValue(Inline.TextDecorationsProperty, next);
            _pendingUnderline = null;
        }
        else
        {
            _pendingUnderline = next != null;
            ApplyToCaret(Inline.TextDecorationsProperty, next);
        }
        UpdateFormatButtons();
        RefocusContentBox();
    }

    // ── Font color (popup palette, ColorSwatchButton 同款样式) ──

    // ponytail 2026-08-28: 预设与旧版一致(透明/黑白灰/纯色系),但呈现方式换成
    // 样式设置界面的 6×2 圆角小方块网格,由 BuildColorPresets 构建。
    static readonly string[] FontColorPresets =
    {
        "#01000000", // 透明(与设置界面一致:露出弹层底色,靠边框区分格子)
        "#000000", "#FFFFFF", "#808080",
        "#FF0000", "#FF7D00", "#FFFF00", "#00FF00",
        "#0000FF", "#00FFFF", "#FF00FF",
    };

    void BuildColorPresets()
    {
        if (ColorPresetsPanel == null) return;
        ColorPresetsPanel.Children.Clear();
        Border? match = null;
        foreach (var hex in FontColorPresets)
        {
            bool isTransparent = hex == "#01000000";
            Brush bg;
            if (isTransparent)
            {
                bg = Brushes.Transparent;
            }
            else
            {
                try { bg = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
                catch { bg = Brushes.Transparent; }
            }
            // ponytail 2026-08-29: 点击直接挂在 swatch 上(与旧版一致的最可靠路径)。
            // 不再走根 Border 的 Preview 转发/坐标兜底 — 弹层树路由或捕获被干扰时
            // 转发路径收不到点击,直接 handler 一定收到(用户报:选输入文字颜色失效)。
            var swatch = new Border
            {
                Width = 24,
                Height = 24,
                Margin = new Thickness(2),
                Background = bg,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Cursor = Cursors.Hand,
                Tag = hex,
            };
            if (isTransparent)
                ToolTipService.SetToolTip(swatch, _loc["Common.Transparent"]);
            swatch.MouseEnter += ColorSwatch_Enter;
            swatch.MouseLeave += ColorSwatch_Leave;
            swatch.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                FontColorPopup.IsOpen = false;
                try
                {
                    var color = (Color)ColorConverter.ConvertFromString(hex);
                    PulseSwatch(swatch);
                    ApplyTextColor(new SolidColorBrush(color), hex);
                }
                catch (Exception ex) { DzTrace.Log($"[FontColor] SwatchClick EXCEPTION: {ex.Message}"); }
            };
            ColorPresetsPanel.Children.Add(swatch);
            if (string.Equals(hex, _lastTextColorHex, StringComparison.OrdinalIgnoreCase))
                match = swatch;
        }
        UpdateColorSelection(match);
    }

    void ColorSwatch_Enter(object sender, MouseEventArgs e)
    {
        if (sender is Border b) b.Opacity = 0.82;
    }

    void ColorSwatch_Leave(object sender, MouseEventArgs e)
    {
        if (sender is Border b) b.Opacity = 1.0;
    }

    void FontColorBtn_Click(object s, RoutedEventArgs e)
    {
        DzTrace.Log($"[FontColor] FontColorBtn_Click fired. popupWasOpen={FontColorPopup.IsOpen}");
        // ponytail 2026-08-28: 只负责打开。StaysOpen=True 后 Popup 不再自动关闭,
        // 关闭统一由 Window_PreviewMouseLeftButtonDown(窗内点击)、Deactivated
        // (点桌面/切走)与 HideNote 处理;这里若再 IsOpen 取反,会把窗口级
        // Preview 刚关掉的菜单立刻重新打开。
        FontColorPopup.IsOpen = true;
        DzTrace.Log($"[FontColor] FontColorBtn_Click set IsOpen=true → now={FontColorPopup.IsOpen}");
    }

    void OpenCustomColorDialog()
    {
        // 从当前按钮色出发,自定义器直接停在现用颜色上。
        string current = "FFFFFF";
        if (FontColorBtn.Foreground is SolidColorBrush scb)
            current = $"{scb.Color.R:X2}{scb.Color.G:X2}{scb.Color.B:X2}";
        var dlg = new ColorPickerDialog(current, followSystemTheme: true) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            try
            {
                var color = (Color)ColorConverter.ConvertFromString("#" + dlg.SelectedColor);
                ApplyTextColor(new SolidColorBrush(color), "#" + dlg.SelectedColor);
            }
            catch { }
        }
    }

    void PulseSwatch(Border swatch)
    {
        try
        {
            swatch.RenderTransformOrigin = new Point(0.5, 0.5);
            var st = swatch.RenderTransform as ScaleTransform ?? new ScaleTransform(1, 1);
            swatch.RenderTransform = st;
            var anim = new DoubleAnimation(1, 1.3, TimeSpan.FromMilliseconds(90)) { AutoReverse = true };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        }
        catch { }
    }

    void UpdateColorSelection(Border? selected)
    {
        foreach (var child in ColorPresetsPanel.Children)
        {
            if (child is not Border b || b.Tag is not string) continue;
            bool isSel = ReferenceEquals(b, selected);
            // 跟随系统深浅色 + 系统强调色:选中环用 Menu.Accent.Solid,普通描边用 Menu.Border.Subtle。
            b.SetResourceReference(Border.BorderBrushProperty, isSel ? "Menu.Accent.Solid" : "Menu.Border.Subtle");
            b.BorderThickness = new Thickness(isSel ? 2 : 1);
        }
    }

    /// <summary>
    /// ponytail 2026-08-28 选色 BUG 修复 — 选中的文字变色 + 光标插入格式同步,
    /// 保证「后续输入内容」也用新色:
    /// ① 有选区 → 选区直接变色;
    /// ② 光标处空选区应用属性 = WPF 打字格式机制,决定下一字符颜色(旧实现只改
    ///    ContentBox.Foreground,既连累已有文字一起变继承色,又盖不掉光标前一行
    ///    已带显式颜色的行尾,导致新打的字仍是旧颜色);
    /// ③ 「A」图标换成所选颜色。
    /// </summary>
    void ApplyTextColor(SolidColorBrush brush, string? sourceHex = null)
    {
        var sel = ContentBox.Selection;
        if (sel != null && !sel.IsEmpty)
        {
            // 有选区 → 直接给选区上色。
            sel.ApplyPropertyValue(TextElement.ForegroundProperty, brush);
            _pendingColor = null;
        }
        else
        {
            // 无选区 → 只写光标插入格式(新输入继承)。
            _pendingColor = brush;
            ApplyToCaret(TextElement.ForegroundProperty, brush);
        }
        // 记住最近颜色:语言切换触发 BuildColorPresets 重建后仍能恢复选中高亮环。
        if (!string.IsNullOrEmpty(sourceHex))
            _lastTextColorHex = sourceHex;
        var match = ColorPresetsPanel.Children.OfType<Border>()
            .FirstOrDefault(b => b.Tag is string t
                && string.Equals(t, _lastTextColorHex, StringComparison.OrdinalIgnoreCase));
        UpdateColorSelection(match);
        UpdateFormatButtons();
        FontColorBtn.Foreground = brush; // 兜底:立即反馈,确保图标显示所选颜色
        // 焦点还给正文,选完色可直接继续输入(光标保持原位)。
        RefocusContentBox();
    }

    void ContentBox_SelectionChanged(object s, RoutedEventArgs e)
    {
        if (_initializing) return;
        // 注意:这里不主动清待输入格式。打字/退格都会触发 SelectionChanged,
        // 若在这里清,退格删掉刚输入的字后待输入格式就被清掉,光标会跳到前一个
        // 字符的格式上去。待输入格式只在格式按钮/下拉再次操作时改变,移动光标
        // (方向键/点击)也不清,保证「先设置、再定位、再输入」即时同步。
        UpdateFormatButtons();
    }

    /// <summary>
    /// 方向键/Home/End/PageUp/PageDown 移动光标:不再清掉待输入格式。
    /// 用户设置格式后期望「接下来输入的内容」都用该格式,即使中途移动了光标
    /// (常见流程:先选字号 → 再点进正文定位 → 输入)。待输入格式只由格式按钮/
    /// 下拉的再次操作改变,保证设置与输入即时同步,不再需要反复调整才生效。
    /// </summary>
    void ContentBox_PreviewKeyDown(object s, KeyEventArgs e)
    {
        // 待输入格式跨光标移动保留(有意为之)。
    }

    /// <summary>点击正文定位光标:同样保留待输入格式(理由同上)。</summary>
    void ContentBox_PreviewMouseLeftButtonDown(object s, MouseButtonEventArgs e)
    {
        // 待输入格式跨点击定位保留(有意为之)。
    }

    /// <summary>文本真正插入前,把待输入格式写到光标处,保证首个字符继承(空白便签也生效)。</summary>
    void ContentBox_PreviewTextInput(object s, TextCompositionEventArgs e)
    {
        if (_initializing) return;
        if (_pendingWeight == null && _pendingStyle == null && _pendingUnderline == null
            && _pendingSize == null && _pendingColor == null)
            return;
        ApplyPendingToCaret();
    }

    void ApplyPendingToCaret()
    {
        if (_pendingWeight is FontWeight w) ApplyToCaret(TextElement.FontWeightProperty, w);
        if (_pendingStyle is FontStyle st) ApplyToCaret(TextElement.FontStyleProperty, st);
        if (_pendingUnderline is bool u) ApplyToCaret(Inline.TextDecorationsProperty, u ? TextDecorations.Underline : null);
        if (_pendingSize is double sz) ApplyToCaret(TextElement.FontSizeProperty, sz);
        if (_pendingColor is SolidColorBrush c) ApplyToCaret(TextElement.ForegroundProperty, c);
    }

    /// <summary>
    /// 兜底:新插入的文字直接套用待输入格式(加粗/斜体/下划线/字号/颜色),
    /// 覆盖粘贴/输入法合成等 PreviewTextInput 没覆盖到的路径。
    /// </summary>
    void ContentBox_TextChanged(object s, TextChangedEventArgs e)
    {
        if (_initializing || _applyingPendingFormat) return;

        bool hasPending = _pendingWeight != null || _pendingStyle != null || _pendingUnderline != null
                          || _pendingSize != null || _pendingColor != null;
        if (hasPending)
        {
            _applyingPendingFormat = true;
            try
            {
                foreach (var change in e.Changes)
                {
                    if (change.AddedLength <= 0) continue;
                    try
                    {
                        // TextChange.Offset 与 TextPointer.GetPositionAtOffset 的 offset
                        // 语义一致,都是「符号(symbol)」偏移(字符 + 段落/Run 等元素边界),
                        // 直接使用即可精确定位到本次新增的文字范围。
                        var start = ContentBox.Document.ContentStart
                            .GetPositionAtOffset(change.Offset, LogicalDirection.Forward);
                        var end = start?.GetPositionAtOffset(change.AddedLength, LogicalDirection.Forward);
                        if (start != null && end != null)
                            ApplyPendingToRange(new TextRange(start, end));
                    }
                    catch { }
                }
            }
            finally
            {
                _applyingPendingFormat = false;
            }
        }

        // 自动保存富文本 JSON(防抖,避免每个按键都写盘)。
        ScheduleNoteFileAutoSave();
    }

    void ScheduleNoteFileAutoSave()
    {
        _autoSaveTimer?.Stop();
        _autoSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _autoSaveTimer.Tick += (_, _) =>
        {
            _autoSaveTimer.Stop();
            _autoSaveTimer = null;
            try { _notesService.SaveNoteFile(_note.Id, BuildNoteFileData()); } catch { }
        };
        _autoSaveTimer.Start();
    }

    // ── 格式读取/写入助手 ──
    // GetPropertyValue 对「未显式设置/继承」的值返回 UnsetValue,这里回退到
    // ContentBox 的默认值(FontWeight/FontStyle/FontSize/Foreground),保证读到有效值。

    object? EffectiveValue(DependencyProperty prop, object? inheritedFallback)
    {
        var sel = ContentBox.Selection;
        if (sel == null) return inheritedFallback;
        var v = sel.GetPropertyValue(prop);
        return v == DependencyProperty.UnsetValue ? inheritedFallback : v;
    }

    /// <summary>
    /// 无选区时把格式写到光标处:①写 WPF 光标插入格式;②把格式直接写到「光标所在的
    /// 空 Run」上。只有 ② 才能真正让光标高度跟着新字号变、并让空白处输入的首个字符
    /// 继承新格式 —— 空选区的 ApplyPropertyValue 只改插入属性,不会改变光标所在 Run/
    /// 新输入字符实际落进的相邻 Run(它们沿用旧属性,这就是「光标大小跟旧文字 / 首字符
    /// 继承旧属性」的根因)。非空 Run 不写,避免把已有文字改掉,新增文字由
    /// ContentBox_TextChanged 兜底补格式。
    /// </summary>
    void ApplyToCaret(DependencyProperty prop, object value)
    {
        var sel = ContentBox.Selection;
        if (sel == null) return;
        sel.ApplyPropertyValue(prop, value);
        ApplyFormatToCaretRun(prop, value);
    }

    /// <summary>把格式直接写到「光标所在的空 Run」上,空白便签/空段落均命中。</summary>
    void ApplyFormatToCaretRun(DependencyProperty prop, object value)
    {
        var caret = ContentBox.CaretPosition;
        if (caret == null) return;
        TextElement? el = caret.Parent as TextElement;
        while (el != null && el is not Run) el = el.Parent as TextElement;
        if (el is Run run && string.IsNullOrEmpty(run.Text))
        {
            try { run.SetValue(prop, value); } catch { }
        }
    }

    /// <summary>把已记录的待输入格式应用到一段文本范围(新输入的文字)。</summary>
    void ApplyPendingToRange(TextRange range)
    {
        if (_pendingWeight is FontWeight w) range.ApplyPropertyValue(TextElement.FontWeightProperty, w);
        if (_pendingStyle is FontStyle st) range.ApplyPropertyValue(TextElement.FontStyleProperty, st);
        if (_pendingUnderline is bool u) range.ApplyPropertyValue(Inline.TextDecorationsProperty, u ? TextDecorations.Underline : null);
        if (_pendingSize is double sz) range.ApplyPropertyValue(TextElement.FontSizeProperty, sz);
        if (_pendingColor is SolidColorBrush c) range.ApplyPropertyValue(TextElement.ForegroundProperty, c);
    }

    /// <summary>
    /// 把键盘焦点还给正文(延迟到 Input 优先级,确保 Popup/ComboBox 已关闭)。
    /// 这样点击上方格式按钮后,用户不用再点回正文即可继续输入,光标也不跳。
    /// 只 Focus、不重新 Select:重新 Select 会清掉刚写入的光标插入格式。
    /// </summary>
    void RefocusContentBox()
    {
        Dispatcher.BeginInvoke(() =>
        {
            try { ContentBox.Focus(); } catch { }
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    static bool IsBold(object? v) => v is FontWeight fw && fw.ToOpenTypeWeight() > 400;
    static bool IsItalic(object? v) => v is FontStyle fs && fs == FontStyles.Italic;
    static bool IsUnderlined(object? v) => v is TextDecorationCollection td && td.Count > 0;

    void UpdateFormatButtons()
    {
        var sel = ContentBox.Selection;
        bool hasSelection = sel != null && !sel.IsEmpty;

        // Bold
        object? boldCurrent;
        if (hasSelection) boldCurrent = sel!.GetPropertyValue(Inline.FontWeightProperty);
        else if (_pendingWeight is FontWeight pw) boldCurrent = pw;
        else boldCurrent = EffectiveValue(Inline.FontWeightProperty, ContentBox.FontWeight);
        BoldBtn.Background = IsBold(boldCurrent) ? FormatActiveBrush : Brushes.Transparent;

        // Italic
        object? italicCurrent;
        if (hasSelection) italicCurrent = sel!.GetPropertyValue(Inline.FontStyleProperty);
        else if (_pendingStyle is FontStyle ps) italicCurrent = ps;
        else italicCurrent = EffectiveValue(Inline.FontStyleProperty, ContentBox.FontStyle);
        ItalicBtn.Background = IsItalic(italicCurrent) ? FormatActiveBrush : Brushes.Transparent;

        // Underline
        object? underlineCurrent;
        if (hasSelection) underlineCurrent = sel!.GetPropertyValue(Inline.TextDecorationsProperty);
        else if (_pendingUnderline is bool pu) underlineCurrent = pu ? TextDecorations.Underline : null;
        else underlineCurrent = EffectiveValue(Inline.TextDecorationsProperty, null);
        UnderlineBtn.Background = IsUnderlined(underlineCurrent) ? FormatActiveBrush : Brushes.Transparent;

        // 字号下拉:有选区时反显选区字号;无选区时反显光标处字号(含待输入字号),
        // 保证下拉显示与正文/待输入格式实时同步(启动后第一次移动光标也会同步)。
        object? sizeCurrent;
        if (hasSelection) sizeCurrent = sel!.GetPropertyValue(TextElement.FontSizeProperty);
        else if (_pendingSize is double psz) sizeCurrent = psz;
        else sizeCurrent = EffectiveValue(TextElement.FontSizeProperty, ContentBox.FontSize);
        if (sizeCurrent is double fontSize)
        {
            foreach (ComboBoxItem item in FontSizeCombo.Items)
            {
                if (item.Tag is string tag && double.TryParse(tag, out var val)
                    && Math.Abs(val - fontSize) < 0.1)
                {
                    _updatingFormatButtons = true;
                    try { FontSizeCombo.SelectedItem = item; }
                    finally { _updatingFormatButtons = false; }
                    break;
                }
            }
        }

        // 文字颜色图标:有选区显示选区色,无选区显示待输入色/光标色。
        object? fg;
        if (hasSelection) fg = sel!.GetPropertyValue(Inline.ForegroundProperty);
        else if (_pendingColor is SolidColorBrush pc) fg = pc;
        else fg = EffectiveValue(Inline.ForegroundProperty, ContentBox.Foreground);
        if (fg is SolidColorBrush scb)
            FontColorBtn.Foreground = scb;
    }
}
