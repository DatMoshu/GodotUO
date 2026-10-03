using System.Text;
using System.Text.Json;
using CentrED.MapGen.Data;
using CentrED.MapGen.IR;
using CentrED.MapGen.Pipeline;
using CentrED.MapGen.Stamps;

namespace CentrED.MapGen.Passes.Rooms;

public sealed class DungeonSpawnerEmitParams
{
    [TunableDisplay("Roster JSON path (repo-relative)")]
    public string RosterJsonPath { get; set; } = "tools/mapgen/data/dungeon-roster.json";

    [TunableDisplay("Output dir", Tooltip = "Where to write <basename>-<seed>.json. Empty (default) = write nothing: the JSON is kept on the pass (LastJson) and the run has no file side effects. Set it (or let the commit step set it) to emit the file.")]
    public string OutputDir { get; set; } = "";

    [TunableDisplay("Output file basename (no extension)")]
    public string Basename { get; set; } = "rooms-maze";

    [TunableDisplay("Map name (Felucca/Trammel/Ilshenar/Malas/Tokuno/TerMur)")]
    public string Map { get; set; } = "Felucca";

    [TunableDisplay("Spawner name prefix")]
    public string NamePrefix { get; set; } = "RoomsMaze";
}

// Emits a JSON file matching ModernUO's ExportSpawnersCommand output. The output
// is consumed in-game by `[ImportSpawners <path>` (see ImportSpawnersCommand.cs).
// One Spawner record per DungeonRoomRect; non-Empty rooms get tier-appropriate
// mob entries from tools/mapgen/data/dungeon-roster.json. Treasure rooms get an
// extra chest-spawner record (TreasureChest spawned via the same Spawner type,
// amount=1) so chests automatically re-stock on the spawner timer.
//
// Spawner GUIDs are derived from (seed, basename, spawner name), so the same seed
// produces byte-identical JSON. Spawner Z is the floor Z under the room centre (Rooms
// Stamp flattens the floor to its FloorZ), not a hardcoded 0.
//
// No file is written unless OutputDir is set: tests, previews (BiomeLab) and plain
// pipeline runs have no side effects. The JSON is always available as LastJson.
//
// Reads:  DungeonRooms (sparse), Height (floor Z).
// Writes: nothing in the IR; optional file on disk.
public sealed class DungeonSpawnerEmitPass : IGenerationPass
{
    public string Name => "Dungeon Spawner Emit";
    public string Category => "Rooms";

    public IrFields Reads => IrFields.None;
    public IrFields Writes => IrFields.None;

    public object CreateDefaultParams() => new DungeonSpawnerEmitParams();

    /// <summary>JSON produced by the last Run (also when nothing was written).</summary>
    public string? LastJson { get; private set; }
    /// <summary>Full path written by the last Run, or null when OutputDir was empty.</summary>
    public string? LastOutputPath { get; private set; }

    public void Run(GenContext ctx, object parameters)
    {
        var p = (DungeonSpawnerEmitParams)parameters;
        var ir = ctx.IR;
        if (ir.DungeonRooms.Count == 0)
        {
            ctx.Report.Warnings.Add("no DungeonRooms in IR — RoomsCarvePass did not run or produced none");
            return;
        }

        var roster = DungeonRoster.Load(RepoRootResolver.Resolve(p.RosterJsonPath));
        if (roster is null)
        {
            ctx.Report.Warnings.Add($"could not load {p.RosterJsonPath}");
            return;
        }

        long seed = unchecked((long)ir.Seed);
        var fileName = $"{p.Basename}-{seed:x}.json";
        string guidSalt = $"{p.Basename}|{seed:x}|";
        LastJson = null;
        LastOutputPath = null;

        var sb = new StringBuilder(64 * 1024);
        sb.Append('[');
        bool first = true;
        int mobSpawners = 0, chestSpawners = 0;

        foreach (var room in ir.DungeonRooms)
        {
            if (room.Kind == RoomKind.Empty) continue;
            string kindKey = room.Kind.ToString();
            int cx = (room.X1 + room.X2) / 2;
            int cy = (room.Y1 + room.Y2) / 2;
            int floorZ = ir.Height_Z is { } hz ? hz[ir.Index(cx, cy)] : 0;

            // Mob spawner.
            if (roster.Tiers.TryGetValue(kindKey, out var mobEntries) && mobEntries.Length > 0)
            {
                int count = roster.SpawnCountByKind.TryGetValue(kindKey, out var c) ? c : 2;
                AppendSpawner(sb, ref first, guidSalt,
                    name: $"{p.NamePrefix}_{kindKey}_{room.Id:D3}",
                    mapName: p.Map,
                    x: cx, y: cy, z: floorZ,
                    count: count,
                    minDelay: roster.MinDelay, maxDelay: roster.MaxDelay,
                    entries: mobEntries,
                    x1: room.X1, y1: room.Y1, x2: room.X2, y2: room.Y2);
                mobSpawners++;
            }

            // Treasure rooms also spawn a chest (separate spawner so chest timer is independent).
            if (room.Kind == RoomKind.Treasure || room.Kind == RoomKind.Boss)
            {
                string chestClass = roster.ChestClassByTier.TryGetValue(kindKey, out var cc) ? cc : "WoodenTreasureChest";
                var chestEntries = new[] { new RosterEntry(chestClass, 100, 1) };
                AppendSpawner(sb, ref first, guidSalt,
                    name: $"{p.NamePrefix}_{kindKey}Chest_{room.Id:D3}",
                    mapName: p.Map,
                    x: cx, y: cy, z: floorZ,
                    count: 1,
                    minDelay: roster.MinDelay, maxDelay: roster.MaxDelay,
                    entries: chestEntries,
                    x1: room.X1, y1: room.Y1, x2: room.X2, y2: room.Y2);
                chestSpawners++;
            }
        }

        sb.Append(']');
        LastJson = sb.ToString();

        if (!string.IsNullOrWhiteSpace(p.OutputDir))
        {
            var outDir = Path.IsPathRooted(p.OutputDir) ? p.OutputDir : RepoRootResolver.Resolve(p.OutputDir);
            Directory.CreateDirectory(outDir);
            var outPath = Path.Combine(outDir, fileName);
            File.WriteAllText(outPath, LastJson);
            LastOutputPath = outPath;
            ctx.Report.Notes.Add($"wrote {outPath}");
        }
        else ctx.Report.Notes.Add($"OutputDir empty: {fileName} not written ({LastJson.Length} bytes kept in memory)");
        ctx.Report.Notes.Add($"spawners: mobs={mobSpawners} chests={chestSpawners}");
    }

