#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using Godot;
using GUO.Workspace;

/// <summary>
/// The UO Shard dock (docs/editor_plan.md §3, §4.5): the live tier. Connects
/// to a shard's editor bridge; while live, every edit, undo and redo in the
/// World tab goes to the shard, which applies it to its own map, pushes it to
/// UltimaLive clients and relays it to the other editors. Blocks from other
/// editors land in this project (last write wins) and the log says who.
/// A command line runs GM commands on the shard as an online character.
/// </summary>
[Tool]
public partial class ShardDock : EditorDock
{
    private readonly ShardLink _link = new();
    private WorldView _world;

    private LineEdit _host, _name, _as, _command;
    private SpinBox _port;
    private CheckButton _live;
    private Label _status;
    private RichTextLabel _log;

    /// <summary>The last block another editor sent: who, where, when received (unix ms), and latency if known.</summary>
    public (string From, int Facet, int Bx, int By, long ReceivedMs, long LatencyMs)? LastRemote { get; private set; }

    /// <summary>The last acknowledgement: clients and editors the shard passed the block to.</summary>
    public (int Clients, int Editors, long SentMs, long AckMs)? LastAck { get; private set; }

    /// <summary>The last command reply.</summary>
    public JsonNode LastCommand { get; private set; }

    /// <summary>Every world-object acknowledgement, in order (ADR-0014).</summary>
    public List<JsonNode> ObjectAcks { get; } = new();

    private long _lastSentMs;

    // The Live map layer's feed (ADR-0027): polled while Live is on and the World tab shows.
    private List<LiveMobile> _liveMobiles = new();
    private List<LiveItem> _liveItems = new();
    private double _pollClock;
    private long _pollSentMs;
    private int _pollReq;
    private int _pollAnswered;
    private const double PollSeconds = 1.0;
    private const int PollMargin = 24;

    /// <summary>The last "mobiles" reply, verbatim.</summary>
    public JsonNode LastMobiles { get; private set; }

    /// <summary>How many "mobiles" requests the Live layer has sent.</summary>
    public int MobilePolls => _pollReq;

    /// <summary>The mobiles the Live layer is drawing, as the shard last reported them.</summary>
    internal IReadOnlyList<LiveMobile> LiveMobiles => _liveMobiles;

    /// <summary>The live items the shard last reported, drawn as live objects beside the mobiles.</summary>
    internal IReadOnlyList<LiveItem> LiveItems => _liveItems;

    /// <summary>The last "items" reply, verbatim.</summary>
    public JsonNode LastItems { get; private set; }

    public bool Live => _link.Connected;

    /// <summary>The "as editor" name (the tour sets a neutral one so no frame shows a user name).</summary>
    public string EditorName
    {
        get => _name?.Text ?? "";
        set
        {
            if (_name == null)
            {
                _Ready();
            }

            _name.Text = value;
        }
    }

    public ShardDock()
    {
        Name = "UOShard";
        Title = "UO Shard";
        LayoutKey = "guo_shard";
        DefaultSlot = DockSlot.Bottom;
        AvailableLayouts = DockLayout.Horizontal | DockLayout.Floating;
    }

    internal void Attach(WorldView world)
    {
        _world = world;
        _world.Editor.BlockWritten += OnBlockWritten;
        _world.Objects.Put += OnObjectPut;
        _world.Objects.Deleted += OnObjectDeleted;
        _world.Layers.LiveSource = () => _liveMobiles;
    }

    /// <summary>
    /// Asks the bridge for the mobiles in the visible region plus a margin
    /// when the Live layer is on, this dock is connected and the World tab
    /// shows, about once a second, with one request in flight.
    /// </summary>
    private void PollLive(double delta)
    {
        if (_world == null || !_link.Connected || !_world.Layers.Live.On || !_world.IsVisibleInTree())
        {
            return;
        }

        _pollClock += delta;
        bool waiting = _pollReq > _pollAnswered && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _pollSentMs < 3000;
        if (_pollClock < PollSeconds || waiting)
        {
            return;
        }

        (int Facet, int X0, int Y0, int X1, int Y1)? view = _world.Layers.LastView;
        if (view == null)
        {
            if (!_world.Host.IsBooted)
            {
                return;
            }

            var player = _world.Host.World.Player;
            view = (_world.Host.Facet, player.X - 64, player.Y - 64, player.X + 64, player.Y + 64);
        }

        _pollClock = 0;
        _pollSentMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var v = view.Value;
        // Queries are public (no "as"): naming an offline character would
        // refuse every poll. "as" stays on GM commands, which need it.
        _link.RequestMobiles(++_pollReq, v.Facet, v.X0 - PollMargin, v.Y0 - PollMargin, v.X1 + PollMargin, v.Y1 + PollMargin);
        _link.RequestItems(++_pollReq, v.Facet, v.X0 - PollMargin, v.Y0 - PollMargin, v.X1 + PollMargin, v.Y1 + PollMargin);
    }

