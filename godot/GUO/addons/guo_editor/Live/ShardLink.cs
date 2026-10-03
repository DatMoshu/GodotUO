#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;

/// <summary>
/// The editor's connection to a shard's editor bridge (ADR-0012): one TCP
/// connection, a JSON object per line. Sends whole blocks and GM commands;
/// receives acknowledgements and other editors' blocks.
/// </summary>
/// <remarks>
/// Reading happens on a background thread and only queues; <see cref="Poll"/>
/// hands messages over on the main thread, where the world and the project
/// live. The bridge is tools/editor_shard/bridge (a ModernUO assembly).
/// </remarks>
internal sealed class ShardLink : IDisposable
{
    private TcpClient _client;
    private StreamWriter _writer;
    private Thread _reader;
    private readonly ConcurrentQueue<JsonNode> _inbox = new();
    private readonly object _sendLock = new();

    public string Host { get; private set; }
    public int Port { get; private set; }
    public string Name { get; private set; }
    public bool Connected => _client?.Connected == true;
    public string Shard { get; private set; }

    /// <summary>Connects and says hello. Throws on failure.</summary>
    public void Connect(string host, int port, string name)
    {
        Disconnect();
        Host = host;
        Port = port;
        Name = name;
        _client = new TcpClient();
        _client.Connect(host, port);
        _writer = new StreamWriter(_client.GetStream(), new UTF8Encoding(false)) { NewLine = "\n" };
        _reader = new Thread(Read) { IsBackground = true, Name = "GUO editor shard link" };
        _reader.Start();
        Send(new JsonObject { ["op"] = "hello", ["editor"] = name });
    }

    private void Read()
    {
        try
        {
            using var reader = new StreamReader(_client.GetStream(), Encoding.UTF8);
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                try
                {
                    JsonNode msg = JsonNode.Parse(line);
                    if ((string)msg["op"] == "hello")
                    {
                        Shard = (string)msg["shard"];
                    }

                    _inbox.Enqueue(msg);
                }
                catch (System.Text.Json.JsonException)
                {
                }
            }
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }

        _inbox.Enqueue(new JsonObject { ["op"] = "closed" });
    }

    public void Send(JsonObject msg)
    {
        lock (_sendLock)
        {
            if (_writer == null)
            {
                return;
            }

            _writer.WriteLine(msg.ToJsonString());
            _writer.Flush();
        }
    }

    /// <summary>A whole block, as the bridge takes it.</summary>
    public void SendBlock(WorldBlock b)
    {
        var land = new JsonArray();
        for (int i = 0; i < 64; i++)
        {
            land.Add(new JsonArray(b.LandId[i], b.LandZ[i]));
        }

        var statics = new JsonArray();
        foreach (WorldStatic s in b.Statics)
        {
            statics.Add(new JsonArray(s.Id, s.X, s.Y, s.Z, s.Hue));
        }

        Send(new JsonObject
        {
            ["op"] = "block", ["facet"] = b.Facet, ["bx"] = b.Bx, ["by"] = b.By,
            ["land"] = land, ["statics"] = statics,
            ["sent_ms"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
    }

    /// <summary>A GM command, run on the shard as an online character.</summary>
    /// <summary>A world object put or delete (ADR-0014; docs/data_formats.md section 10).</summary>
    public void SendObject(string action, string kind, JsonObject obj, Guid? id = null)
    {
        var msg = new JsonObject { ["op"] = "object", ["action"] = action, ["kind"] = kind };
        if (obj != null)
        {
            msg["object"] = obj;
        }

        if (id != null)
        {
            msg["id"] = id.Value.ToString();
        }

        Send(msg);
    }

    /// <summary>Asks for the players and mobiles in a rectangle (the bridge's "mobiles" op, docs/data_formats.md section 10).</summary>
    public void RequestMobiles(int req, int facet, int x0, int y0, int x1, int y1, string asCharacter = null)
    {
        var msg = new JsonObject { ["op"] = "mobiles", ["req"] = req, ["facet"] = facet, ["x0"] = x0, ["y0"] = y0, ["x1"] = x1, ["y1"] = y1 };
        if (!string.IsNullOrWhiteSpace(asCharacter))
        {
            msg["as"] = asCharacter;
        }

        Send(msg);
    }

    /// <summary>The mobiles of a "mobiles" reply.</summary>
    internal static System.Collections.Generic.List<LiveMobile> ToMobiles(JsonNode msg)
    {
        var list = new System.Collections.Generic.List<LiveMobile>();
        if (msg["mobiles"] is JsonArray a)
        {
            foreach (JsonNode m in a)
            {
                list.Add(new LiveMobile((string)m["name"] ?? "", (int)m["facet"], (int)m["x"], (int)m["y"], (int)m["z"], (bool)m["isPlayer"],
                    (uint)m["serial"], (int)m["body"], (int)m["hits"], (int)m["maxHits"], (int)m["notoriety"]));
            }
        }

        return list;
    }

    public void SendCommand(string asCharacter, string text) =>
        Send(new JsonObject { ["op"] = "command", ["as"] = asCharacter, ["text"] = text });

    /// <summary>The next message received, on the calling (main) thread, or null.</summary>
    public JsonNode Poll() => _inbox.TryDequeue(out JsonNode m) ? m : null;

    /// <summary>A block message from another editor, as a <see cref="WorldBlock"/>.</summary>
    public static WorldBlock ToBlock(JsonNode msg)
    {
        var b = new WorldBlock { Facet = (int)msg["facet"], Bx = (int)msg["bx"], By = (int)msg["by"] };
        JsonArray land = msg["land"].AsArray();
        for (int i = 0; i < 64; i++)
        {
            b.LandId[i] = (ushort)(int)land[i][0];
            b.LandZ[i] = (sbyte)(int)land[i][1];
        }

        foreach (JsonNode s in msg["statics"].AsArray())
        {
            b.Statics.Add(new WorldStatic
            {
                Id = (ushort)(int)s[0], X = (byte)(int)s[1], Y = (byte)(int)s[2], Z = (sbyte)(int)s[3], Hue = (ushort)(int)s[4],
            });
        }

        return b;
    }

    public void Disconnect()
    {
        lock (_sendLock)
        {
            _writer = null;
        }

        try
        {
            _client?.Close();
        }
        catch (Exception)
        {
        }

        _client = null;
        _reader = null;
    }

    public void Dispose() => Disconnect();
}
#endif
