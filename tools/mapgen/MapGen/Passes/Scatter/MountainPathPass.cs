using CentrED.MapGen.IR;
using CentrED.MapGen.Pipeline;

namespace CentrED.MapGen.Passes.Scatter;

public sealed class MountainPathParams
{
    [TunableDisplay("Enabled", Tooltip = "Carve a winding dirt path through every Mountain biome region and decorate its edges with small rocks. Without this the mountain reads as a solid wall — the path is what makes it traversable in Felucca canon.")]
    public bool Enabled { get; set; } = true;

    [TunableDisplay("Path width (tiles)", Tooltip = "How thick the walkable dirt strip is. 12 = a wide mountain pass (vs a foot trail). Path edges automatically get the canonical 0x06EB-0x06F2 dirt↔mountain transition tiles.")]
    [TunableRange(1, 24)]
    public int PathWidth { get; set; } = 12;

    [TunableDisplay("Wave amplitude (fraction)", Tooltip = "How far the path meanders off the ridge centerline as a fraction of mountain half-thickness. 0 = straight line, 1 = full mountain width.")]
    [TunableRange(0.0, 1.0)]
    public double WaveAmplitude { get; set; } = 0.5;

    [TunableDisplay("Wave period (tiles)", Tooltip = "Tiles per full sinusoidal cycle of the path. Smaller = tighter switchbacks, larger = gentler sweep.")]
    [TunableRange(8, 128)]
    public int WavePeriod { get; set; } = 32;

    [TunableDisplay("Rock chance (0-1)", Tooltip = "Probability a path-INTERIOR cell (dirt tile) gets a small rock static. Rocks go ON the dirt path itself, not on adjacent mountain tiles — the mountain textures stay clean.")]
    [TunableRange(0.0, 1.0)]
    public double RockChance { get; set; } = 0.08;

    [TunableDisplay("Min rock spacing (tiles)", Tooltip = "Suppress path rocks closer than this to another path rock. Larger = sparser scatter.")]
    [TunableRange(1, 8)]
    public int RockMinSpacing { get; set; } = 4;

    [TunableDisplay("Path Z drop (vs ridge)", Tooltip = "How many Z units BELOW the surrounding mountain peak the path sits. Higher = deeper cut, reads as a valley/trench winding through the ridge. Without this drop the path sits flush with the mountain top which looks like painted dirt on a roof, not a traversable trail.")]
    [TunableRange(0, 60)]
    public int PathZDrop { get; set; } = 25;

    [TunableDisplay("Path floor Z (min)", Tooltip = "Hard floor for path cells — they will never go below this Z. Keeps the trench above sea level so CoastSmoothPass doesn't reclassify it as water.")]
    [TunableRange(-30, 30)]
    public int PathFloorZ { get; set; } = 2;
}

// Carves a winding dirt path through Mountain biome regions and lines its edges
// with small rock statics. Replaces the previous "splatter rocks everywhere on
// mountain interior" behaviour (which produced random clumps of stones across
// the ridge) with a coherent traversable path that reads as Felucca-canon.
//
// Algorithm:
//   1. For each X column, find the [minY..maxY] range of Mountain biome cells.
//   2. Compute path Y for this column = midpoint + amplitude·sin(2π·x/period).
//   3. For PathWidth tiles centered on path Y, replace the LandId with a dirt
//      tile (pool 0x71-0x74). Cells stay at their existing mountain Z so the
//      path naturally climbs/descends with the ridge.
//   4. For every cell immediately adjacent to a path cell (8-neighbour), roll
//      RockChance to drop a small rock static (0x1363-0x1367). Min-spacing
//      suppression keeps the rock line from feeling overcrowded.
//
// Reads:  Biome, LandId, Height.
// Writes: LandId, StaticOps.
public sealed class MountainPathPass : IGenerationPass
{
    public string Name => "Mountain Path";
    public string Category => "Scatter";

    public IrFields Reads => IrFields.Biome | IrFields.LandId | IrFields.Height;
    public IrFields Writes => IrFields.LandId | IrFields.Height | IrFields.StaticOps;

    public object CreateDefaultParams() => new MountainPathParams();

    // Dirt-path land tiles. 0x71-0x74 are the Felucca dirt/road family used on
    // Yew-style mountain paths. TODO verify against a Felucca path dump if these
    // render as the wrong texture in your tiledata.
    private static readonly ushort[] PathTiles = { 0x71, 0x72, 0x73, 0x74 };

    // Small rocks lining the path edges — the family the user identified as
    // "for any paths inside of mountains" (image 52 feedback).
    private static readonly ushort[] PathRocks = { 0x1363, 0x1364, 0x1365, 0x1366, 0x1367 };

