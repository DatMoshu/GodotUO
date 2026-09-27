namespace GUO.Host;

using System;
using Godot;

/// <summary>
/// The boot splash: the GUO sigil on a dark field, then a crossfade into the
/// login screen. It is drawn by the engine, not played from a video, so it is
/// sharp at any resolution.
/// </summary>
/// <remarks>
/// The timeline, in seconds from the first frame:
/// <list type="bullet">
/// <item>0 to 0.7: the sigil fades in from nothing while it settles from
/// 96% to full size (ease-out, done at 1.3).</item>
/// <item>0.55 to 1.35: one soft band of light crosses the gold
/// (SplashIntro.gdshader).</item>
/// <item>1.75: the hold ends and the client boots underneath. Its first frames
/// load the login scene; the splash covers them.</item>
/// <item>Two frames after the boot returns: the whole splash fades out over
/// 0.5 s, revealing the login screen.</item>
/// </list>
/// About 2.3 s in all. With reduced motion (the OS setting, or
/// GUO_REDUCED_MOTION=1) it is a plain fade: no scale and no sweep, 1.7 s.
/// A key, click, tap or gamepad button skips to the boot and a 0.2 s fade;
/// that input is consumed and never reaches the client.
///
/// The field is the boot_splash colour in project.godot (#14100C), so the
/// engine's own splash hands over without a flash of another colour. On or
/// off is a device-wide choice (Options > Video, "Intro on start"), kept in
/// <c>user://guo_splash.cfg</c> rather than a profile, because it plays
/// before anyone has logged in. It is on by default. Scripted runs skip it
/// unless they pass <c>--splash</c>.
/// </remarks>
public partial class SplashIntro : CanvasLayer
{
    private const string ConfigPath = "user://guo_splash.cfg";
    private const string SigilPath = "res://assets/brand/splash_sigil.png";
    private const string ShaderPath = "res://src/Bootstrap/SplashIntro.gdshader";

    private static readonly Color Field = new(0x14 / 255f, 0x10 / 255f, 0x0C / 255f);

    /// <summary>The sigil's height as a fraction of the window's: the boot splash's own (tools/brand SPLASH_HEIGHT).</summary>
    private const float SigilHeight = 0.62f;

    private const float FadeIn = 0.7f;
    private const float Settle = 1.3f;
    private const float SweepFrom = 0.55f, SweepTo = 1.35f;
    private const float Hold = 1.75f;
    private const float FadeOut = 0.5f;
    private const float ReducedFadeIn = 0.5f, ReducedHold = 1.2f;
    private const float SkipFadeOut = 0.2f;

    private Action _boot;
    private bool _reduced;
    private Control _root;
    private TextureRect _sigil;
    private ShaderMaterial _material;
    private double _t;
    private bool _booted;
    private int _framesSinceBoot;
    private double _fadeStart = -1;
    private float _fadeLength = FadeOut;

    /// <summary>Raised once the splash has faded out and freed itself.</summary>
    public event Action Finished;

    /// <summary>Whether the splash plays on start. Device-wide; on unless the player turned it off.</summary>
    public static bool Enabled
    {
        get
        {
            var cfg = new ConfigFile();
            return cfg.Load(ConfigPath) != Error.Ok || (bool)cfg.GetValue("splash", "enabled", true);
        }
        set
        {
            var cfg = new ConfigFile();
            cfg.Load(ConfigPath);
            cfg.SetValue("splash", "enabled", value);
            cfg.Save(ConfigPath);
        }
    }

    /// <summary>The OS asks for less motion, or GUO_REDUCED_MOTION=1 does.</summary>
    public static bool ReducedMotion
    {
        get
        {
            string env = System.Environment.GetEnvironmentVariable("GUO_REDUCED_MOTION");
            if (!string.IsNullOrEmpty(env))
            {
                return env == "1";
            }

            try
            {
                return DisplayServer.AccessibilityShouldReduceAnimation() == 1;
            }
            catch (Exception)
            {
                return false;   // a display server without the query (headless)
            }
        }
    }

    /// <summary>
    /// Shows the splash over <paramref name="parent"/> and calls
    /// <paramref name="boot"/> when the hold ends (or at once on a skip). If
    /// the sigil cannot be loaded the splash is skipped and boot runs now.
    /// </summary>
    public static SplashIntro Play(Node parent, Action boot, bool reducedMotion)
    {
        var splash = new SplashIntro { _boot = boot, _reduced = reducedMotion, Layer = 120, Name = "SplashIntro" };

        if (!splash.Build())
        {
            GD.Print("[GUO] splash        : sigil missing, skipped");
            splash.Free();
            boot();
            return null;
        }

        parent.AddChild(splash);
        GD.Print($"[GUO] splash        : playing{(reducedMotion ? " (reduced motion)" : "")}");
        return splash;
    }

