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

        EventSink.Connected += OnConnected;
        EventSink.Disconnected += m =>
        {
            _ulClients.Remove(m?.NetState);
            _setUpOn.Remove(m?.NetState);
            _queried.Remove(m?.NetState);
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

                    conn.Send(new JsonObject
                    {
                        ["op"] = "hello", ["shard"] = _shardName,
                        ["maps"] = new JsonArray(_ulMaps.Select(m => (JsonNode)m).ToArray()),
                        ["seasons"] = seasons,
                    });
                    Log.Information("GUO editor bridge: editor '{0}' connected", conn.Name);
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
