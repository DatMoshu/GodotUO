#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;

/// <summary>
/// The editor toolbar's run bar: start a shard and start one to four clients, the same
/// launchers a person runs (launchers\shard\run.bat, tools\editor_shard, launchers\game\play.bat).
/// Everything it starts runs detached in its own window and outlives the editor. The dot says
/// whether the chosen shard answers; Start server is off while it does.
/// </summary>
[Tool]
public partial class RunBar : HBoxContainer
{
    private const int DevShard = 0, PrivateShard = 1;

    private OptionButton _server, _count;
    private Button _startServer, _startClients;
    private Label _dot;
    private Timer _poll;
    private bool _checking, _up;

    public override void _Ready()
    {
        _server = new OptionButton { TooltipText = "The dev shard (launchers\\shard\\run.bat, shared with other agents and devices),\nor this checkout's private ModernUO (tools\\editor_shard)." };
        _server.AddItem("Dev shard", DevShard);
        _server.AddItem("Private shard", PrivateShard);
        _server.ItemSelected += _ => { _up = false; Refresh(); Poll(); };
        AddChild(_server);

        _dot = new Label { Text = "●", TooltipText = "Whether the shard answers" };
        AddChild(_dot);

        _startServer = new Button { Text = "Start server" };
        _startServer.Pressed += StartServer;
        AddChild(_startServer);

        AddChild(new VSeparator());

        _startClients = new Button { Text = "Start client", TooltipText = "launchers\\game\\play.bat, logged out at the login screen.\nTwo or more are tiled across the screen. One account per client: a second login kicks the first." };
        _startClients.Pressed += StartClients;
        AddChild(_startClients);

        _count = new OptionButton { TooltipText = "How many clients" };
        for (int i = 1; i <= 4; i++)
        {
            _count.AddItem($"× {i}", i);
        }
        _count.ItemSelected += i => _startClients.Text = i == 0 ? "Start client" : $"Start {i + 1} clients";
        AddChild(_count);

        _poll = new Timer { WaitTime = 2.0, Autostart = true };
        _poll.Timeout += Poll;
        AddChild(_poll);
        Refresh();
        Poll();
    }

    private (string Host, int Port) Target()
    {
        if (_server.Selected == PrivateShard)
        {
            string state = Path.Combine(EditorData.Setting("UO_BUILD", Path.Combine(EditorData.RepoRoot, "build")), "shard_private", "state.json");
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(state));
                return ("127.0.0.1", doc.RootElement.GetProperty("port").GetInt32());
            }
            catch (Exception)
            {
                return ("127.0.0.1", 0);   // not set up yet
            }
        }

        return (EditorData.Setting("UO_SHARD_HOST", "127.0.0.1"),
            int.TryParse(EditorData.Setting("UO_SHARD_PORT", "2593"), out int p) ? p : 2593);
    }

    private void Poll()
    {
        if (_checking)
        {
            return;
        }

        _checking = true;
        var (host, port) = Target();
        Task.Run(async () =>
        {
            bool up = false;
            if (port > 0)
            {
                try
                {
                    using var tcp = new TcpClient();
                    up = await tcp.ConnectAsync(host, port).WaitAsync(TimeSpan.FromMilliseconds(400)).ContinueWith(t => t.IsCompletedSuccessfully);
                }
                catch (Exception)
                {
                    up = false;
                }
            }

            Callable.From(() => { _checking = false; _up = up; Refresh(); }).CallDeferred();
        });
    }

    private void Refresh()
    {
        if (!IsInstanceValid(_dot))
        {
            return;
        }

        var (host, port) = Target();
        _dot.Modulate = _up ? new Color(0.35f, 0.85f, 0.4f) : new Color(0.55f, 0.55f, 0.55f);
        _dot.TooltipText = port == 0 ? "The private shard is not set up: python tools\\editor_shard\\run.py setup --port N"
            : _up ? $"Answering on {host}:{port}" : $"Nothing on {host}:{port}";
        _startServer.Disabled = _up || port == 0;
        _startServer.Text = _up ? "Server running" : "Start server";
    }

    /// <summary>What the Start server button does; F3 runs it on Enter (it opens a window).</summary>
    public void StartServerNow()
    {
        if (!_startServer.Disabled)
        {
            StartServer();
        }
    }

    /// <summary>What the Start client button does, with the count chosen in the bar.</summary>
    public void StartClientsNow() => StartClients();

    private void StartServer()
    {
        if (_server.Selected == PrivateShard)
        {
            Launch(EditorData.Setting("UO_PYTHON", "python"), new[] { Path.Combine(EditorData.RepoRoot, "tools", "editor_shard", "run.py"), "start" }, "GUO private shard", null);
        }
        else
        {
            Launch(Script("shard", "run"), Array.Empty<string>(), "GUO dev shard", null);
        }

        _startServer.Disabled = true;
        _startServer.Text = "Starting…";
    }

    private void StartClients()
    {
        int n = _count.GetSelectedId();
        var (host, port) = Target();
        var env = new Dictionary<string, string>();

        // The private shard is not in config.bat: these reach play.bat for this start only.
        if (_server.Selected == PrivateShard && port > 0)
        {
            env["UO_SHARD_HOST"] = host;
            env["UO_SHARD_PORT"] = port.ToString();
        }

        Rect2I screen = DisplayServer.ScreenGetUsableRect(DisplayServer.WindowGetCurrentScreen());
        int cols = n == 1 ? 1 : 2, rows = (n + 1) / 2;
        int w = screen.Size.X / cols, h = screen.Size.Y / rows;

        for (int i = 0; i < n; i++)
        {
            var args = new List<string>();
            if (n > 1)
            {
                // Title bars take some room: a little under the cell, so neighbours don't overlap.
                args.AddRange(new[] { "--window-size", $"{w - 16},{h - 48}", "--window-position", $"{screen.Position.X + i % cols * w},{screen.Position.Y + i / cols * h + 32}" });
            }

            Launch(Script("game", "play"), args, $"GUO client {i + 1}", env);
        }
    }

    private static string Script(string folder, string name) =>
        Path.Combine(EditorData.RepoRoot, "launchers", folder, name + (OperatingSystem.IsWindows() ? ".bat" : ".sh"));

    /// <summary>Starts a program in a window of its own and lets it go: nothing here waits on it or stops it.</summary>
    private static void Launch(string program, IEnumerable<string> args, string title, Dictionary<string, string> env)
    {
        var psi = new ProcessStartInfo { UseShellExecute = false, WorkingDirectory = EditorData.RepoRoot };
        if (OperatingSystem.IsWindows())
        {
            psi.FileName = "cmd.exe";
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add("start");
            psi.ArgumentList.Add(title);
            psi.ArgumentList.Add(program);
        }
        else
        {
            psi.FileName = program.EndsWith(".sh", StringComparison.Ordinal) ? "bash" : program;
            if (psi.FileName == "bash")
            {
                psi.ArgumentList.Add(program);
            }
        }

        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        foreach (var (k, v) in env ?? new Dictionary<string, string>())
        {
            psi.Environment[k] = v;
        }

        try
        {
            Process.Start(psi)?.Dispose();
            GD.Print($"[GUO editor] started {title}");
        }
        catch (Exception ex)
        {
            GD.PushError($"[GUO editor] couldn't start {title}: {ex.Message}");
        }
    }
}
#endif
