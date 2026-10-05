#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Godot;

/// <summary>Scripted bridge snapshots through a real Shard dock, without logging in to a shard. Repeated after assembly reload.</summary>
public partial class EditorSmoke
{
    private Task _liveLayerTask;
    private readonly Dictionary<string, object> _liveLayerReport = new();
    private Dictionary<uint, LiveMobile> _trafficBefore;
    private int _trafficFrames, _trafficPoll;
    private double _trafficStarted;
    private bool _trafficMoved;
    private Guid? _trafficSpawner;

    private bool ContinueLiveTraffic()
    {
        if (_trafficStarted == 0)
        {
            return false;
        }
        if (CheckLiveTraffic()) { Finish(); }
        return true;
    }

    /// <summary>Optional real-shard evidence, using the existing mobiles role and its one dock connection.</summary>
    private bool CheckLiveTraffic()
    {
        if (ArgValue("--guo-editor-live-traffic") == null)
        {
            return true;
        }

        CellGeometry geo = CellGeometry.From(_world.Host);
        if (geo == null)
        {
            return false;
        }

        if (_trafficStarted == 0)
        {
            _trafficStarted = _elapsed;
            // Capture only NPC labels: a probe character's name can equal its private account name.
            _world.SetLiveKinds(false, true);
            if (ArgValue("--guo-editor-live-traffic-spawn") != null)
            {
                int x = EditX + 4, y = EditY + 2;
                _trafficSpawner = _world.Objects.PlaceSpawner(_liveFacet, x, y,
                    _world.Modes.Data.LandZ(x, y), "Horse", 4)?.Id;
                _live["traffic_fixture_spawner"] = _trafficSpawner;
            }
        }

        var visible = _shard.LiveMobiles.Where(m => !m.Player && m.Facet == geo.Facet
            && new Rect2(0, 0, geo.Width, geo.Height).HasPoint(geo.Project(m.X + 0.5f, m.Y + 0.5f, m.Z))).ToList();
        if (_trafficBefore == null && visible.Count > 0 && ++_trafficFrames >= 4)
        {
            _trafficBefore = visible.ToDictionary(m => m.Serial);
            _trafficPoll = (int?)_shard.LastMobiles?["req"] ?? 0;
            _live["traffic_before"] = visible;
            CaptureTraffic("before");
            _trafficFrames = 0;
        }
        else if (_trafficBefore != null)
        {
            var moved = visible.FirstOrDefault(m => _trafficBefore.TryGetValue(m.Serial, out LiveMobile old)
                && (old.X != m.X || old.Y != m.Y || old.Z != m.Z));
            if (!_trafficMoved && moved.Serial != 0 && (int?)_shard.LastMobiles?["req"] > _trafficPoll)
            {
                _trafficMoved = true;
                _live["traffic_moved_serial"] = moved.Serial;
                _live["traffic_old_position"] = new[] { _trafficBefore[moved.Serial].X, _trafficBefore[moved.Serial].Y, _trafficBefore[moved.Serial].Z };
                _live["traffic_new_position"] = new[] { moved.X, moved.Y, moved.Z };
                _trafficFrames = 0;
            }
            if (_trafficMoved && ++_trafficFrames >= 4)
            {
                _live["traffic_after"] = visible;
                CaptureTraffic("after");
                _live["traffic_moved"] = true;
                _live["mobiles_polls"] = _shard.MobilePolls;
                if (_trafficSpawner is { } id) { _world.Objects.Delete(id); }
                _live["ok"] = _failures.Count == 0;
                return true;
            }
        }

        if (_elapsed - _trafficStarted > 90)
        {
            _failures.Add("live traffic: no visible NPC moved in 90 seconds");
            _live["ok"] = false;
            _live["traffic_moved"] = false;
            if (_trafficSpawner is { } id) { _world.Objects.Delete(id); }
            return true;
        }
        return false;
    }

