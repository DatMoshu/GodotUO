using System.Text.Json.Nodes;
using CentrED.MapGen.Data;
using CentrED.MapGen.IR;
using CentrED.MapGen.Passes.Biome;
using CentrED.MapGen.Pipeline;
using CentrED.Network;

namespace CentrED.MapGen.Tests;

// GUO's own transition table (transitions.guo.json, docs/data_formats.md §26, "Transition table"): the edge shapes, the
// committed table's integrity, the loader, measured resolution, and the pass features it drives.
public class GuoTransitionTableTests
{
    private static GuoTransitionTable Committed() => GuoTransitionTable.Load(TestRepo.BrushTable);

    private static GuoTransitionTable FromJson(string pairs, string extra = "") =>
        GuoTransitionTable.Parse(JsonNode.Parse($$"""{"format":"guo.mapgen.transitions/1"{{extra}},"pairs":[{{pairs}}]}""")!.AsObject());

    [Fact]
    public void EdgeShapes_EachShapeResolvesToItself_SliversToNothing()
    {
        foreach (var (name, dir) in EdgeShapes.All)
            Assert.Equal(name, EdgeShapes.ShapeOf(dir));
        Assert.Equal("N", EdgeShapes.ShapeOf(EdgeShapes.N));
        Assert.Equal("NE", EdgeShapes.ShapeOf(EdgeShapes.N | EdgeShapes.E));
        Assert.Equal("in_SW", EdgeShapes.ShapeOf(EdgeShapes.SW));
        Assert.Null(EdgeShapes.ShapeOf(EdgeShapes.N | EdgeShapes.S));   // a one-tile strip
        Assert.Null(EdgeShapes.ShapeOf(0));
        Assert.Null(EdgeShapes.ShapeOf(0xFF));
    }

    [Fact]
    public void CommittedTable_IsWellFormed()
    {
        var t = Committed();
        var interior = new HashSet<ushort>(TileTables.BuiltIn.Land.Values.SelectMany(v => v));
        foreach (var p in t.Pairs)
        {
            Assert.True(t.Materials.Contains(p.Owner) && t.Materials.Contains(p.Other), p.Key);
            Assert.False(string.IsNullOrWhiteSpace(p.Notes), $"{p.Key} has no notes");
            if (p.Edges.Count == 0) continue;
            // A pair with tiles draws every shape, and no tile stands for two shapes.
            Assert.Equal(EdgeShapes.All.Length, p.Edges.Count);
            var ids = p.Edges.Values.SelectMany(v => v.Select(e => e.Id)).ToList();
            Assert.Equal(ids.Count, ids.Distinct().Count());
            Assert.DoesNotContain(ids, id => interior.Contains(id) || TileFlags.IsWaterLandId(id));
        }
        // A bridge lands on a material both sides can meet.
        foreach (var p in t.Pairs.Where(p => p.Via is not null))
        {
            bool Meets(string a, string b) => t.Find(a, b) is not null || t.Find(b, a) is not null || a == b;
            Assert.True(Meets(p.Owner, p.Via!) && Meets(p.Via!, p.Other), $"{p.Key} via {p.Via}");
        }
    }

    [Fact]
    public void Loader_ReadsWeights_AndTheLookupRepeatsThem()
    {
        var t = FromJson("""{"owner":"A","other":"B","edges":{"N":[["0x0101",3],"0x0102"]},"z":{"N":5,"*":-2}}""");
        var b = t.ToBrushTable();
        var lut = b.Lookup("A", "B")!;
        Assert.Equal(3, lut[EdgeShapes.N]!.Count(id => id == 0x101));
        Assert.Equal(1, lut[EdgeShapes.N]!.Count(id => id == 0x102));
        Assert.Equal(5, GuoTransitionTable.ZFor(t.Pairs[0], "N"));
        Assert.Equal(-2, GuoTransitionTable.ZFor(t.Pairs[0], "in_NE"));
        Assert.Equal((sbyte)5, b.EdgeZ["A>B"][(byte)(EdgeShapes.NW | EdgeShapes.N | EdgeShapes.NE)]);
    }

    [Theory]
    [InlineData("""{"owner":"A","other":"B","edges":{"UP":["0x0101"]}}""")]           // unknown shape
    [InlineData("""{"owner":"A","other":"B","edges":{"N":["0x4000"]}}""")]           // not a land id
    [InlineData("""{"owner":"A","other":"B","edges":{"N":["257"]}}""")]              // not hex
    [InlineData("""{"owner":"A","other":"B","edges":{"N":[["0x0101",0]]}}""")]       // zero weight
    [InlineData("""{"owner":"A","other":"B","z":{"N":99}}""")]                       // z out of range
    [InlineData("""{"owner":"A","other":"B"},{"owner":"A","other":"B"}""")]          // listed twice
    public void Loader_RejectsBadTables(string pairs)
    {
        Assert.Throws<InvalidDataException>(() => FromJson(pairs));
    }

