using CentrED.MapGen.Data;
using CentrED.MapGen.IR;
using CentrED.MapGen.Passes.Scatter;
using CentrED.MapGen.Pipeline;
using CentrED.Network;

namespace CentrED.MapGen.Tests;

public class SpeciesOverrideTests
{
    [Fact]
    public void SpeciesOverrideChangesTreesWithoutMutatingTerrainTables()
    {
        string path=Path.GetTempFileName();
        try
        {
            File.WriteAllText(path,"""{"forest_species":{"Forest":[3277]},"forest_density":{"Forest":1},"land":{"Forest":[999]}}""");
            var ir=new GenIR(32,32,new RectU16(0,0,31,31),1);
            ir.EnsureBiome();ir.EnsureHeight();ir.EnsureLandId();
            Array.Fill(ir.Biome!, (byte)BiomeId.Forest);Array.Fill(ir.LandId!, (ushort)0xC4);
            var original=ir.Tables;
            new ForestScatterPass().Run(new GenContext{IR=ir,Rng=new Random(1),Report=new PassReport{PassName="test"}},new ForestScatterParams{SpeciesTablesPath=path,ShoreBuffer=0,MinRadius=2});
            Assert.NotEmpty(ir.StaticOps);Assert.All(ir.StaticOps,op=>Assert.Equal(3277,op.Id));
            Assert.Same(original,ir.Tables);Assert.All(ir.LandId!,id=>Assert.Equal(0xC4,id));
        }
        finally{File.Delete(path);}
    }
}
