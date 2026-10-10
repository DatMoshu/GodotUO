#if TOOLS
// SPDX-License-Identifier: BSD-2-Clause
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Godot;
using GUO.Workspace;

/// <summary>
/// The Admin main-screen tab (sprint "Admin tab", AD1): the server picked in the run bar, its health in numbers
/// and in plain words, Save now and Restart, the god view of every player, NPC and spawner (AD2a,
/// <see cref="GodViewPanel"/>), and a log of everything the tab did, its times in UTC. It talks to the server's
/// editor bridge on the admin channel (ADR-0035): this workspace's admin token goes in the bridge hello and
/// nowhere else. Desktop editor only. The tab button comes from <see cref="GuoAdminPlugin"/>.
/// </summary>
/// <remarks>
/// Restart goes through the run bar's server manager: a save on the bridge, then the manager stops the exact
/// process it started and starts it again (with the bridge's token, <see cref="ShardSecrets.BridgeEnvironment"/>).
/// A server the run bar did not start cannot be restarted from here; the tab says so.
/// Later stories add the god view's actions (AD2b), commands (AD3), settings (AD4), accounts (AD5) and backups (AD6).
/// </remarks>
[Tool]
public partial class AdminView : VBoxContainer
{
    public const string TabName = "Admin";

    /// <summary>Where a restart is.</summary>
    public enum RestartPhase { None, Saving, WaitingForServer }

    private const double StatusSeconds = 5;
    private const double ReconnectSeconds = 2;
    private const double RestartTimeoutSeconds = 240;

    private readonly ShardLink _link = new();
    private Label _title, _connection;
    private LineEdit _host;
    private SpinBox _port;
    private Button _connect, _save, _restart, _refresh;
    private readonly Dictionary<string, Label> _values = new();
    private RichTextLabel _plain, _log;
    private GodViewPanel _godView;
    private double _statusClock, _reconnectClock;
    private DateTime _statusAt;
    private DateTime _restartStarted;
    private bool _autoTried;
    private bool _userDisconnected;
    private int _req;
    private readonly StringBuilder _logText = new();

    /// <summary>The run bar: the selected server's name, and Restart through its manager.</summary>
    public RunBar Run { get; set; }

    /// <summary>The radar of a map file (MapPanel.RadarFor), for the god view's map.</summary>
    public Func<int, Image> RadarSource { get; set; }

    /// <summary>The god view (AD2a).</summary>
    public GodViewPanel GodView => _godView;

    /// <summary>The access level the server granted this tab, or null (no admin channel).</summary>
    public string Granted { get; private set; }

    /// <summary>Why there is no admin channel, in plain words, or null.</summary>
    public string AdminProblem { get; private set; }

    /// <summary>The last admin_status reply.</summary>
    public JsonNode LastStatus { get; private set; }

    /// <summary>The last admin_save reply.</summary>
    public JsonNode LastSave { get; private set; }

    /// <summary>How many status replies arrived (the smoke check waits on it).</summary>
    public int StatusReplies { get; private set; }

    public bool Saving { get; private set; }

    public RestartPhase Restarting { get; private set; }

    /// <summary>How long the last restart took, from Restart to the bridge answering again (seconds), or -1.</summary>
    public double LastRestartSeconds { get; private set; } = -1;

    public bool Connected => _link.Connected;

    /// <summary>Everything the tab logged, as plain text (the smoke check greps it for secrets).</summary>
    public string LogText => _logText.ToString();

    /// <summary>The plain-language panel's text.</summary>
    public string PlainText => _plain?.GetParsedText() ?? "";

    public AdminView()
    {
        Name = "UOAdmin";
        SizeFlagsVertical = SizeFlags.ExpandFill;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
    }

