using System.Text.Json;
using CentrED.MapGen.IR;
using CentrED.MapGen.Pipeline;
using CentrED.MapGen.Passes.Biome;
using CentrED.MapGen.Passes.Terrain;
using CentrED.MapGen.Passes.Hydrology;
using CentrED.MapGen.Passes.Scatter;
using CentrED.MapGen.Data;
using CentrED.MapGen.Validation;
using CentrED.MapGen.Validation.Rules;
using CentrED.Network;

namespace CentrED.MapGen.Tests;

public class FeluccaCalibrationTests
{
    private static GenIR World(ushort size = 32)
    {
        var ir = new GenIR(size, size, new RectU16(0, 0, (ushort)(size - 1), (ushort)(size - 1)), 42);
        ir.EnsureHeight(); ir.EnsureBiome(); ir.EnsureLandId(); ir.EnsureMoisture(); ir.EnsureTemperature();
        Array.Fill(ir.Biome!, (byte)BiomeId.Grassland);
        Array.Fill(ir.LandId!, (ushort)3);
        Array.Fill(ir.Moisture!, (byte)128); Array.Fill(ir.Temperature!, (byte)128);
        return ir;
    }
    private static GenContext Context(GenIR ir) => new() { IR = ir, Rng = new Random(1), Report = new PassReport { PassName = "test" } };

    [Fact]
    public void DefaultPaintPaletteUsesCanonicalLandPools()
    {
        foreach (var entry in ImageImportPass.Palette.Default.Entries)
            if (TileTables.Default.Land.TryGetValue(entry.Biome, out var ids)) Assert.Equal(ids, entry.LandIds);
        Assert.Equal(new ushort[] { 0xAC, 0xAD, 0xAE, 0xAF }, ImageImportPass.Palette.Default.Entries.Single(e => e.Biome == BiomeId.Jungle).LandIds);
    }

    [Fact]
    public void PaletteTableOverridesKeepExistingPaintColors()
    {
        var tables = new TileTables { Land = new() { [BiomeId.Jungle] = new ushort[] { 1234 } } };
        var entry = ImageImportPass.Palette.FromTables(tables).Entries.Single(e => e.Biome == BiomeId.Jungle);
        Assert.Equal(new ushort[] { 1234 }, entry.LandIds);
        Assert.Equal((0, 96, 0), ((int)entry.R, (int)entry.G, (int)entry.B));
    }

    [Fact]
    public void FoamOnMixedCornersSurvivesButWaterOnFlatDryGroundDoesNot()
    {
        var ir = World(); ir.Height_Z![ir.Index(11, 10)] = -15;
        ir.StaticOps.Add(new(StaticOpKind.Add, 10, 10, -5, 0x17B0, 0));
        ir.StaticOps.Add(new(StaticOpKind.Add, 20, 20, -5, 0x17B0, 0));
        ir.StaticOps.Add(new(StaticOpKind.Add, 10, 10, -5, 0x1797, 0));
        new WaterStaticPlaneRule().Validate(ir, ir.Scope, new RuleContext { Rng = new Random(1) });
        var remaining = Assert.Single(ir.StaticOps);
        Assert.Equal(10, remaining.X); Assert.Equal(10, remaining.Y); Assert.Equal(0x17B0, remaining.Id);
    }

    [Fact]
    public void InteriorRockDensityIsNotAnIgnoredSetting()
    {
        var ir = World(16); Array.Fill(ir.Biome!, (byte)BiomeId.Mountain); Array.Fill(ir.Height_Z!, (sbyte)50);
        new MountainEdgeStaticsPass().Run(Context(ir), new MountainEdgeStaticsParams { EdgeDensity = 0, InteriorDensity = 1 });
        Assert.Equal(256, ir.StaticOps.Count);
        Assert.All(ir.StaticOps, o => { Assert.InRange((int)o.Id, 0x1363, 0x1366); Assert.Equal(50, o.Z); });
    }

