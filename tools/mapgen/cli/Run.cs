using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using CentrED.MapGen;
using CentrED.MapGen.IR;
using CentrED.MapGen.Pipeline;

namespace GuoMapGen;

/// <summary>The settings a run was made with; written to run.json and read back by export.</summary>
public sealed record RunSettings(string PresetId, long Seed, int Width, int Height, bool Fast,
    IReadOnlyList<string> Sets, IReadOnlyList<string> Disable, IReadOnlyList<string> Enable, string? Brushes = null);

/// <summary>
/// <c>run</c>: generate one map into a fresh folder. Emits one JSON line per pass
/// (<c>{"event":"pass",...}</c>) and a final <c>{"event":"done",...}</c>.
/// </summary>
public static class RunCommand
{
    public const int MaxWidth = 7168, MaxHeight = 4096;

    public static int Execute(Args a, JsonEmitter json)
    {
        var outDir = a.Get("out") ?? throw new CliError("run needs --out DIR (a new or empty folder)");
        int size = a.Int("size", 1024);
        int width = a.Int("width", size), height = a.Int("height", size);
        if (width % 8 != 0 || height % 8 != 0) throw new CliError($"map size {width}x{height}: both sides must be multiples of 8");
        if (width < 64 || height < 64 || width > MaxWidth || height > MaxHeight)
            throw new CliError($"map size {width}x{height}: each side must be 64..{MaxWidth}x{MaxHeight}");

        var (preset, presetPath) = Presets.Load(a.Get("preset"));
        string presetId = Presets.IdOf(presetPath);
        long seed = a.Long("seed") ?? preset.Seed ?? 1234567;
        var settings = new RunSettings(presetId, seed, width, height, a.Flag("fast"),
            a.All("set").ToList(), a.All("disable").ToList(), a.All("enable").ToList(), a.Get("brushes"));

        OutputFolder.PrepareFresh(outDir);
        var warnings = new List<string>();
        var steps = Pipeline.Build(preset, settings, warnings);

        json.Event("start", new()
        {
            ["preset"] = presetId, ["seed"] = seed, ["width"] = width, ["height"] = height,
            ["passes"] = steps.Count, ["enabled"] = steps.Count(s => s.Enabled), ["out"] = Path.GetFullPath(outDir),
        });

        var radar = RadarColors.Load(a.Get("client-data"));
        if (radar is null) warnings.Add("radarcol.mul not found (set UO_CLIENT_DATA or --client-data): previews use biome colours");
        int previewMax = a.Int("preview-max", 1024);
        bool stepPreviews = a.Flag("step-previews");

        var total = Stopwatch.StartNew();
        var ir = Pipeline.Run(preset, settings, steps, warnings, (i, step, ms, irNow) =>
        {
            string? preview = null;
            if (stepPreviews && step.Enabled)
            {
                preview = $"steps/{i:D2}-{Slug(step.Pass.Name)}.png";
                Render.Best(irNow, radar).Save(Path.Combine(outDir, preview), previewMax);
            }
            var report = irNow.Reports.GetValueOrDefault(step.Pass.Name);
            json.Event("pass", new()
            {
                ["index"] = i, ["count"] = steps.Count, ["name"] = step.Pass.Name, ["enabled"] = step.Enabled,
                ["ms"] = ms, ["preview"] = preview,
                ["notes"] = step.Enabled ? report?.Notes.Take(5).ToList() : null,
                ["warnings"] = step.Enabled ? report?.Warnings.Take(5).ToList() : null,
            });
        });

        var files = new Dictionary<string, string>();
        Render.Best(ir, radar).Save(Path.Combine(outDir, "radar.png"), previewMax); files["radar"] = "radar.png";
        Render.Biome(ir).Save(Path.Combine(outDir, "biome.png"), previewMax); files["biome"] = "biome.png";
        Render.Height(ir).Save(Path.Combine(outDir, "height.png"), previewMax); files["height"] = "height.png";
        MapDump.Write(ir, Path.Combine(outDir, "map.bin")); files["dump"] = "map.bin";
        File.WriteAllText(Path.Combine(outDir, "preset.json"),
            PipelineFactory.ExportPreset(steps, preset.Name, preset.Description, seed));
        files["preset"] = "preset.json";
        // Points of interest, towns with their footprint and gates: what tools placing content on the
        // map (tools/mapgen_districts) read. Map cells, inclusive rectangles.
        var pois = ir.Pois.Select(p => new Dictionary<string, object?>
        {
            ["id"] = p.Id, ["kind"] = p.Kind.ToString(), ["x"] = p.X, ["y"] = p.Y, ["z"] = p.Z, ["tag"] = p.Tag,
            ["footprint"] = p.Footprint is { } f ? new[] { (int)f.X1, f.Y1, f.X2, f.Y2 } : null,
            ["gates"] = p.Gates?.Select(g => new[] { (int)g.X, g.Y }).ToList(),
        }).ToList();
        File.WriteAllText(Path.Combine(outDir, "pois.json"), JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["schema"] = "guo.mapgen.pois/1", ["width"] = width, ["height"] = height, ["pois"] = pois,
        }, new JsonSerializerOptions { WriteIndented = true }));
        files["pois"] = "pois.json";

        var stats = Likeness.Measure(ir);
        string hash = MapHash.Of(ir);
        var passes = steps.Select((s, i) => new Dictionary<string, object?>
        {
            ["index"] = i, ["name"] = s.Pass.Name, ["enabled"] = s.Enabled,
            ["ms"] = ir.Reports.TryGetValue(s.Pass.Name, out var r) ? (long?)r.Elapsed.TotalMilliseconds : null,
            ["warnings"] = ir.Reports.TryGetValue(s.Pass.Name, out var r2) ? r2.Warnings.Count : 0,
        }).ToList();

        var runJson = new Dictionary<string, object?>
        {
            ["schema"] = "guo.mapgen.run/1",
            ["generator"] = GeneratorInfo.Describe(),
            ["preset"] = presetId, ["seed"] = seed, ["width"] = width, ["height"] = height,
            ["fast"] = settings.Fast, ["sets"] = settings.Sets, ["disable"] = settings.Disable, ["enable"] = settings.Enable,
            ["brushes"] = settings.Brushes ?? "guo", ["brush_table"] = ir.Brushes.Source,
            ["hash"] = hash, ["elapsed_ms"] = total.ElapsedMilliseconds,
            ["statics"] = ir.StaticOps.Count(o => o.Kind == StaticOpKind.Add),
            ["stats"] = stats, ["files"] = files, ["passes"] = passes, ["warnings"] = warnings,
            ["radar_colours"] = radar is not null,
        };
        File.WriteAllText(Path.Combine(outDir, "run.json"), JsonSerializer.Serialize(runJson, new JsonSerializerOptions { WriteIndented = true }));

        json.Event("done", new()
        {
            ["hash"] = hash, ["elapsed_ms"] = total.ElapsedMilliseconds, ["out"] = Path.GetFullPath(outDir),
            ["stats"] = stats, ["warnings"] = warnings,
        });
        return 0;
    }

    public static string Slug(string name) =>
        new string(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
}

