using System.Reflection;
using System.Text;
using System.Text.Json;
using CentrED.MapGen.Data;
using CentrED.MapGen.IR;
using CentrED.MapGen.Passes.Pois;
using CentrED.MapGen.Passes.Terrain;
using CentrED.MapGen.Presets;
using CentrED.Network;

namespace CentrED.MapGen.Pipeline;

/// <summary>Where the generator's data tables come from. Null/empty = repo default.</summary>
public sealed class PipelineDataOptions
{
    public string? TileTablesPath { get; set; }
    /// <summary>The transition table: null or "guo" (default), "guo-core", "dragon" or a file (<see cref="PipelineFactory.ResolveBrushesPath"/>).</summary>
    public string? BrushesPath { get; set; }
    public string? TreeStaticsPath { get; set; }
}

/// <summary>Everything that shapes the step list. Every front end (CLI, editor, web) builds through this.</summary>
public sealed class PipelineOptions
{
    /// <summary>Preset to overlay (already loaded). Applied before every other option.</summary>
    public MapGenPreset? Preset { get; set; }

    /// <summary>Extra pass names to switch off (case- and space-insensitive), e.g. the CLI's <c>disable=</c>.</summary>
    public IEnumerable<string>? Disable { get; set; }

    /// <summary>Image-import mode: path prefix of <c>{prefix}.terrain.png</c> + <c>{prefix}.altitude.png</c>.</summary>
    public string? ImportPrefix { get; set; }
    public int? ImportMinZ { get; set; }
    public int? ImportMaxZ { get; set; }

    /// <summary>Switch off the heavy passes (roads, stamps, scatter) for fast previews.</summary>
    public bool DisableHeavyPasses { get; set; }

    /// <summary>
    /// When false, passes that write files outside the IR (today: Dungeon Spawner Emit, which writes
    /// a spawner JSON) are switched off. Previews must pass false; only an explicit commit passes true.
    /// </summary>
    public bool AllowFileSideEffects { get; set; } = true;

    /// <summary>Explicit output folder for Dungeon Spawner Emit. Only used when <see cref="AllowFileSideEffects"/> is true.</summary>
    public string? SpawnOutputDir { get; set; }

    /// <summary>Optional POI quadrant boundaries JSON (CLI <c>quadrants=</c>).</summary>
    public string? QuadrantsPath { get; set; }

    /// <summary>Preview-safe preset: heavy passes off and no file side effects.</summary>
    public static PipelineOptions PreviewSafe(MapGenPreset? preset = null) => new()
    {
        Preset = preset,
        DisableHeavyPasses = true,
        AllowFileSideEffects = false,
    };
}

/// <summary>Source baseline files for the fast .mul writer.</summary>
public sealed record MapSourceFiles(string Dir, string? MapPath, string StaidxPath, string StaticsPath, string? Note)
{
    public bool IsComplete => MapPath is not null && File.Exists(StaidxPath) && File.Exists(StaticsPath);

    public string Describe() => IsComplete
        ? $"map={Path.GetFileName(MapPath)}" + (Note is null ? "" : $" ({Note})")
        : $"missing files in {Dir}: need map0.mul or map0LegacyMUL.uop, staidx0.mul, statics0.mul";
}

/// <summary>
/// The single place that builds a <see cref="GenIR"/> and its pipeline step list. MapGen.Cli,
/// the web stepper and the editor windows all call into this so the same seed + preset +
/// options produce the same map in every front end.
/// </summary>
public static class PipelineFactory
{
    /// <summary>Passes switched off for fast previews (Biome Lab, web randomizer, preset smoke tests).</summary>
    public static readonly IReadOnlyList<string> HeavyPassNames = new[]
    {
        "Road Graph", "Road Centerline", "Road Stamps", "POI Stamps", "Town Stamps",
        "Stamp Scatter", "Mountain Edge Statics", "Biome Static Scatter", "Forest Scatter",
    };

    /// <summary>Passes that write files outside the IR when they run.</summary>
    public static readonly IReadOnlyList<string> FileSideEffectPassNames = new[]
    {
        "Dungeon Spawner Emit",
    };

    /// <summary>Passes ImageImportPass replaces (it supplies height, biome and land ids itself).</summary>
    public static readonly IReadOnlyList<string> ImageImportReplacedPassNames = new[]
    {
        "Noise Height", "Hydraulic Erosion", "Mountain Shape", "Biome Assign", "Land ID Resolve",
    };

