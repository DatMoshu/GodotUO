// GUO addition, not a port: upstream ClassicUO has no second screen (ADR-0009).

using System;
using Godot;
using GUO.Platform.Android;
using GUO.Renderer;

namespace GUO.Input.Touch.Pregame;

/// <summary>
/// The second screen while the shelf is not in use (before the world, or in
/// it with the shelf off): one stone card with two tabs, Servers and
/// Settings (docs/ui/second_screen_pregame.md). It replaces the welcome
/// panel (DualWelcomeGump, removed) that stood there before.
/// </summary>
/// <remarks>
/// Built like the companion tabs: Godot controls in the client's own look
/// (UoTheme: stone, parchment, font 1, the plate buttons) in a SubViewport at
/// a whole-number art scale, drawn into the second screen's bitmap by
/// <see cref="DualScreen.Draw"/>. The second screen's fingers come here first
/// (<see cref="HandleInput"/>). DualScreen says when it is up
/// (<see cref="OnSecond"/>), where it used to add the welcome gump.
/// </remarks>
internal sealed partial class PregameCard : Node
{
    public enum Tab { Servers, Settings }

    /// <summary>Set by DualScreen: the second screen is the card's (the shelf is not in use).</summary>
    public static bool OnSecond { get; set; }

    public static bool Shown => _instance != null && OnSecond && DualScreen.Active && !DualScreen.Suspended;

    private static PregameCard _instance;

    private SubViewport _viewport;
    private Control _root;
    private PanelContainer _card;
    private Button _tabServers, _tabSettings;
    private Control _servers;
    private PregameSettings _settings;
    private Tab _tab = Tab.Settings;
    private bool _built;
    private int _scale;
    private Vector2I _size;
    private double _refresh;

    private bool _dragging, _dragMoved, _sliding;
    private Vector2 _dragLast;

    public static void Setup(Node host)
    {
        if (_instance == null)
        {
            _instance = new PregameCard();
            host.AddChild(_instance);
        }
    }

    /// <summary>For the probe: the card as built, or null.</summary>
    public static PregameCard Instance => _instance != null && _instance._built ? _instance : null;

    public Tab Current => _tab;

    public PregameSettings Settings => _settings;

    /// <summary>
    /// Art pixels to the second screen's logical pixels: the cards' scale
    /// (UoTheme.PixelScale) carried through the shelf's own scale, so an art
    /// pixel is the same size on the panel whatever the shelf is set to.
    /// </summary>
    private static int ArtScale
    {
        get
        {
            int physical = Math.Max(1, DualScreen.SecondWidth);
            return Math.Max(1, (int) Math.Round(UoTheme.PixelScale * (float) DualScreen.LogicalWidth / physical));
        }
    }

    public override void _Ready()
    {
        _viewport = new SubViewport
        {
            TransparentBg = false,
            Disable3D = true,
            HandleInputLocally = true,
            GuiEmbedSubwindows = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            CanvasItemDefaultTextureFilter = Viewport.DefaultCanvasItemTextureFilter.Nearest,
            Size = new Vector2I(64, 64),
        };
        AddChild(_viewport);
        _root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        _viewport.AddChild(_root);

        // Built on first use (_Process), once the client's art is loaded.
    }

    private void Build()
    {
        _card = new PanelContainer { Theme = UoTheme.Theme };
        _root.AddChild(_card);

        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 5);
        _card.AddChild(col);

