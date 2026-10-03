using CentrED.MapGen.IR;
using CentrED.MapGen.Validation;
using CentrED.Network;

namespace CentrED.MapGen.Stamps;

// Static-layer validation rules for stamped / scattered content. They implement
// IMapRule so the Map Validator can list them too; StaticResnapPass runs them after
// re-snapping statics to the final land.

/// <summary>Removes exact duplicate Add ops (same tile, Z and id). Auto-fix.</summary>
public sealed class DuplicateStaticRule : IMapRule
{
    public string Name => "DuplicateStatic";
    public bool AutoFixable => true;
    public bool Enabled { get; set; } = true;

    public ValidationResult Validate(GenIR ir, RectU16 scope, RuleContext ctx)
    {
        var result = new ValidationResult(Name);
        var seen = new HashSet<long>();
        int dropped = 0;
        ir.StaticOps.RemoveAll(op =>
        {
            if (op.Kind != StaticOpKind.Add) return false;
            if (seen.Add(Key(op))) return false;
            dropped++;
            return true;
        });
        if (dropped > 0)
            result.Add(new ValidationFinding(Name, ValidationSeverity.Info,
                $"dropped {dropped} duplicate statics (same tile, Z and id)", AutoFixed: true));
        return result;
    }

    internal static long Key(StaticOp op) =>
        ((long)op.X << 40) | ((long)op.Y << 24) | ((long)(byte)op.Z << 16) | op.Id;
}

/// <summary>
/// Reports statics buried below the land of their tile, or hovering far above it with
/// nothing underneath. Warn only — StaticResnapPass already moved what it could.
/// </summary>
public sealed class StaticLandZRule : IMapRule
{
    public string Name => "StaticLandZ";
    public bool AutoFixable => false;
    public bool Enabled { get; set; } = true;
    /// <summary>A static more than this far below its land counts as sunk.</summary>
    public int SinkTolerance { get; set; } = 2;
    /// <summary>A static this far above its land, with no lower static on the tile, counts as floating.</summary>
    public int FloatThreshold { get; set; } = 30;

    public ValidationResult Validate(GenIR ir, RectU16 scope, RuleContext ctx)
    {
        var result = new ValidationResult(Name);
        if (ir.Height_Z is null) return result;
        var lowest = new Dictionary<int, int>();
        foreach (var op in ir.StaticOps)
        {
            if (op.Kind != StaticOpKind.Add || !InScope(op, scope)) continue;
            int idx = ir.Index(op.X, op.Y);
            lowest[idx] = lowest.TryGetValue(idx, out var lo) ? Math.Min(lo, op.Z) : op.Z;
        }
        int sunk = 0, floating = 0, examples = 0;
        foreach (var op in ir.StaticOps)
        {
            if (op.Kind != StaticOpKind.Add || !InScope(op, scope)) continue;
            int idx = ir.Index(op.X, op.Y);
            int land = ir.Height_Z[idx];
            bool isSunk = op.Z < land - SinkTolerance;
            bool isFloat = op.Z > land + FloatThreshold && lowest[idx] == op.Z;
            if (!isSunk && !isFloat) continue;
            if (isSunk) sunk++; else floating++;
            if (examples++ < 5)
                result.Add(new ValidationFinding(Name, ValidationSeverity.Warn,
                    $"static 0x{op.Id:X4} at ({op.X},{op.Y}) z={op.Z} on land z={land} ({(isSunk ? "sunk" : "floating")})", op.X, op.Y));
        }
        if (sunk + floating > 0)
            result.Add(new ValidationFinding(Name, ValidationSeverity.Warn,
                $"{sunk} sunk and {floating} floating statics"));
        return result;
    }

    private static bool InScope(StaticOp op, RectU16 s) => op.X >= s.X1 && op.X <= s.X2 && op.Y >= s.Y1 && op.Y <= s.Y2;
}

/// <summary>Reports stamp footprints that share tiles (should be impossible with the occupancy grid).</summary>
public sealed class OverlappingStampsRule : IMapRule
{
    public string Name => "OverlappingStamps";
    public bool AutoFixable => false;
    public bool Enabled { get; set; } = true;