    public const string ImageImportPassName = "Image Import";

    // ---- default data locations (repo-relative) ----

    public const string PresetsRelPath = "tools/mapgen/MapGen/presets";
    public const string TreeStaticsRelPath = "client/ClassicUO/Data/tree-statics.json";
    /// <summary>The user's optional Dragon brush table in the generator data folder (UO_MAPGEN_DATA), built there by <c>guo-mapgen prepare --dragon</c>.</summary>
    public const string DragonBrushesRelPath = LandBrushTable.DataJsonRelativePath;

    public static string DefaultPresetsDir => RepoRootResolver.Resolve(PresetsRelPath);
    public static string DefaultTreeStaticsPath => RepoRootResolver.Resolve(TreeStaticsRelPath);

    /// <summary>Compact form used for name matching: no spaces, lower case.</summary>
    public static string NormalizePassName(string name) => name.Replace(" ", "").ToLowerInvariant();

    private static bool NameIn(string passName, IEnumerable<string> names)
    {
        var key = NormalizePassName(passName);
        return names.Any(n => NormalizePassName(n) == key);
    }

    // =====================================================================
    // Presets
    // =====================================================================

    /// <summary>Lists preset ids (file name without <c>.preset.json</c>) in <paramref name="presetsDir"/>.</summary>
    public static List<string> ListPresetIds(string? presetsDir = null)
    {
        var dir = string.IsNullOrWhiteSpace(presetsDir) ? DefaultPresetsDir : presetsDir;
        if (!Directory.Exists(dir)) return new List<string>();
        return Directory.EnumerateFiles(dir, "*.preset.json")
            .Select(f => Path.GetFileName(f)[..^".preset.json".Length])
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Resolves a bare preset name ("archipelago"), a file name or a path to a preset file path.</summary>
    public static string ResolvePresetPath(string nameOrPath, string? presetsDir = null)
    {
        if (File.Exists(nameOrPath)) return Path.GetFullPath(nameOrPath);
        var dir = string.IsNullOrWhiteSpace(presetsDir) ? DefaultPresetsDir : presetsDir;
        var file = nameOrPath.EndsWith(".preset.json", StringComparison.OrdinalIgnoreCase)
            ? nameOrPath
            : nameOrPath + ".preset.json";
        return Path.Combine(dir, file);
    }

    /// <summary>Loads a preset by name or path. Throws <see cref="FileNotFoundException"/> naming the expected file.</summary>
    public static MapGenPreset LoadPreset(string nameOrPath, string? presetsDir = null)
    {
        var path = ResolvePresetPath(nameOrPath, presetsDir);
        if (!File.Exists(path)) throw new FileNotFoundException($"preset not found: {path}", path);
        return MapGenPreset.Load(path);
    }

    /// <summary>Applies <paramref name="preset"/> and appends any unknown-key warnings.</summary>
    /// <remarks>
    /// The checks (unknown pass names, unknown or read-only parameters, bad values) live in
    /// <see cref="MapGenPreset.ApplyTo"/>, which fills <see cref="MapGenPreset.Warnings"/>;
    /// this only adds a "did you mean" hint for pass names that differ in case or spacing.
    /// </remarks>
    public static void ApplyPreset(List<PipelineStep> steps, MapGenPreset preset, List<string>? warnings = null)
    {
        preset.ApplyTo(steps);
        if (warnings is null) return;
        foreach (var w in preset.Warnings) warnings.Add(w);
        foreach (var name in preset.Passes.Keys.Concat(preset.DisablePasses))
        {
            if (steps.Any(s => s.Pass.Name == name)) continue;
            var near = steps.FirstOrDefault(s => NormalizePassName(s.Pass.Name) == NormalizePassName(name));
            if (near is not null)
                warnings.Add($"preset '{preset.Name}': did you mean \"{near.Pass.Name}\" for \"{name}\"?");
        }
    }

    /// <summary>
    /// Serialises the current step list as a preset: only parameters that differ from
    /// <see cref="IGenerationPass.CreateDefaultParams"/>, passes switched off that are on by default,
    /// and default-off passes that were switched on (listed with their overrides, possibly empty).
    /// </summary>
    public static string ExportPreset(IReadOnlyList<PipelineStep> steps, string name, string? description, long? seed)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("name", name);
            if (!string.IsNullOrWhiteSpace(description)) w.WriteString("description", description);
            if (seed.HasValue) w.WriteNumber("seed", seed.Value);

            w.WriteStartObject("passes");
            foreach (var step in steps)
            {
                if (step.Pass.Name == ImageImportPassName) continue; // mode, not a preset setting
                var diffs = DiffParameters(step);
                bool optIn = step.Enabled && !step.DefaultEnabled;
                if (diffs.Count == 0 && !optIn) continue;
                w.WriteStartObject(step.Pass.Name);
                foreach (var (prop, value) in diffs) WriteValue(w, prop, value);
                w.WriteEndObject();
            }
            w.WriteEndObject();

            // A default-off pass that stays off but carries parameter diffs is listed here
            // too: MapGenPreset.ApplyTo opts a pass in when "passes" names it, so without
            // the entry a re-load would switch it on (export's hash check then refuses).
            w.WriteStartArray("disable_passes");
            foreach (var step in steps)
                if (!step.Enabled && step.Pass.Name != ImageImportPassName
                    && (step.DefaultEnabled || DiffParameters(step).Count > 0))
                    w.WriteStringValue(step.Pass.Name);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static List<(PropertyInfo Prop, object? Value)> DiffParameters(PipelineStep step)
    {
        var result = new List<(PropertyInfo, object?)>();
        var defaults = step.Pass.CreateDefaultParams();
        foreach (var prop in step.Parameters.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanRead || !prop.CanWrite) continue;
            var t = prop.PropertyType;
            if (t != typeof(int) && t != typeof(double) && t != typeof(bool) && t != typeof(string) && !t.IsEnum)
                continue; // MapGenPreset.ApplyTo can only set these types
            var cur = prop.GetValue(step.Parameters);
            var def = prop.GetValue(defaults);
            if (!Equals(cur, def)) result.Add((prop, cur));
        }
        return result;
    }