        // The tabs, then the client's version at the far end.
        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 4);
        col.AddChild(head);
        _tabServers = TabButton("Servers", Tab.Servers);
        _tabSettings = TabButton("Settings", Tab.Settings);
        head.AddChild(_tabServers);
        head.AddChild(_tabSettings);
        head.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore });
        head.AddChild(UoTheme.Label($"GUO {CUOEnviroment.Version}", UoTheme.Muted));

        _servers = BuildServers();
        col.AddChild(_servers);

        _settings = new PregameSettings { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        col.AddChild(_settings);

        Show(_tab);
    }

    private Button TabButton(string text, Tab tab)
    {
        Button b = UoTheme.Button(text, 72);
        b.Pressed += () => Show(tab);
        return b;
    }

    /// <summary>
    /// Servers, as far as step 1 goes: the server this client connects to.
    /// The list (favourites, recent, the community catalogue) and Play come
    /// with step 2.
    /// </summary>
    private static Control BuildServers()
    {
        var pane = new PanelContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        pane.AddThemeStyleboxOverride("panel", UoTheme.Frame(UoTheme.FieldFrame, 8));

        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 4);
        pane.AddChild(col);

        string name = Configuration.Settings.GlobalSettings.LastServerName;
        col.AddChild(UoTheme.Label("This client's server", UoTheme.Heading));
        col.AddChild(UoTheme.Label(string.IsNullOrWhiteSpace(name) ? "Not played on yet" : name, UoTheme.Ink));
        Label note = UoTheme.Label("Your saved servers, the recent ones and the community list will be here.", UoTheme.Muted);
        note.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        col.AddChild(note);

        return pane;
    }

    /// <summary>Opens a tab, as a tap on it does.</summary>
    public void Show(Tab tab)
    {
        _tab = tab;

        if (_card == null)
        {
            return;
        }

        _servers.Visible = tab == Tab.Servers;
        _settings.Visible = tab == Tab.Settings;

        // The open tab is the selected plate with a heading caption.
        foreach ((Button b, bool on) in new[] { (_tabServers, tab == Tab.Servers), (_tabSettings, tab == Tab.Settings) })
        {
            b.AddThemeStyleboxOverride("normal", UoTheme.Plate(on ? UoTheme.SelectedShade : 1f));
            b.AddThemeColorOverride("font_color", on ? UoTheme.Heading : UoTheme.Ink);
            b.AddThemeColorOverride("font_hover_color", on ? UoTheme.Heading : UoTheme.Ink);
        }

        GD.Print($"[GUO] pregame card: {tab.ToString().ToLowerInvariant()}");
    }

    public override void _Process(double delta)
    {
        if (!Shown)
        {
            return;
        }

        if (!_built)
        {
            if (!UoTheme.Ready)
            {
                return;
            }

            Build();
            _built = true;
            GD.Print($"[GUO] pregame card: built for the second screen {DualScreen.LogicalWidth}x{DualScreen.LogicalHeight}, art x{ArtScale}");
        }

        var size = new Vector2I(DualScreen.LogicalWidth, DualScreen.LogicalHeight);
        int art = ArtScale;

        if (size != _size || art != _scale)
        {
            _size = size;
            _scale = art;
            _viewport.Size = size;
            _root.Scale = new Vector2(art, art);
            _card.Position = Vector2.Zero;
            _card.Size = new Vector2(size.X / art, size.Y / art);
            _settings.SetNarrow(size.X / art < PregameSettings.NarrowBelow);
        }

        _refresh -= delta;

        if (_refresh <= 0)
        {
            // Values changed elsewhere (the login gump's boxes, Options) come back in.
            _refresh = 0.5;
            _settings.Refresh();
        }
    }

    // --- drawing and input ---------------------------------------------------------

    /// <summary>The card, drawn into the second screen's bitmap in client pixels.</summary>
    public static void DrawSecond(UltimaBatcher2D b)
    {
        if (!Shown || !_instance._built)
        {
            return;
        }

        Vector2I px = _instance._viewport.Size;
        b.Draw(_instance._viewport.GetTexture(), new Compat.Rectangle(DualScreen.MainWidth, 0, px.X, px.Y), ShaderHueTranslator.GetHueVector(0), 0);
    }

    /// <summary>
    /// The second screen's pointer events while the card is up: a tap is a
    /// click on the control under it, a drag scrolls the open list, and a
    /// finger that comes down on a slider drags its knob. True when consumed.
    /// </summary>
    public static bool HandleInput(InputEvent e)
    {
        if (!Shown || !_instance._built)
        {
            return false;
        }

        PregameCard m = _instance;

        Vector2? window = e switch
        {
            InputEventScreenTouch t => t.Position,
            InputEventScreenDrag d => d.Position,
            _ => null,
        };

        if (window == null)
        {
            return false;
        }

        Vector2 client = window.Value / Client.Game.DpiScale;

        if (client.X < DualScreen.MainWidth)
        {
            return false;
        }

        Vector2 local = client - new Vector2(DualScreen.MainWidth, 0);
        TouchOverlay.Note(e);
        m.Pointer(e, local);

        return true;
    }

    /// <summary>A finger at <paramref name="local"/> (the card's logical pixels); also the probe's tap.</summary>
    public void Pointer(InputEvent e, Vector2 local)
    {
        switch (e)
        {
            case InputEventScreenTouch { Pressed: true }:
                _dragging = true;
                _dragMoved = false;
                _dragLast = local;
                _viewport.PushInput(new InputEventMouseMotion { Position = local, GlobalPosition = local }, true);
                _sliding = _settings.Visible && _settings.SliderAt(local);

                if (_sliding)
                {
                    _viewport.PushInput(Click(local, true), true);
                }

                break;

            case InputEventScreenDrag:
                if (_sliding)
                {
                    _viewport.PushInput(new InputEventMouseMotion { Position = local, GlobalPosition = local, ButtonMask = MouseButtonMask.Left }, true);
                }
                else if (_dragging)
                {
                    float dy = local.Y - _dragLast.Y;
                    _dragMoved |= Math.Abs(dy) > 0.5f;

                    if (_settings.Visible)
                    {
                        _settings.ScrollBy(-dy / _scale);
                    }
                }

                _dragLast = local;
                break;

            case InputEventScreenTouch { Pressed: false }:
                if (_sliding)
                {
                    _viewport.PushInput(Click(local, false), true);
                }
                else if (_dragging && !_dragMoved)
                {
                    _viewport.PushInput(Click(local, true), true);
                    _viewport.PushInput(Click(local, false), true);
                }

                _dragging = false;
                _sliding = false;
                break;
        }
    }

    private static InputEventMouseButton Click(Vector2 at, bool pressed) => new()
    {
        Position = at,
        GlobalPosition = at,
        ButtonIndex = MouseButton.Left,
        Pressed = pressed,
        ButtonMask = pressed ? MouseButtonMask.Left : 0,
    };

    /// <summary>For the probe: a tap on a control, as a finger on the panel would make it.</summary>
    public void Tap(Control c)
    {
        Vector2 at = c.GetGlobalRect().GetCenter();
        Pointer(new InputEventScreenTouch { Pressed = true, Position = at }, at);
        Pointer(new InputEventScreenTouch { Pressed = false, Position = at }, at);
    }

    /// <summary>For the probe: the tab buttons.</summary>
    public Button TabButtonFor(Tab tab) => tab == Tab.Servers ? _tabServers : _tabSettings;

    /// <summary>For the probe: the card's art scale and logical size.</summary>
    public string Geometry => $"{_size.X}x{_size.Y} at x{_scale}";
}