    public override void _Ready()
    {
        if (_log != null)
        {
            return;
        }

        _title = new Label { Text = "Admin", ClipText = true };
        _title.AddThemeFontSizeOverride("font_size", Px(18));
        AddChild(_title);

        var row = new HBoxContainer();
        AddChild(row);
        row.AddChild(new Label { Text = "Editor bridge" });
        _host = new LineEdit { Text = "127.0.0.1", CustomMinimumSize = new Vector2(Px(140), 0), TooltipText = "The server's editor bridge (loopback; a remote server is reached through an ssh tunnel)." };
        row.AddChild(_host);
        _port = new SpinBox { MinValue = 1, MaxValue = 65535, Value = DefaultBridgePort() };
        row.AddChild(_port);
        _connect = new Button { Text = "Connect" };
        _connect.Pressed += () =>
        {
            if (_link.Connected)
            {
                _userDisconnected = true;
                Disconnect("disconnected");
            }
            else
            {
                _userDisconnected = false;
                Connect();
            }
        };
        row.AddChild(_connect);
        _connection = new Label { Text = "not connected", SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true };
        row.AddChild(_connection);

        var split = new HSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        AddChild(split);

        // Left: the Health panel.
        var health = new VBoxContainer { CustomMinimumSize = new Vector2(Px(280), 0) };
        split.AddChild(health);
        health.AddChild(Heading("Health"));
        var grid = new GridContainer { Columns = 2 };
        health.AddChild(grid);
        foreach (string label in new[] { "Uptime", "Online", "Items", "Mobiles", "Memory", "Last save", "World", "Server" })
        {
            grid.AddChild(new Label { Text = label, Modulate = new Color(1, 1, 1, 0.7f) });
            var value = new Label { Text = "-" };
            grid.AddChild(value);
            _values[label] = value;
        }

        health.AddChild(new HSeparator());
        _save = new Button { Text = "Save now", TooltipText = "Saves the world now, as [save in game does. Players stay on." };
        _save.Pressed += () => SaveNow();
        health.AddChild(_save);
        _restart = new Button { Text = "Restart", TooltipText = "Saves the world, then the run bar stops this server and starts it again." };
        _restart.Pressed += ConfirmRestart;
        health.AddChild(_restart);
        _refresh = new Button { Text = "Refresh", TooltipText = "Asks the server for its numbers now (they also refresh every few seconds)." };
        _refresh.Pressed += () => RequestStatus();
        health.AddChild(_refresh);

        // Under it, the same in plain words.
        health.AddChild(Heading("In plain words"));
        _plain = new RichTextLabel { BbcodeEnabled = true, FitContent = false, SizeFlagsVertical = SizeFlags.ExpandFill, SelectionEnabled = true };
        health.AddChild(_plain);

        // Centre: the god view.
        var centre = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        split.AddChild(centre);
        centre.AddChild(Heading("God view"));
        _godView = new GodViewPanel
        {
            Send = msg =>
            {
                if (!_link.Connected || Granted == null)
                {
                    return false;
                }

                _link.Send(msg);
                return true;
            },
            RadarSource = facet => RadarSource?.Invoke(facet),
        };
        _godView.Logged += Log;
        centre.AddChild(_godView);

        // Bottom: the log of everything the tab did.
        AddChild(Heading("Log"));
        _log = new RichTextLabel { ScrollFollowing = true, CustomMinimumSize = new Vector2(0, Px(140)), SelectionEnabled = true };
        AddChild(_log);

        VisibilityChanged += OnShown;
        UpdateView();
    }

    private static Label Heading(string text)
    {
        var l = new Label { Text = text };
        l.AddThemeFontSizeOverride("font_size", Px(15));
        return l;
    }

    // Sizes in the editor's own scale (a 4K display runs the editor at 2x).
    private static int Px(int size) => (int)Math.Round(size * EditorInterface.Singleton.GetEditorScale());

    private ServerProfile Server => Run?.SelectedServer;

    // The bridge port of the selected server when tools/editor_shard set it up, else the configured live port.
    private int DefaultBridgePort()
    {
        if (Server is { } s && ShardSecrets.BridgeEnvironment(s.ServerDirectory).TryGetValue("GUO_BRIDGE_PORT", out string p)
            && int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out int port))
        {
            return port;
        }

        return int.TryParse(EditorData.Setting("UO_EDITOR_LIVE_PORT", "2595"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int live) ? live : 2595;
    }

    // The first time the tab is shown, it connects by itself.
    private void OnShown()
    {
        if (IsVisibleInTree() && !_autoTried && !_link.Connected && !_userDisconnected)
        {
            _autoTried = true;
            _port.Value = DefaultBridgePort();
            Connect(quiet: true);
        }

        UpdateView();
    }

