# Image Crop Preview Window - Implementation Plan

## Overview

Replace the current non-visual image cropping controls in ZoneSettingsDialog and WidgetSettingsDialog with an interactive preview window that allows users to visually adjust image position, zoom, and cropping in real-time.

## Current State Analysis

### Existing Controls
- **ZoneSettingsDialog.xaml** (lines 285-325): Stretch ComboBox, Offset X/Y TextBoxes, Zoom Slider (0.5x-3.0x), Opacity Slider
- **WidgetSettingsDialog.xaml** (lines 191-281): Similar controls for analog/digital clock widgets
- **ZoneWindow.ApplyStyle()** (line 454-501): Applies transformations using margin offsets and width/height scaling

### Key Properties (Zone.cs)
- `BgImageStretch`: "Fill", "Uniform", "UniformToFill", "None"
- `BgImageOffsetX`, `BgImageOffsetY`: Position offsets in pixels
- `BgImageZoom`: Scale factor (0.5-3.0)
- `BackgroundImagePath`: Image file path
- `BackgroundImageOpacity`: 0-100%

### Transformation Logic (ZoneWindow.xaml.cs:498-500)
```csharp
BgImage.Width = bw * _zone.BgImageZoom;
BgImage.Height = bh * _zone.BgImageZoom;
BgImage.Margin = new Thickness(
    -zox - (bw * (_zone.BgImageZoom - 1) / 2),
    -zoy - (bh * (_zone.BgImageZoom - 1) / 2), 0, 0);
```

## Implementation Plan

### 1. New Files

#### 1.1 ImageCropPreviewWindow.xaml
**Location**: `D:\mimo\DesktopZones\Views\ImageCropPreviewWindow.xaml`

**Purpose**: Interactive preview dialog for image cropping

#### 1.2 ImageCropPreviewWindow.xaml.cs
**Location**: `D:\mimo\DesktopZones\Views\ImageCropPreviewWindow.xaml.cs`

**Purpose**: Code-behind with drag/zoom logic

#### 1.3 CropPreviewResult.cs (Optional)
**Location**: `D:\mimo\DesktopZones\Models\CropPreviewResult.cs`

**Purpose**: Strongly-typed result object for crop parameters

---

### 2. XAML Layout Design

