using CentrED.MapGen.Data;
using CentrED.MapGen.IR;
using CentrED.MapGen.Pipeline;
using CentrED.MapGen.Passes.Scatter;
using CentrED.Network;

namespace CentrED.MapGen.Tests;

public class TreeAssemblyTests
{
    [Fact]
    public void OffsetCanopiesArePlacedWholeAtCommonOriginElevation()
    {
        string dir = Path.Combine(Path.GetTempPath(), "tree-assembly-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "Jungle.xml"), """
            <RandomStatics Chance="100"><Statics Description="Jungle tree" Freq="1">
            <Static TileID="100" X="0" Y="0" Z="0" Hue="0"/>
            <Static TileID="101" X="1" Y="0" Z="2" Hue="5"/>
            <Static TileID="102" X="0" Y="1" Z="0" Hue="0"/>
            </Statics></RandomStatics>
            """);
            var ir = new GenIR(32, 32, new RectU16(0, 0, 31, 31), 123);
            ir.EnsureHeight(); ir.EnsureLandId(); ir.EnsureBiome();
            Array.Fill(ir.Height_Z!, (sbyte)4); Array.Fill(ir.LandId!, (ushort)0xAC); Array.Fill(ir.Biome!, (byte)BiomeId.Jungle);
            ir.Tables = new TileTables { ForestSpecies = new() { [BiomeId.Jungle] = [100] }, ForestDensity = new() { [BiomeId.Jungle] = 1 } };
            var ctx = new GenContext { IR = ir, Rng = new Random(1), Report = new PassReport { PassName = "test" } };
            new ForestScatterPass().Run(ctx, new ForestScatterParams { TreeCataloguePath = dir, MinRadius = 2, ShoreBuffer = 0 });
            var trunks = ir.StaticOps.Where(o => o.Id == 100).ToArray();
            Assert.NotEmpty(trunks); Assert.Equal(trunks.Length * 3, ir.StaticOps.Count);
            foreach (var trunk in trunks)
            {
                Assert.Equal(4, trunk.Z);
                Assert.Contains(ir.StaticOps, o => o.Id == 101 && o.X == trunk.X + 1 && o.Y == trunk.Y && o.Z == 6 && o.Hue == 5);
                Assert.Contains(ir.StaticOps, o => o.Id == 102 && o.X == trunk.X && o.Y == trunk.Y + 1 && o.Z == 4);
            }
            Assert.All(ir.StaticOps, o => { Assert.InRange((int)o.X, 0, 31); Assert.InRange((int)o.Y, 0, 31); });
            Assert.All(ir.StaticOps.GroupBy(o => (o.X, o.Y)), g => Assert.Single(g));
        }
        finally { Directory.Delete(dir, true); }
    }
}
