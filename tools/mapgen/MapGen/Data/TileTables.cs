using System.Text.Json;
using CentrED.MapGen.IR;

namespace CentrED.MapGen.Data;

// Loads tile-id tables for biome land, beach, water, river, road, and forest species
// from a single JSON document. Falls back to baked-in defaults if the file is missing
// so the pipeline always runs in a clean checkout.
public sealed class TileTables
{
    public Dictionary<BiomeId, ushort[]> Land { get; init; } = new();
    public ushort[] Beach { get; init; } = Array.Empty<ushort>();
    public ushort[] Water { get; init; } = Array.Empty<ushort>();
    public ushort[] River { get; init; } = Array.Empty<ushort>();
    public ushort[] Road  { get; init; } = Array.Empty<ushort>();

    // Shoreline transition tiles, keyed by 4-bit water-neighbour bitmask
    // bit 0 = N (water above), bit 1 = E, bit 2 = S, bit 3 = W. Index 0 = inland (no water neighbour).
    public ushort[][] Shorelines { get; init; } = new ushort[16][];

    public Dictionary<BiomeId, ushort[]> ForestSpecies { get; init; } = new();
    public Dictionary<BiomeId, double>   ForestDensity { get; init; } = new();
    public ushort ControlPointStaticId { get; set; } = 0x0ED4;    // stone pillar
    public ushort DungeonEntranceStaticId { get; set; } = 0x1AF1; // cave hole

    // Repo-relative location of the canonical tile-table JSON. The JSON is the single source
    // of truth; BuildDefault() mirrors it exactly (TileTablesTests asserts the two agree) so a
    // checkout without the JSON still generates the same map.
    public const string DefaultJsonRelativePath = "tools/mapgen/MapGen/presets/tile-tables.default.json";

    private static TileTables? _default;
    private static readonly object DefaultLock = new();

    /// <summary>
    /// The canonical tables: tile-tables.default.json resolved via <see cref="RepoRootResolver"/>,
    /// falling back to the identical built-in copy when the file is not reachable.
    /// </summary>
    public static TileTables Default
    {
        get
        {
            if (_default is not null) return _default;
            lock (DefaultLock) return _default ??= LoadDefault();
        }
    }

    /// <summary>Built-in copy of tile-tables.default.json (no file access).</summary>
    public static TileTables BuiltIn { get; } = BuildDefault();

    /// <summary>Loads the canonical JSON (repo-relative); built-in tables when it is missing or invalid.</summary>
    public static TileTables LoadDefault()
    {
        string path = RepoRootResolver.Resolve(DefaultJsonRelativePath);
        if (!File.Exists(path)) return BuiltIn;
        return TryLoad(path) ?? BuiltIn;
    }

    public static TileTables LoadOrDefault(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return Default;
        return TryLoad(path) ?? Default;
    }

