using System.Text;
using System.Text.Json;
using CentrED.MapGen.IR;
using CentrED.MapGen.Passes.Pois;
using CentrED.MapGen.Passes.Rooms;
using CentrED.MapGen.Passes.Scatter;
using CentrED.MapGen.Passes.Stamp;
using CentrED.MapGen.Pipeline;
using CentrED.MapGen.Stamps;
using CentrED.Network;

namespace CentrED.MapGen.Tests;

// Shared stamp loader/placer, occupancy, variants, rooms and spawner behaviour.
public class StampSystemTests
{
    private const ushort Grass = 0x0003;

    // ------------------------------------------------------------------ helpers

    private static LoadedStamp Stamp(string kind, int version, string? zMode, int w, int h,
        IEnumerable<(int rx, int ry, int z)> land, IEnumerable<(int rx, int ry, int id, int z)> statics,
        string? extra = null)
    {
        var sb = new StringBuilder();
        sb.Append($"{{\"id\":\"{kind}/t\",\"kind\":\"{kind}\",\"version\":{version},");
        if (zMode is not null) sb.Append($"\"z_mode\":\"{zMode}\",");
        if (extra is not null) sb.Append(extra).Append(',');
        sb.Append($"\"bounds\":{{\"w\":{w},\"h\":{h}}},\"anchor\":{{\"x\":{w / 2},\"y\":{h / 2}}},");
        sb.Append("\"land\":[").Append(string.Join(",", land.Select(l => $"{{\"rx\":{l.rx},\"ry\":{l.ry},\"id\":{Grass},\"z\":{l.z}}}"))).Append("],");
        sb.Append("\"statics\":[").Append(string.Join(",", statics.Select(s => $"{{\"rx\":{s.rx},\"ry\":{s.ry},\"id\":{s.id},\"z\":{s.z},\"hue\":0}}"))).Append("]}");
        using var doc = JsonDocument.Parse(sb.ToString());
        return StampLoader.Parse(doc.RootElement, $"{kind}/t.stamp.json");
    }

    private static IEnumerable<(int, int, int)> Square(int r, Func<int, int, int> z)
    {
        for (int ry = -r; ry <= r; ry++)
        for (int rx = -r; rx <= r; rx++)
            yield return (rx, ry, z(rx, ry));
    }