    private static void AppendSpawner(StringBuilder sb, ref bool first, string guidSalt,
        string name, string mapName,
        int x, int y, int z, int count,
        string minDelay, string maxDelay,
        IReadOnlyList<RosterEntry> entries,
        int x1, int y1, int x2, int y2)
    {
        if (!first) sb.Append(',');
        first = false;
        var guid = StableHash.Guid(guidSalt + name).ToString("D");
        sb.Append('{');
        sb.Append("\"type\":\"Spawner\",");
        sb.Append("\"guid\":\"").Append(guid).Append("\",");
        sb.Append("\"name\":\"").Append(JsonEscape(name)).Append("\",");
        sb.Append("\"location\":{\"x\":").Append(x).Append(",\"y\":").Append(y).Append(",\"z\":").Append(z).Append("},");
        sb.Append("\"map\":\"").Append(JsonEscape(mapName)).Append("\",");
        sb.Append("\"count\":").Append(count).Append(',');
        sb.Append("\"minDelay\":\"").Append(minDelay).Append("\",");
        sb.Append("\"maxDelay\":\"").Append(maxDelay).Append("\",");
        sb.Append("\"spawnBounds\":{\"x1\":").Append(x1).Append(",\"y1\":").Append(y1)
          .Append(",\"x2\":").Append(x2).Append(",\"y2\":").Append(y2).Append("},");
        sb.Append("\"entries\":[");
        for (int i = 0; i < entries.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var e = entries[i];
            sb.Append("{\"name\":\"").Append(JsonEscape(e.Name)).Append("\",")
              .Append("\"probability\":").Append(e.Probability).Append(',')
              .Append("\"maxCount\":").Append(e.MaxCount).Append('}');
        }
        sb.Append(']');
        sb.Append('}');
    }

    private static string JsonEscape(string s)
    {
        if (string.IsNullOrEmpty(s)) return s ?? "";
        var sb = new StringBuilder(s.Length + 8);
        foreach (var c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"':  sb.Append("\\\""); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:   sb.Append(c); break;
            }
        }
        return sb.ToString();
    }
}

internal readonly record struct RosterEntry(string Name, int Probability, int MaxCount);

internal sealed class DungeonRoster
{
    public Dictionary<string, RosterEntry[]> Tiers { get; init; } = new();
    public Dictionary<string, string> ChestClassByTier { get; init; } = new();
    public Dictionary<string, int> SpawnCountByKind { get; init; } = new();
    public string MinDelay { get; init; } = "00:05:00";
    public string MaxDelay { get; init; } = "00:10:00";

    public static DungeonRoster? Load(string path)
    {
        if (!File.Exists(path)) return null;
        using var fs = File.OpenRead(path);
        using var doc = JsonDocument.Parse(fs);
        var root = doc.RootElement;

        var tiers = new Dictionary<string, RosterEntry[]>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("tiers", out var tiersEl) && tiersEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var tier in tiersEl.EnumerateObject())
            {
                var list = new List<RosterEntry>();
                foreach (var entry in tier.Value.EnumerateArray())
                {
                    string name = entry.GetProperty("name").GetString() ?? "";
                    int prob = entry.TryGetProperty("probability", out var pEl) ? pEl.GetInt32() : 100;
                    int maxC = entry.TryGetProperty("maxCount", out var mEl) ? mEl.GetInt32() : 1;
                    list.Add(new RosterEntry(name, prob, maxC));
                }
                tiers[tier.Name] = list.ToArray();
            }
        }

        var chestByTier = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("chest_classes_by_tier", out var chestEl) && chestEl.ValueKind == JsonValueKind.Object)
            foreach (var kv in chestEl.EnumerateObject())
                chestByTier[kv.Name] = kv.Value.GetString() ?? "WoodenTreasureChest";

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("spawn_count_by_kind", out var ce) && ce.ValueKind == JsonValueKind.Object)
            foreach (var kv in ce.EnumerateObject())
                counts[kv.Name] = kv.Value.GetInt32();

        string minDelay = "00:05:00", maxDelay = "00:10:00";
        if (root.TryGetProperty("delays", out var dEl) && dEl.ValueKind == JsonValueKind.Object)
        {
            if (dEl.TryGetProperty("minDelay", out var miEl)) minDelay = miEl.GetString() ?? minDelay;
            if (dEl.TryGetProperty("maxDelay", out var maEl)) maxDelay = maEl.GetString() ?? maxDelay;
        }

        return new DungeonRoster
        {
            Tiers = tiers,
            ChestClassByTier = chestByTier,
            SpawnCountByKind = counts,
            MinDelay = minDelay,
            MaxDelay = maxDelay,
        };
    }
}
