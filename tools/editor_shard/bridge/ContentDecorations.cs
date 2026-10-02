// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Server;
using Server.Commands;

namespace GUO.EditorBridge;

internal sealed class ContentDecorations
{
    private readonly Dictionary<string, JsonObject[]> _sets = new(StringComparer.Ordinal);
    private static JsonObject Owned => (WorldObjectsSync.Applied["content_items"] ??= new JsonObject()).AsObject();
    private static bool Identity(string value) => value != null && Regex.IsMatch(value, @"\A[a-z0-9][a-z0-9-]{0,63}:[a-z0-9][a-z0-9-]{0,63}\z");

    public static ContentDecorations Stage(JsonElement root)
    {
        var result = new ContentDecorations();
        if (!root.TryGetProperty("decorations", out var sets)) return result;
        if (sets.GetArrayLength() > 128) throw new InvalidDataException("Too many decoration sets");
        int total = 0;
        foreach (var set in sets.EnumerateArray())
        {
            string identity = set.GetProperty("identity").GetString();
            int facet = set.GetProperty("facet").GetInt32();
            if (!Identity(identity) || facet < 0 || facet >= Map.Maps.Length || Map.Maps[facet] == null || Map.Maps[facet] == Map.Internal)
                throw new InvalidDataException("Invalid decoration identity or facet");
            var map = Map.Maps[facet];
            var rows = new List<JsonObject>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in set.GetProperty("items").EnumerateArray())
            {
                string id = item.GetProperty("id").GetString();
                int x = item.GetProperty("x").GetInt32(), y = item.GetProperty("y").GetInt32();
                int z = item.GetProperty("z").GetInt32(), graphic = item.GetProperty("graphic").GetInt32(), hue = item.GetProperty("hue").GetInt32();
                if (id == null || !Regex.IsMatch(id, @"\A[a-z0-9][a-z0-9-]{0,63}\z") || !ids.Add(id)
                    || x < 0 || x >= map.Width || y < 0 || y >= map.Height || z < -128 || z > 127
                    || graphic < 0 || graphic > 65535 || hue < 0 || hue > 0x3fff || ++total > 16384 || rows.Count >= 4096)
                    throw new InvalidDataException("Invalid decoration placement");
                rows.Add(new JsonObject { ["id"] = identity + "/" + id, ["map"] = map.MapIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["location"] = new JsonArray(x, y, z), ["item_id"] = graphic.ToString("X"), ["hue"] = hue.ToString("X"), ["type"] = "Static" });
            }
            if (rows.Count == 0 || !result._sets.TryAdd(identity, rows.ToArray())) throw new InvalidDataException("Empty or duplicate decoration set");
        }
        return result;
    }

    public void Register(bool probe)
    {
        CommandSystem.Register("GUOPackDecorate", AccessLevel.Administrator, args =>
        {
            if (args.Length != 1 || !_sets.ContainsKey(args.GetString(0))) { args.Mobile.SendMessage("Usage: GUOPackDecorate pack-id:component-id"); return; }
            Apply(args.GetString(0), Owned);
            args.Mobile.SendMessage("Decoration set applied. Repeating this command preserves existing placements. Save the world to persist it.");
        });
        CommandSystem.Register("GUOPackUndecorate", AccessLevel.Administrator, args =>
        {
            if (args.Length != 1 || !Identity(args.GetString(0))) { args.Mobile.SendMessage("Usage: GUOPackUndecorate pack-id:component-id"); return; }
            int removed = Remove(args.GetString(0), Owned);
            args.Mobile.SendMessage($"Removed {removed} owned placements. Save the world to persist it.");
        });
        if (!probe || _sets.Count == 0) return;
        foreach (var (identity, records) in _sets)
        {
            var unrelated = new Server.Items.Static(1);
            var scratch = new JsonObject { ["unrelated:test/keep"] = new JsonObject { ["serial"] = (uint)unrelated.Serial } };
            try
            {
                Apply(identity, scratch);
                var serials = scratch.Where(pair => pair.Key.StartsWith(identity + "/", StringComparison.Ordinal)).Select(pair => (uint)pair.Value["serial"]).ToArray();
                scratch = JsonNode.Parse(scratch.ToJsonString()).AsObject(); // Same representation used by world-save persistence.
                if (Apply(identity, scratch) != records.Length) throw new InvalidDataException("Repeated decoration placement was not idempotent");
                foreach (var record in records)
                {
                    var item = World.FindItem((Serial)(uint)scratch[(string)record["id"]]["serial"]);
                    if (item == null || item.Deleted || item.Movable || item.ItemID != Convert.ToInt32((string)record["item_id"], 16)
                        || item.Hue != Convert.ToInt32((string)record["hue"], 16) || item.Map != Map.Parse((string)record["map"])
                        || item.X != (int)record["location"][0] || item.Y != (int)record["location"][1] || item.Z != (int)record["location"][2])
                        throw new InvalidDataException("Decoration world placement differs");
                }
                Remove(identity, scratch);
                if (scratch.Count != 1 || unrelated.Deleted || scratch["unrelated:test/keep"] == null || serials.Any(serial => World.FindItem((Serial)serial) is { Deleted: false }))
                    throw new InvalidDataException("Decoration probe did not remove its owned objects");
            }
            finally { Remove(identity, scratch); unrelated.Delete(); }
        }
        Console.WriteLine($"[GUO content] PASS: {_sets.Count} decoration sets placed, reapplied without duplicates, and removed using isolated ownership records.");
    }

    private int Apply(string identity, JsonObject owned)
    {
        var wanted = new HashSet<string>(StringComparer.Ordinal);
        int kept = 0;
        foreach (var record in _sets[identity])
        {
            wanted.Add((string)record["id"]);
            if (WorldObjectsSync.PutItem(record, owned) == WorldObjectsSync.Outcome.Kept) kept++;
        }
        foreach (string id in owned.Select(pair => pair.Key).Where(id => id.StartsWith(identity + "/", StringComparison.Ordinal) && !wanted.Contains(id)).ToArray())
            WorldObjectsSync.DeleteItem(id, owned);
        return kept;
    }

    private static int Remove(string identity, JsonObject owned)
    {
        int removed = 0;
        foreach (string id in owned.Select(pair => pair.Key).Where(id => id.StartsWith(identity + "/", StringComparison.Ordinal)).ToArray())
            if (WorldObjectsSync.DeleteItem(id, owned) == WorldObjectsSync.Outcome.Deleted) removed++;
        return removed;
    }
}
