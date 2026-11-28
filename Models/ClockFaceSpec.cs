using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows.Media;

namespace DesktopZones.Models;

/// <summary>
/// 外观包里的一个指针面盘（纯数据，不执行代码）。坐标固定为 220×220、原点左上、
/// 圆心 (110,110)；宿主按当前时间和用户颜色 token 绘制。
/// </summary>
public sealed class ClockFaceSpec
{
    public int SchemaVersion { get; set; } = 1;
    public double Size { get; set; } = 220;
    public double CenterX { get; set; } = 110;
    public double CenterY { get; set; } = 110;

    /// <summary>设计稿坐标系相对 220×220 面盘中心的平移（SVG 240×260 卡片中心是
    /// (120,130)，转成面盘中心 (110,110) 时用 -10/-20）。</summary>
    public double OffsetX { get; set; }
    public double OffsetY { get; set; }

    /// <summary>线性/径向渐变定义，供 fill/stroke 用 <c>grad:&lt;id&gt;</c> 引用。</summary>
    public List<ClockFaceGradient> Gradients { get; set; } = new();

    /// <summary>静态层，按数组顺序绘制（在指针下方）。</summary>
    public List<ClockFaceLayer> Layers { get; set; } = new();

    public ClockFaceHands Hands { get; set; } = new();

    /// <summary>静态覆盖层（在指针上方，通常放中心帽/高光）。</summary>
    public List<ClockFaceLayer> Overlays { get; set; } = new();
}

public sealed class ClockFaceGradient
{
    /// <summary>包内唯一 id，[a-z0-9][a-z0-9.-]{0,31}。</summary>
    public string Id { get; set; } = "";

    /// <summary>linear / radial。</summary>
    public string Type { get; set; } = "linear";

    // linear：绝对坐标（220 坐标系）
    public double X1 { get; set; }
    public double Y1 { get; set; }
    public double X2 { get; set; } = 1;
    public double Y2 { get; set; }

    // radial：绝对坐标
    public double Cx { get; set; } = 110;
    public double Cy { get; set; } = 110;
    public double RadiusX { get; set; } = 110;
    public double RadiusY { get; set; } = 110;

    public List<ClockFaceGradientStop> Stops { get; set; } = new();
}

public sealed class ClockFaceGradientStop
{
    public double Offset { get; set; }
    /// <summary>#RRGGBB / #AARRGGBB / token:ink|accent|signal。</summary>
    public string Color { get; set; } = "";
    public double Opacity { get; set; } = 1;
}

public sealed class ClockFaceLayer
{
    /// <summary>path / circle / line / text。</summary>
    public string Type { get; set; } = "";

    // path
    public string? Data { get; set; }

    // circle
    public double? Cx { get; set; }
    public double? Cy { get; set; }
    public double? R { get; set; }

    // line
    public double? X1 { get; set; }
    public double? Y1 { get; set; }
    public double? X2 { get; set; }
    public double? Y2 { get; set; }

    // text
    public string? Text { get; set; }
    public double? FontSize { get; set; }
    public string? FontFamily { get; set; }
    /// <summary>Light / Normal / SemiBold / Bold。</summary>
    public string? FontWeight { get; set; }
    /// <summary>start / middle / end。</summary>
    public string? TextAnchor { get; set; }

    /// <summary>#RRGGBB / #AARRGGBB / token:ink|accent|signal / grad:id / none。</summary>
    public string? Fill { get; set; }
    public string? Stroke { get; set; }
    public double? StrokeWidth { get; set; }
    public double FillOpacity { get; set; } = 1;
    public double StrokeOpacity { get; set; } = 1;
    /// <summary>butt / round / square（SVG 命名；WPF 同步映射）。</summary>
    public string? LineCap { get; set; }
    /// <summary>可选虚线，空格/逗号分隔，例如 "2 8"。</summary>
    public string? DashArray { get; set; }
    public double Opacity { get; set; } = 1;
}

public sealed class ClockFaceHands
{
    public ClockFaceHand? Hour { get; set; }
    public ClockFaceHand? Minute { get; set; }
    public ClockFaceSecond? Second { get; set; }
}

/// <summary>时分针形状：路径坐标按“指向 12 点”定义，宿主按时间旋转。</summary>
public sealed class ClockFaceHand
{
    public string? Data { get; set; }
    public string? Fill { get; set; }
    public string? Stroke { get; set; }
    public double? StrokeWidth { get; set; }
    public double FillOpacity { get; set; } = 1;
    public double StrokeOpacity { get; set; } = 1;
    public string? LineCap { get; set; }
    public double Opacity { get; set; } = 1;
}