    private static void WriteValue(Utf8JsonWriter w, PropertyInfo prop, object? value)
    {
        switch (value)
        {
            case null: w.WriteNull(prop.Name); break;
            case int i: w.WriteNumber(prop.Name, i); break;
            case double d: w.WriteNumber(prop.Name, d); break;
            case bool b: w.WriteBoolean(prop.Name, b); break;
            case string s: w.WriteString(prop.Name, s); break;
            case Enum e: w.WriteString(prop.Name, e.ToString()); break;
        }
    }

    // =====================================================================
    // Steps
    // =====================================================================

    /// <summary>Builds the step list for <paramref name="options"/>. Order: preset, disable=, image import, heavy, side-effect gate.</summary>
    public static List<PipelineStep> BuildSteps(PipelineOptions? options = null, List<string>? warnings = null)
    {
        options ??= new PipelineOptions();
        var steps = DefaultPipeline.Build();
        if (options.Preset is not null) ApplyPreset(steps, options.Preset, warnings);
        if (options.Disable is not null) DisablePasses(steps, options.Disable, warnings);
        if (!string.IsNullOrWhiteSpace(options.QuadrantsPath)) SetQuadrants(steps, options.QuadrantsPath);
        if (!string.IsNullOrWhiteSpace(options.ImportPrefix))
            ApplyImageImport(steps, options.ImportPrefix, options.ImportMinZ, options.ImportMaxZ, warnings);
        if (options.DisableHeavyPasses) DisableHeavyPasses(steps);
        ApplySideEffectGate(steps, options.AllowFileSideEffects, options.SpawnOutputDir, warnings);
        return steps;
    }

    /// <summary>Switches off every step whose name matches one in <paramref name="names"/>. Returns the match count.</summary>
    public static int DisablePasses(List<PipelineStep> steps, IEnumerable<string> names, List<string>? warnings = null)
    {
        var list = names.Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        int matched = 0;
        foreach (var step in steps)
        {
            if (!NameIn(step.Pass.Name, list)) continue;
            step.Enabled = false;
            matched++;
        }
        if (warnings is not null)
            foreach (var n in list)
                if (!steps.Any(s => NormalizePassName(s.Pass.Name) == NormalizePassName(n)))
                    warnings.Add($"disable: \"{n}\" matches no pass");
        return matched;
    }

    /// <summary>Switches off <see cref="HeavyPassNames"/>.</summary>
    public static void DisableHeavyPasses(List<PipelineStep> steps)
    {
        foreach (var step in steps)
            if (NameIn(step.Pass.Name, HeavyPassNames)) step.Enabled = false;
    }

