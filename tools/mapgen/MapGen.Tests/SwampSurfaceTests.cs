using CentrED.MapGen.IR;
using CentrED.MapGen.Passes.Biome;
using CentrED.MapGen.Pipeline;
using CentrED.Network;

namespace CentrED.MapGen.Tests;

public class SwampSurfaceTests
{
    [Fact]
    public void SeparateSurfacesUseEdgesAndPreserveWaterAndSlopes()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path.Combine(dir,"land/1-Grass")); Directory.CreateDirectory(Path.Combine(dir,"Wild/Swamp"));
        try
        {
            File.WriteAllText(Path.Combine(dir,"land/1-Grass/1-Grass_To_50-Moss.xml"), """<Trans><TransInfo HashKey="010132010132010132"><MapTiles><MapTile TileID="15817" AltIDMod="0"/></MapTiles></TransInfo></Trans>""");
            File.WriteAllText(Path.Combine(dir,"Wild/Swamp/Moss -- Swamp.xml"),"<Trans/>");
            var ir = new GenIR(32,32,new RectU16(0,0,31,31),1);
            ir.EnsureBiome(); ir.EnsureLandId(); ir.EnsureHeight();
            Array.Fill(ir.Biome!, (byte)BiomeId.Grassland); Array.Fill(ir.LandId!, (ushort)3);
            for(int y=8;y<24;y++) for(int x=8;x<24;x++) ir.Biome![ir.Index(x,y)]=(byte)BiomeId.Swamp;
            ir.Biome![0]=(byte)BiomeId.DeepWater; ir.LandId![0]=0xA8; ir.Height_Z![0]=-5;
            ir.LandId[ir.Index(7,10)]=0x20; ir.Height_Z[ir.Index(8,10)]=-15;
            var heights=(sbyte[])ir.Height_Z.Clone();
            new SwampSurfacePass().Run(new GenContext{IR=ir,Rng=new Random(1),Report=new PassReport{PassName="test"}},new SwampSurfaceParams{CataloguePath=dir});
            Assert.InRange((int)ir.LandId[ir.Index(16,16)],0x3DE9,0x3DEC);
            Assert.InRange((int)ir.LandId[ir.Index(8,16)],0x3DED,0x3DF0);
            Assert.Equal(15817,ir.LandId[ir.Index(7,16)]);
            Assert.Equal(0x20,ir.LandId[ir.Index(7,10)]);
            Assert.Equal(0xA8,ir.LandId[0]); Assert.Equal(heights,ir.Height_Z);
        }
        finally { Directory.Delete(dir,true); }
    }
}
