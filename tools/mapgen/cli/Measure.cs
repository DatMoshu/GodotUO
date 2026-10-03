using System.Security.Cryptography;
using System.Text.Json;
using CentrED.MapGen.Commit;
using CentrED.MapGen.Data;
using CentrED.MapGen.IR;
using CentrED.MapGen.Pipeline;

namespace GuoMapGen;

/// <summary>
/// <c>prepare --measure</c>: measures the transitions of the user's own Felucca (map0 in
/// UO_CLIENT_DATA) and writes the results into the generator data folder, never into the repo
/// (rule 8). Two files, under <c>UO_MAPGEN_DATA/map-mining/</c>:
/// <list type="bullet">
/// <item><c>guo-transition-atlas.json</c>: per biome pair and neighbour mask, the land tiles Felucca
/// uses and how often (the measured-atlas format Land Transitions overlays on the GUO table).</item>
/// <item><c>guo-transition-measure.json</c>: per pair, the counts per edge shape, the most used tiles
/// per shape, and the height of edge cells relative to each side; per material, its interior tile
/// frequencies.</item>
/// </list>
/// <see cref="Resolve"/> then merges the summary into GUO's transition table and writes the user's
/// resolved table, <c>UO_MAPGEN_DATA/transitions.guo.resolved.json</c>, which the generator prefers.
/// A cell is interior when its tile is in a material's interior pool (the tile tables, the road
/// pools, the water ids). Any other cell whose 8 neighbours hold interior tiles of exactly two
/// materials is an edge sample of the pair, counted from the owner's side as GUO's table orders it.
/// </summary>
public static class MeasureCommand
{
    // The pairs the generator asks for, owner first: Land Transitions' list, then the road edges.
    public static readonly (string Owner, string Other)[] Pairs =
    {
        ("Grassland", "Beach"), ("Grassland", "Mountain"), ("Grassland", "Forest"), ("Grassland", "Jungle"),
        ("Grassland", "Snow"), ("Grassland", "Swamp"), ("Grassland", "Water"),
        ("Forest", "Beach"), ("Forest", "Mountain"), ("Forest", "Jungle"), ("Forest", "Swamp"), ("Forest", "Water"),
        ("Beach", "Water"), ("Beach", "Jungle"), ("Beach", "Mountain"),
        ("Snow", "Mountain"), ("Snow", "Water"),
        ("Jungle", "Water"),
        ("Grassland", "Dirt"), ("Forest", "Dirt"), ("Jungle", "Dirt"), ("Snow", "Dirt"), ("Beach", "Dirt"),
        ("Dirt", "Mountain"),
        ("Grassland", "Cobble"), ("Beach", "Cobble"), ("Dirt", "Cobble"),
    };

    public static readonly string[] Materials = { "Grassland", "Forest", "Jungle", "Beach", "Snow", "Mountain", "Swamp", "Water", "Dirt", "Cobble" };

    private static readonly (int dx, int dy, byte bit)[] Dirs =
    {
        (0, -1, EdgeShapes.N), (1, -1, EdgeShapes.NE), (1, 0, EdgeShapes.E), (1, 1, EdgeShapes.SE),
        (0, 1, EdgeShapes.S), (-1, 1, EdgeShapes.SW), (-1, 0, EdgeShapes.W), (-1, -1, EdgeShapes.NW),
    };

    // The road pools RoadPaint paints with (internal there): Felucca dirt and cobblestones.
    private static readonly ushort[] DirtTiles = { 0x71, 0x72, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78 };
    private static readonly ushort[] CobbleTiles = { 0x3E9, 0x3EA, 0x3EB, 0x3EC };

