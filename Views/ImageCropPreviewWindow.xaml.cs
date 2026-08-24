using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopZones.Models;
using DesktopZones.Services;

namespace DesktopZones.Views;

public partial class ImageCropPreviewWindow : Window, INotifyPropertyChanged
{
    private readonly LocalizationService _loc = LocalizationService.Instance;
    
    // Input parameters
    private string _imagePath = "";
    private double _targetWidth;
    private double _targetHeight;
    private double _initialOffsetX;
    private double _initialOffsetY;
    private double _initialZoom;
    private double _initialOpacity;
    private string _cropShape = "Rectangle"; // Rectangle, Circle, Ellipse

    // Current state
    private double _currentOffsetX;
    private double _currentOffsetY;
    private double _currentZoom;
    private double _currentOpacity;
    
    // Drag state
    private bool _isDragging;
    private Point _dragStartPoint;
    private double _dragStartOffsetX;
    private double _dragStartOffsetY;
    
    // Preview scaling (to fit zone in preview area)
    private double _previewScale = 1.0;
    
    // Preview-space dimensions of the crop zone (used by overlay + grid after the
    // blue ZoneOutline border was removed).
    private double _zonePreviewWidth;
    private double _zonePreviewHeight;
    // Real widget title-bar height (DIP) — drives the title-bar/body divider line
    // and the drag snap in the preview. 0 = no title bar (clock/calendar).
    private double _titleBarHeight;
    const double TitleBarSnapThreshold = 8.0;

    // Borderless window corner resize — same WM_NCLBUTTONDOWN loop ZoneWindow uses.
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    const uint WM_NCLBUTTONDOWN = 0x00A1;
    const int HTTOPLEFT = 13, HTTOPRIGHT = 14, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
    
    // Result
    public CropPreviewResult? Result { get; private set; }
    
    // Properties for binding
    public double CurrentOffsetX
    {
        get => _currentOffsetX;
        set { _currentOffsetX = value; UpdateDisplays(); OnPropertyChanged(); }
    }
    
    public double CurrentOffsetY
    {
        get => _currentOffsetY;
        set { _currentOffsetY = value; UpdateDisplays(); OnPropertyChanged(); }
    }
    
    public double CurrentZoom
    {
        get => _currentZoom;
        set { _currentZoom = Math.Max(0.01, value); UpdateDisplays(); OnPropertyChanged(); }
    }
    
    public double CurrentOpacity
    {
        get => _currentOpacity;
        set { _currentOpacity = Math.Max(5, Math.Min(100, value)); UpdateDisplays(); OnPropertyChanged(); }
    }

    public ImageCropPreviewWindow(
        string imagePath,
        double targetWidth,
        double targetHeight,
        double initialOffsetX = 0,
        double initialOffsetY = 0,
        double initialZoom = 1.0,
        double initialOpacity = 40,
        string cropShape = "Rectangle",
        double titleBarHeight = 0)
    {
        InitializeComponent();

        _imagePath = imagePath;
        _targetWidth = targetWidth;
        _targetHeight = targetHeight;
        _initialOffsetX = initialOffsetX;
        _initialOffsetY = initialOffsetY;
        _initialZoom = initialZoom;
        _initialOpacity = initialOpacity;
        _cropShape = cropShape;
        _titleBarHeight = titleBarHeight;

        // Initialize current state
        _currentOffsetX = initialOffsetX;
        _currentOffsetY = initialOffsetY;
        _currentZoom = initialZoom;
        _currentOpacity = initialOpacity;

        DataContext = this;
        ApplyLoc();

        Loaded += OnLoaded;
    }
    
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        CalculatePreviewScale();
        UpdateZoneGeometry();
        LoadImage();
        UpdateImageTransform();
        UpdateOverlay();
        DrawGridLines();
        DrawTitleBarDivider();
        UpdateDisplays();
        
