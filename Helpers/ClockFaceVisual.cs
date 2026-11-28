using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using DesktopZones.Models;

namespace DesktopZones.Helpers;

/// <summary>
/// 用纯 WPF 绘制 <see cref="ClockFaceSpec"/>。面盘是纯数据：这里只解析路径/颜色/
/// 渐变并画图，不执行插件代码。静态层缓存在 <see cref="Geometry"/> 里，时间只影响
/// 指针旋转与秒针弧/光点，所以 1 Hz 更新的成本很小。
/// </summary>
public sealed class ClockFaceVisual : FrameworkElement
{
    readonly ClockFaceSpec _spec;
    readonly Dictionary<ClockFaceLayer, Geometry> _geometryCache = new();
    readonly Dictionary<string, ClockFaceGradient> _gradients = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Brush> _brushCache = new(StringComparer.Ordinal);
    readonly Dictionary<string, Pen> _penCache = new(StringComparer.Ordinal);

    Color _ink = Color.FromRgb(0xEE, 0xF4, 0xFF);
    Color _accent = Color.FromRgb(0x4C, 0xC9, 0xF0);
    Color _signal = Color.FromRgb(0xFF, 0x66, 0x66);
    DateTime _now = DateTime.Now;
    bool _showSeconds = true;

    public ClockFaceVisual(ClockFaceSpec spec)
    {
        _spec = spec ?? throw new ArgumentNullException(nameof(spec));
        Width = Height = spec.Size;
        IsHitTestVisible = false;
        SnapsToDevicePixels = false;
        var clip = new RectangleGeometry(new Rect(0, 0, spec.Size, spec.Size));
        clip.Freeze();
        Clip = clip;
        foreach (var gradient in spec.Gradients)
            _gradients[gradient.Id] = gradient;
    }

    public ClockFaceSpec Spec => _spec;

    protected override Size MeasureOverride(Size availableSize)
        => new(_spec.Size, _spec.Size);

    protected override Size ArrangeOverride(Size finalSize)
        => new(_spec.Size, _spec.Size);

    /// <summary>主题色变化时调用。颜色 token 的 brush 缓存会重建，几何不重建。</summary>
    public void ApplyPalette(Color ink, Color accent, Color signal)
    {
        if (_ink == ink && _accent == accent && _signal == signal) return;
        _ink = ink;
        _accent = accent;
        _signal = signal;
        _brushCache.Clear();
        _penCache.Clear();
        InvalidateVisual();
    }

    /// <summary>每秒调用一次；showSeconds=false 时隐藏秒针层。</summary>
    public void SetTime(DateTime now, bool showSeconds)
    {
        _now = now;
        _showSeconds = showSeconds;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var bounds = new Rect(0, 0, _spec.Size, _spec.Size);
        dc.DrawRectangle(Brushes.Transparent, null, bounds);

        var translated = Math.Abs(_spec.OffsetX) > 0.001 || Math.Abs(_spec.OffsetY) > 0.001;
        if (translated)
            dc.PushTransform(new TranslateTransform(_spec.OffsetX, _spec.OffsetY));
        try
        {
            DrawLayers(dc, _spec.Layers);
            DrawHands(dc);
            DrawLayers(dc, _spec.Overlays);
        }
        finally
        {
            if (translated) dc.Pop();
        }
    }

    void DrawLayers(DrawingContext dc, List<ClockFaceLayer>? layers)
    {
        if (layers == null) return;
        foreach (var layer in layers)
        {
            if (layer.Opacity <= 0) continue;
            var pushed = layer.Opacity < 0.999;
            if (pushed) dc.PushOpacity(layer.Opacity);
            try { DrawLayer(dc, layer); }
            finally { if (pushed) dc.Pop(); }
        }
    }

    void DrawLayer(DrawingContext dc, ClockFaceLayer layer)
    {
        var type = (layer.Type ?? "").Trim().ToLowerInvariant();
        if (type == "text")
        {
            DrawText(dc, layer);
            return;
        }

        var geometry = GetGeometry(layer);
        if (geometry == null) return;
        var fill = ResolveBrush(layer.Fill);
        var pen = ResolvePen(layer.Stroke, layer.StrokeWidth, layer.LineCap, layer.DashArray);
        if (fill == null && pen == null) return;
        if (fill != null)
        {
            var pushed = layer.FillOpacity < 0.999;
            if (pushed) dc.PushOpacity(layer.FillOpacity);
            try { dc.DrawGeometry(fill, null, geometry); }
            finally { if (pushed) dc.Pop(); }
        }
        if (pen != null)
        {
            var pushed = layer.StrokeOpacity < 0.999;
            if (pushed) dc.PushOpacity(layer.StrokeOpacity);
            try { dc.DrawGeometry(null, pen, geometry); }
            finally { if (pushed) dc.Pop(); }
        }
    }

