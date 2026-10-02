#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;

[Tool]
public partial class RunBar : HBoxContainer
{
    private ServerProfiles _profiles;
    private OptionButton _server, _count;
    private Button _start, _stop;
    private Label _status;
    private Godot.Timer _poll;
    private bool _busy;
    private TcpClient _connection;
    private Task _connect;
    private string _connectingId;
    private DateTime _deadline;
    private long _generation;
    private string Root => Path.Combine(EditorData.RepoRoot, "build", "editor_servers");
    private string ListPath => Path.Combine(Root, "profiles.json");
    private ServerProfile Selected => _profiles?.Servers.FirstOrDefault(s => s.Id == _profiles.Selected);
    private string State(ServerProfile s) => Path.Combine(Root, s.Id, "process.json");

    public override void _Ready()
    {
        try
        {
            _profiles = ServerProfiles.Load(ListPath);
            if (!File.Exists(ListPath))
            {
                _profiles.Servers.Add(new ServerProfile { Name = "Configured shard", Host = EditorData.Setting("UO_SHARD_HOST", "127.0.0.1"),
                    Port = int.TryParse(EditorData.Setting("UO_SHARD_PORT", "2593"), out int port) ? port : 2593,
                    ClientProject = ProjectSettings.GlobalizePath("res://"), ClientData = EditorData.Setting("UO_CLIENT_DATA", "") });
                string home = Path.Combine(EditorData.RepoRoot, "build", "shard_private");
                if (File.Exists(Path.Combine(home, "state.json")))
                {
                    using var state = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(home, "state.json")));
                    _profiles.Servers.Add(new ServerProfile { Name = "Private shard", Host = "127.0.0.1", Port = state.RootElement.GetProperty("port").GetInt32(),
                        ServerDirectory = home, Executable = Path.Combine(home, "ModernUO.exe"), ClientProject = ProjectSettings.GlobalizePath("res://"),
                        ClientData = EditorData.Setting("UO_CLIENT_DATA", "") });
                }
                _profiles.Selected = _profiles.Servers[0].Id; _profiles.Save(ListPath);
            }
        }
        catch (Exception e) { GD.PushError("Server profiles: " + e.Message); _profiles = new(); _loadFailed = true; }
        _server = new OptionButton { TooltipText = "Saved server profiles. Each keeps its own server paths and client files." };
        _server.ItemSelected += i => { _profiles.Selected = _profiles.Servers[(int)i].Id; Save(); _generation++; Poll(); };
        AddChild(_server);
        var manage = new Button { Text = "Manage servers" }; manage.Pressed += Manage; AddChild(manage);
        _status = new Label { Text = "Checking…" }; AddChild(_status);
        _start = new Button { Text = "Start server" }; _start.Pressed += () => Run(Start); AddChild(_start);
        _stop = new Button { Text = "Stop server", TooltipText = "Ends only the server process this manager started. Save the world in the server first; unsaved changes are lost." };
        _stop.Pressed += ConfirmStop; AddChild(_stop);
        AddChild(new VSeparator());
        var clients = new Button { Text = "Start clients" }; clients.Pressed += () => Run(StartClients); AddChild(clients);
        _count = new OptionButton(); for (int n = 1; n <= 4; n++) _count.AddItem($"× {n}", n); AddChild(_count);
        Rebuild();
        _poll = new Godot.Timer { WaitTime = 2, Autostart = true }; _poll.Timeout += Poll; AddChild(_poll); Poll();
    }
    public override void _ExitTree() { _generation++; _poll?.Stop(); _connection?.Dispose(); _connection = null; _connect = null; }
    private bool _loadFailed;
    private void Save() { if (_loadFailed) throw new InvalidDataException("Repair the existing profiles.json before saving; it was not overwritten."); _profiles.Save(ListPath); }
    private void Rebuild()
    {
        _server.Clear(); foreach (var s in _profiles.Servers) _server.AddItem(s.Name);
        int selected = _profiles.Servers.FindIndex(s => s.Id == _profiles.Selected);
        if (selected < 0 && _profiles.Servers.Count > 0) { selected = 0; _profiles.Selected = _profiles.Servers[0].Id; }
        if (selected >= 0) _server.Selected = selected;
        _generation++;
    }
    private void Run(Action action) { try { action(); } catch (Exception e) { _status.Text = e.Message; GD.PushError(e.Message); } }
    private void Poll()
    {
        var s = Selected;
        if (_busy || s == null || !IsInsideTree()) return;
        try
        {
            if (_connect != null && (_connectingId != s.Id || _connect.IsCompleted || DateTime.UtcNow >= _deadline))
            {
                bool online = _connectingId == s.Id && _connect.IsCompletedSuccessfully;
                _ = _connect.Exception; // Observe failures; no addon continuation survives assembly reload.
                _connection.Dispose(); _connection = null; _connect = null;
                bool managed = ManagedServerProcess.Running(State(s));
                _status.Text = managed ? "Managed - running" : online ? "Online - external" : "Offline";
                _status.TooltipText = $"{s.Host}:{s.Port}";
                _start.Disabled = online || managed || string.IsNullOrEmpty(s.Executable);
                _stop.Disabled = !managed;
            }
            if (_connect == null)
            {
                _connection = new TcpClient(); _connectingId = s.Id;
                _deadline = DateTime.UtcNow.AddSeconds(1);
                _connect = _connection.ConnectAsync(s.Host, s.Port);
            }
        }
        catch (Exception e) { _connection?.Dispose(); _connection = null; _connect = null; _status.Text = e.Message; }
    }
    private void Start()
    {
        var s = Selected; if (s == null || _busy) return;
        ManagedServerProcess.Start(s, State(s));
        _start.Disabled = true; _status.Text = "Started " + s.Name; Poll();
    }
    private void ConfirmStop()
    {
        var s = Selected; if (s == null || _busy) return;
        var dialog = new ConfirmationDialog { Title = "Stop " + s.Name, DialogText = "Save the world in the server first. Stopping ends this managed process and loses unsaved changes.", OkButtonText = "Stop server" };
        AddChild(dialog); dialog.Canceled += dialog.QueueFree;
        dialog.Confirmed += () =>
        {
            dialog.QueueFree(); _busy = true;
            try { ManagedServerProcess.Stop(State(s)); }
            catch (Exception e) { if (IsInsideTree()) _status.Text = e.Message; }
            finally { _busy = false; Poll(); }
        };
        dialog.PopupCentered();
    }
    private string Engine()
    {
        string path = OS.GetExecutablePath();
        if (OperatingSystem.IsWindows() && !path.EndsWith("_console.exe", StringComparison.OrdinalIgnoreCase))
            path = Path.Combine(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path) + "_console.exe");
        if (!File.Exists(path)) throw new FileNotFoundException("The blocking Godot console executable is required", path);
        return path;
    }
    private void StartClients()
    {
        var s = Selected ?? throw new InvalidOperationException("Select a server first");
        if (!File.Exists(Path.Combine(s.ClientProject, "project.godot"))) throw new InvalidDataException("Choose the client's Godot project folder");
        if (!string.IsNullOrEmpty(s.ClientData) && !Directory.Exists(s.ClientData)) throw new InvalidDataException("Client data folder does not exist");
        if (!string.IsNullOrEmpty(s.ContentLock) && (!File.Exists(s.ContentLock) || !Directory.Exists(s.ContentStore))) throw new InvalidDataException("Choose an existing content lock and installed content store");
        for (int n = 0; n < _count.GetSelectedId(); n++)
        {
            string cache = Path.Combine(Root, s.Id, "clients", n.ToString(), "cache"); Directory.CreateDirectory(cache);
            var psi = new ProcessStartInfo(Engine()) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = s.ClientProject };
            foreach (string arg in new[] { "--path", s.ClientProject, "--", "--cache-dir", cache }) psi.ArgumentList.Add(arg);
            // Only this launch is changed; client settings/accounts/cache are separate per server and slot.
            foreach (string key in psi.Environment.Keys.Where(k => k.StartsWith("UO_", StringComparison.Ordinal) && (k.Contains("PROBE", StringComparison.Ordinal) || k.StartsWith("UO_CONTENT_", StringComparison.Ordinal) || k == "UO_CUSTOM_DATA")).ToArray()) psi.Environment.Remove(key);
            psi.Environment["UO_SHARD_HOST"] = s.Host; psi.Environment["UO_SHARD_PORT"] = s.Port.ToString();
            psi.Environment["UO_CACHE_DIR"] = cache;
            psi.Environment["UO_CONTENT_STORE"] = string.IsNullOrEmpty(s.ContentStore) ? Path.Combine(Root, s.Id, "store") : s.ContentStore;
            psi.Environment["UO_CLIENT_DATA"] = s.ClientData;
            if (!string.IsNullOrEmpty(s.ContentLock)) { psi.Environment["UO_CONTENT_LOCK"] = s.ContentLock; psi.Environment["UO_CONTENT_STORE"] = s.ContentStore; }
            Process.Start(psi)?.Dispose();
        }
        _status.Text = "Clients started for " + s.Name;
    }
    private void Manage()
    {
        var window = new ServerManagerWindow(); AddChild(window);
        window.Open(_profiles, Selected, State, Engine, () => { Save(); Rebuild(); Poll(); });
    }
}
#endif
