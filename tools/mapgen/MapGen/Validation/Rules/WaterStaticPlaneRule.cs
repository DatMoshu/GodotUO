using CentrED.MapGen.Data;
using CentrED.MapGen.IR;
using CentrED.Network;

namespace CentrED.MapGen.Validation.Rules;

// Water-surface statics (DigShore's dug-ring overlay, AutoCoast's, the shore rim) are the
// visible water plane: they must sit at GenIR.OceanZ. Passes that move Z after they were
// placed (Coast Terrace, Land Transitions' flatten, the ZStep/FlatWater fixes, stamps'
// pads) used to leave them sunk under the land or floating. Runs after FlatWater and
// re-pins every generated water static to the plane, and drops the ones whose cell has
// since become dry land above the plane (a dug cell absorbed into the shore by Land
// Transitions or raised by Coast Terrace): water drawn under the grass. Water statics
// that belong to a stamp (a fountain, a town pond) are left alone.
public sealed class WaterStaticPlaneRule : IMapRule
{
    public string Name => "WaterStaticPlane";
    public bool AutoFixable => true;
    public bool Enabled { get; set; } = true;

    public ValidationResult Validate(GenIR ir, RectU16 scope, RuleContext ctx)
    {
        var result = new ValidationResult(Name);
        sbyte plane = (sbyte)Math.Clamp(ctx.OceanZ, sbyte.MinValue, sbyte.MaxValue);
        var stampOps = new HashSet<StaticOp>();
        if (ir.Occupancy is { } occ)
            foreach (var pl in occ.Placements)
                if (pl.IsStamp) foreach (var op in pl.Ops) stampOps.Add(op);

        bool Generated(StaticOp op) => op.Kind == StaticOpKind.Add && TileFlags.IsWaterStaticId(op.Id)
            && op.X >= scope.X1 && op.X <= scope.X2 && op.Y >= scope.Y1 && op.Y <= scope.Y2 && !stampOps.Contains(op);
        bool OnDryLand(StaticOp op)
        {
            int idx = ir.Index(op.X, op.Y);
            if (ir.LandId is not null && TileFlags.IsWaterLandId(ir.LandId[idx])) return false;
            if (ir.Biome is not null && (BiomeId)ir.Biome[idx] is BiomeId.DeepWater or BiomeId.ShallowWater or BiomeId.River) return false;
            // Foam can lie below the dry origin of a sloping land quad. Keep it
            // when another vertex actually descends to the water plane.
            if (op.Id >= 0x179D && op.Id <= 0x17B2 && ir.Height_Z is not null)
                for (int dy = 0; dy <= 1; dy++)
                for (int dx = 0; dx <= 1; dx++)
                    if (op.X + dx < ir.Width && op.Y + dy < ir.Height
                        && ir.Height_Z[ir.Index(op.X + dx, op.Y + dy)] <= plane) return false;
            return ir.Height_Z is not null && ir.Height_Z[idx] > plane;
        }

        int dropped = ir.StaticOps.RemoveAll(op => Generated(op) && OnDryLand(op));
        int moved = 0;
        var ops = ir.StaticOps;
        for (int i = 0; i < ops.Count; i++)
        {
            var op = ops[i];
            if (op.Z == plane || !Generated(op)) continue;
            ops[i] = op with { Z = plane };
            moved++;
        }
        if (dropped > 0)
            result.Add(new ValidationFinding(Name, ValidationSeverity.Info,
                $"dropped {dropped} water statics left on dry land above the water plane", AutoFixed: true));
        if (moved > 0)
            result.Add(new ValidationFinding(Name, ValidationSeverity.Info,
                $"re-pinned {moved} water statics to the water plane Z{plane}", AutoFixed: true));
        return result;
    }
}