    /// <summary>Connects to the bridge with this workspace's admin token. False, with the reason in the log, if it cannot.</summary>
    public bool Connect(string host = null, int port = 0, bool quiet = false)
    {
        if (_log == null)
        {
            _Ready();
        }

        host ??= _host.Text;
        port = port > 0 ? port : (int)_port.Value;
        _host.Text = host;
        _port.Value = port;
        EditorWorkspace.Ensure();
        string token = ShardSecrets.BridgeAdminToken();
        Granted = null;
        AdminProblem = string.IsNullOrEmpty(token)
            ? "This workspace has no admin token yet. Run launchers\\shard\\run.bat or \"tools\\editor_shard\\run.py start\" once and it is made for you."
            : null;
        try
        {
            _link.Connect(host, port, "Admin tab (" + EditorData.Setting("UO_EDITOR_NAME", System.Environment.UserName) + ")", token);
            Log($"connected to the editor bridge on {host}:{port}" + (string.IsNullOrEmpty(token) ? ", without an admin token" : ""));
            return true;
        }
        catch (Exception e)
        {
            if (!quiet || Restarting == RestartPhase.None)
            {
                Log($"[color=orange]no editor bridge on {host}:{port}: {e.Message}[/color]");
            }

            AdminProblem ??= $"No editor bridge answers on {host}:{port}. Start the server from the run bar (or with tools\\editor_shard), then Connect.";
            UpdateView();
            return false;
        }
    }

    private void Disconnect(string why)
    {
        _link.Disconnect();
        Granted = null;
        _godView.OnClosed();
        Log(why);
        UpdateView();
    }

    /// <summary>Asks for the Health numbers.</summary>
    public bool RequestStatus()
    {
        if (!_link.Connected || Granted == null)
        {
            return false;
        }

        _statusClock = 0;
        _link.Send(new JsonObject { ["op"] = "admin_status", ["req"] = ++_req });
        return true;
    }

    /// <summary>Saves the world on the server; the reply arrives once the save is on disk.</summary>
    public bool SaveNow(string reason = null)
    {
        if (!_link.Connected || Granted == null || Saving)
        {
            Log("[color=orange]Save now needs the admin channel (connect first)[/color]");
            return false;
        }

        Saving = true;
        var msg = new JsonObject { ["op"] = "admin_save", ["req"] = ++_req };
        if (reason != null)
        {
            msg["reason"] = reason;
        }

        _link.Send(msg);
        Log(reason == "restart" ? "saving the world before the restart..." : "saving the world...");
        UpdateView();
        return true;
    }

    /// <summary>Why Restart cannot run for the selected server, or null when it can.</summary>
    public string RestartBlocker()
    {
        if (Run == null || Server == null)
        {
            return "No server is picked in the run bar.";
        }

        if (!Run.SelectedManaged)
        {
            return $"Restart is off: \"{Server.Name}\" was not started from the run bar, so the editor cannot stop and start it. "
                + "Stop it where it was started, then start it with the run bar's Start server.";
        }

        if (!_link.Connected || Granted == null)
        {
            return "Restart needs the admin channel: it saves the world first.";
        }

        return Restarting != RestartPhase.None ? "A restart is already under way." : null;
    }

    private void ConfirmRestart()
    {
        if (RestartBlocker() is { } why)
        {
            Log($"[color=orange]{why}[/color]");
            return;
        }

        var dialog = new ConfirmationDialog
        {
            Title = "Restart " + Server.Name,
            DialogText = "Restart saves the world, stops the server and starts it again.\nEveryone online is disconnected until it is back (usually under a minute).",
            OkButtonText = "Restart",
        };
        AddChild(dialog);
        dialog.Canceled += dialog.QueueFree;
        dialog.Confirmed += () =>
        {
            dialog.QueueFree();
            Restart();
        };
        dialog.PopupCentered();
    }

    /// <summary>Saves, then restarts the run bar's server (no dialog: the smoke check and the confirm call this).</summary>
    public bool Restart()
    {
        if (RestartBlocker() is { } why)
        {
            Log($"[color=orange]{why}[/color]");
            return false;
        }

        _restartStarted = DateTime.UtcNow;
        Restarting = RestartPhase.Saving;
        if (!SaveNow("restart"))
        {
            Restarting = RestartPhase.None;
            return false;
        }

        return true;
    }

