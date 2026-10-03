using CentrED.MapGen.IR;
using CentrED.MapGen.Pipeline;

namespace CentrED.MapGen.Passes.Network;

public sealed class RoadCenterlineParams
{
    // Total road width in tiles (1 = the centerline only, 2 = centerline + one side,
    // 3 = one each side). The old code painted a (2*Width-1)-square, so Width=2 meant 3.
    [TunableDisplay("Road width (tiles)", Tooltip = "Tiles across the road. Felucca's country roads are 2-3 wide.")]
    [TunableRange(1, 5)]
    public int Width { get; set; } = 2;

    // 0x518 (the old default) has no land texture; it now maps to cobblestones 0x3E9-0x3EC.
    [TunableDisplay("Cobble tile id", Tooltip = "Any of 0x3E9-0x3EC = the cobblestone family (varied per tile).")]
    public int CobbleTileId { get; set; } = 0x3E9;

    [TunableDisplay("Dirt path tile id", Tooltip = "Any of 0x71-0x78 = the dirt family (varied per tile).")]
    public int DirtTileId { get; set; } = 0x71;

    [TunableDisplay("Stone road tile id")] public int StoneTileId { get; set; } = 0x53B;

    [TunableDisplay("Flatten radius", Tooltip = "Tiles beside the road whose Z is blended toward the road surface.")]
    [TunableRange(0, 6)]
    public int FlattenRadius { get; set; } = 3;

    [TunableDisplay("Profile smoothing (tiles)", Tooltip = "Half-window of the moving average applied to the road's Z along its length.")]
    [TunableRange(0, 32)]
    public int ProfileWindow { get; set; } = 6;

    [TunableDisplay("Max Z step along road")] [TunableRange(1, 8)]
    public int MaxStep { get; set; } = 2;

    // Ignored: water is decided by biome. Kept for preset compat.
    [TunableDisplay("Sea level Z (legacy, ignored)")] [TunableRange(-128, 127)]
    public int SeaLevelZ { get; set; } = 0;

    [TunableDisplay("Paint biome transitions at edges", Tooltip = "Use LandBrushTable to paint <biome> -> Dirt/Cobble edge tiles beside the road")]
    public bool PaintTransitions { get; set; } = true;
}

// F-5 (Ryandor §6): paints every RoadSegment as a Width-wide road with varied dirt or
// cobble tiles, gives it a smoothed Z profile along its length (no stair-steps over
// hills), blends the shoulders toward it, and paints brush edge tiles on the land
// beside it ("Grassland -> Dirt", "Grassland -> Cobble", ...). Road cells get
// Biome=Road so scatter passes keep trees and rocks off them.
//
// Slot order: AFTER RoadGraphPass, BEFORE RoadStampPass.
public sealed class RoadCenterlinePass : IGenerationPass
{
    public string Name => "Road Centerline";
    public string Category => "Network";

    public IrFields Reads => IrFields.Roads | IrFields.Height | IrFields.LandId | IrFields.Biome;
    public IrFields Writes => IrFields.LandId | IrFields.Height | IrFields.Biome;

    public object CreateDefaultParams() => new RoadCenterlineParams();

