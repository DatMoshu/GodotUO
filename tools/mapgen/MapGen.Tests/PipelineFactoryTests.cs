using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using CentrED.MapGen.Data;
using CentrED.MapGen.IR;
using CentrED.MapGen.Passes.Terrain;
using CentrED.MapGen.Pipeline;
using CentrED.MapGen.Presets;
using CentrED.Network;

namespace CentrED.MapGen.Tests;

// PipelineFactory is the single builder behind the CLI, the web stepper and the
// editor windows. These tests pin the behaviour every front end relies on.
public class PipelineFactoryTests
{
    private const ushort Dim = 128;

    // Test binaries may live outside the repo (--artifacts-path in %TEMP%), where
    // RepoRootResolver cannot find the repo from CWD/BaseDirectory. Point CWD at the
    // repo (derived from this source file) before anything resolves a repo path.
    static PipelineFactoryTests()
    {
        if (RepoRootResolver.FindRepoRoot() is not null) return;
        var dir = new DirectoryInfo(Path.GetDirectoryName(ThisFile())!);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tools", "CentrED")))
            dir = dir.Parent;
        if (dir is not null) Environment.CurrentDirectory = dir.FullName;
    }

    // Preview-safe steps, with any pass whose Reads cannot be satisfied switched off.
    // These tests exercise the factory/runner mechanics, not the pass topology (which
    // the terrain work changes independently), so they must not break when it shifts.
    private static List<PipelineStep> RunnableSteps(MapGenPreset? preset = null)
    {
        var steps = PipelineFactory.BuildSteps(PipelineOptions.PreviewSafe(preset));
        for (int guard = 0; guard < steps.Count; guard++)
        {
            try { PipelineRunner.Validate(steps); return steps; }
            catch (PipelineValidationException ex)
            {
                var bad = steps.First(st => st.Enabled && ex.Message.Contains($"'{st.Pass.Name}'"));
                bad.Enabled = false;
            }
        }
        return steps;
    }

    private static string ThisFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

    private static GenIR NewIR(ulong seed = 42) =>
        new(Dim, Dim, PipelineFactory.FullScope(Dim, Dim), seed);