    /// <summary>
    /// Merges a measure summary into the committed GUO table (<see cref="GuoTransitionTable.ResolveWith"/>) and
    /// writes the resolved table to the data folder.
    /// </summary>
    public static Dictionary<string, object?> Resolve(string summaryPath)
    {
        var table = GuoTransitionTable.Load(RepoRootResolver.Resolve(GuoTransitionTable.RelativePath));
        var summary = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(summaryPath))!.AsObject();
        var interior = InteriorMaterials();
        var (resolved, added) = table.ResolveWith(summary, id => id < interior.Length && interior[id] != 0);
        string output = RepoRootResolver.Resolve(GuoTransitionTable.ResolvedRelativePath);
        resolved.Write(output);
        return new Dictionary<string, object?>
        {
            ["ok"] = true, ["output"] = Path.GetFullPath(output), ["added_variants"] = added,
            ["pairs_with_tiles"] = resolved.Pairs.Count(p => p.Edges.Count > 0),
        };
    }

    /// <summary>The interior pool of every material: material index per land id (0 = not interior).</summary>
    public static byte[] InteriorMaterials()
    {
        var m = new byte[0x4000];
        void Set(string material, IEnumerable<ushort> ids)
        {
            byte v = (byte)(Array.IndexOf(Materials, material) + 1);
            foreach (var id in ids) if (id < m.Length) m[id] = v;
        }
        var land = TileTables.BuiltIn.Land;
        Set("Grassland", land[BiomeId.Grassland]);
        Set("Forest", land[BiomeId.Forest]);
        Set("Jungle", land[BiomeId.Jungle]);
        Set("Beach", land[BiomeId.Beach]);
        Set("Snow", land[BiomeId.Snow]);
        Set("Mountain", land[BiomeId.Mountain]);
        Set("Swamp", land[BiomeId.Swamp]);
        Set("Water", Enumerable.Range(0, 0x4000).Select(i => (ushort)i).Where(TileFlags.IsWaterLandId));
        Set("Dirt", DirtTiles);
        Set("Cobble", CobbleTiles);
        return m;
    }

    private sealed class PairStats
    {
        public long Samples;
        public readonly Dictionary<byte, Dictionary<ushort, int>> Masks = new();
        public readonly Dictionary<string, Dictionary<ushort, int>> Shapes = new();
        public readonly Dictionary<string, (long Sum, long N)> DzOwner = new(), DzOther = new();
        public int Slivers;
    }

    public static Dictionary<string, object?> Run(string clientData, int regionWidth)
    {
        var src = PipelineFactory.ResolveMapSource(clientData);
        if (src.MapPath is null) throw new CliError($"no map0.mul or map0LegacyMUL.uop in UO_CLIENT_DATA ({clientData})");
        const int bw = 896, bh = 512, w = bw * 8, h = bh * 8;
        byte[] map = src.MapPath.EndsWith(".uop", StringComparison.OrdinalIgnoreCase)
            ? UopMapExtractor.ExtractMap(src.MapPath, bw, bh)
            : File.ReadAllBytes(src.MapPath);
        if (map.Length < (long)bw * bh * 196) throw new CliError($"{Path.GetFileName(src.MapPath)} is smaller than a 7168x4096 Felucca");

        int rw = Math.Clamp(regionWidth, 8, w);
        var id = new ushort[rw * h];
        var z = new sbyte[rw * h];
        for (int bx = 0; bx < rw / 8; bx++)
        for (int by = 0; by < bh; by++)
        {
            int off = (bx * bh + by) * 196 + 4;
            for (int c = 0; c < 64; c++)
            {
                int x = bx * 8 + (c % 8), y = by * 8 + (c / 8);
                id[y * rw + x] = BitConverter.ToUInt16(map, off + c * 3);
                z[y * rw + x] = (sbyte)map[off + c * 3 + 2];
            }
        }

        var interior = InteriorMaterials();
        byte Mat(int i) => id[i] < interior.Length ? interior[id[i]] : (byte)0;
        var pairIndex = new Dictionary<(byte, byte), int>();
        for (int p = 0; p < Pairs.Length; p++)
            pairIndex[((byte)(Array.IndexOf(Materials, Pairs[p].Owner) + 1), (byte)(Array.IndexOf(Materials, Pairs[p].Other) + 1))] = p;
        var stats = Pairs.Select(_ => new PairStats()).ToArray();
        var interiorCounts = Materials.Select(_ => new Dictionary<ushort, int>()).ToArray();

        Span<byte> nb = stackalloc byte[8];
        for (int y = 1; y < h - 1; y++)
        for (int x = 1; x < rw - 1; x++)
        {
            int i = y * rw + x;
            byte self = Mat(i);
            if (self != 0)
            {
                var ic = interiorCounts[self - 1];
                ic[id[i]] = ic.GetValueOrDefault(id[i]) + 1;
                continue;
            }
            byte a = 0, b = 0;
            bool mixed = false;
            for (int d = 0; d < 8; d++)
            {
                byte m = Mat((y + Dirs[d].dy) * rw + x + Dirs[d].dx);
                nb[d] = m;
                if (m == 0) continue;
                if (a == 0 || a == m) a = m;
                else if (b == 0 || b == m) b = m;
                else mixed = true;
            }
            if (mixed || a == 0 || b == 0) continue;
            // Count the sample for whichever order is a GUO pair.
            foreach (var (owner, other) in new[] { (a, b), (b, a) })
            {
                if (!pairIndex.TryGetValue((owner, other), out int p)) continue;
                byte mask = 0;
                long sumOwn = 0, nOwn = 0, sumOther = 0, nOther = 0;
                for (int d = 0; d < 8; d++)
                {
                    int j = (y + Dirs[d].dy) * rw + x + Dirs[d].dx;
                    if (nb[d] == other) { mask |= Dirs[d].bit; sumOther += z[j]; nOther++; }
                    else if (nb[d] == owner) { sumOwn += z[j]; nOwn++; }
                }
                var s = stats[p];
                s.Samples++;
                if (!s.Masks.TryGetValue(mask, out var tiles)) s.Masks[mask] = tiles = new();
                tiles[id[i]] = tiles.GetValueOrDefault(id[i]) + 1;
                string? shape = EdgeShapes.ShapeOf(mask);
                if (shape is null) { s.Slivers++; continue; }
                if (!s.Shapes.TryGetValue(shape, out var st)) s.Shapes[shape] = st = new();
                st[id[i]] = st.GetValueOrDefault(id[i]) + 1;
                // Height relative to each side, in z units x 100 (whole-number sums).
                if (nOwn > 0) { var (su, n) = s.DzOwner.GetValueOrDefault(shape); s.DzOwner[shape] = (su + (long)Math.Round(100.0 * (z[i] - (double)sumOwn / nOwn)), n + 1); }
                if (nOther > 0) { var (su, n) = s.DzOther.GetValueOrDefault(shape); s.DzOther[shape] = (su + (long)Math.Round(100.0 * (z[i] - (double)sumOther / nOther)), n + 1); }
            }
        }

        string outDir = RepoRootResolver.Resolve("Data/map-mining/");
        Directory.CreateDirectory(outDir);
        var source = new Dictionary<string, object?>
        {
            ["map"] = Path.GetFileName(src.MapPath),
            ["bytes"] = new FileInfo(src.MapPath).Length,
            ["sha256"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(src.MapPath))).ToLowerInvariant(),
            ["region"] = new[] { 0, 0, rw, h },
        };

        // The measured atlas (MeasuredTransitionTable's format, schema 1).
        var atlasPairs = new Dictionary<string, object?>();
        for (int p = 0; p < Pairs.Length; p++)
        {
            var s = stats[p];
            if (s.Samples == 0) continue;
            atlasPairs[$"{Pairs[p].Owner}>{Pairs[p].Other}"] = new Dictionary<string, object?>
            {
                ["samples"] = s.Samples,
                ["rules"] = s.Masks.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key.ToString(),
                    kv => kv.Value.OrderByDescending(t => t.Value).ThenBy(t => t.Key).Select(t => new[] { (int)t.Key, t.Value }).ToArray()),
            };
        }
        var atlas = new Dictionary<string, object?>
        {
            ["schema"] = 1,
            ["generator"] = "guo-mapgen prepare --measure",
            ["source"] = source,
            ["method"] = "Edge samples: cells whose tile is not in an interior pool and whose 8 neighbours hold interior tiles of exactly two materials. The mask is the neighbours of the other material.",
            ["pairs"] = atlasPairs,
        };
        string atlasPath = Path.Combine(outDir, "guo-transition-atlas.json");
        File.WriteAllText(atlasPath, JsonSerializer.Serialize(atlas));

        // The summary: shapes, heights, interiors.
        var measurePairs = new Dictionary<string, object?>();
        for (int p = 0; p < Pairs.Length; p++)
        {
            var s = stats[p];
            measurePairs[$"{Pairs[p].Owner}>{Pairs[p].Other}"] = new Dictionary<string, object?>
            {
                ["samples"] = s.Samples,
                ["slivers"] = s.Slivers,
                ["shapes"] = EdgeShapes.All.Where(sh => s.Shapes.ContainsKey(sh.Name)).ToDictionary(sh => sh.Name, sh =>
                {
                    var tiles = s.Shapes[sh.Name];
                    var (so, no) = s.DzOwner.GetValueOrDefault(sh.Name);
                    var (sx, nx) = s.DzOther.GetValueOrDefault(sh.Name);
                    return (object?)new Dictionary<string, object?>
                    {
                        ["count"] = tiles.Values.Sum(),
                        ["top"] = tiles.OrderByDescending(t => t.Value).ThenBy(t => t.Key).Take(8).Select(t => new object[] { $"0x{t.Key:X4}", t.Value }).ToArray(),
                        ["dz_owner"] = no > 0 ? Math.Round(so / 100.0 / no, 2) : null,
                        ["dz_other"] = nx > 0 ? Math.Round(sx / 100.0 / nx, 2) : null,
                    };
                }),
            };
        }
        var measure = new Dictionary<string, object?>
        {
            ["schema"] = "guo.mapgen.transition-measure/1",
            ["source"] = source,
            ["pairs"] = measurePairs,
            ["interiors"] = Materials.Select((m, k) => (m, k)).ToDictionary(t => t.m, t => (object?)interiorCounts[t.k]
                .OrderByDescending(v => v.Value).Select(v => new object[] { $"0x{v.Key:X4}", v.Value }).ToArray()),
        };
        string measurePath = Path.Combine(outDir, "guo-transition-measure.json");
        File.WriteAllText(measurePath, JsonSerializer.Serialize(measure, new JsonSerializerOptions { WriteIndented = true }));

        return new Dictionary<string, object?>
        {
            ["ok"] = atlasPairs.Count > 0,
            ["atlas"] = Path.GetFullPath(atlasPath),
            ["summary"] = Path.GetFullPath(measurePath),
            ["pairs"] = atlasPairs.Count,
            ["samples"] = stats.Sum(s => s.Samples),
            ["region"] = new[] { 0, 0, rw, h },
        };
    }
}
