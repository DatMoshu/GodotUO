using CentrED.MapGen.IR;
using CentrED.MapGen.Pipeline;
using CentrED.MapGen.Stamps;

namespace CentrED.MapGen.Passes.Stamp;

public sealed class StampScatterParams
{
    [TunableDisplay("Stamps root", Tooltip = "Stamp library folder (mined from your client data; lives in the generator data folder)")]
    public string StampsRoot { get; set; } = StampLoader.DefaultRoot;

    [TunableDisplay("Forest density (per 1000 tiles)")] [TunableRange(0, 50)]
    public double ForestDensityPer1000 { get; set; } = 8.0;

    [TunableDisplay("Sea level offset", Tooltip = "Added to GenIR.SeaLevelZ (the rebased frame: sea level is 0).")] [TunableRange(-128, 127)]
    public int SeaLevelZ { get; set; } = 0;

    [TunableDisplay("Replace land tiles from stamps", Tooltip = "Only for unrotated placements; rotated land would break directional transition art.")]
    public bool PaintLand { get; set; } = false;

    [TunableDisplay("Use stamp rotation+mirror variants", Tooltip = "Only orientation-free kinds (forest) are ever rotated; tree clusters keep their shape.")]
    public bool UseVariants { get; set; } = true;

    [TunableDisplay("Max local slope for placement", Tooltip = "Reject placement when max-min terrain Z over the stamp footprint exceeds this. Lower = stricter.")]
    [TunableRange(1, 64)]
    public int MaxLocalSlope { get; set; } = 12;

    [TunableDisplay("Region coherence", Tooltip = "Chance a placement uses its 64x64 region's preferred stamp (same species across a region); otherwise a weighted random stamp.")]
    [TunableRange(0.0, 1.0)]
    public double RegionCoherence { get; set; } = 0.6;
}

// Composes forest cover from mined 'forest' stamps over Forest/DenseForest/Jungle/
// Grassland. Uses the shared StampLoader/StampPlacer: the whole footprint must be in
// those biomes, dry and within MaxLocalSlope; statics keep their height above the
// ground of their own tile (PerTile grounding) so trees do not float on slopes; the
// occupancy grid stops stamps overlapping each other and clears scatter trees under
// a stamp instead of stacking on them.
public sealed class StampScatterPass : IGenerationPass
{
    private static readonly HashSet<BiomeId> ForestBiomes = new()
    {
        BiomeId.Forest, BiomeId.DenseForest, BiomeId.Jungle, BiomeId.Grassland,
    };

    public string Name => "Stamp Scatter";
    public string Category => "Scatter";

    public IrFields Reads => IrFields.Biome | IrFields.LandId | IrFields.Height;
    public IrFields Writes => IrFields.StaticOps | IrFields.LandId;

    public object CreateDefaultParams() => new StampScatterParams();

    public void Run(GenContext ctx, object parameters)
    {
        var p = (StampScatterParams)parameters;
        var ir = ctx.IR;
        if (ir.Biome is null) return;

        var lib = StampPassSupport.OpenLibrary(ctx, p.StampsRoot);
        int opsBefore = ir.StaticOps.Count, corruptBefore = lib.Errors.Count;
        if (lib.TotalCount == 0)
        {
            ctx.Report.Warnings.Add($"no stamps found at {lib.Root}");
            return;
        }
        var forestStamps = lib.GetKind("forest");
        if (forestStamps.Count == 0)
        {
            ctx.Report.Warnings.Add("no forest stamps in library");
            return;
        }
        ctx.Report.Notes.Add($"library: {lib.TotalCount} stamps total ({(lib.FromIndex ? "index.json" : "scanned")}), {forestStamps.Count} forest");

        // Species pools per biome by tag; fall back to every forest stamp.
        var pools = new Dictionary<BiomeId, IReadOnlyList<LoadedStamp>>();
        foreach (var b in ForestBiomes)
        {
            string tag = b switch
            {
                BiomeId.Grassland => "grassland",
                BiomeId.Jungle => "jungle",
                _ => "forest",
            };
            var pool = forestStamps.Where(s => s.Tags.Contains(tag)).ToList();
            pools[b] = pool.Count > 0 ? pool : forestStamps;
        }

        var opt = new StampPlaceOptions
        {
            SeaLevelZ = ir.SeaLevelZ + p.SeaLevelZ,
            MaxLocalSlope = p.MaxLocalSlope,
            PaintLand = p.PaintLand,
            Grounding = StampGrounding.PerTile,
            AllowedBiomes = ForestBiomes,
            Source = Name,
        };

        int targetCount = (int)((ir.Scope.Width * (long)ir.Scope.Height / 1000.0) * p.ForestDensityPer1000);
        int placed = 0;
        var rejects = new Dictionary<string, int>();
        for (int attempt = 0; attempt < targetCount * 4 && placed < targetCount; attempt++)
        {
            ctx.Cancellation.ThrowIfCancellationRequested();
            int x = ctx.Rng.Next(ir.Scope.X1, ir.Scope.X2 + 1);
            int y = ctx.Rng.Next(ir.Scope.Y1, ir.Scope.Y2 + 1);
            var biome = (BiomeId)ir.Biome[ir.Index(x, y)];
            if (!ForestBiomes.Contains(biome)) continue;
            var pool = pools[biome];

            // Region-coherent pick: a 64x64 super-cell prefers one stamp so neighbouring
            // placements share a species; when that stamp does not fit here, fall back to a
            // weighted random one instead of leaving the region empty.
            bool coherent = ctx.Rng.NextDouble() < p.RegionCoherence;
            LoadedStamp stamp;
            if (coherent)
            {
                ulong h = StableHash.Mix(StableHash.Mix(StableHash.Fnv1a64(Name), (ulong)(x >> 6)), ((ulong)(y >> 6) << 8) | (byte)biome);
                stamp = PickByUnit(pool, (h >> 11) * (1.0 / (1UL << 53)));
            }
            else stamp = StampPlacer.PickWeighted(pool, ctx.Rng);

            var variant = StampVariantPolicy.Pick(stamp, ctx.Rng, p.UseVariants);
            if (StampPlacer.TryPlace(ir, stamp, x, y, variant, opt, out var plan)) { placed++; continue; }
            if (coherent)
            {
                stamp = StampPlacer.PickWeighted(pool, ctx.Rng);
                variant = StampVariantPolicy.Pick(stamp, ctx.Rng, p.UseVariants);
                if (StampPlacer.TryPlace(ir, stamp, x, y, variant, opt, out plan)) { placed++; continue; }
            }
            StampPassSupport.CountReject(rejects, plan.RejectReason);
        }
        ctx.Report.Notes.Add(StampPassSupport.Summary(placed, rejects));
        StampPassSupport.Finish(ctx, lib, opsBefore, corruptBefore);
    }

    // Weighted pick driven by a unit value in [0,1) instead of the RNG.
    private static LoadedStamp PickByUnit(IReadOnlyList<LoadedStamp> stamps, double unit)
    {
        double total = 0;
        foreach (var s in stamps) total += Math.Max(0, s.Weight);
        if (total <= 0) return stamps[Math.Min(stamps.Count - 1, (int)(unit * stamps.Count))];
        double r = unit * total;
        foreach (var s in stamps)
        {
            r -= Math.Max(0, s.Weight);
            if (r < 0) return s;
        }
        return stamps[^1];
    }
}
