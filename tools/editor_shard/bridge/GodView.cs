// The Admin tab's god view (AD2a): every player, NPC and spawner on one facet,
// pushed to the editor as they change. Read-only: it changes nothing.
//
// Request:  {"op":"admin_godview","facet":0,"watch":true,"req":7}
//           watch (default true) keeps the editor subscribed: after the reply
//           it gets a push a second at most, only when something changed.
//           {"op":"admin_godview","watch":false} without a facet ends it.
// Reply:    {"op":"admin_godview","req":7,"ok":true,"facet":0,"facets":[..],"full":true,"seq":1,
//            "players":3,"npcs":812,"spawners":64,"truncated":{"mobiles":false,"spawners":false},
//            "upsert":[rows],"removed":[]}
// Push:     the same without req and facets, "full":false, only rows that are new
//           or changed in upsert and the serials that left the facet in removed.
//           Both carry "ms": the game-thread time spent reading the facet and diffing.
// Rows:     {"serial":..,"kind":"player"|"npc","name":..,"x","y","z","body","hits","maxHits",
//            "notoriety","type", optional "online","staff","hidden","spawner":<serial>}
//           {"serial":..,"kind":"spawner","name":..,"x","y","z","running","count","spawned",
//            "homeRange","nextSpawn":"..Z"|null,"entries":[{"name","max","spawned"}],"moreEntries"}
//
// Find:     {"op":"admin_godview_find","text":"orc","req":8}
//           -> {"op":"admin_godview_find","req":8,"ok":true,"text":"orc","truncated":false,
//               "matches":[{"serial","kind","name","facet","x","y","z"}]} over every facet.
//
// Caps, as LiveMobiles keeps them (ADR-0027), sized for a whole facet (the dev
// shard's Felucca holds about 14,000 NPCs and 2,000 spawners): MaxMobiles
// players and NPCs per facet (players first, then NPCs by serial), MaxSpawners
// spawners, a push a second at most, FindMax matches. Each push hashes every
// entity's row and builds JSON only for those whose hash moved (GodViewDiff).
// Spawners are found by a scan of the world's items, at most every
// SpawnerScanMs; their state is read each push.

using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Server;
using Server.Engines.Spawners;
using Server.Mobiles;

namespace GUO.EditorBridge;

internal static class GodView
{
    public const int MaxMobiles = 25_000;
    public const int MaxSpawners = 10_000;
    public const int PushIntervalMs = 1000;
    public const int SpawnerScanMs = 10_000;
    public const int FindMax = 50;
    public const int MaxEntries = 8;

    private sealed class Watcher
    {
        public int Facet;
        public Action<JsonObject> Send;
        public readonly GodViewDiff Diff = new();
    }

    // Game thread only.
    private static readonly Dictionary<object, Watcher> _watchers = new();
    private static readonly Dictionary<Map, (long At, List<BaseSpawner> List)> _spawners = new();
    private static Timer _timer;

    /// <summary>Every facet the server has, for the tab's facet list.</summary>
    public static JsonArray Facets()
    {
        var list = new JsonArray();
        foreach (Map m in Map.Maps)
        {
            if (m != null && m != Map.Internal)
            {
                list.Add(new JsonObject
                {
                    ["id"] = m.MapIndex, ["map"] = m.MapID, ["name"] = m.Name, ["width"] = m.Width, ["height"] = m.Height,
                });
            }
        }

        return list;
    }

    private static Map MapOf(int facet) =>
        facet >= 0 && facet < Map.Maps.Length && Map.Maps[facet] is { } m && m != Map.Internal ? m : null;