    /// <summary>
    /// Image-import mode: inserts (or reuses) ImageImportPass at the front pointed at the painted
    /// PNGs, and switches off the passes it replaces. Biome Altitude Jitter stays on on purpose:
    /// it breaks the flat-carpet look of painted altitude.
    /// </summary>
    public static ImageImportParams ApplyImageImport(List<PipelineStep> steps, string prefix, int? minZ = null, int? maxZ = null, List<string>? warnings = null)
    {
        foreach (var step in steps)
            if (NameIn(step.Pass.Name, ImageImportReplacedPassNames)) step.Enabled = false;

        var existing = steps.FirstOrDefault(s => s.Pass is ImageImportPass);
        ImageImportParams p;
        if (existing is null)
        {
            var pass = new ImageImportPass();
            p = (ImageImportParams)pass.CreateDefaultParams();
            steps.Insert(0, new PipelineStep { Pass = pass, Parameters = p, Enabled = true, DefaultEnabled = true });
        }
        else
        {
            p = (ImageImportParams)existing.Parameters;
            existing.Enabled = true;
        }
        p.TerrainImagePath = prefix + ".terrain.png";
        p.AltitudeImagePath = prefix + ".altitude.png";
        if (minZ is not null) p.MinZ = minZ.Value;
        if (maxZ is not null) p.MaxZ = maxZ.Value;
        if (warnings is not null && !File.Exists(p.TerrainImagePath))
            warnings.Add($"image import: {p.TerrainImagePath} not found");
        return p;
    }

    /// <summary>Removes image-import mode again: drops ImageImportPass and restores the replaced passes' default state.</summary>
    public static void RemoveImageImport(List<PipelineStep> steps)
    {
        steps.RemoveAll(s => s.Pass is ImageImportPass);
        foreach (var step in steps)
            if (NameIn(step.Pass.Name, ImageImportReplacedPassNames)) step.Enabled = step.DefaultEnabled;
    }

    /// <summary>
    /// Gates passes with file side effects. Without <paramref name="allow"/> they are switched off
    /// (with a warning when that changes something); with it, an explicit <paramref name="spawnOutputDir"/>
    /// is pushed into the pass parameters.
    /// </summary>
    public static void ApplySideEffectGate(List<PipelineStep> steps, bool allow, string? spawnOutputDir, List<string>? warnings = null)
    {
        foreach (var step in steps)
        {
            if (!NameIn(step.Pass.Name, FileSideEffectPassNames)) continue;
            if (!allow)
            {
                if (step.Enabled)
                    warnings?.Add($"'{step.Pass.Name}' skipped: it writes files and this run is a preview (only an explicit commit runs it).");
                step.Enabled = false;
                continue;
            }
            if (!string.IsNullOrWhiteSpace(spawnOutputDir))
                SetStringParam(step.Parameters, "OutputDir", spawnOutputDir);
        }
    }

    private static void SetQuadrants(List<PipelineStep> steps, string path)
    {
        foreach (var step in steps)
            if (step.Parameters is PoiStampParams poi) poi.QuadrantBoundariesPath = path;
    }

    private static void SetStringParam(object parameters, string name, string value)
    {
        var prop = parameters.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (prop is not null && prop.CanWrite && prop.PropertyType == typeof(string))
            prop.SetValue(parameters, value);
    }

