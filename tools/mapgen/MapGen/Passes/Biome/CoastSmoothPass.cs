using CentrED.MapGen.Data;
using CentrED.MapGen.IR;
using CentrED.MapGen.Pipeline;

namespace CentrED.MapGen.Passes.Biome;

public sealed class CoastSmoothParams
{
    // Ignored: the pass uses GenIR.SeaLevelZ / OceanZ. Kept so presets that set it load.
    [TunableDisplay("Sea level Z (legacy, ignored)")] [TunableRange(-128, 127)]
    public int SeaLevelZ { get; set; } = 0;

    // Only used for cells without a biome (image import): Z <= sea + band becomes beach.
    [TunableDisplay("Beach band width (Z)")] [TunableRange(1, 8)]
    public int BeachBand { get; set; } = 2;

    // Off by default: Land Transitions paints sand/grass-to-water edges from the brush table
    // by neighbour mask; the mined shoreline table fought it.
    [TunableDisplay("Use directional shoreline tiles")]
    public bool UseDirectionalShorelines { get; set; } = false;

    [TunableDisplay("Brush coherence (tiles)", Tooltip = "Region size that shares the same brush variant. 0 = per-cell random (Felucca-like).")]
    [TunableRange(0, 16)]
    // Default 0 = per-cell random. Real Felucca's all-sand desert island dump shows the
    // 0x16-0x1C beach variants mixed at 1-tile granularity; any NxN coherence (was 4)
    // produces visible diamond banding when adjacent blocks pick different variants.
    public int BrushCoherence { get; set; } = 0;
}

// Coast pass. Water is decided by BIOME (Deep/Shallow/River), Z only for cells without a
// biome. Water cells get a lattice-picked water tile at GenIR.OceanZ (one flat plane).
// Land is never turned into water and never lifted to sea level; it is only kept above
// the water plane. Optional (off): directional shoreline tiles from TileTables.Shorelines.
public sealed class CoastSmoothPass : IGenerationPass
{
    public string Name => "Coast Smooth";
    public string Category => "Biome";

    public IrFields Reads => IrFields.Height | IrFields.LandId | IrFields.Biome;
    public IrFields Writes => IrFields.LandId;

    public object CreateDefaultParams() => new CoastSmoothParams();

    public void Run(GenContext ctx, object parameters)
    {
        var p = (CoastSmoothParams)parameters;
        var ir = ctx.IR;
        if (ir.LandId is null || ir.Height_Z is null) return;
        var z = ir.Height_Z;
        var l = ir.LandId;
        var bio = ir.Biome; // null for pipelines that never assign biomes — Z decides then
        var beach = ir.Tables.Beach.Length > 0 ? ir.Tables.Beach : new ushort[] { 0x16 };
        var water = ir.Tables.Water.Length > 0 ? ir.Tables.Water : new ushort[] { 0xA8 };
        var shorelinesSafe = FilterWaterFromShorelines(ir.Tables.Shorelines);
        var beachSafe = FilterWaterFromArray(beach);
        if (beachSafe.Length == 0) beachSafe = new ushort[] { 0x16 };
        var scope = ir.Scope;
        int sea = ir.SeaLevelZ;
        int oceanZ = ir.OceanZ;
        int touched = 0, directionalApplied = 0, raised = 0;

        int coh = Math.Max(0, p.BrushCoherence);
        int cohShift = coh <= 0 ? 0 : (int)Math.Log2(NextPow2(coh));

        for (ushort y = scope.Y1; y <= scope.Y2; y++)
        for (ushort x = scope.X1; x <= scope.X2; x++)
        {
            int idx = ir.Index(x, y);
            var bId = bio is null ? BiomeId.Unassigned : (BiomeId)bio[idx];
            bool isWater = bId is BiomeId.DeepWater or BiomeId.ShallowWater or BiomeId.River
                           || (bId == BiomeId.Unassigned && z[idx] < sea);

            if (isWater)
            {
                // Flat water on the ocean plane; lattice pick (Random.Next(4) was biased).
                l[idx] = LatticePick.Pick(water, x, y, ir.Seed);
                z[idx] = (sbyte)oceanZ;
                touched++;
                continue;
            }

            // Land is classified by biome, never by Z. River banks are deliberately below
            // sea level (stepping down to the water plane); only land at or under the water
            // plane itself is lifted (dry ground drawn under the water surface). The old code
            // lifted every land cell below 0 to 0, which rebuilt a wall around every river.
            if (z[idx] <= oceanZ)
            {
                z[idx] = (sbyte)(oceanZ + 1);
                raised++;
            }

            // Beach/unassigned low cells: beach tiles (directional shorelines optional).
            bool beachCell = bId == BiomeId.Beach
                             || (bId == BiomeId.Unassigned && z[idx] <= sea + p.BeachBand);
            if (!beachCell) continue;
            if (p.UseDirectionalShorelines)
            {
                int mask = WaterMask(ir, bio, z, x, y, sea);
                var tiles = mask > 0 && mask < 16 ? shorelinesSafe[mask] : null;
                if (tiles is { Length: > 0 })
                {
                    l[idx] = PickCoherent(tiles, x, y, cohShift, ctx);
                    touched++; directionalApplied++;
                    continue;
                }
            }
            if (bId == BiomeId.Unassigned)
            {
                l[idx] = PickCoherent(beachSafe, x, y, cohShift, ctx);
                touched++;
            }
        }

        ctx.Report.TilesTouched = touched;
        ctx.Report.Notes.Add($"water cells={touched - directionalApplied}, directional={directionalApplied}, land lifted above water plane={raised}");
    }