```xml
<Window x:Class="DesktopZones.Views.ImageCropPreviewWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Image Crop Preview" Width="800" Height="600"
        MinWidth="600" MinHeight="450"
        WindowStartupLocation="CenterOwner" ResizeMode="CanResize"
        WindowStyle="None" AllowsTransparency="True" Background="Transparent">
    
    <Window.Resources>
        <!-- Reuse existing dark theme brushes from ZoneSettingsDialog -->
        <SolidColorBrush x:Key="Bg" Color="#E610111A"/>
        <SolidColorBrush x:Key="Acc" Color="#7C3AED"/>
        <SolidColorBrush x:Key="T1" Color="#E8E8F0"/>
        <SolidColorBrush x:Key="T2" Color="#8888A0"/>
        <SolidColorBrush x:Key="IBg" Color="#0AFFFFFF"/>
        <SolidColorBrush x:Key="IBd" Color="#15FFFFFF"/>
    </Window.Resources>

    <Border Background="{StaticResource Bg}" CornerRadius="12" 
            BorderBrush="#15FFFFFF" BorderThickness="1">
        <Border.Effect>
            <DropShadowEffect BlurRadius="24" ShadowDepth="0" Color="#AA000000" Opacity="0.6"/>
        </Border.Effect>
        
        <Grid Margin="18">
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto"/>  <!-- Title bar -->
                <RowDefinition Height="*"/>     <!-- Preview area -->
                <RowDefinition Height="Auto"/>  <!-- Controls -->
                <RowDefinition Height="Auto"/>  <!-- Buttons -->
            </Grid.RowDefinitions>

            <!-- 1. Title Bar -->
            <Grid Grid.Row="0" Margin="0,0,0,12">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="*"/>
                    <ColumnDefinition Width="Auto"/>
                </Grid.ColumnDefinitions>
                
                <TextBlock x:Name="DialogTitle" Text="Image Crop Preview" 
                           FontSize="16" FontWeight="SemiBold" Foreground="{StaticResource T1}"/>
                
                <!-- Current values display -->
                <StackPanel Grid.Column="1" Orientation="Horizontal">
                    <TextBlock x:Name="OffsetDisplay" Foreground="{StaticResource T2}" 
                               FontSize="12" VerticalAlignment="Center"/>
                    <TextBlock Text=" | " Foreground="{StaticResource T3}" Margin="4,0"/>
                    <TextBlock x:Name="ZoomDisplay" Foreground="{StaticResource T2}" 
                               FontSize="12" VerticalAlignment="Center"/>
                </StackPanel>
            </Grid>

            <!-- 2. Preview Area -->
            <Border Grid.Row="1" Background="#08FFFFFF" CornerRadius="8" 
                    BorderBrush="{StaticResource IBd}" BorderThickness="1"
                    ClipToBounds="True" x:Name="PreviewBorder"
                    MouseWheel="PreviewBorder_MouseWheel"
                    MouseLeftButtonDown="PreviewBorder_MouseLeftButtonDown"
                    MouseLeftButtonUp="PreviewBorder_MouseLeftButtonUp"
                    MouseMove="PreviewBorder_MouseMove"
                    MouseRightButtonDown="PreviewBorder_MouseRightButtonDown">
                
                <Grid x:Name="PreviewGrid">
                    <!-- Zone outline (dashed border showing target dimensions) -->
                    <Border x:Name="ZoneOutline" BorderBrush="#7C3AED" 
                            BorderThickness="2" BorderDashArray="4,2"
                            HorizontalAlignment="Center" VerticalAlignment="Center"
                            Background="Transparent" IsHitTestVisible="False"/>
                    
                    <!-- Grid lines for alignment (optional) -->
                    <Canvas x:Name="GridLinesCanvas" IsHitTestVisible="False" Opacity="0.3"/>
                    
                    <!-- Background image -->
                    <Image x:Name="CropImage" Stretch="Uniform" 
                           HorizontalAlignment="Left" VerticalAlignment="Top"
                           RenderTransformOrigin="0.5,0.5">
                        <Image.RenderTransform>
                            <TransformGroup>
                                <ScaleTransform x:Name="ImageScale"/>
                                <TranslateTransform x:Name="ImageTranslate"/>
                            </TransformGroup>
                        </Image.RenderTransform>
                    </Image>
                    
                    <!-- Crop area overlay (semi-transparent dark outside zone) -->
                    <Canvas x:Name="CropOverlay" IsHitTestVisible="False"/>
                </Grid>
            </Border>

            <!-- 3. Controls Panel -->
            <Grid Grid.Row="2" Margin="0,12,0,0">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="Auto"/>
                    <ColumnDefinition Width="*"/>
                    <ColumnDefinition Width="Auto"/>
                    <ColumnDefinition Width="120"/>
                </Grid.ColumnDefinitions>

                <!-- Stretch mode -->
                <StackPanel Grid.Column="0" VerticalAlignment="Center">
                    <TextBlock Text="Stretch:" Foreground="{StaticResource T2}" FontSize="11"/>
                    <ComboBox x:Name="StretchCombo" Width="120" Margin="0,4,0,0"
                              SelectionChanged="StretchCombo_SelectionChanged">
                        <ComboBoxItem Content="Fill" Tag="Fill"/>
                        <ComboBoxItem Content="Uniform" Tag="Uniform"/>
                        <ComboBoxItem Content="UniformToFill" Tag="UniformToFill" IsSelected="True"/>
                        <ComboBoxItem Content="None" Tag="None"/>
                    </ComboBox>
                </StackPanel>

                <!-- Zoom slider -->
                <StackPanel Grid.Column="2" VerticalAlignment="Center" Margin="12,0,0,0">
                    <TextBlock Text="Zoom:" Foreground="{StaticResource T2}" FontSize="11"/>
                    <StackPanel Orientation="Horizontal" Margin="0,4,0,0">
                        <Slider x:Name="ZoomSlider" Width="150" Minimum="0.5" Maximum="3.0" 
                                TickFrequency="0.1" Value="1.0"
                                ValueChanged="ZoomSlider_ValueChanged"/>
                        <TextBlock x:Name="ZoomValueText" Text="1.0x" 
                                   Foreground="{StaticResource T1}" FontSize="12" 
                                   Width="40" TextAlignment="Right" VerticalAlignment="Center"/>
                    </StackPanel>
                </StackPanel>

                <!-- Opacity slider -->
                <StackPanel Grid.Column="3" VerticalAlignment="Center">
                    <TextBlock Text="Opacity:" Foreground="{StaticResource T2}" FontSize="11"/>
                    <StackPanel Orientation="Horizontal" Margin="0,4,0,0">
                        <Slider x:Name="OpacitySlider" Width="80" Minimum="5" Maximum="100" 
                                TickFrequency="5" Value="40"
                                ValueChanged="OpacitySlider_ValueChanged"/>
                        <TextBlock x:Name="OpacityValueText" Text="40%" 
                                   Foreground="{StaticResource T1}" FontSize="12" 
                                   Width="36" TextAlignment="Right" VerticalAlignment="Center"/>
                    </StackPanel>
                </StackPanel>
            </Grid>

            <!-- 4. Buttons -->
            <StackPanel Grid.Row="3" Orientation="Horizontal" 
                        HorizontalAlignment="Right" Margin="0,12,0,0">
                <Button x:Name="ResetButton" Content="Reset" 
                        Background="#15FFFFFF" Foreground="{StaticResource T2}" 
                        BorderBrush="{StaticResource IBd}" BorderThickness="1" 
                        Padding="16,8" Cursor="Hand" FontSize="12"
                        Click="ResetButton_Click"/>
                <Button x:Name="CancelButton" Content="Cancel" 
                        Background="#15FFFFFF" Foreground="{StaticResource T2}" 
                        BorderBrush="{StaticResource IBd}" BorderThickness="1" 
                        Padding="20,8" Margin="8,0,0,0" Cursor="Hand" FontSize="12"
                        Click="CancelButton_Click"/>
                <Button x:Name="ConfirmButton" Content="Confirm" 
                        Background="{StaticResource Acc}" Foreground="White" 
                        BorderThickness="0" Padding="20,8" Margin="8,0,0,0" 
                        Cursor="Hand" FontSize="12" FontWeight="SemiBold"
                        Click="ConfirmButton_Click"/>
            </StackPanel>
        </Grid>
    </Border>
</Window>
```

---

### 3. Code-Behind Logic

#### 3.1 Constructor and Properties