    void DrawText(DrawingContext dc, ClockFaceLayer layer)
    {
        var brush = ResolveBrush(layer.Fill);
        if (brush == null) return;
        var text = layer.Text ?? "";
        if (text.Length == 0) return;

        var size = layer.FontSize ?? 10;
        var family = string.IsNullOrWhiteSpace(layer.FontFamily) ? "Segoe UI" : layer.FontFamily!;
        FontFamily fontFamily;
        try { fontFamily = new FontFamily(family); }
        catch { fontFamily = new FontFamily("Segoe UI"); }

        var weight = (layer.FontWeight ?? "normal").Trim().ToLowerInvariant() switch
        {
            "light" => FontWeights.Light,
            "semibold" => FontWeights.SemiBold,
            "bold" => FontWeights.Bold,
            _ => FontWeights.Normal
        };
        var typeface = new Typeface(fontFamily, FontStyles.Normal, weight, FontStretches.Normal);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, typeface, size, brush, dpi)
        {
            TextAlignment = TextAlignment.Left,
        };
        var x = layer.X1 ?? 0;
        var y = (layer.Y1 ?? 0) - size * 0.82;
        var anchor = (layer.TextAnchor ?? "start").Trim().ToLowerInvariant();
        if (anchor == "middle") x -= formatted.Width / 2;
        else if (anchor == "end") x -= formatted.Width;
        var pushed = layer.FillOpacity < 0.999;
        if (pushed) dc.PushOpacity(layer.FillOpacity);
        try { dc.DrawText(formatted, new Point(x, y)); }
        finally { if (pushed) dc.Pop(); }
    }

    void DrawHands(DrawingContext dc)
    {
        var seconds = _showSeconds ? _now.Second + _now.Millisecond / 1000.0 : 0;
        var minuteAngle = (_now.Minute + seconds / 60.0) * 6.0;
        var hourAngle = ((_now.Hour % 12) + _now.Minute / 60.0 + seconds / 3600.0) * 30.0;

        DrawHand(dc, _spec.Hands?.Hour, hourAngle);
        DrawHand(dc, _spec.Hands?.Minute, minuteAngle);
        if (!_showSeconds) return;

        var second = _spec.Hands?.Second;
        if (second == null) return;
        var mode = (second.Mode ?? "none").Trim().ToLowerInvariant();
        switch (mode)
        {
            case "rotate":
                DrawHand(dc, second.Hand, seconds * 6.0);
                break;
            case "orbitarc":
                DrawOrbitArc(dc, second, seconds);
                break;
            case "dot":
                DrawDot(dc, second, seconds);
                break;
        }
    }

    void DrawHand(DrawingContext dc, ClockFaceHand? hand, double angle)
    {
        if (hand == null || string.IsNullOrWhiteSpace(hand.Data) || hand.Opacity <= 0) return;
        var geometry = GetHandGeometry(hand.Data!);
        if (geometry == null) return;
        var fill = ResolveBrush(hand.Fill);
        var pen = ResolvePen(hand.Stroke, hand.StrokeWidth, hand.LineCap, null);
        if (fill == null && pen == null) return;

        dc.PushTransform(new RotateTransform(angle, _spec.CenterX, _spec.CenterY));
        try
        {
            if (hand.Opacity < 0.999) dc.PushOpacity(hand.Opacity);
            try
            {
                if (fill != null)
                {
                    var pushed = hand.FillOpacity < 0.999;
                    if (pushed) dc.PushOpacity(hand.FillOpacity);
                    try { dc.DrawGeometry(fill, null, geometry); }
                    finally { if (pushed) dc.Pop(); }
                }
                if (pen != null)
                {
                    var pushed = hand.StrokeOpacity < 0.999;
                    if (pushed) dc.PushOpacity(hand.StrokeOpacity);
                    try { dc.DrawGeometry(null, pen, geometry); }
                    finally { if (pushed) dc.Pop(); }
                }
            }
            finally { if (hand.Opacity < 0.999) dc.Pop(); }
        }
        finally { dc.Pop(); }
    }

    void DrawOrbitArc(DrawingContext dc, ClockFaceSecond second, double seconds)
    {
        var radius = second.Radius ?? 97;
        var sweep = Math.Max(0, Math.Min(360, seconds * 6.0));
        if (sweep < 0.05) return;
        var startAngle = -90.0;
        var endAngle = startAngle + sweep;
        var start = PointOnCircle(radius, startAngle);
        var end = PointOnCircle(radius, endAngle);
        var figure = new PathFigure { StartPoint = start, IsClosed = false, IsFilled = false };
        figure.Segments.Add(new ArcSegment(end, new Size(radius, radius), 0,
            sweep > 180, SweepDirection.Clockwise, true));
        var geometry = new PathGeometry(new[] { figure });
        geometry.Freeze();
        var pen = ResolvePen(second.Stroke, second.StrokeWidth ?? 1.5, second.LineCap, null);
        if (pen != null) dc.DrawGeometry(null, pen, geometry);

        var dotR = second.DotRadius ?? 2;
        var dotFill = ResolveBrush(second.Fill ?? second.Stroke);
        if (dotFill != null && dotR > 0)
        {
            var dot = new EllipseGeometry(end, dotR, dotR);
            dot.Freeze();
            dc.DrawGeometry(dotFill, null, dot);
        }
    }

    void DrawDot(DrawingContext dc, ClockFaceSecond second, double seconds)
    {
        var radius = second.Radius ?? 86;
        var dotR = second.DotRadius ?? 2.4;
        var angle = -90.0 + seconds * 6.0;

        // 05 的拖尾：往回画几颗逐渐变淡的小点，不引入模糊滤镜。
        var trail = second.TrailDegrees ?? 0;
        if (trail > 0)
        {
            var fill = ResolveBrush(second.TrailStroke ?? second.Fill ?? second.Stroke);
            var opacity = second.TrailOpacity ?? 0.25;
            if (fill != null && opacity > 0)
            {
                const int steps = 7;
                for (var i = steps; i >= 1; i--)
                {
                    var a = angle - trail * i / steps;
                    var p = PointOnCircle(radius, a);
                    var r = dotR * (0.25 + 0.55 * (steps - i) / steps);
                    var g = new EllipseGeometry(p, r, r);
                    g.Freeze();
                    dc.PushOpacity(opacity * (steps - i + 1) / steps);
                    try { dc.DrawGeometry(fill, null, g); }
                    finally { dc.Pop(); }
                }
            }
        }

        var dotFill = ResolveBrush(second.Fill ?? second.Stroke);
        if (dotFill == null || dotR <= 0) return;
        var dot = new EllipseGeometry(PointOnCircle(radius, angle), dotR, dotR);
        dot.Freeze();
        dc.DrawGeometry(dotFill, null, dot);
    }

    Point PointOnCircle(double radius, double degrees)
    {
        var rad = degrees * Math.PI / 180.0;
        return new Point(_spec.CenterX + Math.Cos(rad) * radius,
                         _spec.CenterY + Math.Sin(rad) * radius);
    }

    Geometry? GetGeometry(ClockFaceLayer layer)
    {
        if (_geometryCache.TryGetValue(layer, out var cached)) return cached;
        Geometry? geometry = null;
        var type = (layer.Type ?? "").Trim().ToLowerInvariant();
        try
        {
            switch (type)
            {
                case "path" when !string.IsNullOrWhiteSpace(layer.Data):
                    geometry = Geometry.Parse(layer.Data);
                    break;
                case "circle" when layer.Cx.HasValue && layer.Cy.HasValue && layer.R.HasValue:
                    geometry = new EllipseGeometry(new Point(layer.Cx.Value, layer.Cy.Value),
                        layer.R.Value, layer.R.Value);
                    break;
                case "line" when layer.X1.HasValue && layer.Y1.HasValue && layer.X2.HasValue && layer.Y2.HasValue:
                    geometry = new LineGeometry(new Point(layer.X1.Value, layer.Y1.Value),
                        new Point(layer.X2.Value, layer.Y2.Value));
                    break;
            }
            if (geometry != null) geometry.Freeze();
        }
        catch
        {
            geometry = null;
        }
        _geometryCache[layer] = geometry!;
        return geometry;
    }

    readonly Dictionary<string, Geometry?> _handGeometryCache = new(StringComparer.Ordinal);

    Geometry? GetHandGeometry(string data)
    {
        if (_handGeometryCache.TryGetValue(data, out var cached)) return cached;
        Geometry? geometry = null;
        try { geometry = Geometry.Parse(data); geometry?.Freeze(); }
        catch { geometry = null; }
        _handGeometryCache[data] = geometry;
        return geometry;
    }

    Brush? ResolveBrush(string? paint)
    {
        if (string.IsNullOrWhiteSpace(paint)) return null;
        var key = paint.Trim();
        if (key.Equals("none", StringComparison.OrdinalIgnoreCase)) return null;
        if (_brushCache.TryGetValue(key, out var cached)) return cached;
        Brush? brush = null;
        try
        {
            if (key.StartsWith("token:", StringComparison.OrdinalIgnoreCase))
            {
                var token = key["token:".Length..].Trim().ToLowerInvariant();
                var color = token switch
                {
                    "ink" => _ink,
                    "accent" => _accent,
                    "signal" => _signal,
                    _ => _ink
                };
                brush = FrozenBrush(color);
            }
            else if (key.StartsWith("grad:", StringComparison.OrdinalIgnoreCase))
            {
                var id = key["grad:".Length..].Trim();
                if (_gradients.TryGetValue(id, out var gradient)) brush = BuildGradient(gradient);
            }
            else if (TryParseColor(key, out var color))
            {
                brush = FrozenBrush(color);
            }
        }
        catch { brush = null; }
        _brushCache[key] = brush!;
        return brush;
    }

    Pen? ResolvePen(string? stroke, double? width, string? lineCap, string? dash)
    {
        if (string.IsNullOrWhiteSpace(stroke)) return null;
        if (stroke.Trim().Equals("none", StringComparison.OrdinalIgnoreCase)) return null;
        var w = width is > 0 ? width.Value : 1.0;
        var cap = (lineCap ?? "butt").Trim().ToLowerInvariant() switch
        {
            "round" => PenLineCap.Round,
            "square" => PenLineCap.Square,
            _ => PenLineCap.Flat
        };
        var key = $"{stroke.Trim()}|{w:0.###}|{cap}|{dash}";
        if (_penCache.TryGetValue(key, out var cached)) return cached;
        var brush = ResolveBrush(stroke);
        if (brush == null) return null;
        var pen = new Pen(brush, w)
        {
            StartLineCap = cap,
            EndLineCap = cap,
            LineJoin = PenLineJoin.Round,
        };
        if (!string.IsNullOrWhiteSpace(dash))
        {
            var parts = dash.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            var dashes = new List<double>();
            foreach (var part in parts)
                if (double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0)
                    dashes.Add(d / w);
            if (dashes.Count > 0) pen.DashStyle = new DashStyle(dashes, 0);
        }
        pen.Freeze();
        _penCache[key] = pen;
        return pen;
    }

    Brush BuildGradient(ClockFaceGradient gradient)
    {
        var stops = new GradientStopCollection();
        foreach (var stop in gradient.Stops)
        {
            var color = stop.Color.StartsWith("token:", StringComparison.OrdinalIgnoreCase)
                ? stop.Color["token:".Length..].Trim().ToLowerInvariant() switch
                {
                    "ink" => _ink,
                    "accent" => _accent,
                    "signal" => _signal,
                    _ => _ink
                }
                : TryParseColor(stop.Color, out var literal) ? literal : Color.FromArgb(0, 0, 0, 0);
            var a = (byte)Math.Clamp((int)Math.Round(color.A * stop.Opacity), 0, 255);
            stops.Add(new GradientStop(Color.FromArgb(a, color.R, color.G, color.B),
                Math.Clamp(stop.Offset, 0, 1)));
        }

        GradientBrush brush;
        var type = (gradient.Type ?? "linear").Trim().ToLowerInvariant();
        if (type == "radial")
        {
            brush = new RadialGradientBrush(stops)
            {
                MappingMode = BrushMappingMode.Absolute,
                Center = new Point(gradient.Cx, gradient.Cy),
                GradientOrigin = new Point(gradient.Cx, gradient.Cy),
                RadiusX = Math.Max(0.1, gradient.RadiusX),
                RadiusY = Math.Max(0.1, gradient.RadiusY),
                SpreadMethod = GradientSpreadMethod.Pad
            };
        }
        else
        {
            brush = new LinearGradientBrush(stops)
            {
                MappingMode = BrushMappingMode.Absolute,
                StartPoint = new Point(gradient.X1, gradient.Y1),
                EndPoint = new Point(gradient.X2, gradient.Y2),
                SpreadMethod = GradientSpreadMethod.Pad
            };
        }
        brush.Freeze();
        return brush;
    }

    static SolidColorBrush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    static bool TryParseColor(string value, out Color color)
    {
        color = default;
        if (!value.StartsWith('#') || (value.Length != 7 && value.Length != 9)) return false;
        try
        {
            color = (Color)ColorConverter.ConvertFromString(value)!;
            return true;
        }
        catch { return false; }
    }
}