    /// <summary>
    /// admin_godview: fills <paramref name="reply"/> with the whole facet and, when
    /// asked, keeps <paramref name="key"/> subscribed. False when the reply is a refusal.
    /// </summary>
    public static bool Request(object key, JsonNode msg, JsonObject reply, Action<JsonObject> send)
    {
        bool watch = (bool?)msg["watch"] ?? true;
        if (msg["facet"] == null && !watch)
        {
            _watchers.Remove(key);
            reply["watching"] = false;
            return true;
        }

        int facet = (int?)msg["facet"] ?? 0;
        Map map = MapOf(facet);
        if (map == null)
        {
            reply["ok"] = false;
            reply["error"] = $"no facet {facet}";
            return false;
        }

        var watcher = new Watcher { Facet = facet, Send = send };
        var watch0 = System.Diagnostics.Stopwatch.StartNew();
        Snapshot snap = Take(map, forceScan: true);
        (JsonArray upsert, JsonArray removed) = watcher.Diff.Next(snap.Rows, Build);
        reply["facet"] = facet;
        reply["facets"] = Facets();
        reply["full"] = true;
        reply["watching"] = watch;
        Fill(reply, snap, watcher.Diff.Seq, upsert, removed);
        reply["ms"] = watch0.ElapsedMilliseconds;
        if (watch)
        {
            _watchers[key] = watcher;
            _timer ??= Timer.DelayCall(TimeSpan.FromMilliseconds(PushIntervalMs), TimeSpan.FromMilliseconds(PushIntervalMs), Push);
        }
        else
        {
            _watchers.Remove(key);
        }

        return true;
    }

    /// <summary>Ends a subscription (the editor left). Game thread.</summary>
    public static void Forget(object key) => _watchers.Remove(key);

    private static void Push()
    {
        if (_watchers.Count == 0)
        {
            _timer?.Stop();
            _timer = null;
            return;
        }

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var snaps = new Dictionary<int, Snapshot>();
        foreach (Watcher w in _watchers.Values)
        {
            Map map = MapOf(w.Facet);
            if (map == null)
            {
                continue;
            }

            if (!snaps.TryGetValue(w.Facet, out Snapshot snap))
            {
                snaps[w.Facet] = snap = Take(map, forceScan: false);
            }

            (JsonArray upsert, JsonArray removed) = w.Diff.Next(snap.Rows, Build);
            if (upsert.Count == 0 && removed.Count == 0)
            {
                continue;
            }

            var push = new JsonObject { ["op"] = "admin_godview", ["ok"] = true, ["facet"] = w.Facet, ["full"] = false };
            Fill(push, snap, w.Diff.Seq, upsert, removed);
            // The game-thread time of this push so far (the facet read and the diff), for the checks.
            push["ms"] = clock.ElapsedMilliseconds;
            w.Send(push);
        }
    }

    private static void Fill(JsonObject msg, Snapshot snap, long seq, JsonArray upsert, JsonArray removed)
    {
        msg["seq"] = seq;
        msg["players"] = snap.Players;
        msg["npcs"] = snap.Npcs;
        msg["spawners"] = snap.Spawners;
        msg["truncated"] = new JsonObject { ["mobiles"] = snap.MobilesTruncated, ["spawners"] = snap.SpawnersTruncated };
        msg["upsert"] = upsert;
        msg["removed"] = removed;
    }

    private sealed class Snapshot
    {
        public List<GodViewEntry> Rows;
        public int Players, Npcs, Spawners;
        public bool MobilesTruncated, SpawnersTruncated;
    }

    private static Snapshot Take(Map map, bool forceScan)
    {
        var players = new List<GodViewEntry>();
        var npcs = new List<GodViewEntry>();
        foreach (Mobile m in World.Mobiles.Values)
        {
            if (m.Map != map || m.Deleted)
            {
                continue;
            }

            (m is PlayerMobile ? players : npcs).Add(new GodViewEntry((uint)m.Serial, MobileSignature(m), m));
        }

        var snap = new Snapshot { Players = players.Count, Npcs = npcs.Count };
        List<GodViewEntry> rows = GodViewDiff.Cap(players, MaxMobiles, out bool cut);
        rows = new List<GodViewEntry>(rows);
        rows.AddRange(GodViewDiff.Cap(npcs, Math.Max(0, MaxMobiles - rows.Count), out bool cutNpcs));
        snap.MobilesTruncated = cut || cutNpcs;

        var spawnerRows = new List<GodViewEntry>();
        foreach (BaseSpawner s in SpawnersOn(map, forceScan))
        {
            if (!s.Deleted && s.Map == map && s.Parent == null)
            {
                spawnerRows.Add(new GodViewEntry((uint)s.Serial, SpawnerSignature(s), s));
            }
        }

        snap.Spawners = spawnerRows.Count;
        rows.AddRange(GodViewDiff.Cap(spawnerRows, MaxSpawners, out bool cutSpawners));
        snap.SpawnersTruncated = cutSpawners;
        snap.Rows = rows;
        return snap;
    }

