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
using GUO.Workspace;

[Tool]
public partial class RunBar : HBoxContainer
{
    private ServerProfiles _profiles;
    private ClientRegistry _clients;
    private OptionButton _server, _client, _count;
    private Button _start, _stop;
    private Label _status, _warn;
    private Godot.Timer _poll;
    private bool _busy;
    private TcpClient _connection;
    private Task _connect;
    private string _connectingId;
    private DateTime _deadline;
    private long _generation;
    private string ListPath => EditorWorkspace.ServersPath;
    private ServerProfile Selected => _profiles?.Servers.FirstOrDefault(s => s.Id == _profiles.Selected);
    private string State(ServerProfile s) => Path.Combine(Workspace.ServerHome(s.Id), "process.json");
    private ClientProfile CurrentClient => _clients?.Find(_profiles?.ClientFor(Selected));

    /// <summary>The client the next run starts with (ADR-0032): the choice in the Client list, else the server's default. Null when there is none.</summary>
    internal ClientProfile EffectiveClient => CurrentClient;

    /// <summary>The mismatch warning of the selected server and client, or "" (the label beside the lists).</summary>
    public string Warning => _warnDetail;
    private string _warnDetail = "";

    /// <summary>Choose a server by id in the list, as the person would (its default client is selected with it).</summary>
    public void SelectServer(string id)
    {
        int index = _profiles.Servers.FindIndex(s => s.Id == id);
        if (index < 0) throw new ArgumentException("No such server");
        _server.Select(index); ServerChosen(index);
    }

    /// <summary>Choose a client by id in the Client list, as the person would; overrides the server's default.</summary>
    public void SelectClient(string id)
    {
        int index = _client.GetItemCount() - 1;
        for (; index >= 0; index--) if ((string)_client.GetItemMetadata(index) == id) break;
        if (index < 0) throw new ArgumentException("No such client");
        _client.Select(index); ClientChosen(index);
    }

    /// <summary>Re-reads the profile list after another part of the editor (the UO Store tab) changed it.</summary>
    public void ReloadProfiles()
    {
        try { _profiles = ServerProfiles.Load(ListPath); ClientRegistry.Reset(); _clients = ClientRegistry.Current; _loadFailed = false; Rebuild(); Poll(); }
        catch (Exception e) { _status.Text = e.Message; }
    }

    public void StartServerNow() { if (!_start.Disabled) Run(Start); }

    /// <summary>Every saved server profile (the Admin tab's scripted check picks one by its folder).</summary>
    internal IReadOnlyList<ServerProfile> Servers => _profiles?.Servers ?? new List<ServerProfile>();

    /// <summary>Stops the picked server if this manager started it, with no dialog (scripted checks; save first).</summary>
    internal void StopSelectedNow() { var s = Selected; if (s != null) { ManagedServerProcess.Stop(State(s)); Poll(); } }

    /// <summary>The server picked in the list (the Admin tab shows and restarts it), or null.</summary>
    internal ServerProfile SelectedServer => Selected;

    /// <summary>Whether this manager started the picked server and it still runs (only then can it be restarted from here).</summary>
    internal bool SelectedManaged => Selected is { } s && ManagedServerProcess.Running(State(s));

    /// <summary>
    /// The Admin tab's Restart, after it saved the world: stops the exact process this manager started and starts the
    /// same profile again. Throws when the picked server is not one this manager runs.
    /// </summary>
    internal void RestartSelected()
    {
        var s = Selected ?? throw new InvalidOperationException("Select a server first");
        if (!ManagedServerProcess.Running(State(s))) throw new InvalidOperationException($"{s.Name} was not started from the run bar");
        _busy = true;
        try
        {
            ManagedServerProcess.Stop(State(s));
            ManagedServerProcess.Start(s, State(s));
            _status.Text = "Restarted " + s.Name;
        }
        finally { _busy = false; _generation++; Poll(); }
    }
    public void StartClientsNow() => Run(StartClients);