    public override void _Ready()
    {
        if (_log != null)
        {
            return;
        }

        var root = new VBoxContainer();
        root.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(root);

        var row = new HBoxContainer();
        root.AddChild(row);
        _live = new CheckButton { Text = "Live" };
        _live.Toggled += on =>
        {
            if (on)
            {
                Connect();
            }
            else
            {
                Disconnect();
            }
        };
        row.AddChild(_live);
        row.AddChild(new Label { Text = "bridge" });
        _host = new LineEdit { Text = EditorData.Setting("UO_EDITOR_LIVE_HOST", "127.0.0.1"), CustomMinimumSize = new Vector2(120, 0) };
        row.AddChild(_host);
        _port = new SpinBox { MinValue = 1, MaxValue = 65535, Value = int.Parse(EditorData.Setting("UO_EDITOR_LIVE_PORT", "2595"), CultureInfo.InvariantCulture) };
        row.AddChild(_port);
        row.AddChild(new Label { Text = "as editor" });
        _name = new LineEdit { Text = EditorData.Setting("UO_EDITOR_NAME", System.Environment.UserName), CustomMinimumSize = new Vector2(100, 0) };
        row.AddChild(_name);
        _status = new Label { Text = "live: off", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddChild(_status);

        var cmd = new HBoxContainer();
        root.AddChild(cmd);
        cmd.AddChild(new Label { Text = "GM command as" });
        _as = new LineEdit { PlaceholderText = "online character", CustomMinimumSize = new Vector2(120, 0) };
        cmd.AddChild(_as);
        _command = new LineEdit { PlaceholderText = "[where", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _command.TextSubmitted += _ => RunCommand(_as.Text, _command.Text);
        cmd.AddChild(_command);
        var run = new Button { Text = "Run" };
        run.Pressed += () => RunCommand(_as.Text, _command.Text);
        cmd.AddChild(run);

        _log = new RichTextLabel { ScrollFollowing = true, SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 90) };
        root.AddChild(_log);
    }

    /// <summary>Connects to the bridge. False, with the reason in the log, if it cannot.</summary>
    public bool Connect(string host = null, int port = 0, string name = null)
    {
        if (_log == null)
        {
            _Ready();
        }

        host ??= _host.Text;
        port = port > 0 ? port : (int)_port.Value;
        name ??= _name.Text;
        try
        {
            // The command box's "command" op is an admin op since AD3 (ADR-0035): this workspace's token goes in the hello.
            EditorWorkspace.Ensure();
            _link.Connect(host, port, name, ShardSecrets.BridgeAdminToken());
            _status.Text = $"live: on, {host}:{port} as {name}";
            _live.SetPressedNoSignal(true);
            Log($"connected to {host}:{port} as {name}");
            return true;
        }
        catch (Exception ex)
        {
            _status.Text = "live: off";
            _live.SetPressedNoSignal(false);
            Log($"[color=orange]could not connect to {host}:{port}: {ex.Message}[/color]");
            return false;
        }
    }

    public void Disconnect()
    {
        _link.Disconnect();
        _liveMobiles = new List<LiveMobile>();
        _liveItems = new List<LiveItem>();
        LiveObjects.Clear(_world?.Host?.World);
        _status.Text = "live: off";
        _live?.SetPressedNoSignal(false);
        Log("disconnected");
    }

    private long _syncLogMs;

    /// <summary>Pushes the latest live feeds into the World tab's scene as real objects.</summary>
    private void SyncLive()
    {
        var world = _world?.Host?.World;
        if (world == null)
        {
            return;
        }

        LiveObjects.Sync(world, _world.Host.Facet, _liveMobiles, _liveItems);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (now - _syncLogMs > 10000)
        {
            _syncLogMs = now;
            Log($"live objects: {world.Mobiles.Count} mobiles, {world.Items.Count} items in the world");
        }
    }

    public void RunCommand(string asCharacter, string text)
    {
        if (!_link.Connected || string.IsNullOrWhiteSpace(text))
        {
            Log("[color=orange]not live, or no command[/color]");
            return;
        }

        _link.SendCommand(asCharacter, text);
        Log($"command as {asCharacter}: {text}");
    }

    private void OnBlockWritten(WorldBlock b)
    {
        if (!_link.Connected)
        {
            return;
        }

        _lastSentMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _link.SendBlock(b);
        Log($"sent block map{b.Facet} {b.Bx},{b.By} ({b.Statics.Count} statics)");
    }

    private void OnObjectPut(string kind, JsonObject obj)
    {
        if (_link.Connected)
        {
            _link.SendObject("put", kind, obj);
            Log($"sent {kind} {(string)obj["id"]}");
        }
    }

    private void OnObjectDeleted(string kind, Guid id)
    {
        if (_link.Connected)
        {
            _link.SendObject("delete", kind, null, id);
            Log($"sent delete of {kind} {id}");
        }
    }

    public override void _Process(double delta)
    {
        PollLive(delta);
        for (JsonNode msg = _link.Poll(); msg != null; msg = _link.Poll())
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            switch ((string)msg["op"])
            {
                case "hello":
                {
                    Log($"shard '{(string)msg["shard"]}' answers");

                    // Draw the season this shard gives the map, as its clients see it.
                    if (_world != null && _world.IsBooted && msg["seasons"] is JsonObject seasons
                        && seasons[_world.Host.Facet.ToString(CultureInfo.InvariantCulture)] is JsonNode s)
                    {
                        _world.Season = (GUO.Game.Managers.Season)(int)s;
                        Log($"season for map{_world.Host.Facet}: {_world.Season} (the shard's)");
                    }

                    break;
                }
                case "ack":
                    LastAck = ((int)msg["clients"], (int)msg["editors"], _lastSentMs, now);
                    Log($"shard applied block {(int)msg["bx"]},{(int)msg["by"]}: pushed to {(int)msg["clients"]} client(s), "
                        + $"{(int)msg["editors"]} other editor(s); {now - _lastSentMs} ms round trip");
                    break;
                case "block":
                {
                    string from = (string)msg["from"] ?? "?";
                    WorldBlock b = ShardLink.ToBlock(msg);
                    long latency = msg["sent_ms"] is JsonNode sent ? now - (long)sent : -1;
                    _world?.Editor.ApplyRemote(b, from);
                    LastRemote = (from, b.Facet, b.Bx, b.By, now, latency);
                    Log($"{from} changed block map{b.Facet} {b.Bx},{b.By}" + (latency >= 0 ? $" ({latency} ms after they sent it)" : ""));
                    break;
                }

                case "object":
                    _world?.Objects.ApplyRemote(msg);
                    Log($"{(string)msg["from"] ?? "?"}: {(string)msg["action"]} {(string)msg["kind"]}");
                    break;
                case "object_ack":
                    ObjectAcks.Add(msg);
                    Log($"shard: {(string)msg["action"]} {(string)msg["kind"]} {(string)msg["outcome"]} in {(long)msg["ms"]} ms, "
                        + $"relayed to {(int)msg["editors"]} editor(s)");
                    break;
                case "mobiles":
                    _pollAnswered = Math.Max(_pollAnswered, (int?)msg["req"] ?? 0);
                    if (msg["ok"] is JsonNode ok && !(bool)ok)
                    {
                        if ((string)msg["error"] != "rate limited")
                        {
                            Log($"[color=orange]live layer: {(string)msg["error"]}[/color]");
                        }

                        break;
                    }

                    LastMobiles = msg;
                    _liveMobiles = ShardLink.ToMobiles(msg);
                    SyncLive();
                    break;
                case "items":
                    _pollAnswered = Math.Max(_pollAnswered, (int?)msg["req"] ?? 0);
                    if (msg["ok"] is JsonNode okItems && !(bool)okItems)
                    {
                        if ((string)msg["error"] != "rate limited")
                        {
                            Log($"[color=orange]live items: {(string)msg["error"]}[/color]");
                        }

                        break;
                    }

                    LastItems = msg;
                    _liveItems = ShardLink.ToItems(msg);
                    SyncLive();
                    break;
                case "command":
                    LastCommand = msg;
                    Log((bool)msg["ok"]
                        ? $"command ran as {(string)msg["as"]}: {(string)msg["text"]}"
                        : $"[color=orange]command failed: {(string)msg["error"] ?? "not a command"}[/color]");
                    // What the server answered, since AD3 (MUO patch 0005).
                    foreach (JsonNode line in msg["output"] as JsonArray ?? new JsonArray())
                    {
                        Log("    " + ((string)line ?? "").Replace("[", "[lb]"));
                    }

                    break;
                case "error":
                    Log($"[color=orange]shard: {(string)msg["error"]}[/color]");
                    break;
                case "closed":
                    _status.Text = "live: off (connection closed)";
                    _live.SetPressedNoSignal(false);
                    Log("the shard closed the connection");
                    break;
            }
        }
    }

    private void Log(string line)
    {
        GD.Print($"[GUO editor] shard: {line}");
        _log?.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}\n");
    }

    public void Shutdown()
    {
        if (_world != null)
        {
            _world.Editor.BlockWritten -= OnBlockWritten;
            _world.Objects.Put -= OnObjectPut;
            _world.Objects.Deleted -= OnObjectDeleted;
        }

        _link.Dispose();
    }
}
#endif
