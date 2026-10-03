using System.Text.Json;

namespace CentrED.MapGen.Data;

// Loader for client/ClassicUO3D/Data/tree-statics.json — the canonical project
// reference for which static IDs are full trees vs trunks vs canopy overlays.
// Used by ForestScatter / StampScatter / CleanStamps to filter or pair correctly.
public sealed class TreeStatics
{
    public enum Kind : byte { Unknown = 0, WholeTree, TrunkOnly, LeafOverlay, Bush }

    public sealed record Entry(ushort Id, Kind Kind, string? Name, ushort? PairWith);

    public Dictionary<ushort, Entry> ById { get; } = new();
    public List<ushort> WholeTrees { get; } = new();
    public List<ushort> Bushes { get; } = new();
    public List<ushort> TrunkOnly { get; } = new();
    public Dictionary<ushort, ushort> TrunkToLeaf { get; } = new();   // trunk → its leaf-overlay pair

    public bool IsWhole(ushort id) => ById.TryGetValue(id, out var e) && (e.Kind == Kind.WholeTree || e.Kind == Kind.Bush);
    public bool IsLeafOverlay(ushort id) => ById.TryGetValue(id, out var e) && e.Kind == Kind.LeafOverlay;
    public bool IsTrunkOnly(ushort id) => ById.TryGetValue(id, out var e) && e.Kind == Kind.TrunkOnly;

    public static TreeStatics Empty { get; } = new();

    // A self-contained tooling copy may not contain the game's tree JSON.
    // The MIT Norad catalogue already groups each trunk with its canopy.
    public static TreeStatics FromCatalogue(string root)
    {
        var result = new TreeStatics();
        var catalogue = BiomeStaticsTable.LoadFromNorad(root, out _);
        foreach (var group in catalogue.ByBiome.Values.SelectMany(c => c.Groups).Where(g => g.Freq > 0).OrderByDescending(g => g.Freq))
        {
            if (!group.Description.Contains("tree", StringComparison.OrdinalIgnoreCase)) continue;
            var tiles = group.Tiles;
            if (tiles.Count == 2 && tiles.All(t => t.Xoff == 0 && t.Yoff == 0 && t.Zoff == 0))
            {
                ushort trunk = tiles[0].TileID, leaf = tiles[1].TileID;
                if (trunk == leaf || result.TrunkToLeaf.ContainsKey(trunk)) continue;
                result.TrunkToLeaf[trunk] = leaf;
                result.ById[trunk] = new Entry(trunk, Kind.TrunkOnly, group.Description, null);
                result.ById[leaf] = new Entry(leaf, Kind.LeafOverlay, group.Description, trunk);
                result.TrunkOnly.Add(trunk);
            }
            else if (tiles.Count == 1)
            {
                ushort id = tiles[0].TileID;
                result.ById.TryAdd(id, new Entry(id, Kind.WholeTree, group.Description, null));
                result.WholeTrees.Add(id);
            }
        }
        return result;
    }

    public static TreeStatics LoadOrEmpty(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return Empty;
        try
        {
            using var fs = File.OpenRead(path);
            using var doc = JsonDocument.Parse(fs);
            var t = new TreeStatics();
            if (!doc.RootElement.TryGetProperty("graphics", out var gEl) || gEl.ValueKind != JsonValueKind.Object)
                return Empty;
            foreach (var kv in gEl.EnumerateObject())
            {
                if (!ushort.TryParse(kv.Name, out var id)) continue;
                Kind kind = Kind.Unknown;
                if (kv.Value.TryGetProperty("kind", out var kEl))
                {
                    kind = kEl.GetString() switch
                    {
                        "WholeTree" => Kind.WholeTree,
                        "TrunkOnly" => Kind.TrunkOnly,
                        "LeafOverlay" => Kind.LeafOverlay,
                        "Bush" => Kind.Bush,
                        _ => Kind.Unknown,
                    };
                }
                string? name = kv.Value.TryGetProperty("name", out var nEl) ? nEl.GetString() : null;
                ushort? pair = kv.Value.TryGetProperty("pairWith", out var pEl) && pEl.ValueKind == JsonValueKind.Number
                    ? (ushort?)pEl.GetInt32() : null;
                var entry = new Entry(id, kind, name, pair);
                t.ById[id] = entry;
                switch (kind)
                {
                    case Kind.WholeTree: t.WholeTrees.Add(id); break;
                    case Kind.Bush: t.Bushes.Add(id); break;
                    case Kind.TrunkOnly: t.TrunkOnly.Add(id); break;
                }
            }
            // Build TrunkToLeaf: every LeafOverlay entry has a pairWith (trunk id).
            foreach (var e in t.ById.Values)
            {
                if (e.Kind == Kind.LeafOverlay && e.PairWith is { } trunkId)
                    t.TrunkToLeaf[trunkId] = e.Id;
            }
            return t;
        }
        catch { return Empty; }
    }
}
