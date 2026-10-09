// GUO editor bridge: a ModernUO assembly for the editor's live tier (ADR-0012).
//
// Loaded by ModernUO from Assemblies/ when listed in Data/assemblies.json. Only
// tools/editor_shard installs it, into the PRIVATE instance; the shared dev
// shard never loads it.
//
// It does three things:
//   1. On login, makes the client an UltimaLive client: 0x3F/0x02 (shard name),
//      0x3F/0x01 (map definitions), then every block changed since boot.
//   2. Listens for editors on 127.0.0.1 (line-delimited JSON). A "block" message
//      replaces an 8x8 block in the server's own TileMatrix (on the game
//      thread), is pushed to UltimaLive clients on that map (0x40 terrain, then
//      0x3F/0x00 statics, the order the client asks for), and is relayed to the
//      other editors: last write per block wins, and each relay names its author.
//   3. A "command" message runs a GM command as a named online character, via
//      CommandSystem.Handle, exactly as if they had typed it.
//   4. A "multi" message places or removes an authored multi and its doors
//      (AuthoredMulti.cs, tools/multi).
//   5. Admin ops (AdminChannel.cs, ADR-0035) run only for a connection whose
//      hello carried the server's admin token, each at a stated access level,
//      and each is written to the admin audit log.
//
// Live edits are held in memory. The world project (the editor's files) is the
// source of truth; tools/world export + a restart make them permanent.
//
// Packet layouts follow the client, godot/GUO/src/Game/UltimaLive.cs.

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using Server;
using Server.Logging;
using Server.Network;

namespace GUO.EditorBridge;

public static class EditorBridge
{
    private static readonly ILogger Log = LogFactory.GetLogger(typeof(EditorBridge));

    private static string _shardName = "GUO-Editor-Private";
    private static int[] _ulMaps = { 0 };
    private static int _port = 2595;

    // Blocks changed since boot: (map, block number) -> (land 192 bytes, statics 7-byte records).
    private static readonly Dictionary<(int, int), (byte[] Land, byte[] Statics)> _changed = new();
    private static readonly HashSet<NetState> _ulClients = new();
    private static readonly List<EditorConnection> _editors = new();
    private static AdminChannel _admin = new(null, AdminLevel.Administrator, new AuditLog(null));

    public static void Configure()
    {
        _shardName = Environment.GetEnvironmentVariable("GUO_BRIDGE_SHARD") is { Length: > 0 } n ? n : _shardName;
        if (int.TryParse(Environment.GetEnvironmentVariable("GUO_BRIDGE_PORT"), out int p))
        {
            _port = p;
        }

        if (Environment.GetEnvironmentVariable("GUO_BRIDGE_MAPS") is { Length: > 0 } maps)
        {
            _ulMaps = maps.Split(',').Select(int.Parse).ToArray();
        }

        // The audit log sits with the server's own logs, never in Saves.
        _admin = AdminChannel.FromEnvironment(Path.Combine(Core.BaseDirectory, "Logs", "GUO", "admin_audit.jsonl"));

        EventSink.Connected += OnConnected;
        EventSink.Disconnected += m =>
        {
            // On a dropped connection this fires from Mobile.set_NetState(null),
            // so m.NetState is already null (a Dictionary key must not be:
            // that crashed the shard when a client was ended). Find the
            // entries by their mobile instead.
            bool Gone(NetState ns) => ns == null || ns == m?.NetState || ns.Mobile == m;
            _ulClients.RemoveWhere(Gone);
            foreach (NetState ns in _setUpOn.Keys.Where(Gone).ToList())
            {
                _setUpOn.Remove(ns);
            }

            foreach (NetState ns in _queried.Keys.Where(Gone).ToList())
            {
                _queried.Remove(ns);
            }
        };

        unsafe
        {
            // The client answers a hash query with its own 0x3F (65 bytes).
            // ModernUO has no handler for 0x3F; without one the answer is an
            // unknown packet.
            IncomingPackets.Register(0x3F, 0, true, &OnHashResponse);
        }
    }

