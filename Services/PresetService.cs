using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using DesktopZones.Models;

namespace DesktopZones.Services;

/// <summary>
/// Manages presets for a specific component type (Zones, MergedGroups, StickyNotes, Clocks, Calendars, Panels).
/// Files are stored under ./Presets/{subFolder}/{guid}.json. Filename uses GUID to avoid filesystem
/// restrictions on user-entered names; uniqueness is checked against the JSON "name" field instead.
/// </summary>
public class PresetService
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    private readonly string _folder;

    /// <summary>Absolute path of the preset folder for this component type.</summary>
    public string Folder => _folder;

    public PresetService(string subFolder)
    {
        // Anchor to the application's base directory so preset files live next to the executable
        // and travel with the project (e.g. portable installs, source checkouts).
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        _folder = Path.Combine(baseDir, "Presets", subFolder);
    }

    /// <summary>Loads all presets from the folder. Returns an empty list if the folder is missing.</summary>
    public List<ZonePreset> LoadAll()
    {
        var results = new List<ZonePreset>();
        if (!Directory.Exists(_folder)) return results;

        foreach (var file in Directory.EnumerateFiles(_folder, "*.json"))
        {
            try
            {
                var text = File.ReadAllText(file);
                var preset = JsonSerializer.Deserialize<ZonePreset>(text, Opts);
                if (preset == null) continue;
                // Prefer the on-disk GUID over any embedded Id, so file identity is authoritative.
                var fileId = Path.GetFileNameWithoutExtension(file);
                if (Guid.TryParse(fileId, out var g)) preset.Id = g;
                else preset.Id = Guid.NewGuid();
                results.Add(preset);
            }
            catch
            {
                // Skip corrupted files silently — never break the load dialog for one bad file.
            }
        }
        return results
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>True if any preset already uses this name (case-insensitive).</summary>
    public bool ExistsByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        return LoadAll().Any(p => string.Equals(p.Name?.Trim(), name.Trim(), StringComparison.CurrentCultureIgnoreCase));
    }

    /// <summary>Find an existing preset by display name (case-insensitive). Returns null if not found.</summary>
    public ZonePreset? FindByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return LoadAll().FirstOrDefault(p =>
            string.Equals(p.Name?.Trim(), name.Trim(), StringComparison.CurrentCultureIgnoreCase));
    }

    /// <summary>
    /// Suggests the next default name like "预设1" / "Preset 1" by scanning existing presets' trailing numbers.
    /// If no presets exist, returns "预设1" / "Preset 1". Otherwise returns the next integer.
    /// </summary>
    public string SuggestNextName()
    {
        var cn = LocalizationService.Instance.CurrentLanguage == Services.Language.Chinese;
        var prefix = cn ? "预设" : "Preset ";
        var pattern = cn ? @"预设\s*(?<num>\d+)" : @"Preset\s+(?<num>\d+)";
        var regex = new Regex(pattern, RegexOptions.IgnoreCase);

        int max = 0;
        foreach (var p in LoadAll())
        {
            var match = regex.Match(p.Name ?? "");
            if (match.Success && int.TryParse(match.Groups["num"].Value, out int n))
                max = Math.Max(max, n);
        }
        return $"{prefix}{max + 1}";
    }

    /// <summary>
    /// Saves a preset with the given display name.
    /// - If a preset with the same name exists, overwrites its file in place (preserves its GUID).
    /// - Otherwise creates a new GUID-named file.
    /// Returns the saved preset (with its Id populated).
    /// </summary>
    public ZonePreset Save(string name, Zone zone)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Preset name cannot be empty.", nameof(name));

        Directory.CreateDirectory(_folder);

        var existing = FindByName(name);
        var id = existing?.Id ?? Guid.NewGuid();
        var preset = new ZonePreset
        {
            Id = id,
            Name = name.Trim(),
            CreatedAt = existing?.CreatedAt ?? DateTime.Now,
            Zone = zone.Clone()
        };

        var path = Path.Combine(_folder, $"{id}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(preset, Opts));
        return preset;
    }

    /// <summary>Deletes a preset by Id. No-op if not found.</summary>
    public void Delete(Guid id)
    {
        var path = Path.Combine(_folder, $"{id}.json");
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
