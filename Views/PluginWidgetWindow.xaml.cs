using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DeskOrder.PluginApi;
using DesktopZones.Helpers;
using DesktopZones.Services;

namespace DesktopZones.Views;

/// <summary>
/// 插件组件窗口：无边框、桌面层、玻璃/圆角、拖动、缩放、锁定、收起为恢复按钮。
/// 插件只提供内容（<c>descriptor.CreateContent</c>），窗口生命周期完全由宿主掌握。
/// </summary>
public partial class PluginWidgetWindow : Window
{
    readonly PluginWidget _widget;
    readonly PluginService _service;
    readonly LocalizationService _loc = LocalizationService.Instance;
    readonly SnapDrag? _snapDrag;

    PluginWidgetContext? _context;
    JsonPluginStorage? _storage;
    bool _suppressSave;
    bool _collapsed;
    double _restoreWidth;
    double _restoreHeight;
    double _minWidth = 60;
    double _minHeight = 40;
    double _maxWidth = 1200;
    double _maxHeight = 900;
    Action<string>? _langChanged;

    public PluginWidget Widget => _widget;

    public PluginWidgetWindow(PluginWidget widget, PluginService service)
    {
        InitializeComponent();
        // 右键菜单图标（方案 B · 简笔）：这颗组件没有标题栏锁定按钮，直接读模型状态。
        MenuIconBinder.Hook(this, () => _widget?.IsLocked == true ? "🔒" : "🔓");
        _widget = widget;
        _service = service;

        Left = widget.X;
        Top = widget.Y;
        Width = Math.Max(60, widget.Width);
        Height = Math.Max(40, widget.Height);
        Opacity = Math.Clamp(widget.Opacity, 0.2, 1.0);
        _restoreWidth = Width;
        _restoreHeight = Height;

        var descriptor = service.FindDescriptor(widget.PluginId, widget.WidgetTypeId);
        if (descriptor != null)
        {
            _minWidth = Math.Max(60, descriptor.MinSize.Width);
            _minHeight = Math.Max(40, descriptor.MinSize.Height);
            _maxWidth = Math.Max(_minWidth, descriptor.MaxSize.Width);
            _maxHeight = Math.Max(_minHeight, descriptor.MaxSize.Height);
        }
        // 窗口最小尺寸固定为 44（收起态就是一个 44×44 的恢复按钮）；
        // 组件内容的最小尺寸在 ResizeGrip 里单独约束。
        MinWidth = 44;
        MinHeight = 44;
        MaxWidth = _maxWidth;
        MaxHeight = _maxHeight;

        ApplyChrome();
        BuildContent(descriptor);

        Deactivated += (_, _) => { if (IsVisible) NativeMethods.PinBelowProgman(this); };
        Loaded += OnLoaded;
        LocationChanged += (_, _) =>
        {
            if (_suppressSave || _collapsed) return;
            _widget.X = Left;
            _widget.Y = Top;
            _service.SaveWidgets();
        };
        SizeChanged += (_, _) =>
        {
            if (_suppressSave || _collapsed || !IsLoaded) return;
            _widget.Width = Width;
            _widget.Height = Height;
            _service.SaveWidgets();
            NativeMethods.UpdateRoundedCorners(this, _widget.CornerRadius);
        };

        _snapDrag = new SnapDrag(this);
        _langChanged = _ => UpdateMenuLabels();
        _loc.LanguageChanged += _langChanged;

        if (!widget.IsVisible) CollapseNow();


    }
    /// <summary>右键菜单图标（方案 B · 简笔）：Opened 兜底 —— 不管菜单是
    /// ContextMenuService 自动弹的还是代码里手动 IsOpen 的，这里都会补一次。
    /// 与 MenuIconBinder.Hook 是两条路，Apply 幂等，重复调用无副作用。</summary>
    void WidgetMenu_Opened(object s, RoutedEventArgs e) =>
        MenuIconBinder.Apply(s as ContextMenu, _widget?.IsLocked == true ? "🔒" : "🔓");

    void OnLoaded(object sender, RoutedEventArgs e)
    {
        DesktopLayer.BringToFront(this);
        NativeMethods.SetToolWindow(this);
        NativeMethods.DisableDwmFrameShadow(this);
        NativeMethods.RemoveWindowBorder(this);
        if (_widget.EnableAcrylic)
        {
            try
            {
                AcrylicHelper.EnableBlur(this, _widget.GlassBlurAmount, _widget.GlassTintOpacity,
                    _widget.GlassTintLuminosity, _widget.GlassColorMode);
            }
            catch (Exception ex)
            {
                DzTrace.Always("[Plugin] acrylic failed: " + ex.Message);
            }
        }
        NativeMethods.SetRoundedCorners(this, _widget.CornerRadius);
        if (_widget.IsVisible) ShowWidget();
    }

