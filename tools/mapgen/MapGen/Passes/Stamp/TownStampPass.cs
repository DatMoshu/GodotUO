using CentrED.MapGen.IR;
using CentrED.MapGen.Pipeline;
using CentrED.MapGen.Stamps;

namespace CentrED.MapGen.Passes.Stamp;

public sealed class TownStampParams
{
    [TunableDisplay("Stamps root")]
    public string StampsRoot { get; set; } = StampLoader.DefaultRoot;

    [TunableDisplay("Sea level offset", Tooltip = "Added to GenIR.SeaLevelZ (the rebased frame: sea level is 0).")] [TunableRange(-128, 127)]
    public int SeaLevelZ { get; set; } = 0;

    [TunableDisplay("Max local slope")] [TunableRange(1, 64)]
    public int MaxLocalSlope { get; set; } = 6;

    [TunableDisplay("Stamps per POI")] [TunableRange(1, 8)]
    public int StampsPerPoi { get; set; } = 4;

    [TunableDisplay("Cluster radius")] [TunableRange(0, 64)]
    public int ClusterRadius { get; set; } = 24;

    [TunableDisplay("Max statics per stamp", Tooltip = "Reject town stamps bigger than this — keeps house clusters, drops city blocks")]
    [TunableRange(1, 500)]
    public int MaxStaticsPerStamp { get; set; } = 60;

    [TunableDisplay("Min spacing between stamps")] [TunableRange(1, 64)]
    public int MinSpacingBetweenStamps { get; set; } = 6;

    [TunableDisplay("Use variants", Tooltip = "Buildings are directional: town stamps are never rotated, whatever this says. Kept for preset compatibility.")]
    public bool UseVariants { get; set; } = true;
    [TunableDisplay("Paint land tiles")] public bool PaintLand { get; set; } = true;
}

// Stamps town_quarter stamps around each ControlPoint/Town POI. With ClusterRadius > 0,
// places StampsPerPoi stamps clustered in a square around each POI. Overlap is
// prevented globally by the shared occupancy grid (not just within one POI's
// cluster), stamps are never rotated (building art is directional), and they are
// placed rigidly so walls and floors keep their relative Z.
public sealed class TownStampPass : IGenerationPass
{
    public string Name => "Town Stamps";
    public string Category => "Scatter";

    public IrFields Reads => IrFields.Pois | IrFields.Height | IrFields.LandId;
    public IrFields Writes => IrFields.StaticOps | IrFields.LandId;

    public object CreateDefaultParams() => new TownStampParams();

    public void Run(GenContext ctx, object parameters)
    {
        var p = (TownStampParams)parameters;
        var ir = ctx.IR;
        if (ir.Pois.Count == 0)
        {
            ctx.Report.Warnings.Add("no POIs — PoiStampPass must run first");
            return;
        }
        var lib = StampPassSupport.OpenLibrary(ctx, p.StampsRoot);
        int opsBefore = ir.StaticOps.Count, corruptBefore = lib.Errors.Count;
        int allTowns = lib.RowsOfKind("town_quarter").Count;
        if (allTowns == 0)
        {
            ctx.Report.Warnings.Add("no town_quarter stamps in library");
            return;
        }
        // Filter to "house cluster"-sized stamps on the index rows, so the big city-block
        // stamps are never even parsed.
        var towns = lib.GetKind("town_quarter", r => r.StaticCount <= p.MaxStaticsPerStamp);
        ctx.Report.Notes.Add($"library: {allTowns} town stamps, {towns.Count} after MaxStaticsPerStamp={p.MaxStaticsPerStamp}");
        if (towns.Count == 0)
        {
            ctx.Report.Warnings.Add("no town stamps survived filter — bump MaxStaticsPerStamp");
            return;
        }

        var opt = new StampPlaceOptions
        {
            SeaLevelZ = ir.SeaLevelZ + p.SeaLevelZ,
            MaxLocalSlope = p.MaxLocalSlope,
            PaintLand = p.PaintLand,
            Grounding = StampGrounding.Rigid,
            Source = Name,
        };

        int placed = 0;
        var rejects = new Dictionary<string, int>();
        foreach (var poi in ir.Pois)
        {
            ctx.Cancellation.ThrowIfCancellationRequested();
            if (poi.Kind != PoiKind.ControlPoint && poi.Kind != PoiKind.Town) continue;
            var placedHere = new List<(int X, int Y)>();
            int attempts = 0;
            while (placedHere.Count < p.StampsPerPoi && attempts++ < p.StampsPerPoi * 8)
            {
                int dx = p.ClusterRadius > 0 ? ctx.Rng.Next(-p.ClusterRadius, p.ClusterRadius + 1) : 0;
                int dy = p.ClusterRadius > 0 ? ctx.Rng.Next(-p.ClusterRadius, p.ClusterRadius + 1) : 0;
                int x = poi.X + dx;
                int y = poi.Y + dy;
                bool tooClose = false;
                foreach (var (px, py) in placedHere)
                {
                    int ax = x - px, ay = y - py;
                    if (ax * ax + ay * ay < p.MinSpacingBetweenStamps * p.MinSpacingBetweenStamps) { tooClose = true; break; }
                }
                if (tooClose) { StampPassSupport.CountReject(rejects, "spacing"); continue; }

                var stamp = StampPlacer.PickWeighted(towns, ctx.Rng);
                if (StampPlacer.TryPlace(ir, stamp, x, y, StampVariant.Original, opt, out var plan))
                {
                    placed++;
                    placedHere.Add((x, y));
                }
                else StampPassSupport.CountReject(rejects, plan.RejectReason);
            }
        }
        ctx.Report.Notes.Add($"{StampPassSupport.Summary(placed, rejects)} over {ir.Pois.Count} POIs");
        StampPassSupport.Finish(ctx, lib, opsBefore, corruptBefore);
    }
}
