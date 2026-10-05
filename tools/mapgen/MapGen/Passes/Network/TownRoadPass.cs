using CentrED.MapGen.IR;
using CentrED.MapGen.Pipeline;

namespace CentrED.MapGen.Passes.Network;

public sealed class TownRoadParams
{
    // 0x518 (the old default) has no land texture; it now maps to cobblestones.
    [TunableDisplay("Cobble tile id",
        Tooltip = "Any of 0x3E9-0x3EC = the cobblestone family (varied per tile). Same default as RoadCenterlinePass.")]
    public int CobbleTileId { get; set; } = 0x3E9;

    [TunableDisplay("Cross-axis road width (cells)", Tooltip = "Tiles across the main streets. 3 = Felucca town street.")]
    [TunableRange(1, 5)]
    public int Width { get; set; } = 3;

    [TunableDisplay("Flatten Z inside town",
        Tooltip = "If on, the whole footprint is levelled to one Z (see Flatten target; buildings need flat ground) and a skirt around it is blended. Off keeps the natural terrain undulation.")]
    public bool FlattenInteriorZ { get; set; } = true;

    [TunableDisplay("Flatten skirt (tiles)", Tooltip = "Blend band around a flattened footprint, from the plateau Z back to the natural terrain.")]
    [TunableRange(0, 16)]
    public int FlattenSkirt { get; set; } = 3;

    [TunableDisplay("Flatten target", Tooltip = "centre: level to the footprint's centre cell Z. poi: level to the town's own Z (a Town Sites 'Sites' entry's z, or its median ground Z).")]
    public string FlattenTarget { get; set; } = "centre";

    [TunableDisplay("Paint streets", Tooltip = "Paint the cobble cross and gate stubs. Off = only flatten the footprint (a bare district pad).")]
    public bool PaintStreets { get; set; } = true;
}

// For each Town POI with a Footprint, paint a cobble cross at the footprint midline
// and a stub from each Gate cell to the cross. Output is **pure LandId tiles** — no
// stamps, no statics. RoadCenterlinePass picks the new segments up automatically via
// ir.Roads and does its own width/flatten/transition pass on top.
//
// Slot: after TownSiteFinderPass, before RoadGraphPass — so the inter-town A* gets
// the existing-road cost bonus on the painted cells and so each town has gates that
// RoadGraph can target.
public sealed class TownRoadPass : IGenerationPass
{
    public string Name => "Town Roads";
    public string Category => "Network";

    public IrFields Reads => IrFields.Pois | IrFields.Height | IrFields.LandId;
    public IrFields Writes => IrFields.LandId | IrFields.Height | IrFields.Roads;

    public object CreateDefaultParams() => new TownRoadParams();