```csharp
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
    private string _initialStretch;
    private double _initialOpacity;
    
    // Current state
    private double _currentOffsetX;
    private double _currentOffsetY;
    private double _currentZoom;
    private string _currentStretch;
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
        set { _currentZoom = Math.Max(0.5, Math.Min(3.0, value)); UpdateDisplays(); OnPropertyChanged(); }
    }
    
    public string CurrentStretch
    {
        get => _currentStretch;
        set { _currentStretch = value; OnPropertyChanged(); }
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
        string initialStretch = "UniformToFill",
        double initialOpacity = 40)
    {
        InitializeComponent();
        
        _imagePath = imagePath;
        _targetWidth = targetWidth;
        _targetHeight = targetHeight;
        _initialOffsetX = initialOffsetX;
        _initialOffsetY = initialOffsetY;
        _initialZoom = initialZoom;
        _initialStretch = initialStretch;
        _initialOpacity = initialOpacity;
        
        // Initialize current state
        _currentOffsetX = initialOffsetX;
        _currentOffsetY = initialOffsetY;
        _currentZoom = initialZoom;
        _currentStretch = initialStretch;
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
        UpdateDisplays();
    }
```

#### 3.2 Image Loading and Display

```csharp
    private void LoadImage()
    {
        if (string.IsNullOrEmpty(_imagePath) || !System.IO.File.Exists(_imagePath))
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
            bi.EndInit();
            CropImage.Source = bi;
            
            // Set initial stretch mode
            StretchCombo.SelectedItem = StretchCombo.Items
                .Cast<ComboBoxItem>()
                .FirstOrDefault(i => i.Tag?.ToString() == _currentStretch);
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
        // Set zone outline size
        ZoneOutline.Width = _targetWidth * _previewScale;
        ZoneOutline.Height = _targetHeight * _previewScale;
    }
```

#### 3.3 Image Transform Updates

```csharp
    private void UpdateImageTransform()
    {
        if (CropImage.Source == null) return;
        
        var bitmapSource = CropImage.Source as BitmapSource;
        if (bitmapSource == null) return;
        
        double imageWidth = bitmapSource.PixelWidth;
        double imageHeight = bitmapSource.PixelHeight;
        
        // Calculate displayed size based on stretch mode
        double displayedWidth, displayedHeight;
        
        switch (_currentStretch)
        {
            case "Fill":
                // Stretch to fill, maintaining aspect ratio
                double fillScale = Math.Max(
                    (_targetWidth * _currentZoom) / imageWidth,
                    (_targetHeight * _currentZoom) / imageHeight);
                displayedWidth = imageWidth * fillScale;
                displayedHeight = imageHeight * fillScale;
                break;
                
            case "Uniform":
                // Fit inside, maintaining aspect ratio
                double uniformScale = Math.Min(
                    (_targetWidth * _currentZoom) / imageWidth,
                    (_targetHeight * _currentZoom) / imageHeight);
                displayedWidth = imageWidth * uniformScale;
                displayedHeight = imageHeight * uniformScale;
                break;
                
            case "UniformToFill":
                // Fill area, maintaining aspect ratio (may crop)
                double uniformToFillScale = Math.Max(
                    (_targetWidth * _currentZoom) / imageWidth,
                    (_targetHeight * _currentZoom) / imageHeight);
                displayedWidth = imageWidth * uniformToFillScale;
                displayedHeight = imageHeight * uniformToFillScale;
                break;
                
            case "None":
            default:
                // Original size with zoom
                displayedWidth = imageWidth * _currentZoom;
                displayedHeight = imageHeight * _currentZoom;
                break;
        }
        
        // Apply to image (scaled for preview)
        CropImage.Width = displayedWidth * _previewScale;
        CropImage.Height = displayedHeight * _previewScale;
        
        // Apply offset (scaled for preview)
        double previewOffsetX = _currentOffsetX * _previewScale;
        double previewOffsetY = _currentOffsetY * _previewScale;
        
        // Center offset calculation
        double centerX = (ZoneOutline.Width - CropImage.Width) / 2;
        double centerY = (ZoneOutline.Height - CropImage.Height) / 2;
        
        Canvas.SetLeft(CropImage, centerX + previewOffsetX);
        Canvas.SetTop(CropImage, centerY + previewOffsetY);
        
        // Apply opacity
        CropImage.Opacity = _currentOpacity / 100.0;
    }
```

#### 3.4 Drag Handling

```csharp
    private void PreviewBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isDragging = true;
        _dragStartPoint = e.GetPosition(PreviewBorder);
        _dragStartOffsetX = _currentOffsetX;
        _dragStartOffsetY = _currentOffsetY;
        PreviewBorder.CaptureMouse();
        PreviewBorder.Cursor = Cursors.ClosedHand;
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
    
    private void PreviewBorder_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Optional: Right-click to reset position
        // Or use for future context menu
    }
```

#### 3.5 Zoom Handling

```csharp
    private void PreviewBorder_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        double zoomDelta = e.Delta > 0 ? 0.1 : -0.1;
        CurrentZoom += zoomDelta;
        ZoomSlider.Value = _currentZoom;
        UpdateImageTransform();
    }
    
    private void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        CurrentZoom = e.NewValue;
        UpdateImageTransform();
    }
```

#### 3.6 Control Event Handlers

