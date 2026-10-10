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
public partial class RunBar : VBoxContainer
{
    private ServerProfiles _profiles;
    private ClientRegistry _clients;
    private OptionButton _server;
    private MenuButton _client;
    private int _count = 1;
    private readonly List<string> _clientIds = new();
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
        int index = _clientIds.IndexOf(id);
        if (index < 0) throw new ArgumentException("No such client");
        ClientChosen(index);
    }

    /// <summary>Re-reads the profile list after another part of the editor (the UO Store tab) changed it.</summary>
    public void ReloadProfiles()
    {
        try { _profiles = ServerProfiles.Load(ListPath); ClientRegistry.Reset(); _clients = ClientRegistry.Current; _loadFailed = false; Rebuild(); Poll(); }
        catch (Exception e) { Say(e.Message); }
    }

    /// <summary>The visible controls of the bar that lie outside <paramref name="visible"/>, by name (empty when it all fits).</summary>
    internal List<string> Outside(Rect2 visible)
    {
        var outside = new List<string>();
        foreach (Node child in GetChildren())
            if (child is Control c && c.Visible && !visible.Encloses(c.GetGlobalRect())) outside.Add(child is Button b ? b.Text : child.Name);
        return outside;
    }

    /// <summary>How many server dropdowns the bar holds (one).</summary>
    internal int ServerLists => GetChildren().Count(c => c is OptionButton);

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
    /// same profile again. Throws when the picked server is not one this manager runs. <paramref name="whileStopped"/>
    /// runs between the stop and the start (the Settings form writes the server's files then, AD4); the server is
    /// started again whatever it throws, and the exception is passed on after the start.
    /// </summary>
    internal void RestartSelected(Action whileStopped = null)
    {
        var s = Selected ?? throw new InvalidOperationException("Select a server first");
        if (!ManagedServerProcess.Running(State(s))) throw new InvalidOperationException($"{s.Name} was not started from the run bar");
        _busy = true;
        Exception during = null;
        try
        {
            ManagedServerProcess.Stop(State(s));
            try { whileStopped?.Invoke(); } catch (Exception e) { during = e; }
            ManagedServerProcess.Start(s, State(s));
            if (during != null) throw new InvalidOperationException(during.Message, during);
            _status.Text = "Restarted " + s.Name;
        }
        finally { _busy = false; _generation++; Poll(); }
    }
    public void StartClientsNow() => Run(() => StartClients(_count));

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
        _server = new OptionButton { TooltipText = "Saved server profiles. Each names its server files and its default client.",
            ClipText = true, FitToLongestItem = false, CustomMinimumSize = new Vector2(210, 0), TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
        _server.ItemSelected += i => ServerChosen((int)i);
        AddChild(_server);
        var manage = new Button { Text = "Servers", TooltipText = "Manage servers: add, edit and remove server and client profiles" }; manage.Pressed += Manage; AddChild(manage);
        _status = new Label { Text = "●", TooltipText = "Checking…", MouseFilter = MouseFilterEnum.Stop }; Dot(new Color(0.6f, 0.6f, 0.6f));
        AddChild(_status);
        _warn = new Label { Visible = false }; _warn.AddThemeColorOverride("font_color", new Color(0.95f, 0.7f, 0.2f)); AddChild(_warn);
        _start = new Button { Text = "Start", TooltipText = "Start the chosen server" }; _start.Pressed += () => Run(Start); AddChild(_start);
        _stop = new Button { Text = "Stop", TooltipText = "Ends only the server process this manager started. Save the world in the server first; unsaved changes are lost." };
        _stop.Pressed += ConfirmStop; AddChild(_stop);
        _client = new MenuButton { Text = "Client", Flat = false, TooltipText = "Start one to four clients on the chosen server, or choose which client to start. A client names its program and UO data." };
        _client.GetPopup().IdPressed += ClientMenu;
        AddChild(_client);
        Rebuild();
        GetWindow().SizeChanged += RefitHeader; Callable.From(RefitHeader).CallDeferred();
        _poll = new Godot.Timer { WaitTime = 2, Autostart = true }; _poll.Timeout += Poll; _poll.Timeout += FitHeader; AddChild(_poll); Poll();
    }
    public override void _ExitTree() { if (GetWindow() != null && GetWindow().IsConnected(Window.SignalName.SizeChanged, Callable.From(RefitHeader))) GetWindow().SizeChanged -= RefitHeader; ShowTabNames(); _generation++; _poll?.Stop(); _connection?.Dispose(); _connection = null; _connect = null; }
    // The editor's own header cannot scroll or wrap: when the main-screen tabs and the run bar do not fit the window,
    // the tabs that are not open show their icon alone (the name is the tooltip) until they do.
    // The name is hidden, never cleared: the editor finds a main screen by its button's text
    // (EditorInterface.SetMainScreenEditor), so a blank button cannot be opened by name.
    private readonly List<(Button Button, bool Clip, HorizontalAlignment Icon)> _tabs = new();
    private static readonly string[] TabFontColors = { "font_color", "font_hover_color", "font_pressed_color", "font_hover_pressed_color", "font_focus_color", "font_disabled_color" };
    private void RefitHeader() { _open = ""; FitHeader(); }
    private bool _fitting;
    private string _open = "";
    private async void FitHeader()
    {
        if (!IsInsideTree() || _fitting) return;
        var root = EditorInterface.Singleton.GetBaseControl();
        var first = FindTab(root, "World") ?? _tabs.Select(t => t.Button).FirstOrDefault();
        if (first?.GetParent() is not Container strip) return;
        string open = strip.GetChildren().OfType<Button>().FirstOrDefault(b => b.ButtonPressed)?.TooltipText ?? "";
        string now = string.Join("|", strip.GetChildren().OfType<Button>().Where(b => b.ButtonPressed).Select(b => b.Name)) + GetWindow().Size.X;
        if (now == _open) return;
        _open = now; _fitting = true;
        try
        {
            ShowTabNames(); _server.CustomMinimumSize = new Vector2(210, 0);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (!IsInsideTree() || Right() <= GetWindow().Size.X) return;
            foreach (Node n in strip.GetChildren())
                if (n is Button b && b.Text.Length > 0 && b.Icon != null && !b.ButtonPressed)
                {
                    // A centred icon sizes the button to the icon alone (no icon-to-text gap for the hidden name).
                    _tabs.Add((b, b.ClipText, b.IconAlignment)); b.TooltipText = b.Text; b.ClipText = true; b.IconAlignment = HorizontalAlignment.Center;
                    foreach (string c in TabFontColors) b.AddThemeColorOverride(c, Colors.Transparent);
                }
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (IsInsideTree() && Right() > GetWindow().Size.X) _server.CustomMinimumSize = new Vector2(100, 0); // the name is trimmed, its tooltip is whole
        }
        finally { _fitting = false; }
    }
    private float Right() { float end = GetGlobalRect().End.X; foreach (Node c in GetChildren()) if (c is Control k && k.Visible) end = Math.Max(end, k.GetGlobalRect().End.X); return end; }
    private void ShowTabNames()
    {
        foreach (var (b, clip, icon) in _tabs)
            if (GodotObject.IsInstanceValid(b))
            {
                b.ClipText = clip; b.IconAlignment = icon;
                foreach (string c in TabFontColors) b.RemoveThemeColorOverride(c);
            }
        _tabs.Clear();
    }
    private static Button FindTab(Node n, string text)
    {
        if (n is Button b && b.Text == text && b.ToggleMode && b.GetParent() is HBoxContainer) return b;
        foreach (Node c in n.GetChildren()) { var f = FindTab(c, text); if (f != null) return f; }
        return null;
    }
    private bool _loadFailed;
    private void Save() { if (_loadFailed) throw new InvalidDataException("Repair the existing servers.json before saving; it was not overwritten."); _profiles.Save(ListPath); }
    private void ServerChosen(int index)
    {
        _profiles.Selected = _profiles.Servers[index].Id; _profiles.SelectedClient = null; // a server brings its own default client
        Run(Save); RebuildClients(); _generation++; Poll();
    }
    private void ClientChosen(int index)
    {
        string id = index >= 0 && index < _clientIds.Count ? _clientIds[index] : "";
        _profiles.SelectedClient = string.IsNullOrEmpty(id) ? null : id;
        Run(Save); RebuildClients();
    }
    // Menu ids: 1 to 4 start that many clients; 100 + n picks the n-th client profile.
    private void ClientMenu(long id)
    {
        if (id >= 100) { ClientChosen((int)id - 100); return; }
        _count = (int)id; Run(() => StartClients(_count));
    }
    private void Rebuild()
    {
        _server.Clear();
        // Two profiles may share a name (an earlier first-run seed or a migrated copy): label them apart, never rename them on disk.
        foreach (var s in _profiles.Servers)
        {
            int same = _profiles.Servers.Count(o => o.Name == s.Name);
            _server.AddItem(same > 1 ? $"{s.Name} #{_profiles.Servers.Where(o => o.Name == s.Name).ToList().IndexOf(s) + 1}" : s.Name);
            _server.SetItemTooltip(_server.ItemCount - 1, $"{s.Host}:{s.Port}");
        }
        int selected = _profiles.Servers.FindIndex(s => s.Id == _profiles.Selected);
        if (selected < 0 && _profiles.Servers.Count > 0) { selected = 0; _profiles.Selected = _profiles.Servers[0].Id; }
        if (selected >= 0) _server.Selected = selected;
        RebuildClients();
        _generation++;
    }
    private void RebuildClients()
    {
        var popup = _client.GetPopup(); popup.Clear(); _clientIds.Clear();
        for (int n = 1; n <= 4; n++) popup.AddItem(n == 1 ? "Start 1 client" : $"Start {n} clients", n);
        popup.AddSeparator("Client");
        string current = _profiles.ClientFor(Selected);
        foreach (var c in _clientRegistryList())
        {
            _clientIds.Add(c.Id);
            popup.AddRadioCheckItem(c.Name, 100 + _clientIds.Count - 1);
            popup.SetItemChecked(popup.ItemCount - 1, c.Id == current);
        }
        if (_clientIds.Count == 0) { popup.AddItem("No client", 99); popup.SetItemDisabled(popup.ItemCount - 1, true); }
        UpdateWarning();
    }
    private IEnumerable<ClientProfile> _clientRegistryList() => _clients.Clients;
    private void UpdateWarning()
    {
        string warning = ClientRegistry.Mismatch(Selected?.ExpectedClientVersion, CurrentClient);
        _warn.Text = warning == null ? "" : "Mismatch"; _warn.TooltipText = warning ?? ""; _warn.Visible = warning != null;
        if (warning != null) _warnDetail = warning; else _warnDetail = "";
    }
    /// <summary>A message on the status dot (amber); the whole text is its tooltip.</summary>
    private void Say(string text) { if (_status == null) return; _status.TooltipText = text; Dot(new Color(0.95f, 0.7f, 0.2f)); }
    private void Dot(Color color) => _status.AddThemeColorOverride("font_color", color);
    private void Run(Action action) { try { action(); } catch (Exception e) { Say(e.Message); GD.PushError(e.Message); } }
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
                Dot(managed || online ? new Color(0.4f, 0.85f, 0.4f) : new Color(0.6f, 0.6f, 0.6f));
                _status.TooltipText = (managed ? "Managed server, running. " : online ? "An external server answers. " : "Nothing answers. ") + $"{s.Host}:{s.Port}";
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
        catch (Exception e) { _connection?.Dispose(); _connection = null; _connect = null; Say(e.Message); }
    }
    private void Start()
    {
        var s = Selected; if (s == null || _busy) return;
        ManagedServerProcess.Start(s, State(s));
        _start.Disabled = true; Dot(new Color(0.4f, 0.85f, 0.4f));
        _status.TooltipText = "Started. Console: " + LogSources.ServerConsole(s.Id); Poll();
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
            catch (Exception e) { if (IsInsideTree()) Say(e.Message); }
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
    private void StartClients(int count)
    {
        var s = Selected ?? throw new InvalidOperationException("Select a server first");
        var c = CurrentClient ?? throw new InvalidOperationException("Select a client first (Manage servers, Clients)");
        int started = 0;
        for (int n = 1; n <= count; n++)
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
        Say(started == 0 ? $"{c.Name} is already running in every slot" : $"{c.Name} started for {s.Name}");
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
