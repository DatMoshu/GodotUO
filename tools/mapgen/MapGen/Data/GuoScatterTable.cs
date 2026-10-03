using System.Globalization;
using System.Text.Json.Nodes;
using CentrED.MapGen.IR;

namespace CentrED.MapGen.Data;

/// <summary>
/// GUO's own scatter table, format <c>guo.mapgen.scatter/1</c> (docs/data_formats.md §26, "Scatter table"):
/// per biome a placement chance and weighted groups of statics, aliases for biomes that borrow another's
/// groups, and the tree trunk/canopy pairs. It feeds Biome Static Scatter (<see cref="ToStaticsTable"/>)
/// and the trunk/canopy pairing (<see cref="ToTreeStatics"/>).
/// </summary>
public sealed class GuoScatterTable
{
    public const string Format = "guo.mapgen.scatter/1";

    /// <summary>The committed table.</summary>
    public const string RelativePath = "tools/mapgen/MapGen/presets/scatter.guo.json";

    /// <summary>Each group's freq is split over its variants at this scale, so small groups keep their share.</summary>
    private const int VariantScale = 120;

    public sealed class Group
    {
        public string Name = "";
        public int Freq;
        /// <summary>The variants: each one is placed whole, one per pick.</summary>
        public List<List<BiomeStaticsTable.StaticTile>> Sets = new();
    }

    public sealed class Biome
    {
        public int Chance;
        public string Notes = "";
        public List<Group> Groups = new();
    }

    public Dictionary<BiomeId, Biome> Biomes { get; } = new();
    public Dictionary<BiomeId, BiomeId> Aliases { get; } = new();
    public List<(ushort Trunk, ushort Leaves, string Name)> Trees { get; } = new();

    public static GuoScatterTable Load(string path)
    {
        var node = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
            ?? throw new InvalidDataException($"{path}: not a JSON object");
        return Parse(node, path);
    }

    /// <summary>The committed table, or an empty one when the repository is not found.</summary>
    public static GuoScatterTable LoadDefault()
    {
        string path = RepoRootResolver.Resolve(RelativePath);
        return File.Exists(path) ? Load(path) : new GuoScatterTable();
    }

    public static GuoScatterTable Parse(JsonObject doc, string where = "scatter table")
    {
        if ((string?)doc["format"] != Format) throw new InvalidDataException($"{where}: format is not {Format}");
        var t = new GuoScatterTable();
        if (doc["aliases"] is JsonObject aliases)
            foreach (var (name, target) in aliases)
                t.Aliases[ParseBiome(name, where)] = ParseBiome((string?)target ?? "", where);
        if (doc["trees"] is JsonArray trees)
            foreach (var tn in trees)
            {
                if (tn is not JsonObject to) throw new InvalidDataException($"{where}: a tree is not an object");
                ushort trunk = ParseId(to["trunk"], $"{where}: tree"), leaves = ParseId(to["leaves"], $"{where}: tree");
                if (trunk == leaves || t.Trees.Any(x => x.Trunk == trunk))
                    throw new InvalidDataException($"{where}: tree 0x{trunk:X4} is listed twice or is its own canopy");
                t.Trees.Add((trunk, leaves, (string?)to["name"] ?? "tree"));
            }
        foreach (var (name, bn) in doc["biomes"] as JsonObject ?? throw new InvalidDataException($"{where}: no biomes"))
        {
            var id = ParseBiome(name, where);
            string at = $"{where}: {name}";
            if (bn is not JsonObject bo) throw new InvalidDataException($"{at}: not an object");
            if (t.Aliases.ContainsKey(id)) throw new InvalidDataException($"{at}: is also an alias");
            var b = new Biome { Chance = (int?)bo["chance"] ?? 0, Notes = (string?)bo["notes"] ?? "" };
            if (b.Chance is < 0 or > 100) throw new InvalidDataException($"{at}: chance {b.Chance} is out of range");
            foreach (var gn in bo["groups"] as JsonArray ?? throw new InvalidDataException($"{at}: no groups"))
            {
                if (gn is not JsonObject go) throw new InvalidDataException($"{at}: a group is not an object");
                var g = new Group { Name = (string?)go["name"] ?? "", Freq = (int?)go["freq"] ?? 0 };
                string gat = $"{at}.{g.Name}";
                if (g.Freq is < 1 or > 10000) throw new InvalidDataException($"{gat}: freq {g.Freq} is out of range");
                if (go["any"] is JsonArray any)
                    foreach (var e in any)
                        g.Sets.Add(new() { new BiomeStaticsTable.StaticTile { TileID = ParseId(e, gat) } });
                if (go["sets"] is JsonArray sets)
                    foreach (var s in sets)
                        g.Sets.Add(ParseSet(s, gat));
                if (g.Sets.Count == 0) throw new InvalidDataException($"{gat}: no 'any' ids or 'sets'");
                b.Groups.Add(g);
            }
            t.Biomes[id] = b;
        }
        foreach (var (alias, target) in t.Aliases)
            if (!t.Biomes.ContainsKey(target)) throw new InvalidDataException($"{where}: alias {alias} names {target}, which has no entry");
        return t;
    }