    // The spawners on a map, from a scan of every item at most every SpawnerScanMs.
    private static List<BaseSpawner> SpawnersOn(Map map, bool forceScan)
    {
        long now = Environment.TickCount64;
        if (!forceScan && _spawners.TryGetValue(map, out var cached) && now - cached.At < SpawnerScanMs)
        {
            return cached.List;
        }

        var list = new List<BaseSpawner>();
        foreach (Item item in World.Items.Values)
        {
            if (item is BaseSpawner s && s.Map == map && !s.Deleted)
            {
                list.Add(s);
            }
        }

        _spawners[map] = (now, list);
        return list;
    }

    private static JsonObject Build(object source) => source is Mobile m ? MobileRow(m) : SpawnerRow((BaseSpawner)source);

    private static int NotorietyOf(Mobile m)
    {
        try
        {
            return Notoriety.Compute(null, m);
        }
        catch (Exception)
        {
            return Notoriety.CanBeAttacked;
        }
    }

    // A hash of the fields MobileRow writes: equal while the row would be. Notoriety is the dear one to compute,
    // so it is hashed for players only (whose flags change); an NPC's is sent whenever its row is rebuilt.
    private static int MobileSignature(Mobile m)
    {
        var h = new HashCode();
        h.Add(m.X);
        h.Add(m.Y);
        h.Add(m.Z);
        h.Add(m.Hits);
        h.Add(m.HitsMax);
        h.Add(m is PlayerMobile ? NotorietyOf(m) : 0);
        h.Add(m.Name ?? m.RawName);
        h.Add((int)m.Body);
        h.Add(m.NetState != null);
        h.Add(m.AccessLevel);
        h.Add(m.Hidden);
        h.Add(m is ISpawnable { Spawner: BaseSpawner sp } ? (uint)sp.Serial : 0u);
        return h.ToHashCode();
    }

    // A hash of every field SpawnerRow writes.
    private static int SpawnerSignature(BaseSpawner s)
    {
        var h = new HashCode();
        h.Add(s.X);
        h.Add(s.Y);
        h.Add(s.Z);
        h.Add(s.Name);
        h.Add(s.Running);
        h.Add(s.Count);
        h.Add(s.Spawned?.Count ?? 0);
        h.Add(s.HomeRange);
        h.Add(s.NextSpawn > TimeSpan.Zero ? s.End : default);
        IReadOnlyList<SpawnerEntry> all = s.Entries;
        h.Add(all.Count);
        for (int i = 0; i < all.Count && i < MaxEntries; i++)
        {
            h.Add(all[i].SpawnedName);
            h.Add(all[i].SpawnedMaxCount);
            h.Add(all[i].Spawned?.Count ?? 0);
        }

        return h.ToHashCode();
    }

    private static JsonObject MobileRow(Mobile m)
    {
        int noto = NotorietyOf(m);
        var row = new JsonObject
        {
            ["serial"] = (uint)m.Serial,
            ["kind"] = m is PlayerMobile ? "player" : "npc",
            ["name"] = m.Name ?? m.RawName ?? "",
            ["x"] = m.X, ["y"] = m.Y, ["z"] = m.Z,
            ["body"] = (int)m.Body,
            ["hits"] = m.Hits,
            ["maxHits"] = m.HitsMax,
            ["notoriety"] = noto,
            ["type"] = m.GetType().Name,
        };
        if (m is PlayerMobile)
        {
            row["online"] = m.NetState != null;
        }

        if (m.AccessLevel > AccessLevel.Player)
        {
            row["staff"] = m.AccessLevel.ToString();
        }

        if (m.Hidden)
        {
            row["hidden"] = true;
        }

        if (m is ISpawnable { Spawner: BaseSpawner spawner })
        {
            row["spawner"] = (uint)spawner.Serial;
        }

        return row;
    }

