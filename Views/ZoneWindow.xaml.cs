using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Interop;
using DesktopZones.Helpers;
using DesktopZones.Models;
using DesktopZones.Services;
using DesktopZones.ViewModels;
using Microsoft.Win32;

namespace DesktopZones.Views;

public partial class ZoneWindow : Window
{
    // Win32 drop
    [DllImport("shell32.dll")] static extern void DragAcceptFiles(IntPtr h, bool a);
    [DllImport("shell32.dll")] static extern void DragFinish(IntPtr h);
    [DllImport("shell32.dll")] static extern uint DragQueryFile(IntPtr h, uint i, System.Text.StringBuilder? f, uint c);
    [DllImport("shell32.dll")] static extern bool DragQueryPoint(IntPtr h, out POINT p);
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
    const int WM_DROPFILES = 0x0233;

    // Resize
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    const uint WM_NCLBUTTONDOWN = 0x00A1;
    const int HTTOPLEFT = 13, HTTOPRIGHT = 14, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SHBrowseForFolderW(ref BROWSEINFOW b);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool SHGetPathFromIDListW(IntPtr p, System.Text.StringBuilder s);
    [DllImport("ole32.dll")] static extern void CoTaskMemFree(IntPtr p);

    [StructLayout(LayoutKind.Sequential)]
    struct BROWSEINFOW {
        public IntPtr hwndOwner;
        public IntPtr pidlRoot;
        public IntPtr pszDisplayName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszTitle;
        public uint ulFlags;
        public IntPtr lpfn;
        public IntPtr lParam;
        public int iImage;
    }

    private Zone _zone;
    private readonly ZoneManager _mgr;
    public bool IsMinimized => RestoreButton.Visibility == Visibility.Visible;
    private readonly ZoneViewModel _vm;
    private readonly LocalizationService _loc = LocalizationService.Instance;
    private HwndSource? _src;
    private Canvas? _itemCanvas;
    private Action<Services.Language>? _langChanged;

    private bool _dragging, _fileOver;
    private Point _ds, _is;
    private ZoneItemViewModel? _dv;
    private FrameworkElement? _de;
    private readonly System.Windows.Threading.DispatcherTimer _saveDebounce = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _savePending;
    private string _resolvedFillColor = "#08000000";

    public ZoneWindow(Zone zone, ZoneManager mgr, ShellIconService icons)
    {
        InitializeComponent();
        _zone = zone; _mgr = mgr;
        _vm = new ZoneViewModel(zone, mgr, icons);
        DataContext = _vm;
        Left = zone.X; Top = zone.Y;
        Width = SanitizeW(zone.Width); Height = SanitizeW(zone.Height);
        ApplyStyle();
        // Acrylic is applied in OnLoad (needs valid HWND)
        ZoneTitleText.Text = zone.Name;
        SetRestoreIcon();
        ApplyLoc();
        _vm.Items.CollectionChanged += (_, _) => UpdateCanvasSize();
        Loaded += OnLoad;
        LocationChanged += (_, _) => { _zone.X = Left; _zone.Y = Top; ScheduleSave(); };
        SizeChanged += OnSize;
        _saveDebounce.Tick += (_, _) => { _saveDebounce.Stop(); if (_savePending) { _savePending = false; _mgr.SaveConfig(); } };
        _langChanged = _ => ApplyLoc();
        _loc.LanguageChanged += _langChanged;
        _mgr.ZonesChanged += OnZonesChanged;
        if (!_zone.IsVisible) ApplyHidden();
        RebuildSubZoneTabs();
        if (_zone.MergedSubZoneIds.Count > 0) _vm.SelectedSubZoneId = _zone.Id;
        UpdateMergedTitle();
    }

    static double SanitizeW(double w) => w < 100 ? 400 : w;

    void ApplyLoc()
    {
        var cn = _loc.CurrentLanguage == Services.Language.Chinese;
        CtxImport.Header = _loc["Zone.Import"];
        CtxImportFolder.Header = cn ? "导入文件夹..." : "Import Folder...";
        CtxImportFiles.Header = cn ? "导入文件..." : "Import Files...";
        CtxImportFolder2.Header = cn ? "导入文件夹..." : "Import Folder...";
        CtxNew.Header = _loc["Zone.New"];
        CtxNew2.Header = _loc["Zone.New"];
        CtxNewFolder.Header = cn ? "新建文件夹... / New Folder..." : "New Folder...";
        CtxNewTxt.Header = cn ? "文本文档 (.txt)" : "Text Document (.txt)";
        CtxNewDocx.Header = cn ? "Word 文档 (.docx)" : "Word Document (.docx)";
        CtxNewPptx.Header = cn ? "PowerPoint (.pptx)" : "PowerPoint (.pptx)";
        CtxNewXlsx.Header = cn ? "Excel 工作表 (.xlsx)" : "Excel Workbook (.xlsx)";
        CtxNewFolder2.Header = cn ? "新建文件夹... / New Folder..." : "New Folder...";
        CtxNewTxt2.Header = cn ? "文本文档 (.txt)" : "Text Document (.txt)";
        CtxNewDocx2.Header = cn ? "Word 文档 (.docx)" : "Word Document (.docx)";
        CtxNewPptx2.Header = cn ? "PowerPoint (.pptx)" : "PowerPoint (.pptx)";
        CtxNewXlsx2.Header = cn ? "Excel 工作表 (.xlsx)" : "Excel Workbook (.xlsx)";
        CtxDisbandAll.Header = _loc["Merge.DisbandAll"];
        CtxEdit.Header = _loc["Zone.Edit"];
        CtxHide.Header = _loc["Zone.Hide"];
        CtxDelete.Header = _loc["Zone.Delete"];
    }

