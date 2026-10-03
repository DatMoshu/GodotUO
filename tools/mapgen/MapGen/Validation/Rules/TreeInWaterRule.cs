using CentrED.MapGen.Data;
using CentrED.MapGen.IR;
using CentrED.Network;

namespace CentrED.MapGen.Validation.Rules;

// Drop forest/tree/scatter statics that ended up on water cells.
//
// - Every Add op is checked against its OWN cell (a multi-tile group can overhang water
//   with a piece other than its anchor).
// - A scatter group (OccupancyGrid placement, not a stamp) with any piece on water is
//   removed WHOLE, so no half trees or half rock piles remain.
// - Remove ops are never touched: they delete statics of the source map and must reach
//   the committer.
// - Water-surface statics (TileFlags.IsWaterStaticId) are KEPT: DigShore and AutoCoast
//   place them on water and dug-shore cells on purpose (the old predicate wiped ~35k of
//   them per island roundtrip and left brown bottoms uncovered).
// - Stamps are not removed here (stamp placement already rejects water); the stamp
//   checks report a stamp that lost pieces.
public sealed class TreeInWaterRule : IMapRule
{
    public string Name => "TreeInWater";
    public bool AutoFixable => true;
    public bool Enabled { get; set; } = true;

    public ValidationResult Validate(GenIR ir, RectU16 scope, RuleContext ctx)
    {
        var result = new ValidationResult(Name);
        if (ir.LandId is null) return result;

        bool InScope(StaticOp op) => op.X >= scope.X1 && op.X <= scope.X2 && op.Y >= scope.Y1 && op.Y <= scope.Y2;
        bool OnWater(StaticOp op)
        {
            if (op.Kind != StaticOpKind.Add || !InScope(op) || TileFlags.IsWaterStaticId(op.Id)) return false;
            int idx = ir.Index(op.X, op.Y);
            if (TileFlags.IsWaterLandId(ir.LandId[idx])) return true;
            return ir.Biome is not null && (BiomeId)ir.Biome[idx] is BiomeId.DeepWater or BiomeId.ShallowWater or BiomeId.River;
        }

        // Ops to drop, as a multiset (the same op can legitimately appear twice).
        var drop = new Dictionary<StaticOp, int>();
        int groups = 0;
        if (ir.Occupancy is { } occ)
        {
            foreach (var pl in occ.Placements)
            {
                if (pl.IsStamp || !pl.Ops.Any(OnWater)) continue;
                groups++;
                foreach (var op in pl.Ops)
                    if (op.Kind == StaticOpKind.Add) drop[op] = drop.GetValueOrDefault(op) + 1;
            }
        }

        int dropped = 0;
        ir.StaticOps.RemoveAll(op =>
        {
            if (op.Kind != StaticOpKind.Add) return false;
            if (drop.TryGetValue(op, out int left) && left > 0)
            {
                drop[op] = left - 1;
                dropped++;
                return true;
            }
            if (!OnWater(op)) return false;
            dropped++;
            return true;
        });
        if (dropped > 0)
            result.Add(new ValidationFinding(Name, ValidationSeverity.Info,
                $"dropped {dropped} non-water statics on water cells ({groups} whole scatter groups)", AutoFixed: true));
        return result;
    }
}