    // The save before a restart is on disk: the run bar stops and starts the server.
    private void RestartAfterSave(bool saved)
    {
        if (!saved)
        {
            Restarting = RestartPhase.None;
            Log("[color=orange]the save failed, so the server was not restarted[/color]");
            return;
        }

        try
        {
            Log($"stopping {Server.Name}...");
            _link.Disconnect();
            Granted = null;
            _godView.OnClosed();
            Run.RestartSelected();
            Restarting = RestartPhase.WaitingForServer;
            _reconnectClock = 0;
            Log($"started {Server.Name} again; waiting for it to load the world...");
        }
        catch (Exception e)
        {
            Restarting = RestartPhase.None;
            Log($"[color=orange]restart failed: {e.Message}[/color]");
        }

        UpdateView();
    }

    public override void _Process(double delta)
    {
        if (_log == null)
        {
            return;
        }

        if (Restarting == RestartPhase.WaitingForServer && !_link.Connected)
        {
            _reconnectClock += delta;
            if ((DateTime.UtcNow - _restartStarted).TotalSeconds > RestartTimeoutSeconds)
            {
                Restarting = RestartPhase.None;
                Log($"[color=orange]the server did not answer within {RestartTimeoutSeconds:0} s of the restart; see the Logs dock[/color]");
                UpdateView();
            }
            else if (_reconnectClock >= ReconnectSeconds)
            {
                _reconnectClock = 0;
                Connect(quiet: true);
            }
        }

        if (_link.Connected && Granted != null && IsVisibleInTree())
        {
            _statusClock += delta;
            if (_statusClock >= StatusSeconds)
            {
                RequestStatus();
            }
        }

        for (JsonNode msg = _link.Poll(); msg != null; msg = _link.Poll())
        {
            Handle(msg);
        }

        // Ages ("saved 4 min ago") move between replies.
        if (LastStatus != null && Engine.GetProcessFrames() % 30 == 0)
        {
            UpdateTitle();
        }
    }

    private void Handle(JsonNode msg)
    {
        switch ((string)msg["op"])
        {
            case "hello":
                Granted = (string)msg["admin"];
                if (Granted != null)
                {
                    AdminProblem = null;
                    Log($"admin channel open: this tab may act as {Granted}");
                    if (Restarting == RestartPhase.WaitingForServer)
                    {
                        Restarting = RestartPhase.None;
                        LastRestartSeconds = Math.Round((DateTime.UtcNow - _restartStarted).TotalSeconds, 1);
                        Log($"the server is back, {LastRestartSeconds:0.#} s after Restart");
                    }

                    RequestStatus();
                    _link.Send(new JsonObject { ["op"] = "admin_audit", ["count"] = 10, ["req"] = ++_req });
                    if (msg["admin_ops"] is JsonArray ops && ops.Any(o => (string)o == "admin_godview"))
                    {
                        _godView.OnAdminOpen();
                    }
                    else
                    {
                        Log($"the god view needs GameMaster; this tab holds {Granted}");
                    }
                }
                else
                {
                    AdminProblem ??= (string)msg["admin_error"] switch
                    {
                        "this server has no admin token" => "This server was started without an admin token, so it offers map editing only. "
                            + "Start it from the run bar or with \"tools\\editor_shard\\run.py start\", which hand it this workspace's token.",
                        "admin token refused" => "The server refused this workspace's admin token: it was started with another one "
                            + "(another user's workspace, or an older secrets file). Restart it from here or with tools\\editor_shard.",
                        _ => "The server gave this tab no admin access.",
                    };
                    Log($"[color=orange]no admin channel: {(string)msg["admin_error"] ?? "no token sent"}[/color]");
                }

                break;
            case "admin_status":
                if (msg["ok"] is JsonNode ok && !(bool)ok)
                {
                    Log($"[color=orange]status refused: {(string)msg["error"]}[/color]");
                    break;
                }

                LastStatus = msg;
                StatusReplies++;
                _statusAt = DateTime.UtcNow;
                break;
            case "admin_save":
                Saving = false;
                LastSave = msg;
                bool saved = (bool?)msg["ok"] == true;
                Log(saved ? $"world saved in {(long?)msg["ms"] ?? 0} ms" : $"[color=orange]save failed: {(string)msg["error"]}[/color]");
                if (Restarting == RestartPhase.Saving)
                {
                    RestartAfterSave(saved);
                }
                else
                {
                    RequestStatus();
                }

                break;
            case "admin_audit":
                if (msg["entries"] is JsonArray entries && entries.Count > 0)
                {
                    Log($"the server's audit log, last {entries.Count}:");
                    foreach (JsonNode e in entries)
                    {
                        // The audit's times are UTC, as is the tab's clock.
                        string at = ((string)e["at"] ?? "").Length >= 19 ? ((string)e["at"]).Substring(11, 8) + "Z" : "";
                        Log($"    {at} {(string)e["editor"]}: {(string)e["op"]} {((bool?)e["ok"] == true ? "ran" : "refused")}"
                            + ((string)e["error"] is { } err ? $" ({err})" : ""));
                    }
                }

                break;
            case "admin_godview":
            case "admin_godview_find":
                _godView.Handle(msg);
                // Pushes come every second while something moves; the rest of the tab does not change with them.
                return;
            case "error":
                Log($"[color=orange]bridge: {(string)msg["error"]}[/color]");
                break;
            case "closed":
                Granted = null;
                _godView.OnClosed();
                if (Restarting == RestartPhase.None)
                {
                    Log("the server closed the connection");
                }

                break;
        }

        UpdateView();
    }