    // 8-direction offsets used by the path-edge transition step. Hoisted out of
    // the inner loop so the stackalloc doesn't fire per-cell.
    private static readonly (int Dx, int Dy, byte Bit)[] DirOffsets =
    {
        (0, -1, 1 << 0), (1, -1, 1 << 1), (1, 0, 1 << 2), (1, 1, 1 << 3),
        (0,  1, 1 << 4), (-1, 1, 1 << 5), (-1, 0, 1 << 6), (-1, -1, 1 << 7),
    };

    public void Run(GenContext ctx, object parameters)
    {
        var p = (MountainPathParams)parameters;
        if (!p.Enabled) return;

        var ir = ctx.IR;
        if (ir.Biome is null || ir.LandId is null || ir.Height_Z is null) return;
        var biome = ir.Biome;
        var land = ir.LandId;
        var z = ir.Height_Z;
        var scope = ir.Scope;

        int sw = scope.Width;
        int sh = scope.Height;

        // Per-column min/max Y of Mountain biome cells.
        var minY = new int[sw];
        var maxY = new int[sw];
        for (int i = 0; i < sw; i++) { minY[i] = int.MaxValue; maxY[i] = int.MinValue; }

        for (ushort y = scope.Y1; y <= scope.Y2; y++)
        for (ushort x = scope.X1; x <= scope.X2; x++)
        {
            var b = (BiomeId)biome[ir.Index(x, y)];
            if (b != BiomeId.Mountain && b != BiomeId.HighMountain) continue;
            int lx = x - scope.X1;
            if (y < minY[lx]) minY[lx] = y;
            if (y > maxY[lx]) maxY[lx] = y;
        }

        // Carve path cells column-by-column.
        var pathSet = new HashSet<long>();
        int pathCells = 0;
        int period = Math.Max(2, p.WavePeriod);
        int halfWidth = Math.Max(1, p.PathWidth) / 2;
        int extra = Math.Max(1, p.PathWidth) - halfWidth - 1;

        for (int lx = 0; lx < sw; lx++)
        {
            if (minY[lx] == int.MaxValue) continue;     // no mountain in this column
            if (maxY[lx] - minY[lx] < p.PathWidth) continue; // ridge too thin to carve

            int gx = scope.X1 + lx;
            int centerY = (minY[lx] + maxY[lx]) / 2;
            int ridgeHalf = (maxY[lx] - minY[lx]) / 2;
            int amp = (int)(ridgeHalf * p.WaveAmplitude);
            double phase = lx * 2.0 * Math.PI / period;
            int pathY = centerY + (int)Math.Round(amp * Math.Sin(phase));

            // Keep the path one tile inside the ridge band so edges remain mountain.
            int top    = Math.Max(minY[lx] + 1, pathY - halfWidth);
            int bottom = Math.Min(maxY[lx] - 1, pathY + extra);

            for (int py = top; py <= bottom; py++)
            {
                int idx = ir.Index((ushort)gx, (ushort)py);
                var b = (BiomeId)biome[idx];
                if (b != BiomeId.Mountain && b != BiomeId.HighMountain) continue;
                land[idx] = LatticePick.Pick(PathTiles, gx, py, ir.Seed);
                // Drop the path Z below the surrounding mountain top so it reads
                // as a winding trench cut through the ridge, not painted dirt
                // sitting on the peak. Clamped above PathFloorZ to keep the path
                // above sea level (CoastSmoothPass would otherwise reclassify a
                // path cell that fell to Z<0 as water).
                int dropped = z[idx] - p.PathZDrop;
                if (dropped < p.PathFloorZ) dropped = p.PathFloorZ;
                z[idx] = (sbyte)Math.Clamp(dropped, sbyte.MinValue, sbyte.MaxValue);
                pathSet.Add(((long)gx << 32) | (uint)py);
                pathCells++;
            }
        }

        // Drop rocks ON path cells (the dirt tiles), not on adjacent mountain. The
        // user's feedback: mountain textures should stay clean — rocks belong on
        // the lower ground next to the rock face, not perched on the cliff. Path
        // is the canonical "lower ground next to mountain" surface inside the
        // ridge, so it gets the rocks.
        int rockSpacing = Math.Max(1, p.RockMinSpacing);
        var lastRocks = new HashSet<long>();
        int rockStatics = 0;

        foreach (var key in pathSet)
        {
            int px = (int)(key >> 32);
            int py = (int)(key & 0xFFFFFFFF);
            if (ctx.Rng.NextDouble() > p.RockChance) continue;

            // Min-spacing: reject if another rock was placed within RockMinSpacing.
            bool tooClose = false;
            for (int oy = -rockSpacing; oy <= rockSpacing && !tooClose; oy++)
            for (int ox = -rockSpacing; ox <= rockSpacing; ox++)
            {
                if (lastRocks.Contains(((long)(px + ox) << 32) | (uint)(py + oy))) { tooClose = true; break; }
            }
            if (tooClose) continue;

            int idx = ir.Index((ushort)px, (ushort)py);
            ushort rockId = PathRocks[ctx.Rng.Next(PathRocks.Length)];
            ir.StaticOps.Add(new StaticOp(StaticOpKind.Add, (ushort)px, (ushort)py, z[idx], rockId, 0));
            lastRocks.Add(key);
            rockStatics++;
        }

        // Mountain edges adjacent to the path get the canonical dirt↔mountain
        // transition tiles from DragonMod's dirt2mountain.txt. For each mountain
        // biome cell, build a bitmask of which 8 neighbours are path (dirt) cells
        // and replace the LandId with the directional transition tile:
        //   cardinal half (mountain-on-X / dirt-on-opposite): 0x06EB-0x06F2 family
        //   corner-cutout (mostly mountain, dirt on one corner): 0x00E4-0x00E7
        // This makes the path read as a real cut through the rock with sloped
        // cliff faces, not a flat dirt strip painted on top of mountain texture.
        int transitions = 0;
        for (ushort y = scope.Y1; y <= scope.Y2; y++)
        for (ushort x = scope.X1; x <= scope.X2; x++)
        {
            int idx = ir.Index(x, y);
            var b = (BiomeId)biome[idx];
            if (b != BiomeId.Mountain && b != BiomeId.HighMountain) continue;
            if (pathSet.Contains(((long)x << 32) | (uint)y)) continue;

            byte mask = 0;
            // bit 0=N, 1=NE, 2=E, 3=SE, 4=S, 5=SW, 6=W, 7=NW
            foreach (var (dx, dy, bit) in DirOffsets)
            {
                int nx = x + dx, ny = y + dy;
                if ((uint)nx >= (uint)ir.Width || (uint)ny >= (uint)ir.Height) continue;
                if (pathSet.Contains(((long)nx << 32) | (uint)ny)) mask |= bit;
            }
            if (mask == 0) continue;

            ushort transitionTile = PickDirtMountainTransition(mask);
            if (transitionTile == 0) continue;
            land[idx] = transitionTile;
            transitions++;
        }

        ctx.Report.StaticsAdded += rockStatics;
        ctx.Report.Notes.Add($"mountain path: {pathCells} dirt cells, {transitions} edge transitions, {rockStatics} path rocks");
    }