/// <summary>Builds and runs the step list the same way for run and export, so a re-run reproduces the hash.</summary>
public static class Pipeline
{
    public static List<PipelineStep> Build(CentrED.MapGen.Presets.MapGenPreset preset, RunSettings s, List<string> warnings)
    {
        var steps = PipelineFactory.BuildSteps(new PipelineOptions
        {
            Preset = preset,
            Disable = s.Disable,
            DisableHeavyPasses = s.Fast,
            AllowFileSideEffects = false, // the generator never writes spawn files from GUO
        }, warnings);

        foreach (var name in s.Enable)
            Find(steps, name).Enabled = true;
        foreach (var set in s.Sets)
            ApplySet(steps, set);
        // An explicit --enable must not switch a side-effect pass back on.
        PipelineFactory.ApplySideEffectGate(steps, allow: false, spawnOutputDir: null, warnings);
        return steps;
    }

    public static GenIR Run(CentrED.MapGen.Presets.MapGenPreset preset, RunSettings s, List<PipelineStep> steps,
        List<string> warnings, Action<int, PipelineStep, long, GenIR>? afterStep = null)
    {
        var ir = PipelineFactory.CreateIR((ushort)s.Width, (ushort)s.Height,
            PipelineFactory.FullScope(s.Width, s.Height), unchecked((ulong)s.Seed),
            new PipelineDataOptions { BrushesPath = s.Brushes }, warnings);
        var runner = new PipelineRunner();
        for (int i = 0; i < steps.Count; i++)
        {
            var sw = Stopwatch.StartNew();
            if (steps[i].Enabled) runner.RunRange(ir, steps, i, i + 1);
            afterStep?.Invoke(i, steps[i], sw.ElapsedMilliseconds, ir);
        }
        return ir;
    }

