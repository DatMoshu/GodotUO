using System.Collections.Concurrent;
using System.Security.Cryptography;
using CentrED.MapGen.Data;
using CentrED.MapGen.IR;
using CentrED.MapGen.Pipeline;
using CentrED.MapGen.Presets;
using CentrED.Network;

namespace CentrED.MapGen.Tests;

// Terrain invariants on the REAL data: the DragonMod brush table and the canonical tile
// tables, standard presets at 256-512. These are the properties the map review found
// broken (canyon rivers, dry sand under the water, shredded jungle, cliff tiles on
// mountain tops, egg-crate peaks).
public class TerrainInvariantTests
{
    private static readonly ConcurrentDictionary<string, GenIR> Cache = new();

    internal static GenIR Run(string preset, ushort size, bool roads = false)
    {
        return Cache.GetOrAdd($"{preset}|{size}|{roads}", _ =>
        {
            var p = MapGenPreset.Load(TestRepo.Path($"tools/mapgen/MapGen/presets/{preset}.preset.json"));
            var steps = DefaultPipeline.Build();
            p.ApplyTo(steps);
            PipelineFactory.ApplySideEffectGate(steps, allow: false, spawnOutputDir: null);
            foreach (var s in steps)
            {
                // Statics scatter needs tree data and is irrelevant here; roads only on request.
                if (s.Pass.Name is "Forest Scatter" or "Biome Static Scatter" or "Mountain Edge Statics"
                    or "POI Stamps" or "Road Stamps")
                    s.Enabled = false;
                if (!roads && s.Pass.Name is "Road Graph" or "Road Centerline" or "Town Sites" or "Town Roads")
                    s.Enabled = false;
            }
            var ir = new GenIR(size, size, new RectU16(0, 0, (ushort)(size - 1), (ushort)(size - 1)), unchecked((ulong)(p.Seed ?? 1234567)))
            {
                Tables = TileTables.LoadOrDefault(TestRepo.Path(TileTables.DefaultJsonRelativePath)),
                Brushes = LandBrushTable.LoadOrEmpty(TestRepo.BrushTable),
            };
            Assert.True(ir.Brushes.IsLoaded, "transitions.guo.json did not load");
            new PipelineRunner().Run(ir, steps);
            return ir;
        });
    }

    public static IEnumerable<object[]> StandardPresets() => new[]
    {
        new object[] { "island-512" }, new object[] { "continent" }, new object[] { "archipelago" },
        new object[] { "inland-lakes-with-roads" }, new object[] { "test-island" },
    };

    // Inland lakes come from deep noise basins; a 512 crop of that preset can miss every one.
    private static ushort SizeFor(string preset) => preset == "inland-lakes-with-roads" ? (ushort)1024 : (ushort)512;

    private static bool IsWaterBiome(byte b) => (BiomeId)b is BiomeId.DeepWater or BiomeId.ShallowWater or BiomeId.River;

    [Theory]
    [MemberData(nameof(StandardPresets))]
    public void NoLandAtOrBelowTheWaterPlane(string preset)
    {
        var ir = Run(preset, SizeFor(preset));
        int bad = 0, firstX = -1, firstY = -1;
        for (int i = 0; i < ir.TileCount; i++)
        {
            if (IsWaterBiome(ir.Biome![i]) || (BiomeId)ir.Biome[i] == BiomeId.Unassigned) continue;
            if (ir.Height_Z![i] > ir.OceanZ) continue;
            if (bad++ == 0) { firstX = i % ir.Width; firstY = i / ir.Width; }
        }
        Assert.True(bad == 0, $"{preset}: {bad} land cells at or below the water plane Z{ir.OceanZ} (first {firstX},{firstY})");
    }

