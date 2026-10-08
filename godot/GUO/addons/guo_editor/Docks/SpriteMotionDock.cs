#if TOOLS
namespace GUO.Editor;

using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Godot;

/// <summary>Native GUO host for the shared Fit Lab. The local service outlives this dock.</summary>
[Tool]
public partial class SpriteMotionDock : EditorDock
{
    private VBoxContainer _layout;
    private Label _status;
    private Button _open;
    private Control _browser;
    private readonly CancellationTokenSource _stop = new();
    private bool _alive = true;
    private string _url;

    public SpriteMotionDock()
    {
        Name = "SpriteMotion";
        Title = "Fit Lab";
        LayoutKey = "guo_spritemotion";
        DefaultSlot = DockSlot.Bottom;
        AvailableLayouts = DockLayout.Horizontal | DockLayout.Floating;
        IconName = "Animation";
    }

    public override void _Ready()
    {
        _layout = new VBoxContainer();
        _layout.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(_layout);
        var row = new HBoxContainer();
        _layout.AddChild(row);
        _open = new Button { Text = "Open Fit Lab" };
        _open.Pressed += OpenLab;
        row.AddChild(_open);
        var external = new Button { Text = "Open in browser", TooltipText = "The same lab and saved fits" };
        external.Pressed += () => { if (_url != null) OS.ShellOpen(_url); };
        row.AddChild(external);
        _status = new Label { Text = "First use prepares the editor tools automatically. Your assets stay local.", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _layout.AddChild(_status);
        if (System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--spritemotion-smoke") >= 0)
            Callable.From(OpenLab).CallDeferred();
    }

    public void OpenLab()
    {
        if (_open.Disabled || _browser != null) return;
        _open.Disabled = true;
        _status.Text = "Preparing Fit Lab…";
        Task.Run(() =>
        {
            try
            {
                var info = new ProcessStartInfo(EditorData.Setting("UO_PYTHON", "python"))
                {
                    WorkingDirectory = EditorData.RepoRoot,
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                };
                info.ArgumentList.Add(Path.Combine(EditorData.RepoRoot, "tools", "spritemotion", "run.py"));
                info.ArgumentList.Add("open");
                using var process = Process.Start(info);
                using var cancel = _stop.Token.Register(() => { try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { } });
                var errors = process.StandardError.ReadToEndAsync();
                string result = null;
                while (process.StandardOutput.ReadLine() is string line)
                {
                    if (line.StartsWith("{")) result = line;
                    else Post(() => _status.Text = line);
                }
                process.WaitForExit();
                if (process.ExitCode != 0 || result == null)
                    throw new InvalidOperationException("Fit Lab setup failed. " + errors.GetAwaiter().GetResult() + " See the setup message above.");
                var response = JsonNode.Parse(result);
                string url = response["url"].GetValue<string>();
                Post(() => Embed(url));
            }
            catch (Exception error)
            {
                Post(() => { _status.Text += "\n" + error.Message; _open.Disabled = false; });
            }
        });
    }

    private void Post(Action action)
    {
        if (!_alive) return;
        Callable.From(() => { if (_alive && IsInstanceValid(this)) action(); }).CallDeferred();
    }

    private void Embed(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme != "http" || parsed.Host != "127.0.0.1")
        {
            _status.Text = "Refused a non-local Fit Lab address.";
            _open.Disabled = false;
            return;
        }
        _url = url;
        if (!ClassDB.ClassExists("CefTexture"))
            GDExtensionManager.LoadExtension("res://addons/godot_cef/godot_cef.gdextension");
        if (!ClassDB.ClassExists("CefTexture"))
        {
            _status.Text = "Browser runtime installed. Restart the GUO editor once to load it, then open Fit Lab.";
            _open.Disabled = false;
            return;
        }
        _browser = ClassDB.Instantiate("CefTexture").AsGodotObject() as Control;
        if (_browser == null) { _status.Text = "The browser extension could not create its view."; _open.Disabled = false; return; }
        _browser.Set("url", url);
        _browser.Set("enable_accelerated_osr", true);
        _browser.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _browser.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        _browser.CustomMinimumSize = new Vector2(900, 580);
        _browser.FocusMode = Control.FocusModeEnum.All;
        _browser.Connect("load_finished", Callable.From<string, int>((loaded, code) =>
        {
            GD.Print($"[GUO SpriteMotion] page loaded {code}: {loaded}");
            if (System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--spritemotion-smoke") >= 0)
                GetTree().CreateTimer(12).Timeout += CaptureSmoke;
        }));
        _layout.AddChild(_browser);
        MakeVisible();
        _status.Text = "Shared Fit Lab · saves and builds are shared with the standalone browser. Float this dock for more room.";
        GD.Print("[GUO SpriteMotion] embedded " + url);
    }

    private void CaptureSmoke()
    {
        if (!_alive) return;
        string output = Path.Combine(EditorData.RepoRoot, "build", "spritemotion", "editor.png");
        GetViewport().GetTexture().GetImage().SavePng(output);
        GD.Print("[GUO SpriteMotion] captured " + output);
        GetTree().Quit();
    }

    public void Shutdown()
    {
        _alive = false;
        _stop.Cancel();
        // Never terminate the shared Python service or its Blender worker on a C# reload.
        if (_browser != null) { _browser.GetParent()?.RemoveChild(_browser); _browser.QueueFree(); _browser = null; }
    }

    public override void _ExitTree() => Shutdown();
}
#endif
