using CentrED.MapGen.IR;
using CentrED.MapGen.Pipeline;

namespace CentrED.MapGen.Passes.Terrain;

public sealed class SlopeDeriveParams
{
    [TunableDisplay("Sample radius")] [TunableRange(1, 4)]
    public int Radius { get; set; } = 1;
}

// Computes per-tile slope as max(|dz|) across N/E/S/W neighbours within Radius.
// Stored as byte (0..255) with saturation; actual sbyte Z range is -128..127 so deltas
// up to 255 fit. Cheap and good enough for biome / scatter cutoffs.
public sealed class SlopeDerivePass : IGenerationPass
{
    // The default pipeline runs this twice: early for erosion/climate and again (as
    // "Slope Re-derive") after coast, rivers and roads reshaped Z, so scatter passes see
    // the final slopes. Distinct names keep presets and the UI able to address each.
    public SlopeDerivePass() : this("Slope Derive") { }
    public SlopeDerivePass(string name) { Name = name; }

    public string Name { get; }
    public string Category => "Terrain";

    public IrFields Reads => IrFields.Height;
    public IrFields Writes => IrFields.Slope;

    public object CreateDefaultParams() => new SlopeDeriveParams();

    public void Run(GenContext ctx, object parameters)
    {
        var p = (SlopeDeriveParams)parameters;
        var ir = ctx.IR;
        ir.EnsureSlope();
        var z = ir.Height_Z!;
        var s = ir.Slope!;
        var scope = ir.Scope;
        int touched = 0;
        int r = Math.Max(1, p.Radius);
        for (ushort y = scope.Y1; y <= scope.Y2; y++)
        {
            for (ushort x = scope.X1; x <= scope.X2; x++)
            {
                int idx = ir.Index(x, y);
                int z0 = z[idx];
                int maxDelta = 0;
                if (x >= r) maxDelta = Math.Max(maxDelta, Math.Abs(z0 - z[ir.Index(x - r, y)]));
                if (x + r < ir.Width) maxDelta = Math.Max(maxDelta, Math.Abs(z0 - z[ir.Index(x + r, y)]));
                if (y >= r) maxDelta = Math.Max(maxDelta, Math.Abs(z0 - z[ir.Index(x, y - r)]));
                if (y + r < ir.Height) maxDelta = Math.Max(maxDelta, Math.Abs(z0 - z[ir.Index(x, y + r)]));
                s[idx] = (byte)Math.Min(255, maxDelta);
                touched++;
            }
        }
        ctx.Report.TilesTouched = touched;
    }
}
