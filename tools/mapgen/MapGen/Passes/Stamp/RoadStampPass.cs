using CentrED.MapGen.IR;
using CentrED.MapGen.Pipeline;
using CentrED.MapGen.Stamps;

namespace CentrED.MapGen.Passes.Stamp;

public sealed class RoadStampParams
{
    [TunableDisplay("Stamps root")]
    public string StampsRoot { get; set; } = StampLoader.DefaultRoot;

    [TunableDisplay("Sea level offset", Tooltip = "Added to GenIR.SeaLevelZ (the rebased frame: sea level is 0).")] [TunableRange(-128, 127)]
    public int SeaLevelZ { get; set; } = 0;

    [TunableDisplay("Max local slope")] [TunableRange(1, 64)]
    public int MaxLocalSlope { get; set; } = 8;

    [TunableDisplay("Stamp every N cells")] [TunableRange(2, 32)]
    public int StampStride { get; set; } = 6;

    [TunableDisplay("Use variants", Tooltip = "Road art is directional: road stamps are never rotated, whatever this says. Kept for preset compatibility.")]
    public bool UseVariants { get; set; } = true;

    [TunableDisplay("Paint land tiles")] public bool PaintLand { get; set; } = true;

    [TunableDisplay("Max statics per stamp", Tooltip = "Skip road stamps with more statics than this. Road windows mined inside towns carry house fragments; a road needs few statics.")]
    [TunableRange(0, 200)]
    public int MaxStaticsPerStamp { get; set; } = 12;
}

// Walks the Roads polylines emitted by RoadGraphPass and stamps a road_segment stamp
// every Nth cell along each path, plus one road_junction stamp per distinct segment
// endpoint (shared endpoints get one junction, not one per road). Segments are chosen
// to match the local road direction from the stamps' ns/ew/diag tags, because road
// stamps are never rotated (their art is directional). Rigid grounding: the stamp's
// land relief is written around a base Z, edges blended into the terrain.
public sealed class RoadStampPass : IGenerationPass
{
    public string Name => "Road Stamps";
    public string Category => "Scatter";

    public IrFields Reads => IrFields.Roads | IrFields.Height | IrFields.LandId;
    public IrFields Writes => IrFields.StaticOps | IrFields.LandId;

    public object CreateDefaultParams() => new RoadStampParams();

    public void Run(GenContext ctx, object parameters)
    {
        var p = (RoadStampParams)parameters;
        var ir = ctx.IR;
        if (ir.Roads.Count == 0)
        {
            ctx.Report.Warnings.Add("no roads — RoadGraphPass must run first");
            return;
        }
        var lib = StampPassSupport.OpenLibrary(ctx, p.StampsRoot);
        int opsBefore = ir.StaticOps.Count, corruptBefore = lib.Errors.Count;
        var segments = lib.GetKind("road_segment", r => r.StaticCount <= p.MaxStaticsPerStamp);
        if (segments.Count == 0)
        {
            ctx.Report.Warnings.Add("no road_segment stamps in library");
            return;
        }
        var junctions = lib.GetKind("road_junction", r => r.StaticCount <= p.MaxStaticsPerStamp);
        var byDir = new Dictionary<string, IReadOnlyList<LoadedStamp>>();
        foreach (var dir in new[] { "ns", "ew", "diag" })
        {
            var l = segments.Where(s => s.Tags.Contains(dir)).ToList();
            byDir[dir] = l.Count > 0 ? l : segments;
        }

        var opt = new StampPlaceOptions
        {
            SeaLevelZ = ir.SeaLevelZ + p.SeaLevelZ,
            MaxLocalSlope = p.MaxLocalSlope,
            PaintLand = p.PaintLand,
            Grounding = StampGrounding.Rigid,
            Source = Name,
        };

        int placedSegments = 0, placedJunctions = 0;
        var rejects = new Dictionary<string, int>();
        var junctionCells = new HashSet<(int, int)>();
        foreach (var road in ir.Roads)
        {
            ctx.Cancellation.ThrowIfCancellationRequested();
            if (road.Path.Count < 2) continue;
            if (junctions.Count > 0)
            {
                foreach (var (x, y) in new[] { road.Path[0], road.Path[^1] })
                {
                    if (!junctionCells.Add((x, y))) continue; // shared endpoint: one junction only
                    var stamp = StampPlacer.PickWeighted(junctions, ctx.Rng);
                    if (StampPlacer.TryPlace(ir, stamp, x, y, StampVariant.Original, opt, out var plan)) placedJunctions++;
                    else StampPassSupport.CountReject(rejects, plan.RejectReason);
                }
            }
            for (int i = p.StampStride / 2; i < road.Path.Count; i += p.StampStride)
            {
                var (x, y) = road.Path[i];
                var pool = byDir[Direction(road.Path, i)];
                var stamp = StampPlacer.PickWeighted(pool, ctx.Rng);
                if (StampPlacer.TryPlace(ir, stamp, x, y, StampVariant.Original, opt, out var plan)) placedSegments++;
                else StampPassSupport.CountReject(rejects, plan.RejectReason);
            }
        }
        ctx.Report.Notes.Add($"segments={placedSegments} junctions={placedJunctions}; {StampPassSupport.Summary(placedSegments + placedJunctions, rejects)}");
        StampPassSupport.Finish(ctx, lib, opsBefore, corruptBefore);
    }

    // Local direction of the path around index i: "ew" when it runs mostly along X, "ns"
    // mostly along Y, otherwise "diag". Matches the miner's segment tags.
    internal static string Direction(IReadOnlyList<(ushort X, ushort Y)> path, int i)
    {
        int a = Math.Max(0, i - 3), b = Math.Min(path.Count - 1, i + 3);
        int dx = Math.Abs(path[b].X - path[a].X);
        int dy = Math.Abs(path[b].Y - path[a].Y);
        if (dx >= 2 * dy) return "ew";
        if (dy >= 2 * dx) return "ns";
        return "diag";
    }
}
