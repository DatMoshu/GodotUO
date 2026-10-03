using CentrED.MapGen.Data;
using CentrED.MapGen.IR;
using CentrED.Network;

namespace CentrED.MapGen.Validation.Rules;

// Adjacent-tile Z deltas beyond the engine's step zone (16) cannot be walked — clamp them.
// Deltas beyond the soft warn threshold (4) are counted in one finding.
//
// Symmetric: every 4-neighbour pair is checked in both directions and the HIGHER land
// cell is lowered to lower + limit (the old rule only ever moved the +x/+y cell, so the
// fix depended on scan direction). Water cells never move (the validator runs this
// before FlatWater so water stays on its body's plane). Repeats until no pair exceeds
// the limit (bounded sweeps).
public sealed class ZStepRule : IMapRule
{
    public string Name => "ZStep";
    public bool AutoFixable => true;
    public bool Enabled { get; set; } = true;

    public ValidationResult Validate(GenIR ir, RectU16 scope, RuleContext ctx)
    {
        var result = new ValidationResult(Name);
        if (ir.Height_Z is null) return result;
        var z = ir.Height_Z;
        int hard = ctx.ZStepHardLimit;
        int warn = ctx.ZStepWarnThreshold;
        int clamped = 0;

        for (int sweep = 0; sweep < 16; sweep++)
        {
            int changed = 0;
            for (int y = scope.Y1; y <= scope.Y2; y++)
            for (int x = scope.X1; x <= scope.X2; x++)
            {
                int a = ir.Index(x, y);
                if (x + 1 <= scope.X2) changed += Fix(a, ir.Index(x + 1, y));
                if (y + 1 <= scope.Y2) changed += Fix(a, ir.Index(x, y + 1));
            }
            clamped += changed;
            if (changed == 0) break;
        }

        int warned = 0;
        for (int y = scope.Y1; y <= scope.Y2; y++)
        for (int x = scope.X1; x <= scope.X2; x++)
        {
            int a = ir.Index(x, y);
            if (x + 1 <= scope.X2 && Math.Abs(z[a] - z[ir.Index(x + 1, y)]) > warn) warned++;
            if (y + 1 <= scope.Y2 && Math.Abs(z[a] - z[ir.Index(x, y + 1)]) > warn) warned++;
        }

        if (clamped > 0)
            result.Add(new ValidationFinding(Name, ValidationSeverity.Info,
                $"clamped {clamped} tile-edge Z deltas to engine limit ({hard})", AutoFixed: true));
        if (warned > 0)
            result.Add(new ValidationFinding(Name, ValidationSeverity.Warn,
                $"{warned} tile-edge Z deltas exceed warn threshold ({warn}) — visually steep"));
        return result;

        int Fix(int a, int b)
        {
            int d = z[a] - z[b];
            if (Math.Abs(d) <= hard) return 0;
            int hi = d > 0 ? a : b, lo = d > 0 ? b : a;
            if (!IsWater(ir, hi))
            {
                z[hi] = (sbyte)(z[lo] + hard);
                return 1;
            }
            if (!IsWater(ir, lo))
            {
                z[lo] = (sbyte)(z[hi] - hard);
                return 1;
            }
            return 0;
        }
    }

    private static bool IsWater(GenIR ir, int idx)
    {
        if (ir.LandId is not null && TileFlags.IsWaterLandId(ir.LandId[idx])) return true;
        return ir.Biome is not null && (BiomeId)ir.Biome[idx] is BiomeId.DeepWater or BiomeId.ShallowWater or BiomeId.River;
    }
}