    private static TileTables? TryLoad(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var doc = JsonDocument.Parse(fs);
            return Parse(doc.RootElement);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"WARNING: TileTables load failed for '{path}': {ex.Message}");
            return null;
        }
    }

    private static TileTables Parse(JsonElement root)
    {
        var t = new TileTables
        {
            Beach = ReadArray(root, "beach"),
            Water = ReadArray(root, "water"),
            River = ReadArray(root, "river"),
            Road  = ReadArray(root, "road"),
        };
        if (root.TryGetProperty("land", out var landEl) && landEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var kv in landEl.EnumerateObject())
                if (Enum.TryParse<BiomeId>(kv.Name, true, out var biome))
                    t.Land[biome] = ReadArray(kv.Value);
        }
        if (root.TryGetProperty("shorelines", out var shEl) && shEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var kv in shEl.EnumerateObject())
            {
                if (int.TryParse(kv.Name, System.Globalization.NumberStyles.Integer | System.Globalization.NumberStyles.HexNumber, null, out var idx) && idx >= 0 && idx < 16)
                    t.Shorelines[idx] = ReadArray(kv.Value);
            }
        }
        if (root.TryGetProperty("control_point_static", out var cpEl) && cpEl.ValueKind == JsonValueKind.Number)
            t.ControlPointStaticId = (ushort)cpEl.GetInt32();
        if (root.TryGetProperty("dungeon_entrance_static", out var deEl) && deEl.ValueKind == JsonValueKind.Number)
            t.DungeonEntranceStaticId = (ushort)deEl.GetInt32();
        if (root.TryGetProperty("forest_species", out var fsEl) && fsEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var kv in fsEl.EnumerateObject())
                if (Enum.TryParse<BiomeId>(kv.Name, true, out var biome))
                    t.ForestSpecies[biome] = ReadArray(kv.Value);
        }
        if (root.TryGetProperty("forest_density", out var fdEl) && fdEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var kv in fdEl.EnumerateObject())
                if (Enum.TryParse<BiomeId>(kv.Name, true, out var biome) &&
                    kv.Value.ValueKind == JsonValueKind.Number)
                    t.ForestDensity[biome] = kv.Value.GetDouble();
        }
        return t;
    }

    private static ushort[] ReadArray(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var el)) return Array.Empty<ushort>();
        return ReadArray(el);
    }

    private static ushort[] ReadArray(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Array) return Array.Empty<ushort>();
        var list = new List<ushort>();
        foreach (var v in el.EnumerateArray())
        {
            if (v.ValueKind == JsonValueKind.Number)
                list.Add((ushort)v.GetInt32());
            else if (v.ValueKind == JsonValueKind.String &&
                     ushort.TryParse(v.GetString()?.Replace("0x", ""),
                         System.Globalization.NumberStyles.HexNumber, null, out var h))
                list.Add(h);
        }
        return list.ToArray();
    }

    private static TileTables BuildDefault()
    {
        return new TileTables
        {
            // Beach: WALKABLE sand is ONLY 0x16-0x19 (the 4 soft-sand variants). 0x1A-0x1C
            // (26-28) carry the Impassable flag in tiledata — they're sloped-sand variants
            // used for cliff/dune slope edges, never as a beach fill. 0x64 (100) is also
            // impassable (decorative scatter). Keep this pool uniform; per-cell BrushCoherence
            // (=0) avoids visible diamond banding from NxN coherent blocks.
            Beach = new ushort[] { 0x16, 0x17, 0x18, 0x19 },
            Water = new ushort[] { 0xA8, 0xA9, 0xAA, 0xAB },
            River = new ushort[] { 0xA8, 0xA9, 0xAA, 0xAB },
            Road  = new ushort[] { 0x47, 0x48, 0x49 },
            // Interior ("plain") land tiles per biome. Verified against the real Britannia map
            // (tiledata names + frequency): forest floor is 0xC4-0xC7 used evenly, jungle floor is
            // 0xAC-0xAF ("jungle"), mountain interior is the 0x22C-0x22F "rock" family. 0xC8-0xCB
            // are forest↔grass blend tiles and 0xDC-0xDF are cliff faces — neither is an interior.
            Land = new()
            {
                { BiomeId.DeepWater,    new ushort[] { 0xA8, 0xA9, 0xAA, 0xAB } },
                { BiomeId.ShallowWater, new ushort[] { 0xA8, 0xA9, 0xAA, 0xAB } },
                { BiomeId.River,        new ushort[] { 0xA8, 0xA9, 0xAA, 0xAB } },
                { BiomeId.Beach,        new ushort[] { 0x16, 0x17, 0x18, 0x19 } },
                { BiomeId.Grassland,    new ushort[] { 0x03, 0x04, 0x05, 0x06 } },
                { BiomeId.Forest,       new ushort[] { 0xC4, 0xC5, 0xC6, 0xC7 } },
                { BiomeId.DenseForest,  new ushort[] { 0xC4, 0xC5, 0xC6, 0xC7 } },
                { BiomeId.Jungle,       new ushort[] { 0xAC, 0xAD, 0xAE, 0xAF } },
                { BiomeId.Savanna,      new ushort[] { 0x03, 0x04, 0x05, 0x06 } },
                { BiomeId.Desert,       new ushort[] { 0x16, 0x17, 0x18, 0x19 } },
                // Snow + Tundra share the white-snow family 0x011A-0x011D.
                { BiomeId.Tundra,       new ushort[] { 0x011A, 0x011B, 0x011C, 0x011D } },
                { BiomeId.Snow,         new ushort[] { 0x011A, 0x011B, 0x011C, 0x011D } },
                { BiomeId.Mountain,     new ushort[] { 0x022C, 0x022D, 0x022E, 0x022F } },
                { BiomeId.HighMountain, new ushort[] { 0x022C, 0x022D, 0x022E, 0x022F } },
                // Swamp / Wetland: 0x3DEB-0x3DEF moss-green swamp ground.
                { BiomeId.Swamp,        new ushort[] { 0x3DEB, 0x3DEC, 0x3DED, 0x3DEE, 0x3DEF } },
                { BiomeId.Wetland,      new ushort[] { 0x3DEB, 0x3DEC, 0x3DED, 0x3DEE, 0x3DEF } },
            },
            // Tree IDs verified against UO Classic art for leafy/summer variants:
            //   0x0CCE = "tree" (oak, leafy summer)
            //   0x0CCD = "tree" (leafy)
            //   0x0CD0 = "tree" (oak, full leaves)
            //   0x0CD3 = "cedar"
            //   0x0CD8 = "tree" (leafy)
            //   0x0D24 = "palm tree"
            //   0x0D2D = "tree" (jungle palm)
            //   0x0CFE/CFF = "winter tree" (kept only for tundra/snow)
            //   0x0CCA/CCB are dead/winter trees and removed from non-snow biomes
            ForestSpecies = new()
            {
                { BiomeId.Forest,      new ushort[] { 0x0CCD, 0x0CCE, 0x0CD0, 0x0CD3, 0x0CD8 } },
                { BiomeId.DenseForest, new ushort[] { 0x0CCD, 0x0CCE, 0x0CD0, 0x0CD8 } },
                { BiomeId.Jungle,      new ushort[] { 0x0CCD, 0x0CD3, 0x0CD8, 0x0D24, 0x0D2D } },
                { BiomeId.Grassland,   new ushort[] { 0x0CCE, 0x0CD0 } },
                { BiomeId.Savanna,     new ushort[] { 0x0CD3, 0x0CCE } },
                { BiomeId.Tundra,      new ushort[] { 0x0CFE, 0x0CFF } },
                { BiomeId.Snow,        new ushort[] { 0x0CFE } },
                { BiomeId.Swamp,       new ushort[] { 0x0CD0, 0x0D2C } },
                { BiomeId.Wetland,     new ushort[] { 0x0CD0 } },
            },
            ForestDensity = new()
            {
                { BiomeId.Forest, 1.0 },
                { BiomeId.DenseForest, 1.6 },
                { BiomeId.Jungle, 1.4 },
                { BiomeId.Grassland, 0.15 },
                { BiomeId.Savanna, 0.25 },
                { BiomeId.Tundra, 0.25 },
                { BiomeId.Snow, 0.05 },
                { BiomeId.Swamp, 0.6 },
                { BiomeId.Wetland, 0.4 },
            },
        };
    }
}