    private void UpdateView()
    {
        if (_log == null)
        {
            return;
        }

        _connect.Text = _link.Connected ? "Disconnect" : "Connect";
        _connection.Text = Restarting switch
        {
            RestartPhase.Saving => "restarting: saving...",
            RestartPhase.WaitingForServer => "restarting: waiting for the server...",
            _ => !_link.Connected ? "not connected" : Granted != null ? $"connected, admin ({Granted})" : "connected, map editing only (no admin channel)",
        };
        bool admin = _link.Connected && Granted != null;
        _save.Disabled = !admin || Saving || Restarting != RestartPhase.None;
        _refresh.Disabled = !admin;
        string blocker = RestartBlocker();
        _restart.Disabled = blocker != null;
        _restart.TooltipText = blocker ?? "Saves the world, then the run bar stops this server and starts it again.";

        JsonNode s = LastStatus;
        _values["Uptime"].Text = s == null ? "-" : Duration((long?)s["uptime_s"] ?? 0);
        int staffOnline = s == null ? 0 : (int?)s["staff_online"] ?? 0;
        _values["Online"].Text = s == null ? "-" : $"{(int?)s["online"] ?? 0}" + (staffOnline > 0 ? $" ({staffOnline} staff)" : "");
        _values["Items"].Text = s == null ? "-" : ((long?)s["items"] ?? 0).ToString("N0", CultureInfo.InvariantCulture);
        _values["Mobiles"].Text = s == null ? "-" : ((long?)s["mobiles"] ?? 0).ToString("N0", CultureInfo.InvariantCulture);
        _values["Memory"].Text = s == null ? "-" : Megabytes((long?)s["memory_mb"] ?? 0);
        _values["Last save"].Text = s == null ? "-" : SaveAge(s) is { } age ? Duration(age) + " ago" : "never";
        _values["World"].Text = s == null ? "-" : WorldState((string)s["world"]);
        _values["Server"].Text = s == null ? "-" : $"{(string)s["server"]} {(string)s["version"]}";
        _plain.Text = Describe();
        UpdateTitle();
    }

    private void UpdateTitle()
    {
        var parts = new List<string> { "Admin", Server == null ? "no server picked" : Server.Name + (IsLoopback(Server.Host) ? " (this computer)" : $" ({Server.Host})") };
        if (LastStatus is { } s && _link.Connected)
        {
            parts.Add("running " + Duration(((long?)s["uptime_s"] ?? 0) + (long)(DateTime.UtcNow - _statusAt).TotalSeconds));
            parts.Add($"{(int?)s["online"] ?? 0} online");
            parts.Add(SaveAge(s) is { } age ? $"saved {Duration(age)} ago" : "not saved yet");
        }
        else
        {
            parts.Add(Restarting != RestartPhase.None ? "restarting" : "not connected");
        }

        _title.Text = string.Join("  -  ", parts);
        if (LastStatus != null && _values.TryGetValue("Last save", out Label l))
        {
            l.Text = SaveAge(LastStatus) is { } a ? Duration(a) + " ago" : "never";
        }
    }

    private long? SaveAge(JsonNode s) => (long?)s["last_save_s"] is long ago ? ago + (long)(DateTime.UtcNow - _statusAt).TotalSeconds : null;

