namespace GUO.Host;

using System.Collections.Generic;
using Godot;

/// <summary>
/// Proof for the boot splash (SplashIntro): plays it over a stand-in for the
/// login screen and saves frames at fixed times, then quits. It needs no UO
/// install and no shard.
/// </summary>
/// <remarks>
/// <code>
/// godot-console --path godot/GUO res://src/Bootstrap/SplashProof.tscn -- --out &lt;dir&gt; [--reduced] [--skip-at 0.9]
/// godot-console --path godot/GUO --fixed-fps 60 res://src/Bootstrap/SplashProof.tscn -- --out &lt;dir&gt; --size 1920x1080 --plain --all
/// </code>
/// The window has to render: run it without --headless. It stays
/// unfocusable, as project.godot creates it. Frames are PNGs named by their
/// time in milliseconds. --skip-at sends a synthetic key press at that time
/// to show the skip path. --size sets the window (default 1280x720),
/// --plain makes the stand-in login plain black, and --all saves every frame
/// (f_00000.png, ...) instead: with --fixed-fps the timeline then advances
/// exactly one frame per frame however slowly the PNGs save, which is a clean
/// render for a video (the second line). Time here and in SplashIntro is
/// engine time. Nothing is written outside --out.
/// </remarks>
public partial class SplashProof : Node
{
    private static readonly double[] Times =
    {
        0.0, 0.15, 0.3, 0.45, 0.6, 0.75, 0.9, 1.05, 1.2, 1.35, 1.5, 1.7, 1.85, 2.0, 2.15, 2.3, 2.5,
    };

    private string _out = "user://splash_proof";
    private double _skipAt = -1;
    private double _clock;
    private bool _all;
    private int _frame;
    private int _next;
    private bool _skipped, _done;
    private readonly List<string> _saved = new();

    public override void _Ready()
    {
        string[] args = OS.GetCmdlineUserArgs();
        bool reduced = false, plain = false;
        var size = new Vector2I(1280, 720);
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out" when i + 1 < args.Length:
                    _out = args[++i];
                    break;
                case "--reduced":
                    reduced = true;
                    break;
                case "--all":
                    _all = true;
                    break;
                case "--plain":
                    plain = true;
                    break;
                case "--size" when i + 1 < args.Length:
                    string[] wh = args[++i].Split('x');
                    size = new Vector2I(int.Parse(wh[0]), int.Parse(wh[1]));
                    break;
                case "--skip-at" when i + 1 < args.Length:
                    _skipAt = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
                    break;
            }
        }

        DirAccess.MakeDirRecursiveAbsolute(_out);
        DisplayServer.WindowSetSize(size);

        // A stand-in for the login screen, so the crossfade has something to reveal.
        var login = new ColorRect { Color = plain ? Colors.Black : new Color(0.10f, 0.13f, 0.20f) };
        login.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(login);

        SplashIntro splash = SplashIntro.Play(this, () =>
        {
            if (plain)
            {
                return;
            }

            var label = new Label { Text = "login screen (stand-in)", Position = new Vector2(40, 40) };
            label.AddThemeFontSizeOverride("font_size", 28);
            login.AddChild(label);
        }, reduced);

        if (splash == null)
        {
            GD.PrintErr("[splash-proof] the splash did not start (sigil missing?)");
            GetTree().Quit(1);
            return;
        }

        splash.Finished += () => GetTree().CreateTimer(0.4).Timeout += Finish;
        RenderingServer.FramePostDraw += Capture;
    }

    public override void _Process(double delta)
    {
        _clock += delta;
        double t = _clock;
        if (!_skipped && _skipAt >= 0 && t >= _skipAt)
        {
            _skipped = true;
            Input.ParseInputEvent(new InputEventKey { Keycode = Key.Space, Pressed = true });
            GD.Print($"[splash-proof] key press at {t:0.00} s");
        }
    }

    private void Capture()
    {
        double t = _clock;
        if (_all && !_done)
        {
            GetViewport().GetTexture().GetImage().SavePng(_out.PathJoin($"f_{_frame++:D5}.png"));
            return;
        }

        if (_done || _next >= Times.Length || t < Times[_next])
        {
            return;
        }

        while (_next < Times.Length && t >= Times[_next])
        {
            _next++;
        }

        Image image = GetViewport().GetTexture().GetImage();
        string path = _out.PathJoin($"splash_{(int)(t * 1000):D4}ms.png");
        image.SavePng(path);
        _saved.Add(path);
    }

    private void Finish()
    {
        _done = true;
        RenderingServer.FramePostDraw -= Capture;
        GD.Print($"[splash-proof] {(_all ? _frame : _saved.Count)} frames in {_out}");
        GetTree().Quit(0);
    }
}
