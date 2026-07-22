using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using DesktopZones.Models;

namespace DesktopZones.Services;

public class PresetService
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopZones");

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public List<ZonePreset> LoadNormal() => Load("presets_normal.json");

    public void SaveNormal(List<ZonePreset> presets) => Save("presets_normal.json", presets);

    private List<ZonePreset> Load(string file)
    {
        var path = Path.Combine(Dir, file);
        try { return File.Exists(path) ? JsonSerializer.Deserialize<List<ZonePreset>>(File.ReadAllText(path), Opts) ?? new() : new(); }
        catch { return new(); }
    }

    private void Save(string file, List<ZonePreset> presets)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(Path.Combine(Dir, file), JsonSerializer.Serialize(presets, Opts));
    }
}
