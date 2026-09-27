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

    private static Result Sync(JsonNode manifest)
    {
        var result = new Result();
        JsonObject applied = Applied;
        var appliedSpawners = applied["spawners"].AsObject();
        var appliedItems = applied["items"].AsObject();

        // --- spawners, from the ModernUO files the manifest lists ---
        var byGuid = new Dictionary<Guid, BaseSpawner>();
        foreach (var item in World.Items.Values)
        {
            if (item is BaseSpawner s && !s.Deleted)
            {
                byGuid[s.Guid] = s;
            }
        }

        var wanted = new HashSet<string>();
        foreach (JsonNode f in manifest["files"].AsArray())
        {
            string rel = (string)f;
            if (!rel.StartsWith("Data/Spawns/", StringComparison.Ordinal))
            {
                continue;
            }

            string path = Path.Combine(Core.BaseDirectory, rel);
            string text = File.ReadAllText(path);
            var records = JsonNode.Parse(text).AsArray();
            var dtos = JsonSerializer.Deserialize<List<SpawnerDto>>(text, SpawnerJsonSerializer.Options);
            for (int i = 0; i < dtos.Count; i++)
            {
                SpawnerDto dto = dtos[i];
                string id = dto.Guid.ToString();
                wanted.Add(id);
                string hash = Hash(records[i].ToJsonString());

                if (byGuid.TryGetValue(dto.Guid, out BaseSpawner existing)
                    && (string)appliedSpawners[id] == hash
                    && existing.Map == dto.Map && existing.Location == dto.Location)
                {
                    result.SpawnersKept++;
                    continue;
                }

                bool had = existing != null;
                existing?.Delete();
                BaseSpawner spawner = dto.ToSpawner();
                spawner.MoveToWorld(dto.Location, dto.Map);
                spawner.Respawn();
                appliedSpawners[id] = hash;
                if (had)
                {
                    result.SpawnersChanged++;
                }
                else
                {
                    result.SpawnersAdded++;
                }
            }
        }

        foreach (var (id, _) in appliedSpawners.ToList())
        {
            if (!wanted.Contains(id))
            {
                if (byGuid.TryGetValue(Guid.Parse(id), out BaseSpawner gone))
                {
                    gone.Delete();
                    result.SpawnersDeleted++;
                }

                appliedSpawners.Remove(id);
            }
        }

        // --- items, from the manifest (the cfg is for shards without the bridge) ---
        var wantedItems = new HashSet<string>();
        foreach (JsonNode m in manifest["items"].AsArray())
        {
            string id = (string)m["id"];
            wantedItems.Add(id);
            Map map = Map.Parse((string)m["map"]);
            var loc = m["location"].AsArray();
            var at = new Point3D((int)loc[0], (int)loc[1], (int)loc[2]);
            int itemId = Convert.ToInt32((string)m["item_id"], 16);
            int hue = Convert.ToInt32((string)m["hue"] ?? "0x0", 16);
            string type = (string)m["type"] ?? "Static";
            if (type != "Static")
            {
                logger.Warning("GUO editor bridge: item {0} is a {1}; only Static is synced yet", id, type);
                continue;
            }

            Item existing = appliedItems[id]?["serial"] is JsonNode sn ? World.FindItem((Serial)(uint)sn) : null;
            if (existing is { Deleted: false })
            {
                if (existing.Map == map && existing.Location == at && existing.ItemID == itemId && existing.Hue == hue)
                {
                    result.ItemsKept++;
                    continue;
                }

                existing.ItemID = itemId;
                existing.Hue = hue;
                existing.MoveToWorld(at, map);
                result.ItemsChanged++;
            }
            else
            {
                existing = new Static(itemId) { Hue = hue };
                existing.MoveToWorld(at, map);
                result.ItemsAdded++;
            }

            appliedItems[id] = new JsonObject { ["serial"] = (uint)existing.Serial };
        }

        foreach (var (id, node) in appliedItems.ToList())
        {
            if (!wantedItems.Contains(id))
            {
                if (World.FindItem((Serial)(uint)node["serial"]) is { Deleted: false } gone)
                {
                    gone.Delete();
                    result.ItemsDeleted++;
                }

                appliedItems.Remove(id);
            }
        }

        // Saved with the world at the next world save.
        return result;
    }

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