/// <summary>秒针：rotate（普通旋转针）/ orbitArc（02 轨道弧）/ dot（05 巡游光点）/ none。</summary>
public sealed class ClockFaceSecond
{
    public string Mode { get; set; } = "none";

    /// <summary>mode=rotate 时必填。</summary>
    public ClockFaceHand? Hand { get; set; }

    /// <summary>orbitArc / dot 的半径（默认 orbitArc=97、dot=86）。</summary>
    public double? Radius { get; set; }

    /// <summary>orbitArc / dot 的可见笔触/点。</summary>
    public string? Stroke { get; set; }
    public double? StrokeWidth { get; set; }
    public string? LineCap { get; set; }
    public double? DotRadius { get; set; }
    public string? Fill { get; set; }

    /// <summary>可选拖尾：从秒针位置往回扫过的角度（05 用）。</summary>
    public double? TrailDegrees { get; set; }
    public string? TrailStroke { get; set; }
    public double? TrailWidth { get; set; }
    public double? TrailOpacity { get; set; }
}

/// <summary>面盘 JSON 的加载与严格校验（安装时和 UI 预览共用）。</summary>
public static class ClockFaceSpecValidator
{
    public const int MaxLayers = 512;
    public const int MaxOverlays = 64;
    public const int MaxGradients = 32;
    public const long MaxFileBytes = 2 * 1024 * 1024;
    const int MaxPathChars = 32 * 1024;
    const int MaxTextChars = 32;

