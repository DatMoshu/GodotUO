using System.Text.Json;
using CentrED.MapGen.Data;
using CentrED.MapGen.IR;
using CentrED.MapGen.Passes.Biome;
using CentrED.MapGen.Stamps;
using CentrED.MapGen.Validation;
using CentrED.MapGen.Validation.Rules;
using CentrED.Network;

namespace CentrED.MapGen.Tests;

// Stamp seams (LandTransitionPass.RunOnMask), the static rules (TreeInWater whole groups,
// WaterStaticPlane), and the tile-id helpers that were stale.
public class SeamAndStaticRuleTests
{
    private static GenIR GrassMap(int size = 32)
    {
        var ir = new GenIR((ushort)size, (ushort)size, new RectU16(0, 0, (ushort)(size - 1), (ushort)(size - 1)), 42)
        {
            Tables = TileTables.LoadOrDefault(TestRepo.Path(TileTables.DefaultJsonRelativePath)),
            Brushes = LandBrushTable.LoadOrEmpty(TestRepo.Path(LandBrushTable.DefaultJsonRelativePath)),
        };
        ir.EnsureHeight(); ir.EnsureBiome(); ir.EnsureLandId();
        for (int i = 0; i < ir.TileCount; i++)
        {
            ir.Biome![i] = (byte)BiomeId.Grassland;
            ir.LandId![i] = 0x03;
            ir.Height_Z![i] = 5;
        }
        return ir;
    }

    private static RuleContext Ctx(GenIR ir) => new() { Rng = new Random(1), SeaLevelZ = ir.SeaLevelZ, OceanZ = ir.OceanZ };

    [Fact]
    public void StampSeams_FindsRunOnMask()
    {
        Assert.True(StampSeams.IsAvailable, "StampSeams cannot see LandTransitionPass.RunOnMask");
    }

    [Fact]
    public void RunOnMask_GivesAStampedCobblePatchRoadEdges_AndLeavesTheRestAlone()
    {
        var ir = GrassMap();
        var mask = new bool[ir.TileCount];
        for (int y = 12; y < 18; y++)
        for (int x = 12; x < 18; x++)
        {
            int i = ir.Index(x, y);
            ir.LandId![i] = 0x3E9;
            mask[i] = true;
        }
        var before = (ushort[])ir.LandId!.Clone();
        int painted = LandTransitionPass.RunOnMask(ir, mask);

        Assert.True(painted > 0);
        for (int y = 12; y < 18; y++)
        for (int x = 12; x < 18; x++)
            Assert.Equal((ushort)0x3E9, ir.LandId[ir.Index(x, y)]);           // stamp art kept
        int ringEdges = 0;
        for (int x = 11; x <= 18; x++)
        {
            if (ir.LandId[ir.Index(x, 11)] != 0x03) ringEdges++;
            if (ir.LandId[ir.Index(x, 18)] != 0x03) ringEdges++;
        }
        Assert.True(ringEdges >= 12, $"only {ringEdges} of 16 ring cells got an edge tile");
        for (int i = 0; i < ir.TileCount; i++)
        {
            int x = i % ir.Width, y = i / ir.Width;
            if (x >= 11 && x <= 18 && y >= 11 && y <= 18) continue;
            Assert.Equal(before[i], ir.LandId[i]);                               // nothing outside the ring
        }
    }

    [Fact]
    public void RunOnMask_StampedSandBlendsIntoGrass()
    {
        var ir = GrassMap();
        var mask = new bool[ir.TileCount];
        for (int y = 10; y < 20; y++)
        for (int x = 10; x < 20; x++)
        {
            int i = ir.Index(x, y);
            ir.LandId![i] = 0x16;
            mask[i] = true;
        }
        LandTransitionPass.RunOnMask(ir, mask);
        Assert.Equal((byte)BiomeId.Beach, ir.Biome![ir.Index(15, 15)]);
        // The grass ring owns the grass->sand edge.
        Assert.NotEqual((ushort)0x03, ir.LandId![ir.Index(15, 9)]);
        Assert.NotEqual((ushort)0x03, ir.LandId[ir.Index(9, 15)]);
    }