    public void Run(GenContext ctx, object parameters)
    {
        var p = (TownRoadParams)parameters;
        var ir = ctx.IR;
        if (ir.LandId is null || ir.Height_Z is null) return;
        if (ir.Pois.Count == 0) { ctx.Report.Notes.Add("no POIs; skipped"); return; }
        bool targetPoi = string.Equals(p.FlattenTarget, "poi", StringComparison.OrdinalIgnoreCase);
        if (!targetPoi && !string.Equals(p.FlattenTarget, "centre", StringComparison.OrdinalIgnoreCase))
            ctx.Report.Warnings.Add($"unknown FlattenTarget '{p.FlattenTarget}' (centre or poi); using centre");

        var tiles = RoadPaint.PoolFor(p.CobbleTileId, RoadPaint.CobbleTiles);
        int painted = 0;
        int townsHandled = 0;

        foreach (var poi in ir.Pois)
        {
            if (poi.Kind != PoiKind.Town || poi.Footprint is not { } rect) continue;
            ctx.Cancellation.ThrowIfCancellationRequested();

            // Centre cell + axes.
            int cx = (rect.X1 + rect.X2) / 2;
            int cy = (rect.Y1 + rect.Y2) / 2;
            sbyte targetZ = targetPoi ? poi.Z : ir.Height_Z[ir.Index(cx, cy)];
            if (p.FlattenInteriorZ) FlattenFootprint(ir, rect, targetZ, Math.Max(0, p.FlattenSkirt));
            if (!p.PaintStreets) { townsHandled++; continue; }

            var seg = new RoadSegment
            {
                Kind = RoadKind.Cobble,
                FromPoiId = poi.Id,
                ToPoiId = poi.Id, // intra-town: self-loop
            };

            // Horizontal axis (y = cy, x in [X1..X2])
            for (int x = rect.X1; x <= rect.X2; x++)
                PaintBand(ir, tiles, x, cy, p.Width, targetZ, p.FlattenInteriorZ, seg, ref painted);

            // Vertical axis (x = cx, y in [Y1..Y2])
            for (int y = rect.Y1; y <= rect.Y2; y++)
                PaintBand(ir, tiles, cx, y, p.Width, targetZ, p.FlattenInteriorZ, seg, ref painted);

            // Gate stubs: each gate cell is already on either the H or V axis since
            // gates are at midpoints of footprint edges, so the cross already terminates
            // at them. We still explicitly paint the gate cell itself in case Width=1
            // and we want the edge cell tile-typed as cobble for RoadGraph's bonus.
            if (poi.Gates is { } gates)
            {
                foreach (var g in gates)
                    PaintBand(ir, tiles, g.X, g.Y, p.Width, targetZ, p.FlattenInteriorZ, seg, ref painted);
            }

            if (seg.Path.Count > 0) ir.Roads.Add(seg);
            townsHandled++;
        }

        ctx.Report.TilesTouched = painted;
        ctx.Report.Notes.Add($"towns={townsHandled} road-cells={painted}");
    }

    // Paint a small square of cells around (x,y). Width=1 -> single cell, Width=2 -> 3x3
    // (centre + 8 neighbours clipped to half-width), etc. Records the centre cell on the
    // RoadSegment path so duplicate-painting at axis intersections doesn't bloat ir.Roads.
    private static void PaintBand(
        GenIR ir, ushort[] tiles, int cx, int cy, int width,
        sbyte targetZ, bool flatten, RoadSegment seg, ref int painted)
    {
        int lo = -(Math.Max(1, width) - 1) / 2;
        int hi = lo + Math.Max(1, width) - 1;
        for (int dy = lo; dy <= hi; dy++)
        for (int dx = lo; dx <= hi; dx++)
        {
            int x = cx + dx;
            int y = cy + dy;
            if (x < 0 || y < 0 || x >= ir.Width || y >= ir.Height) continue;
            int idx = ir.Index(x, y);
            if (RoadPaint.IsWater(ir, idx)) continue;
            ushort tile = LatticePick.Pick(tiles, x, y, ir.Seed);
            if (ir.LandId![idx] != tile) painted++;
            ir.LandId[idx] = tile;
            if (ir.Biome is not null) ir.Biome[idx] = (byte)BiomeId.Road;
            if (flatten) ir.Height_Z![idx] = targetZ;
        }
        seg.Path.Add(((ushort)cx, (ushort)cy));
    }

    // Level the footprint to targetZ (land cells only) and blend a skirt (3 tiles by
    // default) so the town sits on a plateau with sloped edges instead of a cliff.
    private static void FlattenFootprint(GenIR ir, CentrED.Network.RectU16 rect, sbyte targetZ, int skirt)
    {
        var z = ir.Height_Z!;
        for (int y = rect.Y1 - skirt; y <= rect.Y2 + skirt; y++)
        for (int x = rect.X1 - skirt; x <= rect.X2 + skirt; x++)
        {
            if (x < ir.Scope.X1 || y < ir.Scope.Y1 || x > ir.Scope.X2 || y > ir.Scope.Y2) continue;
            int idx = ir.Index(x, y);
            if (RoadPaint.IsWater(ir, idx)) continue;
            int d = Math.Max(Math.Max(rect.X1 - x, x - rect.X2), Math.Max(rect.Y1 - y, y - rect.Y2));
            if (d <= 0) { z[idx] = targetZ; continue; }
            double t = 1.0 - (double)d / (skirt + 1);
            z[idx] = (sbyte)Math.Round(z[idx] + (targetZ - z[idx]) * t);
        }
    }
}