    static readonly Regex FaceIdRe = new("^[a-z0-9][a-z0-9.-]{1,31}$", RegexOptions.Compiled);
    static readonly Regex GradientIdRe = new("^[A-Za-z0-9][A-Za-z0-9.-]{0,31}$", RegexOptions.Compiled);
    static readonly Regex ColorRe = new("^#(?:[0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$", RegexOptions.Compiled);

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static bool TryLoad(string path, out ClockFaceSpec? spec, out string? error)
    {
        spec = null;
        error = null;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) { error = "face file is missing"; return false; }
            if (info.Length > MaxFileBytes) { error = $"face file is larger than {MaxFileBytes / 1024} KB"; return false; }
            spec = JsonSerializer.Deserialize<ClockFaceSpec>(File.ReadAllText(path), JsonOptions);
            if (spec == null) { error = "face JSON is empty"; return false; }
            var errors = Validate(spec);
            if (errors.Count > 0)
            {
                error = string.Join("; ", errors.Take(6));
                spec = null;
                return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            error = ex.Message;
            spec = null;
            return false;
        }
    }

    public static List<string> Validate(ClockFaceSpec? spec)
    {
        var errors = new List<string>();
        if (spec == null) { errors.Add("face JSON is empty"); return errors; }
        if (spec.SchemaVersion != 1) errors.Add("schemaVersion must be 1");
        if (Math.Abs(spec.Size - 220) > 0.01) errors.Add("size must be 220");
        if (!InRange(spec.CenterX, 0, 220) || !InRange(spec.CenterY, 0, 220))
            errors.Add("center must be inside the 220x220 face");
        if (!InRange(spec.OffsetX, -100, 100) || !InRange(spec.OffsetY, -100, 100))
            errors.Add("offset must be -100..100");

        var gradientIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (spec.Gradients.Count > MaxGradients) errors.Add($"too many gradients (max {MaxGradients})");
        foreach (var g in spec.Gradients.Take(MaxGradients))
        {
            if (string.IsNullOrWhiteSpace(g.Id) || !GradientIdRe.IsMatch(g.Id))
                errors.Add($"gradient id is invalid: '{g.Id}'");
            else if (!gradientIds.Add(g.Id))
                errors.Add($"duplicate gradient id: '{g.Id}'");
            var type = (g.Type ?? "").Trim().ToLowerInvariant();
            if (type is not ("linear" or "radial")) errors.Add($"gradient '{g.Id}': type must be linear or radial");
            if (g.Stops.Count is < 2 or > 8) errors.Add($"gradient '{g.Id}': needs 2..8 stops");
            foreach (var stop in g.Stops)
            {
                if (!InRange(stop.Offset, 0, 1)) errors.Add($"gradient '{g.Id}': stop offset must be 0..1");
                if (!IsColorRef(stop.Color, null)) errors.Add($"gradient '{g.Id}': stop color is invalid");
                if (!InRange(stop.Opacity, 0, 1)) errors.Add($"gradient '{g.Id}': stop opacity must be 0..1");
            }
        }

        ValidateLayers(spec.Layers, "layers", MaxLayers, gradientIds, errors);
        ValidateLayers(spec.Overlays, "overlays", MaxOverlays, gradientIds, errors);

        if (spec.Hands == null) errors.Add("hands is required");
        else
        {
            ValidateHand(spec.Hands.Hour, "hands.hour", gradientIds, errors);
            ValidateHand(spec.Hands.Minute, "hands.minute", gradientIds, errors);
            ValidateSecond(spec.Hands.Second, gradientIds, errors);
        }
        return errors;
    }

    static void ValidateLayers(List<ClockFaceLayer>? layers, string path, int max,
        HashSet<string> gradientIds, List<string> errors)
    {
        if (layers == null) return;
        if (layers.Count > max) errors.Add($"{path} may contain at most {max} layers");
        var index = 0;
        foreach (var layer in layers.Take(max))
        {
            var at = $"{path}[{index++}]";
            var type = (layer.Type ?? "").Trim().ToLowerInvariant();
            switch (type)
            {
                case "path":
                    if (string.IsNullOrWhiteSpace(layer.Data)) errors.Add($"{at}: data is required");
                    else ValidateGeometry(layer.Data!, at, errors);
                    break;
                case "circle":
                    if (!layer.R.HasValue || !InRange(layer.R.Value, 0.01, 500)) errors.Add($"{at}: r must be > 0");
                    if (!layer.Cx.HasValue || !layer.Cy.HasValue) errors.Add($"{at}: cx/cy are required");
                    break;
                case "line":
                    if (!layer.X1.HasValue || !layer.Y1.HasValue || !layer.X2.HasValue || !layer.Y2.HasValue)
                        errors.Add($"{at}: x1/y1/x2/y2 are required");
                    break;
                case "text":
                    if (string.IsNullOrWhiteSpace(layer.Text)) errors.Add($"{at}: text is required");
                    else if (layer.Text!.Length > MaxTextChars) errors.Add($"{at}: text is too long");
                    if (!layer.FontSize.HasValue || !InRange(layer.FontSize.Value, 1, 80))
                        errors.Add($"{at}: fontSize must be 1..80");
                    if (!string.IsNullOrWhiteSpace(layer.FontFamily)
                        && (layer.FontFamily!.Length > 64 || layer.FontFamily.Any(ch => ch is '/' or '\\' or ':')))
                        errors.Add($"{at}: fontFamily is invalid");
                    if (layer.FontWeight is { Length: > 0 } fw &&
                        fw.Trim().ToLowerInvariant() is not ("light" or "normal" or "semibold" or "bold"))
                        errors.Add($"{at}: fontWeight is unsupported");
                    if (layer.TextAnchor is { Length: > 0 } ta &&
                        ta.Trim().ToLowerInvariant() is not ("start" or "middle" or "end"))
                        errors.Add($"{at}: textAnchor is unsupported");
                    break;
                default:
                    errors.Add($"{at}: type must be path/circle/line/text");
                    break;
            }
            ValidatePaint(layer.Fill, gradientIds, $"{at}.fill", errors);
            ValidatePaint(layer.Stroke, gradientIds, $"{at}.stroke", errors);
            if (layer.StrokeWidth is { } sw && !InRange(sw, 0, 40)) errors.Add($"{at}: strokeWidth must be 0..40");
            if (!InRange(layer.FillOpacity, 0, 1)) errors.Add($"{at}: fillOpacity must be 0..1");
            if (!InRange(layer.StrokeOpacity, 0, 1)) errors.Add($"{at}: strokeOpacity must be 0..1");
            if (!InRange(layer.Opacity, 0, 1)) errors.Add($"{at}: opacity must be 0..1");
            ValidateLineCap(layer.LineCap, $"{at}.lineCap", errors);
            ValidateDash(layer.DashArray, $"{at}.dashArray", errors);
        }
    }

    static void ValidateHand(ClockFaceHand? hand, string path, HashSet<string> gradientIds, List<string> errors)
    {
        if (hand == null) return;
        if (string.IsNullOrWhiteSpace(hand.Data)) errors.Add($"{path}: data is required");
        else ValidateGeometry(hand.Data!, path, errors);
        ValidatePaint(hand.Fill, gradientIds, $"{path}.fill", errors);
        ValidatePaint(hand.Stroke, gradientIds, $"{path}.stroke", errors);
        if (hand.StrokeWidth is { } sw && !InRange(sw, 0, 40)) errors.Add($"{path}: strokeWidth must be 0..40");
        if (!InRange(hand.FillOpacity, 0, 1)) errors.Add($"{path}: fillOpacity must be 0..1");
        if (!InRange(hand.StrokeOpacity, 0, 1)) errors.Add($"{path}: strokeOpacity must be 0..1");
        if (!InRange(hand.Opacity, 0, 1)) errors.Add($"{path}: opacity must be 0..1");
        ValidateLineCap(hand.LineCap, $"{path}.lineCap", errors);
    }

    static void ValidateSecond(ClockFaceSecond? second, HashSet<string> gradientIds, List<string> errors)
    {
        if (second == null) return;
        var mode = (second.Mode ?? "").Trim().ToLowerInvariant();
        if (mode is not ("none" or "rotate" or "orbitarc" or "dot"))
        {
            errors.Add("hands.second.mode must be none/rotate/orbitArc/dot");
            return;
        }
        if (mode == "rotate") ValidateHand(second.Hand, "hands.second.hand", gradientIds, errors);
        if (mode is "rotate" or "orbitarc" or "dot")
        {
            if (second.Radius is { } r && !InRange(r, 1, 120)) errors.Add("hands.second.radius must be 1..120");
            if (second.DotRadius is { } dr && !InRange(dr, 0.2, 20)) errors.Add("hands.second.dotRadius must be 0.2..20");
            ValidatePaint(second.Fill, gradientIds, "hands.second.fill", errors);
            ValidatePaint(second.Stroke, gradientIds, "hands.second.stroke", errors);
            if (second.StrokeWidth is { } sw && !InRange(sw, 0, 20)) errors.Add("hands.second.strokeWidth must be 0..20");
            if (second.TrailDegrees is { } td && !InRange(td, 0, 60)) errors.Add("hands.second.trailDegrees must be 0..60");
            if (second.TrailOpacity is { } to && !InRange(to, 0, 1)) errors.Add("hands.second.trailOpacity must be 0..1");
            ValidatePaint(second.TrailStroke, gradientIds, "hands.second.trailStroke", errors);
        }
    }

    static void ValidateGeometry(string data, string at, List<string> errors)
    {
        if (data.Length > MaxPathChars) { errors.Add($"{at}: path data is too long"); return; }
        try
        {
            var geometry = Geometry.Parse(data);
            if (geometry.Bounds.IsEmpty && !string.IsNullOrWhiteSpace(data))
                errors.Add($"{at}: path data is empty");
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException)
        {
            errors.Add($"{at}: path data is invalid");
        }
    }

    static void ValidatePaint(string? value, HashSet<string> gradientIds, string at, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var paint = value.Trim();
        if (paint.Equals("none", StringComparison.OrdinalIgnoreCase)) return;
        if (paint.StartsWith("grad:", StringComparison.OrdinalIgnoreCase))
        {
            var id = paint["grad:".Length..];
            if (!gradientIds.Contains(id)) errors.Add($"{at}: unknown gradient '{id}'");
            return;
        }
        if (!IsColorRef(paint, gradientIds)) errors.Add($"{at}: color is invalid");
    }

    static bool IsColorRef(string? value, HashSet<string>? gradientIds)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var paint = value.Trim();
        if (paint.Equals("none", StringComparison.OrdinalIgnoreCase)) return true;
        if (paint.StartsWith("token:", StringComparison.OrdinalIgnoreCase))
            return paint["token:".Length..].ToLowerInvariant() is "ink" or "accent" or "signal";
        if (paint.StartsWith("grad:", StringComparison.OrdinalIgnoreCase))
            return gradientIds != null && gradientIds.Contains(paint["grad:".Length..]);
        return ColorRe.IsMatch(paint);
    }

    static void ValidateLineCap(string? cap, string at, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(cap)) return;
        if (cap.Trim().ToLowerInvariant() is not ("butt" or "round" or "square"))
            errors.Add($"{at}: lineCap must be butt/round/square");
    }

    static void ValidateDash(string? value, string at, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var parts = value.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 8) { errors.Add($"{at}: dashArray is invalid"); return; }
        foreach (var part in parts)
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || d <= 0 || d > 40)
                errors.Add($"{at}: dashArray values must be 0..40");
    }

    static bool InRange(double value, double min, double max)
        => !double.IsNaN(value) && !double.IsInfinity(value) && value >= min && value <= max;

    public static bool IsValidFaceId(string? id) => !string.IsNullOrWhiteSpace(id) && FaceIdRe.IsMatch(id);
}
