using CentrED.MapGen.IR;
using CentrED.MapGen.Pipeline;

namespace CentrED.MapGen.Passes.Biome;

public sealed class LandIdResolveParams
{
    // v1 hardcoded weighted picks per biome; later: external JSON tile-set table.
}

public sealed class LandIdResolvePass : IGenerationPass
{
    public string Name => "Land ID Resolve";
    public string Category => "Biome";

    public IrFields Reads => IrFields.Biome;
    public IrFields Writes => IrFields.LandId;

    public object CreateDefaultParams() => new LandIdResolveParams();

    public void Run(GenContext ctx, object parameters)
    {
        var ir = ctx.IR;
        ir.EnsureLandId();
        var b = ir.Biome!;
        var l = ir.LandId!;
        var tiles = ir.Tables.Land;
        var scope = ir.Scope;
        int touched = 0;
        for (ushort y = scope.Y1; y <= scope.Y2; y++)
        for (ushort x = scope.X1; x <= scope.X2; x++)
        {
            int idx = ir.Index(x, y);
            var biome = (BiomeId)b[idx];
            if (tiles.TryGetValue(biome, out var set) && set.Length > 0)
                // Coord-hash uniform pick over the lattice. .NET Random.Next(N) showed
                // first-element bias on large maps (water 0xA8 at 38% across 28k samples
                // when the pool is 4 tiles); LatticePick is deterministic + provably uniform.
                l[idx] = LatticePick.Pick(set, x, y, ir.Seed);
            else
                l[idx] = 0x03;
            touched++;
        }
        ctx.Report.TilesTouched = touched;
    }
}
