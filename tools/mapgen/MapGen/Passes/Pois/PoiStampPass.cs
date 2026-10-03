using System.Text.Json;
using CentrED.MapGen.IR;
using CentrED.MapGen.Pipeline;
using CentrED.MapGen.Stamps;
using CentrED.Network;

namespace CentrED.MapGen.Passes.Pois;

public sealed class PoiStampParams
{
    [TunableDisplay("Quadrant boundaries JSON path")]
    public string QuadrantBoundariesPath { get; set; } = "";

    [TunableDisplay("Auto-grid quadrants X")] [TunableRange(1, 16)]
    public int AutoGridX { get; set; } = 4;

    [TunableDisplay("Auto-grid quadrants Y")] [TunableRange(1, 16)]
    public int AutoGridY { get; set; } = 6;

    [TunableDisplay("Min spacing (tiles)")] [TunableRange(16, 512)]
    public int MinSpacing { get; set; } = 96;

    [TunableDisplay("Control points per quadrant")] [TunableRange(0, 8)]
    public int ControlPointsPerQuadrant { get; set; } = 3;

    [TunableDisplay("Dungeons per quadrant")] [TunableRange(0, 4)]
    public int DungeonsPerQuadrant { get; set; } = 1;

    [TunableDisplay("Spacing across quadrants",
        Tooltip = "Apply Min spacing against every POI already on the map (towns and other quadrants), not only this quadrant's. Stops POIs clustering on quadrant borders, which made Road Graph join them with many short parallel roads.")]
    public bool SpacingAcrossQuadrants { get; set; } = true;

    [TunableDisplay("Sea level offset", Tooltip = "Added to GenIR.SeaLevelZ (the rebased frame: sea level is 0).")] [TunableRange(-128, 127)]
    public int SeaLevelZ { get; set; } = 0;
}

public sealed class PoiStampPass : IGenerationPass
{
    public string Name => "POI Stamps";
    public string Category => "Pois";

    public IrFields Reads => IrFields.Height | IrFields.Biome;
    public IrFields Writes => IrFields.Pois | IrFields.StaticOps;

    public object CreateDefaultParams() => new PoiStampParams();

    public void Run(GenContext ctx, object parameters)
    {
        var p = (PoiStampParams)parameters;
        var ir = ctx.IR;
        if (ir.Height_Z is null) return;

        var quadrants = LoadQuadrants(p, ir);
        // Clip to scope and drop those outside.
        var clipped = new List<Quadrant>();
        foreach (var q in quadrants)
        {
            int x1 = Math.Max(q.Rect.X1, ir.Scope.X1);
            int y1 = Math.Max(q.Rect.Y1, ir.Scope.Y1);
            int x2 = Math.Min(q.Rect.X2, ir.Scope.X2);
            int y2 = Math.Min(q.Rect.Y2, ir.Scope.Y2);
            if (x1 > x2 || y1 > y2) continue;
            clipped.Add(new Quadrant(q.Id, new RectU16((ushort)x1, (ushort)y1, (ushort)x2, (ushort)y2)));
        }
        quadrants = clipped;
        ctx.Report.Notes.Add($"quadrants={quadrants.Count}");
        if (quadrants.Count == 0) { ctx.Report.Warnings.Add("no quadrants intersect scope"); return; }

        int nextId = 1;
        int spawned = 0;
        foreach (var q in quadrants)
        {
            ctx.Cancellation.ThrowIfCancellationRequested();
            var placed = new List<(ushort X, ushort Y)>();
            int placedHere = 0;
            // Earlier POIs (towns, other quadrants) count for spacing but not for this quadrant's quota.
            if (p.SpacingAcrossQuadrants)
                foreach (var prev in ir.Pois) placed.Add((prev.X, prev.Y));
            int cpTarget = p.ControlPointsPerQuadrant;
            int dgTarget = p.DungeonsPerQuadrant;

            for (int kind = 0; kind < 2; kind++)
            {
                int target = kind == 0 ? cpTarget : dgTarget;
                var poiKind = kind == 0 ? PoiKind.ControlPoint : PoiKind.DungeonEntrance;
                int attempts = 0;
                while (placedHere < (kind == 0 ? cpTarget : cpTarget + dgTarget) && attempts++ < 256)
                {
                    int x = ctx.Rng.Next(q.Rect.X1, q.Rect.X2 + 1);
                    int y = ctx.Rng.Next(q.Rect.Y1, q.Rect.Y2 + 1);
                    int idx = ir.Index(x, y);
                    sbyte zv = ir.Height_Z[idx];
                    if (zv <= ir.SeaLevelZ + p.SeaLevelZ + 1) continue;  // not in water/beach
                    bool tooClose = false;
                    foreach (var prev in placed)
                    {
                        int dx = prev.X - x, dy = prev.Y - y;
                        if (dx * dx + dy * dy < p.MinSpacing * p.MinSpacing) { tooClose = true; break; }
                    }
                    if (tooClose) continue;

                    placed.Add(((ushort)x, (ushort)y));
                    placedHere++;
                    ir.Pois.Add(new PoiStamp(nextId++, poiKind, (ushort)x, (ushort)y, zv,
                        QuadrantId: q.Id));
                    spawned++;

                    // Emit a static so the POI is visible after commit.
                    ushort graphic = poiKind == PoiKind.ControlPoint
                        ? ir.Tables.ControlPointStaticId
                        : ir.Tables.DungeonEntranceStaticId;
                    if (graphic != 0)
                    {
                        ir.StaticOps.Add(new StaticOp(StaticOpKind.Add,
                            (ushort)x, (ushort)y, zv, graphic, 0));
                        // Claim the marker tile so no stamp or scatter object lands on it.
                        var occ = OccupancyGrid.For(ir);
                        occ.MarkHard(x, y);
                        occ.RecordGround(x, y, zv);
                    }
                }
            }
        }

        ctx.Report.TilesTouched = spawned;
        ctx.Report.Notes.Add($"poi-spawned={spawned}");
    }

