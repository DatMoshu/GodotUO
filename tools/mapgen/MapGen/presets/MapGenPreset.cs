using System.Reflection;
using System.Text.Json;
using CentrED.MapGen.Pipeline;

namespace CentrED.MapGen.Presets;

// Parameter override sheet for an entire pipeline. JSON shape:
// {
//   "name": "Archipelago",
//   "description": "Many small islands, sparse forest, low road density.",
//   "seed": 42,
//   "passes": {
//     "Noise Height":      { "ContinentFalloff": 0.85, "Octaves": 6, "BaseFrequency": 0.006 },
//     "Biome Assign":      { "SeaLevelZ": 4 },
//     "Forest Scatter":    { "DensityMultiplier": 0.5 },
//     "Map Validator":     { "EdgeBandWidth": 12 }
//   },
//   "disable_passes": ["Hydraulic Erosion"]
// }
public sealed class MapGenPreset
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public long? Seed { get; set; }
    public Dictionary<string, JsonElement> Passes { get; set; } = new();
    public List<string> DisablePasses { get; set; } = new();

    /// <summary>
    /// Problems found by the last <see cref="ApplyTo"/>: unknown pass names, unknown or
    /// read-only parameter keys, values of the wrong type. Nothing is thrown; callers print
    /// these (PipelineFactory.ApplyPreset copies them into its warnings list, with a "did you mean" hint).
    /// </summary>
    public List<string> Warnings { get; } = new();

    // Passes that classify water BEFORE Biome Assign rebases heights, so they need the
    // terrain-domain threshold Biome Assign classifies with. Kept in sync automatically
    // unless the preset sets them explicitly.
    private static readonly string[] PreRebaseSeaLevelPasses = { "Moisture & Climate", "Hydraulic Erosion" };

    public static MapGenPreset Load(string path)
    {
        using var fs = File.OpenRead(path);
        using var doc = JsonDocument.Parse(fs);
        var root = doc.RootElement;
        var p = new MapGenPreset
        {
            Name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : Path.GetFileNameWithoutExtension(path),
            Description = root.TryGetProperty("description", out var d) ? d.GetString() : null,
            Seed = root.TryGetProperty("seed", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : null,
        };
        if (root.TryGetProperty("disable_passes", out var dp) && dp.ValueKind == JsonValueKind.Array)
            foreach (var x in dp.EnumerateArray())
                if (x.GetString() is { } pn) p.DisablePasses.Add(pn);
        if (root.TryGetProperty("passes", out var pp) && pp.ValueKind == JsonValueKind.Object)
            foreach (var kv in pp.EnumerateObject())
                p.Passes[kv.Name] = kv.Value.Clone();
        return p;
    }

    // Apply this preset to a list of pipeline steps. Resets every step to its as-coded
    // defaults FIRST (fresh CreateDefaultParams() + Enabled=true), then disables passes
    // named in DisablePasses, then overlays any per-pass overrides. The reset is essential:
    // without it, switching from preset A to preset B leaves any of A's overrides that B
    // doesn't mention — a silent stale-state hazard for anyone iterating.
    //
    // Note: this rebinds step.Parameters to a NEW object reference. Any caller that caches
    // step.Parameters (e.g. BiomeLabWindow._biomeParams) must re-resolve those references
    // after ApplyTo.
    public void ApplyTo(IList<PipelineStep> steps)
    {
        Warnings.Clear();
        foreach (var name in Passes.Keys)
            if (!steps.Any(s => s.Pass.Name == name))
                Warnings.Add($"preset '{Name}': unknown pass \"{name}\" (ignored)");
        foreach (var name in DisablePasses)
            if (!steps.Any(s => s.Pass.Name == name))
                Warnings.Add($"preset '{Name}': disable_passes entry \"{name}\" matches no pass (ignored)");
        foreach (var step in steps)
        {
            step.Parameters = step.Pass.CreateDefaultParams();
            // Reset to the step's as-built default Enabled state (DefaultPipeline's
            // DefaultDisabled set), NOT blanket-true. Otherwise applying any preset
            // silently enables every default-disabled pass — e.g. running the
            // archipelago preset would also bake a giant maze on top because
            // "Maze Stamp" isn't in archipelago.preset.json's disable_passes.
            step.Enabled = step.DefaultEnabled;
        }
        foreach (var step in steps)
        {
            if (DisablePasses.Contains(step.Pass.Name))
                step.Enabled = false;
            if (!Passes.TryGetValue(step.Pass.Name, out var overrides)) continue;
            // A preset that supplies parameter overrides for a default-disabled
            // pass is opting in to that pass — flip it on. Lets maze.preset.json
            // enable "Maze Stamp" purely by listing it under "passes".
            if (!DisablePasses.Contains(step.Pass.Name))
                step.Enabled = true;
            ApplyOverrides(step.Parameters, overrides, $"preset '{Name}': \"{step.Pass.Name}\"", Warnings);
        }
        PropagateSeaLevel(steps);
        PropagateEdgeBand(steps);
    }

    // One edge band: Biome Assign cuts it early (so the coast passes shape it); it follows
    // the Map Validator's EdgeBandWidth unless this preset set Biome Assign's explicitly.
    private void PropagateEdgeBand(IList<PipelineStep> steps)
    {
        var validator = steps.FirstOrDefault(s => s.Pass.Name == "Map Validator");
        var biome = steps.FirstOrDefault(s => s.Pass.Name == "Biome Assign");
        if (validator is null || biome is null) return;
        if (!Passes.TryGetValue("Map Validator", out var vov) || vov.ValueKind != JsonValueKind.Object || !vov.TryGetProperty("EdgeBandWidth", out _))
            return;
        if (Passes.TryGetValue("Biome Assign", out var bov) && bov.ValueKind == JsonValueKind.Object && bov.TryGetProperty("EdgeBandWidth", out _))
            return;
        var src = validator.Parameters.GetType().GetProperty("EdgeBandWidth");
        var dst = biome.Parameters.GetType().GetProperty("EdgeBandWidth");
        if (src is null || dst is null) return;
        dst.SetValue(biome.Parameters, src.GetValue(validator.Parameters));
    }

    // One sea level: copy Biome Assign's SeaLevelZ into the pre-rebase passes that have
    // their own copy, unless this preset set theirs explicitly.
    private void PropagateSeaLevel(IList<PipelineStep> steps)
    {
        var biome = steps.FirstOrDefault(s => s.Pass.Name == "Biome Assign");
        var seaProp = biome?.Parameters.GetType().GetProperty("SeaLevelZ");
        if (biome is null || seaProp is null) return;
        var sea = seaProp.GetValue(biome.Parameters);
        foreach (var step in steps)
        {
            if (!PreRebaseSeaLevelPasses.Contains(step.Pass.Name)) continue;
            if (Passes.TryGetValue(step.Pass.Name, out var ov) && ov.ValueKind == JsonValueKind.Object && ov.TryGetProperty("SeaLevelZ", out _))
                continue;
            step.Parameters.GetType().GetProperty("SeaLevelZ")?.SetValue(step.Parameters, sea);
        }
    }

    private static void ApplyOverrides(object parameters, JsonElement el, string where, List<string> warnings)
    {
        if (el.ValueKind != JsonValueKind.Object) return;
        var t = parameters.GetType();
        foreach (var kv in el.EnumerateObject())
        {
            var prop = t.GetProperty(kv.Name, BindingFlags.Public | BindingFlags.Instance);
            if (prop is null || !prop.CanWrite)
            {
                warnings.Add($"{where} has no parameter \"{kv.Name}\" (ignored)");
                continue;
            }
            try
            {
                if (prop.PropertyType == typeof(int))    prop.SetValue(parameters, kv.Value.GetInt32());
                else if (prop.PropertyType == typeof(double)) prop.SetValue(parameters, kv.Value.GetDouble());
                else if (prop.PropertyType == typeof(bool))   prop.SetValue(parameters, kv.Value.GetBoolean());
                else if (prop.PropertyType == typeof(string)) prop.SetValue(parameters, kv.Value.GetString());
                else if (prop.PropertyType.IsEnum)
                {
                    // Enums accept either a JSON string ("VerticalStrait") or int index.
                    object? enumVal = kv.Value.ValueKind switch
                    {
                        JsonValueKind.String when kv.Value.GetString() is { } s
                            && Enum.TryParse(prop.PropertyType, s, ignoreCase: true, out var parsed) => parsed,
                        JsonValueKind.Number => Enum.ToObject(prop.PropertyType, kv.Value.GetInt32()),
                        _ => null
                    };
                    if (enumVal is not null) prop.SetValue(parameters, enumVal);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException or ArgumentException)
            {
                warnings.Add($"{where}.{kv.Name}: invalid value {kv.Value.GetRawText()} ({ex.Message})");
            }
        }
    }
}

public static class MapGenPresetLoader
{
    public static List<MapGenPreset> LoadFromDirectory(string dir)
    {
        var result = new List<MapGenPreset>();
        if (!Directory.Exists(dir)) return result;
        foreach (var path in Directory.EnumerateFiles(dir, "*.preset.json"))
        {
            try { result.Add(MapGenPreset.Load(path)); }
            catch { /* skip corrupt */ }
        }
        return result;
    }
}
