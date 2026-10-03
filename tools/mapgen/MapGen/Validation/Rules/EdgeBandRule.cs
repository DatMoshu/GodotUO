using CentrED.MapGen.IR;
using CentrED.Network;

namespace CentrED.MapGen.Validation.Rules;

// "No terrain at the edge of the map" — force the outer N tiles into water and drop any
// StaticOps in that band. Only for maps that already END in water: when more than
// MaxLandFraction of the band is land (inland-lakes, continent-to-the-edge presets) the
// rule leaves it alone instead of cutting an artificial moat around the map.
public sealed class EdgeBandRule : IMapRule
{
    public string Name => "EdgeBand";
    public bool AutoFixable => true;
    public bool Enabled { get; set; } = true;

    public double MaxLandFraction { get; set; } = 0.25;

    public ValidationResult Validate(GenIR ir, RectU16 scope, RuleContext ctx)
    {
        var result = new ValidationResult(Name);
        if (ir.LandId is null || ir.Height_Z is null) return result;
        int n = ctx.EdgeBandWidth;
        // Edge band uses uniform OceanZ — same Z as the main body. User explicitly wants
        // ALL water at one level (no deeper "edge floor") because the visible step from
        // -5 inside scope to -9 in the band to -5 in surrounding Felucca reads as a
        // discontinuity through the paint scope edge (procgen image #70 right side).
        sbyte deepZ = (sbyte)Math.Max(-128, ctx.OceanZ);
        var waterTiles = ir.Tables.Water.Length > 0 ? ir.Tables.Water : new ushort[] { 0xA8 };
        int touched = 0;
        if (n <= 0) return result;

        int bandCells = 0, bandLand = 0;
        for (ushort y = scope.Y1; y <= scope.Y2; y++)
        for (ushort x = scope.X1; x <= scope.X2; x++)
        {
            bool nearEdge = x < n || y < n || x >= ir.Width - n || y >= ir.Height - n;
            if (!nearEdge) continue;
            bandCells++;
            int i = ir.Index(x, y);
            bool water = CentrED.MapGen.Data.TileFlags.IsWaterLandId(ir.LandId[i])
                || (ir.Biome is not null && (BiomeId)ir.Biome[i] is BiomeId.DeepWater or BiomeId.ShallowWater);
            if (!water) bandLand++;
        }
        if (bandCells == 0) return result;
        if (bandLand > bandCells * MaxLandFraction)
        {
            result.Add(new ValidationFinding(Name, ValidationSeverity.Info,
                $"edge band is {100.0 * bandLand / bandCells:F0}% land — map runs to the edge by design; band left as is"));
            return result;
        }

        for (ushort y = scope.Y1; y <= scope.Y2; y++)
        for (ushort x = scope.X1; x <= scope.X2; x++)
        {
            bool nearEdge = x < n || y < n || x >= ir.Width - n || y >= ir.Height - n;
            if (!nearEdge) continue;
            int idx = ir.Index(x, y);
            if (!CentrED.MapGen.Data.TileFlags.IsWaterLandId(ir.LandId[idx]))
                ir.LandId[idx] = CentrED.MapGen.Pipeline.LatticePick.Pick(waterTiles, x, y, ir.Seed);
            if (ir.Height_Z[idx] != deepZ) ir.Height_Z[idx] = deepZ;
            if (ir.Biome is not null && (BiomeId)ir.Biome[idx] is not (BiomeId.DeepWater or BiomeId.ShallowWater))
                ir.Biome[idx] = (byte)BiomeId.DeepWater;
            touched++;
        }

        // Drop static ops that landed in the edge band.
        int dropped = 0;
        ir.StaticOps.RemoveAll(op =>
        {
            bool nearEdge = op.X < n || op.Y < n || op.X >= ir.Width - n || op.Y >= ir.Height - n;
            if (nearEdge) dropped++;
            return nearEdge;
        });

        if (touched > 0)
            result.Add(new ValidationFinding(Name, ValidationSeverity.Info,
                $"forced {touched} edge tiles to deep water", AutoFixed: true));
        if (dropped > 0)
            result.Add(new ValidationFinding(Name, ValidationSeverity.Info,
                $"dropped {dropped} statics from edge band", AutoFixed: true));
        return result;
    }
}