    private sealed record Quadrant(string Id, RectU16 Rect);

    /// <summary>Splits <paramref name="scope"/> into gridX x gridY cells that tile it exactly (no remainder strip).</summary>
    internal static List<(string Id, RectU16 Rect)> AutoGrid(RectU16 scope, int gridX, int gridY)
    {
        int gx = Math.Clamp(gridX, 1, Math.Max(1, (int)scope.Width));
        int gy = Math.Clamp(gridY, 1, Math.Max(1, (int)scope.Height));
        var list = new List<(string, RectU16)>(gx * gy);
        for (int j = 0; j < gy; j++)
        for (int i = 0; i < gx; i++)
        {
            int x1 = scope.X1 + (int)((long)i * scope.Width / gx);
            int x2 = scope.X1 + (int)((long)(i + 1) * scope.Width / gx) - 1;
            int y1 = scope.Y1 + (int)((long)j * scope.Height / gy);
            int y2 = scope.Y1 + (int)((long)(j + 1) * scope.Height / gy) - 1;
            list.Add(($"Q{i}-{j}", new RectU16((ushort)x1, (ushort)y1, (ushort)x2, (ushort)y2)));
        }
        return list;
    }

    private static List<Quadrant> LoadQuadrants(PoiStampParams p, GenIR ir)
    {
        var list = new List<Quadrant>();
        if (!string.IsNullOrWhiteSpace(p.QuadrantBoundariesPath) && File.Exists(p.QuadrantBoundariesPath))
        {
            try
            {
                using var fs = File.OpenRead(p.QuadrantBoundariesPath);
                using var doc = JsonDocument.Parse(fs);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    int n = 0;
                    foreach (var el in doc.RootElement.EnumerateArray())
                    {
                        // Deterministic fallback id (was Guid.NewGuid, which changed every run).
                        string id = el.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : $"Q{n}";
                        n++;
                        ushort x1 = (ushort)el.GetProperty("x1").GetInt32();
                        ushort y1 = (ushort)el.GetProperty("y1").GetInt32();
                        ushort x2 = (ushort)el.GetProperty("x2").GetInt32();
                        ushort y2 = (ushort)el.GetProperty("y2").GetInt32();
                        list.Add(new Quadrant(id, new RectU16(x1, y1, x2, y2)));
                    }
                    if (list.Count > 0) return list;
                }
            }
            catch { /* fall through to auto-grid */ }
        }

        // Auto-grid fallback within IR scope. Cell edges come from i*W/g so the last
        // row/column reaches the scope edge (integer W/g used to drop a remainder strip).
        return AutoGrid(ir.Scope, p.AutoGridX, p.AutoGridY).Select(q => new Quadrant(q.Id, q.Rect)).ToList();
    }
}