    private bool Build()
    {
        if (!ResourceLoader.Exists(SigilPath) || GD.Load<Texture2D>(SigilPath) is not Texture2D source)
        {
            return false;
        }

        // Mipmapped, so the 1254 px master shrinks cleanly to a phone's window.
        Image image = source.GetImage();
        if (image.IsCompressed())
        {
            image.Decompress();
        }

        image.GenerateMipmaps();
        var texture = ImageTexture.CreateFromImage(image);

        _root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_root);

        var field = new ColorRect { Color = Field, MouseFilter = Control.MouseFilterEnum.Ignore };
        field.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(field);

        _material = new ShaderMaterial { Shader = GD.Load<Shader>(ShaderPath) };
        _material.SetShaderParameter("sigil", texture);
        _material.SetShaderParameter("alpha", 0f);
        _material.SetShaderParameter("sweep", -1f);

        _sigil = new TextureRect
        {
            Texture = texture,
            Material = _material,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            // The painted sigil only: every other canvas stays nearest-neighbour.
            TextureFilter = CanvasItem.TextureFilterEnum.LinearWithMipmaps,
        };
        _root.AddChild(_sigil);
        return true;
    }

    public override void _Process(double delta)
    {
        // Engine time, not the wall clock: a hitch arrives as one long delta
        // either way, and a movie-maker render (--write-movie, fixed fps)
        // plays the timeline frame-exact however slowly it renders.
        _t += delta;
        double t = _t;
        Vector2 view = GetViewport().GetVisibleRect().Size;

        float alpha, scale, sweep;
        if (_reduced)
        {
            alpha = Smooth(0, ReducedFadeIn, t);
            scale = 1f;
            sweep = -1f;
        }
        else
        {
            alpha = Smooth(0, FadeIn, t);
            float u = Mathf.Clamp((float)(t / Settle), 0f, 1f);
            scale = 0.96f + 0.04f * (1f - Mathf.Pow(1f - u, 3f));
            sweep = t < SweepFrom || t > SweepTo ? -1f : Mathf.Lerp(-0.3f, 1.3f, (float)((t - SweepFrom) / (SweepTo - SweepFrom)));
        }

        // The crossfade: the field through the root's modulate, the sigil
        // through its own alpha, since its shader writes COLOR outright.
        float fade = 1f;
        if (_fadeStart >= 0)
        {
            float k = Mathf.Clamp((float)((t - _fadeStart) / _fadeLength), 0f, 1f);
            fade = 1f - k * k * (3f - 2f * k);
        }

        float side = view.Y * SigilHeight * scale;
        _sigil.Size = new Vector2(side, side);
        _sigil.Position = (view - _sigil.Size) / 2f;
        _material.SetShaderParameter("alpha", alpha * fade);
        _material.SetShaderParameter("sweep", sweep);

        if (!_booted && t >= (_reduced ? ReducedHold : Hold))
        {
            BootNow();
        }
        else if (_booted && _fadeStart < 0 && ++_framesSinceBoot > 2)
        {
            // Measured from here, not from the boot call: loading the login
            // scene can hold a frame for a while, and the fade must not be
            // spent during it.
            _fadeStart = t;
        }

        if (_fadeStart >= 0)
        {
            _root.Modulate = new Color(1, 1, 1, fade);
            if (fade <= 0f)
            {
                GD.Print("[GUO] splash        : done");
                Finished?.Invoke();
                QueueFree();
            }
        }
    }

    public override void _Input(InputEvent @event)
    {
        if (_fadeStart >= 0 || !IsSkip(@event))
        {
            return;
        }

        GetViewport().SetInputAsHandled();
        _fadeLength = SkipFadeOut;
        if (!_booted)
        {
            GD.Print("[GUO] splash        : skipped");
            BootNow();
        }
    }

    private static bool IsSkip(InputEvent e) => e switch
    {
        InputEventKey k => k.Pressed && !k.Echo,
        InputEventMouseButton m => m.Pressed,
        InputEventScreenTouch s => s.Pressed,
        InputEventJoypadButton j => j.Pressed,
        _ => false,
    };

    private void BootNow()
    {
        _booted = true;
        _boot?.Invoke();
        _boot = null;
    }

    private static float Smooth(double a, double b, double x)
    {
        float k = Mathf.Clamp((float)((x - a) / (b - a)), 0f, 1f);
        return k * k * (3f - 2f * k);
    }
}