```csharp
    private void StretchCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StretchCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            CurrentStretch = tag;
            UpdateImageTransform();
        }
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
        CurrentStretch = _initialStretch;
        CurrentOpacity = _initialOpacity;
        
        ZoomSlider.Value = _currentZoom;
        OpacitySlider.Value = _currentOpacity;
        StretchCombo.SelectedItem = StretchCombo.Items
            .Cast<ComboBoxItem>()
            .FirstOrDefault(i => i.Tag?.ToString() == _currentStretch);
        
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
            Stretch = _currentStretch,
            Opacity = _currentOpacity
        };
        DialogResult = true;
        Close();
    }
```

#### 3.7 Overlay and Grid Lines

```csharp
    private void UpdateOverlay()
    {
        CropOverlay.Children.Clear();
        
        // Create semi-transparent overlay outside zone
        // This shows the user what will be cropped out
        
        double previewWidth = PreviewBorder.ActualWidth;
        double previewHeight = PreviewBorder.ActualHeight;
        
        // Top overlay
        var topRect = new System.Windows.Shapes.Rectangle
        {
            Width = previewWidth,
            Height = (previewHeight - ZoneOutline.Height) / 2,
            Fill = new SolidColorBrush(Color.FromArgb(0x80, 0x00, 0x00, 0x00))
        };
        Canvas.SetTop(topRect, 0);
        Canvas.SetLeft(topRect, 0);
        CropOverlay.Children.Add(topRect);
        
        // Bottom overlay
        var bottomRect = new System.Windows.Shapes.Rectangle
        {
            Width = previewWidth,
            Height = (previewHeight - ZoneOutline.Height) / 2,
            Fill = new SolidColorBrush(Color.FromArgb(0x80, 0x00, 0x00, 0x00))
        };
        Canvas.SetTop(bottomRect, (previewHeight + ZoneOutline.Height) / 2);
        Canvas.SetLeft(bottomRect, 0);
        CropOverlay.Children.Add(bottomRect);
        
        // Left overlay
        var leftRect = new System.Windows.Shapes.Rectangle
        {
            Width = (previewWidth - ZoneOutline.Width) / 2,
            Height = ZoneOutline.Height,
            Fill = new SolidColorBrush(Color.FromArgb(0x80, 0x00, 0x00, 0x00))
        };
        Canvas.SetTop(leftRect, (previewHeight - ZoneOutline.Height) / 2);
        Canvas.SetLeft(leftRect, 0);
        CropOverlay.Children.Add(leftRect);
        
        // Right overlay
        var rightRect = new System.Windows.Shapes.Rectangle
        {
            Width = (previewWidth - ZoneOutline.Width) / 2,
            Height = ZoneOutline.Height,
            Fill = new SolidColorBrush(Color.FromArgb(0x80, 0x00, 0x00, 0x00))
        };
        Canvas.SetTop(rightRect, (previewHeight - ZoneOutline.Height) / 2);
        Canvas.SetLeft(rightRect, (previewWidth + ZoneOutline.Width) / 2);
        CropOverlay.Children.Add(rightRect);
    }
    
    private void DrawGridLines()
    {
        GridLinesCanvas.Children.Clear();
        
        // Draw rule-of-thirds grid lines
        double lineWidth = 1;
        var brush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
        
        // Vertical lines (1/3 and 2/3)
        for (int i = 1; i <= 2; i++)
        {
            double x = ZoneOutline.Width * i / 3;
            var line = new System.Windows.Shapes.Line
            {
                X1 = x, Y1 = 0,
                X2 = x, Y2 = ZoneOutline.Height,
                Stroke = brush,
                StrokeThickness = lineWidth
            };
            Canvas.SetLeft(line, (PreviewBorder.ActualWidth - ZoneOutline.Width) / 2);
            Canvas.SetTop(line, (PreviewBorder.ActualHeight - ZoneOutline.Height) / 2);
            GridLinesCanvas.Children.Add(line);
        }
        
        // Horizontal lines (1/3 and 2/3)
        for (int i = 1; i <= 2; i++)
        {
            double y = ZoneOutline.Height * i / 3;
            var line = new System.Windows.Shapes.Line
            {
                X1 = 0, Y1 = y,
                X2 = ZoneOutline.Width, Y2 = y,
                Stroke = brush,
                StrokeThickness = lineWidth
            };
            Canvas.SetLeft(line, (PreviewBorder.ActualWidth - ZoneOutline.Width) / 2);
            Canvas.SetTop(line, (PreviewBorder.ActualHeight - ZoneOutline.Height) / 2);
            GridLinesCanvas.Children.Add(line);
        }
    }
```

#### 3.8 Display Updates and Localization

```csharp
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
        var cn = _loc.CurrentLanguage == Language.Chinese;
        DialogTitle.Text = cn ? "图片裁剪预览" : "Image Crop Preview";
        ConfirmButton.Content = cn ? "确认" : "Confirm";
        CancelButton.Content = cn ? "取消" : "Cancel";
        ResetButton.Content = cn ? "重置" : "Reset";
    }
    
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
```

---

### 4. Data Model

#### 4.1 CropPreviewResult.cs

```csharp
namespace DesktopZones.Models;

public class CropPreviewResult
{
    public double OffsetX { get; set; }
    public double OffsetY { get; set; }
    public double Zoom { get; set; }
    public string Stretch { get; set; } = "UniformToFill";
    public double Opacity { get; set; } = 40;
}
```

---

### 5. Integration Changes

#### 5.1 ZoneSettingsDialog.xaml Modifications

**Add "Crop" button next to background image path:**

