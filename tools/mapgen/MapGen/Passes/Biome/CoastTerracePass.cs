using CentrED.MapGen.Data;
using CentrED.MapGen.IR;
using CentrED.MapGen.Noise;
using CentrED.MapGen.Pipeline;

namespace CentrED.MapGen.Passes.Biome;

public sealed class CoastTerraceParams
{
    [TunableDisplay("Min cliff height", Tooltip = "Coast must be at least this many Z units above water to qualify as a cliff. 2 catches typical inland Z (test island grass Z=7); higher values restrict to dramatic cliffs only.")]
    [TunableRange(2, 64)]
    // Earlier default 8 never fired on our test island where inland grass sits at Z=7,
    // leaving sand cells at the same Z as inland grass — a 7-unit cliff straight into
    // water (procgen 200506). Real Felucca shows sand at sea-level Z and grass cliffing
    // up gradually inland. 2 captures that.
    public int MinCliffHeight { get; set; } = 2;

    [TunableDisplay("Terrace band width", Tooltip = "Number of inland steps the terrace gradient extends. Larger = gentler slope.")]
    [TunableRange(1, 64)]
    public int TerraceBandWidth { get; set; } = 24;

    [TunableDisplay("Terrace step Z", Tooltip = "Max Z rise per tile moving inland from the water. 3 = gentle Felucca-like slope.")]
    [TunableRange(1, 16)]
    public int TerraceStepZ { get; set; } = 3;

    [TunableDisplay("Force beach Z to sea level", Tooltip = "After terracing, force any beach-LandId cell touching water down to sea level. Without this, sand right at the water can stay at the +1 terrace floor and look raised.")]
    public bool ForceBeachAtWaterToSea { get; set; } = true;

    // Ignored: the pass uses GenIR.SeaLevelZ. Kept so presets that set it load.
    [TunableDisplay("Sea level Z (legacy, ignored)")] [TunableRange(-128, 127)]
    public int SeaLevelZ { get; set; } = 0;
}

// F-8 (Norad: Embankment Support / High Coast / Rough Support): when a high-altitude
// land cell sits directly next to water, the default beach gradient produces a flat
// shoreline. Real UO coastlines (think Britain, Yew) step down through 1-2 terrace
// levels before meeting water — the "high coast" look.
//
// Algorithm: distance d (tiles over land) from the nearest water; within
// TerraceBandWidth, land is capped at sea + TerraceStepZ*(d-1), so the shore rises
// inland at a bounded slope. Only lowers; land already below the ceiling is untouched.
//
// Slot AFTER AutoCoastPass and BEFORE LandTransitionPass.
public sealed class CoastTerracePass : IGenerationPass
{
    public string Name => "Coast Terrace";
    public string Category => "Biome";

    public IrFields Reads => IrFields.Height | IrFields.LandId;
    public IrFields Writes => IrFields.Height;

    public object CreateDefaultParams() => new CoastTerraceParams();

    public void Run(GenContext ctx, object parameters)
    {
        var p = (CoastTerraceParams)parameters;
        var ir = ctx.IR;
        if (ir.Height_Z is null || ir.LandId is null) return;
        var z = ir.Height_Z;
        var l = ir.LandId;
        var bio = ir.Biome;
        var scope = ir.Scope;
        int sw = scope.Width, sh = scope.Height;
        int n = sw * sh;
        int sea = ir.SeaLevelZ;

        int G(int li) => ir.Index(scope.X1 + li % sw, scope.Y1 + li / sw);
        bool IsWater(int g)
        {
            if (bio is not null && (BiomeId)bio[g] != BiomeId.Unassigned)
                return (BiomeId)bio[g] is BiomeId.DeepWater or BiomeId.ShallowWater or BiomeId.River;
            return TileFlags.IsWaterLandId(l[g]);
        }
        bool IsOcean(int g)
        {
            if (bio is not null && (BiomeId)bio[g] != BiomeId.Unassigned)
                return (BiomeId)bio[g] is BiomeId.DeepWater or BiomeId.ShallowWater;
            return TileFlags.IsWaterLandId(l[g]);
        }

        // Seeds: all water (and the brown dug band, which is water under statics). The
        // terrace is a ceiling that RISES inland: a land cell d tiles from water may be at
        // most sea + step*(d-1). Cells under the ceiling are untouched; only coastal
        // bluffs are cut back into a slope. (The old pass lowered cells inland of the lip
        // to lip - step, i.e. sloping AWAY from the sea and leaving the cliff in place.)
        var seed = new bool[n];
        for (int i = 0; i < n; i++)
        {
            int g = G(i);
            seed[i] = IsWater(g) || (z[g] <= ir.ShoreDigZ);
        }
        int band = Math.Max(1, p.TerraceBandWidth);
        int step = Math.Max(1, p.TerraceStepZ);
        var dist = FieldOps.Distance4(seed, sw, sh, band + 1);
        int touched = 0, seeds = 0;
        for (int i = 0; i < n; i++)
        {
            int d = dist[i];
            if (d == 0 || d > band) continue;
            int g = G(i);
            int ceiling = sea + step * (d - 1);
            if (d == 1) seeds++;
            if (z[g] < sea + p.MinCliffHeight) continue;
            if (z[g] <= ceiling) continue;
            z[g] = (sbyte)Math.Clamp(ceiling, sbyte.MinValue, sbyte.MaxValue);
            touched++;
        }

        // Beach touching the OCEAN sits at sea level (sand visibly raised above the surf
        // looks wrong). River/lake banks are left alone: they already step down to the
        // water plane by at most the river bank step.
        int forced = 0;
        if (p.ForceBeachAtWaterToSea)
        {
            for (int i = 0; i < n; i++)
            {
                int g = G(i);
                bool isBeach = bio is not null && (BiomeId)bio[g] != BiomeId.Unassigned
                    ? (BiomeId)bio[g] == BiomeId.Beach
                    : l[g] is >= 0x16 and <= 0x1C or >= 0x33 and <= 0x3E;
                if (!isBeach || IsWater(g)) continue;
                int lx = i % sw, ly = i / sw;
                bool touchesOcean = false;
                if (lx > 0 && IsOcean(G(i - 1))) touchesOcean = true;
                else if (lx < sw - 1 && IsOcean(G(i + 1))) touchesOcean = true;
                else if (ly > 0 && IsOcean(G(i - sw))) touchesOcean = true;
                else if (ly < sh - 1 && IsOcean(G(i + sw))) touchesOcean = true;
                if (!touchesOcean || z[g] <= sea) continue;
                z[g] = (sbyte)sea;
                forced++;
            }
        }

        ctx.Report.TilesTouched = touched + forced;
        ctx.Report.Notes.Add($"shore cells={seeds}, terrace cells lowered={touched} (ceiling sea+{step}*(d-1) over {band} tiles), beach forced to sea={forced}");
    }
}
