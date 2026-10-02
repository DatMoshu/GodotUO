// SPDX-License-Identifier: BSD-2-Clause
//
// The GUO editor's world objects, applied to the private shard at boot
// (ADR-0014). Reads the manifest tools/world wrote (Data/GUO/guo_objects.json,
// put there by tools/editor_shard start --objects) and makes the world match
// it, by id:
//
//   spawners  through ModernUO's own SpawnerDto -> ToSpawner path, the one
//             [ImportSpawners uses, keyed by the spawner's GUID;
//   items     created as Static items, and remembered by serial.
//
// What GUO applied is recorded inside the world save itself (a ModernUO
// GenericPersistence, "GUOWorldObjects"), so the record and the world it
// describes are always saved and loaded together: a restart without a save
// rolls both back. (A file beside the save does not work: ModernUO's save
// replaces the whole Saves folder.) A later sync deletes and moves only what
// the record says is GUO's. Running it again with the same manifest changes
// nothing.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Server;
using Server.Engines.Spawners;
using Server.Items;
using Server.Logging;

namespace GUO.EditorBridge;

public static class WorldObjectsSync
{
    private static readonly ILogger logger = LogFactory.GetLogger(typeof(WorldObjectsSync));

    public static string ManifestPath => Path.Combine(Core.BaseDirectory, "Data", "GUO", "guo_objects.json");

    /// <summary>What GUO has applied: spawner GUID -> content hash, item id -> serial. Loaded with the save.</summary>
    internal static JsonObject Applied = NewApplied();

    internal static JsonObject NewApplied() =>
        new() { ["spawners"] = new JsonObject(), ["items"] = new JsonObject() };

    public static void Configure()
    {
        _ = new AppliedPersistence();
    }

    public static void Initialize()
    {
        // After the world has loaded, on the game thread.
        Server.Timer.DelayCall(TimeSpan.Zero, Run);
    }

    private static void Run()
    {
        if (!File.Exists(ManifestPath))
        {
            return;
        }

        try
        {
            var r = Sync(JsonNode.Parse(File.ReadAllText(ManifestPath)));

            // Save now, so the world and GUO's record of it (both in the save)
            // are on disk together; tools/editor_shard stops the process hard.
            if (r.SpawnersAdded + r.SpawnersChanged + r.SpawnersDeleted + r.ItemsAdded + r.ItemsChanged + r.ItemsDeleted > 0)
            {
                World.Save();
            }
            logger.Information(
                "GUO editor bridge: world objects synced: spawners +{0} ~{1} -{2} ={3}, items +{4} ~{5} -{6} ={7}",
                r.SpawnersAdded, r.SpawnersChanged, r.SpawnersDeleted, r.SpawnersKept,
                r.ItemsAdded, r.ItemsChanged, r.ItemsDeleted, r.ItemsKept
            );
        }
        catch (Exception ex)
        {
            logger.Error(ex, "GUO editor bridge: world objects sync failed");
        }
    }

    private sealed class Result
    {
        public int SpawnersAdded, SpawnersChanged, SpawnersDeleted, SpawnersKept;
        public int ItemsAdded, ItemsChanged, ItemsDeleted, ItemsKept;
    }

    /// <summary>What one put or delete did.</summary>
    public enum Outcome
    {
        Kept,
        Added,
        Changed,
        Deleted,
        Missing,
        Skipped,
    }

    private static JsonObject AppliedSpawners => Applied["spawners"].AsObject();
    private static JsonObject AppliedItems => Applied["items"].AsObject();