```xml
<!-- Background image (around line 285) -->
<Grid Margin="0,4,0,0">
    <Grid.ColumnDefinitions>
        <ColumnDefinition Width="*"/>
        <ColumnDefinition Width="Auto"/>
        <ColumnDefinition Width="Auto"/>
    </Grid.ColumnDefinitions>
    
    <TextBox x:Name="BgImagePathBox" Style="{StaticResource DTb}" 
             Text="{Binding BgImagePath, UpdateSourceTrigger=PropertyChanged}" IsReadOnly="True"/>
    
    <Button Grid.Column="1" x:Name="CropBtn" Content="✂" Width="28" 
            Background="{StaticResource Acc}" Foreground="White" BorderThickness="0" 
            Margin="4,0,0,0" Cursor="Hand" FontSize="12"
            Click="CropBgImage_Click" IsEnabled="False">
        <Button.Resources>
            <Style TargetType="Border"><Setter Property="CornerRadius" Value="4"/></Style>
        </Button.Resources>
    </Button>
    
    <Button Grid.Column="2" x:Name="BrowseBgBtn" Content="..." Width="32" 
            Background="{StaticResource Acc}" Foreground="White" BorderThickness="0" 
            Margin="4,0,0,0" Cursor="Hand" FontSize="12" Click="BrowseBgImage_Click">
        <Button.Resources>
            <Style TargetType="Border"><Setter Property="CornerRadius" Value="4"/></Style>
        </Button.Resources>
    </Button>
</Grid>
```

#### 5.2 ZoneSettingsDialog.xaml.cs Modifications

**Add Crop button click handler:**

```csharp
// Add after BrowseBgImage_Click method (around line 277)
void CropBgImage_Click(object sender, RoutedEventArgs e)
{
    if (string.IsNullOrEmpty(BgImagePath) || !File.Exists(BgImagePath))
        return;
    
    double.TryParse(ZoneWidth, out double targetWidth);
    double.TryParse(ZoneHeight, out double targetHeight);
    
    var cropWindow = new ImageCropPreviewWindow(
        imagePath: BgImagePath,
        targetWidth: targetWidth,
        targetHeight: targetHeight,
        initialOffsetX: _bgOffsetX,
        initialOffsetY: _bgOffsetY,
        initialZoom: _bgZoomVal,
        initialStretch: BgStretchValue,
        initialOpacity: BgImageOpacityPercent)
    {
        Owner = this
    };
    
    if (cropWindow.ShowDialog() == true && cropWindow.Result != null)
    {
        _bgOffsetX = cropWindow.Result.OffsetX;
        _bgOffsetY = cropWindow.Result.OffsetY;
        _bgZoomVal = cropWindow.Result.Zoom;
        BgStretchValue = cropWindow.Result.Stretch;
        BgImageOpacityPercent = cropWindow.Result.Opacity;
        
        // Update UI controls
        OffsetXBox.Text = _bgOffsetX.ToString("F0");
        OffsetYBox.Text = _bgOffsetY.ToString("F0");
        ZoomSlider.Value = _bgZoomVal;
        OpacitySlider.Value = _bgImageOpacity;
        BgStretchCombo.SelectedIndex = BgStretchValue switch
        {
            "Fill" => 0,
            "Uniform" => 1,
            "UniformToFill" => 2,
            _ => 3
        };
    }
}
```

**Enable Crop button when image is loaded:**

```csharp
// Modify BgImagePath property setter (around line 50)
private string _bgImagePath = "";
public string BgImagePath
{
    get => _bgImagePath;
    set
    {
        _bgImagePath = value;
        if (!string.IsNullOrEmpty(value)) _fillColor = "#00000000";
        
        // Enable/disable crop button
        if (CropBtn != null)
            CropBtn.IsEnabled = !string.IsNullOrEmpty(value) && File.Exists(value);
        
        OnPropertyChanged();
    }
}
```

#### 5.3 WidgetSettingsDialog.xaml Modifications

**Add Crop button to analog clock section (around line 197):**

```xml
<Grid Margin="0,4,0,0">
    <Grid.ColumnDefinitions>
        <ColumnDefinition Width="*"/>
        <ColumnDefinition Width="Auto"/>
        <ColumnDefinition Width="Auto"/>
    </Grid.ColumnDefinitions>
    
    <TextBox x:Name="BgImagePathBox" Style="{StaticResource DTb}" IsReadOnly="True"/>
    
    <Button Grid.Column="1" x:Name="CropBtn" Content="✂" Width="28" 
            Background="{StaticResource Acc}" Foreground="White" BorderThickness="0" 
            Margin="4,0,0,0" Cursor="Hand" FontSize="12"
            Click="CropBgImage_Click" IsEnabled="False">
        <Button.Resources>
            <Style TargetType="Border"><Setter Property="CornerRadius" Value="4"/></Style>
        </Button.Resources>
    </Button>
    
    <Button Grid.Column="2" x:Name="BrowseBgBtn" Content="..." Width="32" ... />
</Grid>
```

**Add Crop button to digital clock section (around line 243):**

