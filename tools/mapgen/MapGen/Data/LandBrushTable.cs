using System.Text.Json;

namespace CentrED.MapGen.Data;

// Loader for the LandBrush JSON produced by TransitionMiner (and compatible
// with CentrED's LandBrush Manager). Used by LandTransitionPass to look up
// the right tile for a given (selfBiome, otherBiome, dirMask) triple instead
// of relying on hardcoded id ranges.
//
// HashKey9 (optional, F-1): 9-byte string of biome group codes for the 3x3
// neighbourhood (NW N NE W C E SW S SE), matching uo-landscaper-mod's format.
// Lets us encode 3-way biome junctions that the binary self/other Direction
// mask cannot express. PickTransitionTile prefers HashKey9 matches when an
// exact key is supplied; otherwise falls back to Direction mask.
public sealed class LandBrushTable
{
    public sealed class Brush
    {
        public string Name = "";
        public List<ushort> Tiles = new();
        public Dictionary<string, List<Transition>> Transitions = new();
    }

    public sealed class Transition
    {
        public ushort TileID;
        public int AltitudeMin; // Dragon decimal Z addition, retained separately from tile selection
        public int AltitudeMax;
        public byte Direction;
        public string? HashKey9;     // optional 9-cell biome-group hex (18 chars), e.g. "010118181801181818"
        public sbyte AltIDMod;       // optional z offset (Norad-compatible); 0 if absent
    }

    public Dictionary<string, Brush> Brushes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsLoaded => Brushes.Count > 0;

    public static LandBrushTable Empty { get; } = new();

    /// <summary>
    /// Repo-relative path of a brush table shipped in the repo (DragonMod rules import). GUO does not
    /// ship one until the owner clears Dragon's terms (docs/upstream/mapgen.md); the user's own import
    /// lives in the data folder (<see cref="DataJsonRelativePath"/>).
    /// </summary>
    public const string DefaultJsonRelativePath = "tools/mapgen/MapGen/presets/landbrush.dragon.json";

    /// <summary>The user's Dragon import in the generator data folder (UO_MAPGEN_DATA/landbrush.dragon.json).</summary>
    public const string DataJsonRelativePath = "mined/landbrush.dragon.json";

    /// <summary>Loads the user's import, else a shipped table; <see cref="Empty"/> when neither exists.</summary>
    public static LandBrushTable LoadDefault()
    {
        string user = RepoRootResolver.Resolve(DataJsonRelativePath);
        return LoadOrEmpty(File.Exists(user) ? user : RepoRootResolver.Resolve(DefaultJsonRelativePath));
    }