    [Fact]
    public void CatalogueFallbackEmitsTrunkAndCanopyTogether()
    {
        string dir = Path.Combine(Path.GetTempPath(), "tree-pairs-" + Guid.NewGuid()); Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "Forest.xml"), """<RandomStatics Chance="15"><Statics Description="Test tree" Freq="10"><Static TileID="3277" X="0" Y="0" Z="0" Hue="0"/><Static TileID="3278" X="0" Y="0" Z="0" Hue="0"/></Statics></RandomStatics>""");
            var ir = World(); ir.Trees = TreeStatics.FromCatalogue(dir);
            Assert.Equal(3278, ir.Trees.TrunkToLeaf[3277]);
            Array.Fill(ir.Biome!, (byte)BiomeId.Forest); Array.Fill(ir.LandId!, (ushort)0xC4);
            ir.Tables = new TileTables { ForestSpecies = new() { [BiomeId.Forest] = new ushort[] { 3277 } }, ForestDensity = new() { [BiomeId.Forest] = 1 } };
            new ForestScatterPass().Run(Context(ir), new ForestScatterParams { MinRadius = 3, ShoreBuffer = 0 });
            Assert.NotEmpty(ir.StaticOps);
            foreach (var group in ir.StaticOps.GroupBy(o => (o.X, o.Y, o.Z))) Assert.Equal(new ushort[] { 3277, 3278 }, group.Select(o => o.Id).OrderBy(i => i));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void DiagonalRiversRemainConnectedAndNeverLoop()
    {
        var ir = World(64);
        for (int y = 0; y < 64; y++) { ir.Biome![ir.Index(63, y)] = (byte)BiomeId.ShallowWater; ir.Height_Z![ir.Index(63, y)] = -5; }
        new RiverCarvePass().Run(Context(ir), new RiverCarveParams { EightDirectionFlow = true, ScaleByArea = false, SourceCount = 20, MinSourceZ = 0, MaxSourceZ = 1, MinLength = 4, MaxWidth = 1, LakeFillEnabled = false, MeanderFrequency = .08 });
        Assert.NotEmpty(ir.Rivers); int diagonals = 0;
        foreach (var river in ir.Rivers)
        {
            Assert.Equal(river.Path.Count, river.Path.Distinct().Count());
            for (int i = 1; i < river.Path.Count; i++)
            {
                var a = river.Path[i - 1]; var b = river.Path[i];
                Assert.InRange(Math.Abs(a.X - b.X), 0, 1); Assert.InRange(Math.Abs(a.Y - b.Y), 0, 1);
                if (a.X == b.X || a.Y == b.Y) continue;
                diagonals++;
                Assert.True((BiomeId)ir.Biome![ir.Index(a.X, b.Y)] == BiomeId.River || (BiomeId)ir.Biome[ir.Index(b.X, a.Y)] == BiomeId.River);
            }
        }
        Assert.True(diagonals > 0);
    }

    [Theory]
    [InlineData(false, BiomeId.Grassland)]
    [InlineData(true, BiomeId.Beach)]
    public void BeachFringeIsExplicitAndOptional(bool force, BiomeId expected)
    {
        var ir = World(); ir.Height_Z![ir.Index(15, 15)] = -8;
        new BiomeAssignPass().Run(Context(ir), new BiomeAssignParams { ForceBeachFringe = force, RangeStrength = 0, MountainMinLandFraction = 0, SmoothPasses = 0, EdgeBandWidth = 0 });
        Assert.Equal((byte)expected, ir.Biome![ir.Index(16, 15)]);
        Assert.Equal((byte)BiomeId.ShallowWater, ir.Biome[ir.Index(15, 15)]);
    }

    [Fact]
    public void FlatLowlandsDoNotRaiseWater()
    {
        var ir = World(); Array.Fill(ir.Height_Z!, (sbyte)12); ir.Height_Z![0] = -8;
        new BiomeAssignPass().Run(Context(ir), new BiomeAssignParams { ForceBeachFringe = false, FlatLowlandFraction = 1, RangeStrength = 0, MountainMinLandFraction = 0, EdgeBandWidth = 0 });
        Assert.Equal(-8, ir.Height_Z[0]);
        Assert.All(ir.Height_Z.Skip(1), value => Assert.Equal(0, value));
    }

    [Fact]
    public void SteepRockRisesFromLowlandWithoutChangingItsFoot()
    {
        var ir = World();
        for (int y = 8; y < 24; y++) for (int x = 8; x < 24; x++) ir.Biome![ir.Index(x, y)] = (byte)BiomeId.Mountain;
        new MountainShapePass().Run(Context(ir), new MountainShapeParams { SteepProfile = true, PeakZ = 50, FootZ = 2, RampTiles = 10, MaxStep = 16, PeakAltitudeVariance = 0 });
        Assert.Equal(0, ir.Height_Z![ir.Index(7, 16)]);
        Assert.InRange((int)ir.Height_Z[ir.Index(8, 16)], 12, 16);
        Assert.InRange((int)ir.Height_Z[ir.Index(16, 16)], 44, 50);
    }

    [Fact]
    public void AtlasPreservesAnimatedWaterInUndugChannels()
    {
        var ir = World();
        for (int y = 0; y < 32; y++) for (int x = 16; x < 32; x++)
        {
            int i = ir.Index(x, y); ir.Biome![i] = (byte)BiomeId.ShallowWater; ir.LandId![i] = 0xA8; ir.Height_Z![i] = -5;
        }
        string path = Path.GetTempFileName();
        try
        {
            // All wet/dry configurations offered a bed tile: a channel without
            // overlay water must still reject that otherwise-valid atlas choice.
            var choices = Enumerable.Range(0, 16).ToDictionary(i => $"1:{i}", _ => new[] { new[] { 100, 100 } });
            File.WriteAllText(path, JsonSerializer.Serialize(new { schema = 1, exact = new { }, corners = choices }));
            new ReferenceCoastPass().Run(Context(ir), new ReferenceCoastParams { AtlasPath = path });
            for (int y = 1; y < 31; y++) Assert.Equal(0xA8, ir.LandId![ir.Index(16, y)]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void MissingAtlasReportsAndPreservesExistingTiles()
    {
        var ir = World(); var ctx = Context(ir);
        new ReferenceCoastPass().Run(ctx, new ReferenceCoastParams { AtlasPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid()+".json") });
        Assert.Single(ctx.Report.Warnings);
        Assert.All(ir.LandId!, id => Assert.Equal(3, id));
    }
}
