using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
        string cropShape = "Rectangle")
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
        UpdateZoneOutline();
        LoadImage();
        UpdateImageTransform();
        UpdateOverlay();
        DrawGridLines();
        UpdateDisplays();
        
        // Set initial control values
        ZoomSlider.Value = _currentZoom;
        OpacitySlider.Value = _currentOpacity;
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
        _previewScale = Math.Min(scaleX, scaleY);
        
        // Cap at 1.0 to avoid upscaling
        _previewScale = Math.Min(_previewScale, 1.0);
    }
    
    private void UpdateZoneOutline()
    {
        // Set zone outline size based on crop shape
        if (_cropShape == "Circle")
        {
            // For circle, use the smaller dimension
            double size = Math.Min(_targetWidth, _targetHeight) * _previewScale;
            ZoneOutlineEllipse.Width = size;
            ZoneOutlineEllipse.Height = size;
            ZoneOutlineEllipse.Visibility = Visibility.Visible;
            ZoneOutline.Visibility = Visibility.Collapsed;
        }
        else
        {
            // For rectangle/ellipse
            ZoneOutline.Width = _targetWidth * _previewScale;
            ZoneOutline.Height = _targetHeight * _previewScale;
            ZoneOutline.Visibility = Visibility.Visible;
            ZoneOutlineEllipse.Visibility = Visibility.Collapsed;

            // For ellipse, set CornerRadius to make it rounded
            if (_cropShape == "Ellipse")
            {
                ZoneOutline.CornerRadius = new CornerRadius(ZoneOutline.Width / 2, ZoneOutline.Height / 2, ZoneOutline.Height / 2, ZoneOutline.Width / 2);
            }
            else
            {
                ZoneOutline.CornerRadius = new CornerRadius(8); // Default rounded corners
            }
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
        
        UpdateImageTransform();
        UpdateDisplays();
    }
    
    private void PreviewBorder_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        double zoomDelta = e.Delta > 0 ? 0.1 : -0.1;
        CurrentZoom += zoomDelta;
        ZoomSlider.Value = _currentZoom;
        UpdateImageTransform();
    }
    
    private void PreviewBorder_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsLoaded) return;
        
        CalculatePreviewScale();
        UpdateZoneOutline();
        UpdateImageTransform();
        UpdateOverlay();
        DrawGridLines();
    }
    
    private void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        CurrentZoom = e.NewValue;
        UpdateImageTransform();
    }
    
    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
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

        ZoomSlider.Value = _currentZoom;
        OpacitySlider.Value = _currentOpacity;
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
        Result = new CropPreviewResult
        {
            OffsetX = _currentOffsetX,
            OffsetY = _currentOffsetY,
            Zoom = _currentZoom,
            Opacity = _currentOpacity
        };
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
            double zoneLeft = (previewWidth - ZoneOutline.Width) / 2;
            double zoneTop = (previewHeight - ZoneOutline.Height) / 2;

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
            if (zoneTop + ZoneOutline.Height < previewHeight)
            {
                var bottomRect = new System.Windows.Shapes.Rectangle
                {
                    Width = previewWidth,
                    Height = previewHeight - (zoneTop + ZoneOutline.Height),
                    Fill = new SolidColorBrush(Color.FromArgb(0x80, 0x00, 0x00, 0x00))
                };
                Canvas.SetTop(bottomRect, zoneTop + ZoneOutline.Height);
                Canvas.SetLeft(bottomRect, 0);
                CropOverlay.Children.Add(bottomRect);
            }

            // Left overlay
            if (zoneLeft > 0)
            {
                var leftRect = new System.Windows.Shapes.Rectangle
                {
                    Width = zoneLeft,
                    Height = ZoneOutline.Height,
                    Fill = new SolidColorBrush(Color.FromArgb(0x80, 0x00, 0x00, 0x00))
                };
                Canvas.SetTop(leftRect, zoneTop);
                Canvas.SetLeft(leftRect, 0);
                CropOverlay.Children.Add(leftRect);
            }

            // Right overlay
            if (zoneLeft + ZoneOutline.Width < previewWidth)
            {
                var rightRect = new System.Windows.Shapes.Rectangle
                {
                    Width = previewWidth - (zoneLeft + ZoneOutline.Width),
                    Height = ZoneOutline.Height,
                    Fill = new SolidColorBrush(Color.FromArgb(0x80, 0x00, 0x00, 0x00))
                };
                Canvas.SetTop(rightRect, zoneTop);
                Canvas.SetLeft(rightRect, zoneLeft + ZoneOutline.Width);
                CropOverlay.Children.Add(rightRect);
            }
        }
    }
    
    private void DrawGridLines()
    {
        GridLinesCanvas.Children.Clear();

        // Draw rule-of-thirds grid lines
        double lineWidth = 1;
        var brush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));

        double zoneWidth, zoneHeight, zoneLeft, zoneTop;

        if (_cropShape == "Circle")
        {
            double size = Math.Min(_targetWidth, _targetHeight) * _previewScale;
            zoneWidth = size;
            zoneHeight = size;
        }
        else
        {
            zoneWidth = ZoneOutline.Width;
            zoneHeight = ZoneOutline.Height;
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
        var cn = _loc.CurrentLanguage == DesktopZones.Services.Language.Chinese;
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