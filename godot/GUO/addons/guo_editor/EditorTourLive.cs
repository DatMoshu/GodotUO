#if TOOLS
namespace GUO.Editor;

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Godot;
using GUO.Workspace;

/// <summary>S8/S9 shown through their real controls and the smoke's local fixtures; no shard login.</summary>
public partial class EditorTour
{
    private async Task<bool> TourUntil(Func<bool> ready, double seconds = 6)
    {
        var clock = Stopwatch.StartNew();
        while (!ready() && clock.Elapsed.TotalSeconds < seconds) await Frames();
        return ready();
    }

    private async Task ServerConsoleSeg()
    {
        LogsDock dock = All<LogsDock>(EditorInterface.Singleton.GetBaseControl()).FirstOrDefault();
        Check(dock?.Panel != null, "the Logs dock is available");
        if (dock?.Panel == null) return;

        string previousRoot = Workspace.RootOverride;
        string root = Path.Combine(_out, "console_workspace");
        string id = Guid.NewGuid().ToString("N");
        string home = Path.Combine(root, "servers", id);
        string state = Path.Combine(home, "process.json");
        LogsPanel panel = null;
        bool runVisible = _run.Visible;
        try
        {
            _run.Hide();
            Workspace.RootOverride = root;
            Directory.CreateDirectory(home);
            string script = Path.Combine(home, "console_fixture.py");
            File.WriteAllText(script, "import sys, time\n"
                + "print('INFO: scripted stdout captured', flush=True)\n"
                + "print('WARNING: scripted stderr captured', file=sys.stderr, flush=True)\n"
                + "print('password=tour-fixture-value', flush=True)\n"
                + "time.sleep(120)\n");
            string python = EditorData.Setting("UO_PYTHON", OperatingSystem.IsWindows() ? "python" : "python3");
            string executable = Path.IsPathFullyQualified(python) ? python : (System.Environment.GetEnvironmentVariable("PATH") ?? "")
                .Split(Path.PathSeparator).Select(p => Path.Combine(p.Trim('"'), python + (OperatingSystem.IsWindows() && !python.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? ".exe" : "")))
                .FirstOrDefault(File.Exists) ?? python;
            var profile = new ServerProfile { Id = id, Name = "Tour console", Host = "localhost", Port = 2619,
                ServerDirectory = home, Executable = executable, Arguments = new[] { "-u", script } };
            string console = LogSources.ServerConsole(id);
            dock.Panel.Hide();
            panel = new LogsPanel { AutoDiscover = false, SettingsPath = Path.Combine(root, "sources.json"),
                PollMilliseconds = 50, CustomMinimumSize = new Vector2(0, 480) };
            panel.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            dock.AddChild(panel);
            LogView view = panel.AddSource("serverconsole", "Server console: Tour fixture", new LogTailer(console));
            view.Note = "Scripted fixture; both output streams use the Run bar's managed-process capture.";
            panel.Show("serverconsole");
            dock.MakeVisible();
            ManagedServerProcess.Start(profile, state);
            Check(await TourUntil(() => view.BodyText.Contains("scripted stdout captured") && view.BodyText.Contains("scripted stderr captured")),
                "the managed console captured stdout and stderr");
            Check(view.BodyText.Contains("password=[redacted]") && !view.BodyText.Contains("tour-fixture-value"),
                "the Logs view hides the fixture secret");
            Check(ManagedServerProcess.Running(state), "the fixture process is managed and running");
            // The normal status contains its file path. This fixture's captions give the
            // state instead, so no path is present in the recorded view.
            foreach (Label label in All<Label>(view).Where(l => l.Text == view.StatusText)) label.Hide();
            Say("Server console in Logs follows stdout and stderr captured by the Run bar's managed-process path. "
                + "This tour starts a harmless script, not a shard: its output is real, and the fixture secret is redacted.", top: true);
            MarkControl(view, "managed stdout + stderr");
            await Shot(5);

            OptionButton levelFilter = All<OptionButton>(view).First();
            levelFilter.Select(1);
            levelFilter.EmitSignal(OptionButton.SignalName.ItemSelected, 1L);
            Check(view.ShownCount == 1 && view.BodyText.Contains("scripted stderr captured"), "the warning filter selects stderr's warning");
            Say("The level filter keeps the warning from stderr and hides the informational lines. "
                + "Filtering changes the read-only view; it leaves the captured console file alone.", top: true);
            _overlay.ClearMarks();
            MarkControl(levelFilter, "level filter: warnings");
            await Shot(5);

            levelFilter.Select(0);
            levelFilter.EmitSignal(OptionButton.SignalName.ItemSelected, 0L);
            ManagedServerProcess.Stop(state);
            Check(!ManagedServerProcess.Running(state) && !File.Exists(state), "the exact managed fixture stopped and its state was removed");
            Check(File.Exists(console) && new FileInfo(console).Length > 0, "the captured output remains after stopping");
            Say("Stopping removes this managed process's state and releases its console. The captured output remains in Logs. "
                + "No real server was started or stopped, and no client installation was changed.", top: true);
            _overlay.ClearMarks();
            MarkControl(view, "captured output remains");
            await Shot(5);
        }
        finally
        {
            ManagedServerProcess.Stop(state);
            panel?.Shutdown();
            panel?.Free();
            dock.Panel.Show();
            Workspace.RootOverride = previousRoot;
            _run.Visible = runVisible;
        }
    }