    /// <summary>Deep-ish copy of a step list (fresh parameter objects via JSON round trip of writable properties).</summary>
    public static List<PipelineStep> CloneSteps(IReadOnlyList<PipelineStep> steps)
    {
        var result = new List<PipelineStep>(steps.Count);
        foreach (var s in steps)
        {
            var p = s.Pass.CreateDefaultParams();
            foreach (var prop in p.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                if (prop.CanRead && prop.CanWrite) prop.SetValue(p, prop.GetValue(s.Parameters));
            result.Add(new PipelineStep { Pass = s.Pass, Parameters = p, Enabled = s.Enabled, DefaultEnabled = s.DefaultEnabled });
        }
        return result;
    }

    // =====================================================================
    // IR + data
    // =====================================================================

    /// <summary>
    /// Resolves the brush table: an explicit path; "guo" (the default) is the user's resolved GUO table
    /// when <c>prepare --measure</c> wrote one, else the committed GUO table; "dragon" is the user's Dragon
    /// import (<c>prepare --dragon</c>).
    /// </summary>
    public static string ResolveBrushesPath(string? explicitPath)
    {
        if (string.IsNullOrWhiteSpace(explicitPath) || explicitPath.Equals("guo", StringComparison.OrdinalIgnoreCase))
            return LandBrushTable.DefaultPath();
        if (explicitPath.Equals("guo-core", StringComparison.OrdinalIgnoreCase))
            return RepoRootResolver.Resolve(GuoTransitionTable.RelativePath);
        if (explicitPath.Equals("dragon", StringComparison.OrdinalIgnoreCase))
            return RepoRootResolver.Resolve(DragonBrushesRelPath);
        return explicitPath;
    }

    public static string ResolveTreeStaticsPath(string? explicitPath)
        => string.IsNullOrWhiteSpace(explicitPath) ? DefaultTreeStaticsPath : explicitPath;

    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, (DateTime Stamp, object Value)> Cache = new(StringComparer.OrdinalIgnoreCase);

    private static T Cached<T>(string kind, string? path, Func<string?, T> load) where T : class
    {
        string key = kind + "|" + (path ?? "");
        DateTime stamp = path is not null && File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        lock (CacheLock)
        {
            if (Cache.TryGetValue(key, out var hit) && hit.Stamp == stamp) return (T)hit.Value;
        }
        var value = load(path);
        lock (CacheLock) Cache[key] = (stamp, value);
        return value;
    }

    /// <summary>
    /// Creates an IR with the tile tables, brush table and tree statics loaded. Missing data is not
    /// fatal (the loaders fall back to defaults) but is reported in <paramref name="warnings"/> so a
    /// UI can show it — a missing tree-statics.json silently drops every tree's leaves otherwise.
    /// </summary>
    public static GenIR CreateIR(ushort width, ushort height, RectU16 scope, ulong seed,
        PipelineDataOptions? data = null, List<string>? warnings = null)
    {
        data ??= new PipelineDataOptions();

        string? tablesPath = string.IsNullOrWhiteSpace(data.TileTablesPath) ? null : data.TileTablesPath;
        if (tablesPath is not null && !File.Exists(tablesPath))
            warnings?.Add($"tile tables not found at {tablesPath} — using built-in defaults");

        string brushesPath = ResolveBrushesPath(data.BrushesPath);
        var brushes = Cached("brushes", brushesPath, p => LandBrushTable.LoadOrEmpty(p));
        if (!brushes.IsLoaded)
            warnings?.Add($"land brush table not found at {brushesPath} — transitions use defaults");

        string treesPath = ResolveTreeStaticsPath(data.TreeStaticsPath);
        if (!File.Exists(treesPath))
            warnings?.Add($"tree-statics.json not found at {treesPath} — using GUO's scatter table for trunk/canopy pairing");
        var trees = File.Exists(treesPath)
            ? Cached("trees", treesPath, p => TreeStatics.LoadOrEmpty(p!))
            : Cached("scatter-trees", RepoRootResolver.Resolve(GuoScatterTable.RelativePath),
                p => File.Exists(p) ? GuoScatterTable.Load(p!).ToTreeStatics() : TreeStatics.Empty);

        return new GenIR(width, height, scope, seed)
        {
            Tables = Cached("tables", tablesPath, p => TileTables.LoadOrDefault(p)),
            Brushes = brushes,
            Trees = trees,
        };
    }

    /// <summary>Convenience: full-extent scope for a width x height IR.</summary>
    public static RectU16 FullScope(int width, int height)
        => new((ushort)0, (ushort)0, (ushort)(width - 1), (ushort)(height - 1));

    // =====================================================================
    // Map source files (fast writer baseline)
    // =====================================================================

    /// <summary>
    /// Shared baseline resolver for the fast .mul writer (CLI <c>source=</c>, editor Source dir).
    /// Prefers <c>map0.mul</c> over <c>map0LegacyMUL.uop</c> when both exist (the historical CLI
    /// rule); the <see cref="MapSourceFiles.Note"/> says so, so a UI can show which file was used.
    /// </summary>
    public static MapSourceFiles ResolveMapSource(string dir)
    {
        string mul = Path.Combine(dir, "map0.mul");
        string uop = Path.Combine(dir, "map0LegacyMUL.uop");
        bool hasMul = File.Exists(mul), hasUop = File.Exists(uop);
        string? map = hasMul ? mul : hasUop ? uop : null;
        string? note = hasMul && hasUop ? "map0.mul preferred over map0LegacyMUL.uop" : null;
        return new MapSourceFiles(dir, map, Path.Combine(dir, "staidx0.mul"), Path.Combine(dir, "statics0.mul"), note);
    }
}