    void OnLoad(object s, RoutedEventArgs e)
    {
        NativeMethods.PinToDesktop(this); NativeMethods.SetToolWindow(this);
        NativeMethods.SetRoundedCorners(this, (int)_zone.CornerRadius);
        // Re-apply acrylic now that HWND is valid (constructor called ApplyStyle before HWND existed)
        ApplyAcrylic(_resolvedFillColor);
        var hwnd = new WindowInteropHelper(this).Handle;
        int ex = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, ex & ~NativeMethods.WS_EX_APPWINDOW);
        DragAcceptFiles(hwnd, true);
        _src = HwndSource.FromHwnd(hwnd); _src?.AddHook(WndProc);

        // Find the Canvas for size updates
        _itemCanvas = FindVisualChild<Canvas>(this);
        UpdateCanvasSize();
    }

    IntPtr WndProc(IntPtr h, int m, IntPtr w, IntPtr l, ref bool hd)
    { if (m == WM_DROPFILES) { DoDrop(w); hd = true; } return IntPtr.Zero; }

    void DoDrop(IntPtr drop)
    { try { uint n = DragQueryFile(drop, 0xFFFFFFFF, null, 0); var (sx, sy) = FindFreeSpot(); for (uint i = 0; i < n; i++) { var sb = new System.Text.StringBuilder(260); DragQueryFile(drop, i, sb, 260); if (!string.IsNullOrEmpty(sb.ToString())) { Add(sb.ToString(), sx, sy); sx += 80; if (sx > _zone.Width - 80) { sx = 10; sy += 90; } } } UpdateCanvasSize(); } finally { DragFinish(drop); } }

    void Add(string path, double x, double y)
    { var t = Dir(path) ? ItemType.Folder : Path.GetExtension(path).ToLowerInvariant() switch { ".lnk" => ItemType.Shortcut, ".exe" => ItemType.Application, _ => ItemType.Shortcut }; var nm = Path.GetFileNameWithoutExtension(path); var cx = Math.Max(0, Math.Min(Snap(x), Math.Max(0, _zone.Width - 72))); var cy = Math.Max(0, Math.Min(Snap(y - 40), Math.Max(0, _zone.Height - 88))); _vm.AddItem(new ZoneItem(nm, path, t, cx, cy)); }
    static bool Dir(string p) => Directory.Exists(p);
    double Clamp(double v, double max) => Math.Max(0, Math.Min(Snap(v), max));
    double Snap(double v) => _zone.SnapToGrid ? ZoneViewModel.SnapToGrid(v, _zone.GridSize) : v;

    // ── Show / Hide ──

    public void ShowZone()
    {
        if (_zone.Width < 100) _zone.Width = 400; if (_zone.Height < 100) _zone.Height = 300;
        Width = _zone.Width; Height = _zone.Height; Left = _zone.X; Top = _zone.Y;
        MainContent.Visibility = Visibility.Visible; RestoreButton.Visibility = Visibility.Collapsed;
        _zone.IsVisible = true;
        ApplyStyle();
        NativeMethods.PinToDesktop(this);
        NativeMethods.SetRoundedCorners(this, (int)_zone.CornerRadius);
        _mgr.FireZoneVisibilityChanged(_zone.Id, true);
    }

    public void HideZone()
    {
        // Save dimensions only if not currently minimized (RestoreButton not visible)
        // If minimized, the original dimensions are already saved in _zone
        if (RestoreButton.Visibility != Visibility.Visible)
        {
            _zone.X = Left; _zone.Y = Top; _zone.Width = Width; _zone.Height = Height;
            _mgr.SaveConfig();
        }
        // Always disable blur and clean up state before hiding
        AcrylicHelper.DisableBlur(this);
        MainContent.Visibility = Visibility.Collapsed;
        Width = 36; Height = 36;
        NativeMethods.DisableRoundedCorners(this);
        if (!_zone.EnableRestoreButton)
        {
            Hide();
        }
        else
        {
            RestoreButton.Visibility = Visibility.Visible;
            NativeMethods.PinToDesktop(this);
        }
        _zone.IsVisible = false;
        _mgr.FireZoneVisibilityChanged(_zone.Id, false);
    }

    void ApplyHidden()
    {
        AcrylicHelper.DisableBlur(this);
        NativeMethods.DisableRoundedCorners(this);
        MainContent.Visibility = Visibility.Collapsed;
        Width = 36; Height = 36;
        if (!_zone.EnableRestoreButton)
        {
            Hide();
        }
        else
        {
            RestoreButton.Visibility = Visibility.Visible;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _mgr.ZonesChanged -= OnZonesChanged;
        if (_src != null) { _src.RemoveHook(WndProc); _src = null; }
        if (_langChanged != null) { _loc.LanguageChanged -= _langChanged; _langChanged = null; }
        var h = new WindowInteropHelper(this).Handle;
        if (h != IntPtr.Zero) DragAcceptFiles(h, false);
        base.OnClosed(e);
    }

    void OnZonesChanged()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _vm.RefreshItems();
            UpdateCanvasSize();
        }), System.Windows.Threading.DispatcherPriority.Normal);
    }

    // ── Drag: DIRECT handler on title bar ──

    void TitleBar_Drag(object s, MouseButtonEventArgs e)
    { try { ControlPoint.Opacity = 0.6; DragMove(); ControlPoint.Opacity = 0.4; NativeMethods.PinToDesktop(this); } catch { } }

    // ── Window-level mouse: resize grips only ──

    void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
    }

    // ── Resize ──

    void ResizeGrip_Down(object s, MouseButtonEventArgs e)
    { if (s is not Border g) return; int d = g == GripTL ? HTTOPLEFT : g == GripTR ? HTTOPRIGHT : g == GripBL ? HTBOTTOMLEFT : HTBOTTOMRIGHT; SendMessage(new WindowInteropHelper(this).Handle, WM_NCLBUTTONDOWN, (IntPtr)d, IntPtr.Zero); NativeMethods.PinToDesktop(this); e.Handled = true; }

    // ── Import ──

    void ImportFiles_Click(object s, RoutedEventArgs e)
    { var d = new OpenFileDialog { Title = _loc["Zone.ImportTitle"], Filter = "All|*.lnk;*.exe;*.*|Shortcuts|*.lnk|Apps|*.exe", Multiselect = true }; if (d.ShowDialog() == true) ImportArranged(d.FileNames); }

    void ImportFolder_Click(object s, RoutedEventArgs e)
    {
        var displayBuf = IntPtr.Zero;
        var pidl = IntPtr.Zero;
        try
        {
            var h = new WindowInteropHelper(this); h.EnsureHandle();
            displayBuf = Marshal.AllocHGlobal(520); // MAX_PATH*2 Unicode
            var bi = new BROWSEINFOW
            {
                hwndOwner = h.Handle,
                pszDisplayName = displayBuf,
                lpszTitle = "Select Folder",
                ulFlags = 0x40
            };
            pidl = SHBrowseForFolderW(ref bi);
            if (pidl != IntPtr.Zero)
            {
                var sb = new System.Text.StringBuilder(260);
                if (SHGetPathFromIDListW(pidl, sb) && Directory.Exists(sb.ToString()))
                    ImportArranged(new[] { sb.ToString() });
            }
        }
        catch (Exception ex) { MessageBox.Show($"Import failed: {ex.Message}"); }
        finally
        {
            if (displayBuf != IntPtr.Zero) Marshal.FreeHGlobal(displayBuf);
            if (pidl != IntPtr.Zero) CoTaskMemFree(pidl);
        }
    }

    void ImportBtn_Click(object s, MouseButtonEventArgs e)
    {
        ImportBtn.ContextMenu.IsOpen = true;
        e.Handled = true;
    }

    void ImportArranged(string[] paths)
    {
        var (sx, sy) = FindFreeSpot();
        foreach (var f in paths) { Add(f, sx, sy); sx += 80; if (sx > _zone.Width - 80) { sx = 10; sy += 90; } }
        UpdateCanvasSize();
    }

    (double, double) FindFreeSpot()
    {
        if (_zone.Items.Count == 0) return (10, 10);
        int gs = _zone.GridSize;
        double maxY = 0;
        foreach (var i in _zone.Items) { if (i.Y > maxY) maxY = i.Y; }
        double maxX = 0;
        foreach (var i in _zone.Items) { if (Math.Abs(i.Y - maxY) < 10 && i.X > maxX) maxX = i.X; }
        double sx = maxX + gs, sy = maxY;
        if (sx > _zone.Width - gs) { sx = 10; sy = maxY + gs; }
        return (sx, sy);
    }

    void RearrangeAll()
    {
        if (!_zone.AutoArrange) return;

        // Determine which zone's items to rearrange and which grid size to use
        List<Models.ZoneItem> items;
        int gridSize;

        if (_zone.MergedSubZoneIds.Count > 0 && _vm.SelectedSubZoneId.HasValue && _vm.SelectedSubZoneId.Value != _zone.Id)
        {
            // Merged mode with a sub-zone tab selected
            var subZone = _mgr.Zones.FirstOrDefault(z => z.Id == _vm.SelectedSubZoneId.Value);
            if (subZone == null) return;
            items = subZone.Items;
            gridSize = subZone.GridSize;
        }
        else
        {
            items = _zone.Items;
            gridSize = _zone.GridSize;
        }

        double x = 10, y = 10;
        foreach (var item in items.OrderBy(i => i.Y).ThenBy(i => i.X))
        {
            item.X = ZoneViewModel.SnapToGrid(x, gridSize);
            item.Y = ZoneViewModel.SnapToGrid(y, gridSize);
            x += gridSize;
            if (x > _zone.Width - gridSize) { x = 10; y += gridSize; }
        }
        _vm.RefreshMergedItems();
    }

    // ── Right-click zone ──

    void Window_PreviewMouseRightButtonDown(object s, MouseButtonEventArgs e)
    { if (IsOnItem(e.OriginalSource) || MainContent.Visibility != Visibility.Visible) return; ZoneBorder.ContextMenu.IsOpen = true; e.Handled = true; }
    void EditZone_Click(object s, RoutedEventArgs e) { _vm.IsEditing = !_vm.IsEditing; EditBtnText.Text = _vm.IsEditing ? "✓" : "⚙"; }
    void HideZone_Click(object s, RoutedEventArgs e) { HideZone(); }
    void DeleteZone_Click(object s, RoutedEventArgs e) { if (MessageBox.Show(_loc.Get("Dialog.DeleteZoneMsg", _zone.Name), _loc["Dialog.DeleteZoneTitle"], MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes) { _mgr.DeleteZone(_zone.Id); Close(); } }
    void DisbandAll_Click(object s, RoutedEventArgs e)
    {
        if (!_zone.MergedGroupId.HasValue) return;
        if (MessageBox.Show(_loc["Merge.ConfirmDisband"], _loc["Merge.DisbandAll"], MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            _mgr.DisbandMergedGroup(_zone.MergedGroupId.Value);
        }
    }

    void DisbandThis_Click(object s, RoutedEventArgs e)
    {
        if (!_zone.MergedGroupId.HasValue) return;
        // If this zone is a sub-zone (not master), remove it from the group
        if (_zone.MergedSubZoneIds.Count == 0)
        {
            var cn = _loc.CurrentLanguage == Services.Language.Chinese;
            if (MessageBox.Show(
                cn ? $"确定要将分区「{_zone.Name}」从组合中分离吗？"
                   : $"Remove zone \"{_zone.Name}\" from the merged group?",
                _loc["Merge.DisbandThis"], MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                _mgr.RemoveFromMergedGroup(_zone.Id);
            }
        }
        else
        {
            // This is the master — disband the whole group
            DisbandAll_Click(s, e);
        }
    }

    // ── New submenu ──

    void NewFolder_Click(object s, RoutedEventArgs e)
    {
        var displayBuf = IntPtr.Zero;
        var pidl = IntPtr.Zero;
        try
        {
            var h = new WindowInteropHelper(this); h.EnsureHandle();
            displayBuf = Marshal.AllocHGlobal(520);
            var bi = new BROWSEINFOW
            {
                hwndOwner = h.Handle,
                pszDisplayName = displayBuf,
                lpszTitle = "Select Parent Folder",
                ulFlags = 0x40
            };
            pidl = SHBrowseForFolderW(ref bi);
            if (pidl != IntPtr.Zero)
            {
                var sb = new System.Text.StringBuilder(260);
                if (SHGetPathFromIDListW(pidl, sb))
                {
                    // Prompt for folder name
                    string parentPath = sb.ToString();
                    string folderName = Microsoft.VisualBasic.Interaction.InputBox(
                        "Folder Name:", "New Folder", "New Folder");
                    if (!string.IsNullOrWhiteSpace(folderName))
                    {
                        string fullPath = Path.Combine(parentPath, folderName);
                        Directory.CreateDirectory(fullPath);
                    }
                }
            }
        }
        catch (Exception ex) { MessageBox.Show($"Failed: {ex.Message}"); }
        finally
        {
            if (displayBuf != IntPtr.Zero) Marshal.FreeHGlobal(displayBuf);
            if (pidl != IntPtr.Zero) CoTaskMemFree(pidl);
        }
    }

    void CreateNewFile(string defaultExt, string filter)
    {
        var d = new SaveFileDialog
        {
            Title = "Create New File",
            Filter = filter,
            DefaultExt = defaultExt,
            FileName = "NewDocument" + defaultExt
        };
        if (d.ShowDialog() == true)
        {
            try { System.IO.File.Create(d.FileName).Dispose(); }
            catch (Exception ex) { MessageBox.Show($"Failed: {ex.Message}"); }
            Add(d.FileName, 10, 10);
            UpdateCanvasSize();
            _mgr.SaveConfig();
        }
    }

    void NewTxt_Click(object s, RoutedEventArgs e)
    {
        CreateNewFile(".txt", "Text Document|*.txt|All Files|*.*");
    }

    void NewDocx_Click(object s, RoutedEventArgs e)
    {
        CreateNewFile(".docx", "Word Document|*.docx|All Files|*.*");
    }

    void NewPptx_Click(object s, RoutedEventArgs e)
    {
        CreateNewFile(".pptx", "PowerPoint|*.pptx|All Files|*.*");
    }

    void NewXlsx_Click(object s, RoutedEventArgs e)
    {
        CreateNewFile(".xlsx", "Excel Worksheet|*.xlsx|All Files|*.*");
    }

    // Minimized state drag — uses DragMove() like title bar
    private bool _restoreDragging;
    private Point _restoreDown;

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
            _zone.X = Left; _zone.Y = Top; _mgr.SaveConfig();
        }
    }

    void Restore_MouseUp(object s, MouseButtonEventArgs e)
    {
        RestoreButton.ReleaseMouseCapture();
        if (!_restoreDragging) { ShowZone(); _mgr.SaveConfig(); }
    }

    void Restore_Enter(object s, MouseEventArgs e) { RestoreButton.Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x2A, 0x2A, 0x4E)); RestoreIconChar.Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)); }
    void Restore_Leave(object s, MouseEventArgs e) { RestoreButton.Background = new SolidColorBrush(Color.FromArgb(0xDD, 0x1A, 0x1A, 0x2E)); if (!string.IsNullOrEmpty(_zone.IconColor)) { try { RestoreIconChar.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_zone.IconColor)!); } catch { } } }

    void Ctrl_Enter(object s, MouseEventArgs e) { if (s is Border b) b.Background = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)); }
    void Ctrl_Leave(object s, MouseEventArgs e) { if (s is Border b) b.Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)); }
    void HideButton_Click(object s, MouseButtonEventArgs e) { HideZone(); e.Handled = true; }

    void AlignGrid_Click(object s, MouseButtonEventArgs e)
    {
        _zone.SnapToGrid = true;
        RearrangeAll();
        _mgr.SaveConfig();
        e.Handled = true;
    }
    void EditButton_Click(object s, MouseButtonEventArgs e) { _vm.IsEditing = !_vm.IsEditing; EditBtnText.Text = _vm.IsEditing ? "✓" : "⚙"; e.Handled = true; }

    // ── File drops (WPF) ──

    void Canvas_DragEnter(object s, DragEventArgs e) { if (e.Data.GetDataPresent(DataFormats.FileDrop)) { _fileOver = true; e.Effects = DragDropEffects.Link; e.Handled = true; } }
    void Canvas_DragLeave(object s, DragEventArgs e) => _fileOver = false;
    void Canvas_DragOver(object s, DragEventArgs e) { if (_fileOver) { e.Effects = DragDropEffects.Link; e.Handled = true; } }
    void Canvas_Drop(object s, DragEventArgs e) { _fileOver = false; if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } fs) return; var (sx, sy) = FindFreeSpot(); foreach (var f in fs) { Add(f, sx, sy); sx += 80; if (sx > _zone.Width - 80) { sx = 10; sy += 90; } } e.Handled = true; }

    // ── Window-level drag-drop (fallback for transparent windows) ──

    void Window_DragEnter(object s, DragEventArgs e) { if (e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effects = DragDropEffects.Link; e.Handled = true; } }
    void Window_DragOver(object s, DragEventArgs e) { if (e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effects = DragDropEffects.Link; e.Handled = true; } }
    void Window_Drop(object s, DragEventArgs e) { if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } fs) return; var (sx, sy) = FindFreeSpot(); foreach (var f in fs) { Add(f, sx, sy); sx += 80; if (sx > _zone.Width - 80) { sx = 10; sy += 90; } } UpdateCanvasSize(); e.Handled = true; }

    // ── Item drag ──

    void Item_MouseDown(object s, MouseButtonEventArgs e)
    { if (e.ClickCount == 2) { if (s is FrameworkElement fe && fe.DataContext is ZoneItemViewModel iv) Open(iv); e.Handled = true; return; } if (s is FrameworkElement el && el.DataContext is ZoneItemViewModel vm) { _dv = vm; _de = el; _ds = e.GetPosition(this); _is = new Point(vm.X, vm.Y); _dragging = false; el.CaptureMouse(); e.Handled = true; } }
    void Item_MouseMove(object s, MouseEventArgs e)
    { if (_dv == null || _de == null) return; var d = e.GetPosition(this) - _ds; if (!_dragging) { if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return; _dragging = true; _de.Opacity = 0.7; } _dv.X = Math.Max(0, Math.Min(_is.X + d.X, _zone.Width - 72)); _dv.Y = Math.Max(0, Math.Min(_is.Y + d.Y, _zone.Height - 88)); }
    void Item_MouseUp(object s, MouseButtonEventArgs e)
    { if (_dv == null) return; if (_de != null) { _de.ReleaseMouseCapture(); _de.Opacity = 1.0; } if (_dragging) { _vm.MoveItem(_dv.Id, _dv.X, _dv.Y, _zone.SnapToGrid); _vm.RefreshItems(); } _dv = null; _de = null; _dragging = false; }

    void Item_Enter(object s, MouseEventArgs e)
    {
        if (s is Grid g)
            g.Background = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
    }

    void Item_Leave(object s, MouseEventArgs e)
    {
        if (s is Grid g)
            g.Background = Brushes.Transparent;
    }

    // ── Context menu ──

    void ItemOpen_Click(object s, RoutedEventArgs e) { if (VM(s) is ZoneItemViewModel v) Open(v); }
    void ItemOpenLocation_Click(object s, RoutedEventArgs e) { if (VM(s) is not ZoneItemViewModel v) return; if (v.Type is ItemType.Shortcut or ItemType.Application) { var d = Path.GetDirectoryName(v.TargetPath); if (!string.IsNullOrEmpty(d)) System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{v.TargetPath}\""); } else System.Diagnostics.Process.Start("explorer.exe", v.TargetPath); }
    void ItemRename_Click(object s, RoutedEventArgs e)
    {
        if (VM(s) is not ZoneItemViewModel v) return;
        var rn = new Views.RenameDialog(v.Name) { Owner = this };
        if (rn.ShowDialog() == true) { v.Name = rn.NewName; _mgr.SaveConfig(); }
    }
    void ItemDelete_Click(object s, RoutedEventArgs e) { if (VM(s) is ZoneItemViewModel v) _vm.DeleteItemCommand.Execute(v); }

    static ZoneItemViewModel? VM(object s) => s is MenuItem mi && mi.Parent is ContextMenu cm && cm.PlacementTarget is FrameworkElement fe && fe.DataContext is ZoneItemViewModel vm ? vm : null;
    static void Open(ZoneItemViewModel v) { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = v.TargetPath, UseShellExecute = true }); } catch { } }

    // ── Style (CRITICAL: updates _zone reference) ──

    public void ApplyStyle()
    {
        var config = _mgr.GetConfig();
        bool useGlobal = config.UseGlobalAppearance;
        string borderColor = useGlobal ? config.GlobalBorderColor : _zone.BorderColor;
        string fillColor = useGlobal ? config.GlobalFillColor : _zone.FillColor;
        double borderThickness = useGlobal ? config.GlobalBorderThickness : _zone.BorderThickness;

        string titleBarFillColor = _zone.TitleBarFillColor;
        double controlOpacity = _zone.ControlOpacity;
        int cornerRadius = _zone.CornerRadius;
        string titleTextColor = _zone.TitleTextColor;
        string iconColor = _zone.IconColor;
        bool quickBarMode = _zone.QuickBarMode;
        string bgImagePath = _zone.BackgroundImagePath;
        string bgImageStretch = _zone.BgImageStretch;
        double bgImageOffsetX = _zone.BgImageOffsetX;
        double bgImageOffsetY = _zone.BgImageOffsetY;
        double bgImageZoom = _zone.BgImageZoom;
        double bgImageOpacity = _zone.BackgroundImageOpacity;

        // Handle merged group style
        if (_zone.MergedSubZoneIds.Count > 0)
        {
            // This is the master zone of a merged group
            // Check if there's a selected sub-zone
            var selectedSubId = _vm?.SelectedSubZoneId;
            if (selectedSubId.HasValue && selectedSubId.Value != _zone.Id)
            {
                // A sub-zone is selected
                var selectedSubZone = _mgr.Zones.FirstOrDefault(z => z.Id == selectedSubId.Value);
                if (selectedSubZone != null)
                {
                    if (_zone.MergedGroupUseUnifiedFill)
                    {
                        // Unified fill mode: use master's unified settings
                        fillColor = _zone.MergedGroupFillColor;
                        borderColor = _zone.MergedGroupBorderColor;
                        borderThickness = _zone.MergedGroupBorderThickness;
                        titleBarFillColor = _zone.MergedGroupTitleBarFillColor;
                        controlOpacity = _zone.MergedGroupControlOpacity;
                        cornerRadius = _zone.MergedGroupCornerRadius;
                        titleTextColor = _zone.MergedGroupTitleTextColor;
                        iconColor = _zone.MergedGroupIconColor;
                        quickBarMode = _zone.MergedGroupQuickBarMode;
                        bgImagePath = _zone.MergedGroupBackgroundImagePath;
                        bgImageStretch = _zone.MergedGroupBgImageStretch;
                        bgImageOffsetX = _zone.MergedGroupBgImageOffsetX;
                        bgImageOffsetY = _zone.MergedGroupBgImageOffsetY;
                        bgImageZoom = _zone.MergedGroupBgImageZoom;
                        bgImageOpacity = _zone.MergedGroupBackgroundImageOpacity;
                    }
                    else
                    {
                        // Keep original fill mode: use sub-zone's own settings
                        fillColor = selectedSubZone.FillColor;
                        borderColor = selectedSubZone.BorderColor;
                        borderThickness = selectedSubZone.BorderThickness;
                        titleBarFillColor = selectedSubZone.TitleBarFillColor;
                        controlOpacity = selectedSubZone.ControlOpacity;
                        cornerRadius = selectedSubZone.CornerRadius;
                        titleTextColor = selectedSubZone.TitleTextColor;
                        iconColor = selectedSubZone.IconColor;
                        quickBarMode = selectedSubZone.QuickBarMode;
                        bgImagePath = selectedSubZone.BackgroundImagePath;
                        bgImageStretch = selectedSubZone.BgImageStretch;
                        bgImageOffsetX = selectedSubZone.BgImageOffsetX;
                        bgImageOffsetY = selectedSubZone.BgImageOffsetY;
                        bgImageZoom = selectedSubZone.BgImageZoom;
                        bgImageOpacity = selectedSubZone.BackgroundImageOpacity;
                    }
                }
            }
            else
            {
                // No sub-zone selected (showing master's own items)
                if (_zone.MergedGroupUseUnifiedFill)
                {
                    // Unified fill mode: use master's own unified settings
                    fillColor = _zone.MergedGroupFillColor;
                    borderColor = _zone.MergedGroupBorderColor;
                    borderThickness = _zone.MergedGroupBorderThickness;
                    titleBarFillColor = _zone.MergedGroupTitleBarFillColor;
                    controlOpacity = _zone.MergedGroupControlOpacity;
                    cornerRadius = _zone.MergedGroupCornerRadius;
                    titleTextColor = _zone.MergedGroupTitleTextColor;
                    iconColor = _zone.MergedGroupIconColor;
                    quickBarMode = _zone.MergedGroupQuickBarMode;
                    bgImagePath = _zone.MergedGroupBackgroundImagePath;
                    bgImageStretch = _zone.MergedGroupBgImageStretch;
                    bgImageOffsetX = _zone.MergedGroupBgImageOffsetX;
                    bgImageOffsetY = _zone.MergedGroupBgImageOffsetY;
                    bgImageZoom = _zone.MergedGroupBgImageZoom;
                    bgImageOpacity = _zone.MergedGroupBackgroundImageOpacity;
                }
            }
        }
        else if (_zone.MergedGroupId.HasValue && _zone.MergedSubZoneIds.Count == 0)
        {
            // This is the master zone of a merged group
            if (_zone.MergedGroupUseUnifiedFill)
            {
                // Unified fill mode: use master's own fill settings
                fillColor = _zone.MergedGroupFillColor;
                borderColor = _zone.MergedGroupBorderColor;
                borderThickness = _zone.MergedGroupBorderThickness;
                titleBarFillColor = _zone.MergedGroupTitleBarFillColor;
                controlOpacity = _zone.MergedGroupControlOpacity;
                cornerRadius = _zone.MergedGroupCornerRadius;
                titleTextColor = _zone.MergedGroupTitleTextColor;
                iconColor = _zone.MergedGroupIconColor;
                quickBarMode = _zone.MergedGroupQuickBarMode;
                bgImagePath = _zone.MergedGroupBackgroundImagePath;
                bgImageStretch = _zone.MergedGroupBgImageStretch;
                bgImageOffsetX = _zone.MergedGroupBgImageOffsetX;
                bgImageOffsetY = _zone.MergedGroupBgImageOffsetY;
                bgImageZoom = _zone.MergedGroupBgImageZoom;
                bgImageOpacity = _zone.MergedGroupBackgroundImageOpacity;
            }
        }

        // Acrylic: pass resolved fillColor so it uses the correct value (not stale config)
        _resolvedFillColor = fillColor;
        ApplyAcrylic(fillColor);

        // Border: always apply user's border color and thickness (AFTER acrylic to ensure they're not overridden)
        try { ZoneBorder.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(borderColor)!); } catch { }
        ZoneBorder.BorderThickness = new Thickness(borderThickness);
        MainContent.CornerRadius = new CornerRadius(cornerRadius);
        ZoneBorder.CornerRadius = new CornerRadius(cornerRadius);
        try { FillRect.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fillColor)!); } catch { }
        FillRect.RadiusX = FillRect.RadiusY = cornerRadius;
        try { TitleBarBg.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(titleBarFillColor)!); } catch { }
        // Title text color
        try { ZoneTitleText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(titleTextColor)!); } catch { }
        // Icon color
        if (!string.IsNullOrEmpty(iconColor))
        { try { var ic = new SolidColorBrush((Color)ColorConverter.ConvertFromString(iconColor)!); TitleIconChar.Foreground = ic; RestoreIconChar.Foreground = ic; } catch { } }
        ControlPoint.Opacity = Math.Max(0.05, controlOpacity / 100.0);

        // QuickBar mode: hide title bar and control points
        if (quickBarMode)
        {
            TitleBarBg.Visibility = Visibility.Collapsed;
            ControlPoint.Visibility = Visibility.Collapsed;
        }
        else
        {
            TitleBarBg.Visibility = Visibility.Visible;
            ControlPoint.Visibility = Visibility.Visible;
        }
        if (!string.IsNullOrEmpty(bgImagePath) && File.Exists(bgImagePath))
        {
            try
            {
                var bi = new System.Windows.Media.Imaging.BitmapImage();
                bi.BeginInit();
                bi.UriSource = new Uri(bgImagePath);
                bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bi.EndInit();
                BgImage.Source = bi;
                BgImage.Stretch = Stretch.UniformToFill;

                var bw = BgImageBorder.ActualWidth > 0 ? BgImageBorder.ActualWidth : _zone.Width;
                var bh = BgImageBorder.ActualHeight > 0 ? BgImageBorder.ActualHeight : _zone.Height;

                // UniformToFill — fill target area maintaining aspect ratio
                double imgW = bi.PixelWidth;
                double imgH = bi.PixelHeight;
                double utfScale = Math.Max((bw * bgImageZoom) / imgW, (bh * bgImageZoom) / imgH);
                double displayedW = imgW * utfScale;
                double displayedH = imgH * utfScale;

                BgImage.Width = displayedW;
                BgImage.Height = displayedH;

                // Position image: center at zone center + offset (matches preview positioning)
                double zoneCenterX = bw / 2;
                double zoneCenterY = bh / 2;
                double imgCenterX = displayedW / 2;
                double imgCenterY = displayedH / 2;
                double zox = bgImageOffsetX;
                double zoy = bgImageOffsetY;

                BgImage.Margin = new Thickness(
                    zoneCenterX - imgCenterX + zox,
                    zoneCenterY - imgCenterY + zoy, 0, 0);
                BgImage.HorizontalAlignment = HorizontalAlignment.Left;
                BgImage.VerticalAlignment = VerticalAlignment.Top;
                BgImage.Opacity = Math.Max(0.01, bgImageOpacity / 100.0);
            }
            catch { BgImage.Opacity = 0; }
        }
        else { BgImage.Source = null; BgImage.Opacity = 0; }
    }

    void SetRestoreIcon()
    {
        var icon = string.IsNullOrEmpty(_zone.IconChar) ? (string.IsNullOrEmpty(_zone.Name) ? "⊞" : _zone.Name[..1]) : _zone.IconChar;
        RestoreIconChar.Text = icon;
        TitleIconChar.Text = string.IsNullOrEmpty(_zone.IconChar) ? icon : _zone.IconChar;
    }
    void OnSize(object s, SizeChangedEventArgs e) { if (!IsLoaded || MainContent.Visibility != Visibility.Visible) return; _zone.Width = Width; _zone.Height = Height; ScheduleSave(); RearrangeAll(); UpdateCanvasSize(); NativeMethods.UpdateRoundedCorners(this, (int)_zone.CornerRadius); }

    void ScheduleSave() { _savePending = true; _saveDebounce.Stop(); _saveDebounce.Start(); }

    void UpdateCanvasSize()
    {
        if (_itemCanvas == null) return;

        // Use the actually displayed items list (sub-zone's items when a sub-zone tab is selected)
        List<Models.ZoneItem> displayItems;
        if (_zone.MergedSubZoneIds.Count > 0 && _vm.SelectedSubZoneId.HasValue && _vm.SelectedSubZoneId.Value != _zone.Id)
        {
            var subZone = _mgr.Zones.FirstOrDefault(z => z.Id == _vm.SelectedSubZoneId.Value);
            displayItems = subZone?.Items ?? _zone.Items;
        }
        else
        {
            displayItems = _zone.Items;
        }

        if (displayItems.Count == 0) { _itemCanvas.Width = _zone.Width - 2; _itemCanvas.Height = _zone.Height - 50; return; }
        double maxX = 0, maxY = 0;
        foreach (var i in displayItems) { if (i.X + 80 > maxX) maxX = i.X + 80; if (i.Y + 96 > maxY) maxY = i.Y + 96; }
        _itemCanvas.Width = Math.Max(_zone.Width - 20, maxX + 20);
        _itemCanvas.Height = Math.Max(_zone.Height - 50, maxY + 20);
    }

    // ── Acrylic / frosted glass ──
    void ApplyAcrylic(string? fillColorOverride = null)
    {
        string fillColor = fillColorOverride ?? _zone.FillColor;

        if (_zone.EnableAcrylic)
        {
            AcrylicHelper.EnableBlur(this, _zone.GlassBlurAmount, _zone.GlassTintOpacity, _zone.GlassTintLuminosity, _zone.GlassColorMode);
            try
            {
                var tint = (Color)ColorConverter.ConvertFromString(fillColor)!;
                FillRect.Fill = new SolidColorBrush(tint);
                FillRect.Opacity = 1.0; // Brush alpha from FillColor controls transparency
                if (TitleBarBg != null && !string.IsNullOrEmpty(_zone.TitleBarFillColor))
                {
                    try
                    {
                        TitleBarBg.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_zone.TitleBarFillColor)!);
                    }
                    catch
                    {
                        TitleBarBg.Background = new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF));
                    }
                }
            }
            catch
            {
                if (FillRect != null) FillRect.Fill = new SolidColorBrush(Color.FromArgb(0x04, 0x00, 0x00, 0x00));
            }
        }
        else
        {
            AcrylicHelper.DisableBlur(this);
            try
            {
                FillRect.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fillColor)!);
                FillRect.Opacity = 1.0;
                TitleBarBg.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_zone.TitleBarFillColor)!);
            }
            catch { }
        }
    }

    static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            var result = FindVisualChild<T>(child);
            if (result != null) return result;
        }
        return null;
    }
    static bool IsOnItem(object s) { var c = s as DependencyObject; while (c != null) { if (c is Grid g && g.DataContext is ZoneItemViewModel) return true; c = VisualTreeHelper.GetParent(c); } return false; }

    public void RefreshZone(Zone zone)
    {
        _zone = zone; // ← KEY FIX: update the reference
        _vm.RefreshZone(zone);
        ZoneTitleText.Text = zone.Name;
        SetRestoreIcon();
        ApplyStyle();
        UpdateMergedTitle();
        RebuildSubZoneTabs();
        if (zone.IsVisible) ShowZone(); else ApplyHidden();
    }

    // ── Inline title editing ──

    void ZoneTitle_LostFocus(object s, RoutedEventArgs e)
    {
        var text = ZoneTitleText.Text?.Trim() ?? "";
        if (!string.IsNullOrEmpty(text) && text != _zone.Name)
        {
            _zone.Name = text;
            _mgr.SaveConfig();
        }
        ZoneTitleText.Text = _zone.Name;
    }

    void ZoneTitle_KeyDown(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            var text = ZoneTitleText.Text?.Trim() ?? "";
            if (!string.IsNullOrEmpty(text) && text != _zone.Name)
            {
                _zone.Name = text;
                _mgr.SaveConfig();
            }
            ZoneTitleText.Text = _zone.Name;
            // Move focus away
            FocusManager.SetFocusedElement(FocusManager.GetFocusScope(ZoneTitleText), this);
            e.Handled = true;
        }
    }

    protected override void OnActivated(EventArgs e) { base.OnActivated(e); if (IsLoaded) NativeMethods.PinToDesktop(this); }

    // ── Merge support ──

    void UpdateMergedTitle()
    {
        if (_zone.MergedSubZoneIds.Count > 0)
        {
            if (!string.IsNullOrEmpty(_zone.MergedGroupName))
                ZoneTitleText.Text = _zone.MergedGroupName;
            ZoneTitleText.IsReadOnly = true;
            ZoneTitleText.Cursor = Cursors.Arrow;
        }
        else
        {
            ZoneTitleText.IsReadOnly = false;
            ZoneTitleText.Cursor = Cursors.IBeam;
        }
    }

    void RebuildSubZoneTabs()
    {
        SubZoneTabs.Children.Clear();
        if (_zone.MergedSubZoneIds.Count == 0)
        {
            SubZoneTabsRow.Visibility = Visibility.Collapsed;
            CtxDisbandAll.Visibility = Visibility.Collapsed;
            CtxDisbandThis.Visibility = Visibility.Collapsed;
            if (CtxMergeSep != null) CtxMergeSep.Visibility = Visibility.Collapsed;
            return;
        }

        SubZoneTabsRow.Visibility = Visibility.Visible;
        CtxDisbandAll.Visibility = Visibility.Visible;
        // Show "Disband This" for sub-zones (not master)
        bool isMaster = _zone.MergedSubZoneIds.Count > 0;
        CtxDisbandThis.Visibility = isMaster ? Visibility.Collapsed : Visibility.Visible;
        if (CtxMergeSep != null) CtxMergeSep.Visibility = Visibility.Visible;

        // Master zone tab
        AddSubZoneTab(_zone.Id, _zone.Name, _zone.IconChar);

        // Sub-zone tabs
        foreach (var subId in _zone.MergedSubZoneIds)
        {
            var sub = _mgr.Zones.FirstOrDefault(z => z.Id == subId);
            if (sub != null)
                AddSubZoneTab(sub.Id, sub.Name, sub.IconChar);
        }
    }

    void AddSubZoneTab(Guid zoneId, string name, string iconChar)
    {
        var cn = _loc.CurrentLanguage == Services.Language.Chinese;
        bool isSelected = _vm.SelectedSubZoneId == zoneId;

        var tab = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(1, 0, 1, 0),
            Cursor = Cursors.Hand,
            Tag = zoneId,
            ToolTip = cn ? "点击切换到此分区" : "Click to switch to this zone"
        };

        var sp = new StackPanel { Orientation = Orientation.Horizontal };

        if (!string.IsNullOrEmpty(iconChar))
        {
            sp.Children.Add(new TextBlock
            {
                Text = iconChar,
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 3, 0)
            });
        }

        sp.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center
        });

        // Highlight selected tab
        if (isSelected)
        {
            tab.Background = new SolidColorBrush(Color.FromArgb(0x30, 0x00, 0x00, 0x00));
            foreach (var child in sp.Children)
                if (child is TextBlock tb)
                    tb.Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xF0));
        }
        else
        {
            tab.Background = new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF));
            foreach (var child in sp.Children)
                if (child is TextBlock tb)
                    tb.Foreground = new SolidColorBrush(Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF));
        }

        tab.MouseLeftButtonDown += SubZoneTab_Click;
        tab.Child = sp;
        SubZoneTabs.Children.Add(tab);
    }

    void SubZoneTab_Click(object s, MouseButtonEventArgs e)
    {
        if (s is not Border tab || tab.Tag is not Guid zoneId) return;
        _vm.SelectedSubZoneId = zoneId;
        ApplyStyle(); // Apply style based on selected sub-zone
        RearrangeAll(); // Rearrange items for the newly selected sub-zone
        UpdateCanvasSize();
        RebuildSubZoneTabs(); // refresh tab highlights
    }
}
