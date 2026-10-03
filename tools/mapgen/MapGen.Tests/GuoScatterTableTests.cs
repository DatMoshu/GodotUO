using System.Text.Json.Nodes;
using CentrED.MapGen.Data;
using CentrED.MapGen.IR;
using CentrED.MapGen.Passes.Scatter;
using CentrED.MapGen.Pipeline;
using CentrED.Network;

namespace CentrED.MapGen.Tests;

// GUO's own scatter table (scatter.guo.json, docs/data_formats.md §26, "Scatter table"): the committed table's
// integrity, the loader, the catalogue choice and what Biome Static Scatter and Forest Scatter make of it.
public class GuoScatterTableTests
{
    private static GuoScatterTable Committed() => GuoScatterTable.Load(TestRepo.Path(GuoScatterTable.RelativePath));

    private static GuoScatterTable FromJson(string biomes, string extra = "") =>
        GuoScatterTable.Parse(JsonNode.Parse($$$"""{"format":"guo.mapgen.scatter/1"{{{extra}}},"biomes":{{{{biomes}}}}}""")!.AsObject());

    private static GenIR World(BiomeId biome, ushort size = 64)
    {
        var ir = new GenIR(size, size, new RectU16(0, 0, (ushort)(size - 1), (ushort)(size - 1)), 42);
        ir.EnsureHeight(); ir.EnsureBiome(); ir.EnsureLandId(); ir.EnsureSlope();
        Array.Fill(ir.Biome!, (byte)biome);
        Array.Fill(ir.Height_Z!, (sbyte)10);
        Array.Fill(ir.LandId!, (ushort)3);
        return ir;
    }

    private static GenContext Context(GenIR ir) => new() { IR = ir, Rng = new Random(1), Report = new PassReport { PassName = "test" } };

    [Fact]
    public void CommittedTable_IsWellFormed()
    {
        var t = Committed();
        foreach (var b in new[] { BiomeId.Grassland, BiomeId.Forest, BiomeId.Jungle, BiomeId.Beach, BiomeId.Desert, BiomeId.Swamp })
        {
            Assert.True(t.Biomes[b].Chance > 0, $"{b} scatters nothing");
            Assert.False(string.IsNullOrWhiteSpace(t.Biomes[b].Notes), $"{b} has no notes");
        }
        // Trees belong to Forest Scatter: no ground-cover group names a tree, and no trunk or canopy is scattered.
        var treeIds = t.Trees.SelectMany(p => new[] { p.Trunk, p.Leaves }).ToHashSet();
        foreach (var (id, b) in t.Biomes)
            foreach (var g in b.Groups)
            {
                Assert.DoesNotContain("tree", g.Name, StringComparison.OrdinalIgnoreCase);
                Assert.All(g.Sets.SelectMany(s => s), tile => Assert.DoesNotContain(tile.TileID, treeIds));
            }
        Assert.Equal(14, t.Trees.Count);
        Assert.Equal(t.Trees.Count, t.Trees.Select(p => p.Leaves).Distinct().Count());
    }

    [Fact]
    public void StaticsTable_SplitsGroupsOverVariantsAndFillsAliases()
    {
        var table = Committed().ToStaticsTable();
        foreach (var alias in new[] { BiomeId.Savanna, BiomeId.DenseForest, BiomeId.Wetland, BiomeId.Tundra, BiomeId.HighMountain })
            Assert.True(table.ByBiome.ContainsKey(alias), $"{alias} has no catalogue");
        Assert.Same(table.ByBiome[BiomeId.Forest].Groups, table.ByBiome[BiomeId.DenseForest].Groups);

        // A group's share is its freq, however many variants it has: grass tufts (4 ids, 32) outweigh ferns (6 ids, 5).
        var grass = table.ByBiome[BiomeId.Grassland];
        int Share(string name) => grass.Groups.Where(g => g.Description == name).Sum(g => g.Freq);
        Assert.Equal(32.0 / 5, (double)Share("grass tufts") / Share("ferns"), 1);
        Assert.Equal(grass.Groups.Sum(g => g.Freq), grass.TotalFreq);
    }

    [Fact]
    public void TreeStatics_PairsEveryTrunkWithItsCanopy()
    {
        var trees = Committed().ToTreeStatics();
        Assert.Equal((ushort)0x0CCE, trees.TrunkToLeaf[0x0CCD]);
        Assert.True(trees.IsTrunkOnly(0x0CF8));
        Assert.True(trees.IsLeafOverlay(0x0CF9));
    }

    [Theory]
    [InlineData("""{"format":"guo.mapgen.scatter/0","biomes":{}}""", "format")]
    [InlineData("""{"format":"guo.mapgen.scatter/1","biomes":{"Moon":{"chance":1,"groups":[]}}}""", "unknown biome")]
    [InlineData("""{"format":"guo.mapgen.scatter/1","biomes":{"Forest":{"chance":101,"groups":[]}}}""", "chance")]
    [InlineData("""{"format":"guo.mapgen.scatter/1","biomes":{"Forest":{"chance":5,"groups":[{"name":"x","freq":0,"any":["0x0CAF"]}]}}}""", "freq")]
    [InlineData("""{"format":"guo.mapgen.scatter/1","biomes":{"Forest":{"chance":5,"groups":[{"name":"x","freq":1}]}}}""", "no 'any'")]
    [InlineData("""{"format":"guo.mapgen.scatter/1","biomes":{"Forest":{"chance":5,"groups":[{"name":"x","freq":1,"any":["CAF"]}]}}}""", "static id")]
    [InlineData("""{"format":"guo.mapgen.scatter/1","biomes":{"Forest":{"chance":5,"groups":[{"name":"x","freq":1,"sets":[[["0x0CAF",9,0,0]]]}]}}}""", "out of range")]
    [InlineData("""{"format":"guo.mapgen.scatter/1","aliases":{"Savanna":"Grassland"},"biomes":{"Forest":{"chance":5,"groups":[{"name":"x","freq":1,"any":["0x0CAF"]}]}}}""", "alias")]
    [InlineData("""{"format":"guo.mapgen.scatter/1","trees":[{"trunk":"0x0CCD","leaves":"0x0CCD"}],"biomes":{}}""", "own canopy")]
    public void Parse_RejectsBadTables(string json, string message)
    {
        var e = Assert.Throws<InvalidDataException>(() => GuoScatterTable.Parse(JsonNode.Parse(json)!.AsObject()));
        Assert.Contains(message, e.Message);
    }