    public override void _Ready()
    {
        try
        {
            EditorWorkspace.Ensure();
            _profiles = ServerProfiles.Load(ListPath);
            ClientRegistry.Reset(); _clients = ClientRegistry.Current;
            if (!File.Exists(ListPath))
            {
                string data = EditorData.Setting("UO_CLIENT_DATA", "");
                if (!Path.IsPathFullyQualified(data)) data = "";
                string client = _clients.FindOrAddProject("This project", EditorWorkspace.HostProject, data, "", EditorData.Setting("UO_CLIENT_VERSION", ""), null, "manual").Id;
                _clients.Save();
                _profiles.Servers.Add(new ServerProfile { Name = "Configured shard", Host = EditorData.Setting("UO_SHARD_HOST", "127.0.0.1"),
                    Port = int.TryParse(EditorData.Setting("UO_SHARD_PORT", "2593"), out int port) ? port : 2593, DefaultClient = client });
                string home = Path.Combine(EditorData.RepoRoot, "build", "shard_private");
                if (File.Exists(Path.Combine(home, "state.json")))
                {
                    using var state = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(home, "state.json")));
                    _profiles.Servers.Add(new ServerProfile { Name = "Private shard", Host = "127.0.0.1", Port = state.RootElement.GetProperty("port").GetInt32(),
                        ServerDirectory = home, Executable = Path.Combine(home, "ModernUO.exe"), DefaultClient = client });
                }
                _profiles.Selected = _profiles.Servers[0].Id; _profiles.Save(ListPath);
            }
        }
        catch (Exception e) { GD.PushError("Server profiles: " + e.Message); _profiles = new(); _clients ??= new ClientRegistry(); _loadFailed = true; }
        _server = new OptionButton { TooltipText = "Saved server profiles. Each names its server files and its default client." };
        _server.ItemSelected += i => ServerChosen((int)i);
        AddChild(_server);
        _client = new OptionButton { TooltipText = "The client to start: the server's default, or another. A client names its program and UO data." };
        _client.ItemSelected += i => ClientChosen((int)i);
        AddChild(_client);
        var manage = new Button { Text = "Manage servers" }; manage.Pressed += Manage; AddChild(manage);
        _status = new Label { Text = "Checking…" }; AddChild(_status);
        _warn = new Label { Visible = false }; _warn.AddThemeColorOverride("font_color", new Color(0.95f, 0.7f, 0.2f)); AddChild(_warn);
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
    private void Save() { if (_loadFailed) throw new InvalidDataException("Repair the existing servers.json before saving; it was not overwritten."); _profiles.Save(ListPath); }
    private void ServerChosen(int index)
    {
        _profiles.Selected = _profiles.Servers[index].Id; _profiles.SelectedClient = null; // a server brings its own default client
        Run(Save); RebuildClients(); _generation++; Poll();
    }
    private void ClientChosen(int index)
    {
        string id = (string)_client.GetItemMetadata(index);
        _profiles.SelectedClient = string.IsNullOrEmpty(id) ? null : id;
        Run(Save); UpdateWarning();
    }
    private void Rebuild()
    {
        _server.Clear(); foreach (var s in _profiles.Servers) _server.AddItem(s.Name);
        int selected = _profiles.Servers.FindIndex(s => s.Id == _profiles.Selected);
        if (selected < 0 && _profiles.Servers.Count > 0) { selected = 0; _profiles.Selected = _profiles.Servers[0].Id; }
        if (selected >= 0) _server.Selected = selected;
        RebuildClients();
        _generation++;
    }
    private void RebuildClients()
    {
        _client.Clear();
        foreach (var c in _clients.Clients) { _client.AddItem(c.Name); _client.SetItemMetadata(_client.GetItemCount() - 1, c.Id); }
        if (_clients.Clients.Count == 0) { _client.AddItem("No client"); _client.SetItemMetadata(0, ""); }
        int at = _clients.Clients.FindIndex(c => c.Id == _profiles.ClientFor(Selected));
        _client.Selected = at >= 0 ? at : 0;
        UpdateWarning();
    }
    private void UpdateWarning()
    {
        string warning = ClientRegistry.Mismatch(Selected?.ExpectedClientVersion, CurrentClient);
        _warn.Text = warning == null ? "" : "Mismatch"; _warn.TooltipText = warning ?? ""; _warn.Visible = warning != null;
        if (warning != null) _warnDetail = warning; else _warnDetail = "";
    }
    private void Run(Action action) { try { action(); } catch (Exception e) { if (_status != null) _status.Text = e.Message; GD.PushError(e.Message); } }
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
        _start.Disabled = true; _status.Text = "Started " + s.Name;
        _status.TooltipText = "Console: " + LogSources.ServerConsole(s.Id); Poll();
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

    /// <summary>
    /// What starting slot <paramref name="slot"/> (1 to 4) of the selected server and client would run. Nothing is started
    /// (the dry run the smoke check uses); the slot folder is <c>runs/server/client/slot-n</c> of the workspace.
    /// </summary>
    internal LaunchPlan PlanSlot(int slot)
    {
        var s = Selected ?? throw new InvalidOperationException("Select a server first");
        var c = CurrentClient ?? throw new InvalidOperationException("Select a client first (Manage servers, Clients)");
        string store = EditorWorkspace.ContentStore(s);
        if (c.Kind == ClientKinds.External) return ClientLaunch.PlanExternal(c, s.Host, s.Port, s.Id, slot);
        if (c.Kind == ClientKinds.GuoBuild) return ClientLaunch.PlanBuild(c, s.Host, s.Port, s.Id, slot, store, s.ContentLock);
        string project = string.IsNullOrEmpty(c.Program) ? EditorWorkspace.HostProject : c.Program;
        if (!File.Exists(Path.Combine(project, "project.godot"))) throw new InvalidDataException("Choose the client's Godot project folder");
        if (!string.IsNullOrEmpty(c.BaseData) && !Directory.Exists(c.BaseData)) throw new InvalidDataException("Client data folder does not exist");
        if (!string.IsNullOrEmpty(s.ContentLock) && (!File.Exists(s.ContentLock) || !Directory.Exists(s.ContentStore))) throw new InvalidDataException("Choose an existing content lock and installed content store");
        string slotDir = Workspace.RunSlot(s.Id, c.Id, slot);
        var plan = new LaunchPlan { Kind = c.Kind, FileName = Engine(), SlotDir = slotDir, WorkingDir = project, Console = Workspace.ClientConsole(s.Id, c.Id, slot),
            Environment = ClientLaunch.GuoEnvironment(s.Host, s.Port, slotDir, c, store, s.ContentLock) };
        plan.Arguments.AddRange(new[] { "--path", project, "--", "--cache-dir", Path.Combine(slotDir, "cache") });
        plan.Arguments.AddRange(c.Arguments);
        return plan;
    }
    private void StartClients()
    {
        var s = Selected ?? throw new InvalidOperationException("Select a server first");
        var c = CurrentClient ?? throw new InvalidOperationException("Select a client first (Manage servers, Clients)");
        int started = 0;
        for (int n = 1; n <= _count.GetSelectedId(); n++)
        {
            var plan = PlanSlot(n);
            Directory.CreateDirectory(plan.SlotDir);
            if (plan.Kind != ClientKinds.External) Directory.CreateDirectory(Path.Combine(plan.SlotDir, "cache"));
            // A tracked client (a build or an external program) is one exact process per slot; a running slot is left alone.
            bool tracked = plan.Kind != ClientKinds.GuoProject;
            if (tracked && ManagedServerProcess.Running(plan.State)) continue;
            ClientLaunch.WritePlugins(plan.SlotDir, c);
            Spawn(plan, tracked);
            started++;
        }
        _status.Text = started == 0 ? $"{c.Name} is already running in every slot" : $"{c.Name} started for {s.Name}";
    }
    private static void Spawn(LaunchPlan plan, bool tracked)
    {
        ProcessStartInfo psi;
        string[] args = plan.Arguments.ToArray();
        string console = plan.Console;
        if (console != null) Directory.CreateDirectory(Path.GetDirectoryName(console));
        // The client's console goes to a file the Logs dock tails (a client has no stdout the editor could keep).
        if (console != null && OperatingSystem.IsWindows() && !new[] { plan.FileName, console }.Concat(args).Any(a => a.Contains('"') || a.Contains('%') || a.Contains('^') || a.Contains('&')))
        {
            psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = plan.WorkingDir };
            psi.Arguments = "/d /s /c \"\"" + plan.FileName + "\" " + string.Join(" ", args.Select(a => "\"" + a + "\"")) + " > \"" + console + "\" 2>&1\"";
        }
        else
        {
            psi = plan.ToStartInfo();
        }
        if (plan.Kind != ClientKinds.External)
        {
            // Only this launch is changed; client settings/accounts/cache are separate per server, client and slot.
            foreach (string key in psi.Environment.Keys.Where(ClientLaunch.Inherited).ToArray()) psi.Environment.Remove(key);
            foreach (var (k, v) in plan.Environment) psi.Environment[k] = v;
        }
        if (tracked) ManagedServerProcess.Start(psi, plan.State);
        else Process.Start(psi)?.Dispose();
    }
    private void Manage()
    {
        var window = new ServerManagerWindow(); AddChild(window);
        window.Open(_profiles, Selected, State, Engine, () => { Save(); Rebuild(); Poll(); }, _clients);
    }
}
#endif
