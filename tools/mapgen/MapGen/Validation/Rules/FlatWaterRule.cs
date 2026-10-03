using CentrED.MapGen.Data;
using CentrED.MapGen.IR;
using CentrED.Network;

namespace CentrED.MapGen.Validation.Rules;

// Every water body is one flat surface. UO water sprites are flat — varying Z produces
// visible stretching/jaggies.
//
// Bodies are 4-connected sets of water LandIds. An ocean body (Deep/Shallow biome) is
// pinned to the ocean plane (RuleContext.OceanZ = GenIR.OceanZ); a river/lake body to its
// own most common Z (River Carve gives each body one level). One aggregated finding per
// rule run instead of one per cell.
public sealed class FlatWaterRule : IMapRule
{
    public string Name => "FlatWater";
    public bool AutoFixable => true;
    public bool Enabled { get; set; } = true;

    public ValidationResult Validate(GenIR ir, RectU16 scope, RuleContext ctx)
    {
        var result = new ValidationResult(Name);
        if (ir.LandId is null || ir.Height_Z is null) return result;
        int sw = scope.Width, sh = scope.Height;
        var seen = new bool[sw * sh];
        var q = new Queue<int>();
        var cells = new List<int>();
        var zCount = new Dictionary<int, int>();
        int bodies = 0, fixedCells = 0, fixedBodies = 0;

        for (int i = 0; i < seen.Length; i++)
        {
            if (seen[i] || !IsWater(ir, G(i))) continue;
            bodies++;
            cells.Clear();
            zCount.Clear();
            bool ocean = false;
            seen[i] = true;
            q.Enqueue(i);
            while (q.Count > 0)
            {
                int c = q.Dequeue();
                cells.Add(c);
                int g = G(c);
                int zv = ir.Height_Z[g];
                zCount[zv] = zCount.GetValueOrDefault(zv) + 1;
                if (ir.Biome is null || (BiomeId)ir.Biome[g] is BiomeId.DeepWater or BiomeId.ShallowWater or BiomeId.Unassigned)
                    ocean = true;
                int cx = c % sw, cy = c / sw;
                if (cx > 0) Push(c - 1);
                if (cx < sw - 1) Push(c + 1);
                if (cy > 0) Push(c - sw);
                if (cy < sh - 1) Push(c + sw);
            }
            int target = ocean ? ctx.OceanZ : zCount.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).First().Key;
            int changed = 0;
            foreach (int c in cells)
            {
                int g = G(c);
                if (ir.Height_Z[g] == target) continue;
                ir.Height_Z[g] = (sbyte)Math.Clamp(target, sbyte.MinValue, sbyte.MaxValue);
                changed++;
            }
            if (changed > 0) { fixedCells += changed; fixedBodies++; }
        }

        if (fixedCells > 0)
            result.Add(new ValidationFinding(Name, ValidationSeverity.Info,
                $"flattened {fixedCells} water cells in {fixedBodies} of {bodies} bodies to their body Z", AutoFixed: true));
        return result;

        int G(int li) => ir.Index(scope.X1 + li % sw, scope.Y1 + li / sw);
        void Push(int nb)
        {
            if (seen[nb] || !IsWater(ir, G(nb))) return;
            seen[nb] = true;
            q.Enqueue(nb);
        }
    }

    private static bool IsWater(GenIR ir, int g) => TileFlags.IsWaterLandId(ir.LandId![g]);
}