    [Fact]
    public void Resolve_AddsMeasuredVariants_WithinTheRules()
    {
        var t = FromJson("""
            {"owner":"A","other":"B","edges":{"N":["0x0101"]}},
            {"owner":"A","other":"C","edges":{},"measure":false}
            """, ""","resolve":{"min_pair_samples":10,"min_shape_samples":5,"min_share":0.1,"max_variants":4,"scale":8}""");
        var measure = JsonNode.Parse("""
            {"schema":"guo.mapgen.transition-measure/1","source":{"map":"test"},"pairs":{
              "A>B":{"samples":100,"shapes":{
                "N":{"count":100,"top":[["0x0101",50],["0x0201",40],["0x0003",9],["0x0202",1]]},
                "E":{"count":3,"top":[["0x0301",3]]}}},
              "A>C":{"samples":100,"shapes":{"N":{"count":100,"top":[["0x0401",100]]}}}}}
            """)!.AsObject();
        var (r, added) = t.ResolveWith(measure, id => id == 0x0003);
        var n = r.Find("A", "B")!.Edges["N"];
        Assert.Equal(1, added);                                        // 0x0201; 0x0003 is interior, 0x0202 too rare
        Assert.Equal(8, n.Single(e => e.Id == 0x0101).Weight);         // the core id takes its measured weight
        Assert.Equal(6, n.Single(e => e.Id == 0x0201).Weight);         // 40/50 * 8, rounded
        Assert.False(r.Find("A", "B")!.Edges.ContainsKey("E"));        // too few samples
        Assert.Empty(r.Find("A", "C")!.Edges);                         // measure: false
        Assert.Single(t.Find("A", "B")!.Edges["N"]);                   // the source table is untouched
    }

    private static GenIR Map(int size, Func<int, int, BiomeId> biome, Func<int, int, sbyte>? z = null)
    {
        var ir = new GenIR((ushort)size, (ushort)size, new RectU16(0, 0, (ushort)(size - 1), (ushort)(size - 1)), 7)
        {
            Tables = TileTables.LoadOrDefault(TestRepo.Path(TileTables.DefaultJsonRelativePath)),
            Brushes = LandBrushTable.LoadOrEmpty(TestRepo.BrushTable),
        };
        ir.EnsureHeight(); ir.EnsureBiome(); ir.EnsureLandId();
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            int i = ir.Index(x, y);
            var bio = biome(x, y);
            ir.Biome![i] = (byte)bio;
            ir.LandId![i] = ir.Tables.Land[bio][0];
            ir.Height_Z![i] = z?.Invoke(x, y) ?? (sbyte)(bio is BiomeId.ShallowWater ? ir.OceanZ : 5);
        }
        return ir;
    }

    private static PassReport Run(GenIR ir, LandTransitionParams p)
    {
        var report = new PassReport { PassName = "t" };
        new LandTransitionPass().Run(new GenContext { IR = ir, Rng = new Random(1), Report = report }, p);
        return report;
    }

    [Fact]
    public void Bridges_PutSandBetweenGrassAndWater_AndGrassBetweenForestAndSand()
    {
        // Columns: forest | grass | water, then a forest block against sand.
        var ir = Map(32, (x, y) => y < 16
            ? (x < 10 ? BiomeId.Forest : x < 20 ? BiomeId.Grassland : BiomeId.ShallowWater)
            : (x < 16 ? BiomeId.Forest : BiomeId.Beach));
        Run(ir, new LandTransitionParams());
        for (int y = 1; y < 31; y++)
        for (int x = 1; x < 31; x++)
        {
            var self = (BiomeId)ir.Biome![ir.Index(x, y)];
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                var nb = (BiomeId)ir.Biome[ir.Index(x + dx, y + dy)];
                Assert.False(self == BiomeId.Grassland && nb == BiomeId.ShallowWater, $"grass touches water at ({x},{y})");
                Assert.False(self == BiomeId.Forest && nb == BiomeId.Beach, $"forest touches sand at ({x},{y})");
            }
        }
    }

    [Fact]
    public void EdgeZOffsets_RaiseTheRockLip_OnlyWhenAskedFor()
    {
        // Rock to the north (y < 8) at z 40, grass below at z 5: the grass row y = 8 is an N edge.
        BiomeId Bio(int x, int y) => y < 8 ? BiomeId.Mountain : BiomeId.Grassland;
        sbyte Z(int x, int y) => (sbyte)(y < 8 ? 40 : 5);
        var off = Map(24, Bio, Z);
        Run(off, new LandTransitionParams());
        Assert.Equal(5, off.Height_Z![off.Index(12, 8)]);
        var on = Map(24, Bio, Z);
        Run(on, new LandTransitionParams { EdgeZOffsets = true });
        Assert.Equal(5 + 8, on.Height_Z![on.Index(12, 8)]);   // Grassland>Mountain N lifts at most 8
        Assert.Equal(0x023B, on.LandId![on.Index(12, 8)]);     // and the N edge tile is drawn
    }
}
