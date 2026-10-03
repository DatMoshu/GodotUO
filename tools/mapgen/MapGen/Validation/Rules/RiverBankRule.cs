using CentrED.MapGen.IR;
using CentrED.MapGen.Passes.Hydrology;
using CentrED.Network;

namespace CentrED.MapGen.Validation.Rules;

// River and lake banks rise from the water plane by at most 4 Z per tile (the shore
// transition tiles cannot render a steeper step). River Carve builds banks that way; this
// rule re-applies the same bound after the passes that run later (coast conversion,
// sliver absorption, road shoulders). Only lowers land.
public sealed class RiverBankRule : IMapRule
{
    public string Name => "RiverBank";
    public bool AutoFixable => true;
    public bool Enabled { get; set; } = true;
    public int BankStep { get; set; } = 4;

    public ValidationResult Validate(GenIR ir, RectU16 scope, RuleContext ctx)
    {
        var result = new ValidationResult(Name);
        int changed = RiverCarvePass.LowerBanks(ir, BankStep);
        if (changed > 0)
            result.Add(new ValidationFinding(Name, ValidationSeverity.Info,
                $"lowered {changed} bank cells to keep river banks within {BankStep} Z per tile", AutoFixed: true));
        return result;
    }
}
