using CentrED.MapGen.IR;
using CentrED.MapGen.Pipeline;
using CentrED.MapGen.Passes.Network;

namespace CentrED.MapGen.Passes.Biome;

public sealed class CliffEdgeParams
{
    [TunableDisplay("Edge ring width (tiles)", Tooltip = "How many cells in from the mountain boundary get rewritten to cliff-face tiles. 1 = thin rim, 2 = thicker cliff band.")]
    [TunableRange(0, 4)]
    public int RingWidth { get; set; } = 1;

    [TunableDisplay("Drop edge Z toward neighbour", Tooltip = "If true, edge cells lower their altitude partway toward the adjacent non-mountain cell so the cliff visibly steps down instead of floating flat.")]
    public bool TerraceZ { get; set; } = true;

    [TunableDisplay("Terrace Z fraction (0..100)", Tooltip = "Edge Z = lerp(mountain_z, neighbour_min_z, fraction/100). 50 = halfway down.")]
    [TunableRange(0, 100)]
    public int TerraceFraction { get; set; } = 50;

    [TunableDisplay("Skip if HighMountain", Tooltip = "Don't terrace HighMountain biome (treat as plateau interior).")]
    public bool SkipHighMountain { get; set; } = false;
}

// Cliff-face tiles for Mountain↔dirt-path borders (e.g. roads carved through mountain).
// Per real-Felucca dumps: 0x00DC-0x00DF are NOT grass↔mountain transition tiles; they
// only appear where DIRT-PATH tiles sit inside a Mountain region, lining the edge of
// the path with cliff-face rock. Mountain↔grass uses natural iso texture blending
// from the smooth 0x022C-0x022F mountain top tiles directly meeting grass — no special
// edge tile needed.
//
// This pass walks every Mountain cell whose neighbour LandId is a dirt-path tile
// (0x71-0x78 "dirt big/small stones" per DragonMod maptrans.txt block (09)) and
// rewrites it to a 0xDC-0xDF cliff-face tile. Cells whose neighbours are grass,
// forest, water, or any non-path biome are LEFT ALONE — they get the natural
// mountain-top edge.
//
// The directional pick uses a coarse 4-way rule based on where the dirt-path
// neighbours sit relative to the centre cell:
//   path mostly to the N  → 0xDC (north-facing cliff)
//   path mostly to the E  → 0xDD (east-facing cliff)
//   path mostly to the S  → 0xDE (south-facing cliff)
//   path mostly to the W  → 0xDF (west-facing cliff)
// Diagonal-only matches fall back to random pick to avoid empty cells.
//
// Pairs with: a future RoadCarvePass that paints dirt-path land tiles inside Mountain
// biome — at which point this pass produces the proper "carved mountain road" look
// from real Felucca (image 30). Until then it does nothing on synthetic islands,
// which is correct: islands without paths shouldn't have cliff-face tiles.
//
// Reads:  Biome, LandId, Height.
// Writes: LandId, Height.
public sealed class CliffEdgePass : IGenerationPass
{
    public string Name => "Cliff Edge";
    public string Category => "Biome";

    public IrFields Reads  => IrFields.Biome | IrFields.LandId | IrFields.Height;
    public IrFields Writes => IrFields.LandId | IrFields.Height;

    public object CreateDefaultParams() => new CliffEdgeParams();

    // Cliff-face tile IDs. Index is the dominant dirt-path-neighbour direction:
    //   0 = N, 1 = E, 2 = S, 3 = W.
    private static readonly ushort[] CliffByDir = { 0x00DC, 0x00DD, 0x00DE, 0x00DF };

    // Dirt-path land tile IDs per DragonMod maptrans.txt block (09): "dirt big stones,
    // dirt small stones" — the canonical UO dirt-path family. 0x70 included as the
    // dirt-only variant occasionally used as a path tile.
    private static bool IsDirtPathLand(ushort id) => id >= 0x0070 && id <= 0x0078;