```xml
<Grid Margin="0,4,0,0">
    <Grid.ColumnDefinitions>
        <ColumnDefinition Width="*"/>
        <ColumnDefinition Width="Auto"/>
        <ColumnDefinition Width="Auto"/>
    </Grid.ColumnDefinitions>
    
    <TextBox x:Name="DigitalBgImagePathBox" Style="{StaticResource DTb}" IsReadOnly="True"/>
    
    <Button Grid.Column="1" x:Name="DigitalCropBtn" Content="✂" Width="28" 
            Background="{StaticResource Acc}" Foreground="White" BorderThickness="0" 
            Margin="4,0,0,0" Cursor="Hand" FontSize="12"
            Click="DigitalCropBgImage_Click" IsEnabled="False">
        <Button.Resources>
            <Style TargetType="Border"><Setter Property="CornerRadius" Value="4"/></Style>
        </Button.Resources>
    </Button>
    
    <Button Grid.Column="2" x:Name="DigitalBrowseBgBtn" Content="..." Width="32" ... />
</Grid>
```

#### 5.4 WidgetSettingsDialog.xaml.cs Modifications

**Add crop handlers for both analog and digital sections:**

```csharp
// Add after BrowseBgImage_Click method
void CropBgImage_Click(object sender, RoutedEventArgs e)
{
    if (string.IsNullOrEmpty(BgImagePathBox.Text) || !File.Exists(BgImagePathBox.Text))
        return;
    
    // Get widget dimensions (you'll need to pass these from the widget)
    double targetWidth = _currentWidget?.Width ?? 200;
    double targetHeight = _currentWidget?.Height ?? 200;
    
    // Get current values from sliders/textboxes
    double.TryParse(OffsetXBox.Text, out double offsetX);
    double.TryParse(OffsetYBox.Text, out double offsetY);
    
    var cropWindow = new ImageCropPreviewWindow(
        imagePath: BgImagePathBox.Text,
        targetWidth: targetWidth,
        targetHeight: targetHeight,
        initialOffsetX: offsetX,
        initialOffsetY: offsetY,
        initialZoom: ZoomSlider.Value,
        initialStretch: GetSelectedStretch(BgStretchCombo),
        initialOpacity: BgOpacitySlider.Value)
    {
        Owner = this
    };
    
    if (cropWindow.ShowDialog() == true && cropWindow.Result != null)
    {
        OffsetXBox.Text = cropWindow.Result.OffsetX.ToString("F0");
        OffsetYBox.Text = cropWindow.Result.OffsetY.ToString("F0");
        ZoomSlider.Value = cropWindow.Result.Zoom;
        BgOpacitySlider.Value = cropWindow.Result.Opacity;
        SetStretchSelection(BgStretchCombo, cropWindow.Result.Stretch);
    }
}

void DigitalCropBgImage_Click(object sender, RoutedEventArgs e)
{
    if (string.IsNullOrEmpty(DigitalBgImagePathBox.Text) || !File.Exists(DigitalBgImagePathBox.Text))
        return;
    
    // Similar implementation for digital clock
    // ...
}

// Helper methods
string GetSelectedStretch(ComboBox combo)
{
    if (combo.SelectedItem is ComboBoxItem item)
        return item.Tag?.ToString() ?? "UniformToFill";
    return "UniformToFill";
}

void SetStretchSelection(ComboBox combo, string stretch)
{
    combo.SelectedItem = combo.Items
        .Cast<ComboBoxItem>()
        .FirstOrDefault(i => i.Tag?.ToString() == stretch);
}
```

---

### 6. Data Flow Diagram

```
┌─────────────────────────────────────────────────────────────────┐
│                     User Flow                                    │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│  ZoneSettingsDialog / WidgetSettingsDialog                      │
│  ┌──────────────────────────────────────────────────────────┐   │
│  │  [Browse] button → Select image file                     │   │
│  │  [Crop ✂] button → Opens preview window                 │   │
│  └──────────────────────────────────────────────────────────┘   │
│                              │                                   │
│         ShowDialog(imagePath, targetWidth, targetHeight,        │
│                    initialOffsetX, initialOffsetY,              │
│                    initialZoom, initialStretch, initialOpacity)  │
│                              │                                   │
└──────────────────────────────┼──────────────────────────────────┘
                               │
                               ▼
┌─────────────────────────────────────────────────────────────────┐
│  ImageCropPreviewWindow                                         │
│  ┌──────────────────────────────────────────────────────────┐   │
│  │  ┌────────────────────────────────────────────────────┐  │   │
│  │  │  Preview Area                                       │  │   │
│  │  │  ┌──────────────────────────────────────────────┐  │  │   │
│  │  │  │  Zone Outline (dashed border)                 │  │  │   │
│  │  │  │  ┌────────────────────────────────────────┐  │  │  │   │
│  │  │  │  │  Background Image                      │  │  │  │   │
│  │  │  │  │  (draggable, zoomable)                 │  │  │  │   │
│  │  │  │  └────────────────────────────────────────┘  │  │  │   │
│  │  │  │  Grid Lines (rule of thirds)                 │  │  │   │
│  │  │  └──────────────────────────────────────────────┘  │  │   │
│  │  │  Crop Overlay (semi-transparent outside zone)      │  │   │
│  │  └────────────────────────────────────────────────────┘  │   │
│  │                                                          │   │
│  │  Controls:                                               │   │
│  │  [Stretch ▼]  [Zoom ════════ 1.0x]  [Opacity ════ 40%]  │   │
│  │                                                          │   │
│  │  [Reset]  [Cancel]  [Confirm]                            │   │
│  └──────────────────────────────────────────────────────────┘   │
│                              │                                   │
│         DialogResult = true                                     │
│         Result = CropPreviewResult { OffsetX, OffsetY,          │
│                                      Zoom, Stretch, Opacity }   │
│                              │                                   │
└──────────────────────────────┼──────────────────────────────────┘
                               │
                               ▼
┌─────────────────────────────────────────────────────────────────┐
│  Return to Caller Dialog                                        │
│  ┌──────────────────────────────────────────────────────────┐   │
│  │  Update local state:                                      │   │
│  │    _bgOffsetX = result.OffsetX                           │   │
│  │    _bgOffsetY = result.OffsetY                           │   │
│  │    _bgZoomVal = result.Zoom                              │   │
│  │    BgStretchValue = result.Stretch                       │   │
│  │    BgImageOpacityPercent = result.Opacity                │   │
│  │                                                          │   │
│  │  Update UI controls:                                      │   │
│  │    OffsetXBox.Text = ...                                 │   │
│  │    OffsetYBox.Text = ...                                 │   │
│  │    ZoomSlider.Value = ...                                │   │
│  │    OpacitySlider.Value = ...                             │   │
│  │    BgStretchCombo.SelectedIndex = ...                    │   │
│  └──────────────────────────────────────────────────────────┘   │
│                              │                                   │
│         User clicks [Apply]                                     │
│                              │                                   │
└──────────────────────────────┼──────────────────────────────────┘
                               │
                               ▼
┌─────────────────────────────────────────────────────────────────┐
│  ZoneManager.UpdateZone(zone)                                   │
│                              │                                   │
└──────────────────────────────┼──────────────────────────────────┘
                               │
                               ▼
┌─────────────────────────────────────────────────────────────────┐
│  ZoneWindow.ApplyStyle()                                        │
│  ┌──────────────────────────────────────────────────────────┐   │
│  │  Apply transformations:                                   │   │
│  │    BgImage.Width = bw * zone.BgImageZoom                 │   │
│  │    BgImage.Height = bh * zone.BgImageZoom                │   │
│  │    BgImage.Margin = Thickness(                            │   │
│  │      -offsetX - (bw * (zoom - 1) / 2),                  │   │
│  │      -offsetY - (bh * (zoom - 1) / 2), 0, 0)            │   │
│  │    BgImage.Opacity = zone.BackgroundImageOpacity / 100   │   │
│  └──────────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────────┘
```

