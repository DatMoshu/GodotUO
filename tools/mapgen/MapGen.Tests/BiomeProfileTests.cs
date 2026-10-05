using CentrED.MapGen.IR;
using CentrED.MapGen.Validation;
using CentrED.MapGen.Validation.Rules;
using CentrED.Network;

namespace CentrED.MapGen.Tests;

/// <summary>BiomeDistribution judges a planet by its own profile, not always by Felucca's mix.</summary>
public class BiomeProfileTests
{
    // 97% desert, 3% mountain: a desert planet.
    private static GenIR DesertWorld()
    {
        const int size = 32;
        var ir = new GenIR(size, size, new RectU16(0, 0, size - 1, size - 1), 7);
        ir.EnsureBiome();
        for (int i = 0; i < ir.TileCount; i++)
            ir.Biome![i] = (byte)(i % 33 == 0 ? BiomeId.Mountain : BiomeId.Desert);
        return ir;
    }

    private static ValidationResult Check(GenIR ir, string profile) =>
        new BiomeDistributionRule().Validate(ir, ir.Scope, new RuleContext { Rng = new Random(1), BiomeProfile = profile });

    [Fact]
    public void Felucca_FlagsADesertPlanet()
    {
        var r = Check(DesertWorld(), "felucca");
        Assert.True(r.Errors > 0);   // one biome over 92% of the land
        Assert.True(r.Warned > 0);   // no grass, too much desert
    }

    [Fact]
    public void Desert_AcceptsADesertPlanet()
    {
        var r = Check(DesertWorld(), "desert");
        Assert.Equal(0, r.Errors);
        Assert.Equal(0, r.Warned);
        Assert.Contains(r.Findings, f => f.Severity == ValidationSeverity.Info && f.Message.Contains("desert profile"));
    }

    [Fact]
    public void Desert_StillWarnsWhenTheMixIsWrong()
    {
        var ir = DesertWorld();
        for (int i = 0; i < ir.TileCount; i++) ir.Biome![i] = (byte)BiomeId.Forest;
        Assert.True(Check(ir, "desert").Warned > 0);
    }

    [Fact]
    public void UnknownProfile_IsAnError()
    {
        var r = Check(DesertWorld(), "tatooine");
        Assert.Equal(1, r.Errors);
    }
}
