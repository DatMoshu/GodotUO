using System.Text.Json;
using CentrED.MapGen.Data;
using CentrED.MapGen.IR;
using CentrED.MapGen.Pipeline;
using CentrED.MapGen.Passes.Biome;
using CentrED.MapGen.Passes.Network;
using CentrED.Network;

namespace CentrED.MapGen.Tests;

public class BorderCompositionTests
{
    private static GenIR World()
    {
        var ir = new GenIR(32, 32, new RectU16(0, 0, 31, 31), 42);
        ir.EnsureHeight(); ir.EnsureBiome(); ir.EnsureLandId();
        Array.Fill(ir.Biome!, (byte)BiomeId.Grassland);
        Array.Fill(ir.LandId!, (ushort)3);
        return ir;
    }
    private static GenContext Context(GenIR ir) => new() { IR = ir, Rng = new Random(1), Report = new PassReport { PassName = "test" } };

    [Fact]
    public void SubmergedCornerUsesBedRuleAndReplacesFullWaterAtSamePlane()
    {
        var ir = World();
        for (int y = 0; y < 32; y++) for (int x = 16; x < 32; x++)
        {
            int i = ir.Index(x, y); ir.Biome![i] = (byte)BiomeId.ShallowWater;
            ir.Height_Z![i] = -15; ir.LandId![i] = 0x64;
        }
        ir.StaticOps.Add(new(StaticOpKind.Add, 16, 16, -5, 0x1799, 0));
        ir.StaticOps.Add(new(StaticOpKind.Add, 16, 16, 10, 0x1799, 0));
        ir.StaticOps.Add(new(StaticOpKind.Add, 20, 16, -5, 0x1799, 0));
        string path = Path.GetTempFileName();
        try
        {
            var none = new Dictionary<string, int[][]>();
            var bed = new Dictionary<string, int[][]> { ["1:438"] = [[0x53, 100]] };
            var foam = new Dictionary<string, int[][]> { ["1:438"] = [[0x17A3, 100]] };
            File.WriteAllText(path, JsonSerializer.Serialize(new { schema = 1, exact = none, corners = none,
                bed_exact = bed, bed_corners = none, foam_exact = none, foam_corners = none,
                bed_foam_exact = foam, bed_foam_corners = none }));
            new ReferenceCoastPass().Run(Context(ir), new ReferenceCoastParams { AtlasPath = path });
            Assert.Equal(0x53, ir.LandId![ir.Index(16, 16)]);
            Assert.Contains(ir.StaticOps, o => o.X == 16 && o.Y == 16 && o.Id == 0x17A3);
            Assert.DoesNotContain(ir.StaticOps, o => o.X == 16 && o.Y == 16 && o.Z == -5 && o.Id == 0x1799);
            Assert.Contains(ir.StaticOps, o => o.X == 16 && o.Y == 16 && o.Z == 10 && o.Id == 0x1799);
            Assert.Contains(ir.StaticOps, o => o.X == 20 && o.Y == 16 && o.Id == 0x1799);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(BiomeId.ShallowWater)]
    [InlineData(BiomeId.River)]
    public void NarrowOceanInletGetsSubmergedBedWithoutExpandingIntoLand(BiomeId water)
    {
        var ir = World();
        for (int y = 0; y < 32; y++) for (int x = 14; x < 18; x++)
        {
            int i = ir.Index(x, y); ir.Biome![i] = (byte)water;
            ir.Height_Z![i] = -5; ir.LandId![i] = 0xA8;
        }
        new DigShorePass().Run(Context(ir), new DigShoreParams { DigNarrowInlets = true, SandExtension = 0 });
        Assert.Equal(-15, ir.Height_Z![ir.Index(14, 16)]);
        Assert.Equal(0, ir.Height_Z[ir.Index(13, 16)]);
        Assert.Equal(3, ir.LandId![ir.Index(13, 16)]);
        Assert.Contains(ir.StaticOps, o => o.X == 14 && o.Y == 16 && o.Z == -5);
    }

    [Fact]
    public void MountainTrailUsesDirtOwnedRockTransitions()
    {
        var ir = World();
        for (int y = 4; y < 28; y++) for (int x = 4; x < 28; x++)
        {
            int i = ir.Index(x, y); ir.Biome![i] = (byte)BiomeId.Mountain; ir.LandId![i] = 0x22C;
        }
        ir.Brushes = new LandBrushTable();
        var brush = new LandBrushTable.Brush { Name = "Dirt" };
        brush.Transitions["Mountain"] = Enumerable.Range(1, 255).Select(m => new LandBrushTable.Transition { Direction = (byte)m, TileID = 0x6EB }).ToList();
        ir.Brushes.Brushes["Dirt"] = brush;
        new MountainTrailPass().Run(Context(ir), new MountainTrailParams { MinRegionSize = 16, RockChance = 0 });
        new CliffEdgePass().Run(Context(ir), new CliffEdgeParams());
        Assert.Contains(ir.LandId!, id => id == 0x6EB);
        for (int i = 0; i < ir.LandId!.Length; i++)
            if (ir.LandId[i] == 0x6EB) Assert.Equal((byte)BiomeId.Road, ir.Biome![i]);
    }

}