---

### 7. Edge Cases and Solutions

#### 7.1 Image Smaller Than Crop Area

**Problem**: When the image is smaller than the zone dimensions, the preview may look odd.

**Solution**: 
```csharp
private void UpdateImageTransform()
{
    // ... existing code ...
    
    // Ensure minimum display size
    displayedWidth = Math.Max(displayedWidth, imageWidth * 0.1);
    displayedHeight = Math.Max(displayedHeight, imageHeight * 0.1);
    
    // Show checkered pattern background for empty areas
    if (displayedWidth < _targetWidth || displayedHeight < _targetHeight)
    {
        ShowCheckeredBackground();
    }
}
```

#### 7.2 Very Large Images

**Problem**: Loading very large images (e.g., 8K) may cause performance issues.

**Solution**:
```csharp
private void LoadImage()
{
    if (string.IsNullOrEmpty(_imagePath) || !System.IO.File.Exists(_imagePath))
        return;
    
    try
    {
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.UriSource = new Uri(_imagePath);
        bi.CacheOption = BitmapCacheOption.OnLoad;
        
        // Limit decode size for performance
        bi.DecodePixelWidth = 1920;
        bi.DecodePixelHeight = 1080;
        
        bi.EndInit();
        bi.Freeze(); // For cross-thread access
        CropImage.Source = bi;
    }
    catch
    {
        CropImage.Source = null;
    }
}
```

#### 7.3 Different Aspect Ratios

**Problem**: Zone and image have different aspect ratios.

**Solution**: The stretch modes handle this:
- **Fill**: May crop edges to fill zone
- **Uniform**: Fits inside zone, may leave empty space
- **UniformToFill**: Fills zone, may crop edges
- **None**: Shows original size, may overflow

#### 7.4 Multiple Monitors

**Problem**: Preview window may appear on wrong monitor.

**Solution**:
```csharp
public ImageCropPreviewWindow(...)
{
    InitializeComponent();
    
    // Position relative to owner window
    WindowStartupLocation = WindowStartupLocation.CenterOwner;
    
    // Ensure window is visible on current monitor
    StateChanged += (s, e) =>
    {
        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
        }
    };
}
```

#### 7.5 Window Resize Handling

**Problem**: When user resizes the preview window, the preview should update.

**Solution**:
```csharp
private void PreviewBorder_SizeChanged(object sender, SizeChangedEventArgs e)
{
    if (!IsLoaded) return;
    
    CalculatePreviewScale();
    UpdateZoneOutline();
    UpdateImageTransform();
    UpdateOverlay();
    DrawGridLines();
}
```

#### 7.6 Undo/Redo Support (Optional Enhancement)

**Problem**: Users may want to undo/redo changes.

**Solution**:
```csharp
private Stack<CropPreviewState> _undoStack = new();
private Stack<CropPreviewState> _redoStack = new();

private void SaveState()
{
    _undoStack.Push(new CropPreviewState
    {
        OffsetX = _currentOffsetX,
        OffsetY = _currentOffsetY,
        Zoom = _currentZoom,
        Stretch = _currentStretch,
        Opacity = _currentOpacity
    });
    _redoStack.Clear();
}

private void Undo()
{
    if (_undoStack.Count == 0) return;
    
    _redoStack.Push(new CropPreviewState
    {
        OffsetX = _currentOffsetX,
        OffsetY = _currentOffsetY,
        Zoom = _currentZoom,
        Stretch = _currentStretch,
        Opacity = _currentOpacity
    });
    
    var state = _undoStack.Pop();
    ApplyState(state);
}

private void Redo()
{
    if (_redoStack.Count == 0) return;
    
    _undoStack.Push(new CropPreviewState
    {
        OffsetX = _currentOffsetX,
        OffsetY = _currentOffsetY,
        Zoom = _currentZoom,
        Stretch = _currentStretch,
        Opacity = _currentOpacity
    });
    
    var state = _redoStack.Pop();
    ApplyState(state);
}
```

