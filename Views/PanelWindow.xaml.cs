using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Interop;
using DesktopZones.Helpers;
using DesktopZones.Models;
using DesktopZones.Services;
using Microsoft.Win32;

namespace DesktopZones.Views;

public partial class PanelWindow : Window
{
    // Resize
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    const uint WM_NCLBUTTONDOWN = 0x00A1;
    const int HTTOPLEFT = 13, HTTOPRIGHT = 14, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SHBrowseForFolderW(ref BROWSEINFOW b);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool SHGetPathFromIDListW(IntPtr p, System.Text.StringBuilder s);
    [DllImport("ole32.dll")] static extern void CoTaskMemFree(IntPtr p);

    [StructLayout(LayoutKind.Sequential)]
    struct BROWSEINFOW
    {
        public IntPtr hwndOwner;
        public IntPtr pidlRoot;
        public IntPtr pszDisplayName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszTitle;
        public uint ulFlags;
        public IntPtr lpfn;
        public IntPtr lParam;
        public int iImage;
    }

    private readonly ZoneManager _zoneManager;
    private readonly ConfigService _configService;
    private readonly ShellIconService _iconService = new();
    private readonly LocalizationService _loc = LocalizationService.Instance;
    private readonly System.Windows.Threading.DispatcherTimer _clockTimer;
    private bool _isGridView = true;
    private Zone? _selectedZone;
    private Action<Services.Language>? _langChanged;

    public PanelWindow(ZoneManager zoneManager, ConfigService configService)
    {
        InitializeComponent();
        _zoneManager = zoneManager;
        _configService = configService;

        var config = configService.Load();
        if (config.PanelX > 0 || config.PanelY > 0)
        {
            Left = config.PanelX; Top = config.PanelY;
        }
        else
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Right - 400;
            Top = wa.Top + 60;
        }
        Width = config.PanelWidth > 200 ? config.PanelWidth : 800;
        Height = config.PanelHeight > 200 ? config.PanelHeight : 450;

        _zoneManager.ZonesChanged += RebuildDisplay;
        Loaded += OnLoad;
        LocationChanged += SavePosition;
        Activated += (_, _) => { Topmost = true; };
        SizeChanged += (_, _) => { SavePosition(null, EventArgs.Empty); NativeMethods.UpdateRoundedCorners(this, 10); };
        _langChanged = _ => ApplyLoc();
        _loc.LanguageChanged += _langChanged;