        // Set initial control values
        BgZoom.Value = _currentZoom;
        BgOpacity.Value = _currentOpacity;
    }
    
    private void LoadImage()
    {
        if (string.IsNullOrEmpty(_imagePath) || !File.Exists(_imagePath))
        {
            CropImage.Source = null;
            return;
        }
        
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.UriSource = new Uri(_imagePath);
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.DecodePixelWidth = 1920;
            bi.DecodePixelHeight = 1080;
            bi.EndInit();
            bi.Freeze();
            CropImage.Source = bi;
        }
        catch
        {
            CropImage.Source = null;
        }
    }
    
    private void CalculatePreviewScale()
    {
        // Calculate scale to fit zone in preview area
        double availableWidth = PreviewBorder.ActualWidth - 40; // margins
        double availableHeight = PreviewBorder.ActualHeight - 40;
        
        if (availableWidth <= 0 || availableHeight <= 0)
        {
            _previewScale = 1.0;
            return;
        }
        
        double scaleX = availableWidth / _targetWidth;
        double scaleY = availableHeight / _targetHeight;
        // ponytail: no 1.0 cap — the zone display always fits the current window
        // size proportionally (grows and shrinks with the preview area).
        _previewScale = Math.Min(scaleX, scaleY);
    }
    
    private void UpdateZoneGeometry()
    {
        // Compute the preview-space size of the crop zone. No visual outline is
        // drawn anymore — the dark CropOverlay already delineates the zone edge.
        if (_cropShape == "Circle")
        {
            double size = Math.Min(_targetWidth, _targetHeight) * _previewScale;
            _zonePreviewWidth = size;
            _zonePreviewHeight = size;
        }
        else
        {
            _zonePreviewWidth = _targetWidth * _previewScale;
            _zonePreviewHeight = _targetHeight * _previewScale;
        }
    }
    
    private void UpdateImageTransform()
    {
        if (CropImage.Source == null) return;
        
        var bitmapSource = CropImage.Source as BitmapSource;
        if (bitmapSource == null) return;
        
        double imageWidth = bitmapSource.PixelWidth;
        double imageHeight = bitmapSource.PixelHeight;
        
        // Always UniformToFill — fill target area maintaining aspect ratio
        double fillScale = Math.Max(
            (_targetWidth * _currentZoom) / imageWidth,
            (_targetHeight * _currentZoom) / imageHeight);
        double displayedWidth = imageWidth * fillScale;
        double displayedHeight = imageHeight * fillScale;

        // Apply to image (scaled for preview)
        CropImage.Width = displayedWidth * _previewScale;
        CropImage.Height = displayedHeight * _previewScale;

        // Apply offset (scaled for preview)
        double previewOffsetX = _currentOffsetX * _previewScale;
        double previewOffsetY = _currentOffsetY * _previewScale;

        // Center offset calculation - position relative to preview area center
        double previewCenterX = PreviewBorder.ActualWidth / 2;
        double previewCenterY = PreviewBorder.ActualHeight / 2;
        double zoneCenterX = previewCenterX;
        double zoneCenterY = previewCenterY;

        // Position image so its center aligns with zone center + offset
        double imageCenterX = CropImage.Width / 2;
        double imageCenterY = CropImage.Height / 2;

        Canvas.SetLeft(CropImage, zoneCenterX - imageCenterX + previewOffsetX);
        Canvas.SetTop(CropImage, zoneCenterY - imageCenterY + previewOffsetY);

        // Update canvas size
        ImageCanvas.Width = PreviewBorder.ActualWidth;
        ImageCanvas.Height = PreviewBorder.ActualHeight;

        // No clip on ImageCanvas — the overlay (CropOverlay) handles showing
        // what's outside the crop area via dark fill with a shaped hole.
        ImageCanvas.Clip = null;

        // Apply opacity
        CropImage.Opacity = _currentOpacity / 100.0;
    }
    
    private void PreviewBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isDragging = true;
        _dragStartPoint = e.GetPosition(PreviewBorder);
        _dragStartOffsetX = _currentOffsetX;
        _dragStartOffsetY = _currentOffsetY;
        PreviewBorder.CaptureMouse();
        PreviewBorder.Cursor = Cursors.Hand;
    }
    
    private void PreviewBorder_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDragging)
        {
            _isDragging = false;
            PreviewBorder.ReleaseMouseCapture();
            PreviewBorder.Cursor = Cursors.Hand;
        }
    }
    
    private void PreviewBorder_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging) return;
        
        Point currentPoint = e.GetPosition(PreviewBorder);
        double deltaX = currentPoint.X - _dragStartPoint.X;
        double deltaY = currentPoint.Y - _dragStartPoint.Y;
        
        // Convert preview delta to actual offset delta
        _currentOffsetX = _dragStartOffsetX + (deltaX / _previewScale);
        _currentOffsetY = _dragStartOffsetY + (deltaY / _previewScale);
        ApplyTitleBarSnap();
        
        UpdateImageTransform();
        UpdateDisplays();
    }
    
    private void PreviewBorder_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        double zoomDelta = e.Delta > 0 ? 0.1 : -0.1;
        CurrentZoom += zoomDelta;
        BgZoom.Value = _currentZoom;
        UpdateImageTransform();
    }
    
    private void ResizeGrip_Down(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement g) return;
        int d = g.Name switch
        {
            "GripTL" => HTTOPLEFT,
            "GripTR" => HTTOPRIGHT,
            "GripBL" => HTBOTTOMLEFT,
            _ => HTBOTTOMRIGHT,
        };
        SendMessage(new WindowInteropHelper(this).Handle, WM_NCLBUTTONDOWN, (IntPtr)d, IntPtr.Zero);
        e.Handled = true;
    }
    
    private void PreviewBorder_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsLoaded) return;
        
        CalculatePreviewScale();
        UpdateZoneGeometry();
        UpdateImageTransform();
        UpdateOverlay();
        DrawGridLines();
        DrawTitleBarDivider();
    }
    
    private void BgZoom_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        CurrentZoom = e.NewValue;
        UpdateImageTransform();
    }
    
    private void BgOpacity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        CurrentOpacity = e.NewValue;
        UpdateImageTransform();
    }
    
    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        CurrentOffsetX = _initialOffsetX;
        CurrentOffsetY = _initialOffsetY;
        CurrentZoom = _initialZoom;
        CurrentOpacity = _initialOpacity;

        BgZoom.Value = _currentZoom;
        BgOpacity.Value = _currentOpacity;
        CropImage.Stretch = Stretch.UniformToFill;
        UpdateImageTransform();
    }
    
    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Result = null;
        DialogResult = false;
        Close();
    }
    
    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        Result = new CropPreviewResult(_currentOffsetX, _currentOffsetY, _currentZoom, _currentOpacity);
        DialogResult = true;
        Close();
    }
    
    private void UpdateOverlay()
    {
        CropOverlay.Children.Clear();

        // Create semi-transparent overlay outside zone
        // This shows the user what will be cropped out

        double previewWidth = PreviewBorder.ActualWidth;
        double previewHeight = PreviewBorder.ActualHeight;

        // Update CropOverlay size to match preview area
        CropOverlay.Width = previewWidth;
        CropOverlay.Height = previewHeight;

        if (_cropShape == "Circle" || _cropShape == "Ellipse")
        {
            // For circle/ellipse, create a full-screen overlay with a hole
            double zoneWidth, zoneHeight;
            if (_cropShape == "Circle")
            {
                double size = Math.Min(_targetWidth, _targetHeight) * _previewScale;
                zoneWidth = size;
                zoneHeight = size;
            }
            else
            {
                zoneWidth = _targetWidth * _previewScale;
                zoneHeight = _targetHeight * _previewScale;
            }

            double zoneLeft = (previewWidth - zoneWidth) / 2;
            double zoneTop = (previewHeight - zoneHeight) / 2;

            // Create a path with a hole using CombinedGeometry
            var fullRect = new RectangleGeometry(new Rect(0, 0, previewWidth, previewHeight));
            var holeEllipse = new EllipseGeometry(
                new Point(zoneLeft + zoneWidth / 2, zoneTop + zoneHeight / 2),
                zoneWidth / 2,
                zoneHeight / 2);

            var combinedGeo = new CombinedGeometry(
                GeometryCombineMode.Exclude,
                fullRect,
                holeEllipse);

            var overlayPath = new System.Windows.Shapes.Path
            {
                Fill = new SolidColorBrush(Color.FromArgb(0x80, 0x00, 0x00, 0x00)),
                Data = combinedGeo
            };

            CropOverlay.Children.Add(overlayPath);
        }
        else
        {
            // Rectangle overlay
            double zoneLeft = (previewWidth - _zonePreviewWidth) / 2;
            double zoneTop = (previewHeight - _zonePreviewHeight) / 2;

            // Top overlay
            if (zoneTop > 0)
            {
                var topRect = new System.Windows.Shapes.Rectangle
                {
                    Width = previewWidth,
                    Height = zoneTop,
                    Fill = new SolidColorBrush(Color.FromArgb(0x80, 0x00, 0x00, 0x00))
                };
                Canvas.SetTop(topRect, 0);
                Canvas.SetLeft(topRect, 0);
                CropOverlay.Children.Add(topRect);
            }

            // Bottom overlay
            if (zoneTop + _zonePreviewHeight < previewHeight)
            {
                var bottomRect = new System.Windows.Shapes.Rectangle
                {
                    Width = previewWidth,
                    Height = previewHeight - (zoneTop + _zonePreviewHeight),
                    Fill = new SolidColorBrush(Color.FromArgb(0x80, 0x00, 0x00, 0x00))
                };
                Canvas.SetTop(bottomRect, zoneTop + _zonePreviewHeight);
                Canvas.SetLeft(bottomRect, 0);
                CropOverlay.Children.Add(bottomRect);
            }

            // Left overlay
            if (zoneLeft > 0)
            {
                var leftRect = new System.Windows.Shapes.Rectangle
                {
                    Width = zoneLeft,
                    Height = _zonePreviewHeight,
                    Fill = new SolidColorBrush(Color.FromArgb(0x80, 0x00, 0x00, 0x00))
                };
                Canvas.SetTop(leftRect, zoneTop);
                Canvas.SetLeft(leftRect, 0);
                CropOverlay.Children.Add(leftRect);
            }

            // Right overlay
            if (zoneLeft + _zonePreviewWidth < previewWidth)
            {
                var rightRect = new System.Windows.Shapes.Rectangle
                {
                    Width = previewWidth - (zoneLeft + _zonePreviewWidth),
                    Height = _zonePreviewHeight,
                    Fill = new SolidColorBrush(Color.FromArgb(0x80, 0x00, 0x00, 0x00))
                };
                Canvas.SetTop(rightRect, zoneTop);
                Canvas.SetLeft(rightRect, zoneLeft + _zonePreviewWidth);
                CropOverlay.Children.Add(rightRect);
            }
        }
    }
    
    private void DrawGridLines()
    {
        GridLinesCanvas.Children.Clear();

        // Draw rule-of-thirds grid lines using the management-UI secondary text
        // color so they stay visible in light/dark/high-contrast and in
        // System-accent mode (Border.* brushes get repainted at low alpha there).
        double lineWidth = 1;
        Brush brush;
        try { brush = (Brush)FindResource("Brush.Text.Secondary"); }
        catch { brush = Brushes.White; }

        double zoneWidth, zoneHeight, zoneLeft, zoneTop;

        if (_cropShape == "Circle")
        {
            double size = Math.Min(_targetWidth, _targetHeight) * _previewScale;
            zoneWidth = size;
            zoneHeight = size;
        }
        else
        {
            zoneWidth = _zonePreviewWidth;
            zoneHeight = _zonePreviewHeight;
        }

        zoneLeft = (PreviewBorder.ActualWidth - zoneWidth) / 2;
        zoneTop = (PreviewBorder.ActualHeight - zoneHeight) / 2;

        // For circle/ellipse, clip the grid lines
        if (_cropShape == "Circle" || _cropShape == "Ellipse")
        {
            var clipGeo = new EllipseGeometry(
                new Point(zoneLeft + zoneWidth / 2, zoneTop + zoneHeight / 2),
                zoneWidth / 2,
                zoneHeight / 2);
            GridLinesCanvas.Clip = clipGeo;
        }
        else
        {
            GridLinesCanvas.Clip = null;
        }

        // Vertical lines (1/3 and 2/3)
        for (int i = 1; i <= 2; i++)
        {
            double x = zoneWidth * i / 3;
            var line = new System.Windows.Shapes.Line
            {
                X1 = x, Y1 = 0,
                X2 = x, Y2 = zoneHeight,
                Stroke = brush,
                StrokeThickness = lineWidth
            };
            Canvas.SetLeft(line, zoneLeft);
            Canvas.SetTop(line, zoneTop);
            GridLinesCanvas.Children.Add(line);
        }

        // Horizontal lines (1/3 and 2/3)
        for (int i = 1; i <= 2; i++)
        {
            double y = zoneHeight * i / 3;
            var line = new System.Windows.Shapes.Line
            {
                X1 = 0, Y1 = y,
                X2 = zoneWidth, Y2 = y,
                Stroke = brush,
                StrokeThickness = lineWidth
            };
            Canvas.SetLeft(line, zoneLeft);
            Canvas.SetTop(line, zoneTop);
            GridLinesCanvas.Children.Add(line);
        }
    }
    
    private void DrawTitleBarDivider()
    {
        DividerCanvas.Children.Clear();
        if (_titleBarHeight <= 0) return;
        if (_cropShape == "Circle" || _cropShape == "Ellipse") return;

        // 真实窗口的标题栏/主体分界线：y = 裁切区域顶部 + 标题栏高度 × 预览缩放。
        // 预览界面不区分「标题栏独立填充」开关——这条线始终显示，只作裁剪参考。
        double zoneLeft = (PreviewBorder.ActualWidth - _zonePreviewWidth) / 2;
        double zoneTop = (PreviewBorder.ActualHeight - _zonePreviewHeight) / 2;
        double y = zoneTop + _titleBarHeight * _previewScale;

        Brush brush;
        try { brush = (Brush)FindResource("Brush.Text.Secondary"); }
        catch { brush = Brushes.White; }

        DividerCanvas.Children.Add(new System.Windows.Shapes.Line
        {
            X1 = zoneLeft, Y1 = y,
            X2 = zoneLeft + _zonePreviewWidth, Y2 = y,
            Stroke = brush,
            // 随预览缩放同步增粗（2–4px），放大后仍保持可见比例。
            StrokeThickness = Math.Min(4, Math.Max(2, 2 * _previewScale)),
            IsHitTestVisible = false
        });
    }

    void ApplyTitleBarSnap()
    {
        if (_titleBarHeight <= 0) return;
        if (_cropShape == "Circle" || _cropShape == "Ellipse") return;
        if (CropImage == null || CropImage.Source == null) return;

        // 图片上边缘靠近分界线时自动吸附；拖过阈值即可自由摆放（占满整个分区）。
        double zoneTop = (PreviewBorder.ActualHeight - _zonePreviewHeight) / 2;
        double dividerY = zoneTop + _titleBarHeight * _previewScale;
        double imageTop = PreviewBorder.ActualHeight / 2 - CropImage.Height / 2
                        + _currentOffsetY * _previewScale;
        double delta = dividerY - imageTop;
        if (Math.Abs(delta) < TitleBarSnapThreshold)
            _currentOffsetY += delta / _previewScale;
    }
    
    private void UpdateDisplays()
    {
        if (!IsLoaded) return;
        
        OffsetDisplay.Text = $"X: {_currentOffsetX:F0}  Y: {_currentOffsetY:F0}";
        ZoomDisplay.Text = $"Zoom: {_currentZoom:F1}x";
        ZoomValueText.Text = $"{_currentZoom:F1}x";
        OpacityValueText.Text = $"{_currentOpacity:F0}%";
    }
    
    private void ApplyLoc()
    {
        var cn = _loc.CurrentLanguage == "zh";
        DialogTitle.Text = _loc["CropPreview.Title"];
        ConfirmButton.Content = _loc["CropPreview.Confirm"];
        CancelButton.Content = _loc["CropPreview.Cancel"];
        ResetButton.Content = _loc["CropPreview.Reset"];
        LabelZoom.Text = cn ? "缩放:" : "Zoom:";
        LabelOpacity.Text = cn ? "透明度:" : "Opacity:";
    }
    
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}