    // Pick a dirt↔mountain transition tile based on which neighbour cells are
    // path (dirt). Mask bit convention: 0=N, 1=NE, 2=E, 3=SE, 4=S, 5=SW, 6=W, 7=NW.
    //
    // Tile families from DragonMod's dirt2mountain.txt rules:
    //   Cardinal half (dirt on one side, mountain on the other):
    //     dirt on N → 0x06EB | dirt on E → 0x06ED | dirt on S → 0x06EE | dirt on W → 0x06EC
    //   Corner cutout (mostly mountain, small dirt corner):
    //     NW → 0x00E6 | NE → 0x00E7 | SW → 0x00E5 | SE → 0x00E4
    private static ushort PickDirtMountainTransition(byte mask)
    {
        bool n  = (mask & (1 << 0)) != 0;
        bool ne = (mask & (1 << 1)) != 0;
        bool e  = (mask & (1 << 2)) != 0;
        bool se = (mask & (1 << 3)) != 0;
        bool s  = (mask & (1 << 4)) != 0;
        bool sw = (mask & (1 << 5)) != 0;
        bool w  = (mask & (1 << 6)) != 0;
        bool nw = (mask & (1 << 7)) != 0;

        int dirtCount = System.Numerics.BitOperations.PopCount(mask);

        // Single-corner dirt → corner-cutout tile.
        if (dirtCount == 1)
        {
            if (nw) return 0x00E6;
            if (ne) return 0x00E7;
            if (sw) return 0x00E5;
            if (se) return 0x00E4;
            if (n)  return 0x06EB;
            if (e)  return 0x06ED;
            if (s)  return 0x06EE;
            if (w)  return 0x06EC;
        }

        // Dominant cardinal side picks the cardinal half-tile.
        int nSide = (n ? 1 : 0) + (ne ? 1 : 0) + (nw ? 1 : 0);
        int eSide = (e ? 1 : 0) + (ne ? 1 : 0) + (se ? 1 : 0);
        int sSide = (s ? 1 : 0) + (se ? 1 : 0) + (sw ? 1 : 0);
        int wSide = (w ? 1 : 0) + (nw ? 1 : 0) + (sw ? 1 : 0);
        int max = Math.Max(Math.Max(nSide, eSide), Math.Max(sSide, wSide));
        if (max == nSide) return 0x06EB;
        if (max == eSide) return 0x06ED;
        if (max == sSide) return 0x06EE;
        return 0x06EC;
    }
}