    public static LandBrushTable LoadOrEmpty(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return Empty;
        try
        {
            using var fs = File.OpenRead(path);
            using var doc = JsonDocument.Parse(fs);
            var t = new LandBrushTable();
            foreach (var bp in doc.RootElement.EnumerateObject())
            {
                if (bp.Name.StartsWith("_")) continue; // skip provenance / metadata fields
                if (bp.Value.ValueKind != JsonValueKind.Object) continue;
                var brush = new Brush { Name = bp.Name };
                if (bp.Value.TryGetProperty("Tiles", out var tiles) && tiles.ValueKind == JsonValueKind.Array)
                    foreach (var tid in tiles.EnumerateArray()) brush.Tiles.Add((ushort)tid.GetInt32());
                if (bp.Value.TryGetProperty("Transitions", out var trs) && trs.ValueKind == JsonValueKind.Object)
                {
                    foreach (var tp in trs.EnumerateObject())
                    {
                        var list = new List<Transition>();
                        foreach (var tEl in tp.Value.EnumerateArray())
                        {
                            var entry = new Transition
                            {
                                TileID = (ushort)tEl.GetProperty("TileID").GetInt32(),
                                Direction = tEl.TryGetProperty("Direction", out var d) ? (byte)d.GetInt32() : (byte)0,
                            };
                            if (tEl.TryGetProperty("HashKey9", out var hk) && hk.ValueKind == JsonValueKind.String)
                                entry.HashKey9 = hk.GetString();
                            if (tEl.TryGetProperty("AltIDMod", out var am) && am.ValueKind == JsonValueKind.Number)
                                entry.AltIDMod = (sbyte)am.GetInt32();
                            if (tEl.TryGetProperty("AltitudeMin", out var mn)) entry.AltitudeMin = mn.GetInt32();
                            if (tEl.TryGetProperty("AltitudeMax", out var mx)) entry.AltitudeMax = mx.GetInt32();
                            list.Add(entry);
                        }
                        brush.Transitions[tp.Name] = list;
                    }
                }
                t.Brushes[bp.Name] = brush;
            }
            return t;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"WARNING: LandBrushTable.LoadOrEmpty failed for '{path}': {ex.Message}");
            return Empty;
        }
    }

    // Returns a tile id for the given transition, or 0 if no entry covers the mask.
    // Picks the entry with the smallest direction-bit-count that fully contains the
    // requested mask — same algorithm as CentrED's TryGetMinimalTransition.
    public ushort PickTransitionTile(string self, string other, byte mask, Random rng)
    {
        if (!Brushes.TryGetValue(self, out var brush)) return 0;
        if (!brush.Transitions.TryGetValue(other, out var list) || list.Count == 0) return 0;
        var matched = list.Where(t => (t.Direction & mask) == mask).ToList();
        if (matched.Count == 0) return 0;
        int minPop = matched.Min(t => PopCount(t.Direction));
        var best = matched.Where(t => PopCount(t.Direction) == minPop).ToArray();
        return best[rng.Next(best.Length)].TileID;
    }

    // Deterministic variant of PickTransitionTile: chooses among the equally-minimal
    // entries by a caller-supplied per-cell hash instead of an RNG draw, so the result
    // does not depend on how many draws earlier passes made. Returns 0 when no entry
    // covers the mask.
    public ushort PickTransitionTile(string self, string other, byte mask, uint hash)
    {
        var lut = Lookup(self, other);
        if (lut is null) return 0;
        var c = lut[mask];
        return c is { Length: > 0 } ? c[hash % (uint)c.Length] : (ushort)0;
    }

    /// <summary>True when the table has any transition from <paramref name="self"/> toward <paramref name="other"/>.</summary>
    public bool HasPair(string self, string other) => Lookup(self, other) is not null;

    private readonly Dictionary<(string, string), ushort[]?[]?> _lookups = new();

    // Per-pair table: for each 8-bit mask, the tile ids of the minimal-popcount entries
    // whose Direction is a superset of the mask (CentrED's TryGetMinimalTransition).
    // null = pair absent. Built once per pair.
    public ushort[]?[]? Lookup(string self, string other)
    {
        lock (_lookups)
        {
            if (_lookups.TryGetValue((self, other), out var cached)) return cached;
            ushort[]?[]? lut = null;
            if (Brushes.TryGetValue(self, out var brush)
                && brush.Transitions.TryGetValue(other, out var list) && list.Count > 0)
            {
                lut = new ushort[]?[256];
                for (int mask = 1; mask < 256; mask++)
                {
                    int best = int.MaxValue;
                    var ids = new List<ushort>();
                    foreach (var t in list)
                    {
                        if ((t.Direction & mask) != mask) continue;
                        int pc = PopCount(t.Direction);
                        if (pc < best) { best = pc; ids.Clear(); }
                        if (pc == best && !ids.Contains(t.TileID)) ids.Add(t.TileID);
                    }
                    lut[mask] = ids.Count > 0 ? ids.ToArray() : null;
                }
            }
            _lookups[(self, other)] = lut;
            return lut;
        }
    }

    // F-1: Pick a transition tile by exact 9-cell HashKey (3-way junction support).
    // Returns 0 if no entry stores HashKey9 metadata or none matches the supplied key.
    // Caller should fall back to PickTransitionTile (mask-based) on miss.
    public ushort PickByHashKey9(string self, string other, string hashKey9, Random rng)
    {
        if (string.IsNullOrEmpty(hashKey9)) return 0;
        if (!Brushes.TryGetValue(self, out var brush)) return 0;
        if (!brush.Transitions.TryGetValue(other, out var list) || list.Count == 0) return 0;
        var matched = list.Where(t => string.Equals(t.HashKey9, hashKey9, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matched.Length == 0) return 0;
        return matched[rng.Next(matched.Length)].TileID;
    }

    private static int PopCount(byte v)
    {
        int c = 0;
        while (v != 0) { v &= (byte)(v - 1); c++; }
        return c;
    }
}