    public static PipelineStep Find(List<PipelineStep> steps, string name)
    {
        var key = PipelineFactory.NormalizePassName(name);
        return steps.FirstOrDefault(s => PipelineFactory.NormalizePassName(s.Pass.Name) == key)
            ?? throw new CliError($"no pass named '{name}'");
    }

    /// <summary>"Pass Name.Key=value": sets one int/double/bool/string/enum parameter.</summary>
    public static void ApplySet(List<PipelineStep> steps, string set)
    {
        int eq = set.IndexOf('=');
        int dot = eq < 0 ? -1 : set.LastIndexOf('.', eq);
        if (eq < 0 || dot < 0) throw new CliError($"--set '{set}': expected \"Pass Name.Key=value\"");
        var step = Find(steps, set[..dot]);
        string key = set[(dot + 1)..eq], raw = set[(eq + 1)..];
        var prop = step.Parameters.GetType().GetProperty(key, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
            ?? throw new CliError($"--set '{set}': pass '{step.Pass.Name}' has no parameter '{key}'");
        if (!prop.CanWrite) throw new CliError($"--set '{set}': '{key}' is read-only");
        var t = prop.PropertyType;
        object value;
        try
        {
            value = t == typeof(int) ? int.Parse(raw, CultureInfo.InvariantCulture)
                : t == typeof(double) ? double.Parse(raw, CultureInfo.InvariantCulture)
                : t == typeof(bool) ? bool.Parse(raw)
                : t == typeof(string) ? raw
                : t.IsEnum ? Enum.Parse(t, raw, ignoreCase: true)
                : throw new CliError($"--set '{set}': '{key}' is a {t.Name}, which presets cannot set");
        }
        catch (FormatException) { throw new CliError($"--set '{set}': '{raw}' is not a valid {SchemaCommand.TypeName(t)}"); }
        catch (ArgumentException) { throw new CliError($"--set '{set}': '{raw}' is not one of {string.Join(", ", Enum.GetNames(t))}"); }
        prop.SetValue(step.Parameters, value);
    }
}

/// <summary>Output folders are always fresh: a new folder, or an existing empty one.</summary>
public static class OutputFolder
{
    public static void PrepareFresh(string dir)
    {
        if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any())
            throw new CliError($"output folder is not empty: {dir}. Each run writes to a fresh folder.");
        var client = Environment.GetEnvironmentVariable("UO_CLIENT_DATA");
        if (!string.IsNullOrWhiteSpace(client) && IsInside(dir, client))
            throw new CliError("output folder is inside the UO install; generated maps never go there");
        Directory.CreateDirectory(dir);
    }

    public static bool IsInside(string path, string root)
    {
        var p = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return p.StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }
}

public static class GeneratorInfo
{
    public static Dictionary<string, object?> Describe() => new()
    {
        ["name"] = "MapGen",
        ["assembly"] = typeof(PipelineFactory).Assembly.GetName().Version?.ToString(),
        ["data_dir_set"] = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(CentrED.MapGen.Data.RepoRootResolver.DataDirEnvVar)),
    };
}
