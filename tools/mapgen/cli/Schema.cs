using System.Reflection;
using CentrED.MapGen;
using CentrED.MapGen.Pipeline;
using CentrED.MapGen.Presets;

namespace GuoMapGen;

/// <summary>Preset lookup shared by every command. The default is the Felucca-like candidate.</summary>
public static class Presets
{
    public const string Default = "felucca-stage18";

    public static List<string> List() => PipelineFactory.ListPresetIds();

    public static (MapGenPreset Preset, string Path) Load(string? nameOrPath)
    {
        var name = string.IsNullOrWhiteSpace(nameOrPath) ? Default : nameOrPath;
        var path = PipelineFactory.ResolvePresetPath(name);
        if (!File.Exists(path)) throw new CliError($"preset not found: {name}");
        return (MapGenPreset.Load(path), path);
    }
}

/// <summary>
/// <c>schema</c>: every pass with its on/off state and every tunable parameter, built from the
/// passes' own [TunableDisplay]/[TunableRange]/[TunableTileSet] attributes so a UI never
/// hand-writes a field.
/// </summary>
public static class SchemaCommand
{
    public static Dictionary<string, object?> Build(string? presetName)
    {
        var (preset, presetPath) = Presets.Load(presetName);
        var warnings = new List<string>();
        var steps = PipelineFactory.BuildSteps(new PipelineOptions { Preset = preset, AllowFileSideEffects = false }, warnings);

        var passes = new List<object?>();
        for (int i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            var defaults = step.Pass.CreateDefaultParams();
            passes.Add(new Dictionary<string, object?>
            {
                ["index"] = i,
                ["name"] = step.Pass.Name,
                ["category"] = step.Pass.Category,
                ["enabled"] = step.Enabled,
                ["default_enabled"] = step.DefaultEnabled,
                ["heavy"] = PipelineFactory.HeavyPassNames.Contains(step.Pass.Name),
                ["file_side_effects"] = PipelineFactory.FileSideEffectPassNames.Contains(step.Pass.Name),
                ["tunables"] = Tunables(step.Parameters, defaults),
            });
        }

        return new Dictionary<string, object?>
        {
            ["schema"] = "guo.mapgen.schema/1",
            ["preset"] = new Dictionary<string, object?>
            {
                ["id"] = Path.GetFileName(presetPath)[..^".preset.json".Length],
                ["name"] = preset.Name,
                ["description"] = preset.Description,
                ["seed"] = preset.Seed,
            },
            ["presets"] = Presets.List(),
            ["default_preset"] = Presets.Default,
            ["sizes"] = Sizes,
            ["passes"] = passes,
            ["warnings"] = warnings,
        };
    }

    /// <summary>Map sizes the UI offers: square test sizes and the client's facet sizes.</summary>
    public static readonly object[] Sizes =
    {
        new[] { 256, 256 }, new[] { 512, 512 }, new[] { 1024, 1024 }, new[] { 2048, 2048 },
        new[] { 6144, 4096 }, new[] { 7168, 4096 }, new[] { 2304, 1600 }, new[] { 2560, 2048 },
        new[] { 1448, 1448 }, new[] { 1280, 4096 },
    };

    public static List<object?> Tunables(object parameters, object defaults)
    {
        var list = new List<object?>();
        foreach (var prop in parameters.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanRead) continue;
            var display = prop.GetCustomAttribute<TunableDisplayAttribute>();
            var range = prop.GetCustomAttribute<TunableRangeAttribute>();
            var tileSet = prop.GetCustomAttribute<TunableTileSetAttribute>();
            var t = prop.PropertyType;
            string type = TypeName(t);
            var entry = new Dictionary<string, object?>
            {
                ["key"] = prop.Name,
                ["label"] = display?.Label ?? prop.Name,
                ["tooltip"] = display?.Tooltip,
                ["type"] = type,
                ["editable"] = prop.CanWrite && type != "other",
                ["value"] = Value(prop.GetValue(parameters)),
                ["default"] = Value(prop.GetValue(defaults)),
            };
            if (range is not null) { entry["min"] = range.Min; entry["max"] = range.Max; }
            if (tileSet is not null) entry["tile_set"] = tileSet.Category;
            if (t.IsEnum) entry["options"] = Enum.GetNames(t);
            list.Add(entry);
        }
        return list;
    }

    public static string TypeName(Type t) =>
        t == typeof(int) ? "int"
        : t == typeof(double) ? "double"
        : t == typeof(bool) ? "bool"
        : t == typeof(string) ? "string"
        : t.IsEnum ? "enum"
        : "other";

    public static object? Value(object? v) => v switch
    {
        null => null,
        int or double or bool or string => v,
        Enum e => e.ToString(),
        _ => v.ToString(),
    };
}
