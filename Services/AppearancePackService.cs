using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DesktopZones.Helpers;
using DesktopZones.Models;

namespace DesktopZones.Services;

/// <summary>外观包校验结果。空 <see cref="Errors"/> 且至少一项内容 = 可安装。</summary>
public sealed class AppearancePackValidation
{
    public List<string> Errors { get; } = new();
    public List<AcrylicHelper.GlassMaterialRecipe> Materials { get; } = new();
    public List<AppearancePresetImport> Presets { get; } = new();

    public int MaterialCount => Materials.Count;
    public int PresetCount => Presets.Count;
    public bool Ok => Errors.Count == 0;
}

/// <summary>一条待导入的预设（已解析出目标类型与包内绝对路径）。</summary>
public sealed class AppearancePresetImport
{
    public PresetKind Kind { get; init; }
    public string Path { get; init; } = "";
    public string DeclaredKind { get; init; } = "";
}

/// <summary>
/// 外观包（<c>kind=appearance</c>）的校验、材质注册与预设导入。
///
/// 纯数据：不执行代码。材质映射到 <see cref="AcrylicHelper.GlassMaterialRecipe"/>，
/// 安装后出现在「外观 → 材质」下拉框；预设复制进 <c>Presets\&lt;kind&gt;\</c>，
/// 安装后出现在对应「加载预设」列表。两者都**不自动改现有对象**，停用/卸载即撤销。
/// </summary>
public sealed class AppearancePackService
{
    public static AppearancePackService Instance { get; } = new();

    AppearancePackService() { }

    public const int MaxMaterials = 32;
    public const int MaxPresets = 32;
    public const long MaxFileBytes = 2 * 1024 * 1024;
    const int MaxReportedErrors = 20;

