using CentrED.MapGen.Data;
using CentrED.MapGen.IR;
using CentrED.Network;

namespace CentrED.MapGen.Validation.Rules;

// Report-only replacement for Land Transitions' old strict assert (which threw out of the
// pipeline): counts plain interior grass/forest tiles that sit next to sand or water,
// i.e. coast cells that never received an edge tile. One aggregated warning.
public sealed class CoastalGrassRule : IMapRule
{
    public string Name => "CoastalGrass";
    public bool AutoFixable => false;
    public bool Enabled { get; set; } = true;

    public ValidationResult Validate(GenIR ir, RectU16 scope, RuleContext ctx)
    {
        var result = new ValidationResult(Name);
        if (ir.LandId is null || ir.Biome is null) return result;
        var interior = new HashSet<ushort>();
        foreach (var bio in new[] { BiomeId.Grassland, BiomeId.Forest, BiomeId.DenseForest })
            if (ir.Tables.Land.TryGetValue(bio, out var set)) foreach (var id in set) interior.Add(id);

        int count = 0, sample = -1;
        for (int y = scope.Y1; y <= scope.Y2; y++)
        for (int x = scope.X1; x <= scope.X2; x++)
        {
            int idx = ir.Index(x, y);
            if (!interior.Contains(ir.LandId[idx])) continue;
            bool coastal = false;
            for (int dy = -1; dy <= 1 && !coastal; dy++)
            for (int dx = -1; dx <= 1 && !coastal; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                int nx = x + dx, ny = y + dy;
                if (nx < scope.X1 || ny < scope.Y1 || nx > scope.X2 || ny > scope.Y2) continue;
                int n = ir.Index(nx, ny);
                var nb = (BiomeId)ir.Biome[n];
                coastal = nb is BiomeId.Beach or BiomeId.Desert or BiomeId.DeepWater or BiomeId.ShallowWater or BiomeId.River
                          || TileFlags.IsWaterLandId(ir.LandId[n]);
            }
            if (!coastal) continue;
            count++;
            if (sample < 0) sample = idx;
        }
        if (count > 0)
            result.Add(new ValidationFinding(Name, ValidationSeverity.Warn,
                $"{count} plain grass/forest tiles touch sand or water without an edge tile (first at {sample % ir.Width},{sample / ir.Width})"));
        else
            result.Add(new ValidationFinding(Name, ValidationSeverity.Info, "every grass/forest coast cell has an edge tile"));
        return result;
    }
}