---

### 8. Localization Keys

Add to LocalizationService:

| Key | Chinese | English |
|-----|---------|---------|
| `CropPreview.Title` | 图片裁剪预览 | Image Crop Preview |
| `CropPreview.Confirm` | 确认 | Confirm |
| `CropPreview.Cancel` | 取消 | Cancel |
| `CropPreview.Reset` | 重置 | Reset |
| `CropPreview.Zoom` | 缩放 | Zoom |
| `CropPreview.Opacity` | 透明度 | Opacity |
| `CropPreview.Stretch` | 拉伸模式 | Stretch Mode |
| `CropPreview.Offset` | 偏移量 | Offset |

---

### 9. Testing Checklist

#### 9.1 Functional Tests
- [ ] Open preview window from ZoneSettingsDialog
- [ ] Open preview window from WidgetSettingsDialog (analog clock)
- [ ] Open preview window from WidgetSettingsDialog (digital clock)
- [ ] Drag image to adjust position
- [ ] Use mouse wheel to zoom
- [ ] Use slider to zoom
- [ ] Change stretch mode
- [ ] Adjust opacity
- [ ] Click Reset to restore initial values
- [ ] Click Cancel to discard changes
- [ ] Click Confirm to apply changes
- [ ] Verify changes appear in settings dialog
- [ ] Verify changes apply to zone/widget

#### 9.2 Edge Case Tests
- [ ] Test with image smaller than zone
- [ ] Test with very large image (>4K)
- [ ] Test with different aspect ratios
- [ ] Test with no image loaded (button disabled)
- [ ] Test with invalid image path
- [ ] Test window resize
- [ ] Test on multiple monitors
- [ ] Test keyboard shortcuts (Escape to cancel)

#### 9.3 UI/UX Tests
- [ ] Dark theme consistency
- [ ] Smooth drag interaction
- [ ] Responsive zoom (no lag)
- [ ] Clear visual feedback
- [ ] Grid lines align correctly
- [ ] Zone outline visible
- [ ] Crop overlay shows correctly

---

### 10. Performance Considerations

1. **Image Loading**: Use `BitmapCacheOption.OnLoad` and `DecodePixelWidth/Height` for large images
2. **Transform Updates**: Use `Dispatcher.BeginInvoke` for smooth updates during drag
3. **Memory Management**: Call `CropImage.Source = null` and `GC.Collect()` when closing
4. **Rendering**: Use `RenderOptions.BitmapScalingMode="HighQuality"` for smooth scaling

---

### 11. Future Enhancements

1. **Aspect Ratio Lock**: Option to lock aspect ratio during resize
2. **Crop Presets**: Common crop ratios (1:1, 16:9, 4:3, etc.)
3. **Keyboard Shortcuts**: Arrow keys for precise positioning
4. **Snap to Grid**: Align to rule-of-thirds or custom grid
5. **Compare Mode**: Side-by-side before/after comparison
6. **Export Cropped Image**: Save the cropped region as a new file
7. **History**: Undo/redo stack
8. **Presets**: Save/load custom crop presets

---

### 12. Implementation Timeline

| Phase | Task | Estimated Time |
|-------|------|----------------|
| 1 | Create CropPreviewResult model | 0.5 hours |
| 2 | Create ImageCropPreviewWindow.xaml | 2 hours |
| 3 | Create ImageCropPreviewWindow.xaml.cs | 4 hours |
| 4 | Integrate with ZoneSettingsDialog | 1 hour |
| 5 | Integrate with WidgetSettingsDialog | 1.5 hours |
| 6 | Add localization strings | 0.5 hours |
| 7 | Testing and bug fixes | 2 hours |
| 8 | Performance optimization | 1 hour |
| **Total** | | **12.5 hours** |

---

### 13. Dependencies

- No new NuGet packages required
- Uses existing WPF framework
- Reuses existing dark theme resources
- Compatible with .NET 10.0-windows target

---

### 14. Risks and Mitigations

| Risk | Impact | Mitigation |
|------|--------|------------|
| Performance with large images | High | Decode to smaller size, use bitmap caching |
| Complex transform calculations | Medium | Thorough testing, edge case handling |
| Localization completeness | Low | Add all keys, test both languages |
| Cross-monitor positioning | Low | Use CenterOwner startup location |

---

## Summary

This implementation plan provides a comprehensive solution for adding an interactive image cropping preview window to DesktopZones. The solution:

1. **Follows existing patterns**: Uses the same dark theme, window style, and localization approach
2. **Integrates seamlessly**: Minimal changes to existing dialogs
3. **Provides rich interaction**: Drag, zoom, stretch modes, opacity control
4. **Handles edge cases**: Large images, different aspect ratios, window resize
5. **Is maintainable**: Clean separation of concerns, well-documented code

The implementation can be completed in approximately 12.5 hours and requires no new dependencies.