        // Clock timer
        _clockTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => UpdateClock();
        _clockTimer.Start();
    }

    void ApplyLoc()
    {
        TitleText.Text = _loc["Panel.Title"];
        HideBtn.ToolTip = _loc["Panel.Hide"];
        SearchPlaceholder.Text = _loc["Panel.Search"];
        PopulateZoneSelector();
    }

    void UpdateClock()
    {
        if (ClockText == null || DateText == null) return;
        var now = DateTime.Now;
        ClockText.Text = now.ToString("HH:mm:ss");
        DateText.Text = now.ToString("yyyy年M月d日 dddd");
    }

    void PopulateZoneSelector()
    {
        if (ZoneSelector == null) return;
        var prevSelection = ZoneSelector.SelectedIndex;
        ZoneSelector.Items.Clear();
        var cn = _loc.CurrentLanguage == Services.Language.Chinese;
        ZoneSelector.Items.Add(cn ? "全部分区" : "All Zones");
        foreach (var zone in _zoneManager.Zones)
        {
            ZoneSelector.Items.Add(zone.Name);
        }
        if (prevSelection >= 0 && prevSelection < ZoneSelector.Items.Count)
            ZoneSelector.SelectedIndex = prevSelection;
        else
            ZoneSelector.SelectedIndex = 0;
    }

    void ZoneSelector_Changed(object s, SelectionChangedEventArgs e)
    {
        if (ZoneSelector == null || ZoneSelector.SelectedIndex < 0) return;
        if (ZoneSelector.SelectedIndex == 0)
            _selectedZone = null;
        else if (ZoneSelector.SelectedIndex - 1 < _zoneManager.Zones.Count)
            _selectedZone = _zoneManager.Zones[ZoneSelector.SelectedIndex - 1];
        RebuildDisplay();
    }

    void SearchBox_TextChanged(object s, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text)
            ? Visibility.Visible : Visibility.Collapsed;
        RebuildDisplay();
    }

    void GridToggle_Click(object s, MouseButtonEventArgs e)
    {
        _isGridView = true;
        if (GridToggleBtn != null) GridToggleBtn.Background = new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF));
        if (ListToggleBtn != null) ListToggleBtn.Background = Brushes.Transparent;
        RebuildDisplay();
    }

    void ListToggle_Click(object s, MouseButtonEventArgs e)
    {
        _isGridView = false;
        if (ListToggleBtn != null) ListToggleBtn.Background = new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF));
        if (GridToggleBtn != null) GridToggleBtn.Background = Brushes.Transparent;
        RebuildDisplay();
    }

    void SettingsBtn_Click(object s, MouseButtonEventArgs e)
    {
        var config = _zoneManager.GetConfig();
        var dlg = new WidgetSettingsDialog(WidgetSettingsTarget.Panel) { Owner = this };
        dlg.LoadFromConfig(config, _zoneManager);
        if (dlg.ShowDialog() == true && dlg.DialogResultOk)
        {
            config.PanelWidth = dlg.ParsedWidth;
            config.PanelHeight = dlg.ParsedHeight;
            config.GlobalBorderThickness = dlg.ParsedBorderThickness;
            config.GlobalBorderColor = dlg.ParsedBorderColor;
            config.PanelFillColor = dlg.ParsedFillColor;
            config.PanelUseGlobalAppearance = dlg.ParsedUseGlobalAppearance;
            config.GlassBlurAmount = dlg.ParsedGlassBlur;
            config.GlassTintOpacity = dlg.ParsedGlassTintOpacity;
            config.GlassTintLuminosity = dlg.ParsedGlassLuminosity;
            config.GlassColorMode = dlg.ParsedGlassColorMode;
            config.EnableLiquidGlass = dlg.ParsedLiquidGlass;
            config.PanelTitleBarFillColor = dlg.ParsedTitleBarFill;
            config.PanelControlOpacity = dlg.ParsedButtonOpacity;

            // Panel background image
            config.PanelBackgroundImagePath = dlg.ParsedBgImagePath;
            config.PanelBgImageOffsetX = dlg.ParsedBgOffsetX;
            config.PanelBgImageOffsetY = dlg.ParsedBgOffsetY;
            config.PanelBgImageZoom = dlg.ParsedBgZoom;
            config.PanelBackgroundImageOpacity = dlg.ParsedBgOpacity;

            _configService.Save(config);
            ApplyAcrylic();
            ApplyStyle();
            ApplyBackgroundImage();
        }
        e?.Handled = true;
    }

    void OnLoad(object s, RoutedEventArgs e)
    {
        // Panel should stay above other windows - don't call PinToDesktop
        NativeMethods.SetToolWindow(this);
        ApplyAcrylic();
        ApplyStyle();
        ApplyBackgroundImage();
        ApplyLoc();
        RebuildDisplay();
        // Set rounded corners LAST after all sizing
        NativeMethods.SetRoundedCorners(this, 10);
        NativeMethods.UpdateRoundedCorners(this, 10);
    }

    void SavePosition(object? _, EventArgs __)
    {
        var config = _configService.Load();
        config.PanelX = Left;
        config.PanelY = Top;
        config.PanelWidth = Width;
        config.PanelHeight = Height;
        _configService.Save(config);
        _zoneManager.GetConfig().PanelX = Left;
        _zoneManager.GetConfig().PanelY = Top;
        _zoneManager.GetConfig().PanelWidth = Width;
        _zoneManager.GetConfig().PanelHeight = Height;
    }

    // ── Acrylic ──

    /// <summary>Refresh all visual styles. Optional <paramref name="cfg"/> is applied
    /// to the underlying config first; mirrors ZoneWindow.RefreshZone's reference-update
    /// pattern (see ClockWidget.RefreshAppearance for rationale).</summary>
    public void RefreshAppearance(Models.PanelPresetConfig? cfg = null)
    {
        if (cfg != null) cfg.ApplyTo(_zoneManager.GetConfig());
        ApplyAcrylic();
        ApplyStyle();
        ApplyBackgroundImage();
    }

    public void ApplyAcrylic()
    {
        var config = _zoneManager.GetConfig();
        string fillColorStr = config.PanelFillColor;

        if (config.EnableLiquidGlass || config.GlassBlurAmount > 0)
        {
            AcrylicHelper.EnableBlur(this, config.GlassBlurAmount, config.GlassTintOpacity, config.GlassTintLuminosity, config.GlassColorMode);
        }
        else
        {
            AcrylicHelper.DisableBlur(this);
        }
    }

    public void ApplyStyle()
    {
        var config = _zoneManager.GetConfig();
        string fillColorStr = config.PanelFillColor;
        // Use PanelBorderColor when panel opts out of global appearance (mirrors
        // ClockWidget/CalendarWidget ApplyAcrylic pattern). Otherwise fall back to
        // GlobalBorderColor for visual consistency with other global-styled widgets.
        string borderColorStr = config.PanelUseGlobalAppearance ? config.GlobalBorderColor : config.PanelBorderColor;
        double borderThickness = config.PanelUseGlobalAppearance ? config.GlobalBorderThickness : config.GlobalBorderThickness;

        // Fill
        try
        {
            var fill = (Color)ColorConverter.ConvertFromString(fillColorStr)!;
            FillRect.Fill = new SolidColorBrush(fill);
            FillRect.Opacity = 1.0; // Brush alpha from FillColor controls transparency
        }
        catch { }

        // Border
        try
        {
            PanelBorder.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(borderColorStr)!);
        }
        catch { }
        PanelBorder.BorderThickness = new Thickness(borderThickness);

        // Title bar fill
        try
        {
            var tbColor = (Color)ColorConverter.ConvertFromString(config.PanelTitleBarFillColor);
            TopBar.Background = new SolidColorBrush(tbColor);
        }
        catch { }
    }

    public void ApplyBackgroundImage()
    {
        try
        {
            var config = _zoneManager.GetConfig();
            if (!string.IsNullOrEmpty(config.PanelBackgroundImagePath) && System.IO.File.Exists(config.PanelBackgroundImagePath))
            {
                var bi = new System.Windows.Media.Imaging.BitmapImage();
                bi.BeginInit();
                bi.UriSource = new Uri(config.PanelBackgroundImagePath);
                bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bi.EndInit();
                BgImage.Source = bi;
                BgImage.Stretch = Stretch.UniformToFill;

                double bw = BgImageBorder.ActualWidth > 0 ? BgImageBorder.ActualWidth : Width;
                double bh = BgImageBorder.ActualHeight > 0 ? BgImageBorder.ActualHeight : Height;

                // UniformToFill — fill target area maintaining aspect ratio
                double imgW = bi.PixelWidth;
                double imgH = bi.PixelHeight;
                double utfScale = Math.Max((bw * config.PanelBgImageZoom) / imgW, (bh * config.PanelBgImageZoom) / imgH);
                double displayedW = imgW * utfScale;
                double displayedH = imgH * utfScale;

                BgImage.Width = displayedW;
                BgImage.Height = displayedH;

                // Position image: center at zone center + offset (matches ZoneWindow logic)
                double zoneCenterX = bw / 2;
                double zoneCenterY = bh / 2;
                double imgCenterX = displayedW / 2;
                double imgCenterY = displayedH / 2;
                double ox = config.PanelBgImageOffsetX;
                double oy = config.PanelBgImageOffsetY;

                BgImage.Margin = new Thickness(
                    zoneCenterX - imgCenterX + ox,
                    zoneCenterY - imgCenterY + oy, 0, 0);
                BgImage.HorizontalAlignment = HorizontalAlignment.Left;
                BgImage.VerticalAlignment = VerticalAlignment.Top;
                BgImage.Opacity = Math.Max(0.01, config.PanelBackgroundImageOpacity / 100.0);
            }
            else
            {
                BgImage.Source = null;
                BgImage.Opacity = 0;
            }
        }
        catch { if (BgImage != null) { BgImage.Source = null; BgImage.Opacity = 0; } }
    }

    // ── Build display ──

    public void RebuildDisplay()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                if (ContentStack == null) return;
                ContentStack.Children.Clear();

                string search = SearchBox?.Text?.Trim() ?? "";
                bool hasSearch = !string.IsNullOrEmpty(search);

                var zonesToShow = _selectedZone != null
                    ? new[] { _selectedZone }
                    : _zoneManager.Zones.ToArray();

                if (_isGridView)
                {
                    // Grid view: wrap items in a WrapPanel
                    var wrapPanel = new WrapPanel { Margin = new Thickness(4) };
                    foreach (var zone in zonesToShow)
                    {
                        foreach (var item in zone.Items.ToList())
                        {
                            if (hasSearch && !FuzzySearchHelper.MatchFuzzy(item.Name, search))
                                continue;
                            var card = CreateItemCard(item, zone, isGrid: true);
                            wrapPanel.Children.Add(card);
                        }
                    }
                    ContentStack.Children.Add(wrapPanel);
                }
                else
                {
                    // List view: vertical stack
                    foreach (var zone in zonesToShow)
                    {
                        foreach (var item in zone.Items.ToList())
                        {
                            if (hasSearch && !FuzzySearchHelper.MatchFuzzy(item.Name, search))
                                continue;
                            var card = CreateItemCard(item, zone, isGrid: false);
                            ContentStack.Children.Add(card);
                        }
                    }
                }
            }
            catch { }
        }), System.Windows.Threading.DispatcherPriority.Normal);
    }

    Border CreateZoneHeader(Zone zone)
    {
        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 5, 8, 5),
            Margin = new Thickness(0, 4, 0, 4),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1)
        };

        var stack = new StackPanel { Orientation = Orientation.Horizontal };

        // Zone icon
        var iconBorder = new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)),
            Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center
        };
        iconBorder.Child = new TextBlock
        {
            Text = string.IsNullOrEmpty(zone.IconChar) ? "⊞" : zone.IconChar,
            FontSize = 13, Foreground = new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        stack.Children.Add(iconBorder);

        // Zone name
        stack.Children.Add(new TextBlock
        {
            Text = zone.Name, FontSize = 13, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xF0)),
            VerticalAlignment = VerticalAlignment.Center
        });

        card.Child = stack;
        return card;
    }

    Border CreateItemCard(ZoneItem item, Zone zone, bool isGrid = true)
    {
        var card = new Border
        {
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(4),
            Cursor = Cursors.Hand,
            Tag = (item, zone)
        };

        card.MouseLeftButtonDown += Item_Click;
        card.MouseRightButtonDown += Item_RightClick;
        card.MouseEnter += Item_Enter;
        card.MouseLeave += Item_Leave;
        card.ContextMenu = CreateItemContextMenu(item, zone);

        if (isGrid)
        {
            // Grid view: icon centered, name below
            card.Width = 80; card.Height = 80;
            card.Margin = new Thickness(4);
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var iconImg = new System.Windows.Controls.Image
            {
                Width = 40, Height = 40, Stretch = Stretch.Uniform,
                Source = _iconService.GetIcon(item.TargetPath, item.Type),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 4)
            };
            RenderOptions.SetBitmapScalingMode(iconImg, BitmapScalingMode.HighQuality);
            stack.Children.Add(iconImg);
            stack.Children.Add(new TextBlock
            {
                Text = item.Name, FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0xC0, 0xD0)),
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextAlignment = TextAlignment.Center,
                MaxWidth = 72
            });
            card.Child = stack;
        }
        else
        {
            // List view: icon + name horizontal
            card.Margin = new Thickness(0, 2, 0, 2);
            card.Padding = new Thickness(8, 4, 8, 4);
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var iconImg = new System.Windows.Controls.Image
            {
                Width = 20, Height = 20, Stretch = Stretch.Uniform,
                Source = _iconService.GetIcon(item.TargetPath, item.Type),
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            RenderOptions.SetBitmapScalingMode(iconImg, BitmapScalingMode.HighQuality);
            Grid.SetColumn(iconImg, 0);
            grid.Children.Add(iconImg);
            var nameTb = new TextBlock
            {
                Text = item.Name, FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xF0)),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(nameTb, 1);
            grid.Children.Add(nameTb);
            card.Child = grid;
        }
        return card;
    }

    ContextMenu CreateItemContextMenu(ZoneItem item, Zone zone)
    {
        var menu = new ContextMenu();

        var openItem = new MenuItem { Header = _loc["Item.Open"] };
        openItem.Click += (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = item.TargetPath, UseShellExecute = true }); } catch { }
        };
        menu.Items.Add(openItem);

        var openLocation = new MenuItem { Header = _loc["Item.OpenLocation"] };
        openLocation.Click += (_, _) =>
        {
            if (item.Type is ItemType.Shortcut or ItemType.Application)
            {
                var d = Path.GetDirectoryName(item.TargetPath);
                if (!string.IsNullOrEmpty(d))
                    System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{item.TargetPath}\"");
            }
            else System.Diagnostics.Process.Start("explorer.exe", item.TargetPath);
        };
        menu.Items.Add(openLocation);

        var renameItem = new MenuItem { Header = _loc["Item.Rename"] };
        renameItem.Click += (_, _) =>
        {
            var rn = new RenameDialog(item.Name) { Owner = this };
            if (rn.ShowDialog() == true)
            {
                item.Name = rn.NewName;
                _zoneManager.SaveConfig();
                _zoneManager.NotifyChanged();
            }
        };
        menu.Items.Add(renameItem);

        menu.Items.Add(new Separator());

        var deleteItem = new MenuItem { Header = _loc["Item.Delete"], Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x66, 0x66)) };
        deleteItem.Click += (_, _) =>
        {
            zone.Items.Remove(item);
            _zoneManager.SaveConfig();
            _zoneManager.NotifyChanged();
            // ZonesChanged fires → RebuildDisplay + ZoneWindow refreshes
        };
        menu.Items.Add(deleteItem);

        return menu;
    }

    void Item_Click(object s, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            if (s is Border b && b.Tag is (ZoneItem item, _))
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = item.TargetPath, UseShellExecute = true }); } catch { }
                e.Handled = true;
            }
        }
    }

    void Item_RightClick(object s, MouseButtonEventArgs e)
    {
        if (s is Border b && b.ContextMenu != null)
        {
            b.ContextMenu.IsOpen = true;
            e.Handled = true;
        }
    }

    void Item_Enter(object s, MouseEventArgs e)
    {
        if (s is Border b)
            b.Background = new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF));
    }

    void Item_Leave(object s, MouseEventArgs e)
    {
        if (s is Border b)
            b.Background = Brushes.Transparent;
    }

    // ── New file/folder helpers ──

    void CreateNewFolder(Zone zone)
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
                    string parentPath = sb.ToString();
                    string folderName = Microsoft.VisualBasic.Interaction.InputBox(
                        "Folder Name:", "New Folder", "New Folder");
                    if (!string.IsNullOrWhiteSpace(folderName))
                    {
                        string fullPath = Path.Combine(parentPath, folderName);
                        Directory.CreateDirectory(fullPath);
                        AddItemToZone(zone, Path.GetFileName(fullPath), fullPath, ItemType.Folder);
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

    void CreateNewFile(string defaultExt, string filter, Zone zone)
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
            try
            {
                File.Create(d.FileName).Dispose();
                string name = Path.GetFileNameWithoutExtension(d.FileName);
                ItemType type = Path.GetExtension(d.FileName).ToLowerInvariant() switch
                {
                    ".lnk" => ItemType.Shortcut,
                    ".exe" => ItemType.Application,
                    ".txt" => ItemType.Document,
                    ".docx" => ItemType.Document,
                    ".pptx" => ItemType.Document,
                    ".xlsx" => ItemType.Document,
                    _ => ItemType.Shortcut
                };
                AddItemToZone(zone, name, d.FileName, type);
            }
            catch (Exception ex) { MessageBox.Show($"Failed: {ex.Message}"); }
        }
    }

    void AddItemToZone(Zone zone, string name, string path, ItemType type, double x = 10, double y = 10)
    {
        var item = new ZoneItem(name, path, type, x, y);
        zone.Items.Add(item);
        _zoneManager.SaveConfig();
        _zoneManager.NotifyChanged();
        // ZonesChanged fires → RebuildDisplay + ZoneWindow refreshes
    }

    // ── Title bar drag ──

    // ── Content area right-click ──

    void ContentArea_RightClick(object s, MouseButtonEventArgs e)
    {
        var contextMenu = new ContextMenu();

        // Import Files
        var importFilesItem = new MenuItem { Header = _loc["Panel.ImportFiles"] };
        importFilesItem.Click += ImportFile_Click;
        contextMenu.Items.Add(importFilesItem);

        // Import Folder
        var importFolderItem = new MenuItem { Header = _loc["Panel.ImportFolder"] };
        importFolderItem.Click += ImportFolder_Click;
        contextMenu.Items.Add(importFolderItem);

        contextMenu.Items.Add(new Separator());

        // New submenu - same structure as zone
        var newItem = new MenuItem { Header = _loc["Panel.New"] };

        var newFolderItem = new MenuItem { Header = _loc["Panel.NewFolder"] };
        newFolderItem.Click += NewFolder_Click;
        newItem.Items.Add(newFolderItem);

        newItem.Items.Add(new Separator());

        var newTxtItem = new MenuItem { Header = _loc["Panel.NewTxt"] };
        newTxtItem.Click += NewTextFile_Click;
        newItem.Items.Add(newTxtItem);

        var newDocxItem = new MenuItem { Header = _loc["Panel.NewDocx"] };
        newDocxItem.Click += NewWordFile_Click;
        newItem.Items.Add(newDocxItem);

        var newPptxItem = new MenuItem { Header = _loc["Panel.NewPptx"] };
        newPptxItem.Click += NewPptFile_Click;
        newItem.Items.Add(newPptxItem);

        var newXlsxItem = new MenuItem { Header = _loc["Panel.NewXlsx"] };
        newXlsxItem.Click += NewExcelFile_Click;
        newItem.Items.Add(newXlsxItem);

        contextMenu.Items.Add(newItem);

        contextMenu.Items.Add(new Separator());

        // Edit Panel - same position as zone's "Edit Zone"
        var editItem = new MenuItem { Header = _loc["Panel.Settings"] };
        editItem.Click += (_, _) => SettingsBtn_Click(null!, null!);
        contextMenu.Items.Add(editItem);

        // Hide Panel - same position as zone's "Hide Zone"
        var hideItem = new MenuItem { Header = _loc["Panel.Hide"] };
        hideItem.Click += (_, _) => HideButton_Click(null!, null!);
        contextMenu.Items.Add(hideItem);

        ContextMenu = contextMenu;
        contextMenu.IsOpen = true;
        e.Handled = true;
    }

    void TitleBar_Drag(object s, MouseButtonEventArgs e)
    {
        try { DragMove(); } catch { }
    }

    // ── Resize ──

    void ResizeGrip_Down(object s, MouseButtonEventArgs e)
    {
        if (s is not Border g) return;
        // Determine direction based on horizontal/vertical alignment
        bool isLeft = g.HorizontalAlignment == HorizontalAlignment.Left;
        bool isTop = g.VerticalAlignment == VerticalAlignment.Top;
        int d = (isLeft, isTop) switch
        {
            (true, true) => HTTOPLEFT,
            (false, true) => HTTOPRIGHT,
            (true, false) => HTBOTTOMLEFT,
            _ => HTBOTTOMRIGHT
        };
        SendMessage(new WindowInteropHelper(this).Handle, WM_NCLBUTTONDOWN, (IntPtr)d, IntPtr.Zero);
        e.Handled = true;
    }

    // ── Import/New ──

    void ImportBtn_Click(object s, MouseButtonEventArgs e)
    {
        if (s is Border b)
        {
            b.ContextMenu.IsOpen = true;
            e.Handled = true;
        }
    }

    void ImportFile_Click(object s, RoutedEventArgs e)
    {
        var targetZone = GetTargetZone();
        if (targetZone == null) return;

        var d = new OpenFileDialog
        {
            Title = _loc["Zone.ImportTitle"],
            Filter = "All|*.lnk;*.exe;*.*|Shortcuts|*.lnk|Apps|*.exe",
            Multiselect = true
        };
        if (d.ShowDialog() == true)
        {
            ImportFilesToZone(targetZone, d.FileNames);
        }
    }

    void ImportFolder_Click(object s, RoutedEventArgs e)
    {
        var targetZone = GetTargetZone();
        if (targetZone == null) return;

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
                lpszTitle = _loc.CurrentLanguage == Services.Language.Chinese ? "选择文件夹" : "Select Folder",
                ulFlags = 0x40
            };
            pidl = SHBrowseForFolderW(ref bi);
            if (pidl != IntPtr.Zero)
            {
                var sb = new System.Text.StringBuilder(260);
                if (SHGetPathFromIDListW(pidl, sb) && Directory.Exists(sb.ToString()))
                {
                    ImportFilesToZone(targetZone, new[] { sb.ToString() });
                }
            }
        }
        catch (Exception ex) { MessageBox.Show($"Import failed: {ex.Message}"); }
        finally
        {
            if (displayBuf != IntPtr.Zero) Marshal.FreeHGlobal(displayBuf);
            if (pidl != IntPtr.Zero) CoTaskMemFree(pidl);
        }
    }

    void NewFolder_Click(object s, RoutedEventArgs e)
    {
        var targetZone = GetTargetZone();
        if (targetZone == null) return;
        CreateNewFolder(targetZone);
    }

    void NewTextFile_Click(object s, RoutedEventArgs e)
    {
        var targetZone = GetTargetZone();
        if (targetZone == null) return;
        CreateNewFile(".txt", "Text Files|*.txt", targetZone);
    }

    void NewWordFile_Click(object s, RoutedEventArgs e)
    {
        var targetZone = GetTargetZone();
        if (targetZone == null) return;
        CreateNewFile(".docx", "Word Files|*.docx", targetZone);
    }

    void NewPptFile_Click(object s, RoutedEventArgs e)
    {
        var targetZone = GetTargetZone();
        if (targetZone == null) return;
        CreateNewFile(".pptx", "PowerPoint Files|*.pptx", targetZone);
    }

    void NewExcelFile_Click(object s, RoutedEventArgs e)
    {
        var targetZone = GetTargetZone();
        if (targetZone == null) return;
        CreateNewFile(".xlsx", "Excel Files|*.xlsx", targetZone);
    }

    private Zone? GetTargetZone()
    {
        if (_selectedZone != null)
            return _selectedZone;

        // If showing all zones, show zone selector dialog
        if (_zoneManager.Zones.Count == 1)
            return _zoneManager.Zones[0];

        if (_zoneManager.Zones.Count > 1)
        {
            // Show zone selection dialog
            var cn = _loc.CurrentLanguage == Services.Language.Chinese;
            var dlg = new Window
            {
                Title = cn ? "选择分区" : "Select Zone",
                Width = 300, Height = 200,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent
            };

            var mainBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x2E)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10)
            };

            var grid = new Grid { Margin = new Thickness(18) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Title
            var titleText = new TextBlock
            {
                Text = cn ? "选择目标分区" : "Select Target Zone",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xF0)),
                Margin = new Thickness(0, 0, 0, 12)
            };
            Grid.SetRow(titleText, 0);
            grid.Children.Add(titleText);

            // Zone list
            var listBox = new ListBox
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xF0))
            };
            foreach (var zone in _zoneManager.Zones)
            {
                listBox.Items.Add(zone.Name);
            }
            listBox.SelectedIndex = 0;
            Grid.SetRow(listBox, 1);
            grid.Children.Add(listBox);

            // Buttons
            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };

            Zone? selectedZone = null;

            var cancelButton = new Button
            {
                Content = cn ? "取消" : "Cancel",
                Width = 60, Height = 28, FontSize = 11,
                Background = new SolidColorBrush(Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xC0)),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x25, 0xFF, 0xFF, 0xFF)),
                Margin = new Thickness(0, 0, 8, 0)
            };
            cancelButton.Click += (_, _) => dlg.Close();
            buttonPanel.Children.Add(cancelButton);

            var selectButton = new Button
            {
                Content = cn ? "选择" : "Select",
                Width = 60, Height = 28, FontSize = 11,
                Background = new SolidColorBrush(Color.FromRgb(0x7C, 0x3A, 0xED)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0)
            };
            selectButton.Click += (_, _) =>
            {
                if (listBox.SelectedIndex >= 0)
                {
                    selectedZone = _zoneManager.Zones[listBox.SelectedIndex];
                }
                dlg.Close();
            };
            buttonPanel.Children.Add(selectButton);

            Grid.SetRow(buttonPanel, 2);
            grid.Children.Add(buttonPanel);

            mainBorder.Child = grid;
            dlg.Content = mainBorder;
            dlg.ShowDialog();

            return selectedZone;
        }

        return null;
    }

    private void ImportFilesToZone(Zone zone, string[] paths)
    {
        var (sx, sy) = FindFreeSpot(zone);
        foreach (var f in paths)
        {
            string name = Path.GetFileName(f);
            ItemType type = File.GetAttributes(f).HasFlag(FileAttributes.Directory)
                ? ItemType.Folder
                : Path.GetExtension(f).ToLowerInvariant() switch
                {
                    ".lnk" => ItemType.Shortcut,
                    ".exe" => ItemType.Application,
                    _ => ItemType.Shortcut
                };
            AddItemToZone(zone, name, f, type, sx, sy);
            sx += 80;
            if (sx > zone.Width - 80) { sx = 10; sy += 90; }
        }
    }

    private (double, double) FindFreeSpot(Zone zone)
    {
        if (zone.Items.Count == 0) return (10, 10);
        double maxY = 0;
        foreach (var i in zone.Items) { if (i.Y > maxY) maxY = i.Y; }
        double maxX = 0;
        foreach (var i in zone.Items) { if (Math.Abs(i.Y - maxY) < 10 && i.X > maxX) maxX = i.X; }
        double sx = maxX + 80, sy = maxY;
        if (sx > zone.Width - 80) { sx = 10; sy = maxY + 90; }
        return (sx, sy);
    }

    // ── Drag-drop from Explorer ──

    void Panel_DragEnter(object s, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Link;
            e.Handled = true;
        }
    }

    void Panel_DragOver(object s, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Link;
            e.Handled = true;
        }
    }

    void Panel_Drop(object s, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } fs) return;
        var targetZone = GetTargetZone();
        if (targetZone == null) return;
        ImportFilesToZone(targetZone, fs);
        e.Handled = true;
    }

    // ── Hide ──

    void HideButton_Click(object s, MouseButtonEventArgs e)
    {
        SavePosition(null, EventArgs.Empty);
        var config = _configService.Load();
        config.PanelEnabled = false;
        _configService.Save(config);
        Close();
        e?.Handled = true;
    }

    void Ctrl_Enter(object s, MouseEventArgs e)
    {
        if (s is Border b) b.Background = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF));
    }

    void Ctrl_Leave(object s, MouseEventArgs e)
    {
        if (s is Border b) b.Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_langChanged != null) { _loc.LanguageChanged -= _langChanged; _langChanged = null; }
        _zoneManager.ZonesChanged -= RebuildDisplay;
        SavePosition(null, EventArgs.Empty);
        // Clear the enabled flag so it doesn't auto-restore on next launch
        var config = _configService.Load();
        config.PanelEnabled = false;
        _configService.Save(config);
        base.OnClosed(e);
    }
}
