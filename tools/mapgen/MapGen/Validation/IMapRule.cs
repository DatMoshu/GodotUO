using CentrED.MapGen.IR;

namespace CentrED.MapGen.Validation;

public interface IMapRule
{
    string Name { get; }
    bool AutoFixable { get; }
    bool Enabled { get; set; }
    ValidationResult Validate(GenIR ir, CentrED.Network.RectU16 scope, RuleContext ctx);
}

public sealed class RuleContext
{
    public required Random Rng { get; init; }
    public int SeaLevelZ { get; init; } = 0;
    // OceanZ: target Z for FlatWaterRule. Distinct from SeaLevelZ (which is a biome-
    // classification threshold used by NoiseHeight/BiomeAssign). Default -5 matches the
    // rest of Felucca's water surface (the live cells around our paint scope sit at
    // Z=-5 — see procgen image #58 / #67). Without this split the validator pulled
    // every water cell back to SeaLevelZ=0, producing the diagonal Z step at the
    // scope boundary.
    public int OceanZ { get; init; } = -5;
    public int EdgeBandWidth { get; init; } = 8;
    public int ZStepHardLimit { get; init; } = 16;
    public int ZStepWarnThreshold { get; init; } = 4;
}