    [Fact]
    public void TreeInWater_RemovesWholeScatterGroups_AndKeepsRemoveOps()
    {
        var ir = GrassMap();
        int wx = 20, wy = 20;
        int w = ir.Index(wx, wy);
        ir.Biome![w] = (byte)BiomeId.ShallowWater;
        ir.LandId![w] = 0xA8;
        var group = new[]
        {
            new StaticOp(StaticOpKind.Add, (ushort)(wx - 1), (ushort)wy, 5, 0x0D3F, 0), // anchor on land
            new StaticOp(StaticOpKind.Add, (ushort)wx, (ushort)wy, 5, 0x0D40, 0),       // piece over water
        };
        ir.StaticOps.AddRange(group);
        OccupancyGrid.For(ir).AddPlacement(new OccupancyGrid.Placement("test", "g", new[] { w - 1, w }, group, IsStamp: false));
        var keepTree = new StaticOp(StaticOpKind.Add, 5, 5, 5, 0x0CCD, 0);
        var removeOp = new StaticOp(StaticOpKind.Remove, (ushort)wx, (ushort)wy, 0, 0x0CCD, 0);
        var waterStatic = new StaticOp(StaticOpKind.Add, (ushort)wx, (ushort)wy, -5, 0x17A0, 0);
        ir.StaticOps.Add(keepTree);
        ir.StaticOps.Add(removeOp);
        ir.StaticOps.Add(waterStatic);

        new TreeInWaterRule().Validate(ir, ir.Scope, Ctx(ir));

        Assert.DoesNotContain(group[0], ir.StaticOps);
        Assert.DoesNotContain(group[1], ir.StaticOps);
        Assert.Contains(keepTree, ir.StaticOps);
        Assert.Contains(removeOp, ir.StaticOps);
        Assert.Contains(waterStatic, ir.StaticOps);
    }

    [Fact]
    public void WaterStaticPlane_PinsGeneratedWaterStatics_ButNotStampOnes()
    {
        var ir = GrassMap();
        foreach (var (x, y) in new[] { (3, 3), (4, 3) })
        {
            int i = ir.Index(x, y);
            ir.Biome![i] = (byte)BiomeId.ShallowWater;
            ir.Height_Z![i] = (sbyte)ir.ShoreDigZ;
            ir.LandId![i] = 0x4C;
        }
        var onDryLand = new StaticOp(StaticOpKind.Add, 10, 10, -5, 0x179B, 0);
        ir.StaticOps.Add(onDryLand);
        var sunk = new StaticOp(StaticOpKind.Add, 3, 3, -15, 0x179A, 0);
        var rim = new StaticOp(StaticOpKind.Add, 4, 3, 2, 0x17A3, 0);
        var stampPond = new StaticOp(StaticOpKind.Add, 8, 8, 7, 0x1797, 0);
        ir.StaticOps.AddRange(new[] { sunk, rim, stampPond });
        OccupancyGrid.For(ir).AddPlacement(new OccupancyGrid.Placement("test", "pond", new[] { ir.Index(8, 8) }, new[] { stampPond }, IsStamp: true));

        new WaterStaticPlaneRule().Validate(ir, ir.Scope, Ctx(ir));

        Assert.Contains(sunk with { Z = (sbyte)ir.OceanZ }, ir.StaticOps);
        Assert.Contains(rim with { Z = (sbyte)ir.OceanZ }, ir.StaticOps);
        Assert.Contains(stampPond, ir.StaticOps);
        Assert.DoesNotContain(ir.StaticOps, op => op.X == 10 && op.Y == 10);   // water under grass dropped
    }

    [Theory]
    [InlineData(0x1559, true)]
    [InlineData(0x1796, true)]
    [InlineData(0x179A, true)]
    [InlineData(0x17A6, true)]
    [InlineData(0x17B2, true)]
    [InlineData(0x1795, false)]
    [InlineData(0x17B3, false)]
    public void WaterStaticIds_CoverTheWholeShoreRun(int id, bool water)
        => Assert.Equal(water, TileFlags.IsWaterStaticId((ushort)id));

    [Fact]
    public void LandIdClassifier_MatchesTheTileTables()
    {
        foreach (ushort id in TileTables.BuiltIn.Land[BiomeId.Grassland]) Assert.True(LandIdClassifier.IsGrass(id));
        foreach (ushort id in TileTables.BuiltIn.Land[BiomeId.Forest]) Assert.True(LandIdClassifier.IsForestFloor(id));
        foreach (ushort id in TileTables.BuiltIn.Land[BiomeId.Jungle]) Assert.True(LandIdClassifier.IsJungle(id));
        foreach (ushort id in TileTables.BuiltIn.Land[BiomeId.Beach]) Assert.True(LandIdClassifier.IsBeach(id));
        foreach (ushort id in new ushort[] { 0x07, 0x08, 0x09, 0x0B, 0xC5, 0xAC }) Assert.False(LandIdClassifier.IsGrass(id));
        foreach (ushort id in new ushort[] { 0x71, 0x78 }) Assert.True(LandIdClassifier.IsDirtOrScrub(id));
    }

    // The wall passes pick pieces by direction: corner/post, north-south run, east-west run.
    [Theory]
    [InlineData("maze", "Maze Stamp")]
    [InlineData("rooms-maze", "Rooms Stamp")]
    [InlineData("rooms-maze-256", "Rooms Stamp")]
    public void WallPresets_UseTheDirectionalStoneSet(string preset, string pass)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(TestRepo.Path($"tools/mapgen/MapGen/presets/{preset}.preset.json")));
        var p = doc.RootElement.GetProperty("passes").GetProperty(pass);
        Assert.Equal(0x80, p.GetProperty("WallId").GetInt32());
        Assert.Equal(0x82, p.GetProperty("WallIdNS").GetInt32());
        Assert.Equal(0x81, p.GetProperty("WallIdEW").GetInt32());
    }
}
