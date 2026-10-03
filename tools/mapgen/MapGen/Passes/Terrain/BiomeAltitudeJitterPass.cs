using CentrED.MapGen.Noise;
using CentrED.MapGen.IR;
using CentrED.MapGen.Pipeline;

namespace CentrED.MapGen.Passes.Terrain;

public sealed class BiomeAltitudeJitterParams
{
    [TunableDisplay("Grass jitter ±")] [TunableRange(0, 8)]
    public int GrassJitter { get; set; } = 1;

    [TunableDisplay("Forest jitter ±")] [TunableRange(0, 8)]
    public int ForestJitter { get; set; } = 2;

    [TunableDisplay("Sand/Beach jitter ±")] [TunableRange(0, 8)]
    public int SandJitter { get; set; } = 0;

    [TunableDisplay("Swamp jitter ±")] [TunableRange(0, 8)]
    public int SwampJitter { get; set; } = 1;

    [TunableDisplay("Desert jitter ±")] [TunableRange(0, 8)]
    public int DesertJitter { get; set; } = 2;

    [TunableDisplay("Snow jitter ±")] [TunableRange(0, 8)]
    public int SnowJitter { get; set; } = 1;

    [TunableDisplay("Mountain jitter ±", Tooltip = "Per-tile altitude noise for mountain interiors. Higher = peakier silhouettes.")]
    [TunableRange(0, 24)]
    public int MountainJitter { get; set; } = 3; // was 14: Mountain Shape now builds the relief; 14 made an egg-crate of spikes

    [TunableDisplay("Cave jitter ±", Tooltip = "Per-tile altitude noise for Cave biome tiles. UO's stretched-art (texmap) for the 0x0244 cave-floor tile is missing/black — flat-Z cave-floor tiles render as solid black diamonds. A jitter of 2 forces each tile to slope vs neighbours, pushing the renderer onto the regular LAND art (tan textured) without visibly raising/lowering floor altitude.")]
    [TunableRange(0, 6)]
    public int CaveJitter { get; set; } = 2;

    [TunableDisplay("Mountain smooth passes", Tooltip = "Number of 3x3 box-blur passes over Mountain biome cells AFTER jitter. 0 = raw per-cell noise (very rough), 1 = bumpy ridges, 2 = rolling, 3+ = soft dome.")]
    [TunableRange(0, 5)]
    public int MountainSmoothPasses { get; set; } = 1;

    [TunableDisplay("Skip if mountain", Tooltip = "If true, mountains keep their incoming altitude (e.g. from MountainShapePass). If false, MountainJitter is applied.")]
    public bool SkipMountain { get; set; } = false;

    [TunableDisplay("Frequency (per 1000)", Tooltip = "Probability per cell of being jittered")]
    [TunableRange(0, 1000)]
    public int Frequency { get; set; } = 700;

    [TunableDisplay("Noise frequency", Tooltip = "Jitter follows smooth noise at this frequency (1/tiles), so the ground undulates over several tiles. 0 = the old per-tile random jitter, whose alternating Z shades open ground as a checkerboard. Cave floors always use per-tile jitter (see Cave jitter).")]
    [TunableRange(0, 1)]
    public double NoiseFrequency { get; set; } = 0.09;
}

// F-10 (Dragon `unevenly.txt` style): adds per-biome random altitude jitter to break the
// flat-look of biome interiors. Run after BiomeAssignPass and after MountainShapePass so
// it operates on already-classified land. Skips water tiles.
public sealed class BiomeAltitudeJitterPass : IGenerationPass
{
    public string Name => "Biome Altitude Jitter";
    public string Category => "Terrain";

    public IrFields Reads => IrFields.Biome | IrFields.Height;
    public IrFields Writes => IrFields.Height;

    public object CreateDefaultParams() => new BiomeAltitudeJitterParams();

    public void Run(GenContext ctx, object parameters)
    {
        var p = (BiomeAltitudeJitterParams)parameters;
        var ir = ctx.IR;
        if (ir.Biome is null || ir.Height_Z is null) return;
        var z = ir.Height_Z;
        var b = ir.Biome;
        var scope = ir.Scope;
        int touched = 0;
        var oct = p.NoiseFrequency > 0 ? FieldOps.Octaves(ir.Seed, 0x4A17_7E55UL, 2) : null;

        for (ushort y = scope.Y1; y <= scope.Y2; y++)
        for (ushort x = scope.X1; x <= scope.X2; x++)
        {
            int idx = ir.Index(x, y);
            var biome = (BiomeId)b[idx];
            int amp = AmplitudeForBiome(biome, p);
            if (amp <= 0) continue;
            int delta;
            if (oct is not null && biome != BiomeId.Cave)
                delta = (int)Math.Round(amp * Math.Clamp(FieldOps.Fbm(oct, x, y, p.NoiseFrequency) * 1.6, -1.0, 1.0));
            else
            {
                if (ctx.Rng.Next(1000) >= p.Frequency) continue;
                delta = ctx.Rng.Next(-amp, amp + 1);
            }
            if (delta == 0) continue;
            z[idx] = (sbyte)Math.Clamp(z[idx] + delta, sbyte.MinValue, sbyte.MaxValue);
            touched++;
        }

        // Mountain smoothing: pure per-cell random ±jitter produces a noisy spiky carpet
        // (image 23). Real Felucca peaks are smoother — neighbouring tiles share altitude
        // within ±2 of each other. Box-blur N times over Mountain cells only to fold the
        // random spikes into broad rolling peaks while keeping cell-to-cell variation.
        if (p.MountainSmoothPasses > 0)
        {
            var tmp = new sbyte[z.Length];
            for (int pass = 0; pass < p.MountainSmoothPasses; pass++)
            {
                Array.Copy(z, tmp, z.Length);
                for (ushort y = scope.Y1; y <= scope.Y2; y++)
                for (ushort x = scope.X1; x <= scope.X2; x++)
                {
                    int idx = ir.Index(x, y);
                    var biome = (BiomeId)b[idx];
                    if (biome != BiomeId.Mountain && biome != BiomeId.HighMountain) continue;
                    int sum = 0, n = 0;
                    for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= ir.Width || ny >= ir.Height) continue;
                        sum += tmp[ir.Index(nx, ny)];
                        n++;
                    }
                    if (n > 0) z[idx] = (sbyte)(sum / n);
                }
            }
        }

        ctx.Report.TilesTouched = touched;
    }

    private static int AmplitudeForBiome(BiomeId id, BiomeAltitudeJitterParams p) => id switch
    {
        BiomeId.Grassland => p.GrassJitter,
        BiomeId.Forest or BiomeId.DenseForest or BiomeId.Jungle => p.ForestJitter,
        BiomeId.Beach => p.SandJitter,
        BiomeId.Swamp or BiomeId.Wetland => p.SwampJitter,
        BiomeId.Desert or BiomeId.Savanna => p.DesertJitter,
        BiomeId.Snow or BiomeId.Tundra => p.SnowJitter,
        BiomeId.Mountain or BiomeId.HighMountain => p.SkipMountain ? 0 : p.MountainJitter,
        BiomeId.Cave => p.CaveJitter,
        // Water / Lava / Road / River → never jitter.
        _ => 0,
    };
}
