using System.Text.Json;
using CentrED.MapGen.Data;
using CentrED.MapGen.IR;
using CentrED.MapGen.Pipeline;

namespace CentrED.MapGen.Passes.Rooms;

public sealed class DungeonDecorScatterParams
{
    [TunableDisplay("Decor frequencies JSON path (repo-relative)")]
    public string DecorJsonPath { get; set; } = "tools/mapgen/data/dungeon-decor-frequencies.json";

    [TunableDisplay("Global density multiplier")]
    [TunableRange(0.0, 4.0)]
    public double DensityMultiplier { get; set; } = 1.0;

    [TunableDisplay("Decor seed (0 = use pipeline seed)")]
    public int DecorSeed { get; set; } = 0;
}

// Scatters dungeon decor (bones, blood, web, torches, urns, barrels) onto Floor
// and Corridor tiles. Density per room-kind comes from
// tools/mapgen/data/dungeon-decor-frequencies.json. Tile IDs come from the same
// JSON — hand-curated MVP, replaceable with a miner output later.
//
// Reads:  DungeonZone.
// Writes: StaticOps.
public sealed class DungeonDecorScatterPass : IGenerationPass
{
    public string Name => "Dungeon Decor Scatter";
    public string Category => "Rooms";

    public IrFields Reads => IrFields.DungeonZone;
    public IrFields Writes => IrFields.StaticOps;

    public object CreateDefaultParams() => new DungeonDecorScatterParams();

    public void Run(GenContext ctx, object parameters)
    {
        var p = (DungeonDecorScatterParams)parameters;
        var ir = ctx.IR;
        if (ir.DungeonZone is null)
        {
            ctx.Report.Warnings.Add("DungeonZone not allocated — DungeonDecorScatter requires RoomsCarvePass first");
            return;
        }

        var data = DungeonDecorData.Load(RepoRootResolver.Resolve(p.DecorJsonPath));
        if (data is null)
        {
            ctx.Report.Warnings.Add($"could not load {p.DecorJsonPath}");
            return;
        }

        var rng = p.DecorSeed != 0 ? new Random(p.DecorSeed) : ctx.Rng;
        var scope = ir.Scope;
        var zone = ir.DungeonZone;
        var height = ir.Height_Z;
        double multiplier = Math.Max(0.0, p.DensityMultiplier);

        int placed = 0;
        for (int y = scope.Y1; y <= scope.Y2; y++)
        for (int x = scope.X1; x <= scope.X2; x++)
        {
            int idx = ir.Index(x, y);
            var z = (DungeonZone)zone[idx];
            if (z == DungeonZone.Outside || z == DungeonZone.Wall) continue;

            string kindKey = z switch
            {
                DungeonZone.RoomBoss     => "Boss",
                DungeonZone.RoomElite    => "Elite",
                DungeonZone.RoomTrash    => "Trash",
                DungeonZone.RoomTreasure => "Treasure",
                DungeonZone.RoomEmpty    => "Empty",
                DungeonZone.Corridor     => "Corridor",
                _                        => "Empty",
            };

            if (!data.DensityByRoomKind.TryGetValue(kindKey, out var densities)) continue;

            // densities is a deterministically-sorted array (see DungeonDecorData.Load);
            // do NOT switch back to Dictionary iteration here — see comment on the type.
            foreach (var (category, density) in densities)
            {
                double d = density * multiplier;
                if (d <= 0.0) continue;
                if (rng.NextDouble() > d) continue;
                if (!data.Decor.TryGetValue(category, out var entries) || entries.Length == 0) continue;
                ushort id = PickWeighted(entries, rng);
                sbyte zh = height is not null ? height[idx] : (sbyte)0;
                ir.StaticOps.Add(new StaticOp(StaticOpKind.Add, (ushort)x, (ushort)y, zh, id, 0));
                placed++;
                break; // at most one decor per tile
            }
        }

        ctx.Report.StaticsAdded += placed;
        ctx.Report.Notes.Add($"decor placed: {placed}");
    }

    private static ushort PickWeighted((ushort Id, int Weight)[] entries, Random rng)
    {
        int total = 0;
        foreach (var e in entries) total += Math.Max(1, e.Weight);
        int pick = rng.Next(total);
        foreach (var e in entries)
        {
            pick -= Math.Max(1, e.Weight);
            if (pick < 0) return e.Id;
        }
        return entries[0].Id;
    }
}

// DensityByRoomKind stores its inner category→density map as a sorted
// KeyValuePair array, NOT a Dictionary. Reason: the scatter pass iterates this
// inside the per-tile rng.NextDouble() loop, so iteration order is part of the
// seeded output. Dictionary<,> iteration order is implementation-defined and
// changes across .NET versions / JIT — that would silently break reproducible
// seeds. Sort once at load, iterate the array forever after.
internal sealed class DungeonDecorData
{
    public Dictionary<string, (ushort Id, int Weight)[]> Decor { get; init; } = new();
    public Dictionary<string, KeyValuePair<string, double>[]> DensityByRoomKind { get; init; } = new();

    public static DungeonDecorData? Load(string path)
    {
        if (!File.Exists(path)) return null;
        using var fs = File.OpenRead(path);
        using var doc = JsonDocument.Parse(fs);
        var root = doc.RootElement;

        var decor = new Dictionary<string, (ushort, int)[]>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("decor", out var decorEl) && decorEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var cat in decorEl.EnumerateObject())
            {
                var list = new List<(ushort, int)>();
                foreach (var entry in cat.Value.EnumerateArray())
                {
                    var idStr = entry.GetProperty("id").GetString();
                    int weight = entry.TryGetProperty("weight", out var wEl) ? wEl.GetInt32() : 1;
                    if (TryParseHex(idStr, out ushort id))
                        list.Add((id, weight));
                }
                decor[cat.Name] = list.ToArray();
            }
        }

        var density = new Dictionary<string, KeyValuePair<string, double>[]>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("density_by_room_kind", out var densEl) && densEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var room in densEl.EnumerateObject())
            {
                var inner = new List<KeyValuePair<string, double>>();
                foreach (var kv in room.Value.EnumerateObject())
                    inner.Add(new KeyValuePair<string, double>(kv.Name, kv.Value.GetDouble()));
                // Sort by category name so per-tile iteration order is deterministic
                // regardless of .NET version's Dictionary internals.
                inner.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
                density[room.Name] = inner.ToArray();
            }
        }

        return new DungeonDecorData { Decor = decor, DensityByRoomKind = density };
    }

    private static bool TryParseHex(string? s, out ushort id)
    {
        id = 0;
        if (string.IsNullOrEmpty(s)) return false;
        var hex = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s.Substring(2) : s;
        return ushort.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out id);
    }
}