    // Per UltimaLive client: the map it was last set up on, and the maps it
    // has been asked for hashes on. Game thread only.
    private static readonly Dictionary<NetState, int> _setUpOn = new();
    private static readonly Dictionary<NetState, HashSet<int>> _queried = new();

    /// <summary>
    /// Readies a client for the map its mobile is on, and returns how many
    /// changed blocks it was sent. The client builds its per-map CRC table
    /// only when the server asks for hashes on that map (0x3F/0xFF); an update
    /// (0x40, 0x3F/0x00) on a map never queried dereferences that table and
    /// throws (UltimaLive.cs). So the first time a client is on a map it is
    /// asked once, for the block the player stands in. Then it is sent every
    /// block changed on that map since boot, which it missed while elsewhere.
    /// </summary>
    private static int SetUpMap(NetState ns, Mobile m)
    {
        if (m?.Map == null || !_ulMaps.Contains(m.Map.MapID))
        {
            return 0;
        }

        int mapId = m.Map.MapID;
        if (!_queried.TryGetValue(ns, out HashSet<int> asked))
        {
            _queried[ns] = asked = new HashSet<int>();
        }

        if (asked.Add(mapId))
        {
            int here = (m.X >> 3) * (m.Map.Height >> 3) + (m.Y >> 3);
            ns.Send(Header(15, (uint)here, 0, 0xFF, (byte)mapId));
        }

        _setUpOn[ns] = mapId;
        int sent = 0;
        foreach (var ((map, block), data) in _changed)
        {
            if (map == mapId)
            {
                SendBlock(ns, map, block, data.Land, data.Statics);
                sent++;
            }
        }

        return sent;
    }

    /// <summary>
    /// ModernUO has no map-change event: once a second, a client whose mobile
    /// has moved to another map is set up there.
    /// </summary>
    private static void CheckMaps()
    {
        foreach (NetState ns in _ulClients.ToArray())
        {
            Mobile m = ns.Mobile;
            if (m?.Map != null && (!_setUpOn.TryGetValue(ns, out int on) || on != m.Map.MapID))
            {
                int sent = SetUpMap(ns, m);
                if (_setUpOn.TryGetValue(ns, out on) && on == m.Map.MapID)
                {
                    Log.Information("GUO editor bridge: {0} now on map{1} ({2} changed block(s) sent)", m.RawName, on, sent);
                }
            }
        }
    }

    private static void OnHashResponse(NetState ns, SpanReader reader)
    {
        // Nothing to do with the CRCs yet: the bridge pushes changed blocks
        // itself rather than comparing hashes. The query only has to happen.
    }