    private void CaptureTraffic(string phase)
    {
        Image frame = _world.Capture();
        if (frame == null || frame.IsEmpty())
        {
            _failures.Add("live traffic: a windowed World frame is required");
            _live["ok"] = false;
            return;
        }
        string file = Path.Combine(_out, $"world_live_mobiles_{phase}.png");
        frame.SavePng(file);
        _live[$"traffic_{phase}_png"] = file;
    }

    private bool StepLiveLayer()
    {
        if (_liveLayerTask == null)
        {
            _liveLayerReport["ok"] = true;
            _liveLayerReport["after_reload"] = _afterReload;
            _liveLayerTask = RunLiveLayerAsync();
        }

        if (!_liveLayerTask.IsCompleted)
        {
            return false;
        }

        if (_liveLayerTask.IsFaulted)
        {
            LiveCheck("completed", false, _liveLayerTask.Exception?.GetBaseException().ToString());
        }

        _report["live_layer"] = _liveLayerReport;
        return true;
    }

    private void LiveCheck(string name, bool ok, string detail = "")
    {
        _liveLayerReport[name] = ok;
        if (!ok)
        {
            _liveLayerReport["ok"] = false;
            _failures.Add($"Live layer: {name} {detail}".Trim());
        }
        GD.Print($"[GUO editor] smoke Live layer {name}: {(ok ? "PASS" : "FAIL")}");
    }

    internal static TcpListener LiveFixtureListener() => new(IPAddress.Loopback, 0);

    internal static JsonObject FixtureMobile(uint serial, string name, int x, int y, int z, bool player, int facet = 0) => new()
    {
        ["serial"] = serial, ["name"] = name, ["x"] = x, ["y"] = y, ["z"] = z,
        ["isPlayer"] = player, ["facet"] = facet, ["body"] = 400,
        ["hits"] = 100, ["maxHits"] = 100, ["notoriety"] = 1,
    };

    internal static JsonObject FixtureSnapshot(int req, params JsonObject[] rows)
    {
        var list = new JsonArray();
        foreach (JsonObject row in rows)
        {
            list.Add(row);
        }
        return new JsonObject { ["op"] = "mobiles", ["req"] = req, ["ok"] = true,
            ["facet"] = 0, ["count"] = rows.Length, ["mobiles"] = list };
    }