    // The DragonMod tables map near-full water masks to the pure water tile 0xAA; painting
    // that on a beach cell left "water" standing on dry land at the wrong Z.
    [Theory]
    [MemberData(nameof(StandardPresets))]
    public void NoWaterTileOnALandCell(string preset)
    {
        var ir = Run(preset, SizeFor(preset));
        int bad = 0, first = -1;
        for (int i = 0; i < ir.TileCount; i++)
        {
            if (IsWaterBiome(ir.Biome![i]) || !TileFlags.IsWaterLandId(ir.LandId![i])) continue;
            if (bad++ == 0) first = i;
        }
        Assert.True(bad == 0, $"{preset}: {bad} land-biome cells carry a water tile (first {first % ir.Width},{first / ir.Width})");
    }

    // The edge band is cut in Biome Assign, before the coast: the band is ocean and the
    // cut has the usual shore (no plain grass/forest tile against water anywhere).
    [Theory]
    [InlineData("archipelago")]
    [InlineData("continent")]
    public void EdgeBandIsOcean_AndItsSeamHasEdgeTiles(string preset)
    {
        var ir = Run(preset, 512);
        int w = ir.Width, h = ir.Height;
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            if (!(x < 6 || y < 6 || x >= w - 6 || y >= h - 6)) continue;
            Assert.True(IsWaterBiome(ir.Biome![ir.Index(x, y)]), $"{preset}: land in the edge band at ({x},{y})");
        }
        var grass = new HashSet<ushort>(ir.Tables.Land[BiomeId.Grassland].Concat(ir.Tables.Land[BiomeId.Forest]));
        int bare = 0;
        for (int y = 1; y < h - 1; y++)
        for (int x = 1; x < w - 1; x++)
        {
            if (!grass.Contains(ir.LandId![ir.Index(x, y)])) continue;
            if (TileFlags.IsWaterLandId(ir.LandId[ir.Index(x + 1, y)]) || TileFlags.IsWaterLandId(ir.LandId[ir.Index(x - 1, y)])
                || TileFlags.IsWaterLandId(ir.LandId[ir.Index(x, y + 1)]) || TileFlags.IsWaterLandId(ir.LandId[ir.Index(x, y - 1)]))
                bare++;
        }
        Assert.True(bare == 0, $"{preset}: {bare} plain grass/forest tiles touch water");
    }

    // Swamp has brushes only against grass and forest; anything else is a hard edge.
    [Theory]
    [InlineData("continent")]
    [InlineData("jungle")]
    [InlineData("archipelago")]
    public void SwampOnlyBordersGrassOrForest(string preset)
    {
        var ir = Run(preset, 512);
        int w = ir.Width, h = ir.Height, bad = 0;
        string first = "";
        // Biome Assign cuts the edge band before the coast passes, so the map border is
        // checked too.
        const int band = 0;
        for (int y = band; y < h - band; y++)
        for (int x = band; x < w - band; x++)
        {
            if ((BiomeId)ir.Biome![ir.Index(x, y)] is not (BiomeId.Swamp or BiomeId.Wetland)) continue;
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int nx = x + dx, ny = y + dy;
                if ((dx == 0 && dy == 0) || nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                var nb = (BiomeId)ir.Biome[ir.Index(nx, ny)];
                if (nb is BiomeId.Swamp or BiomeId.Wetland or BiomeId.Grassland or BiomeId.Savanna
                    or BiomeId.Forest or BiomeId.DenseForest or BiomeId.Road) continue;
                if (bad++ == 0) first = $"({x},{y}) next to {nb}";
            }
        }
        Assert.True(bad == 0, $"{preset}: {bad} swamp edges against a class with no brush, first {first}");
    }

    [Theory]
    [MemberData(nameof(StandardPresets))]
    public void EveryWaterBodyIsFlat_AndTheDugShoreIsOneLevel(string preset)
    {
        var ir = Run(preset, SizeFor(preset));
        int w = ir.Width, h = ir.Height;
        var seen = new bool[ir.TileCount];
        var q = new Queue<int>();
        int bodies = 0;
        for (int i = 0; i < ir.TileCount; i++)
        {
            if (seen[i] || !TileFlags.IsWaterLandId(ir.LandId![i])) continue;
            bodies++;
            int z0 = ir.Height_Z![i];
            seen[i] = true;
            q.Enqueue(i);
            while (q.Count > 0)
            {
                int c = q.Dequeue();
                Assert.True(ir.Height_Z[c] == z0, $"{preset}: water body at ({i % w},{i / w}) has Z {z0} and {ir.Height_Z[c]} at ({c % w},{c / w})");
                int cx = c % w, cy = c / w;
                foreach (int n in new[] { cx > 0 ? c - 1 : -1, cx < w - 1 ? c + 1 : -1, cy > 0 ? c - w : -1, cy < h - 1 ? c + w : -1 })
                {
                    if (n < 0 || seen[n] || !TileFlags.IsWaterLandId(ir.LandId[n])) continue;
                    seen[n] = true;
                    q.Enqueue(n);
                }
            }
        }
        Assert.True(bodies > 0, $"{preset}: no water at all");
        for (int i = 0; i < ir.TileCount; i++)
        {
            ushort id = ir.LandId![i];
            if (id is >= 0x4C and <= 0x6F && IsWaterBiome(ir.Biome![i]))
                Assert.True(ir.Height_Z![i] == ir.ShoreDigZ, $"{preset}: dug cell ({i % w},{i / w}) at Z{ir.Height_Z[i]}, expected {ir.ShoreDigZ}");
        }
    }

    [Theory]
    [InlineData("continent", 512)]
    [InlineData("jungle", 512)]
    [InlineData("highlands", 512)]
    public void RiverBanksStepAtMostFour(string preset, ushort size)
    {
        var ir = Run(preset, size);
        int w = ir.Width, rivers = 0, bad = 0;
        string first = "";
        for (int i = 0; i < ir.TileCount; i++)
        {
            if ((BiomeId)ir.Biome![i] != BiomeId.River) continue;
            rivers++;
            int cx = i % w, cy = i / w;
            foreach (var (nx, ny) in new[] { (cx - 1, cy), (cx + 1, cy), (cx, cy - 1), (cx, cy + 1) })
            {
                if (nx < 0 || ny < 0 || nx >= w || ny >= ir.Height) continue;
                int n = ny * w + nx;
                if (IsWaterBiome(ir.Biome[n]) || TileFlags.IsWaterLandId(ir.LandId![n])) continue;
                int step = ir.Height_Z![n] - ir.Height_Z[i];
                if (step <= 4) continue;
                if (bad++ == 0) first = $"({nx},{ny}) Z{ir.Height_Z[n]} over water Z{ir.Height_Z[i]}";
            }
        }
        Assert.True(rivers > 0, $"{preset}: no river cells — the test would prove nothing");
        Assert.True(bad == 0, $"{preset}: {bad} river bank steps > 4, first {first}");
    }

    [Fact]
    public void JungleInteriorIsPreserved()
    {
        var ir = Run("jungle", 512);
        var interior = ir.Tables.Land[BiomeId.Jungle];
        int jungle = 0, kept = 0;
        for (int i = 0; i < ir.TileCount; i++)
        {
            if ((BiomeId)ir.Biome![i] != BiomeId.Jungle) continue;
            jungle++;
            if (Array.IndexOf(interior, ir.LandId![i]) >= 0) kept++;
        }
        Assert.True(jungle > 1000, $"jungle preset produced only {jungle} jungle cells");
        double pct = 100.0 * kept / jungle;
        Assert.True(pct > 80.0, $"only {pct:F1}% of jungle cells kept a jungle interior tile");
    }

    [Theory]
    [MemberData(nameof(StandardPresets))]
    public void NoLandBiomeAbove95Percent(string preset)
    {
        var ir = Run(preset, 256);
        var counts = new int[256];
        int land = 0;
        foreach (var b in ir.Biome!)
        {
            if (IsWaterBiome(b) || (BiomeId)b is BiomeId.Beach or BiomeId.Road or BiomeId.Unassigned) continue;
            counts[b]++;
            land++;
        }
        Assert.True(land > 0, $"{preset}: no land");
        int peak = Array.IndexOf(counts, counts.Max());
        double pct = 100.0 * counts[peak] / land;
        Assert.True(pct <= 95.0, $"{preset}: {(BiomeId)peak} covers {pct:F1}% of the land");
    }

    [Theory]
    [InlineData("continent")]
    [InlineData("highlands")]
    [InlineData("test-island")]
    public void MountainCellsUseMountainTiles(string preset)
    {
        var ir = Run(preset, 512);
        int mountain = 0, bad = 0;
        string first = "";
        for (int i = 0; i < ir.TileCount; i++)
        {
            if ((BiomeId)ir.Biome![i] is not (BiomeId.Mountain or BiomeId.HighMountain)) continue;
            mountain++;
            ushort id = ir.LandId![i];
            bool ok = id is >= 0x22C and <= 0x22F   // rock
                      or >= 0xDC and <= 0xDF;       // cliff face beside a trail/road
            if (ok) continue;
            if (bad++ == 0) first = $"0x{id:X} at ({i % ir.Width},{i / ir.Width})";
        }
        Assert.True(mountain > 0, $"{preset}: no mountains");
        Assert.True(bad == 0, $"{preset}: {bad} of {mountain} mountain cells use a non-mountain tile, first {first}");
    }

    [Theory]
    [MemberData(nameof(StandardPresets))]
    public void NoSingleTileSpikesOrPits(string preset)
    {
        var ir = Run(preset, 512);
        int w = ir.Width, h = ir.Height, spikes = 0;
        string first = "";
        for (int y = 1; y < h - 1; y++)
        for (int x = 1; x < w - 1; x++)
        {
            int i = y * w + x;
            if (IsWaterBiome(ir.Biome![i])) continue;
            int z = ir.Height_Z![i];
            int lo = int.MaxValue, hi = int.MinValue;
            foreach (int n in new[] { i - 1, i + 1, i - w, i + w })
            {
                lo = Math.Min(lo, ir.Height_Z[n]);
                hi = Math.Max(hi, ir.Height_Z[n]);
            }
            // A spike rises (a pit drops) more than 8 above (below) ALL four neighbours.
            if (z - hi > 8 || lo - z > 8)
            {
                if (spikes++ == 0) first = $"({x},{y}) Z{z} vs neighbours {lo}..{hi}";
            }
        }
        Assert.True(spikes == 0, $"{preset}: {spikes} single-tile spikes/pits, first {first}");
    }

    [Fact]
    public void SameSeed_SameLand_WithRealBrushes()
    {
        var a = Run("continent", 256);
        Cache.TryRemove("continent|256|False", out _);
        var b = Run("continent", 256);
        Assert.NotSame(a, b);
        Assert.Equal(Digest(a.LandId!), Digest(b.LandId!));
        Assert.Equal(Digest(a.Height_Z!), Digest(b.Height_Z!));
        Assert.Equal(Digest(a.Biome!), Digest(b.Biome!));
        Assert.Equal(a.StaticOps.Count, b.StaticOps.Count);
    }

    [Fact]
    public void RoadsAreAtLeastTwoWide_AndUseRealTiles()
    {
        var ir = Run("inland-lakes-with-roads", 512, roads: true);
        int road = 0, invalid = 0, lonely = 0;
        int w = ir.Width;
        for (int i = 0; i < ir.TileCount; i++)
        {
            if ((BiomeId)ir.Biome![i] != BiomeId.Road) continue;
            road++;
            ushort id = ir.LandId![i];
            if (id == 0x518) invalid++;
            int x = i % w, y = i / w;
            bool wide = false;
            foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                int nx = x + dx, ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= w || ny >= ir.Height) continue;
                if ((BiomeId)ir.Biome[ny * w + nx] == BiomeId.Road) { wide = true; break; }
            }
            if (!wide) lonely++;
        }
        Assert.True(road > 0, "no road cells");
        Assert.Equal(0, invalid);
        Assert.True(lonely * 100 < road, $"{lonely} of {road} road cells have no 4-connected road neighbour");
    }

    private static string Digest<T>(T[] arr) where T : struct
    {
        var bytes = new byte[arr.Length * System.Runtime.InteropServices.Marshal.SizeOf<T>()];
        Buffer.BlockCopy(arr, 0, bytes, 0, bytes.Length);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}