    private static JsonObject SpawnerRow(BaseSpawner s)
    {
        var entries = new JsonArray();
        IReadOnlyList<SpawnerEntry> all = s.Entries;
        for (int i = 0; i < all.Count && i < MaxEntries; i++)
        {
            SpawnerEntry e = all[i];
            entries.Add(new JsonObject { ["name"] = e.SpawnedName, ["max"] = e.SpawnedMaxCount, ["spawned"] = e.Spawned?.Count ?? 0 });
        }

        // NextSpawn counts down; the time it points at stays put, so an idle spawner sends nothing.
        TimeSpan next = s.NextSpawn;
        return new JsonObject
        {
            ["serial"] = (uint)s.Serial,
            ["kind"] = "spawner",
            ["name"] = s.Name ?? s.GetType().Name,
            ["x"] = s.X, ["y"] = s.Y, ["z"] = s.Z,
            ["running"] = s.Running,
            ["count"] = s.Count,
            ["spawned"] = s.Spawned?.Count ?? 0,
            ["homeRange"] = s.HomeRange,
            ["nextSpawn"] = next > TimeSpan.Zero ? s.End.ToString("yyyy-MM-ddTHH:mm:ssZ") : null,
            ["entries"] = entries,
            ["moreEntries"] = Math.Max(0, all.Count - MaxEntries),
        };
    }

    /// <summary>admin_godview_find: players, NPCs and spawners on every facet whose name (or serial) matches.</summary>
    public static void Find(JsonNode msg, JsonObject reply)
    {
        string text = ((string)msg["text"] ?? "").Trim();
        reply["text"] = text;
        var matches = new JsonArray();
        bool truncated = false;
        if (text.Length > 0)
        {
            foreach (Mobile m in World.Mobiles.Values)
            {
                if (m.Deleted || m.Map == null || m.Map == Map.Internal
                    || !GodViewDiff.Matches(text, (uint)m.Serial, m.Name, m.RawName, m.GetType().Name))
                {
                    continue;
                }

                if (!Add(matches, m.Map, (uint)m.Serial, m is PlayerMobile ? "player" : "npc", m.Name ?? m.RawName, m.Location))
                {
                    truncated = true;
                    break;
                }
            }

            foreach (Item item in World.Items.Values)
            {
                if (truncated)
                {
                    break;
                }

                if (item is not BaseSpawner s || s.Deleted || s.Map == null || s.Map == Map.Internal)
                {
                    continue;
                }

                var names = new List<string> { s.Name, s.GetType().Name };
                foreach (SpawnerEntry e in s.Entries)
                {
                    names.Add(e.SpawnedName);
                }

                if (GodViewDiff.Matches(text, (uint)s.Serial, names.ToArray())
                    && !Add(matches, s.Map, (uint)s.Serial, "spawner", s.Name ?? s.GetType().Name, s.Location))
                {
                    truncated = true;
                }
            }
        }

        reply["truncated"] = truncated;
        reply["matches"] = matches;
    }

    private static bool Add(JsonArray matches, Map map, uint serial, string kind, string name, Point3D at)
    {
        if (matches.Count >= FindMax)
        {
            return false;
        }

        matches.Add(new JsonObject
        {
            ["serial"] = serial, ["kind"] = kind, ["name"] = name ?? "", ["facet"] = map.MapIndex,
            ["x"] = at.X, ["y"] = at.Y, ["z"] = at.Z,
        });
        return true;
    }
}