    private async Task RunLiveLayerAsync()
    {
        MapLayers layers = _world.Layers;
        var source = layers.LiveSource;
        bool on = layers.Live.On, players = layers.Live.Players, mobiles = layers.Live.Mobiles;
        var listener = LiveFixtureListener();
        var dock = new ShardDock();
        try
        {
            // This fixture replaces the feed temporarily. Production continues to use the plugin's one Shard dock socket.
            listener.Start();
            AddChild(dock);
            dock.Hide();
            dock.Attach(_world);
            _world.SetLayer("Live", true);
            _world.SetLiveKinds(true, true);
            LiveCheck("connect", dock.Connect(IPAddress.Loopback.ToString(), ((IPEndPoint)listener.LocalEndpoint).Port, "Smoke live"));
            Task<TcpClient> accept = listener.AcceptTcpClientAsync();
            if (!await Until(() => accept.IsCompleted, 5))
            {
                LiveCheck("accept", false);
                return;
            }

            using TcpClient peer = await accept;
            using var writer = new StreamWriter(peer.GetStream(), new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
            await writer.WriteLineAsync(new JsonObject { ["op"] = "hello", ["shard"] = "Smoke live" }.ToJsonString());
            async Task<bool> Snapshot(int req, params JsonObject[] rows)
            {
                await writer.WriteLineAsync(FixtureSnapshot(req, rows).ToJsonString());
                return await Until(() => (int?)dock.LastMobiles?["req"] == req, 5);
            }

            Func<float, float, float, Vector2> project = (x, y, z) =>
                new Vector2(200 + ((x - 1496) - (y - 1628)) * 22, 150 + ((x - 1496) + (y - 1628)) * 22 - z * 4);
            var view = new LayerView { Facet = 0, Project = project, Screen = new Rect2(0, 0, 400, 300) };
            ImagePaint Paint()
            {
                var image = new ImagePaint(400, 300);
                layers.Live.Draw(image, view);
                return image;
            }
            bool GreenAt(ImagePaint image, int x, int y) => image.PixelAt(x, y).G > 0.95f && image.PixelAt(x, y).R < 0.35f;

            LiveCheck("appear_reply", await Snapshot(1,
                FixtureMobile(1, "Fixture Player", 1497, 1629, 10, true),
                FixtureMobile(2, "Fixture Horse", 1494, 1630, 0, false),
                FixtureMobile(3, "Other facet", 1497, 1629, 10, true, 1)));
            var items = layers.Items("Live", 0).ToList();
            LiveCheck("appear_cells", items.Count == 2 && items.Any(i => i.Label == "Fixture Player" && i.X == 1497 && i.Y == 1629 && i.Z == 10));
            ImagePaint first = Paint();
            LiveCheck("draw_cell_and_z", GreenAt(first, 200, 176));
            LiveCheck("hover_details", layers.Live.Hover(0, new Vector2(200, 176), project)
                == "Fixture Player (player)\nSerial 0x00000001\nmap0: 1497, 1629, 10");
            LiveCheck("hover_empty", layers.Live.Hover(0, Vector2.Zero, project) == "");
            PopupMenu menu = _world.MapLayersMenu.GetPopup();
            menu.EmitSignal(PopupMenu.SignalName.IdPressed, 1000);
            LiveCheck("filter_players", layers.Items("Live", 0).Single().Label == "Fixture Horse"
                && layers.Live.Hover(0, new Vector2(200, 176), project) == "" && !GreenAt(Paint(), 200, 176));
            LiveCheck("layers_menu_sync", _world.MenuItemChecked("Live") && !_world.MenuItemChecked("Live players"));
            LiveCheck("layers_menu_filter", _world.SetMenuItem("Live mobiles", false)
                && !layers.Items("Live", 0).Any() && !menu.IsItemChecked(menu.GetItemIndex(1001)));
            _world.SetLiveKinds(true, false);
            LiveCheck("filter_mobiles", layers.Items("Live", 0).Single().Label == "Fixture Player"
                && Paint().Labels.All(l => l.Text != "Fixture Horse"));
            _world.SetLiveKinds(false, false);
            LiveCheck("filter_none", !layers.Items("Live", 0).Any() && Paint().Labels.Count == 0);
            _world.SetLiveKinds(true, true);

            LiveCheck("move_reply", await Snapshot(2, FixtureMobile(1, "Fixture Player", 1498, 1629, 5, true),
                FixtureMobile(2, "Fixture Horse", 1494, 1630, 0, false)));
            ImagePaint moved = Paint();
            LiveCheck("move_cell_and_z", GreenAt(moved, 222, 218) && !GreenAt(moved, 200, 176)
                && layers.Items("Live", 0).Single(i => i.Label == "Fixture Player") is { X: 1498, Y: 1629, Z: 5 });
            LiveCheck("move_hover", layers.Live.Hover(0, new Vector2(200, 176), project) == ""
                && layers.Live.Hover(0, new Vector2(222, 218), project).Contains("1498, 1629, 5"));
            LiveCheck("remove_reply", await Snapshot(3, FixtureMobile(2, "Fixture Horse", 1494, 1630, 0, false)));
            LiveCheck("remove_icon_and_hover", !GreenAt(Paint(), 222, 218)
                && layers.Live.Hover(0, new Vector2(222, 218), project) == ""
                && layers.Items("Live", 0).Count() == 1);
            _world.SetLayer("Live", false);
            LiveCheck("off_hover", layers.Live.Hover(0, new Vector2(112, 172), project) == "");
            dock.Disconnect();
            LiveCheck("disconnect_clears", !layers.Items("Live", 0).Any());
        }
        finally
        {
            dock.Shutdown();
            dock.Free();
            listener.Stop();
            layers.LiveSource = source;
            _world.SetLiveKinds(players, mobiles);
            _world.SetLayer("Live", on);
        }
    }
}
#endif
