// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GUO.Store;
using Server;
using Server.Commands;
using Server.Engines.Spawners;

namespace GUO.EditorBridge;

internal sealed class ContentSpawners
{
    private readonly Dictionary<string, (JsonElement Content, string Recipe, string Hash)> _sets = new(StringComparer.Ordinal);
    private static JsonObject Owned => (WorldObjectsSync.Applied["content_spawners"] ??= new JsonObject()).AsObject();
    private static bool Identity(string value) => value != null && System.Text.RegularExpressions.Regex.IsMatch(value, @"\A[a-z0-9][a-z0-9-]{0,63}:[a-z0-9][a-z0-9-]{0,63}\z");

    public static ContentSpawners Stage(JsonElement root, ContentCreatures creatures)
    {
        var result = new ContentSpawners();
        if (!root.TryGetProperty("spawners", out var rows)) return result;
        if (rows.GetArrayLength() > 128) throw new InvalidDataException("Too many pack spawners");
        foreach (var row in rows.EnumerateArray())
        {
            string id = row.GetProperty("identity").GetString();
            if (!Identity(id)) throw new InvalidDataException("Invalid spawner identity");
            var content = row.GetProperty("content");
            StoreSpawnerDefinition.Validate(content, creatures.Contains);
            int facet = content.GetProperty("facet").GetInt32(), radius = content.GetProperty("radius").GetInt32();
            int x = content.GetProperty("x").GetInt32(), y = content.GetProperty("y").GetInt32();
            if (facet >= Map.Maps.Length || Map.Maps[facet] == null || Map.Maps[facet] == Map.Internal
                || x - radius < 0 || y - radius < 0 || x + radius >= Map.Maps[facet].Width || y + radius >= Map.Maps[facet].Height)
                throw new InvalidDataException("Spawner area outside facet");
            string recipe = creatures.SpawnRecipe(content.GetProperty("creature").GetString());
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content.GetRawText() + recipe)));
            result._sets.Add(id, (content.Clone(), recipe, hash));
        }
        return result;
    }

    private ContentSpawner Apply(string identity, JsonObject owned)
    {
        var definition = _sets[identity]; var row = definition.Content;
        var map = Map.Maps[row.GetProperty("facet").GetInt32()];
        var location = new Point3D(row.GetProperty("x").GetInt32(), row.GetProperty("y").GetInt32(), row.GetProperty("z").GetInt32());
        var previous = Find(identity, owned);
        if (previous != null && previous.ContentHash == definition.Hash && previous.Location == location && previous.Map == map) return previous;
        var spawner = new ContentSpawner(identity, definition.Hash);
        try
        {
            int radius = row.GetProperty("radius").GetInt32();
            spawner.InitSpawn(row.GetProperty("count").GetInt32(), TimeSpan.FromSeconds(row.GetProperty("min_delay").GetInt32()),
                TimeSpan.FromSeconds(row.GetProperty("max_delay").GetInt32()), row.GetProperty("team").GetInt32(),
                new Rectangle3D(location.X - radius, location.Y - radius, -128, radius * 2 + 1, radius * 2 + 1, 256));
            spawner.AddEntry(typeof(ContentCreature).FullName, amount: row.GetProperty("count").GetInt32(), dotimer: false, parameters: definition.Recipe);
            spawner.MoveToWorld(location, map);
            previous?.Delete();
            owned[identity] = new JsonObject { ["serial"] = (uint)spawner.Serial };
            spawner.Respawn();
            return spawner;
        }
        catch { spawner.Delete(); throw; }
    }

    private static ContentSpawner Find(string identity, JsonObject owned)
    {
        if (owned[identity] == null) return null;
        var item = World.FindItem((Serial)(uint)owned[identity]["serial"]);
        if (item == null || item.Deleted) return null;
        if (item is not ContentSpawner spawner || spawner.ContentIdentity != identity)
            throw new InvalidDataException("Spawner ownership record does not match its saved object");
        return spawner;
    }
    private static void Remove(string identity, JsonObject owned) { Find(identity, owned)?.Delete(); owned.Remove(identity); }

    public void Register(bool probe)
    {
        CommandSystem.Register("GUOPackSpawn", AccessLevel.Administrator, args =>
        {
            if (args.Length != 1 || !_sets.ContainsKey(args.GetString(0))) { args.Mobile.SendMessage("Usage: GUOPackSpawn pack-id:component-id"); return; }
            Apply(args.GetString(0), Owned); args.Mobile.SendMessage("Spawner applied. Save the world to persist it.");
        });
        CommandSystem.Register("GUOPackUnspawn", AccessLevel.Administrator, args =>
        {
            if (args.Length != 1 || !Identity(args.GetString(0))) { args.Mobile.SendMessage("Usage: GUOPackUnspawn pack-id:component-id"); return; }
            Remove(args.GetString(0), Owned); args.Mobile.SendMessage("Owned spawner removed. Save the world to persist it.");
        });
        if (!probe || _sets.Count == 0) return;
        Server.Timer.DelayCall(TimeSpan.Zero, () =>
        {
            foreach (string id in _sets.Keys)
            {
                var scratch = new JsonObject();
                var spawner = Apply(id, scratch);
                try
                {
                    var first = spawner.Spawned.Keys.ToArray();
                    if (first.Length != spawner.Count || first.Any(m => m is not ContentCreature)) throw new InvalidDataException("Pack spawner failed to fill its population");
                    if (Apply(id, scratch) != spawner || !first.All(m => spawner.Spawned.ContainsKey(m))) throw new InvalidDataException("Repeat application replaced live spawns");
                    first[0].Delete();
                    spawner.OnTick();
                    if (spawner.Spawned.Count != spawner.Count || spawner.Spawned.ContainsKey(first[0])) throw new InvalidDataException("Native spawner did not replace a missing creature");
                    var live = spawner.Spawned.Keys.ToArray();
                    Remove(id, scratch);
                    if (!spawner.Deleted || scratch.Count != 0 || live.Any(m => !m.Deleted)) throw new InvalidDataException("Spawner removal leaked owned creatures");
                }
                finally { Remove(id, scratch); }
            }
            Console.WriteLine($"[GUO content] PASS: {_sets.Count} native spawners filled, reapplied, replaced missing creatures, and cleaned up.");
        });
    }
}

public sealed class ContentSpawner : Spawner
{
    public string ContentIdentity { get; private set; }
    public string ContentHash { get; private set; }
    public ContentSpawner(Serial serial) : base(serial) { }
    internal ContentSpawner(string identity, string hash) { ContentIdentity = identity; ContentHash = hash; }
    public override void Serialize(IGenericWriter writer) { base.Serialize(writer); writer.WriteEncodedInt(0); writer.Write(ContentIdentity); writer.Write(ContentHash); }
    public override void Deserialize(IGenericReader reader)
    {
        base.Deserialize(reader);
        if (reader.ReadEncodedInt() != 0) throw new InvalidDataException("Unsupported content spawner save version");
        ContentIdentity = reader.ReadString(); ContentHash = reader.ReadString();
    }
}