    public ValidationResult Validate(GenIR ir, RectU16 scope, RuleContext ctx)
    {
        var result = new ValidationResult(Name);
        if (ir.Occupancy is not { } occ) return result;
        var owner = new Dictionary<int, int>();
        int overlaps = 0;
        var pairs = new HashSet<(int, int)>();
        for (int p = 0; p < occ.Placements.Count; p++)
        {
            var pl = occ.Placements[p];
            if (!pl.IsStamp) continue;
            foreach (int cell in pl.Footprint)
            {
                if (owner.TryGetValue(cell, out int other) && other != p)
                {
                    overlaps++;
                    if (pairs.Add((other, p)) && pairs.Count <= 5)
                        result.Add(new ValidationFinding(Name, ValidationSeverity.Warn,
                            $"{occ.Placements[other].Id} overlaps {pl.Id}", cell % ir.Width, cell / ir.Width));
                }
                else owner[cell] = p;
            }
        }
        if (overlaps > 0)
            result.Add(new ValidationFinding(Name, ValidationSeverity.Error,
                $"{overlaps} tiles claimed by more than one stamp ({pairs.Count} stamp pairs)"));
        return result;
    }
}

/// <summary>
/// Finds multi-tile objects (stamps and multi-tile scatter groups) of which some pieces were
/// removed later (e.g. a validator dropped the piece standing in water). Auto-fix removes
/// the orphaned remainder so no half-trees or half-rocks are committed.
/// </summary>
public sealed class PartialObjectRule : IMapRule
{
    public string Name => "PartialMultiTile";
    public bool AutoFixable => true;
    public bool Enabled { get; set; } = true;
    /// <summary>Only scatter groups are repaired; a stamp missing pieces is reported, not removed.</summary>
    public bool RemoveRemnantsOfScatterGroups { get; set; } = true;

    public ValidationResult Validate(GenIR ir, RectU16 scope, RuleContext ctx)
    {
        var result = new ValidationResult(Name);
        if (ir.Occupancy is not { } occ || occ.Placements.Count == 0) return result;
        var present = new Dictionary<long, int>();
        foreach (var op in ir.StaticOps)
        {
            if (op.Kind != StaticOpKind.Add) continue;
            long k = DuplicateStaticRule.Key(op);
            present[k] = present.TryGetValue(k, out var c) ? c + 1 : 1;
        }
        var remove = new HashSet<long>();
        int partialStamps = 0, partialGroups = 0;
        foreach (var pl in occ.Placements)
        {
            if (pl.Ops.Length < 2) continue;
            int missing = 0;
            foreach (var op in pl.Ops)
                if (!present.ContainsKey(DuplicateStaticRule.Key(op))) missing++;
            if (missing == 0 || missing == pl.Ops.Length) continue;
            if (pl.IsStamp)
            {
                partialStamps++;
                if (partialStamps <= 5)
                    result.Add(new ValidationFinding(Name, ValidationSeverity.Warn,
                        $"stamp {pl.Id} lost {missing}/{pl.Ops.Length} statics"));
            }
            else
            {
                partialGroups++;
                if (RemoveRemnantsOfScatterGroups)
                    foreach (var op in pl.Ops) remove.Add(DuplicateStaticRule.Key(op));
            }
        }
        if (remove.Count > 0)
        {
            int dropped = ir.StaticOps.RemoveAll(op => op.Kind == StaticOpKind.Add && remove.Contains(DuplicateStaticRule.Key(op)));
            result.Add(new ValidationFinding(Name, ValidationSeverity.Info,
                $"removed {dropped} remnant statics of {partialGroups} partial multi-tile groups", AutoFixed: true));
        }
        else if (partialGroups > 0)
            result.Add(new ValidationFinding(Name, ValidationSeverity.Warn, $"{partialGroups} partial multi-tile groups"));
        if (partialStamps > 0)
            result.Add(new ValidationFinding(Name, ValidationSeverity.Warn, $"{partialStamps} stamps lost some of their statics"));
        return result;
    }
}