    [Fact]
    public void Sets_KeepOffsetsAndHue()
    {
        var t = FromJson("""
            "Beach":{"chance":4,"groups":[{"name":"bloom","freq":2,"sets":[[["0x0D2A",0,0,0],["0x0D2B",1,-1,3,33]]]}]}
            """);
        var tiles = t.Biomes[BiomeId.Beach].Groups[0].Sets[0];
        Assert.Equal(2, tiles.Count);
        Assert.Equal((0x0D2B, 1, -1, 3, 33), (tiles[1].TileID, tiles[1].Xoff, tiles[1].Yoff, tiles[1].Zoff, tiles[1].Hue));
    }

    [Fact]
    public void Catalogue_DefaultsToGuo_FallsBackWhenMissing_ReadsLandscaperFolders()
    {
        var notes = new List<string>();
        var guo = BiomeStaticScatterPass.LoadCatalogue("guo", notes, out string source);
        Assert.Equal("GUO's scatter table", source);
        Assert.Equal(Committed().Biomes[BiomeId.Forest].Chance, guo.ByBiome[BiomeId.Forest].ChancePercent);
        Assert.Empty(notes);

        var missing = BiomeStaticScatterPass.LoadCatalogue("Data/map-mining/no-such-atlas", notes, out source);
        Assert.Equal("GUO's scatter table", source);
        Assert.Contains(notes, n => n.Contains("not found"));
        Assert.Equal(guo.ByBiome.Count, missing.ByBiome.Count);

        string dir = Path.Combine(Path.GetTempPath(), "scatter-norad-" + Guid.NewGuid()); Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "Forest.xml"), """<RandomStatics Chance="15"><Statics Description="Rock" Freq="10"><Static TileID="6004" X="0" Y="0" Z="0" Hue="0"/></Statics></RandomStatics>""");
            var norad = BiomeStaticScatterPass.LoadCatalogue(dir, notes, out source);
            Assert.Equal(dir, source);
            Assert.Equal(15, norad.ByBiome[BiomeId.Forest].ChancePercent);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData(BiomeId.Forest)]
    [InlineData(BiomeId.Jungle)]
    [InlineData(BiomeId.Swamp)]
    [InlineData(BiomeId.Savanna)]
    public void Scatter_PlacesOnlyTheBiomesOwnGroundCover(BiomeId biome)
    {
        var table = Committed();
        var canonical = table.Aliases.TryGetValue(biome, out var target) ? target : biome;
        var allowed = table.Biomes[canonical].Groups.SelectMany(g => g.Sets).SelectMany(s => s).Select(t => t.TileID).ToHashSet();
        var ir = World(biome);
        ir.Trees = table.ToTreeStatics();
        new BiomeStaticScatterPass().Run(Context(ir), new BiomeStaticScatterParams { DenseBiomesCsv = biome.ToString(), GroundCoverOnly = true });
        Assert.NotEmpty(ir.StaticOps);
        Assert.All(ir.StaticOps, o => Assert.Contains(o.Id, allowed));
        // Dense biomes roll per cell: the share placed is near the table's chance.
        double share = (double)ir.StaticOps.Select(o => (o.X, o.Y)).Distinct().Count() / ir.TileCount;
        Assert.InRange(share, table.Biomes[canonical].Chance / 100.0 * 0.5, table.Biomes[canonical].Chance / 100.0 * 1.5);
    }

    [Fact]
    public void ForestScatter_GivesEveryTrunkItsCanopy_FromTheGuoTable()
    {
        var ir = World(BiomeId.Forest);
        Array.Fill(ir.LandId!, (ushort)0xC4);
        ir.Trees = Committed().ToTreeStatics();
        ir.Tables = new TileTables { ForestSpecies = new() { [BiomeId.Forest] = new ushort[] { 0x0CDA, 0x0CE6 } }, ForestDensity = new() { [BiomeId.Forest] = 1 } };
        new ForestScatterPass().Run(Context(ir), new ForestScatterParams { MinRadius = 3, ShoreBuffer = 0 });
        Assert.NotEmpty(ir.StaticOps);
        foreach (var group in ir.StaticOps.GroupBy(o => (o.X, o.Y, o.Z)))
        {
            var ids = group.Select(o => o.Id).OrderBy(i => i).ToArray();
            Assert.True(ids.SequenceEqual(new ushort[] { 0x0CDA, 0x0CDB }) || ids.SequenceEqual(new ushort[] { 0x0CE6, 0x0CE7 }), string.Join(",", ids));
        }
    }
}
