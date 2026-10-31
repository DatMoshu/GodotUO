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
/// <item>0 to 0.8: the stationary sigil fades in.</item>
/// <item>1.0 to 2.2: one soft band of light crosses the gold
/// (SplashIntro.gdshader).</item>
/// <item>1.1 to 2.4: GODOT settles below, one letter every 0.15 seconds.</item>
/// <item>5.15: the hold ends and the client boots underneath. Its first frames
/// load the login scene; the splash covers them.</item>
/// <item>Two frames after the boot returns: the whole splash fades out over
/// 0.82 s, revealing the login screen.</item>
/// </list>
/// About 6 s plus login loading. With reduced motion (the OS setting, or
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
    private const string LettersPath = "res://assets/brand/godot_letters.png";

    private static readonly Color Field = new(0x14 / 255f, 0x10 / 255f, 0x0C / 255f);

    private const float FadeIn = 0.8f;
    private const float SweepFrom = 1f, SweepTo = 2.2f;
    private const float Hold = 5.15f;
    private const float FadeOut = 0.82f;
    private const float ReducedFadeIn = 0.5f, ReducedHold = 1.2f;
    private const float SkipFadeOut = 0.2f;

    private Action _boot;
    private bool _reduced;
    private Control _root;
    private TextureRect _sigil;
    private readonly TextureRect[] _letters = new TextureRect[5];
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

        // Crop transparent padding and preserve the master aspect ratio.
        Image image = source.GetImage();
        if (image.IsCompressed())
        {
            image.Decompress();
        }

        image = image.GetRegion(image.GetUsedRect());
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
        if (GD.Load<Texture2D>(LettersPath) is Texture2D letters)
        {
            for (int i = 0; i < _letters.Length; i++)
            {
                _letters[i] = new TextureRect
                {
                    Texture = new AtlasTexture { Atlas = letters, Region = new Rect2(i * 82, 0, 82, 100) },
                    ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                    StretchMode = TextureRect.StretchModeEnum.Scale,
                    TextureFilter = CanvasItem.TextureFilterEnum.Linear,
                    MouseFilter = Control.MouseFilterEnum.Ignore,
                    Modulate = new Color(1, 1, 1, 0),
                };
                _root.AddChild(_letters[i]);
            }
        }
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

        float alpha, sweep;
        if (_reduced)
        {
            alpha = Smooth(0, ReducedFadeIn, t);
            sweep = -1f;
        }
        else
        {
            alpha = EaseOut((float)(t / FadeIn));
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

        // Owner-selected letter-settle composition, designed at 1920 x 1080.
        float unit = Mathf.Min(view.X / 1920f, view.Y / 1080f);
        Vector2 origin = (view - new Vector2(1920, 1080) * unit) / 2f;
        float height = 490f * unit;
        _sigil.Size = new Vector2(height * _sigil.Texture.GetWidth() / _sigil.Texture.GetHeight(), height);
        _sigil.Position = origin + new Vector2((1920f * unit - _sigil.Size.X) / 2f, 235f * unit);
        for (int i = 0; i < _letters.Length; i++)
        {
            if (_letters[i] == null) continue;
            float settle = _reduced ? 1f : EaseOut((float)((t - 1.1 - i * 0.15) / 0.7));
            _letters[i].Size = new Vector2(82, 100) * unit;
            _letters[i].Position = origin + new Vector2(755 + i * 82, 755 + 42 * (1 - settle)) * unit;
            // Root modulation applies the final crossfade once.
            _letters[i].Modulate = new Color(1, 1, 1, _reduced ? alpha : settle);
        }
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

    private static float EaseOut(float value)
    {
        float u = Mathf.Clamp(value, 0f, 1f);
        return 1f - Mathf.Pow(1f - u, 3f);
    }
}