    static readonly Regex MaterialIdRe = new("^[a-z0-9][a-z0-9.-]{1,31}$", RegexOptions.Compiled);

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    readonly Dictionary<string, AcrylicHelper.GlassMaterialRecipe> _materials =
        new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, (int Materials, int Presets)> _summaries =
        new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> _errors = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>已注册的包内材质（key = <c>pack:&lt;pluginId&gt;:&lt;id&gt;</c>）。</summary>
    public IReadOnlyList<AcrylicHelper.GlassMaterialRecipe> Materials =>
        _materials.Values.OrderBy(m => m.Key, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>包 id →（材质数，预设数），用于已安装列表/详情。</summary>
    public (int Materials, int Presets)? GetSummary(string pluginId)
        => _summaries.TryGetValue(pluginId, out var summary) ? summary : null;

    /// <summary>包 id → 加载错误。</summary>
    public IReadOnlyDictionary<string, string> Errors => _errors;

    public AcrylicHelper.GlassMaterialRecipe? FindMaterial(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        return _materials.TryGetValue(key, out var recipe) ? recipe : null;
    }

    // ── 刷新材质注册表 ──

    /// <summary>按插件记录重建材质表（启动/安装/卸载/启停都走这里）。</summary>
    public bool Refresh(IEnumerable<PluginRecord> records, bool pluginsEnabled)
    {
        var next = new Dictionary<string, AcrylicHelper.GlassMaterialRecipe>(StringComparer.OrdinalIgnoreCase);
        var summaries = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (pluginsEnabled)
        {
            foreach (var record in records)
            {
                if (record.KindValue != PluginKind.Appearance || !record.Enabled) continue;
                var dir = PluginPaths.VersionDir(record.Id, record.Version);
                if (!Directory.Exists(dir))
                {
                    errors[record.Id] = "files are missing; reinstall the appearance pack";
                    continue;
                }
                var manifest = PluginPackages.ReadManifest(dir, out var manifestError);
                if (manifest == null || manifest.KindValue != PluginKind.Appearance)
                {
                    errors[record.Id] = manifestError ?? "plugin.json is not an appearance pack";
                    continue;
                }
                var validation = Validate(dir, manifest);
                if (!validation.Ok)
                {
                    errors[record.Id] = string.Join("; ", validation.Errors.Take(3));
                    continue;
                }
                foreach (var recipe in validation.Materials) next[recipe.Key] = recipe;
                summaries[record.Id] = (validation.MaterialCount, validation.PresetCount);
            }
        }

        var changed = !SameRecipes(_materials, next) || !SameSummaries(_summaries, summaries)
                      || !SameErrors(_errors, errors);
        _materials.Clear();
        foreach (var pair in next) _materials[pair.Key] = pair.Value;
        _summaries.Clear();
        foreach (var pair in summaries) _summaries[pair.Key] = pair.Value;
        _errors.Clear();
        foreach (var pair in errors) _errors[pair.Key] = pair.Value;
        return changed;
    }

    static bool SameRecipes(Dictionary<string, AcrylicHelper.GlassMaterialRecipe> a,
        Dictionary<string, AcrylicHelper.GlassMaterialRecipe> b)
        => a.Count == b.Count && a.All(p => b.ContainsKey(p.Key));

    static bool SameSummaries(Dictionary<string, (int, int)> a, Dictionary<string, (int, int)> b)
        => a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var v) && v == p.Value);

    static bool SameErrors(Dictionary<string, string> a, Dictionary<string, string> b)
        => a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var v) && v == p.Value);

    // ── 校验 ──

    public AppearancePackValidation Validate(string packageDir, PluginManifest manifest)
    {
        var result = new AppearancePackValidation();
        if (manifest.KindValue != PluginKind.Appearance)
        {
            result.Errors.Add("kind must be appearance");
            return result;
        }

        foreach (var relative in manifest.Payload?.Materials ?? new List<string>())
        {
            if (result.Errors.Count >= MaxReportedErrors) break;
            if (!PluginPaths.IsSafeRelativePath(relative))
            {
                result.Errors.Add($"materials path is unsafe: {relative}");
                continue;
            }
            if (!TryResolveFile(packageDir, relative, out var full, out var error))
            {
                result.Errors.Add(error!);
                continue;
            }
            var definitions = ReadMaterialFile(full, out error);
            if (definitions == null)
            {
                result.Errors.Add(error!);
                continue;
            }
            foreach (var definition in definitions)
            {
                if (result.Materials.Count >= MaxMaterials)
                {
                    result.Errors.Add($"too many materials (max {MaxMaterials})");
                    break;
                }
                ValidateMaterial(manifest, definition, result);
            }
        }

        foreach (var preset in manifest.Payload?.Presets ?? new List<PluginPresetDeclaration>())
        {
            if (result.Errors.Count >= MaxReportedErrors) break;
            if (result.Presets.Count >= MaxPresets)
            {
                result.Errors.Add($"too many presets (max {MaxPresets})");
                break;
            }
            if (!PluginPaths.IsSafeRelativePath(preset.File))
            {
                result.Errors.Add($"presets[].file is unsafe: {preset.File}");
                continue;
            }
            if (!TryResolveFile(packageDir, preset.File, out var full, out var error))
            {
                result.Errors.Add(error!);
                continue;
            }
            if (!TryMapPresetKind(preset.Kind, out var kind))
            {
                result.Errors.Add($"presets[].kind is not supported: {preset.Kind}");
                continue;
            }
            var validatedKind = kind;
            var record = PresetService.For(validatedKind).ValidateFile(full, out error);
            if (record == null && string.Equals(preset.Kind?.Trim(), "media", StringComparison.OrdinalIgnoreCase))
            {
                // media 同时覆盖「律动条/唱片机」两种预设，按文件里的 Kind 兜底。
                validatedKind = PresetKind.MediaPlayerVinyl;
                record = PresetService.For(validatedKind).ValidateFile(full, out error);
            }
            if (record == null)
            {
                result.Errors.Add($"preset '{preset.File}': {error}");
                continue;
            }
            result.Presets.Add(new AppearancePresetImport
            {
                Kind = validatedKind,
                Path = full,
                DeclaredKind = preset.Kind
            });
        }

        if (result.Ok && result.MaterialCount == 0 && result.PresetCount == 0)
            result.Errors.Add("payload contains no materials or presets");
        return result;
    }

    void ValidateMaterial(PluginManifest manifest, AppearanceMaterialDefinition definition,
        AppearancePackValidation result)
    {
        if (!MaterialIdRe.IsMatch(definition.Id ?? ""))
        {
            result.Errors.Add($"material id is invalid: {definition.Id}");
            return;
        }
        if (!definition.Name.HasAny)
        {
            result.Errors.Add($"material '{definition.Id}': name.zh/en is required");
            return;
        }
        var backdrop = (definition.Backdrop ?? "").Trim().ToLowerInvariant();
        if (backdrop is not ("acrylic" or "blur"))
        {
            result.Errors.Add($"material '{definition.Id}': backdrop must be acrylic or blur");
            return;
        }
        if (definition.Blur is < 0 or > 60)
        {
            result.Errors.Add($"material '{definition.Id}': blur must be 0..60");
            return;
        }
        if (definition.Tint is < 0 or > 100)
        {
            result.Errors.Add($"material '{definition.Id}': tint must be 0..100");
            return;
        }
        if (definition.Luminosity is < 0 or > 150)
        {
            result.Errors.Add($"material '{definition.Id}': luminosity must be 0..150");
            return;
        }
        if (definition.Grain is < 0 or > 0.3)
        {
            result.Errors.Add($"material '{definition.Id}': grain must be 0..0.3");
            return;
        }
        var key = MaterialKey(manifest.Id, definition.Id);
        if (result.Materials.Any(m => string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase)))
        {
            result.Errors.Add($"material id is duplicated: {definition.Id}");
            return;
        }

        result.Materials.Add(new AcrylicHelper.GlassMaterialRecipe(
            key,
            "",
            backdrop == "blur" ? AcrylicHelper.AccentStateBlur : AcrylicHelper.AccentStateAcrylic,
            backdrop == "acrylic",
            definition.Blur,
            definition.Tint,
            definition.Luminosity,
            "Default",
            definition.Grain,
            definition.Name.Get("zh"),
            definition.Name.Get("en")));
    }

    static List<AppearanceMaterialDefinition>? ReadMaterialFile(string path, out string? error)
    {
        error = null;
        try
        {
            var text = File.ReadAllText(path, Encoding.UTF8);
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                return JsonSerializer.Deserialize<List<AppearanceMaterialDefinition>>(text, JsonOptions);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                var file = JsonSerializer.Deserialize<AppearanceMaterialFile>(text, JsonOptions);
                if (file?.Materials == null)
                {
                    error = "material file must contain materials[]";
                    return null;
                }
                return file.Materials;
            }
            error = "material file must be an array or an object with materials[]";
            return null;
        }
        catch (JsonException ex)
        {
            error = "material file is not valid JSON: " + ex.Message;
            return null;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    static bool TryResolveFile(string packageDir, string relative, out string full, out string? error)
    {
        full = "";
        error = null;
        var resolved = PluginPaths.ResolveInside(packageDir, relative);
        if (resolved == null)
        {
            error = $"unsafe path: {relative}";
            return false;
        }
        if (!File.Exists(resolved))
        {
            error = "file is missing: " + relative;
            return false;
        }
        if (new FileInfo(resolved).Length > MaxFileBytes)
        {
            error = "file is larger than 2 MB: " + relative;
            return false;
        }
        full = resolved;
        return true;
    }

    public static string MaterialKey(string pluginId, string materialId) => $"pack:{pluginId}:{materialId}";

    public static bool IsPackMaterial(string? key)
        => !string.IsNullOrEmpty(key) && key.StartsWith("pack:", StringComparison.OrdinalIgnoreCase);

    static bool TryMapPresetKind(string? raw, out PresetKind kind)
    {
        switch (raw?.Trim().ToLowerInvariant())
        {
            case "zone": kind = PresetKind.Zone; return true;
            case "panel": kind = PresetKind.Panel; return true;
            case "sticky": kind = PresetKind.StickyNote; return true;
            case "clock": kind = PresetKind.Clock; return true;
            case "calendar": kind = PresetKind.Calendar; return true;
            case "dock": kind = PresetKind.Dock; return true;
            case "media": kind = PresetKind.MediaPlayerBars; return true;
            default: kind = PresetKind.Zone; return false;
        }
    }

    // ── 安装 / 卸载 ──

    /// <summary>
    /// 导入预设（在包目录已落盘之后调用）。先清掉同包旧版本导入的预设，
    /// 任一条失败则回滚，避免留下半个包。
    /// </summary>
    public bool ImportPresets(string packageDir, PluginManifest manifest, out string? error)
    {
        error = null;
        if (manifest.KindValue != PluginKind.Appearance) return true;
        var validation = Validate(packageDir, manifest);
        if (!validation.Ok)
        {
            error = string.Join("; ", validation.Errors.Take(5));
            return false;
        }

        DeletePresets(manifest.Id);
        foreach (var preset in validation.Presets)
        {
            var imported = PresetService.For(preset.Kind)
                .ImportFile(preset.Path, manifest.Id, out var importError);
            if (imported != null) continue;
            DeletePresets(manifest.Id);
            error = $"cannot import preset '{Path.GetFileName(preset.Path)}': {importError}";
            return false;
        }
        foreach (var preset in validation.Presets)
        {
            DzTrace.Always($"[Appearance] imported preset {preset.Kind} from {manifest.Id}");
        }
        return true;
    }

    /// <summary>卸载/更新前清理：删除该包导入的全部预设。</summary>
    public void DeletePresets(string pluginId)
    {
        foreach (var kind in Enum.GetValues<PresetKind>())
        {
            try { PresetService.For(kind).DeleteForPack(pluginId); }
            catch (Exception ex) { DzTrace.Always($"[Appearance] cannot delete presets: {ex.Message}"); }
        }
    }

    /// <summary>记录被卸载后调用：撤销预设 + 材质 + 摘要。</summary>
    public void OnUninstalled(string pluginId)
    {
        DeletePresets(pluginId);
        foreach (var key in _materials.Keys
                     .Where(k => k.StartsWith(MaterialKey(pluginId, ""), StringComparison.OrdinalIgnoreCase))
                     .ToList())
            _materials.Remove(key);
        _summaries.Remove(pluginId);
        _errors.Remove(pluginId);
    }
}