    // ── 外观 ──

    void ApplyChrome()
    {
        MainContent.CornerRadius = new CornerRadius(_widget.CornerRadius);
        MainContent.BorderThickness = new Thickness(Math.Max(0, _widget.BorderThickness));
        MainContent.BorderBrush = BrushFrom(_widget.BorderColor, ThemeBrushes.BorderSubtleModern);
        MainContent.Background = BrushFrom(_widget.FillColor, Brushes.Transparent);
        ResizeGrip.Visibility = _widget.IsLocked ? Visibility.Collapsed : Visibility.Visible;
        UpdateMenuLabels();
    }

    static Brush BrushFrom(string? hex, Brush fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try
        {
            if (ColorConverter.ConvertFromString(hex) is Color color)
                return new SolidColorBrush(color);
        }
        catch { }
        return fallback;
    }

    // ── 内容 ──

    void BuildContent(PluginWidgetDescriptor? descriptor)
    {
        var loaded = _service.GetLoaded(_widget.PluginId);
        if (descriptor == null || loaded == null)
        {
            var record = _service.GetRecord(_widget.PluginId);
            var name = record?.Id ?? _widget.PluginId;
            ShowPlaceholder(_loc.Get("Plugin.Disabled.Placeholder", name));
            return;
        }

        _storage = new JsonPluginStorage(null, _widget.SettingsJson, () =>
        {
            _widget.SettingsJson = _storage?.ToJson() ?? "{}";
            _service.SaveWidgets();
        });

        _context = new PluginWidgetContext
        {
            Settings = _storage,
            IsTileMode = _widget.TileMode,
            IsLocked = _widget.IsLocked,
            Foreground = ThemeBrushes.TextPrimaryModern,
            Accent = ThemeBrushes.AccentModern,
            Scale = 1.0,
            Loc = PluginLocalizer.Instance,
            SaveSettingsCallback = () => _storage.Flush(),
            NotifyCallback = (title, body) =>
                _service.Notify($"{descriptor.DisplayName} · {title}", body)
        };

        try
        {
            var content = descriptor.CreateContent(_context);
            if (content == null) throw new InvalidOperationException("CreateContent returned null");
            ContentHost.Content = content;
            ErrorHost.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            DzTrace.Always($"[Plugin] {_widget.PluginId}/{_widget.WidgetTypeId} content failed: {ex}");
            ShowPlaceholder(_loc.Get("Plugin.Error.LoadFailed", _widget.PluginId, ex.Message));
        }
    }

    void ShowPlaceholder(string message)
    {
        ContentHost.Content = null;
        ContentHost.Visibility = Visibility.Collapsed;
        ErrorText.Text = message;
        ErrorHost.Visibility = Visibility.Visible;
    }

    // ── 显示 / 隐藏 ──

    public void ShowWidget()
    {
        _widget.IsVisible = true;
        _collapsed = false;
        _suppressSave = true;
        try
        {
            MainContent.Visibility = Visibility.Visible;
            RestoreButton.Visibility = Visibility.Collapsed;
            if (Width < 60 || Height < 40)
            {
                Width = _restoreWidth;
                Height = _restoreHeight;
            }
        }
        finally { _suppressSave = false; }

        if (!IsVisible) Show();
        DesktopLayer.BringToFront(this);
        _service.SaveWidgets();
    }

