using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using DesktopZones.Models;

namespace DesktopZones.Services;

/// <summary>
/// Manages presets for any component kind (Zone / Clock / Calendar /
/// StickyNote / MergedGroup / Panel). Files live under
/// ./Presets/{subFolder}/{guid}.json. Filename uses GUID to avoid filesystem
/// restrictions on user-entered names; uniqueness is checked against the
/// JSON "name" field instead. Each preset JSON is the concrete preset POCO
/// (ZonePreset / ClockPreset / …) — strong typing on load, no payload dispatch.
/// </summary>
public class PresetService
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    private readonly string _folder;
    private readonly PresetKind _kind;

    public string Folder => _folder;
    public PresetKind Kind => _kind;

    public PresetService(PresetKind kind)
    {
        _kind = kind;
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        _folder = Path.Combine(baseDir, "Presets", PresetRecord.SubFolderFor(kind));
    }

    /// <summary>Convenience factory — returns the service for a given kind.</summary>
    public static PresetService For(PresetKind kind) => new(kind);

    // ── Load ──

    /// <summary>Load all presets of this service's kind. Returns empty list if folder missing.</summary>
    public List<PresetRecord> LoadAll()
    {
        var results = new List<PresetRecord>();
        if (!Directory.Exists(_folder)) return results;

        foreach (var file in Directory.EnumerateFiles(_folder, "*.json"))
        {
            try
            {
                var text = File.ReadAllText(file);
                var preset = DeserializeAny(text);
                if (preset == null) continue;

                // Prefer the on-disk GUID over any embedded Id, so file identity is authoritative.
                var fileId = Path.GetFileNameWithoutExtension(file);
                if (Guid.TryParse(fileId, out var g)) preset.Id = g;
                else preset.Id = Guid.NewGuid();

                // Files that don't carry a Kind (legacy Zone presets) get coerced to the
                // service's kind so the dialog + card template know what to render.
                if (preset.Kind == default) preset.Kind = _kind;

                results.Add(preset);
            }
            catch
            {
                // Skip corrupted files silently — never break the load dialog for one bad file.
            }
        }
        return results.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>
    /// Read a preset JSON and return the strongly-typed preset (ZonePreset /
    /// ClockPreset / …). Falls back to ZonePreset for legacy files that lack a
    /// Kind field — that's the only preset type that existed before v5.
    /// </summary>
    private PresetRecord? DeserializeAny(string text)
    {
        // Try probing for Kind first to dispatch. Kind is stored as an integer
        // (1, 2, 3, …) in the JSON; GetString() throws on a number, so we read
        // the raw value and parse it ourselves. Without this fix, Clock/Calendar/
        // StickyNote/Panel/MergedGroup presets get mis-cast as ZonePreset.
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("Kind", out var kindProp))
            {
                int kindInt = kindProp.ValueKind switch
                {
                    JsonValueKind.Number => kindProp.GetInt32(),
                    JsonValueKind.String => int.TryParse(kindProp.GetString(), out var n) ? n : -1,
                    _ => -1
                };
                if (Enum.IsDefined(typeof(PresetKind), kindInt))
                {
                    var kind = (PresetKind)kindInt;
                    return kind switch
                    {
                        PresetKind.Zone => JsonSerializer.Deserialize<ZonePreset>(text, Opts),
                        PresetKind.Clock => JsonSerializer.Deserialize<ClockPreset>(text, Opts),
                        PresetKind.Calendar => JsonSerializer.Deserialize<CalendarPreset>(text, Opts),
                        PresetKind.StickyNote => JsonSerializer.Deserialize<StickyNotePreset>(text, Opts),
                        PresetKind.MergedGroup => JsonSerializer.Deserialize<MergedGroupPreset>(text, Opts),
                        PresetKind.Panel => JsonSerializer.Deserialize<PanelPreset>(text, Opts),
                        _ => JsonSerializer.Deserialize<ZonePreset>(text, Opts)
                    };
                }
            }
        }
        catch { /* fall through to legacy parse */ }

        // Legacy Zone preset (no Kind field). The pre-v5 schema is exactly the
        // ZonePreset shape, so deserialize as ZonePreset directly.
        return JsonSerializer.Deserialize<ZonePreset>(text, Opts);
    }

    // ── Save / Delete ──

    /// <summary>True if any preset already uses this name (case-insensitive).</summary>
    public bool ExistsByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        return LoadAll().Any(p => string.Equals(p.Name?.Trim(), name.Trim(), StringComparison.CurrentCultureIgnoreCase));
    }

    /// <summary>Find an existing preset by display name (case-insensitive). Returns null if not found.</summary>
    public PresetRecord? FindByName(string name)
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
        // Use the existing "Preset.DefaultNamePrefix" translation key for both the output and
        // the matching regex. Regex.Escape handles the trailing space safely.
        var loc = LocalizationService.Instance;
        var prefix = loc["Preset.DefaultNamePrefix"];
        var regex = new Regex(Regex.Escape(prefix) + @"(?<num>\d+)", RegexOptions.IgnoreCase);

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
    /// Save a preset of this service's kind. Caller passes the typed payload
    /// (Zone / DesktopClock / …); we wrap it into the right preset POCO.
    /// If a preset with the same name exists, overwrites its file in place
    /// (preserves its GUID).
    /// </summary>
    public PresetRecord Save(string name, object payload)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Preset name cannot be empty.", nameof(name));

        Directory.CreateDirectory(_folder);

        var existing = FindByName(name);
        var id = existing?.Id ?? Guid.NewGuid();
        var trimmedName = name.Trim();
        var createdAt = existing?.CreatedAt ?? DateTime.Now;

        PresetRecord preset = _kind switch
        {
            PresetKind.Zone => new ZonePreset { Id = id, Name = trimmedName, CreatedAt = createdAt, Zone = ((Zone)payload).Clone() },
            PresetKind.Clock => new ClockPreset { Id = id, Name = trimmedName, CreatedAt = createdAt, Clock = ((DesktopClock)payload).Clone() },
            PresetKind.Calendar => new CalendarPreset { Id = id, Name = trimmedName, CreatedAt = createdAt, Calendar = ((DesktopCalendar)payload).Clone() },
            PresetKind.StickyNote => BuildStickyNotePreset(id, trimmedName, createdAt, (StickyNote)payload),
            PresetKind.MergedGroup => new MergedGroupPreset { Id = id, Name = trimmedName, CreatedAt = createdAt, Zone = ((Zone)payload).Clone() },
            PresetKind.Panel => new PanelPreset { Id = id, Name = trimmedName, CreatedAt = createdAt, Config = ((PanelPresetConfig)payload).Clone() },
            _ => throw new InvalidOperationException($"Unknown preset kind {_kind}")
        };
        preset.Kind = _kind;

        var path = Path.Combine(_folder, $"{id}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(preset, preset.GetType(), Opts));
        return preset;
    }

    /// <summary>
    /// StickyNote preset should NOT bake the user's working text into the preset
    /// (presets describe appearance/style, not content). Strip Content before save.
    /// </summary>
    private static StickyNotePreset BuildStickyNotePreset(Guid id, string name, DateTime createdAt, StickyNote note)
    {
        var clone = note.Clone();
        clone.Content = "";
        return new StickyNotePreset { Id = id, Name = name, CreatedAt = createdAt, Note = clone };
    }

    /// <summary>Deletes a preset by Id. No-op if not found.</summary>
    public void Delete(Guid id)
    {
        var path = Path.Combine(_folder, $"{id}.json");
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    /// <summary>Extract the typed payload out of a preset record. Caller casts as needed.</summary>
    public static object GetPayload(PresetRecord record) => record switch
    {
        ZonePreset z => z.Zone,
        ClockPreset c => c.Clock,
        CalendarPreset c => c.Calendar,
        StickyNotePreset s => s.Note,
        MergedGroupPreset m => m.Zone,
        PanelPreset p => p.Config,
        _ => throw new InvalidOperationException($"Unknown preset type {record.GetType().Name}")
    };
}