    /// <summary>The catalogue Biome Static Scatter reads: one weighted group per variant, aliases filled in.</summary>
    public BiomeStaticsTable ToStaticsTable()
    {
        var table = new BiomeStaticsTable();
        foreach (var (id, b) in Biomes)
        {
            var cat = new BiomeStaticsTable.BiomeCatalogue { Biome = id, ChancePercent = b.Chance };
            foreach (var g in b.Groups)
            {
                int each = Math.Max(1, (int)Math.Round((double)g.Freq * VariantScale / g.Sets.Count));
                foreach (var set in g.Sets)
                {
                    cat.Groups.Add(new BiomeStaticsTable.StaticGroup { Description = g.Name, Freq = each, Tiles = set });
                    cat.TotalFreq += each;
                }
            }
            table.ByBiome[id] = cat;
        }
        foreach (var (alias, target) in Aliases)
        {
            var src = table.ByBiome[target];
            table.ByBiome[alias] = new BiomeStaticsTable.BiomeCatalogue
            {
                Biome = alias, ChancePercent = src.ChancePercent, Groups = src.Groups, TotalFreq = src.TotalFreq,
            };
        }
        return table;
    }

    /// <summary>The trunk/canopy pairing Forest Scatter and Biome Static Scatter use.</summary>
    public TreeStatics ToTreeStatics()
    {
        var t = new TreeStatics();
        foreach (var (trunk, leaves, name) in Trees)
        {
            t.ById[trunk] = new TreeStatics.Entry(trunk, TreeStatics.Kind.TrunkOnly, name, null);
            t.ById[leaves] = new TreeStatics.Entry(leaves, TreeStatics.Kind.LeafOverlay, name, trunk);
            t.TrunkOnly.Add(trunk);
            t.TrunkToLeaf[trunk] = leaves;
        }
        return t;
    }

    private static BiomeId ParseBiome(string name, string where)
        => Enum.TryParse<BiomeId>(name, true, out var id) && Enum.IsDefined(id)
            ? id : throw new InvalidDataException($"{where}: unknown biome '{name}'");

    private static List<BiomeStaticsTable.StaticTile> ParseSet(JsonNode? s, string at)
    {
        var set = new List<BiomeStaticsTable.StaticTile>();
        foreach (var tn in s as JsonArray ?? throw new InvalidDataException($"{at}: a set is not a list"))
        {
            // [id, x, y, z] or [id, x, y, z, hue]
            if (tn is not JsonArray a || a.Count is < 4 or > 5) throw new InvalidDataException($"{at}: a tile is not [id, x, y, z(, hue)]");
            int x = (int?)a[1] ?? 0, y = (int?)a[2] ?? 0, z = (int?)a[3] ?? 0, hue = a.Count == 5 ? (int?)a[4] ?? 0 : 0;
            if (x is < -8 or > 8 || y is < -8 or > 8 || z is < -64 or > 64 || hue is < 0 or > 0xFFFF)
                throw new InvalidDataException($"{at}: offset or hue out of range in {a.ToJsonString()}");
            set.Add(new BiomeStaticsTable.StaticTile
            {
                TileID = ParseId(a[0], at), Xoff = (sbyte)x, Yoff = (sbyte)y, Zoff = (sbyte)z, Hue = (ushort)hue,
            });
        }
        if (set.Count == 0) throw new InvalidDataException($"{at}: an empty set");
        return set;
    }

    private static ushort ParseId(JsonNode? e, string at)
    {
        string? text = (string?)e;
        if (text is null || !text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            || !ushort.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id) || id == 0)
            throw new InvalidDataException($"{at}: '{e?.ToJsonString()}' is not a static id 0x0001-0xFFFF");
        return id;
    }
}