public class TileTableSourceTests
{
    [Fact]
    public void DefaultJson_MatchesBuiltInLandTable()
    {
        var json = TileTables.LoadOrDefault(TestRepo.Path(TileTables.DefaultJsonRelativePath));
        Assert.NotSame(TileTables.BuiltIn, json);
        foreach (var (biome, ids) in TileTables.BuiltIn.Land)
        {
            Assert.True(json.Land.TryGetValue(biome, out var fromJson), $"JSON has no land entry for {biome}");
            Assert.Equal(ids, fromJson);
        }
        Assert.Equal(TileTables.BuiltIn.Land.Count, json.Land.Count);
    }

    [Fact]
    public void MountainsAreRock_ForestIsEven_NoCliffTilesAsInterior()
    {
        var t = TileTables.LoadOrDefault(TestRepo.Path(TileTables.DefaultJsonRelativePath));
        Assert.Equal(new ushort[] { 0x22C, 0x22D, 0x22E, 0x22F }, t.Land[BiomeId.Mountain]);
        Assert.Equal(new ushort[] { 0xC4, 0xC5, 0xC6, 0xC7 }, t.Land[BiomeId.Forest]);
        foreach (var ids in t.Land.Values)
            Assert.DoesNotContain(ids, id => id is >= 0xDC and <= 0xDF);
    }