    private static Result Sync(JsonNode manifest)
    {
        var result = new Result();

        // --- spawners, from the ModernUO files the manifest lists ---
        var wanted = new HashSet<string>();
        foreach (JsonNode f in manifest["files"].AsArray())
        {
            string rel = (string)f;
            if (!rel.StartsWith("Data/Spawns/", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (JsonNode record in JsonNode.Parse(File.ReadAllText(Path.Combine(Core.BaseDirectory, rel))).AsArray())
            {
                wanted.Add((string)record["guid"]);
                switch (PutSpawner(record.AsObject()))
                {
                    case Outcome.Kept: result.SpawnersKept++; break;
                    case Outcome.Added: result.SpawnersAdded++; break;
                    case Outcome.Changed: result.SpawnersChanged++; break;
                }
            }
        }

        foreach (var (id, _) in AppliedSpawners.ToList())
        {
            if (!wanted.Contains(id) && DeleteSpawner(id) == Outcome.Deleted)
            {
                result.SpawnersDeleted++;
            }
        }

        // --- items, from the manifest (the cfg is for shards without the bridge) ---
        var wantedItems = new HashSet<string>();
        foreach (JsonNode m in manifest["items"].AsArray())
        {
            wantedItems.Add((string)m["id"]);
            switch (PutItem(m))
            {
                case Outcome.Kept: result.ItemsKept++; break;
                case Outcome.Added: result.ItemsAdded++; break;
                case Outcome.Changed: result.ItemsChanged++; break;
            }
        }

        foreach (var (id, _) in AppliedItems.ToList())
        {
            if (!wantedItems.Contains(id) && DeleteItem(id) == Outcome.Deleted)
            {
                result.ItemsDeleted++;
            }
        }

        return result;
    }

    private static BaseSpawner FindSpawner(Guid guid)
    {
        foreach (var item in World.Items.Values)
        {
            if (item is BaseSpawner s && !s.Deleted && s.Guid == guid)
            {
                return s;
            }
        }

        return null;
    }

    /// <summary>
    /// One spawner, given as a ModernUO SpawnerDto record: made (or remade)
    /// through ModernUO's own DTO path, the one [ImportSpawners uses. Kept as
    /// it is when GUO applied the same record and it stands where it should.
    /// Game thread only.
    /// </summary>
    public static Outcome PutSpawner(JsonObject record)
    {
        string text = record.ToJsonString();
        var dto = JsonSerializer.Deserialize<SpawnerDto>(text, SpawnerJsonSerializer.Options);
        string id = dto.Guid.ToString();
        string hash = Hash(text);
        BaseSpawner existing = FindSpawner(dto.Guid);
        if (existing != null && (string)AppliedSpawners[id] == hash
            && existing.Map == dto.Map && existing.Location == dto.Location)
        {
            return Outcome.Kept;
        }

        bool had = existing != null;
        existing?.Delete();
        BaseSpawner spawner = dto.ToSpawner();
        spawner.MoveToWorld(dto.Location, dto.Map);
        spawner.Respawn();
        AppliedSpawners[id] = hash;
        return had ? Outcome.Changed : Outcome.Added;
    }

    /// <summary>Deletes a spawner GUO applied (and what it spawned, as a spawner's delete does).</summary>
    public static Outcome DeleteSpawner(string id)
    {
        if (!AppliedSpawners.ContainsKey(id))
        {
            return Outcome.Missing;
        }

        AppliedSpawners.Remove(id);
        BaseSpawner gone = FindSpawner(Guid.Parse(id));
        gone?.Delete();
        return gone != null ? Outcome.Deleted : Outcome.Missing;
    }

    /// <summary>One placed item, in the manifest's form (id, map, location, item_id, hue, type).</summary>
    public static Outcome PutItem(JsonNode m)
        => PutItem(m, AppliedItems);

    internal static Outcome PutItem(JsonNode m, JsonObject owned)
    {
        string id = (string)m["id"];
        Map map = Map.Parse((string)m["map"]);
        var loc = m["location"].AsArray();
        var at = new Point3D((int)loc[0], (int)loc[1], (int)loc[2]);
        int itemId = Convert.ToInt32((string)m["item_id"], 16);
        int hue = Convert.ToInt32((string)m["hue"] ?? "0x0", 16);
        string type = (string)m["type"] ?? "Static";
        if (type != "Static")
        {
            logger.Warning("GUO editor bridge: item {0} is a {1}; only Static is synced yet", id, type);
            return Outcome.Skipped;
        }

        Item existing = owned[id]?["serial"] is JsonNode sn ? World.FindItem((Serial)(uint)sn) : null;
        Outcome outcome;
        if (existing is { Deleted: false })
        {
            if (existing.Map == map && existing.Location == at && existing.ItemID == itemId && existing.Hue == hue)
            {
                return Outcome.Kept;
            }

            existing.ItemID = itemId;
            existing.Hue = hue;
            existing.MoveToWorld(at, map);
            outcome = Outcome.Changed;
        }
        else
        {
            existing = new Static(itemId) { Hue = hue };
            existing.MoveToWorld(at, map);
            outcome = Outcome.Added;
        }

        owned[id] = new JsonObject { ["serial"] = (uint)existing.Serial };
        return outcome;
    }

    public static Outcome DeleteItem(string id)
        => DeleteItem(id, AppliedItems);

    internal static Outcome DeleteItem(string id, JsonObject owned)
    {
        if (owned[id]?["serial"] is not JsonNode sn)
        {
            return Outcome.Missing;
        }

        owned.Remove(id);
        if (World.FindItem((Serial)(uint)sn) is { Deleted: false } gone)
        {
            gone.Delete();
            return Outcome.Deleted;
        }

        return Outcome.Missing;
    }

    // --- the editor's neutral objects (docs/data_formats.md section 13) to ModernUO's form ---
    // Mirrors tools/world/backends/modernuo.py, key for key, so a spawner put
    // live and the same spawner exported later hash the same.

    public static JsonObject SpawnerRecord(JsonNode s)
    {
        var entries = new JsonArray();
        foreach (JsonNode e in s["entries"].AsArray())
        {
            entries.Add(new JsonObject
            {
                ["name"] = (string)e["name"],
                ["maxCount"] = (int?)e["max"] ?? 1,
                ["probability"] = (int?)e["probability"] ?? 100,
            });
        }

        var rec = new JsonObject
        {
            ["$type"] = "Spawner",
            ["guid"] = (string)s["id"],
            ["name"] = "GUO " + (string)s["entries"][0]["name"],
            ["location"] = new JsonArray((int)s["x"], (int)s["y"], (int)s["z"]),
            ["map"] = (string)s["map"],
            ["count"] = (int?)s["count"] ?? 1,
            ["minDelay"] = (string)s["min_delay"] ?? "00:05:00",
            ["maxDelay"] = (string)s["max_delay"] ?? "00:10:00",
            ["team"] = (int?)s["team"] ?? 0,
            ["homeRange"] = (int?)s["home_range"] ?? 2,
            ["walkingRange"] = (int?)s["walking_range"] ?? -1,
            ["entries"] = entries,
        };
        if (s["extra"] is JsonObject extra)
        {
            foreach (var (k, v) in extra)
            {
                if (!rec.ContainsKey(k))
                {
                    rec[k] = v?.DeepClone();
                }
            }
        }

        return rec;
    }

    public static JsonObject ItemManifest(JsonNode i) => new()
    {
        ["id"] = (string)i["id"],
        ["map"] = (string)i["map"],
        ["location"] = new JsonArray((int)i["x"], (int)i["y"], (int)i["z"]),
        ["item_id"] = (string)i["item_id"],
        ["hue"] = (string)i["hue"] ?? "0x0000",
        ["type"] = (string)i["type"] ?? "Static",
    };

    private static string Hash(string s) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s)));
}

/// <summary>GUO's record of applied world objects, stored in the world save (Saves/GUOWorldObjects).</summary>
public sealed class AppliedPersistence : GenericPersistence
{
    public AppliedPersistence() : base("GUOWorldObjects", 10)
    {
    }

    public override void Serialize(IGenericWriter writer)
    {
        writer.WriteEncodedInt(0); // version
        writer.Write(WorldObjectsSync.Applied.ToJsonString());
    }

    public override void Deserialize(IGenericReader reader)
    {
        reader.ReadEncodedInt(); // version
        string text = reader.ReadString();
        WorldObjectsSync.Applied = string.IsNullOrEmpty(text)
            ? WorldObjectsSync.NewApplied()
            : JsonNode.Parse(text).AsObject();
    }
}
