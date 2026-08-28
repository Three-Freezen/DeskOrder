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
                    // 跳过空 run(格式锚点):锚点只在编辑期有意义,保存后会
                    // 让旧颜色在重载时复活(「颜色跳到别的颜色」来源之一)。
                    if (run.Text.Length == 0) break;
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
                if (string.IsNullOrEmpty(rd.Text)) continue; // 防御:旧文件里的空锚点 run 不复活
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
        if (_vm?.IsLocked != true) Topmost = true;
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

    static bool IsWithinElement(object? src, FrameworkElement target)
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

    // ── Formatting toolbar handlers ──
    // 重构(Word 式富文本):格式统一经 ApplyFormat 写入。有选区 → 只改选区;
    // 无选区 → 写入 WPF 光标打字格式,后续输入自动继承。不再维护跨光标存活的
    // pending 状态机、也不在 TextChanged 里按偏移回写格式 —— 那是格式串位的根源。

    void FontSizeCombo_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        if (_initializing || _updatingFormatButtons) return;
        if (FontSizeCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag
            && double.TryParse(tag, out var fs))
        {
            ApplyFormat(TextElement.FontSizeProperty, fs);
            UpdateFormatButtons();
            // ComboBox 会抢焦点:应用完把焦点还给正文,用户可直接继续输入。
            RefocusContentBox();
        }
    }

    void BoldBtn_Click(object s, RoutedEventArgs e)
    {
        var current = EffectiveValue(Inline.FontWeightProperty, ContentBox.FontWeight);
        ApplyFormat(Inline.FontWeightProperty, IsBold(current) ? FontWeights.Normal : FontWeights.Bold);
        UpdateFormatButtons();
        RefocusContentBox();
    }

    void ItalicBtn_Click(object s, RoutedEventArgs e)
    {
        var current = EffectiveValue(Inline.FontStyleProperty, ContentBox.FontStyle);
        ApplyFormat(Inline.FontStyleProperty, IsItalic(current) ? FontStyles.Normal : FontStyles.Italic);
        UpdateFormatButtons();
        RefocusContentBox();
    }

    void UnderlineBtn_Click(object s, RoutedEventArgs e)
    {
        var current = EffectiveValue(Inline.TextDecorationsProperty, null);
        ApplyFormat(Inline.TextDecorationsProperty, IsUnderlined(current) ? null : TextDecorations.Underline);
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
    /// 选色应用 — 有选区给选区上色,无选区写入光标打字格式(后续输入自动继承),
    /// 并把「A」图标同步成所选颜色。格式串位修复的一部分:不再有跨光标存活的
    /// pending 颜色,颜色只作用在当下光标/选区上。
    /// </summary>
    void ApplyTextColor(SolidColorBrush brush, string? sourceHex = null)
    {
        ApplyFormat(TextElement.ForegroundProperty, brush);
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
        // 光标移动/选区变化 → 工具条反显光标处或选区处格式。格式本身不做任何
        // 写入,不干预用户输入,从机制上杜绝格式串位。
        UpdateFormatButtons();
    }

    /// <summary>
    /// 自动保存富文本 JSON(防抖,避免每个按键都写盘)。
    /// 待输入格式的一次性精确应用:刚设置过格式(pending 存在)且发生真实文本
    /// 插入时,暂存插入的文本并延迟到本次输入处理完成后应用(TextChanged 在
    /// 编辑器插入事务中途触发,同步写入会被编辑器后续步骤覆盖 —— 实测)。
    /// 应用完立即清空,之后其它位置的输入一律不受影响。
    /// </summary>
    void ContentBox_TextChanged(object s, TextChangedEventArgs e)
    {
        if (_initializing) return;
        if (_pendingFormat != null)
        {
            foreach (var change in e.Changes)
            {
                if (change.AddedLength <= 0) continue;
                try
                {
                    var start = ContentBox.Document.ContentStart
                        .GetPositionAtOffset(change.Offset, LogicalDirection.Forward);
                    var wideEnd = start?.GetPositionAtOffset(change.AddedLength, LogicalDirection.Forward);
                    if (start == null || wideEnd == null) continue;
                    var text = new TextRange(start, wideEnd).Text;
                    if (string.IsNullOrEmpty(text)) continue;
                    // 纯段符变更(回车)不消费 pending:回车后 WPF 会把拆分点前
                    // 文字的格式写成新光标的打字格式,若此时把格式写到新段落
                    // 反而会被打字格式压过;保持 pending,由回车后输入的第一个
                    // 字符照常精确应用。
                    if (text.All(c => c is '\r' or '\n')) continue;
                    _pendingInsertText = text;
                }
                catch { }
            }
            if (_pendingInsertText != null && !_pendingDeferredQueued)
            {
                _pendingDeferredQueued = true;
                var token = _pendingToken;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _pendingDeferredQueued = false;
                    if (token == _pendingToken) ApplyPendingDeferred();
                }), DispatcherPriority.Input);
            }
        }
        ScheduleNoteFileAutoSave();
    }

    /// <summary>
    /// 输入事务完成后,在记录位置附近精确匹配暂存的插入文本并应用格式。
    /// 只格式化「插入的那段文本」,绝不碰其它文字;先清状态再应用,
    /// 防止格式写入触发的 TextChanged 再次调度形成循环。
    /// </summary>
    void ApplyPendingDeferred()
    {
        if (_pendingFormat is not { } pf || _pendingInsertText is not { } target) return;
        var text = _pendingInsertText;
        var k = _pendingCaretOffset;
        ClearPendingFormat();
        DzTrace.Log($"[Fmt] deferred apply: text='{EscapeForLog(text)}' k={k}");
        try
        {
            if (k < 0) return;
            for (int delta = 0; delta <= 3; delta++)
            {
                var s = ContentBox.Document.ContentStart.GetPositionAtOffset(k + delta, LogicalDirection.Forward);
                if (s == null) break;
                var e2 = s.GetPositionAtOffset(text.Length, LogicalDirection.Forward);
                if (e2 == null) continue;
                var range = new TextRange(s, e2);
                if (range.Text == text)
                {
                    ApplyPendingToRange(pf, range);
                    DzTrace.Log($"[Fmt] deferred applied at delta=+{delta}");
                    return;
                }
            }
            for (int delta = 1; delta <= 2; delta++)
            {
                var s = ContentBox.Document.ContentStart.GetPositionAtOffset(k - delta, LogicalDirection.Forward);
                if (s == null) break;
                var e2 = s.GetPositionAtOffset(text.Length, LogicalDirection.Forward);
                if (e2 == null) continue;
                var range = new TextRange(s, e2);
                if (range.Text == text)
                {
                    ApplyPendingToRange(pf, range);
                    DzTrace.Log($"[Fmt] deferred applied at delta=-{delta}");
                    return;
                }
            }
            DzTrace.Log("[Fmt] deferred no-match in window, dropped");
        }
        catch { }
    }

    static string EscapeForLog(string s) => s.Replace("\r", "\\r").Replace("\n", "\\n");

    void ApplyPendingToRange(PendingFormat pf, TextRange range)
    {
        if (pf.Size is double sz) TryApplyFormat(range, TextElement.FontSizeProperty, sz);
        if (pf.Weight is FontWeight w) TryApplyFormat(range, TextElement.FontWeightProperty, w);
        if (pf.Style is FontStyle st) TryApplyFormat(range, TextElement.FontStyleProperty, st);
        if (pf.Underline is bool u) TryApplyFormat(range, Inline.TextDecorationsProperty, u ? TextDecorations.Underline : null);
        if (pf.Color is SolidColorBrush c) TryApplyFormat(range, TextElement.ForegroundProperty, c);
    }

    /// <summary>单项应用:文档在每次应用后会重排,单项各自 try 防止中途失败丢格式。</summary>
    static void TryApplyFormat(TextRange range, DependencyProperty prop, object? value)
    {
        try { range.ApplyPropertyValue(prop, value); } catch { }
    }

    /// <summary>方向键/Home/End/PageUp/PageDown = 主动移动光标 → 待输入格式复位(Word 行为)。</summary>
    void ContentBox_PreviewKeyDown(object s, KeyEventArgs e)
    {
        if (_pendingFormat == null) return;
        switch (e.Key)
        {
            case Key.Left:
            case Key.Right:
            case Key.Up:
            case Key.Down:
            case Key.Home:
            case Key.End:
            case Key.PageUp:
            case Key.PageDown:
                ClearPendingFormat();
                break;
        }
    }

    /// <summary>点击正文 = 光标定位 → 待输入格式复位,新位置沿用其原有格式(Word 行为)。</summary>
    void ContentBox_PreviewMouseLeftButtonDown(object s, MouseButtonEventArgs e)
    {
        if (_pendingFormat != null) ClearPendingFormat();
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

    // ── 光标待输入格式(Word 式,一次一位置) ──
    // 工具条点击(无选区)时记录;TextChanged 时精确应用到「本次插入的范围」
    // 后立即清空;方向键/点击定位也会清空 —— 格式只作用于设置处光标的后续
    // 输入。与旧版 pending 的关键区别:旧版跨光标移动永久存活 + 对任意
    // TextChange 无条件回写,这就是格式串位的根源。
    sealed record PendingFormat(double? Size, FontWeight? Weight, FontStyle? Style, bool? Underline, SolidColorBrush? Color);

    private PendingFormat? _pendingFormat;
    // 设置格式时快照的光标整数偏移(TextPointer 快照会随文档重组悬空,整数不会)。
    private int _pendingCaretOffset = -1;
    // 延迟应用:TextChanged 时暂存插入的文本,Input 优先级回调时在记录偏移附近
    // 精确匹配该文本并应用格式。
    private string? _pendingInsertText;
    // 每次设置/清空待输入格式自增;延迟回调校验令牌,过期回调直接丢弃。
    private int _pendingToken;
    private bool _pendingDeferredQueued;

    void SetPendingFormat(DependencyProperty prop, object? value)
    {
        _pendingFormat ??= new PendingFormat(null, null, null, null, null);
        if (prop == TextElement.FontSizeProperty && value is double d)
            _pendingFormat = _pendingFormat with { Size = d };
        else if (prop == TextElement.FontWeightProperty && value is FontWeight w)
            _pendingFormat = _pendingFormat with { Weight = w };
        else if (prop == TextElement.FontStyleProperty && value is FontStyle st)
            _pendingFormat = _pendingFormat with { Style = st };
        else if (prop == Inline.TextDecorationsProperty)
            _pendingFormat = _pendingFormat with { Underline = value != null };
        else if (prop == TextElement.ForegroundProperty)
            _pendingFormat = _pendingFormat with { Color = value as SolidColorBrush };
        try
        {
            _pendingCaretOffset = ContentBox.Document.ContentStart
                .GetOffsetToPosition(ContentBox.CaretPosition);
        }
        catch { _pendingCaretOffset = -1; }
        _pendingToken++;
    }

    /// <summary>
    /// 清待输入格式并删除相邻的空锚点 Run —— 锚点只在 pending 存活期内有效,
    /// 导航/定位后残留的锚点会把旧颜色带进以后输入的文字(「颜色莫名其妙
    /// 跳到别的颜色」的根源之一)。
    /// </summary>
    void ClearPendingFormat()
    {
        _pendingFormat = null;
        _pendingCaretOffset = -1;
        _pendingInsertText = null;
        _pendingToken++;
        RemoveAdjacentEmptyAnchorRuns();
    }

    void RemoveAdjacentEmptyAnchorRuns()
    {
        try
        {
            var caret = ContentBox.CaretPosition;
            if (caret == null) return;
            if (caret.Parent is Run inRun && inRun.Text.Length == 0 && inRun.Parent is Paragraph inPara)
            {
                inPara.Inlines.Remove(inRun);
                return;
            }
            var fwd = caret.GetInsertionPosition(LogicalDirection.Forward);
            if (fwd?.Parent is Run fRun && fRun.Text.Length == 0 && fRun.Parent is Paragraph fPara
                && ReferenceEquals(fPara.Inlines.LastInline, fRun))
                fPara.Inlines.Remove(fRun);
            var bwd = caret.GetInsertionPosition(LogicalDirection.Backward);
            if (bwd?.Parent is Run bRun && bRun.Text.Length == 0 && bRun.Parent is Paragraph bPara
                && ReferenceEquals(bPara.Inlines.LastInline, bRun))
                bPara.Inlines.Remove(bRun);
        }
        catch { }
    }

    object? EffectiveValue(DependencyProperty prop, object? inheritedFallback)
    {
        var sel = ContentBox.Selection;
        if (sel == null) return inheritedFallback;
        var v = sel.GetPropertyValue(prop);
        return v == DependencyProperty.UnsetValue ? inheritedFallback : v;
    }

    /// <summary>
    /// Word 式格式应用 — 唯一写入入口:
    /// ① 有选区 → 只格式化选区;
    /// ② 无选区 → 记录「光标待输入格式」并尝试建空 Run 锚点。空选区一律不写
    ///    WPF 打字格式 —— 实测两 run 边界处(前 run 尾/后 run 头)的
    ///    ApplyPropertyValue 会把前后 run 整体刷成新格式,这正是「设置新属性时
    ///    前面内容一起被改」的根源;无选区只走「锚点 + TextChanged 精确应用」。
    /// </summary>
    void ApplyFormat(DependencyProperty prop, object? value)
    {
        var sel = ContentBox.Selection;
        if (sel == null) return;
        if (!sel.IsEmpty)
        {
            sel.ApplyPropertyValue(prop, value);
            ClearPendingFormat();
            return;
        }
        SetPendingFormat(prop, value);
        EnsureCaretAnchorRun();
    }

    /// <summary>
    /// 空 Run 锚点(保证后续输入确定性地继承新格式):
    /// ① 光标在空 Run 内 ② 前方紧邻空 Run(上次遗留)③ 段落末尾 run 的行尾追加。
    /// 其它位置(run 开头/行中)不建锚点:实测 WPF 归一化会把锚点合并回去、
    /// 打字并入相邻 run,锚点无效;这些位置由 ContentBox_TextChanged 精确兜底。
    /// 锚点样式 = 「光标前方文字的当前格式」+ 全部待输入属性的合并快照:
    /// 复用旧锚点时先整体重置,上一次设置的颜色等属性绝不残留。
    /// </summary>
    void EnsureCaretAnchorRun()
    {
        var caret = ContentBox.CaretPosition;
        if (caret == null) return;

        // ① 光标所在 run 为空 → 直接写。
        TextElement? el = caret.Parent as TextElement;
        while (el != null && el is not Run) el = el.Parent as TextElement;
        if (el is Run run && string.IsNullOrEmpty(run.Text))
        {
            StyleAnchorRun(run);
            return;
        }

        // ② 光标前方紧邻空 run → 写它(防重复拆分堆积)。
        try
        {
            var fwd = caret.GetInsertionPosition(LogicalDirection.Forward);
            TextElement? fe = fwd?.Parent as TextElement;
            while (fe != null && fe is not Run) fe = fe.Parent as TextElement;
            if (fwd != null && fe is Run fwdRun && string.IsNullOrEmpty(fwdRun.Text)
                && fwdRun.ContentStart.CompareTo(fwd) == 0)
            {
                StyleAnchorRun(fwdRun);
                return;
            }
        }
        catch { /* 保守:走段末追加兜底 */ }

        // ③ 段落末尾 run 的行尾 → 段末追加带格式空 Run。
        if (el is not Run target || target.Parent is not Paragraph para) return;
        if (!ReferenceEquals(para.Inlines.LastInline, target)) return;
        int offset;
        try { offset = target.ContentStart.GetOffsetToPosition(caret); }
        catch { return; }
        if (offset != target.Text.Length || target.Text.Length == 0) return;
        var anchor = new Run("");
        para.Inlines.Add(anchor);
        StyleAnchorRun(anchor);
    }

    /// <summary>
    /// 锚点快照:对 5 个属性逐一写入「待输入值 ?? 光标前文字的有效值(无则框默认)」。
    /// 这样锚点 = 当前打字位置的格式快照 + 本次设置,后续输入确定性地继承。
    /// </summary>
    void StyleAnchorRun(Run anchor)
    {
        var pf = _pendingFormat;
        var basePos = ContentBox.CaretPosition?.GetInsertionPosition(LogicalDirection.Backward);
        TextElement? el = basePos?.Parent as TextElement;
        while (el != null && el is not Run) el = el.Parent as TextElement;
        var src = el as Run; // 光标前方文字所在 run(无则用框默认)
        void Set(DependencyProperty prop, object? pendingVal, object? fallback)
        {
            try { anchor.SetValue(prop, pendingVal ?? (src != null ? src.GetValue(prop) : fallback)); } catch { }
        }
        Set(TextElement.FontSizeProperty, pf?.Size, ContentBox.FontSize);
        Set(Inline.FontWeightProperty, pf?.Weight is FontWeight w ? w : null, ContentBox.FontWeight);
        Set(Inline.FontStyleProperty, pf?.Style is FontStyle st ? st : null, ContentBox.FontStyle);
        Set(Inline.TextDecorationsProperty, pf?.Underline is bool u ? (u ? TextDecorations.Underline : null) : null, null);
        Set(TextElement.ForegroundProperty, pf?.Color, ContentBox.Foreground);
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

    /// <summary>
    /// 工具条状态反显 — 纯读取,绝不写格式:
    /// 选区/光标处的实际格式驱动 B/I/U 高亮、字号下拉与「A」图标颜色;
    /// 刚设置过待输入格式时(含行中无法写打字格式的位置)优先反显 pending。
    /// </summary>
    void UpdateFormatButtons()
    {
        var sel = ContentBox.Selection;
        bool hasSelection = sel != null && !sel.IsEmpty;
        var pf = _pendingFormat;

        // Bold / Italic / Underline:有选区显选区,无选区显光标处(优先刚设置的待输入格式)。
        object? boldCurrent = hasSelection ? sel!.GetPropertyValue(Inline.FontWeightProperty)
            : pf?.Weight is FontWeight pw ? pw
            : EffectiveValue(Inline.FontWeightProperty, ContentBox.FontWeight);
        BoldBtn.Background = IsBold(boldCurrent) ? FormatActiveBrush : Brushes.Transparent;

        object? italicCurrent = hasSelection ? sel!.GetPropertyValue(Inline.FontStyleProperty)
            : pf?.Style is FontStyle ps ? ps
            : EffectiveValue(Inline.FontStyleProperty, ContentBox.FontStyle);
        ItalicBtn.Background = IsItalic(italicCurrent) ? FormatActiveBrush : Brushes.Transparent;

        object? underlineCurrent = hasSelection ? sel!.GetPropertyValue(Inline.TextDecorationsProperty)
            : pf?.Underline is bool pu ? (pu ? TextDecorations.Underline : null)
            : EffectiveValue(Inline.TextDecorationsProperty, null);
        UnderlineBtn.Background = IsUnderlined(underlineCurrent) ? FormatActiveBrush : Brushes.Transparent;

        // 字号下拉:有选区反显选区字号;无选区反显光标处字号(优先待输入格式)。
        object? sizeCurrent = hasSelection ? sel!.GetPropertyValue(TextElement.FontSizeProperty)
            : pf?.Size is double psz ? psz
            : EffectiveValue(TextElement.FontSizeProperty, ContentBox.FontSize);
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

        // 文字颜色图标:有选区显示选区色,无选区显示光标处色(优先待输入格式)。
        object? fg = hasSelection ? sel!.GetPropertyValue(Inline.ForegroundProperty)
            : pf?.Color is SolidColorBrush pc ? pc
            : EffectiveValue(Inline.ForegroundProperty, ContentBox.Foreground);
        if (fg is SolidColorBrush scb)
            FontColorBtn.Foreground = scb;
    }
}