    [Fact]
    public void BrushTable_LoadsAndCoversTheAllowListedPairs()
    {
        var b = LandBrushTable.LoadOrEmpty(TestRepo.BrushTable);
        Assert.True(b.IsLoaded);
        Assert.Equal("guo", b.Source);
        foreach (var (self, other) in new[] { ("Grassland", "Beach"), ("Grassland", "Mountain"), ("Grassland", "Forest"),
                     ("Grassland", "Jungle"), ("Grassland", "Swamp"), ("Forest", "Mountain"), ("Beach", "Jungle"), ("Beach", "Mountain"),
                     ("Snow", "Mountain"), ("Grassland", "Dirt"), ("Forest", "Dirt"), ("Snow", "Dirt"), ("Dirt", "Mountain") })
        {
            Assert.True(b.HasPair(self, other), $"{self}->{other} missing");
            // Every edge shape has a tile.
            foreach (var (shape, dir) in EdgeShapes.All)
                Assert.True(b.PickTransitionTile(self, other, dir, 0u) != 0, $"{self}->{other} has no {shape} tile");
        }
        // The shores and the pairs Britannia never draws directly go through a bridge material.
        Assert.Equal("Beach", b.Via["Grassland>Water"]);
        Assert.Equal("Grassland", b.Via["Forest>Beach"]);
        Assert.Equal("Beach", b.Via["Jungle>Water"]);
        Assert.Contains("Beach>Water", b.Plain);
    }

    [Fact]
    public void Preset_UnknownKeysAreReported_NotSilentlyDropped()
    {
        var path = Path.Combine(Path.GetTempPath(), "mapgen-preset-" + Guid.NewGuid().ToString("N") + ".preset.json");
        File.WriteAllText(path, """
            { "name": "t", "passes": { "Biome Assign": { "SeaLevelZ": 7, "NoSuchKey": 1 }, "No Such Pass": {} },
              "disable_passes": ["Moisture Climate"] }
            """);
        try
        {
            var preset = MapGenPreset.Load(path);
            var steps = DefaultPipeline.Build();
            preset.ApplyTo(steps);
            Assert.Contains(preset.Warnings, w => w.Contains("NoSuchKey"));
            Assert.Contains(preset.Warnings, w => w.Contains("No Such Pass"));
            Assert.Contains(preset.Warnings, w => w.Contains("Moisture Climate"));
            // One sea level: the pre-rebase passes follow Biome Assign.
            var climate = steps.Single(s => s.Pass.Name == "Moisture & Climate").Parameters;
            Assert.Equal(7, climate.GetType().GetProperty("SeaLevelZ")!.GetValue(climate));
        }
        finally { File.Delete(path); }
    }
}
