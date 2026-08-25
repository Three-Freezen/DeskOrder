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
                        PresetKind.MergedGroup => MigrateMergedGroupPreset(JsonSerializer.Deserialize<MergedGroupPreset>(text, Opts)),
                        PresetKind.Panel => JsonSerializer.Deserialize<PanelPreset>(text, Opts),
                        PresetKind.Subfolder => JsonSerializer.Deserialize<SubfolderPreset>(text, Opts),
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

    // ── One-time migration of legacy MergedGroup* flat fields ──
    // ponytail: lift legacy MergedGroup flat fields to nested POCO schema; remove after one release cycle
    // Old presets store MergedGroupBorderColor / MergedSubZoneIds / … at the
    // Zone level. After the refactor (MergedGroupStyle + MergedGroupMembership
    // POCOs) System.Text.Json drops them into Zone.ExtensionData. Lift them
    // back into the nested POCOs once, then clear ExtensionData so subsequent
    // saves don't rewrite the stale flat keys.
    private static MergedGroupPreset? MigrateMergedGroupPreset(MergedGroupPreset? p)
    {
        if (p == null) return null;
        var d = p.Zone.ExtensionData;
        if (d == null || d.Count == 0) return p;

        var s = p.Zone.MergedGroupStyle;
        var m = p.Zone.MergedGroupMembership;
        bool any = false;

        void MoveString(string key, Action<string> setter)
        {
            if (!d.TryGetValue(key, out var el) || el.ValueKind != JsonValueKind.String) return;
            setter(el.GetString() ?? "");
            d.Remove(key); any = true;
        }
        void MoveDouble(string key, Action<double> setter)
        {
            if (!d.TryGetValue(key, out var el)) return;
            try { setter(el.GetDouble()); d.Remove(key); any = true; } catch { }
        }
        void MoveInt(string key, Action<int> setter)
        {
            if (!d.TryGetValue(key, out var el)) return;
            try { setter(el.GetInt32()); d.Remove(key); any = true; } catch { }
        }
        void MoveBool(string key, Action<bool> setter)
        {
            if (!d.TryGetValue(key, out var el)) return;
            try { setter(el.GetBoolean()); d.Remove(key); any = true; } catch { }
        }

        // Style
        MoveString("MergedGroupBorderColor",                v => s.BorderColor = v);
        MoveDouble("MergedGroupBorderThickness",            v => s.BorderThickness = v);
        MoveInt   ("MergedGroupCornerRadius",               v => s.CornerRadius = v);
        MoveString("MergedGroupFillColor",                  v => s.FillColor = v);
        MoveString("MergedGroupTitleBarFillColor",          v => s.TitleBarFillColor = v);
        MoveString("MergedGroupTitleTextColor",             v => s.TitleTextColor = v);
        MoveString("MergedGroupIconColor",                  v => s.IconColor = v);
        MoveDouble("MergedGroupControlOpacity",             v => s.ControlOpacity = v);
        MoveDouble("MergedGroupTitleBarOpacity",            v => s.TitleBarOpacity = v);
        MoveBool  ("MergedGroupUseUnifiedFill",             v => s.UseUnifiedFill = v);
        MoveBool  ("MergedGroupQuickBarMode",               v => s.TileMode = v);
        MoveBool  ("MergedGroupTitleBarTextColorAdaptive",  v => s.TitleBarTextColorAdaptive = v);
        MoveString("MergedGroupBackgroundImagePath",        v => s.BackgroundImagePath = v);
        MoveString("MergedGroupBgImageStretch",             v => s.BgImageStretch = v);
        MoveDouble("MergedGroupBgImageOffsetX",             v => s.BgImageOffsetX = v);
        MoveDouble("MergedGroupBgImageOffsetY",             v => s.BgImageOffsetY = v);
        MoveDouble("MergedGroupBgImageZoom",                v => s.BgImageZoom = v);
        MoveDouble("MergedGroupBackgroundImageOpacity",     v => s.BackgroundImageOpacity = v);

        // Membership
        if (d.TryGetValue("MergedGroupId", out var idEl))
        {
            m.GroupId = idEl.ValueKind == JsonValueKind.Null ? (Guid?)null : idEl.TryGetGuid(out var g) ? g : (Guid?)null;
            d.Remove("MergedGroupId"); any = true;
        }
        if (d.TryGetValue("MergedSubZoneIds", out var idsEl))
        {
            var ids = new List<Guid>();
            foreach (var item in idsEl.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String && Guid.TryParse(item.GetString(), out var g)) ids.Add(g);
            m.SubZoneIds = ids;
            d.Remove("MergedSubZoneIds"); any = true;
        }
        MoveString("MergedGroupName", v => m.DisplayName = v);
        MoveString("MergedGroupIcon", v => m.Icon = v);

        if (any) p.Zone.ExtensionData = d.Count > 0 ? d : null;
        return p;
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
            // ponytail 2026-08-26: Q8 — SubFolder presets describe appearance only,
            // not content. Strip SubItems before persisting (the dedicated
            // SaveSubfolderPreset convenience method already did this; the dispatch
            // here was cloning the payload with SubItems intact).
            PresetKind.Subfolder => BuildSubfolderPreset(id, trimmedName, createdAt, (ZoneItem)payload),
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

    /// <summary>
    /// ponytail 2026-08-26: SubFolder preset should NOT bake the user's items
    /// into the preset (Q8 — presets describe style, not content). Strip SubItems
    /// before save. Mirrors SaveSubfolderPreset's body; that method now goes
    /// through Save → BuildSubfolderPreset so the dispatch and the convenience
    /// path agree.
    /// </summary>
    private static SubfolderPreset BuildSubfolderPreset(Guid id, string name, DateTime createdAt, ZoneItem subfolder)
    {
        var clone = subfolder.Clone();
        clone.SubItems.Clear();
        return new SubfolderPreset { Id = id, Name = name, CreatedAt = createdAt, Subfolder = clone };
    }

    /// <summary>Deletes a preset by Id. Returns true on success, false if the file was missing or could not be deleted.</summary>
    public bool Delete(Guid id)
    {
        var path = Path.Combine(_folder, $"{id}.json");
        try
        {
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PresetService] Delete failed for {path}: {ex}");
            return false;
        }
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
        SubfolderPreset s => s.Subfolder,
        _ => throw new InvalidOperationException($"Unknown preset type {record.GetType().Name}")
    };

    // ── Subfolder convenience API ──
    // Instance methods — caller must instantiate via PresetService.For(PresetKind.Subfolder).
    // SaveSubfolderPreset strips SubItems (Q8: presets describe style, not content).

    /// <summary>Save a SubFolder preset. SubItems are stripped before persist.</summary>
    public SubfolderPreset SaveSubfolderPreset(string name, ZoneItem subfolder)
    {
        var snapshot = subfolder.Clone();
        snapshot.SubItems.Clear(); // Q8: 不存内容
        return (SubfolderPreset)Save(name, snapshot);
    }

    /// <summary>Apply a saved SubFolder preset onto a target SubFolder. Copies the 14 SubFolder
    /// style fields + Name; SubItems and positional fields (X/Y/TargetPath/IconPath/Id/Type) are
    /// intentionally NOT touched (Q8).</summary>
    public bool ApplySubfolderPreset(Guid presetId, ZoneItem target)
    {
        var preset = LoadAll().OfType<SubfolderPreset>().FirstOrDefault(p => p.Id == presetId);
        if (preset == null) return false;
        var src = preset.Subfolder;
        target.Name = src.Name;
        target.IconSizeAutoGrow = src.IconSizeAutoGrow;
        target.CornerRounded = src.CornerRounded;
        target.FillFollowsZone = src.FillFollowsZone;
        target.FillColorOverride = src.FillColorOverride;
        target.FillOpacityOverride = src.FillOpacityOverride;
        target.BackgroundImagePath = src.BackgroundImagePath;
        target.BackgroundImageOpacity = src.BackgroundImageOpacity;
        target.EnableLiquidGlass = src.EnableLiquidGlass;
        target.GlassBlurAmount = src.GlassBlurAmount;
        target.GlassTintOpacity = src.GlassTintOpacity;
        target.GlassTintLuminosity = src.GlassTintLuminosity;
        target.GlassColorMode = src.GlassColorMode;
        target.GridSize = src.GridSize;
        target.SnapToGrid = src.SnapToGrid;
        target.AutoArrange = src.AutoArrange;
        target.HoverAnimation = src.HoverAnimation;
        target.HoverExpandSpeed = src.HoverExpandSpeed;
        target.HoverAutoExpand = src.HoverAutoExpand;
        // SubItems intentionally not touched (Q8)
        return true;
    }

    /// <summary>List all saved SubFolder presets (SubfolderPreset only).</summary>
    public IReadOnlyList<SubfolderPreset> ListSubfolderPresets()
        => LoadAll().OfType<SubfolderPreset>().ToList();

    /// <summary>Delete a SubFolder preset by Id. Returns false if the file was missing.</summary>
    public bool DeleteSubfolderPreset(Guid presetId) => Delete(presetId);
}