    public void Run(GenContext ctx, object parameters)
    {
        var p = (RoadCenterlineParams)parameters;
        var ir = ctx.IR;
        if (ir.Roads.Count == 0)
        {
            ctx.Report.Notes.Add("no roads — Road Graph / Town Roads produced none");
            return;
        }
        if (ir.Height_Z is null) return;
        ir.EnsureLandId();
        var z = ir.Height_Z;
        var l = ir.LandId!;
        var dirt = RoadPaint.PoolFor(p.DirtTileId, RoadPaint.DirtTiles);
        var cobble = RoadPaint.PoolFor(p.CobbleTileId, RoadPaint.CobbleTiles);
        var stone = RoadPaint.PoolFor(p.StoneTileId, new[] { (ushort)p.StoneTileId });

        int painted = 0, flattened = 0, transitions = 0;
        var dirtCells = new HashSet<int>();
        var cobbleCells = new HashSet<int>();
        var roadZ = new Dictionary<int, int>();

        int lo = -(Math.Max(1, p.Width) - 1) / 2;
        int hi = lo + Math.Max(1, p.Width) - 1;

        foreach (var seg in ir.Roads)
        {
            if (seg.Path.Count == 0) continue;
            var pool = seg.Kind switch
            {
                RoadKind.Cobble => cobble,
                RoadKind.Stone => stone,
                _ => dirt,
            };
            var set = seg.Kind == RoadKind.Cobble ? cobbleCells : dirtCells;
            var prof = RoadPaint.SmoothProfile(ir, seg.Path, p.ProfileWindow, Math.Max(1, p.MaxStep));
            for (int k = 0; k < seg.Path.Count; k++)
            {
                var (cx, cy) = seg.Path[k];
                for (int dy = lo; dy <= hi; dy++)
                for (int dx = lo; dx <= hi; dx++)
                {
                    int nx = cx + dx, ny = cy + dy;
                    if ((uint)nx >= ir.Width || (uint)ny >= ir.Height) continue;
                    if (nx < ir.Scope.X1 || ny < ir.Scope.Y1 || nx > ir.Scope.X2 || ny > ir.Scope.Y2) continue;
                    int idx = ir.Index(nx, ny);
                    if (RoadPaint.IsWater(ir, idx)) continue; // never pave water
                    if (cobbleCells.Contains(idx) && set == dirtCells) continue; // town streets win
                    l[idx] = LatticePick.Pick(pool, nx, ny, ir.Seed);
                    if (ir.Biome is not null) ir.Biome[idx] = (byte)BiomeId.Road;
                    // First writer wins the Z so crossings stay level.
                    if (!roadZ.ContainsKey(idx)) roadZ[idx] = prof[k];
                    if (set == cobbleCells) dirtCells.Remove(idx);
                    set.Add(idx);
                    painted++;
                }
            }
        }

        foreach (var (idx, rz) in roadZ)
        {
            if (z[idx] != rz) { z[idx] = (sbyte)Math.Clamp(rz, sbyte.MinValue, sbyte.MaxValue); flattened++; }
        }

        // Shoulders: blend toward the nearest road cell's Z over FlattenRadius tiles.
        if (p.FlattenRadius > 0)
        {
            int r = p.FlattenRadius;
            var best = new Dictionary<int, (int Dist, int Z)>();
            foreach (var (idx, rz) in roadZ)
            {
                int cx = idx % ir.Width, cy = idx / ir.Width;
                for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    int nx = cx + dx, ny = cy + dy;
                    if (nx < ir.Scope.X1 || ny < ir.Scope.Y1 || nx > ir.Scope.X2 || ny > ir.Scope.Y2) continue;
                    int n = ir.Index(nx, ny);
                    if (roadZ.ContainsKey(n) || RoadPaint.IsWater(ir, n)) continue;
                    int d = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    if (!best.TryGetValue(n, out var cur) || d < cur.Dist || (d == cur.Dist && rz < cur.Z))
                        best[n] = (d, rz);
                }
            }
            foreach (var (n, (d, rz)) in best)
            {
                double t = 1.0 - (double)d / (r + 1);
                int nz = (int)Math.Round(z[n] + (rz - z[n]) * t);
                if (ir.Biome is not null && (BiomeId)ir.Biome[n] is BiomeId.Mountain or BiomeId.HighMountain)
                    nz = Math.Max(nz, z[n] - (z[n] - rz) / 2); // cut into rock only halfway
                if (nz != z[n]) { z[n] = (sbyte)Math.Clamp(nz, sbyte.MinValue, sbyte.MaxValue); flattened++; }
            }
        }

        if (p.PaintTransitions)
        {
            transitions += RoadPaint.PaintEdges(ir, dirtCells, "Dirt", skipMountain: false);
            transitions += RoadPaint.PaintEdges(ir, cobbleCells, "Cobble", skipMountain: false);
        }

        ctx.Report.TilesTouched = painted + flattened + transitions;
        ctx.Report.Notes.Add($"road cells dirt={dirtCells.Count} cobble={cobbleCells.Count}, width={p.Width}, z-adjusted={flattened}, edge tiles={transitions}");
    }
}