    private static string Digest<T>(T[]? data) where T : unmanaged
    {
        if (data is null) return "null";
        var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(data.AsSpan()).ToArray();
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static MapGenPreset PresetFromJson(string json, string name = "test")
    {
        var path = Path.Combine(Path.GetTempPath(), $"pf-{Guid.NewGuid():N}.preset.json");
        File.WriteAllText(path, json);
        try { return MapGenPreset.Load(path); }
        finally { File.Delete(path); }
    }

    private static Dictionary<string, object?> ParamValues(object p) =>
        p.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(x => x.CanRead && x.CanWrite)
            .ToDictionary(x => x.Name, x => x.GetValue(p));

    private static void AssertSameSteps(IReadOnlyList<PipelineStep> expected, IReadOnlyList<PipelineStep> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].Pass.Name, actual[i].Pass.Name);
            Assert.True(expected[i].Enabled == actual[i].Enabled, $"Enabled differs for {expected[i].Pass.Name}");
            var e = ParamValues(expected[i].Parameters);
            var a = ParamValues(actual[i].Parameters);
            foreach (var (k, v) in e)
                Assert.True(Equals(v, a[k]), $"{expected[i].Pass.Name}.{k}: expected {v}, got {a[k]}");
        }
    }

    [Fact]
    public void BuildSteps_WithPreset_MatchesManualApply()
    {
        var preset = PipelineFactory.LoadPreset("archipelago");
        var manual = DefaultPipeline.Build();
        preset.ApplyTo(manual);

        var built = PipelineFactory.BuildSteps(new PipelineOptions { Preset = preset });
        AssertSameSteps(manual, built);
    }

    [Fact]
    public void LoadPreset_Missing_ThrowsFileNotFound()
    {
        Assert.Throws<FileNotFoundException>(() => PipelineFactory.LoadPreset("no-such-preset-xyz"));
        Assert.Contains("archipelago", PipelineFactory.ListPresetIds());
    }

    [Fact]
    public void RunRange_InSlices_MatchesSingleRun()
    {
        var steps = RunnableSteps();
        var whole = NewIR();
        new PipelineRunner().Run(whole, steps);

        var sliced = NewIR();
        var runner = new PipelineRunner();
        int mid = steps.Count / 2;
        runner.RunRange(sliced, steps, 0, 3);
        runner.RunRange(sliced, steps, 3, mid);
        runner.RunRange(sliced, steps, mid, steps.Count);

        Assert.Equal(Digest(whole.Height_Z), Digest(sliced.Height_Z));
        Assert.Equal(Digest(whole.Biome), Digest(sliced.Biome));
        Assert.Equal(Digest(whole.LandId), Digest(sliced.LandId));
        Assert.Equal(whole.StaticOps, sliced.StaticOps);
    }

    [Fact]
    public void RunRange_ReportsEachEnabledStep()
    {
        var steps = RunnableSteps();
        var seen = new List<int>();
        new PipelineRunner().RunRange(NewIR(), steps, 0, 4, onStep: (i, _) => seen.Add(i));
        Assert.Equal(Enumerable.Range(0, 4).Where(i => steps[i].Enabled), seen);
    }

    [Fact]
    public void ImageImport_InsertsPassFirst_AndDisablesReplacedPasses()
    {
        var steps = PipelineFactory.BuildSteps(new PipelineOptions
        {
            ImportPrefix = "some/prefix", ImportMinZ = -5, ImportMaxZ = 127,
        });
        Assert.Equal(PipelineFactory.ImageImportPassName, steps[0].Pass.Name);
        Assert.True(steps[0].Enabled);
        var p = (ImageImportParams)steps[0].Parameters;
        Assert.Equal("some/prefix.terrain.png", p.TerrainImagePath);
        Assert.Equal("some/prefix.altitude.png", p.AltitudeImagePath);
        Assert.Equal(-5, p.MinZ);
        Assert.Equal(127, p.MaxZ);
        foreach (var name in PipelineFactory.ImageImportReplacedPassNames)
            Assert.False(steps.Single(s => s.Pass.Name == name).Enabled, name);
        Assert.True(steps.Single(s => s.Pass.Name == "Biome Altitude Jitter").Enabled);

        // Re-applying reuses the existing pass rather than inserting a second one.
        PipelineFactory.ApplyImageImport(steps, "other");
        Assert.Single(steps, s => s.Pass.Name == PipelineFactory.ImageImportPassName);

        PipelineFactory.RemoveImageImport(steps);
        Assert.DoesNotContain(steps, s => s.Pass.Name == PipelineFactory.ImageImportPassName);
        Assert.True(steps.Single(s => s.Pass.Name == "Noise Height").Enabled);
    }

    private static PipelineStep Spawner(List<PipelineStep> steps) =>
        steps.Single(s => s.Pass.Name == "Dungeon Spawner Emit");

    private static string? OutputDirOf(PipelineStep step) =>
        step.Parameters.GetType().GetProperty("OutputDir")?.GetValue(step.Parameters) as string;

    [Fact]
    public void SideEffectGate_PreviewDisablesSpawnerEmit()
    {
        var preset = PresetFromJson("""{ "name": "spawn-on", "passes": { "Dungeon Spawner Emit": {} } }""");
        var warnings = new List<string>();
        var steps = PipelineFactory.BuildSteps(new PipelineOptions { Preset = preset, AllowFileSideEffects = false }, warnings);
        Assert.False(Spawner(steps).Enabled);
        Assert.Contains(warnings, w => w.Contains("Dungeon Spawner Emit"));
    }

    [Fact]
    public void SideEffectGate_CommitSetsExplicitOutputDir()
    {
        var preset = PresetFromJson("""{ "name": "spawn-on", "passes": { "Dungeon Spawner Emit": {} } }""");
        var dir = Path.Combine(Path.GetTempPath(), "pf-spawns");
        var steps = PipelineFactory.BuildSteps(new PipelineOptions
        {
            Preset = preset, AllowFileSideEffects = true, SpawnOutputDir = dir,
        });
        Assert.True(Spawner(steps).Enabled);
        Assert.Equal(dir, OutputDirOf(Spawner(steps)));
    }

    [Fact]
    public void ApplyPreset_ReportsUnknownKeys()
    {
        var preset = PresetFromJson("""
        {
          "name": "typos",
          "passes": {
            "Noise Heigth": { "Octaves": 3 },
            "noise height": { "Octaves": 3 },
            "Noise Height": { "Octavez": 3, "Octaves": 4 }
          },
          "disable_passes": ["Hydraulic Erosion", "Nope Pass"]
        }
        """);
        var warnings = new List<string>();
        PipelineFactory.ApplyPreset(DefaultPipeline.Build(), preset, warnings);
        Assert.Contains(warnings, w => w.Contains("unknown pass \"Noise Heigth\""));
        Assert.Contains(warnings, w => w.Contains("did you mean \"Noise Height\""));
        Assert.Contains(warnings, w => w.Contains("no parameter \"Octavez\""));
        Assert.Contains(warnings, w => w.Contains("\"Nope Pass\""));
        Assert.DoesNotContain(warnings, w => w.Contains("\"Octaves\""));
        Assert.DoesNotContain(warnings, w => w.Contains("\"Hydraulic Erosion\""));
    }

    [Fact]
    public void ShippedPresets_HaveNoUnknownKeys_OrOnlyReportThem()
    {
        // Not an assertion on content (presets belong to the terrain work) — just that
        // validation runs over every shipped preset without throwing.
        foreach (var id in PipelineFactory.ListPresetIds())
        {
            var preset = PipelineFactory.LoadPreset(id);
            var ex = Record.Exception(() => PipelineFactory.ApplyPreset(DefaultPipeline.Build(), preset, new List<string>()));
            Assert.Null(ex);
        }
    }

    [Fact]
    public void ExportPreset_RoundTripsThroughMapGenPresetLoad()
    {
        var steps = DefaultPipeline.Build();
        var noise = (NoiseHeightParams)steps.Single(s => s.Pass.Name == "Noise Height").Parameters;
        noise.Octaves = 7;
        noise.ContinentFalloff = 0.123456789;
        noise.Shape = Enum.GetValues<ContinentShape>().First(v => v != noise.Shape);
        steps.Single(s => s.Pass.Name == "Hydraulic Erosion").Enabled = false;
        steps.Single(s => s.Pass.Name == "Maze Stamp").Enabled = true; // default-off opt-in

        var json = PipelineFactory.ExportPreset(steps, "exported", "round trip", 99);
        using (var doc = JsonDocument.Parse(json))
            Assert.Equal(99, doc.RootElement.GetProperty("seed").GetInt64());

        var preset = PresetFromJson(json);
        var exportWarnings = new List<string>();
        PipelineFactory.ApplyPreset(DefaultPipeline.Build(), preset, exportWarnings);
        Assert.Empty(exportWarnings);
        var rebuilt = PipelineFactory.BuildSteps(new PipelineOptions { Preset = preset });
        AssertSameSteps(steps, rebuilt);
    }

    [Fact]
    public void ExportPreset_DefaultPipeline_ListsOnlyGatedDiffs()
    {
        var steps = DefaultPipeline.Build();
        var json = PipelineFactory.ExportPreset(steps, "defaults", null, null);
        var preset = PresetFromJson(json);
        Assert.Empty(preset.DisablePasses);
        AssertSameSteps(steps, PipelineFactory.BuildSteps(new PipelineOptions { Preset = preset }));
    }

    [Theory]
    [InlineData("river carve")]
    [InlineData("RiverCarve")]
    [InlineData("RIVER CARVE")]
    public void DisablePasses_IsCaseAndSpaceInsensitive(string name)
    {
        var steps = DefaultPipeline.Build();
        var warnings = new List<string>();
        int matched = PipelineFactory.DisablePasses(steps, new[] { name, "no such pass" }, warnings);
        Assert.Equal(1, matched);
        Assert.False(steps.Single(s => s.Pass.Name == "River Carve").Enabled);
        Assert.Single(warnings);
    }

    [Fact]
    public void DisableHeavyPasses_SwitchesOffEveryHeavyPass()
    {
        var steps = PipelineFactory.BuildSteps(new PipelineOptions { DisableHeavyPasses = true });
        var heavy = steps.Where(s => PipelineFactory.HeavyPassNames.Contains(s.Pass.Name)).ToList();
        Assert.NotEmpty(heavy);
        foreach (var s in heavy)
            Assert.False(s.Enabled, s.Pass.Name);
    }

    [Fact]
    public void ResolveMapSource_PrefersMulOverUop_AndNotes()
    {
        var dir = Directory.CreateTempSubdirectory("pf-src").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "staidx0.mul"), "");
            File.WriteAllText(Path.Combine(dir, "statics0.mul"), "");
            File.WriteAllText(Path.Combine(dir, "map0LegacyMUL.uop"), "");

            var uopOnly = PipelineFactory.ResolveMapSource(dir);
            Assert.True(uopOnly.IsComplete);
            Assert.EndsWith("map0LegacyMUL.uop", uopOnly.MapPath);
            Assert.Null(uopOnly.Note);

            File.WriteAllText(Path.Combine(dir, "map0.mul"), "");
            var both = PipelineFactory.ResolveMapSource(dir);
            Assert.EndsWith("map0.mul", both.MapPath);
            Assert.NotNull(both.Note);

            File.Delete(Path.Combine(dir, "statics0.mul"));
            Assert.False(PipelineFactory.ResolveMapSource(dir).IsComplete);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ResolveMapSource_EmptyDir_IsIncomplete()
    {
        var dir = Directory.CreateTempSubdirectory("pf-empty").FullName;
        try
        {
            var src = PipelineFactory.ResolveMapSource(dir);
            Assert.Null(src.MapPath);
            Assert.False(src.IsComplete);
            Assert.Contains("missing", src.Describe());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void GenIRSnapshot_CaptureRestore_RoundTrips()
    {
        var steps = RunnableSteps();
        var ir = NewIR();
        var runner = new PipelineRunner();
        runner.RunRange(ir, steps, 0, 6);
        var heightBefore = Digest(ir.Height_Z);
        var biomeBefore = Digest(ir.Biome);
        int opsBefore = ir.StaticOps.Count;
        int reportsBefore = ir.Reports.Count;

        var snap = GenIRSnapshot.Capture(ir);
        Assert.True(snap.DenseBytes > 0);
        runner.RunRange(ir, steps, 6, steps.Count);
        Assert.NotEqual(reportsBefore, ir.Reports.Count);

        snap.Restore(ir);
        Assert.Equal(heightBefore, Digest(ir.Height_Z));
        Assert.Equal(biomeBefore, Digest(ir.Biome));
        Assert.Equal(opsBefore, ir.StaticOps.Count);
        Assert.Equal(reportsBefore, ir.Reports.Count);

        // Restoring then re-running the tail reproduces the uninterrupted result.
        runner.RunRange(ir, steps, 6, steps.Count);
        var whole = NewIR();
        runner.Run(whole, steps);
        Assert.Equal(Digest(whole.LandId), Digest(ir.LandId));
    }

    [Fact]
    public void CreateIR_WarnsOnMissingTreeStatics()
    {
        var warnings = new List<string>();
        var ir = PipelineFactory.CreateIR(Dim, Dim, PipelineFactory.FullScope(Dim, Dim), 1,
            new PipelineDataOptions { TreeStaticsPath = Path.Combine(Path.GetTempPath(), "no-such-tree-statics.json") },
            warnings);
        Assert.NotNull(ir);
        Assert.Contains(warnings, w => w.Contains("tree-statics"));
    }

    [Fact]
    public void CreateIR_SameInputs_SameMap()
    {
        // The parity guarantee across front ends: same factory inputs -> same output.
        LandIdDigest(out var a);
        LandIdDigest(out var b);
        Assert.Equal(a, b);

        static void LandIdDigest(out string digest)
        {
            var steps = RunnableSteps(PipelineFactory.LoadPreset("archipelago"));
            var ir = PipelineFactory.CreateIR(Dim, Dim, PipelineFactory.FullScope(Dim, Dim), 7);
            new PipelineRunner().Run(ir, steps);
            digest = Digest(ir.LandId);
        }
    }
}
