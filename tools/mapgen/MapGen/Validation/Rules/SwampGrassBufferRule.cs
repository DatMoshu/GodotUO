using CentrED.MapGen.IR;
using CentrED.Network;

namespace CentrED.MapGen.Validation.Rules;

// F-6 (Ryandor §3.C): swamp must be surrounded by ≥2 grass tiles before transitioning
// to any other biome. UO's canonical art only ships swamp↔grass transitions; swamp
// directly touching forest/sand/water cannot transition cleanly and renders broken.
//
// Report only. The buffer itself is built in Biome Assign (SwampGrassBuffer), BEFORE land
// ids and transitions are painted; changing the biome here, after the tiles exist, only
// desynchronised Biome from LandId. One aggregated finding per run.
public sealed class SwampGrassBufferRule : IMapRule
{
    public string Name => "SwampGrassBuffer";
    public bool AutoFixable => false;
    public bool Enabled { get; set; } = true;

    // Manhattan radius — every swamp cell needs grass within this many cells in every
    // direction toward a non-grass non-swamp neighbour.
    public int RequiredGrassRadius { get; set; } = 2;

    public ValidationResult Validate(GenIR ir, RectU16 scope, RuleContext ctx)
    {
        var result = new ValidationResult(Name);
        if (ir.Biome is null) return result;
        var b = ir.Biome;
        int violations = 0;
        ReadOnlySpan<(int dx, int dy)> nb = stackalloc (int, int)[] { (1, 0), (-1, 0), (0, 1), (0, -1) };

        for (ushort y = scope.Y1; y <= scope.Y2; y++)
        for (ushort x = scope.X1; x <= scope.X2; x++)
        {
            int idx = ir.Index(x, y);
            if ((BiomeId)b[idx] is not (BiomeId.Swamp or BiomeId.Wetland)) continue;
            bool violated = false;
            foreach (var (dx, dy) in nb)
            {
                for (int step = 1; step <= RequiredGrassRadius && !violated; step++)
                {
                    int nx = x + dx * step, ny = y + dy * step;
                    if ((uint)nx >= ir.Width || (uint)ny >= ir.Height) break;
                    var nb2 = (BiomeId)b[ir.Index(nx, ny)];
                    if (nb2 is BiomeId.Grassland or BiomeId.Savanna or BiomeId.Road) break;
                    if (nb2 is BiomeId.Swamp or BiomeId.Wetland) continue;
                    if (nb2 is BiomeId.DeepWater or BiomeId.ShallowWater or BiomeId.River or BiomeId.Beach) break;
                    violated = true;
                }
                if (violated) break;
            }
            if (violated) violations++;
        }
        result.Add(violations == 0
            ? new ValidationFinding(Name, ValidationSeverity.Info, "swamp buffer OK")
            : new ValidationFinding(Name, ValidationSeverity.Warn,
                $"{violations} swamp cells lack a {RequiredGrassRadius}-tile grass buffer (Biome Assign's SwampGrassBuffer should prevent this)"));
        return result;
    }
}
