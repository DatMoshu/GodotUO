using CentrED.MapGen.Data;
using CentrED.MapGen.IR;
using CentrED.MapGen.Passes.Biome;
using CentrED.MapGen.Pipeline;
using CentrED.Network;

namespace CentrED.MapGen.Tests;

public class SwampSurfaceTests
{
    // GUO's transition table (Grassland>Swamp, Swamp>Bog), the default: no Landscaper data needed.
    private static readonly HashSet<ushort> Bog = new() { 0x3DE9, 0x3DEA, 0x3DEB, 0x3DEC };

    private static (GenIR Ir, GenContext Ctx) SwampSquare(int size = 40, int x0 = 10, int x1 = 29)
    {
        var ir = new GenIR((ushort)size, (ushort)size, new RectU16(0, 0, (ushort)(size - 1), (ushort)(size - 1)), 42);
        ir.EnsureHeight(); ir.EnsureBiome(); ir.EnsureLandId();
        Array.Fill(ir.Biome!, (byte)BiomeId.Grassland); Array.Fill(ir.LandId!, (ushort)3); Array.Fill(ir.Height_Z!, (sbyte)5);
        for (int y = x0; y <= x1; y++)
            for (int x = x0; x <= x1; x++) { ir.Biome![ir.Index(x, y)] = (byte)BiomeId.Swamp; ir.LandId![ir.Index(x, y)] = 0x3DEB; }
        return (ir, new GenContext { IR = ir, Rng = new Random(1), Report = new PassReport { PassName = "test" } });
    }

    private static GuoTransitionTable.Pair Pair(string owner, string other) =>
        GuoTransitionTable.Load(TestRepo.BrushTable).Find(owner, other)!;

    private static HashSet<ushort> Ids(GuoTransitionTable.Pair p, string shape) => p.Edges[shape].Select(e => e.Id).ToHashSet();

    [Fact]
    public void GuoTable_SplitsMossAndBog_AndDrawsBothEdges()
    {
        var (ir, ctx) = SwampSquare();
        new SwampSurfacePass().Run(ctx, new SwampSurfaceParams { MossBorderWidth = 2 });
        Assert.Empty(ctx.Report.Warnings);
        Assert.Contains(ctx.Report.Notes, n => n.Contains("edges from"));
        ushort At(int x, int y) => ir.LandId![ir.Index(x, y)];

        Assert.Contains(At(20, 20), Bog);                                       // deep inside: open bog
        var moss = Pair("Swamp", "Bog"); var grass = Pair("Grassland", "Swamp");
        // Square 10..29, band 2: moss on 10..11 and 28..29 (distance 1..2), bog from 12 inward.
        Assert.Contains(At(20, 11), Ids(moss, "S"));                            // moss with bog to the south
        Assert.Contains(At(20, 28), Ids(moss, "N"));
        Assert.Contains(At(11, 20), Ids(moss, "E"));
        Assert.Contains(At(28, 20), Ids(moss, "W"));
        Assert.Contains(At(11, 11), Ids(moss, "in_SE"));                        // bog only on the diagonal
        Assert.Contains(At(20, 9), Ids(grass, "S"));                             // grass with moss to the south
        Assert.Contains(At(30, 20), Ids(grass, "W"));
        Assert.Contains(At(9, 9), Ids(grass, "in_SE"));
        Assert.Equal((ushort)3, At(20, 5));                                      // grass away from the swamp is untouched
    }

    [Fact]
    public void MissingLandscaperFolder_FallsBackToGuo()
    {
        var (ir, ctx) = SwampSquare();
        new SwampSurfacePass().Run(ctx, new SwampSurfaceParams { CataloguePath = "mined/no-such-transitions" });
        Assert.Empty(ctx.Report.Warnings);
        Assert.Contains(ctx.Report.Notes, n => n.Contains("using GUO's transition table"));
        Assert.Contains(ir.LandId![ir.Index(20, 20)], Bog);
    }

    [Fact]
    public void SlopedCellsAreLeftAlone()
    {
        var (ir, ctx) = SwampSquare();
        for (int x = 0; x < 40; x++) ir.Height_Z![ir.Index(x, 20)] = 9;          // a ridge row breaks flatness
        new SwampSurfacePass().Run(ctx, new SwampSurfaceParams());
        Assert.Equal((ushort)0x3DEB, ir.LandId![ir.Index(20, 19)]);              // its north neighbour is not flat
    }

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