    public void HideWidget(double delayMs = 0)
    {
        if (delayMs <= 0)
        {
            CollapseNow();
            return;
        }
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delayMs) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            CollapseNow();
        };
        timer.Start();
    }

    void CollapseNow()
    {
        if (_collapsed) return;
        _suppressSave = true;
        try
        {
            if (MainContent.Visibility == Visibility.Visible)
            {
                _restoreWidth = Math.Max(60, Width);
                _restoreHeight = Math.Max(40, Height);
            }
            MainContent.Visibility = Visibility.Collapsed;
            // 「可恢复按钮」关掉时不留 44×44 的占位，整窗隐藏，只能从托盘「全部显示」找回
            // （与内置组件的同名开关语义一致）。
            if (_widget.EnableRestoreButton)
            {
                RestoreButton.Visibility = Visibility.Visible;
                ResizeGrip.Visibility = Visibility.Collapsed;
                Width = 44;
                Height = 44;
            }
            else
            {
                RestoreButton.Visibility = Visibility.Collapsed;
                ResizeGrip.Visibility = Visibility.Collapsed;
                if (IsVisible) Hide();
            }
            _collapsed = true;
            _widget.IsVisible = false;
        }
        finally { _suppressSave = false; }
        _service.SaveWidgets();
    }

    void RestoreButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ShowWidget();
    }

    // ── 交互 ──

    void DragSurface_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 1) return;
        if (_widget.IsLocked) return;
        if (IsInteractiveSource(e.OriginalSource as DependencyObject)) return;
        _snapDrag?.Start(e, () =>
        {
            _widget.X = Left;
            _widget.Y = Top;
            _service.SaveWidgets();
        });
    }

    static bool IsInteractiveSource(DependencyObject? source)
    {
        for (var d = source; d != null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is ButtonBase or TextBoxBase or Slider or ComboBox or ScrollBar or Thumb
                or PasswordBox or MenuItem)
                return true;
            if (d is FrameworkElement fe && fe.Name == "PART_ContentHost") return true;
        }
        return false;
    }

    void ResizeGrip_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_widget.IsLocked || _collapsed) return;
        Width = Math.Clamp(Width + e.HorizontalChange, _minWidth, _maxWidth);
        Height = Math.Clamp(Height + e.VerticalChange, _minHeight, _maxHeight);
    }

    // ── 右键菜单 ──

    void UpdateMenuLabels()
    {
        try
        {
            CtxLock.Header = _loc[_widget.IsLocked ? "Common.Unlock" : "Common.Lock"];
        }
        catch { }
    }

    void Lock_Click(object sender, RoutedEventArgs e)
    {
        _widget.IsLocked = !_widget.IsLocked;
        ResizeGrip.Visibility = _widget.IsLocked ? Visibility.Collapsed : Visibility.Visible;
        UpdateMenuLabels();
        _service.SaveWidgets();
    }

    /// <summary>打开宿主属性面板（与内置组件同一个面板，走浮窗 + 停靠两种形态）。</summary>
    void Settings_Click(object sender, RoutedEventArgs e)
    {
        try { PropertyWindowService.OpenOrFocus(_widget, this); }
        catch (Exception ex) { DzTrace.Always("[Plugin] widget settings: " + ex.Message); }
    }

    void Hide_Click(object sender, RoutedEventArgs e) => HideWidget();

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        var name = _service.FindDescriptor(_widget.PluginId, _widget.WidgetTypeId)?.DisplayName
                   ?? _widget.PluginId;
        if (MessageBox.Show(_loc.Get("Plugin.DeleteWidgetConfirm", name), _loc["Plugin.DeleteWidgetTitle"],
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        CloseWithoutSaving();
        _service.DeleteWidget(_widget);
    }

    /// <summary>
    /// 属性面板的实时预览：外观/几何都直接改在同一个 <see cref="PluginWidget"/> 实例上，
    /// 这里只负责把它们重新铺到窗口（不写盘；Apply 才落盘）。
    /// </summary>
    public void RefreshAppearance()
    {
        try
        {
            Opacity = Math.Clamp(_widget.Opacity, 0.2, 1.0);
            _suppressSave = true;
            try
            {
                if (!_collapsed)
                {
                    Width = Math.Clamp(_widget.Width, _minWidth, _maxWidth);
                    Height = Math.Clamp(_widget.Height, _minHeight, _maxHeight);
                }
            }
            finally { _suppressSave = false; }

            ApplyChrome();
            NativeMethods.SetRoundedCorners(this, _widget.CornerRadius);
            if (_widget.EnableAcrylic)
            {
                AcrylicHelper.EnableBlur(this, _widget.GlassBlurAmount, _widget.GlassTintOpacity,
                    _widget.GlassTintLuminosity, _widget.GlassColorMode);
            }
            else
            {
                AcrylicHelper.DisableBlur(this);
            }
            ResizeGrip.Visibility = _widget.IsLocked || _collapsed
                ? Visibility.Collapsed : Visibility.Visible;

            // 磁贴模式/锁定是插件能读到的运行时状态：面板一改就要同步进 context 并通知插件，
            // 否则插件永远看不到新值（context 只在创建时填一次）。
            if (_context != null)
            {
                var tileChanged = _context.IsTileMode != _widget.TileMode;
                var lockChanged = _context.IsLocked != _widget.IsLocked;
                _context.IsTileMode = _widget.TileMode;
                _context.IsLocked = _widget.IsLocked;
                if (tileChanged) _context.RaiseTileModeChanged();
                if (lockChanged) _context.RaiseLockedChanged();
            }
            UpdateMenuLabels();
        }
        catch (Exception ex)
        {
            DzTrace.Always("[Plugin] widget preview failed: " + ex.Message);
        }
    }

    public void CloseWithoutSaving()
    {
        _suppressSave = true;
        try { Close(); } catch { }
        finally { _suppressSave = false; }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_langChanged != null) _loc.LanguageChanged -= _langChanged;
        try { AcrylicHelper.DisableBlur(this); } catch { }
        base.OnClosed(e);
    }

    /// <summary>主题变化时由宿主调用（浅色/深色切换）。</summary>
    public void NotifyThemeChanged()
    {
        _context?.RaiseThemeChanged();
        ApplyChrome();
    }
}