    /// <summary>The Health panel in plain words.</summary>
    internal string Describe()
    {
        var text = new StringBuilder();
        if (AdminProblem != null)
        {
            text.Append($"[color=orange]{AdminProblem}[/color]\n\n");
        }

        if (LastStatus is not { } s || !_link.Connected)
        {
            text.Append(Restarting == RestartPhase.WaitingForServer
                ? "The server is starting again. This tab reconnects by itself when it has loaded the world."
                : "Not connected to a server's editor bridge yet. Start the server in the run bar, then press Connect.");
            return text.ToString();
        }

        int online = (int?)s["online"] ?? 0, staff = (int?)s["staff_online"] ?? 0;
        text.Append($"{(string)s["server"]} {(string)s["version"]} has been running for {Duration((long?)s["uptime_s"] ?? 0, words: true)}.\n");
        text.Append(online == 0 ? "Nobody is online.\n"
            : $"{online} {(online == 1 ? "person is" : "people are")} online" + (staff > 0 ? $", {staff} of them staff.\n" : ".\n"));
        text.Append($"The world holds {((long?)s["items"] ?? 0).ToString("N0", CultureInfo.InvariantCulture)} items and "
            + $"{((long?)s["mobiles"] ?? 0).ToString("N0", CultureInfo.InvariantCulture)} mobiles (creatures and characters).\n");
        if (SaveAge(s) is { } age)
        {
            text.Append($"It was last saved {Duration(age, words: true)} ago.");
            text.Append(age > 30 * 60 ? " [color=orange]That is a while: Save now keeps the latest changes safe.[/color]\n" : "\n");
        }
        else
        {
            text.Append("[color=orange]It has never been saved: Save now writes the world to disk.[/color]\n");
        }

        text.Append($"It uses {Megabytes((long?)s["memory_mb"] ?? 0)} of memory.\n");
        if ((string)s["world"] is { } w && w != "Running")
        {
            text.Append($"Right now the world is {WorldState(w).ToLowerInvariant()}.\n");
        }

        text.Append('\n');
        text.Append(RestartBlocker() is { } why && Restarting == RestartPhase.None
            ? why
            : "Restart saves the world, stops the server and starts it again from the run bar; players are disconnected until it is back.");
        return text.ToString();
    }

    private static string WorldState(string state) => state switch
    {
        "Running" => "Running",
        "PendingSave" or "Saving" => "Saving",
        "WritingSave" => "Writing the save",
        "Loading" => "Loading",
        null => "-",
        _ => state,
    };

    internal static string Duration(long seconds, bool words = false)
    {
        seconds = Math.Max(0, seconds);
        long d = seconds / 86400, h = seconds % 86400 / 3600, m = seconds % 3600 / 60, s = seconds % 60;
        if (!words)
        {
            return d > 0 ? $"{d} d {h} h" : h > 0 ? $"{h} h {m} m" : m > 0 ? $"{m} m" : $"{s} s";
        }

        static string N(long n, string unit) => $"{n} {unit}{(n == 1 ? "" : "s")}";
        return d > 0 ? N(d, "day") + (h > 0 ? " " + N(h, "hour") : "")
            : h > 0 ? N(h, "hour") + (m > 0 ? " " + N(m, "minute") : "")
            : m > 0 ? N(m, "minute")
            : N(s, "second");
    }

    internal static string Megabytes(long mb) => mb >= 1024 ? (mb / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " GB" : $"{mb} MB";

    private static bool IsLoopback(string host) =>
        host == "localhost" || System.Net.IPAddress.TryParse(host, out var a) && System.Net.IPAddress.IsLoopback(a);

    // One clock in the whole tab: UTC with a Z, as the server's audit log writes it.
    private void Log(string line)
    {
        string stamp = GodViewPanel.Clock(DateTime.UtcNow);
        GD.Print($"[GUO editor] admin: {line}");
        _logText.Append(stamp).Append(' ').Append(line).Append('\n');
        _log?.AppendText($"[{stamp}] {line}\n");
    }

    /// <summary>Brings the Admin tab to the front of the main screen.</summary>
    public void MakeVisible()
    {
        EditorInterface.Singleton.SetMainScreenEditor(TabName);
        Visible = true;
    }

    public void Shutdown() => _link.Dispose();

    public override void _ExitTree() => Shutdown();
}
#endif