    private async Task LiveLayerSeg()
    {
        EditorInterface.Singleton.SetMainScreenEditor(GuoEditorPlugin.WorldTabName);
        Check(_world.GoTo(0, 1496, 1628), "the World tab opened Britain");
        if (!WorldUp) return;
        await Frames(30);
        MapLayers layers = _world.Layers;
        var source = layers.LiveSource;
        bool on = layers.Live.On, players = layers.Live.Players, mobiles = layers.Live.Mobiles;
        Vector2I? mouse = _world.ForcedMouse;
        bool runVisible = _run.Visible;
        var listener = EditorSmoke.LiveFixtureListener();
        var dock = new ShardDock();
        try
        {
            _run.Hide();
            listener.Start();
            AddChild(dock);
            dock.Hide();
            dock.Attach(_world);
            _world.SetLayer("Live", true);
            _world.SetLiveKinds(true, true);
            Check(dock.Connect(IPAddress.Loopback.ToString(), ((IPEndPoint)listener.LocalEndpoint).Port, "Tour bridge"),
                "the Shard dock connected to the smoke's local stub bridge");
            Task<TcpClient> accept = listener.AcceptTcpClientAsync();
            Check(await TourUntil(() => accept.IsCompleted), "the local bridge accepted its dock connection");
            if (!accept.IsCompleted) return;
            using TcpClient peer = await accept;
            using var writer = new StreamWriter(peer.GetStream(), new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
            await writer.WriteLineAsync(new JsonObject { ["op"] = "hello", ["shard"] = "Tour bridge" }.ToJsonString());
            async Task Snapshot(int req, params JsonObject[] rows)
            {
                await writer.WriteLineAsync(EditorSmoke.FixtureSnapshot(req, rows).ToJsonString());
                Check(await TourUntil(() => (int?)dock.LastMobiles?["req"] == req), $"bridge snapshot {req} reached the Live layer");
                await Frames(8);
            }
            void PointAtMobile(int x, int y, int z)
            {
                CellGeometry geo = CellGeometry.From(_world.Host);
                Vector2 at = geo.Project(x + 0.5f, y + 0.5f, z);
                Aim(new Vector2I((int)Math.Round(at.X), (int)Math.Round(at.Y)));
                Control canvas = All<SubViewportContainer>(_world).First();
                _overlay.Pointer(canvas.GetGlobalRect().Position + at * canvas.Size / (Vector2)_world.CanvasSize);
                _overlay.SetDetail(layers.Live.Hover(0, at, geo.Project));
            }

            await Snapshot(1, EditorSmoke.FixtureMobile(1, "Tour player", 1497, 1629, 10, true),
                EditorSmoke.FixtureMobile(2, "Tour horse", 1494, 1630, 0, false));
            Check(layers.Items("Live", 0).Count() == 2, "both fixture markers are present");
            PointAtMobile(1497, 1629, 10);
            Say("Live draws player and mobile markers from the Shard dock's bridge snapshots, using each cell's x, y and z. "
                + "Green is the scripted player; grey is the scripted mobile. This is the smoke's local bridge fixture, with no shard login.", top: true);
            await Shot(5);

            await Snapshot(2, EditorSmoke.FixtureMobile(1, "Tour player", 1498, 1629, 5, true),
                EditorSmoke.FixtureMobile(2, "Tour horse", 1494, 1630, 0, false));
            Check(layers.Items("Live", 0).Single(i => i.Label == "Tour player") is { X: 1498, Y: 1629, Z: 5 },
                "the next snapshot moved the player and changed its height");
            PointAtMobile(1498, 1629, 5);
            Say("The next bridge snapshot moves the player one cell and changes its z from 10 to 5. "
                + "The marker and hover details use the new position; the grey mobile remains where it was.", top: true);
            await Shot(5);

            _world.SetLiveKinds(false, true);
            Check(layers.Items("Live", 0).Single().Label == "Tour horse" && !_world.MenuItemChecked("Live players"),
                "the Live players filter hides only the player");
            _world.ForcedMouse = null;
            _overlay.SetDetail(null);
            _overlay.ClearMarks();
            Say("The Layers menu filters players and mobiles separately. Live players is off here, so only the grey mobile remains. "
                + "The source snapshot is unchanged; this switch only changes what the World draws.", top: true);
            MarkControl(_world.LayersMenu, "Live players: off");
            await Shot(5);

            _world.SetLiveKinds(true, true);
            await Snapshot(3, EditorSmoke.FixtureMobile(2, "Tour horse", 1494, 1630, 0, false));
            Check(layers.Items("Live", 0).Single().Label == "Tour horse", "a missing player was removed by the next snapshot");
            Say("A snapshot that omits the player removes its marker. The remaining mobile is still drawn. "
                + "Disconnecting the dock clears this feed; the tour restores the previous layer and filter settings afterwards.", top: true);
            _overlay.ClearMarks();
            await Shot(5);
            dock.Disconnect();
            Check(!layers.Items("Live", 0).Any(), "disconnecting cleared the fixture feed");
        }
        finally
        {
            dock.Shutdown();
            dock.Free();
            listener.Stop();
            layers.LiveSource = source;
            _world.SetLiveKinds(players, mobiles);
            _world.SetLayer("Live", on);
            _world.ForcedMouse = mouse;
            _run.Visible = runVisible;
        }
    }
}
#endif