    private static ushort[][] FilterWaterFromShorelines(ushort[][] src)
    {
        var dst = new ushort[src.Length][];
        for (int i = 0; i < src.Length; i++)
            dst[i] = src[i] is null ? Array.Empty<ushort>() : FilterWaterFromArray(src[i]);
        return dst;
    }

    private static ushort[] FilterWaterFromArray(ushort[] src)
    {
        if (src is null || src.Length == 0) return Array.Empty<ushort>();
        var list = new List<ushort>(src.Length);
        foreach (var id in src) if (!TileFlags.IsWaterLandId(id)) list.Add(id);
        return list.ToArray();
    }

    private static int NextPow2(int v)
    {
        v--; v |= v >> 1; v |= v >> 2; v |= v >> 4; v |= v >> 8; v |= v >> 16;
        return v + 1;
    }

    // Region-coherent variant pick: tiles in the same NxN block share the same index.
    private static ushort PickCoherent(ushort[] tiles, int x, int y, int cohShift, GenContext ctx)
    {
        if (tiles.Length == 1) return tiles[0];
        if (cohShift == 0) return LatticePick.Pick(tiles, x, y, ctx.IR.Seed);
        uint hash = unchecked((uint)((x >> cohShift) * 73856093) ^ (uint)((y >> cohShift) * 19349663) ^ (uint)ctx.IR.Seed);
        return tiles[hash % (uint)tiles.Length];
    }

    // 4-bit mask: bit 0 = N, bit 1 = E, bit 2 = S, bit 3 = W. Water by biome when known.
    private static int WaterMask(GenIR ir, byte[]? bio, sbyte[] z, ushort x, ushort y, int seaLevel)
    {
        int mask = 0;
        if (y > 0 && IsWater(ir.Index(x, y - 1))) mask |= 1;
        if (x + 1 < ir.Width && IsWater(ir.Index(x + 1, y))) mask |= 2;
        if (y + 1 < ir.Height && IsWater(ir.Index(x, y + 1))) mask |= 4;
        if (x > 0 && IsWater(ir.Index(x - 1, y))) mask |= 8;
        return mask;

        bool IsWater(int i)
        {
            if (bio is not null && (BiomeId)bio[i] != BiomeId.Unassigned)
                return (BiomeId)bio[i] is BiomeId.DeepWater or BiomeId.ShallowWater or BiomeId.River;
            return z[i] < seaLevel;
        }
    }
}
