using CentrED.MapGen.IR;
using CentrED.MapGen.Pipeline;
using CentrED.MapGen.Stamps;
using CentrED.MapGen.Validation;

namespace CentrED.MapGen.Passes.Scatter;

public sealed class StaticResnapParams
{
    [TunableDisplay("Re-snap statics to land", Tooltip = "Move placed statics by the change in land Z under them since they were placed (validator Z-step / flat-water fixes run after scatter).")]
    public bool Resnap { get; set; } = true;

    [TunableDisplay("Drop duplicate statics", Tooltip = "Remove exact duplicates (same tile, Z and id).")]
    public bool DropDuplicates { get; set; } = true;

    [TunableDisplay("Repair partial multi-tile groups", Tooltip = "Remove the remaining pieces of scatter groups that lost a piece to a later pass.")]
    public bool RepairPartialGroups { get; set; } = true;

    [TunableDisplay("Report static Z vs land", Tooltip = "Warn about sunk / floating statics and overlapping stamps.")]
    public bool ReportChecks { get; set; } = true;
}

// Final static-layer pass, meant to run AFTER Map Validator. The validator's ZStep and
// FlatWater rules change land Z after every scatter/stamp pass has placed its statics;
// without this pass those statics float above or sink into the corrected land.
//
// Placers record the land Z each static was put on (OccupancyGrid ground record); this
// pass moves each recorded static by (current land Z - recorded land Z), then runs the
// stamp/static checks: duplicates, partial multi-tile groups, Z vs land, overlaps.
// Statics from passes that do not use the occupancy grid are left alone.
public sealed class StaticResnapPass : IGenerationPass
{
    public string Name => "Static Resnap";
    public string Category => "Validation";

    public IrFields Reads => IrFields.Height;
    public IrFields Writes => IrFields.StaticOps;

    public object CreateDefaultParams() => new StaticResnapParams();

    public void Run(GenContext ctx, object parameters)
    {
        var p = (StaticResnapParams)parameters;
        var ir = ctx.IR;
        int before = ir.StaticOps.Count;

        var ruleCtx = new RuleContext { Rng = ctx.Rng, SeaLevelZ = ir.SeaLevelZ };
        if (ir.Occupancy is { } occ)
        {
            int cleared = occ.FlushClears();
            if (cleared > 0) ctx.Report.Notes.Add($"flushed {cleared} queued scatter clears");
            // Partial groups are matched by the Z they were placed at, so check before moving anything.
            if (p.RepairPartialGroups) Apply(ctx, new PartialObjectRule(), ir, ruleCtx);
            if (p.Resnap && ir.Height_Z is { } h)
            {
                int moved = Resnap(ir, occ, h);
                ctx.Report.Notes.Add($"re-snapped {moved} statics to changed land");
            }
        }
        if (p.DropDuplicates) Apply(ctx, new DuplicateStaticRule(), ir, ruleCtx);
        if (p.ReportChecks)
        {
            Apply(ctx, new StaticLandZRule(), ir, ruleCtx);
            Apply(ctx, new OverlappingStampsRule(), ir, ruleCtx);
        }
        ctx.Report.StaticsRemoved = Math.Max(0, before - ir.StaticOps.Count);
    }

    private static void Apply(GenContext ctx, IMapRule rule, GenIR ir, RuleContext ruleCtx)
    {
        var r = rule.Validate(ir, ir.Scope, ruleCtx);
        foreach (var f in r.Findings)
        {
            if (f.Severity == ValidationSeverity.Info) ctx.Report.Notes.Add($"{rule.Name}: {f.Message}");
            else ctx.Report.Warnings.Add($"{rule.Name}: {f.Message}");
        }
    }

    /// <summary>Moves every static with a ground record by the land-Z change under it. Returns the count moved.</summary>
    public static int Resnap(GenIR ir, OccupancyGrid occ, sbyte[] heightZ)
    {
        int moved = 0;
        var ops = ir.StaticOps;
        for (int i = 0; i < ops.Count; i++)
        {
            var op = ops[i];
            if (op.Kind != StaticOpKind.Add) continue;
            int idx = ir.Index(op.X, op.Y);
            if (!occ.TryGetGround(idx, out sbyte ground)) continue;
            int delta = heightZ[idx] - ground;
            if (delta == 0) continue;
            ops[i] = op with { Z = (sbyte)Math.Clamp(op.Z + delta, sbyte.MinValue, sbyte.MaxValue) };
            moved++;
        }
        occ.RebaseGround(heightZ);
        return moved;
    }
}