    public void Run(GenContext ctx, object parameters)
    {
        var p = (CliffEdgeParams)parameters;
        var ir = ctx.IR;
        if (ir.Biome is null || ir.LandId is null || ir.Height_Z is null) return;

        var biome = ir.Biome;
        var land = ir.LandId;
        var z = ir.Height_Z;
        var scope = ir.Scope;
        int ring = System.Math.Max(1, p.RingWidth);

        // Snapshot land + height: we mutate them in-place, but the edge classifier
        // must read pre-mutation neighbour state to avoid re-classifying just-rewritten
        // cliff cells as path neighbours of further-inland cells.
        var landSnap = new ushort[land.Length];
        System.Array.Copy(land, landSnap, land.Length);
        var zSnap = new sbyte[z.Length];
        System.Array.Copy(z, zSnap, z.Length);

        int rewritten = 0, terraced = 0;

        for (ushort y = scope.Y1; y <= scope.Y2; y++)
        for (ushort x = scope.X1; x <= scope.X2; x++)
        {
            int idx = ir.Index(x, y);
            var b = (BiomeId)biome[idx];
            if (b != BiomeId.Mountain && b != BiomeId.HighMountain) continue;
            if (b == BiomeId.HighMountain && p.SkipHighMountain) continue;
            // Only still-interior rock: cells Land Transitions / roads already gave an edge
            // tile keep it (the old pass overwrote them).
            if (!IsMountainInterior(landSnap[idx])) continue;

            // Only fire when a neighbour is a DIRT-PATH land tile. Grass/forest/water/
            // sand etc. are deliberately ignored — natural iso blending handles those
            // boundaries without a special edge tile (per real-Felucca dumps in images
            // 28/29: zero 0xDC-0xDF tiles at grass↔mountain boundaries).
            int dirN = 0, dirE = 0, dirS = 0, dirW = 0;
            int minNeighbourZ = int.MaxValue;
            bool isEdge = false;
            for (int r = 1; r <= ring; r++)
            {
                if (CheckPath(landSnap, ir, x, y - r)) { dirN++; isEdge = true; minNeighbourZ = System.Math.Min(minNeighbourZ, SafeZ(zSnap, ir, x, y - r)); }
                if (CheckPath(landSnap, ir, x + r, y)) { dirE++; isEdge = true; minNeighbourZ = System.Math.Min(minNeighbourZ, SafeZ(zSnap, ir, x + r, y)); }
                if (CheckPath(landSnap, ir, x, y + r)) { dirS++; isEdge = true; minNeighbourZ = System.Math.Min(minNeighbourZ, SafeZ(zSnap, ir, x, y + r)); }
                if (CheckPath(landSnap, ir, x - r, y)) { dirW++; isEdge = true; minNeighbourZ = System.Math.Min(minNeighbourZ, SafeZ(zSnap, ir, x - r, y)); }
            }
            if (!isEdge) continue;

            // Dominant cardinal direction. Diagonal path neighbours add weight to both
            // adjacent cardinals; a remaining tie goes to the first cardinal in N,E,S,W
            // order among the tied ones, chosen by a per-cell hash (deterministic, no RNG).
            int[] counts = { dirN * 2, dirE * 2, dirS * 2, dirW * 2 };
            if (CheckPath(landSnap, ir, x + 1, y - 1)) { counts[0]++; counts[1]++; }
            if (CheckPath(landSnap, ir, x + 1, y + 1)) { counts[1]++; counts[2]++; }
            if (CheckPath(landSnap, ir, x - 1, y + 1)) { counts[2]++; counts[3]++; }
            if (CheckPath(landSnap, ir, x - 1, y - 1)) { counts[3]++; counts[0]++; }
            int best = counts.Max();
            var tied = Enumerable.Range(0, 4).Where(i => counts[i] == best).ToArray();
            int dir = tied.Length == 1 ? tied[0] : tied[CentrED.MapGen.Noise.FieldOps.Hash(x, y, ir.Seed) % (uint)tied.Length];
            ushort cliffId = CliffByDir[dir];
            // Preserve the established elevation pass. With a real dirt/rock
            // brush, the transition is painted on the road after terracing.
            if (!ir.Brushes.Brushes.TryGetValue("Dirt", out var dirtBrush) || !dirtBrush.Transitions.ContainsKey("Mountain"))
                land[idx] = cliffId;
            rewritten++;

            if (p.TerraceZ && minNeighbourZ != int.MaxValue)
            {
                int mountainZ = zSnap[idx];
                int newZ = mountainZ + ((minNeighbourZ - mountainZ) * p.TerraceFraction) / 100;
                z[idx] = (sbyte)System.Math.Clamp(newZ, sbyte.MinValue, sbyte.MaxValue);
                terraced++;
            }
        }

        var roads = new HashSet<int>();
        for (int i = 0; i < land.Length; i++)
            if ((BiomeId)biome[i] == BiomeId.Road && IsDirtPathLand(land[i])) roads.Add(i);
        int blended = RoadPaint.PaintMountainEdges(ir, roads);
        ctx.Report.TilesTouched = rewritten + blended;
        ctx.Report.Notes.Add($"dirt-owned mountain transitions={blended}; original terrace heights retained");
        ctx.Report.Notes.Add($"cliff-rewrite={rewritten} terraced={terraced}");
    }

    private static bool IsMountainInterior(ushort id) => id >= 0x022C && id <= 0x022F;

    private static bool CheckPath(ushort[] land, GenIR ir, int x, int y)
    {
        if (x < 0 || y < 0 || x >= ir.Width || y >= ir.Height) return false;
        return IsDirtPathLand(land[ir.Index((ushort)x, (ushort)y)]);
    }

    private static int SafeZ(sbyte[] z, GenIR ir, int x, int y)
    {
        if (x < 0 || y < 0 || x >= ir.Width || y >= ir.Height) return 0;
        return z[ir.Index((ushort)x, (ushort)y)];
    }
}