    public static void Initialize()
    {
        Server.Timer.StartTimer(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), CheckMaps);
        var thread = new Thread(Listen) { IsBackground = true, Name = "GUO editor bridge" };
        thread.Start();
        Log.Information("GUO editor bridge: editors on 127.0.0.1:{0}, UltimaLive shard '{1}', maps {2}", _port, _shardName, string.Join(",", _ulMaps));
        Log.Information(
            _admin.Enabled
                ? "GUO editor bridge: admin channel on (the token grants {0}; audit log {1})"
                : "GUO editor bridge: admin channel off (no admin token; map editing only){0}{1}",
            _admin.Enabled ? _admin.Grants.ToString() : "", _admin.Enabled ? _admin.Audit.Path : ""
        );
    }

    // --- UltimaLive to game clients ----------------------------------------

    private static void OnConnected(Mobile m)
    {
        // Connected fires before the login packets; UltimaLive goes after them.
        Server.Timer.DelayCall(TimeSpan.FromSeconds(2), () =>
        {
            NetState ns = m?.NetState;
            if (ns == null)
            {
                return;
            }

            ns.Send(LoginPacket());
            ns.Send(MapDefinitionsPacket());

            _ulClients.Add(ns);
            int sent = SetUpMap(ns, m);

            Log.Information("GUO editor bridge: UltimaLive on for {0} ({1} changed block(s) sent)", m.RawName, sent);
        });
    }

    private static byte[] Header(int length, uint block, uint field7, byte command, byte mapId)
    {
        var b = new byte[length];
        b[0] = 0x3F;
        b[1] = (byte)(length >> 8);
        b[2] = (byte)length;
        b[3] = (byte)(block >> 24);
        b[4] = (byte)(block >> 16);
        b[5] = (byte)(block >> 8);
        b[6] = (byte)block;
        b[7] = (byte)(field7 >> 24);
        b[8] = (byte)(field7 >> 16);
        b[9] = (byte)(field7 >> 8);
        b[10] = (byte)field7;
        b[13] = command;
        b[14] = mapId;
        return b;
    }

    // 0x3F/0x02: activates UltimaLive; the name (offset 15, ASCII) names the client's copy folder.
    private static byte[] LoginPacket()
    {
        byte[] b = Header(43, 0, 0, 0x02, 0);
        byte[] name = Encoding.ASCII.GetBytes(_shardName);
        Array.Copy(name, 0, b, 15, Math.Min(name.Length, 27));
        return b;
    }

    // 0x3F/0x01: N records of 9 bytes from offset 15; the client reads N = V*7/9 from offset 7.
    private static byte[] MapDefinitionsPacket()
    {
        var maps = _ulMaps.Select(i => Map.Maps[i]).Where(m => m != null).ToList();
        int n = maps.Count;
        uint v = (uint)((n * 9 + 6) / 7);
        byte[] b = Header(15 + n * 9, 0, v, 0x01, 0);
        int at = 15;
        foreach (Map m in maps)
        {
            b[at] = (byte)m.MapID;
            Put16(b, at + 1, m.Width);
            Put16(b, at + 3, m.Height);
            Put16(b, at + 5, m.Width);
            Put16(b, at + 7, m.Height);
            at += 9;
        }

        return b;
    }

    private static void Put16(byte[] b, int at, int v)
    {
        b[at] = (byte)(v >> 8);
        b[at + 1] = (byte)v;
    }

    private static void SendBlock(NetState ns, int map, int block, byte[] land, byte[] statics)
    {
        // Terrain first (0x40: fixed 201 bytes), then statics (0x3F/0x00).
        var t = new byte[201];
        t[0] = 0x40;
        t[1] = (byte)(block >> 24);
        t[2] = (byte)(block >> 16);
        t[3] = (byte)(block >> 8);
        t[4] = (byte)block;
        Array.Copy(land, 0, t, 5, 192);
        t[200] = (byte)map;
        ns.Send(t);

        int count = statics.Length / 7;
        byte[] s = Header(15 + statics.Length, (uint)block, (uint)count, 0x00, (byte)map);
        Array.Copy(statics, 0, s, 15, statics.Length);
        ns.Send(s);
    }

    // --- editors ----------------------------------------------------------

    private sealed class EditorConnection
    {
        public TcpClient Client;
        public StreamWriter Writer;
        public string Name = "?";
        public long LastMobilesMs;
        // The access level the admin token granted in hello (ADR-0035); null: map editing only.
        public AdminLevel? Admin;
        public int AdminRefusals;
        private readonly object _lock = new();

        public void Send(JsonObject msg)
        {
            lock (_lock)
            {
                try
                {
                    Writer.WriteLine(msg.ToJsonString());
                    Writer.Flush();
                }
                catch (Exception)
                {
                    // A closed editor drops out on its reader thread.
                }
            }
        }
    }

    private static void Listen()
    {
        var listener = new TcpListener(IPAddress.Loopback, _port);
        listener.Start();
        while (true)
        {
            TcpClient c = listener.AcceptTcpClient();
            var t = new Thread(() => Serve(c)) { IsBackground = true, Name = "GUO editor bridge client" };
            t.Start();
        }
    }

    private static void Serve(TcpClient client)
    {
        var conn = new EditorConnection
        {
            Client = client,
            Writer = new StreamWriter(client.GetStream(), new UTF8Encoding(false)) { NewLine = "\n" },
        };
        lock (_editors)
        {
            _editors.Add(conn);
        }

        try
        {
            using var reader = new StreamReader(client.GetStream(), Encoding.UTF8);
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                JsonNode msg;
                try
                {
                    msg = JsonNode.Parse(line);
                }
                catch (JsonException)
                {
                    conn.Send(new JsonObject { ["op"] = "error", ["error"] = "not JSON" });
                    continue;
                }

                string op = (string)msg["op"];
                if (op == "hello")
                {
                    conn.Name = (string)msg["editor"] ?? "?";
                    // The admin token, if offered (ADR-0035). Never logged:
                    // the audit entry masks it.
                    conn.Admin = _admin.CheckHello((string)msg["admin_token"], out string adminRefused);
                    if (adminRefused != null)
                    {
                        conn.AdminRefusals++;
                        _admin.Audit.Record(conn.Name, "hello", null, false, msg, adminRefused);
                        Log.Warning("GUO editor bridge: editor '{0}' was refused admin: {1}", conn.Name, adminRefused);
                        Thread.Sleep(AdminChannel.RefusalDelayMs);
                    }
                    // Each map's season, which the shard sends its clients: the
                    // editor draws the same seasonal art when it knows it.
                    var seasons = new JsonObject();
                    foreach (Map m in Map.Maps)
                    {
                        if (m != null && m != Map.Internal)
                        {
                            seasons[m.MapID.ToString()] = m.Season;
                        }
                    }

                    var reply = new JsonObject
                    {
                        ["op"] = "hello", ["shard"] = _shardName,
                        ["maps"] = new JsonArray(_ulMaps.Select(m => (JsonNode)m).ToArray()),
                        ["seasons"] = seasons,
                        ["admin"] = conn.Admin?.ToString(),
                    };
                    if (conn.Admin is { } level)
                    {
                        reply["admin_ops"] = _admin.OpsFor(level);
                        _admin.Audit.Record(conn.Name, "hello", level, true, msg);
                    }
                    else if (adminRefused != null)
                    {
                        reply["admin_error"] = adminRefused;
                    }

                    conn.Send(reply);
                    Log.Information("GUO editor bridge: editor '{0}' connected{1}", conn.Name, conn.Admin is { } l ? $" (admin, {l})" : "");
                    if (conn.AdminRefusals >= AdminChannel.MaxRefusals)
                    {
                        Log.Warning("GUO editor bridge: editor '{0}' closed after {1} refused admin tokens", conn.Name, conn.AdminRefusals);
                        break;
                    }
                }
                else if (AdminChannel.IsAdminOp(op))
                {
                    Core.LoopContext.Post(() => RunAdmin(conn, msg, op));
                }
                else if (op == "block")
                {
                    long received = Environment.TickCount64;
                    // The game thread owns the TileMatrix and the NetStates.
                    Core.LoopContext.Post(() => ApplyBlock(conn, msg, received));
                }
                else if (op == "command")
                {
                    Core.LoopContext.Post(() => RunCommand(conn, msg));
                }
                else if (op == "equip")
                {
                    Core.LoopContext.Post(() => Equip(conn, msg));
                }
                else if (op == "equip-many")
                {
                    Core.LoopContext.Post(() => EquipMany(conn, msg));
                }
                else if (op == "unequip")
                {
                    Core.LoopContext.Post(() => Unequip(conn, msg));
                }
                else if (op == "multi")
                {
                    Core.LoopContext.Post(() => AuthoredMultis.Handle(msg, conn.Send));
                }
                else if (op == "mobiles")
                {
                    int req = (int?)msg["req"] ?? 0;
                    long now = Environment.TickCount64;
                    if (now - conn.LastMobilesMs < LiveMobiles.MinIntervalMs)
                    {
                        conn.Send(LiveMobiles.Refuse(req, "rate limited"));
                        continue;
                    }

                    conn.LastMobilesMs = now;
                    bool hello = conn.Name != "?";
                    Core.LoopContext.Post(() =>
                    {
                        string refused = LiveMobiles.Authorise(msg, hello);
                        conn.Send(refused != null ? LiveMobiles.Refuse(req, refused) : LiveMobiles.Query(msg));
                    });
                }
                else if (op == "object")
                {
                    long received = Environment.TickCount64;
                    Core.LoopContext.Post(() => ApplyObject(conn, msg, received));
                }
            }
        }
        catch (IOException)
        {
        }
        finally
        {
            lock (_editors)
            {
                _editors.Remove(conn);
            }

            client.Close();
            Log.Information("GUO editor bridge: editor '{0}' left", conn.Name);
        }
    }

    // {"op":"block","facet":0,"bx":145,"by":208,"land":[[id,z]x64 row-major],"statics":[[id,x,y,z,hue],...]}
    private static void ApplyBlock(EditorConnection from, JsonNode msg, long received)
    {
        int facet = (int)msg["facet"], bx = (int)msg["bx"], by = (int)msg["by"];
        Map map = facet >= 0 && facet < Map.Maps.Length ? Map.Maps[facet] : null;
        if (map == null || map == Map.Internal)
        {
            from.Send(new JsonObject { ["op"] = "error", ["error"] = $"no map {facet}" });
            return;
        }

        var landTiles = new LandTile[64];
        var land = new byte[192];
        JsonArray cells = msg["land"].AsArray();
        for (int i = 0; i < 64; i++)
        {
            short id = (short)(int)cells[i][0];
            sbyte z = (sbyte)(int)cells[i][1];
            landTiles[i] = new LandTile(id, z);
            land[i * 3] = (byte)id;
            land[i * 3 + 1] = (byte)(id >> 8);
            land[i * 3 + 2] = (byte)z;
        }

        var lists = new List<StaticTile>[8][];
        for (int x = 0; x < 8; x++)
        {
            lists[x] = new List<StaticTile>[8];
            for (int y = 0; y < 8; y++)
            {
                lists[x][y] = new List<StaticTile>();
            }
        }

        var records = new MemoryStream();
        foreach (JsonNode s in msg["statics"].AsArray())
        {
            ushort id = (ushort)(int)s[0];
            byte x = (byte)(int)s[1], y = (byte)(int)s[2];
            sbyte z = (sbyte)(int)s[3];
            short hue = (short)(int)s[4];
            lists[x & 7][y & 7].Add(new StaticTile(id, x, y, z, hue));
            records.Write(new[] { (byte)id, (byte)(id >> 8), x, y, (byte)z, (byte)hue, (byte)(hue >> 8) });
        }

        var tiles = new StaticTile[8][][];
        for (int x = 0; x < 8; x++)
        {
            tiles[x] = new StaticTile[8][];
            for (int y = 0; y < 8; y++)
            {
                tiles[x][y] = lists[x][y].ToArray();
            }
        }

        // The server's own map: walking, line of sight and placement see it now.
        map.Tiles.SetLandBlock(bx, by, landTiles);
        map.Tiles.SetStaticBlock(bx, by, tiles);

        int block = bx * (map.Height >> 3) + by;
        byte[] statics = records.ToArray();
        _changed[(map.MapID, block)] = (land, statics);

        int clients = 0;
        foreach (NetState ns in NetState.Instances)
        {
            if (_ulClients.Contains(ns) && ns.Mobile?.Map == map)
            {
                if (!_setUpOn.TryGetValue(ns, out int on) || on != map.MapID)
                {
                    // Newly on this map: setting up sends every changed block
                    // of it, this one included.
                    SetUpMap(ns, ns.Mobile);
                }
                else
                {
                    SendBlock(ns, map.MapID, block, land, statics);
                }

                clients++;
            }
        }

        var relay = new JsonObject
        {
            ["op"] = "block",
            ["from"] = from.Name,
            ["facet"] = facet,
            ["bx"] = bx,
            ["by"] = by,
            ["land"] = JsonNode.Parse(msg["land"].ToJsonString()),
            ["statics"] = JsonNode.Parse(msg["statics"].ToJsonString()),
            // The sender's clock, so a receiving editor can measure the trip.
            ["sent_ms"] = msg["sent_ms"] is JsonNode sent ? JsonNode.Parse(sent.ToJsonString()) : null,
        };
        int editors = 0;
        lock (_editors)
        {
            foreach (EditorConnection e in _editors)
            {
                if (e != from)
                {
                    e.Send(relay);
                    editors++;
                }
            }
        }

        long ms = Environment.TickCount64 - received;
        from.Send(new JsonObject
        {
            ["op"] = "ack", ["facet"] = facet, ["bx"] = bx, ["by"] = by,
            ["clients"] = clients, ["editors"] = editors, ["ms"] = ms,
        });
        Log.Information(
            "GUO editor bridge: block map{0} {1},{2} from '{3}': {4} statics; sent to {5} client(s), relayed to {6} editor(s) in {7} ms",
            facet, bx, by, from.Name, statics.Length / 7, clients, editors, ms
        );
    }

    // {"op":"object","action":"put","kind":"spawner"|"item","object":{...neutral, data_formats 13...}}
    // {"op":"object","action":"delete","kind":"spawner"|"item","id":"<guid>"}
    // Applied with the same code as the boot sync (WorldObjectsSync, ADR-0014),
    // so the shard's clients see it through the server's own item packets, and
    // relayed to the other editors. Saved with the world at the next save; the
    // world project stays the source of truth.
    private static void ApplyObject(EditorConnection from, JsonNode msg, long received)
    {
        string action = (string)msg["action"];
        string kind = (string)msg["kind"];
        string id = action == "delete" ? (string)msg["id"] : (string)msg["object"]?["id"];
        WorldObjectsSync.Outcome outcome;
        try
        {
            outcome = (action, kind) switch
            {
                ("put", "spawner") => WorldObjectsSync.PutSpawner(WorldObjectsSync.SpawnerRecord(msg["object"])),
                ("put", "item") => WorldObjectsSync.PutItem(WorldObjectsSync.ItemManifest(msg["object"])),
                ("delete", "spawner") => WorldObjectsSync.DeleteSpawner(id),
                ("delete", "item") => WorldObjectsSync.DeleteItem(id),
                _ => throw new ArgumentException($"unknown object op {action}/{kind}"),
            };
        }
        catch (Exception ex)
        {
            from.Send(new JsonObject { ["op"] = "error", ["error"] = $"object {action} {kind} {id}: {ex.Message}" });
            return;
        }

        var relay = JsonNode.Parse(msg.ToJsonString()).AsObject();
        relay["from"] = from.Name;
        int editors = 0;
        lock (_editors)
        {
            foreach (EditorConnection e in _editors)
            {
                if (e != from)
                {
                    e.Send(relay);
                    editors++;
                }
            }
        }

        long ms = Environment.TickCount64 - received;
        from.Send(new JsonObject
        {
            ["op"] = "object_ack", ["action"] = action, ["kind"] = kind, ["id"] = id,
            ["outcome"] = outcome.ToString(), ["editors"] = editors, ["ms"] = ms,
        });
        Log.Information(
            "GUO editor bridge: object {0} {1} {2} from '{3}': {4}; relayed to {5} editor(s) in {6} ms",
            action, kind, id, from.Name, outcome, editors, ms
        );
    }

    // {"op":"equip","as":"<online character>","item_id":65520,"hue":0}
    // A new Item of that id, on the layer its tiledata names, equipped on the
    // character; whatever held that layer goes to the backpack. How a new
    // wearable (ADR-0022) is put on a character without a target cursor.
    private static Mobile Online(string who) =>
        NetState.Instances.Select(ns => ns.Mobile)
            .FirstOrDefault(x => x != null && string.Equals(x.RawName, who, StringComparison.OrdinalIgnoreCase));

    private static void Equip(EditorConnection from, JsonNode msg)
    {
        string who = (string)msg["as"];
        Mobile m = Online(who);
        if (m == null)
        {
            from.Send(new JsonObject { ["op"] = "equip", ["ok"] = false, ["error"] = $"'{who}' is not online" });
            return;
        }

        JsonObject r = EquipOne(m, (int)msg["item_id"], (int?)msg["hue"] ?? 0);
        r["op"] = "equip";
        from.Send(r);
        Log.Information("GUO editor bridge: '{0}' equipped {1} on {2}: {3}", from.Name, (string)r["item"], m.RawName, (bool)r["ok"]);
    }

    // {"op":"equip-many","as":"<online character>","item_ids":[65504, ...],"hue":0}
    // Each in turn, as "equip" does: whatever held its layer goes to the backpack.
    private static void EquipMany(EditorConnection from, JsonNode msg)
    {
        string who = (string)msg["as"];
        Mobile m = Online(who);
        if (m == null)
        {
            from.Send(new JsonObject { ["op"] = "equip-many", ["ok"] = false, ["error"] = $"'{who}' is not online" });
            return;
        }

        var results = new JsonArray();
        bool all = true;
        foreach (JsonNode id in msg["item_ids"]!.AsArray())
        {
            JsonObject r = EquipOne(m, (int)id!, (int?)msg["hue"] ?? 0);
            all &= (bool)r["ok"];
            results.Add(r);
        }

        from.Send(new JsonObject { ["op"] = "equip-many", ["ok"] = all, ["as"] = m.RawName, ["results"] = results });
        Log.Information("GUO editor bridge: '{0}' equipped {1} item(s) on {2}: {3}", from.Name, results.Count, m.RawName, all);
    }

    // {"op":"unequip","as":"<online character>","item_ids":[65505, ...]}
    // Each worn item with that id goes to the backpack; hair (virtual on
    // ModernUO) is cleared.
    private static void Unequip(EditorConnection from, JsonNode msg)
    {
        string who = (string)msg["as"];
        Mobile m = Online(who);
        if (m == null)
        {
            from.Send(new JsonObject { ["op"] = "unequip", ["ok"] = false, ["error"] = $"'{who}' is not online" });
            return;
        }

        var removed = new JsonArray();
        var missing = new JsonArray();
        foreach (JsonNode node in msg["item_ids"]!.AsArray())
        {
            int id = (int)node!;
            if (m.HairItemID == id)
            {
                m.HairItemID = 0;
                removed.Add($"hair 0x{id:X4}");
                continue;
            }

            Item worn = m.Items.FirstOrDefault(i => i.ItemID == id && i.Parent == m);
            if (worn == null)
            {
                missing.Add($"0x{id:X4}");
                continue;
            }

            m.Backpack?.DropItem(worn);
            removed.Add($"0x{id:X4} from {worn.Layer}");
        }

        from.Send(new JsonObject { ["op"] = "unequip", ["ok"] = missing.Count == 0, ["as"] = m.RawName,
                                   ["removed"] = removed, ["not_worn"] = missing });
        Log.Information("GUO editor bridge: '{0}' took {1} item(s) off {2}", from.Name, removed.Count, m.RawName);
    }

    // One item onto the character, on the layer its tiledata names. A hand item
    // clears both hands into the backpack (a shield conflicts with a two-handed
    // weapon); anything else clears only its own layer. Hair is ModernUO's
    // virtual hair (HairItemID), not an item.
    private static JsonObject EquipOne(Mobile m, int itemId, int hue)
    {
        var layer = (Layer)TileData.ItemTable[itemId & TileData.MaxItemValue].Quality;
        if (layer == Layer.Hair)
        {
            m.HairItemID = itemId;
            m.HairHue = hue;
            return new JsonObject { ["ok"] = true, ["as"] = m.RawName, ["item"] = $"0x{itemId:X4}", ["item_id"] = itemId,
                                    ["layer"] = "Hair", ["serial"] = 0, ["virtual_hair"] = true };
        }

        if (m.Backpack == null)
        {
            m.AddItem(new Server.Items.Backpack());
        }

        var moved = new JsonArray();
        var clear = layer is Layer.OneHanded or Layer.TwoHanded ? new[] { Layer.OneHanded, Layer.TwoHanded } : new[] { layer };
        foreach (var l in clear)
        {
            if (m.FindItemOnLayer(l) is { } held)
            {
                m.Backpack.DropItem(held);
                moved.Add($"{held.GetType().Name} 0x{held.ItemID:X4} from {l}");
            }
        }

        var item = new Item(itemId) { Layer = layer, Hue = hue, Movable = true };
        bool canEquip = item.CanEquip(m), checkEquip = m.CheckEquip(item);
        bool ok = m.EquipItem(item);
        if (!ok)
        {
            item.Delete();
        }

        return new JsonObject { ["ok"] = ok, ["as"] = m.RawName, ["item"] = $"0x{itemId:X4}", ["item_id"] = itemId,
                                ["layer"] = layer.ToString(), ["serial"] = ok ? (uint)item.Serial : 0,
                                ["can_equip"] = canEquip, ["check_equip"] = checkEquip, ["moved_to_pack"] = moved };
    }

    // {"op":"admin_whoami"} / {"op":"admin_audit","count":50}, each with an optional "req" echoed back.
    // Authorised against the level the hello's token granted, run, and audited
    // (ADR-0035). AD1 onwards add their ops here and in AdminChannel.Ops.
    private static void RunAdmin(EditorConnection from, JsonNode msg, string op)
    {
        JsonNode req = msg["req"] is JsonNode r ? JsonNode.Parse(r.ToJsonString()) : null;
        string refused = _admin.Authorise(op, from.Admin);
        if (refused != null)
        {
            _admin.Audit.Record(from.Name, op, from.Admin, false, msg, refused);
            from.Send(new JsonObject { ["op"] = op, ["req"] = req, ["ok"] = false, ["error"] = refused });
            Log.Warning("GUO editor bridge: '{0}' refused {1}: {2}", from.Name, op, refused);
            return;
        }

        _admin.Audit.Record(from.Name, op, AdminChannel.Ops[op], true, msg);
        var reply = new JsonObject { ["op"] = op, ["req"] = req, ["ok"] = true };
        switch (op)
        {
            case "admin_whoami":
                reply["editor"] = from.Name;
                reply["level"] = from.Admin.ToString();
                reply["ops"] = _admin.OpsFor(from.Admin!.Value);
                break;
            case "admin_audit":
                reply["entries"] = _admin.Audit.Recent(Math.Clamp((int?)msg["count"] ?? 50, 1, AuditLog.Keep));
                break;
        }

        from.Send(reply);
        Log.Information("GUO editor bridge: '{0}' ran {1} at {2}", from.Name, op, AdminChannel.Ops[op]);
    }

    // {"op":"command","as":"Guosweep","text":"[add ..."}
    private static void RunCommand(EditorConnection from, JsonNode msg)
    {
        string who = (string)msg["as"];
        string text = (string)msg["text"];
        Mobile m = NetState.Instances.Select(ns => ns.Mobile)
            .FirstOrDefault(x => x != null && string.Equals(x.RawName, who, StringComparison.OrdinalIgnoreCase));
        if (m == null)
        {
            from.Send(new JsonObject { ["op"] = "command", ["ok"] = false, ["error"] = $"'{who}' is not online" });
            return;
        }

        bool handled = CommandSystem.Handle(m, text);
        from.Send(new JsonObject { ["op"] = "command", ["ok"] = handled, ["as"] = m.RawName, ["text"] = text });
        Log.Information("GUO editor bridge: '{0}' ran \"{1}\" as {2}: {3}", from.Name, text, m.RawName, handled ? "handled" : "not a command");
    }
}
