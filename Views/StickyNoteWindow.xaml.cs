using System;
using System.Collections.Generic;
using System.Globalization;
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
    // ponytail: 悬停高光已收口到 Helpers/HoverHighlight.cs（全应用唯一配方 #30FFFFFF）。
    // 原来的 PinHoverBrush / LockHoverBrush 与 PinBtn_Enter/Leave 等手工换色已删除。
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
    // ponytail: cached button-color brush for title bar buttons. Set by ApplyTitleBar from
    // _note.ButtonColor; hover/click handlers read it so the hover→leave cycle doesn't clobber it.
    private SolidColorBrush? _buttonBrush;
    private HoverExpandBehavior? _hover;
    private SnapDrag? _snapDrag;
    private SnapResize? _snapResize;

    // 字体颜色弹层 — 当前文字颜色(8 位 AARRGGBB)。语言切换重建色板后恢复高亮环,
    // 与「A」图标同源(都由 UpdateFormatButtons 写,见 2026-11-28 修复)。
    private string _lastTextColorHex = "#FFE0E0E0";

    // 程序化同步工具条(字体族/字号/颜色)时为 true,防止 SelectionChanged 再触发应用逻辑(防循环)。
    private bool _updatingFormatButtons;
    private DispatcherTimer? _autoSaveTimer;

    // ── 当前笔触(Word 式,2026-11-28) ──
    // 无选区时改格式只记在这里(= 下一个输入的字用什么格式),**纯内存、绝不写文档**;
    // 之后每次插入都按 TextChanged 的 change 范围精确刷到新文字上,直到光标移动/
    // 点击正文/失焦/粘贴。有选区时改格式只改选区,同时把结果并入这里(接着打字沿用)。
    private TypingFormat? _typingFormat;
    // 回刷自身会触发 TextChanged,用它防递归。
    private bool _writingFormat;
    // 正在加载正文(JSON/纯文本)时为 true:此时的变化不是用户输入,不许刷笔触。
    private bool _loadingContent;
    // 字号下拉里临时插入的「非预设值」条目(反显 13.5 / 22 这类正文实际字号)。
    private ComboBoxItem? _customSizeItem;

    // ponytail: 位置防抖保存 — 拖拽移动后持久化 X/Y（与分区 ZoneWindow 一致）。
    private readonly DispatcherTimer _positionSaveDebounce = new() { Interval = TimeSpan.FromMilliseconds(500) };
    // ponytail 2026-09-26: 标题栏改名的"停手即提交"防抖(同 ZoneWindow)。
    private readonly DispatcherTimer _titleCommitDebounce = new() { Interval = TimeSpan.FromMilliseconds(700) };
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
        // 右键菜单图标（方案 B · 简笔）：锁定图标复用标题栏那颗按钮的字符。
        MenuIconBinder.Hook(this, () => LockBtn.Content as string);
        _note = note;
        _notesService = notesService;
        _vm = new StickyNoteViewModel(note);
        // ponytail: VM starts at default false — pull persisted lock state from model so a
        // reloaded locked note shows 🔒, not 🔓. Matches ZoneWindow ctor pattern.
        _vm.IsLocked = note.IsLocked;
        DataContext = _vm;

        // 2026-11-28: 「A」图标初值(文档样式是正文的兜底格式;字体族下拉由 ApplyLoc 建)。
        _lastTextColorHex = note.TextColor;

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
        // ponytail 2026-09-26: 便签标题同样"停手即提交" —— 见 ZoneWindow.ZoneTitle_TextChanged
        // 的说明(在窗口内点不可聚焦处不会触发 LostFocus,只靠 Enter/失焦会丢改名)。
        _titleCommitDebounce.Tick += (_, _) => CommitTitleRename();
        TitleBox.TextChanged += TitleBox_TextChanged;
        Deactivated += (_, _) => CommitTitleRename();
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

        // 2026-11-28: 粘贴 / 拖入外部内容保留源格式(Word 默认),不套用当前笔触;
        // 键盘焦点离开正文也算「光标要走了」,笔触复位。
        DataObject.AddPastingHandler(ContentBox, (_, _) => ClearTypingFormat());
        ContentBox.PreviewDrop += (_, _) => ClearTypingFormat();
        ContentBox.LostKeyboardFocus += (_, _) => ClearTypingFormat();

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
        // 2026-11-28: 工具条初值 = 文档样式(之前被 _initializing 挡掉,固定停在 XAML 的 14)。
        UpdateFormatButtons();
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
        // ponytail 2026-08-28: 恢复按钮态钉在桌面层(见 HideNote),展开(悬停预览或
        // 点击固定)时把便签本体恢复到浮动策略——与 ShowNote 的 Topmost+PinToDesktop
        // 一致;锁定便签保持桌面层不浮动。
        _hover.Expanded += OnNoteExpanded;
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
        // ponytail 2026-09-26: VM 的 Title 也要跟模型同步 —— 它是 ApplyToModel() 回写
        // 模型的源(自动保存 / 关窗都会调),只刷新 TitleBox 的话,别处改的名字会在
        // 下一次自动保存时被 VM 里的旧值写回去。
        _vm.Title = _note.Title;
        if (TitleBox != null) TitleBox.Text = _note.Title;
        // ponytail 2026-08-26: re-pin / Topmost only while the window is still up.
        // OnClosed → UpdateNote → NotesChanged re-enters this handler while
        // WmDestroy is tearing the window down; PinToDesktop would call
        // WindowInteropHelper.EnsureHandle → "关闭窗口后，无法设置可见性…"
        // InvalidOperationException (crash when 全部隐藏 closes note windows).
        if (IsVisible)
        {
            // ponytail 2026-08-28: 收起成恢复按钮的便签钉在桌面层(HideNote 同策略),
            // 任何模型同步都不把它浮回应用窗口之上;展开的便签维持各自浮动策略。
            if (_hover is { IsExpanded: false } || MainContent.Visibility != Visibility.Visible)
            {
                Topmost = false;
                NativeMethods.PinBelowProgman(this);
            }
            else if (_note.PinnedTop) Topmost = true;
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
        // 加载期间置位:这些 TextChanged 不是用户输入,不许触发「当前笔触」回刷。
        _loadingContent = true;
        try
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
        finally { _loadingContent = false; }
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
            // v1 → v2(2026-11-28):旧文件里「等于旧默认值」的显式格式降级成「继承文档样式」,
            // 并立即写回一次 —— 否则每次打开都要重新迁移,而且老便签永远不跟随字体设置。
            if (fileData.NormalizeLegacyFormat())
                _notesService.SaveNoteFile(_note.Id, fileData);
            LoadFromNoteFileData(fileData);
        }
        else
        {
            LoadContent(_note.Content);
        }
    }

    NoteFileData BuildNoteFileData()
    {
        var data = new NoteFileData { Version = NoteFileData.CurrentVersion };
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
                    // 跳过空 run(编辑期残留的锚点):保存为空 run 没有任何意义。
                    if (run.Text.Length == 0) break;
                    // v2:只存「inline 链上真正写过」的格式;继承自文档样式的值一律写 null,
                    // 这样以后改便签字体时,这些文字才会跟着变。
                    runs.Add(new NoteRunData
                    {
                        Text = run.Text,
                        Bold = TryLocalInlineValue(run, TextElement.FontWeightProperty) is FontWeight fw ? NoteFormatting.IsBold(fw) : null,
                        Italic = TryLocalInlineValue(run, TextElement.FontStyleProperty) is FontStyle fs ? NoteFormatting.IsItalic(fs) : null,
                        Underline = TryLocalInlineValue(run, Inline.TextDecorationsProperty) is TextDecorationCollection td ? NoteFormatting.IsUnderlined(td) : null,
                        Size = TryLocalInlineValue(run, TextElement.FontSizeProperty) is double d ? d : null,
                        Color = TryLocalInlineValue(run, TextElement.ForegroundProperty) is SolidColorBrush b ? NoteFormatting.ToHex(b.Color) : null
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

    /// <summary>inline 链(run → span → paragraph)上真正 write 过的值;继承来的不算。</summary>
    static object? TryLocalInlineValue(TextElement el, DependencyProperty prop)
    {
        for (TextElement? e = el; e != null; e = e.Parent as TextElement)
        {
            var v = e.ReadLocalValue(prop);
            if (v != DependencyProperty.UnsetValue) return v;
        }
        return null;
    }

    void LoadFromNoteFileData(NoteFileData data)
    {
        _loadingContent = true;
        try { BuildDocumentFromNoteFileData(data); }
        finally { _loadingContent = false; }
    }

    void BuildDocumentFromNoteFileData(NoteFileData data)
    {
        var doc = new FlowDocument { LineHeight = double.NaN };
        foreach (var pd in data.Paragraphs)
        {
            var para = new Paragraph();
            foreach (var rd in pd.Runs)
            {
                if (string.IsNullOrEmpty(rd.Text)) continue; // 防御:旧文件里的空锚点 run 不复活
                var run = new Run(rd.Text);
                // v2:null = 继承文档样式,不写本地值(写了就成了「钉死」)。
                if (rd.Bold is bool b) run.FontWeight = b ? FontWeights.Bold : FontWeights.Normal;
                if (rd.Italic is bool i) run.FontStyle = i ? FontStyles.Italic : FontStyles.Normal;
                if (rd.Underline == true) run.TextDecorations = TextDecorations.Underline;
                if (rd.Size is double sz && sz > 0) run.FontSize = sz;
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
        var saveItem = new MenuItem { Header = _loc["Note.Save"], Icon = MenuIconFactory.Icon("Menu.Save") };
        saveItem.Click += (_, _) => SaveToFile();
        var saveAsItem = new MenuItem { Header = _loc["Note.SaveAs"], Icon = MenuIconFactory.Icon("Menu.SaveAs") };
        saveAsItem.Click += (_, _) => SaveAsToFile();
        menu.Items.Add(saveItem);
        menu.Items.Add(saveAsItem);
        menu.Items.Add(new Separator());
        var openItem = new MenuItem { Header = _loc["Note.OpenFile"], Icon = MenuIconFactory.Icon("Menu.Open") };
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
                    // 没写 size/color 就不写本地值 —— 让它继承便签的文档样式(v2 语义)。
                    if (para.FontSize > 0) run.FontSize = para.FontSize;
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
                    if (para.FontSize > 0) run.FontSize = para.FontSize;
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
        // 关掉 Win11 给窗口描的那圈 1px 边框（否则它压在自己画的边框外面 = 双层边框，
        // 角上还从描边弧外面绕过去）。与快捷栏 ApplyDockWindowFrame 同一个理由。
        NativeMethods.RemoveWindowBorder(this);
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
        // ponytail 2026-09-26「先出整窗框再展开」修复:窗口尺寸必须在**播展开动画之前**就位。
        // 原先这句 Width/Height 排在 ApplyAcrylic 之后 —— 整窗隐藏态(缩到 36×36)下由快捷键
        // 唤出时,动画第一帧会先出现一个全尺寸空窗轮廓,内容随后才展开(关闭路径无此问题,
        // 因为收起不动窗口尺寸)。改动时务必保持「先定尺寸再动画」。
        MinWidth = 180; MinHeight = 120;
        Width = savedW; Height = _note.TileMode ? Math.Max(120, savedH - NoteTileTitleBarCut) : savedH;
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
            // ponytail 2026-08-28: 从恢复按钮态展开走展开动画(与 CollapseAnimated
            // 对称——关有开也要有);已展开的重复 Show 仍瞬时对齐,不重播。
            bool fromButton = RestoreButton.Visibility == Visibility.Visible;
            MainContent.Visibility = Visibility.Visible; RestoreButton.Visibility = Visibility.Collapsed;
            if (fromButton) _hover?.ExpandAnimated(permanent: true);
            else _hover?.SnapToExpanded();
        }
        // ponytail: ghost-glass fix — re-apply acrylic AFTER SnapToExpanded so the
        // expanded-state gate sees IsExpanded == true and re-enables liquid glass when
        // showing from the collapsed button.
        ApplyAcrylic();
        _note.IsVisible = true; if (_vm?.IsLocked != true) NativeMethods.PinToDesktop(this);
        NativeMethods.SetRoundedCorners(this, _note.CornerRadius);
        _notesService.UpdateNote(_note);
        // 尺寸已在本方法开头(动画之前)设好 —— 这里不再重复设置,保持「先定尺寸再动画」。
        // ponytail: locked notes stay below app windows — Topmost would re-pin above them and
        // defeat PinBelowProgman. PinnedTop notes still get Topmost via the constructor branch.
        if (_vm?.IsLocked != true) Topmost = true;
        Activate();
    }

    // ── 快捷键唤出(参考面板 hotkey:聚焦显示器居中 + 展开动画) ──

    /// <summary>当前焦点显示器工作区换算成 DIP(与 Left/Top 同坐标系)。
    /// MonitorHelper 返回物理像素,按本窗口 DPI 换算(与 PanelWindow 约定一致)。</summary>
    Rect FocusedWorkAreaDip()
    {
        var waPx = MonitorHelper.FocusedWorkArea();
        double sx = 1, sy = 1;
        try { var d = VisualTreeHelper.GetDpi(this); sx = d.DpiScaleX; sy = d.DpiScaleY; } catch { }
        return new Rect(waPx.Left / sx, waPx.Top / sy, waPx.Width / sx, waPx.Height / sy);
    }

    /// <summary>把便签定位到当前聚焦显示器工作区正中央。同步写回 _note.X/Y ——
    /// ShowNote 会用模型坐标覆盖 Left/Top,只改窗口属性会被冲掉。</summary>
    public void CenterOnFocusedScreen()
    {
        var wa = FocusedWorkAreaDip();
        double left = wa.Left + (wa.Width - Width) / 2;
        double top = wa.Top + (wa.Height - Height) / 2;
        _note.X = left; _note.Y = top;
        Left = left; Top = top;
    }

    /// <summary>便签当前坐标是否还落在虚拟桌面内。拔掉副屏 / 改分辨率后 X/Y 会留在
    /// 旧坐标上,这时「按原位置打开」= 便签永远看不见、快捷键也救不回来(托盘显示
    /// 同样按 X/Y 打开),所以这种情况退化成居中。X/Y 与窗口 Left/Top 同步
    /// (LocationChanged + 各拖拽路径都写回),收起态也成立。</summary>
    bool NotePositionReachable()
    {
        double w = _note.Width > 0 ? _note.Width : Width;
        double h = _note.Height > 0 ? _note.Height : Height;
        double vl = SystemParameters.VirtualScreenLeft, vt = SystemParameters.VirtualScreenTop;
        double vr = vl + SystemParameters.VirtualScreenWidth, vb = vt + SystemParameters.VirtualScreenHeight;
        return _note.X + w > vl && _note.X < vr && _note.Y + h > vt && _note.Y < vb;
    }

    /// <summary>快捷键唤出:按便签自己的展开动画(种类/速度/原点=恢复按钮中心或
    /// 左上角)展开。恢复按钮态从按钮动画展开;整窗隐藏的便签先恢复尺寸再显示;
    /// 无恢复按钮的便签直接显示。定位由「固定位置打开」(便签设置→开关区)决定:
    /// 勾选 = 聚焦显示器居中(旧行为);默认不勾 = 按便签自己的位置打开。
    /// 非快捷键路径(托盘/按钮点击)不受影响,始终在历史位置展开。</summary>
    public void ShowFromHotkey()
    {
        bool fromButton = RestoreButton.Visibility == Visibility.Visible;

        // 整窗隐藏的便签先恢复窗口尺寸(HideNote 缩到了 36×36;与 ApplyHidden 同款)
        if (!IsVisible && !_note.EnableRestoreButton)
        {
            MinWidth = 180; MinHeight = 120;
            Width = _note.Width < 200 ? 260 : _note.Width;
            Height = _note.Height < 150 ? 200 : NoteWindowHeight();
        }

        // ponytail 2026-08-30: 快捷键唤出定位 —— 「固定位置打开」勾选时才居中。
        // 不勾(默认)= 不碰窗口坐标,各状态各自成立:
        //   • 展开态 / 恢复按钮态:窗口保持原位(收起态窗口仍是整窗大小,恢复按钮
        //     按 HoverExpandOrigin 停在角落或正中),原地展开即「便签当前位置」。
        //   • 整窗隐藏态:窗口被 HideNote 缩到 36×36 时左上角没动,ShowNote 会按
        //     _note.X/Y 重新摆回 → 正是「上一次显示的位置」。
        // 之前无条件居中的话,放在副屏或角落的便签每次按快捷键都会被搬到屏幕中间。
        if (_note.OpenAtFixedPosition || !NotePositionReachable()) CenterOnFocusedScreen();

        if (fromButton && _hover != null)
        {
            // 从恢复按钮态展开:走便签自己的展开动画,原点跟随 HoverExpandOrigin 设置
            if (!IsVisible) Show();
            _note.IsVisible = true;
            _hover.ExpandAnimated(permanent: true);
            _notesService.UpdateNote(_note);
            Activate();
            return;
        }

        ShowNote();
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
            // ponytail 2026-08-28: 收起成恢复按钮后钉回桌面层(壁纸上方、应用窗口
            // 之下) — 此前未锁定便签用 PinToDesktop(HWND_TOP),恢复按钮会浮在
            // 其它应用窗口上。必须先清 Topmost:带 WS_EX_TOPMOST 的窗口一旦被
            // 点击激活就会跳回最上层。便签本体展开时由 OnNoteExpanded 恢复浮动。
            AcrylicHelper.DisableBlur(this);
            Topmost = false;
            if (IsVisible) NativeMethods.PinBelowProgman(this);
            if (waveDelayMs > 0)
                _hover?.CollapseAfterDelay(waveDelayMs, null);
            else
                _hover?.CollapseAnimated();
        }
        _note.IsVisible = false;
        _notesService.UpdateNote(_note);
#if DEBUG
        DzTrace.Log($"[StickyNoteWindow] HideNote DONE winVisible={IsVisible} content={MainContent.Visibility} btn={RestoreButton.Visibility} hoverExpanded={_hover?.IsExpanded} size={Width}x{Height}");
#endif
    }

    /// <summary>ponytail 2026-08-28: 恢复按钮态钉在桌面层(HideNote),展开时便签
    /// 本体恢复浮动策略(未锁定:Topmost + HWND_TOP,与 ShowNote 一致);
    /// 锁定便签保持桌面层。悬停预览与点击固定展开共用此入口。</summary>
    void OnNoteExpanded()
    {
        if (_vm?.IsLocked == true) return;
        Topmost = true;
        if (IsVisible) NativeMethods.PinToDesktop(this);
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
            // ponytail 2026-08-28: 会话恢复的隐藏便签,恢复按钮态同样钉桌面层
            // (与 HideNote 一致)。BeginInvoke 等 Show 之后再钉——PinBelowProgman
            // 带 SWP_SHOWWINDOW,窗口未显示时提前调用会把窗口提前亮出来。
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (IsVisible) { Topmost = false; NativeMethods.PinBelowProgman(this); }
            }), System.Windows.Threading.DispatcherPriority.Loaded);
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
                // 恢复按钮态钉在桌面层(与 HideNote 收起路径一致),拖完不浮回应用窗口之上。
                if (IsVisible) NativeMethods.PinBelowProgman(this);
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

    // ── 自绘背板(方案①:壁纸采样 + 可变半径模糊) ──

    WallpaperBackdropLayer? _wallpaperBackdrop;

    /// <summary>材质 = 「自绘背板」通道时,把壁纸采样层插到填充层之下并关掉 DWM 玻璃。
    /// 返回 true = 背板已接管;壁纸不可用/材质=自定义/开关关闭 → false(回退 DWM)。</summary>
    bool TryApplyWallpaperBackdrop(string fillColorStr)
    {
        // ponytail 2026-09-26(二期修订): 回退 DWM 前必须先把背板层藏起来 —— 它是不透明的
        // 壁纸裁剪图,留着就会盖住 DWM 玻璃及着色,表现为"改颜色没反应"(见 ZoneWindow 同款注释)。
        if (!AcrylicHelper.ResolveSelfDrawn())
        {
            _wallpaperBackdrop?.SetVisible(false);
            return false;
        }
        _wallpaperBackdrop ??= WallpaperBackdropLayer.TryCreate();
        if (_wallpaperBackdrop == null) return false;

        _wallpaperBackdrop.Attach(MainContent, BodyFillRect);
        _wallpaperBackdrop.SetVisible(true);
        _wallpaperBackdrop.BindWindow(this);
        _wallpaperBackdrop.SetAppearance(this,
            AcrylicHelper.ResolveSelfDrawnRadiusPx(_note.GlassBlurAmount),
            AcrylicHelper.ResolveBackdropTint(fillColorStr, 1.0, _note.GlassColorMode,
                _note.GlassTintOpacity, _note.GlassTintLuminosity),
            AcrylicHelper.ResolveNoiseOpacity(_note.GlassMaterial));
        AcrylicHelper.DisableBlur(this);
        return true;
    }

    public void ApplyAcrylic()
    {
        string fillColorStr = _note.FillColor;

        // ponytail: ghost-glass fix — see ZoneWindow. A collapsed note keeps its full-size
        // window, so enabling blur here would paint the tint across the whole window.
        // Only enable while content is expanded; collapsed / acrylic-off → disable.
        bool expanded = _hover?.IsExpanded ?? false;
        if (_note.EnableLiquidGlass && expanded)
        {
            // ponytail 2026-09-26 方案①: 选了材质 → 壁纸采样自绘背板(半径真正生效);
            // 壁纸不可用/自定义 → 落回 DWM。
            if (TryApplyWallpaperBackdrop(fillColorStr))
            {
                // 边框跟随分区：不再叠那条彩色渐变（CreateChromaticBorder 是液态玻璃的旧装饰），
                // 可见边框就是 ApplyStyle 里设的「边框颜色 / 线宽」。
                if (NoteGlassBorder != null) NoteGlassBorder.BorderThickness = new Thickness(0);
                if (BodyFillRect != null) BodyFillRect.Fill = AcrylicHelper.HitTestFill;
                return;
            }

            // ponytail 2026-08-30: 一体化 — 填充并入玻璃 tint(算一层),BodyFillRect 透明;
            // 填充色与玻璃配色作为两个输入本质上仍是两层。
            var blurResult = AcrylicHelper.EnableBlurComposite(this, _note.GlassBlurAmount,
                fillColorStr, 1.0, _note.GlassColorMode, _note.GlassTintOpacity, _note.GlassTintLuminosity,
                AcrylicHelper.ResolveAccentState(_note.GlassMaterial),
                AcrylicHelper.ResolveUseClassicBlur(_note.GlassMaterial));
            if (!blurResult.Success)
                System.Diagnostics.Debug.WriteLine($"[StickyNoteWindow] EnableBlur failed: {blurResult.Error}");
            // 同上：边框跟随分区。
            if (NoteGlassBorder != null) NoteGlassBorder.BorderThickness = new Thickness(0);
            // ponytail 2026-09-26: 非亚克力材质(毛玻璃/清透/液态/深色玻璃)的 DWM 背板不认着色
            // (实测 state3 完全不着色),合成着色改由 BodyFillRect 承担;亚克力系返回 null →
            // 保持透明,着色交给 DWM(历史行为)。
            BodyFillRect.Fill = AcrylicHelper.ResolveWpfGlassTintBrush(_note.GlassMaterial,
                fillColorStr, 1.0, _note.GlassColorMode, _note.GlassTintOpacity, _note.GlassTintLuminosity)
                ?? AcrylicHelper.HitTestFill;
        }
        else
        {
            // ponytail: ghost-glass fix — this branch previously never disabled blur, so
            // toggling acrylic off (or collapsing) could leave the full-window blur behind.
            AcrylicHelper.DisableBlur(this);
            _wallpaperBackdrop?.SetVisible(false);
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
        // 2026-11-28: 正文的「文档样式」—— 放在最前面,任何一次外观刷新都会同步它。
        ApplyTextStyle();
        try
        {
            var bc = (Color)ColorConverter.ConvertFromString(_note.BorderColor);
            NoteBorder.BorderBrush = new SolidColorBrush(bc);
            NoteBorder.BorderThickness = new Thickness(_note.BorderThickness);
        }
        catch { }

        // ponytail 2026-08-26: 圆角/尖角 switch — corner elements + DWM lockstep.
        int r = _note.CornerRadius;
        // 可见半径压到 Windows 给窗口切的那个值（8 DIP）：否则四个角上 DWM 切出来的玻璃
        // 会从 WPF 画的描边弧外面露出一小条（见 NativeMethods.VisibleCornerRadius）。
        int rv = NativeMethods.VisibleCornerRadius(r, _note.EnableLiquidGlass && !AcrylicHelper.ResolveSelfDrawn());
        MainContent.CornerRadius = new CornerRadius(rv);
        NoteBorder.CornerRadius = new CornerRadius(rv);
        if (NoteGlassBorder != null)
            NoteGlassBorder.CornerRadius = new CornerRadius(rv);
        TitleBarBorder.CornerRadius = new CornerRadius(rv, rv, 0, 0);
        // 磁贴模式:砍掉最上面一层标题栏(格式化工具栏保留)。
        TitleBarBorder.Visibility = _note.TileMode ? Visibility.Collapsed : Visibility.Visible;
        if (BodyFillRect != null)
            BodyFillRect.RadiusX = BodyFillRect.RadiusY = _note.TitleBarFillIndependent ? 0 : rv;
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

    /// <summary>
    /// 便签正文的「文档样式」:字号 / 文字颜色(2026-11-28)。
    ///
    /// 这是修复「字体设置跟着文本框内容走」的核心之一:只改 RichTextBox 的控件级属性,
    /// **不碰任何 run 的本地值**。没有单独设置过格式的文字(JSON 里 Size/Color
    /// 为 null)会自动继承这两个值 —— 所以设置面板改一次,整张便签立即跟随;而用户真的
    /// 手动改过格式的文字仍然保持自己的覆盖值。
    /// </summary>
    void ApplyTextStyle()
    {
        try
        {
            ContentBox.FontSize = _note.FontSize > 0 ? _note.FontSize : 14;
            var brush = FrozenBrush(ParseColor(_note.TextColor, Color.FromRgb(0xE0, 0xE0, 0xE0)));
            ContentBox.Foreground = brush;
            ContentBox.CaretBrush = brush;
        }
        catch { }
    }

    static Color ParseColor(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try { return (Color)ColorConverter.ConvertFromString(hex)!; }
        catch { return fallback; }
    }

    static SolidColorBrush FrozenBrush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
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
            // 字号/字体族数字跟随系统深浅色（Menu.Text.Primary），不再锁定白色。
            if (FontSizeCombo != null)
                FontSizeCombo.SetResourceReference(Control.ForegroundProperty, "Menu.Text.Primary");

            // 按钮透明度 — 所有小按钮统一走 HoverHighlight 的那一个值（分区/组件/面板一致）。
            HoverHighlight.ApplyControlOpacity(_note.ControlOpacity,
                new UIElement?[] { SaveBtn, PinBtn, HideBtn, LockBtn, BoldBtn, ItalicBtn, UnderlineBtn, FontColorBtn });
            if (RestoreIconChar != null) RestoreIconChar.Opacity = HoverHighlight.ChromeOpacity;
            if (RestoreIconPath != null) RestoreIconPath.Opacity = HoverHighlight.ChromeOpacity;
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
        // ponytail: live-preview the title name too — RefreshAppearance re-applies the
        // chrome (icon/colors/glass) but previously never re-wrote the title TextBox, so
        // renaming a note in the style panel left the on-screen title stale until Apply.
        // Keep the VM in sync as well so a later inline title edit compares against the
        // current name rather than the stale constructor-time value.
        _vm.Note = _note;
        _vm.Title = _note.Title;
        if (TitleBox != null) TitleBox.Text = _note.Title;
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
        // 2026-11-28: 设置面板改「字体/字号/文字颜色」时工具条也要跟上
        // (正文本身由 ApplyStyle → ApplyTextStyle 即时更新)。
        UpdateFormatButtons();
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
        MenuIconBinder.Apply(NoteBorder.ContextMenu, LockBtn.Content as string);
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
        // 2026-11-28: 字体族下拉里「系统默认」的文案也跟语言走(重建会重置选中项,
        // 这里再按当前状态反显一次)。
        if (!_initializing) UpdateFormatButtons();
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
    void TitleBox_LostFocus(object s, RoutedEventArgs e) => CommitTitleRename();

    void TitleBox_KeyDown(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitTitleRename();
            ContentBox.Focus();
            e.Handled = true;
        }
    }

    /// <summary>
    /// 标题栏内联改名的唯一提交口(Enter 与失焦共用)。
    /// ponytail 2026-09-26: 走 <see cref="NotesService.RenameNote"/>(模型 + 立即落盘 +
    /// NotesChanged),设置界面实时跟上,不做"二次应用";空名 / 同名视为放弃。
    /// <c>_vm.Title</c> 必须一起同步 —— 它是 <c>ApplyToModel()</c>(自动保存 / 关窗)回写
    /// 模型的源,不同步的话下一次自动保存就会把刚改的名字写回旧值。
    /// 正文 / 富文本不由改名路径落盘:编辑器自己的 400ms 自动保存 + 关窗兜底本来就在管,
    /// 改名只碰标题(旧实现借 Save() 顺手刷一次正文,不是必要语义)。
    /// </summary>
    void CommitTitleRename()
    {
        _titleCommitDebounce.Stop();
        var text = TitleBox.Text?.Trim() ?? "";
        if (_notesService.RenameNote(_note, text))
        {
            _vm.Title = _note.Title;
            PropertyWindowManager.Instance.RefreshEditorTitle(_note);
        }
        TitleBox.Text = _note.Title ?? "";
    }

    /// <summary>
    /// 停手 700ms 自动提交(与 <see cref="ZoneWindow"/> 的标题栏同策)。
    /// ponytail 2026-09-26: 便签标题原来只在 Enter / LostFocus 提交 —— 用户敲完名字点
    /// 便签空白处(或在窗口内点不可聚焦的元素)时焦点根本不移出 TextBox,LostFocus 不触发,
    /// 标题框里显示着新名、模型与设置界面却还是旧名;更糟的是关窗时 <c>_vm.ApplyToModel()</c>
    /// 会用 VM 里的旧值把模型写回去,改名彻底丢失。
    /// 文本与已保存值不同才起表;提交后的回填会让 TextChanged 再进一次(那时文本 ==
    /// 已保存值 → 不再起表),不会自激。
    /// </summary>
    void TitleBox_TextChanged(object s, TextChangedEventArgs e)
    {
        var text = TitleBox.Text?.Trim() ?? "";
        if (text.Length == 0 || text == (_note.Title ?? ""))
        {
            _titleCommitDebounce.Stop();
            return;
        }
        _titleCommitDebounce.Stop();
        _titleCommitDebounce.Start();
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
        // ponytail 2026-11-14(审计修复 P2-9): 刚被热键 BringToFront 过的 500ms 兜底
        // timer 若没停,会在窗口关闭后继续跑并对已关闭窗口 PinToDesktop(同文件注释里
        // 记录过的 "关闭窗口后无法设置可见性" 一类崩溃)。
        _bringToFrontTimer?.Stop();
        _bringToFrontTimer = null;
        _autoSaveTimer?.Stop();
        _autoSaveTimer = null;
        // ponytail: unsubscribe BEFORE 任何会触发 NotesChanged 的写盘 —— 关窗收尾期间重入
        // 自己的 OnNotesChanged / OnServiceLockChanged 会撞上 EnsureHandle →
        // "关闭窗口后，无法设置可见性…" 并让应用在退出时崩溃(见下面 UpdateNote 处的历史说明)。
        // 2026-09-26:摘订阅提到最前面,因为下面新增的标题提交也会走 UpdateNote。
        _notesService.LockChanged -= OnServiceLockChanged;
        _notesService.NotesChanged -= OnNotesChanged;
        // ponytail: 关窗前落盘未保存的位置。
        _positionSaveDebounce.Stop();
        if (_positionSavePending && !_deleted) { _positionSavePending = false; _notesService.Save(); }
        // ponytail 2026-09-26: 标题栏里还没提交的改名先落盘 —— 必须在 _vm.ApplyToModel()
        // 之前:VM 的 Title 还停在构造时的旧值,晚一步就会把用户刚敲的名字覆盖回去。
        _titleCommitDebounce.Stop();
        if (!_deleted) CommitTitleRename();
        _vm.Content = SaveContent();
        _vm.ApplyToModel();
        if (!_deleted)
        {
            // 关窗前把富文本正文落盘一次(删除便签时不再写回,避免孤儿文件)。
            _notesService.SaveNoteFile(_note.Id, BuildNoteFileData());
            _notesService.UpdateNote(_note);
        }
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
            // ponytail 2026-11-14(审计修复 P2-9): 关窗后不再操作窗口句柄。
            if (!IsVisible)
            {
                _bringToFrontTimer?.Stop();
                _bringToFrontTimer = null;
                return;
            }
            Topmost = false;
            if (_vm?.IsLocked != true) NativeMethods.PinToDesktop(this);
            _bringToFrontTimer?.Stop();
            _bringToFrontTimer = null;
        };
        _bringToFrontTimer.Start();
    }

    // ── Formatting toolbar handlers ──
    // Word 式富文本(2026-11-28 重构):
    //   有选区 → 只改选区,并把这次修改并入「当前笔触」;
    //   无选区 → 只改「当前笔触」(纯内存),不碰文档;之后每次输入都按 TextChanged 的
    //            change 范围精确刷到新文字上,直到光标移动 / 点击正文 / 失焦 / 粘贴。
    //   工具条反显 = 笔触 > 选区值 > 光标处值 > 文档样式,只读,绝不写文档。

    void FontSizeCombo_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        if (_initializing || _updatingFormatButtons) return;
        if (FontSizeCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag
            && double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var fs))
        {
            ApplyFormat(TextElement.FontSizeProperty, fs);
            UpdateFormatButtons();
            // ComboBox 会抢焦点:应用完把焦点还给正文,用户可直接继续输入。
            RefocusContentBox();
        }
    }

    void BoldBtn_Click(object s, RoutedEventArgs e)
    {
        var current = CurrentFormatValue(TextElement.FontWeightProperty, ContentBox.FontWeight);
        ApplyFormat(TextElement.FontWeightProperty, NoteFormatting.IsBold(current) ? FontWeights.Normal : FontWeights.Bold);
        UpdateFormatButtons();
        RefocusContentBox();
    }

    void ItalicBtn_Click(object s, RoutedEventArgs e)
    {
        var current = CurrentFormatValue(TextElement.FontStyleProperty, ContentBox.FontStyle);
        ApplyFormat(TextElement.FontStyleProperty, NoteFormatting.IsItalic(current) ? FontStyles.Normal : FontStyles.Italic);
        UpdateFormatButtons();
        RefocusContentBox();
    }

    void UnderlineBtn_Click(object s, RoutedEventArgs e)
    {
        var current = CurrentFormatValue(Inline.TextDecorationsProperty, null);
        ApplyFormat(Inline.TextDecorationsProperty, NoteFormatting.IsUnderlined(current) ? null : TextDecorations.Underline);
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
                    ApplyTextColor(new SolidColorBrush(color));
                }
                catch (Exception ex) { DzTrace.Log($"[FontColor] SwatchClick EXCEPTION: {ex.Message}"); }
            };
            ColorPresetsPanel.Children.Add(swatch);
            // 高亮环按「颜色值」比较(预设是 6 位、正文反显是 8 位 ARGB,字符串比不出来)。
            if (ParseColor(hex, Colors.Transparent) == ParseColor(_lastTextColorHex, Colors.Transparent))
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
                ApplyTextColor(new SolidColorBrush(color));
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

    /// <summary>按颜色值同步色板高亮环(与「A」图标同源,见 UpdateFormatButtons)。</summary>
    void UpdateColorSelectionByColor(Color? target)
    {
        Border? match = null;
        if (target is Color t && ColorPresetsPanel != null)
        {
            foreach (var child in ColorPresetsPanel.Children)
            {
                if (child is Border b && b.Tag is string tag && ParseColor(tag, Colors.Transparent) == t)
                {
                    match = b;
                    break;
                }
            }
        }
        UpdateColorSelection(match);
    }

    /// <summary>
    /// 选色应用 — 有选区给选区上色;无选区只记「当前笔触」(下一次输入生效)。
    /// 「A」图标与色板高亮环统一由 UpdateFormatButtons 写,两者永远同源。
    /// </summary>
    void ApplyTextColor(SolidColorBrush brush)
    {
        ApplyFormat(TextElement.ForegroundProperty, brush);
        UpdateFormatButtons();
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
    /// 正文变化(2026-11-28 重写):
    /// ① 有「当前笔触」时,把本次插入的**精确范围**刷成笔触格式 —— 直接用
    ///    TextChangedEventArgs.Changes 的 Offset/AddedLength,不再靠「偏移 ±3 猜
    ///    文本」;这是「设了字号,打出来还是邻居的格式」的根治点。
    /// ② 防抖保存富文本 JSON。
    /// </summary>
    void ContentBox_TextChanged(object s, TextChangedEventArgs e)
    {
        if (_initializing) return;
        // 自己写格式触发的变化:不算用户输入,但仍要落盘(格式也是数据)。
        if (_writingFormat) { ScheduleNoteFileAutoSave(); return; }
        if (_loadingContent) { ScheduleNoteFileAutoSave(); return; }
        if (_typingFormat is { } fmt)
        {
            foreach (var change in e.Changes)
            {
                if (change.AddedLength <= 0) continue;
                var start = ContentBox.Document.ContentStart
                    .GetPositionAtOffset(change.Offset, LogicalDirection.Forward);
                var end = start?.GetPositionAtOffset(change.AddedLength, LogicalDirection.Forward);
                if (start == null || end == null) continue;
                var range = new TextRange(start, end);
                int offset = change.Offset, added = change.AddedLength;
                // 延迟到编辑器事务结束(Input 优先级)再写:TextChanged 在插入事务
                // 中途触发,同步写入会被编辑器后续步骤覆盖(实测)。
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (range.Text.Length == 0)
                    {
                        // 极少数情况(IME 合成后 run 被归一化):按偏移重新定位一次。
                        var s2 = ContentBox.Document.ContentStart.GetPositionAtOffset(offset, LogicalDirection.Forward);
                        var e2 = s2?.GetPositionAtOffset(added, LogicalDirection.Forward);
                        if (s2 == null || e2 == null) return;
                        range = new TextRange(s2, e2);
                    }
                    ApplyTypingFormatToRange(range, fmt);
                }), DispatcherPriority.Input);
            }
        }
        ScheduleNoteFileAutoSave();
    }

    /// <summary>
    /// 把「当前笔触」写到一段文字上。只写非 null 的属性(null = 沿用其原格式),
    /// 单项各自 try:文档在每次写入后会重排,一项失败不该丢其它项。
    /// </summary>
    void ApplyTypingFormatToRange(TextRange range, TypingFormat fmt)
    {
        if (string.IsNullOrEmpty(range.Text)) return;
        _writingFormat = true;
        try
        {
            if (fmt.Size is double sz && sz > 0) TryApplyProperty(range, TextElement.FontSizeProperty, sz);
            if (fmt.Weight is FontWeight w) TryApplyProperty(range, TextElement.FontWeightProperty, w);
            if (fmt.Style is FontStyle st) TryApplyProperty(range, TextElement.FontStyleProperty, st);
            if (fmt.Underline is bool u)
                TryApplyProperty(range, Inline.TextDecorationsProperty, u ? TextDecorations.Underline : null);
            if (fmt.Color is SolidColorBrush c) TryApplyProperty(range, TextElement.ForegroundProperty, c);
        }
        catch { }
        finally { _writingFormat = false; }
    }

    static void TryApplyProperty(TextRange range, DependencyProperty prop, object? value)
    {
        try { range.ApplyPropertyValue(prop, value); } catch { }
    }

    /// <summary>方向键/Home/End/PageUp/PageDown = 主动移动光标 → 当前笔触复位(Word 行为)。</summary>
    void ContentBox_PreviewKeyDown(object s, KeyEventArgs e)
    {
        if (_typingFormat == null) return;
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
                ClearTypingFormat();
                break;
        }
    }

    /// <summary>点击正文 = 光标定位 → 当前笔触复位,新位置沿用其原有格式(Word 行为)。</summary>
    void ContentBox_PreviewMouseLeftButtonDown(object s, MouseButtonEventArgs e)
    {
        ClearTypingFormat();
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

    // ── 当前笔触(Word 式,2026-11-28) ──
    // 无选区改格式只记在这里;打字时按 TextChanged 的 change 范围精确落到文字上;
    // 方向键/点击正文/失焦/粘贴都会复位。null = 沿用原格式(不改)。
    // 纯逻辑在 Helpers/NoteFormatting.cs(可脱离窗口单测)。

    void ClearTypingFormat() => _typingFormat = null;

    /// <summary>
    /// Word 式格式应用 — 唯一写入入口:
    /// ① 有选区 → 只格式化选区;
    /// ② 无选区 → 只改「当前笔触」,不写文档(实测:空选区 ApplyPropertyValue 会把
    ///    整个 run、甚至相邻两个 run 一起改掉,段末则完全无效,所以绝不能走那条路)。
    /// 两条分支最后都把这次修改并入笔触 —— 紧接着输入的字沿用这次改出来的格式,
    /// 「选中一段 → 设 30 → 直接敲字替换」也就不会丢格式。
    /// </summary>
    void ApplyFormat(DependencyProperty prop, object? value)
    {
        var sel = ContentBox.Selection;
        if (sel == null) return;
        // 光标场景取左侧字符(WPF 的输入方向就是左邻);选区场景取选区第一个字符
        // (替换输入时新字本来就会并进那一段,取它才是 Word 语义)。
        var baseFormat = sel.IsEmpty
            ? NoteFormatting.Snapshot(ContentBox.CaretPosition, fromLeftChar: true)
            : NoteFormatting.Snapshot(sel.Start, fromLeftChar: false);
        _typingFormat = NoteFormatting.Merge(baseFormat, prop, value);
        if (!sel.IsEmpty)
        {
            // 选区写入同样置位:ApplyPropertyValue 可能触发带 AddedLength 的元素变化,
            // 不拦住的话回刷会把这几个属性再"补"到别的字符上。
            _writingFormat = true;
            try { sel.ApplyPropertyValue(prop, value); }
            finally { _writingFormat = false; }
        }
        DzTrace.Log($"[Fmt] apply {prop.Name} → typing={_typingFormat} selEmpty={sel.IsEmpty}");
    }


    /// <summary>
    /// 工具条开关按钮用的「当前值」:有选区看选区;无选区优先当前笔触,其次光标左侧
    /// 字符,最后文档样式。让 B/I/U 的切换基于用户看到的状态,而不是凭空取反。
    /// </summary>
    object? CurrentFormatValue(DependencyProperty prop, object? documentFallback)
    {
        var sel = ContentBox.Selection;
        if (sel == null) return documentFallback;
        if (!sel.IsEmpty)
        {
            var v = sel.GetPropertyValue(prop);
            return v == DependencyProperty.UnsetValue ? documentFallback : v;
        }
        // 下划线在笔触里可能是「明确关掉」,ReadFormat 用 null 表示「没有下划线」会
        // 和「未知」混淆 —— 这里单独取,否则 U 按钮会卡在反显出来的旧状态上。
        if (prop == Inline.TextDecorationsProperty && _typingFormat?.Underline is bool u)
            return u ? TextDecorations.Underline : null;
        var fromTyping = NoteFormatting.Read(_typingFormat, prop);
        if (fromTyping != null) return fromTyping;
        var fromLeft = NoteFormatting.Read(NoteFormatting.Snapshot(ContentBox.CaretPosition), prop);
        return fromLeft ?? documentFallback;
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


    // ── 工具条反显 + 下拉同步 ──
    // 优先级(Word 式):有选区看选区;无选区优先「当前笔触」,其次光标左侧字符,
    // 最后文档样式。只读,绝不写文档;混合选区显示混合态(不高亮 / 下拉置空)。

    /// <summary>
    /// B/I/U 的「激活」底块。必须走 <see cref="HoverHighlight.BaseBackground"/>:
    /// 直接写 <c>Button.Background</c> 会被 HoverHighlight 的 MouseLeave 复位成透明 ——
    /// 表现就是「点了按钮不亮」(鼠标一移开高光立刻消失)。
    /// </summary>
    static void SetToggleActive(Button? btn, bool active)
    {
        if (btn == null) return;
        HoverHighlight.SetBaseBackground(btn, active ? FormatActiveBrush : null);
        // 鼠标正悬停时保持悬停高光(与激活态同色),离开时由 HoverHighlight 按 BaseBackground 恢复。
        btn.Background = btn.IsMouseOver
            ? HoverHighlight.Brush
            : (active ? FormatActiveBrush : Brushes.Transparent);
    }

    void UpdateFormatButtons()
    {
        var sel = ContentBox.Selection;
        bool hasSelection = sel != null && !sel.IsEmpty;
        var typing = hasSelection ? null : _typingFormat;

        // 取「当前要显示的值」:null = 混合态(只有选区才可能)。
        object? Probe(DependencyProperty prop, object? documentFallback)
        {
            if (hasSelection)
            {
                var v = sel!.GetPropertyValue(prop);
                return v == DependencyProperty.UnsetValue ? null : v;
            }
            if (typing != null)
            {
                var tv = NoteFormatting.Read(typing, prop);
                if (tv != null) return tv;
            }
            var lv = NoteFormatting.Read(NoteFormatting.Snapshot(ContentBox.CaretPosition), prop);
            return lv ?? documentFallback;
        }

        bool IsMixed(DependencyProperty prop) =>
            hasSelection && sel!.GetPropertyValue(prop) == DependencyProperty.UnsetValue;

        // B / I / U 高亮
        SetToggleActive(BoldBtn, NoteFormatting.IsBold(Probe(TextElement.FontWeightProperty, ContentBox.FontWeight)));
        SetToggleActive(ItalicBtn, NoteFormatting.IsItalic(Probe(TextElement.FontStyleProperty, ContentBox.FontStyle)));
        // 下划线单独取:笔触里「明确关掉」不能落回左侧字符的旧状态(同 CurrentFormatValue)。
        var underlineCurrent = typing?.Underline is bool ul
            ? (ul ? TextDecorations.Underline : null)
            : Probe(Inline.TextDecorationsProperty, null);
        SetToggleActive(UnderlineBtn, NoteFormatting.IsUnderlined(underlineCurrent));

        // 字号:混合 → 清空;非预设值 → 临时插一条,反显才是真的
        if (!IsMixed(TextElement.FontSizeProperty)
            && Probe(TextElement.FontSizeProperty, ContentBox.FontSize) is double fontSize)
        {
            SyncFontSizeCombo(fontSize);
        }
        else
        {
            _updatingFormatButtons = true;
            try { FontSizeCombo.SelectedIndex = -1; }
            finally { _updatingFormatButtons = false; }
        }

        // 文字颜色:「A」图标 + 色板高亮环同源(B3 次生问题的修复)
        if (!IsMixed(TextElement.ForegroundProperty)
            && Probe(TextElement.ForegroundProperty, ContentBox.Foreground) is SolidColorBrush scb)
        {
            FontColorBtn.Foreground = scb;
            _lastTextColorHex = NoteFormatting.ToHex(scb.Color);
            UpdateColorSelectionByColor(scb.Color);
        }
        else
        {
            FontColorBtn.Foreground = _buttonBrush
                ?? new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));
            UpdateColorSelectionByColor(null);
        }
    }

    /// <summary>字号下拉:预设项命中就选它;否则临时插一条「自定义值」,避免反显停在旧值。</summary>
    void SyncFontSizeCombo(double fontSize)
    {
        _updatingFormatButtons = true;
        try
        {
            ComboBoxItem? match = null;
            foreach (var obj in FontSizeCombo.Items)
            {
                if (obj is ComboBoxItem item && item.Tag is string tag
                    && double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var val)
                    && Math.Abs(val - fontSize) < 0.1)
                {
                    match = item;
                    break;
                }
            }
            if (match == null)
            {
                _customSizeItem ??= new ComboBoxItem();
                _customSizeItem.Tag = fontSize.ToString("0.##", CultureInfo.InvariantCulture);
                _customSizeItem.Content = _customSizeItem.Tag;
                if (!FontSizeCombo.Items.Contains(_customSizeItem))
                    FontSizeCombo.Items.Add(_customSizeItem);
                match = _customSizeItem;
            }
            else if (_customSizeItem != null && FontSizeCombo.Items.Contains(_customSizeItem))
            {
                FontSizeCombo.Items.Remove(_customSizeItem);
            }
            FontSizeCombo.SelectedItem = match;
        }
        finally { _updatingFormatButtons = false; }
    }
}
