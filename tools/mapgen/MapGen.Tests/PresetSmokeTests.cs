using CentrED.MapGen;
using CentrED.MapGen.Data;
using CentrED.MapGen.IR;
using CentrED.MapGen.Pipeline;
using CentrED.MapGen.Presets;
using CentrED.Network;

namespace CentrED.MapGen.Tests;

// Run every *.preset.json against a small (256x256) pipeline. Regression net for
// "I broke moisture for everyone" — if a preset goes degenerate (no biomes assigned,
// single biome covers >99%, pass exception), the test fails with a clear pointer.
//
// Heavy passes (stamps, scatter, POIs, roads) are disabled because they pull external
// data files we don't want to depend on in CI and add significant time. The terrain →
// climate → biome → land-id → transition chain stays enabled — that's the spine the
// rest of the system rides on.
public class PresetSmokeTests
{
    private const ushort Size = 256;

    private static readonly string[] HeavyDisable =
    {
        "RoadGraph", "RoadCenterline", "RoadStamps", "POIStamps", "TownStamps",
        "StampScatter", "MountainEdgeStatics", "BiomeStaticScatter", "ForestScatter"
    };

    public static IEnumerable<object[]> PresetFiles()
    {
        var dir = RepoRootResolver.Resolve("tools/mapgen/MapGen/presets");
        if (!Directory.Exists(dir)) yield break;
        foreach (var f in Directory.EnumerateFiles(dir, "*.preset.json"))
            yield return new object[] { Path.GetFileName(f) };
    }

    [Theory]
    [MemberData(nameof(PresetFiles))]
    public void Preset_RunsWithoutThrowing_AndProducesABiomeDistribution(string presetFile)
    {
        var dir = RepoRootResolver.Resolve("tools/mapgen/MapGen/presets");
        var path = Path.Combine(dir, presetFile);
        var preset = MapGenPreset.Load(path);

        var steps = PipelineFactory.BuildSteps(new PipelineOptions
        {
            Preset = preset,
            Disable = HeavyDisable,
            AllowFileSideEffects = false,
        });
        Assert.Empty(preset.Warnings);

        long seed = preset.Seed ?? 1234567;
        var ir = PipelineFactory.CreateIR(Size, Size, new RectU16(0, 0, Size - 1, Size - 1), unchecked((ulong)seed));
        Assert.True(ir.Brushes.IsLoaded, "Smoke tests must exercise the actual transition table");
        new PipelineRunner().Run(ir, steps);

        // Biome buffer must exist and be populated.
        Assert.NotNull(ir.Biome);
        var counts = new int[256];
        int total = ir.Biome!.Length;
        foreach (var b in ir.Biome) counts[b]++;

        // At least 2 distinct biomes should be present — a degenerate preset that
        // only writes Unassigned, or only Forest, is broken.
        int distinct = counts.Count(c => c > 0);
        Assert.True(distinct >= 2,
            $"preset {presetFile} produced only {distinct} biome(s) — pipeline is degenerate");

        // No single biome should occupy >99% of the map. This is intentionally lax
        // (presets like 'barren' may legitimately go to 85% one biome); the threshold
        // is there to catch the "everything is Desert" / "everything is Forest" classes
        // of regression seen during tuning.
        int peak = counts.Max();
        double peakPct = 100.0 * peak / total;
        Assert.True(peakPct < 99.0,
            $"preset {presetFile} has a single biome at {peakPct:F1}% — moisture cascade likely broken");
    }
}