    /// <summary>A grass IR whose land rises 2 Z per tile eastwards (base 10).</summary>
    private static GenIR SlopedIr(int size = 32, int slopePerTile = 2)
    {
        var ir = new GenIR((ushort)size, (ushort)size, new RectU16(0, 0, (ushort)(size - 1), (ushort)(size - 1)), 7);
        ir.Height_Z = new sbyte[size * size];
        ir.LandId = new ushort[size * size];
        ir.Biome = new byte[size * size];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            int i = ir.Index(x, y);
            ir.Height_Z[i] = (sbyte)(10 + x * slopePerTile);
            ir.LandId[i] = Grass;
            ir.Biome[i] = (byte)BiomeId.Grassland;
        }
        return ir;
    }

    // ------------------------------------------------------------------ Z normalisation

    [Fact]
    public void V1AbsoluteStamp_IsNormalisedToTheAnchorGround()
    {
        // Source ground 20 at the anchor, 24 one tile east; a tree on the east tile at 24.
        var s = Stamp("forest", 1, null, 3, 3,
            Square(1, (rx, ry) => rx == 1 ? 24 : 20),
            new[] { (1, 0, 0x0CCD, 24) });
        Assert.Equal(StampZMode.Absolute, s.SourceZMode);
        Assert.Equal(20, s.ReferenceZ);
        Assert.Equal(20, s.SourceGroundZ);
        Assert.Equal(4, s.StaticZRel[0]);
        Assert.True(s.TryGetLandZ(0, 0, out var anchorZ) && anchorZ == 0);
    }

    [Fact]
    public void V2RelativeStamp_IsTakenAsIs_AndReadsReferenceZ()
    {
        var s = Stamp("forest", 2, "relative", 3, 3, Square(1, (_, _) => 0),
            new[] { (0, 0, 0x0CCD, 0) }, extra: "\"reference_z\":37");
        Assert.Equal(StampZMode.Relative, s.SourceZMode);
        Assert.Equal(0, s.ReferenceZ);
        Assert.Equal(37, s.SourceGroundZ);
        Assert.Equal(0, s.StaticZRel[0]);
    }

    [Fact]
    public void ExplicitAbsoluteZMode_WinsOverVersion()
    {
        var s = Stamp("forest", 2, "absolute", 1, 1, new[] { (0, 0, 15) }, new[] { (0, 0, 0x0CCD, 15) });
        Assert.Equal(StampZMode.Absolute, s.SourceZMode);
        Assert.Equal(0, s.StaticZRel[0]);
    }

    [Theory]
    [InlineData("_trash/20260101-000000/forest/a.stamp.json", true)]
    [InlineData("forest/_old/a.stamp.json", true)]
    [InlineData("_schema/x.stamp.json", true)]
    [InlineData("forest/a.stamp.json", false)]
    [InlineData("forest/_a.stamp.json", false)]
    public void ReservedFolders_AreNotPartOfTheLibrary(string rel, bool reserved) =>
        Assert.Equal(reserved, StampLoader.IsReservedPath(rel));

    [Fact]
    public void Loader_SkipsUnderscoreFolders_AndCachesUntilFilesChange()
    {
        var root = Path.Combine(Path.GetTempPath(), "mapgen-stamps-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "forest"));
            Directory.CreateDirectory(Path.Combine(root, "_trash", "x", "forest"));
            const string json = "{\"id\":\"forest/a\",\"kind\":\"forest\",\"version\":2,\"z_mode\":\"relative\",\"bounds\":{\"w\":1,\"h\":1},\"anchor\":{\"x\":0,\"y\":0},\"land\":[],\"statics\":[{\"rx\":0,\"ry\":0,\"id\":3277,\"z\":0,\"hue\":0}]}";
            File.WriteAllText(Path.Combine(root, "forest", "a.stamp.json"), json);
            File.WriteAllText(Path.Combine(root, "_trash", "x", "forest", "b.stamp.json"), json.Replace("forest/a", "forest/b"));
            var lib = StampLoader.Load(root);
            Assert.Equal(1, lib.TotalCount);
            Assert.Single(lib.GetKind("forest"));
            Assert.Same(lib, StampLoader.Load(root));
        }
        finally
        {
            StampLoader.InvalidateCache(root);
            try { Directory.Delete(root, true); } catch { }
        }
    }

    // ------------------------------------------------------------------ slopes: no float / sink

    [Fact]
    public void PerTileGrounding_PutsEveryStaticOnTheGroundUnderIt()
    {
        var ir = SlopedIr();
        // Flat source stamp: three trees in a row, each on its own source ground.
        var s = Stamp("forest", 2, "relative", 5, 1,
            new[] { (-2, 0, 0), (-1, 0, 0), (0, 0, 0), (1, 0, 0), (2, 0, 0) },
            new[] { (-2, 0, 0x0CCD, 0), (0, 0, 0x0CD0, 0), (2, 0, 0x0CD3, 0) });
        var opt = new StampPlaceOptions { Grounding = StampGrounding.PerTile };
        Assert.True(StampPlacer.TryPlace(ir, s, 16, 16, StampVariant.Original, opt, out var plan), plan.RejectReason);
        foreach (var st in plan.Statics)
            Assert.Equal(ir.Height_Z![ir.Index(st.X, st.Y)], st.Z);
    }

    [Fact]
    public void PerTileGrounding_KeepsHeightAboveTheSourceGround()
    {
        var ir = SlopedIr();
        // A static 7 above its source ground (e.g. something on a table) stays 7 above.
        var s = Stamp("forest", 2, "relative", 3, 1,
            new[] { (-1, 0, 0), (0, 0, 3), (1, 0, 0) },
            new[] { (0, 0, 0x0CCD, 10) });
        var opt = new StampPlaceOptions { Grounding = StampGrounding.PerTile };
        Assert.True(StampPlacer.TryPlace(ir, s, 16, 16, StampVariant.Original, opt, out var plan), plan.RejectReason);
        var st = Assert.Single(plan.Statics);
        Assert.Equal(ir.Height_Z![ir.Index(16, 16)] + 7, st.Z);
    }

    [Fact]
    public void RigidGrounding_FlattensTheFootprint_AndStaticsSitOnIt()
    {
        var ir = SlopedIr();
        var s = Stamp("town_quarter", 2, "relative", 3, 3, Square(1, (_, _) => 0),
            new[] { (-1, -1, 0x0080, 0), (1, 1, 0x0080, 0) });
        var opt = new StampPlaceOptions { Grounding = StampGrounding.Rigid, BlendEdges = false };
        Assert.True(StampPlacer.TryPlace(ir, s, 16, 16, StampVariant.Original, opt, out var plan), plan.RejectReason);
        foreach (var l in plan.Land) Assert.Equal(plan.BaseZ, l.Z);
        foreach (var st in plan.Statics)
        {
            Assert.Equal(plan.BaseZ, st.Z);
            Assert.Equal(ir.Height_Z![ir.Index(st.X, st.Y)], st.Z); // land was written
        }
        Assert.Equal(10 + 16 * 2, plan.BaseZ); // mean of the 40/42/44 columns
    }

    [Fact]
    public void Plan_RejectsSteepFootprints_AndWater()
    {
        var steep = SlopedIr(slopePerTile: 10);
        var s = Stamp("forest", 2, "relative", 5, 5, Square(2, (_, _) => 0), new[] { (0, 0, 0x0CCD, 0) });
        var plan = StampPlacer.Plan(s, 5, 10, StampVariant.Original, new GenIrTerrain(steep), new StampPlaceOptions());
        Assert.Equal("slope", plan.RejectReason);

        var wet = SlopedIr();
        wet.Height_Z![wet.Index(11, 10)] = -5; // one footprint tile below sea level, not the centre
        plan = StampPlacer.Plan(s, 10, 10, StampVariant.Original, new GenIrTerrain(wet), new StampPlaceOptions());
        Assert.Equal("water", plan.RejectReason);
    }

    [Fact]
    public void Plan_ChecksTheBiomeOverTheWholeFootprint()
    {
        var ir = SlopedIr();
        ir.Biome![ir.Index(12, 10)] = (byte)BiomeId.Desert; // edge tile only
        var s = Stamp("forest", 2, "relative", 5, 5, Square(2, (_, _) => 0), new[] { (0, 0, 0x0CCD, 0) });
        var opt = new StampPlaceOptions { AllowedBiomes = new HashSet<BiomeId> { BiomeId.Grassland } };
        var plan = StampPlacer.Plan(s, 10, 10, StampVariant.Original, new GenIrTerrain(ir), opt,
            (x, y) => (BiomeId)ir.Biome![ir.Index(x, y)]);
        Assert.Equal("biome", plan.RejectReason);
    }

    [Fact]
    public void PaintedStamps_DoNotCarryWaterOntoDryLand()
    {
        var ir = SlopedIr(slopePerTile: 0);
        // Town window cut next to a canal: one water land tile and a shore-foam static.
        using var doc = JsonDocument.Parse("""
        {"id":"town_quarter/w","kind":"town_quarter","version":2,"z_mode":"relative","bounds":{"w":3,"h":1},"anchor":{"x":1,"y":0},
         "land":[{"rx":-1,"ry":0,"id":168,"z":0},{"rx":0,"ry":0,"id":3,"z":0},{"rx":1,"ry":0,"id":1001,"z":0}],
         "statics":[{"rx":-1,"ry":0,"id":6056,"z":0,"hue":0},{"rx":1,"ry":0,"id":128,"z":0,"hue":0}]}
        """);
        var s = StampLoader.Parse(doc.RootElement, "town_quarter/w.stamp.json");
        var opt = new StampPlaceOptions { Grounding = StampGrounding.Rigid, PaintLand = true };
        var plan = StampPlacer.Plan(s, 10, 10, StampVariant.Original, new GenIrTerrain(ir), opt);
        Assert.True(plan.Ok, plan.RejectReason);
        Assert.Equal(Grass, plan.Land.Single(l => l.X == 9).Id);       // water id not painted
        Assert.Equal(1001, plan.Land.Single(l => l.X == 11).Id);       // other ids are
        Assert.DoesNotContain(plan.Statics, st => st.Id == 6056);       // 0x17A8 shore water dropped
        Assert.Contains(plan.Statics, st => st.Id == 128);
    }

    // ------------------------------------------------------------------ occupancy

    [Fact]
    public void Occupancy_PreventsOverlappingStamps()
    {
        var ir = SlopedIr(slopePerTile: 0);
        var s = Stamp("town_quarter", 2, "relative", 3, 3, Square(1, (_, _) => 0), new[] { (0, 0, 0x0080, 0) });
        var opt = new StampPlaceOptions { Grounding = StampGrounding.Rigid };
        Assert.True(StampPlacer.TryPlace(ir, s, 10, 10, StampVariant.Original, opt, out _));
        Assert.False(StampPlacer.TryPlace(ir, s, 10, 10, StampVariant.Original, opt, out var again));
        Assert.Equal("overlap", again.RejectReason);
        Assert.False(StampPlacer.TryPlace(ir, s, 12, 11, StampVariant.Original, opt, out var touching));
        Assert.Equal("overlap", touching.RejectReason);
        Assert.True(StampPlacer.TryPlace(ir, s, 13, 10, StampVariant.Original, opt, out _));
        Assert.Equal(2, OccupancyGrid.For(ir).Placements.Count);
    }

    [Fact]
    public void Stamp_ClearsSoftScatterUnderIt_ButNotLaterOps()
    {
        var ir = SlopedIr(slopePerTile: 0);
        var occ = OccupancyGrid.For(ir);
        // A scattered tree on a soft cell.
        ir.StaticOps.Add(new StaticOp(StaticOpKind.Add, 10, 10, 10, 0x0CCD, 0));
        occ.MarkSoft(10, 10);
        var s = Stamp("town_quarter", 2, "relative", 3, 3, Square(1, (_, _) => 0), new[] { (0, 0, 0x0080, 0) });
        Assert.True(StampPlacer.TryPlace(ir, s, 10, 10, StampVariant.Original,
            new StampPlaceOptions { Grounding = StampGrounding.Rigid }, out _));
        StampPlacer.FinishPass(ir);
        Assert.DoesNotContain(ir.StaticOps, o => o.Id == 0x0CCD);
        Assert.Contains(ir.StaticOps, o => o.Id == 0x0080 && o.X == 10 && o.Y == 10);
    }

    // ------------------------------------------------------------------ variants

    [Theory]
    [InlineData("town_quarter")]
    [InlineData("road_segment")]
    [InlineData("road_junction")]
    [InlineData("shoreline")]
    [InlineData("other")]
    public void DirectionalKinds_NeverVary(string kind)
    {
        var s = Stamp(kind, 2, "relative", 1, 1, new[] { (0, 0, 0) }, new[] { (0, 0, 0x0080, 0) });
        Assert.False(s.AllowsVariants);
        foreach (StampVariant v in Enum.GetValues<StampVariant>())
            Assert.Equal(StampVariant.Original, StampVariantPolicy.Resolve(s, v));
        var rng = new Random(1);
        for (int i = 0; i < 50; i++) Assert.Equal(StampVariant.Original, StampVariantPolicy.Pick(s, rng, useVariants: true));
    }

    [Fact]
    public void ForestStamps_MayVary()
    {
        var s = Stamp("forest", 2, "relative", 1, 1, new[] { (0, 0, 0) }, new[] { (0, 0, 0x0CCD, 0) });
        Assert.True(s.AllowsVariants);
        Assert.Equal(StampVariant.Rot90, StampVariantPolicy.Resolve(s, StampVariant.Rot90));
    }

    // ------------------------------------------------------------------ determinism helpers

    [Fact]
    public void StableHash_IsFixedAcrossProcesses()
    {
        // Known FNV-1a 64 values: these must never change (seeds and spawner GUIDs derive from them).
        Assert.Equal(0xcbf29ce484222325UL, StableHash.Fnv1a64(""));
        Assert.Equal(0xaf63dc4c8601ec8cUL, StableHash.Fnv1a64("a"));
        Assert.Equal(StableHash.Guid("x"), StableHash.Guid("x"));
        Assert.NotEqual(StableHash.Guid("x"), StableHash.Guid("y"));
    }

    // ------------------------------------------------------------------ rooms

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void RoomsCarve_GuaranteesBossAndTreasure_WithoutDuplicatingBounds(int seed)
    {
        const int size = 160;
        var ir = new GenIR(size, size, new RectU16(0, 0, size - 1, size - 1), (ulong)seed);
        var pass = new RoomsCarvePass();
        var p = (RoomsCarveParams)pass.CreateDefaultParams();
        p.WeightBoss = 0;
        p.WeightTreasure = 0;
        var report = new PassReport { PassName = pass.Name };
        pass.Run(new GenContext { IR = ir, Rng = PipelineRunner.CreateStepRng(ir.Seed, 0), Report = report }, p);

        var rooms = ir.DungeonRooms;
        Assert.True(rooms.Count >= 2, $"only {rooms.Count} rooms");
        Assert.Contains(rooms, r => r.Kind == RoomKind.Boss);
        Assert.Contains(rooms, r => r.Kind == RoomKind.Treasure);
        var bounds = rooms.Select(r => (r.X1, r.Y1, r.X2, r.Y2)).ToList();
        Assert.Equal(bounds.Count, bounds.Distinct().Count());
        Assert.Empty(report.Warnings);
    }

    [Fact]
    public void SpawnerEmit_WritesNothingByDefault_AndIsDeterministic()
    {
        const int size = 160;
        string Run()
        {
            var ir = new GenIR(size, size, new RectU16(0, 0, size - 1, size - 1), 11);
            var carve = new RoomsCarvePass();
            carve.Run(new GenContext { IR = ir, Rng = PipelineRunner.CreateStepRng(ir.Seed, 0), Report = new PassReport { PassName = "t" } }, carve.CreateDefaultParams());
            ir.Height_Z = new sbyte[size * size];
            Array.Fill(ir.Height_Z, (sbyte)-20);
            var emit = new DungeonSpawnerEmitPass();
            var p = (DungeonSpawnerEmitParams)emit.CreateDefaultParams();
            Assert.Equal("", p.OutputDir);
            emit.Run(new GenContext { IR = ir, Rng = PipelineRunner.CreateStepRng(ir.Seed, 1), Report = new PassReport { PassName = "t" } }, p);
            Assert.Null(emit.LastOutputPath);
            Assert.NotNull(emit.LastJson);
            return emit.LastJson!;
        }
        var spawnDir = TestRepo.Path("Data/Spawns");
        var before = Directory.Exists(spawnDir) ? Directory.GetFiles(spawnDir).Select(f => (f, File.GetLastWriteTimeUtc(f))).ToList() : new();
        var a = Run();
        var b = Run();
        Assert.Equal(a, b);                         // GUIDs are derived, not random
        Assert.Contains("-20", a);                  // Z from the floor, not 0
        var after = Directory.Exists(spawnDir) ? Directory.GetFiles(spawnDir).Select(f => (f, File.GetLastWriteTimeUtc(f))).ToList() : new();
        Assert.Equal(before, after);
    }

    // ------------------------------------------------------------------ pipeline registration

    [Fact]
    public void DefaultPipeline_RegistersStampPasses_OffByDefault_AndResnapLast()
    {
        var steps = DefaultPipeline.Build();
        var names = steps.Select(s => s.Pass.Name).ToList();
        int validator = names.IndexOf("Map Validator");
        Assert.True(validator >= 0);
        foreach (var n in new[] { "Stamp Scatter", "Town Stamps" })
        {
            int i = names.IndexOf(n);
            Assert.InRange(i, 0, validator - 1);
            Assert.False(steps[i].Enabled, n + " must be opt-in: it changes every preset's look");
        }
        Assert.IsType<StaticResnapPass>(steps[^1].Pass);
        Assert.True(steps[^1].Enabled);
    }

    // ------------------------------------------------------------------ POIs

    [Fact]
    public void PoiStamps_SpacingAppliesAcrossQuadrants()
    {
        const int size = 256;
        List<PoiStamp> Run(bool across)
        {
            var ir = new GenIR(size, size, new RectU16(0, 0, size - 1, size - 1), 5);
            ir.Height_Z = Enumerable.Repeat((sbyte)20, size * size).ToArray();
            var pass = new PoiStampPass();
            var p = (PoiStampParams)pass.CreateDefaultParams();
            p.AutoGridX = 4; p.AutoGridY = 4; p.MinSpacing = 96; p.ControlPointsPerQuadrant = 2; p.DungeonsPerQuadrant = 0;
            p.SpacingAcrossQuadrants = across;
            pass.Run(new GenContext { IR = ir, Rng = PipelineRunner.CreateStepRng(ir.Seed, 0), Report = new PassReport { PassName = "t" } }, p);
            return ir.Pois.ToList();
        }
        var spread = Run(true);
        for (int i = 0; i < spread.Count; i++)
        for (int j = i + 1; j < spread.Count; j++)
        {
            int dx = spread[i].X - spread[j].X, dy = spread[i].Y - spread[j].Y;
            Assert.True(dx * dx + dy * dy >= 96 * 96, $"POIs {spread[i].Id} and {spread[j].Id} are closer than Min spacing");
        }
        Assert.True(spread.Count < Run(false).Count);   // fewer POIs, so Road Graph builds fewer roads
    }
}
