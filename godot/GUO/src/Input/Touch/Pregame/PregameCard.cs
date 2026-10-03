// GUO addition, not a port: upstream ClassicUO has no second screen (ADR-0009)
// and no server list.

using System;
using System.Linq;
using Godot;
using GUO.Game.Managers;
using GUO.Game.Scenes;
using GUO.Game.UI.Gumps.Login;
using GUO.Platform.Android;
using GUO.Renderer;

namespace GUO.Input.Touch.Pregame;

/// <summary>
/// The pre-game card: one stone card with two tabs, Servers and Settings
/// (docs/ui/second_screen_pregame.md). On a device with a second screen it
/// is that screen whenever the shelf is not in use (before the world, or in
/// it with the shelf off), in place of the old welcome panel. On one screen
/// (a phone, the Odin, the desktop) it opens over the login screen from a
/// "Servers" button beside the login gump, and closes back to it.
/// </summary>
/// <remarks>
/// Built like the companion tabs: Godot controls in the client's own look
/// (UoTheme: stone, parchment, font 1, the plate buttons) in a SubViewport at
/// a whole-number art scale. On the second screen it is drawn into that
/// screen's bitmap by <see cref="DualScreen.Draw"/> and takes that screen's
/// fingers (<see cref="HandleInput"/>); on the main screen it is a texture on
/// a CanvasLayer and takes the window's pointer and keys from GameController
/// (<see cref="HandleMainInput"/>). DualScreen says when the second screen
/// is the card's (<see cref="OnSecond"/>).
/// </remarks>
internal sealed partial class PregameCard : Node
{
    public enum Tab { Servers, Settings }

    /// <summary>Set by DualScreen: the second screen is the card's (the shelf is not in use).</summary>
    public static bool OnSecond { get; set; }

    /// <summary>Open over the main screen (one-screen devices), from the Servers button.</summary>
    public static bool OnMain => _instance != null && _instance._onMain;

    public static bool ShownOnSecond => _instance != null && OnSecond && DualScreen.Active && !DualScreen.Suspended && !_instance._onMain;

    public static bool Shown => ShownOnSecond || OnMain;

    /// <summary>The largest card on the main screen, in art pixels: a card, not a wall of stone.</summary>
    private static readonly Vector2I MainMax = new(480, 400);

    private static PregameCard _instance;

    private SubViewport _viewport;
    private Control _root;
    private PanelContainer _card;
    private Button _tabServers, _tabSettings, _close;
    private PregameServers _servers;
    private PregameSettings _settings;
    private Tab _tab = Tab.Servers;
    private bool _built;
    private int _scale;
    private Vector2I _size;
    private double _refresh;

    private bool _onMain;
    private CanvasLayer _layer;
    private ColorRect _band;
    private TextureRect _view;
    private Control _serversButtonHost;
    private Button _serversButton;

    private bool _dragging, _dragMoved, _sliding;
    private Vector2 _dragLast, _dragStart;

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

    public PregameServers Servers => _servers;

    /// <summary>For the probe: the control that has the card's focus (the pad's cursor), or null.</summary>
    public Control PadFocusOwner => _viewport?.GuiGetFocusOwner();

    /// <summary>
    /// Art pixels to the card's viewport pixels. On the second screen: the
    /// cards' scale (UoTheme.PixelScale) carried through the shelf's own
    /// scale, so an art pixel is the same size on the panel whatever the
    /// shelf is set to. On the main screen: the cards' scale.
    /// </summary>
    private int ArtScale
    {
        get
        {
            if (_onMain)
            {
                // The cards' scale, one step less at a time while the window
                // would leave the card narrower than its two panes need (the
                // desktop's login window is 640x480).
                int art = Math.Max(1, UoTheme.PixelScale);
                int window = GetTree().Root.Size.X;

                while (art > 1 && window / art < PregameSettings.NarrowBelow)
                {
                    art--;
                }

                return art;
            }

            // The one-screen panel is drawn at the client's own whole-number
            // scale, so an art pixel is a whole number of client pixels:
            // rounded down, the card as big as the login gump's art beside it.
            if (DualScreen.IsPanel)
            {
                return Math.Max(1, (int) (UoTheme.PixelScale / Client.Game.DpiScale));
            }

            int physical = Math.Max(1, DualScreen.SecondWidth);
            return Math.Max(1, (int) Math.Round(UoTheme.PixelScale * (float) DualScreen.LogicalWidth / physical));
        }
    }

    public override void _Ready()
    {
        _viewport = new SubViewport
        {
            TransparentBg = true,
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

        // The main-screen host: a dark band, the card's texture over it, and
        // the Servers button beside the login gump. Above the client, below
        // the Store and the effects menu.
        _layer = new CanvasLayer { Layer = 90 };
        AddChild(_layer);
        _band = new ColorRect { Color = new Color(0, 0, 0, 0.55f), MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        _layer.AddChild(_band);
        _view = new TextureRect { TextureFilter = CanvasItem.TextureFilterEnum.Nearest, MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        _layer.AddChild(_view);
        _serversButtonHost = new Control { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        _layer.AddChild(_serversButtonHost);
        _devHost = new Control { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        _layer.AddChild(_devHost);

        // Built on first use (_Process), once the client's art is loaded.
    }

    private void Build()
    {
        _card = new PanelContainer { Theme = UoTheme.Theme };
        _root.AddChild(_card);

        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 5);
        _card.AddChild(col);

        // The tabs. No version beside them: GUO has no release number yet
        // (the assembly's is the 1.0.0.0 default).
        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 4);
        col.AddChild(head);
        _tabServers = TabButton("Servers", Tab.Servers);
        _tabSettings = TabButton("Settings", Tab.Settings);
        head.AddChild(_tabServers);
        head.AddChild(_tabSettings);
        head.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore });

        // On the main screen, the way back to the login gump.
        _close = UoTheme.Button("Close", 48);
        _close.Pressed += CloseOnMain;
        head.AddChild(_close);

        _servers = new PregameServers { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        col.AddChild(_servers);

        _settings = new PregameSettings { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        col.AddChild(_settings);

        // The Servers button beside the login gump (one screen).
        _serversButtonHost.Theme = UoTheme.Theme;
        _serversButton = UoTheme.Button("Servers", 56);
        _serversButton.MouseFilter = Control.MouseFilterEnum.Ignore;
        _serversButtonHost.AddChild(_serversButton);

        // A dev build's one-click dev logins, under it (DevLogin).
        _devHost.Theme = UoTheme.Theme;
        _devPlate = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _devPlate.AddThemeStyleboxOverride("panel", UoTheme.Frame(UoTheme.FieldFrame, 3));
        _devHost.AddChild(_devPlate);
        _devRow = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _devRow.AddThemeConstantOverride("separation", 3);
        _devPlate.AddChild(_devRow);

        Show(_tab);
    }

    private Button TabButton(string text, Tab tab)
    {
        Button b = UoTheme.Button(text, 72);
        b.Pressed += () => Show(tab);
        return b;
    }

    /// <summary>Opens a tab, as a tap on it does.</summary>
    public void Show(Tab tab)
    {
        _tab = tab;

        if (_card == null)
        {
            return;
        }

        if (tab == Tab.Servers)
        {
            _servers.Rebuild();
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

    // --- one screen: the Servers button and the card over the login screen ----------

    /// <summary>Whether the Servers button stands beside the login gump now.</summary>
    public static bool ServersButtonShown => _instance != null && _instance._serversButtonHost.Visible;

    /// <summary>Opens the card over the main screen (the Servers button; the probe).</summary>
    public static void OpenOnMain(Tab tab = Tab.Servers)
    {
        if (_instance == null || !_instance._built)
        {
            return;
        }

        _instance._onMain = true;
        _instance._size = Vector2I.Zero; // lay out again for the main screen
        _instance.Show(tab);
        GD.Print("[GUO] pregame card: open on the main screen");
    }

    public static void CloseOnMain()
    {
        if (_instance == null || !_instance._onMain)
        {
            return;
        }

        _instance._onMain = false;
        _instance._size = Vector2I.Zero;
        _instance._view.Visible = false;
        _instance._band.Visible = false;
        _instance._viewport.GuiReleaseFocus();
        HideKeyboard();
        GD.Print("[GUO] pregame card: closed on the main screen");
    }

    /// <summary>The Servers button: at the login screen, when no second screen holds the card.</summary>
    private static bool WantsServersButton =>
        Client.Game?.Scene is LoginScene && !ShownOnSecond && !OnMain && UIManager.GetGump<LoginGump>() is LoginGump g && !g.IsDisposed;

    private void PlaceServersButton()
    {
        bool show = WantsServersButton;
        _serversButtonHost.Visible = show;

        if (!show)
        {
            return;
        }

        LoginGump g = UIManager.GetGump<LoginGump>();
        float dpi = Client.Game.DpiScale;
        int art = Math.Max(1, UoTheme.PixelScale);
        _serversButtonHost.Scale = new Vector2(art, art);
        _serversButton.ResetSize();
        Vector2 size = _serversButton.Size * art;
        Vector2 window = GetTree().Root.Size;

        // Beside the login gump, level with its middle, where the screen has
        // room (a phone's wide login screen); else inside its right edge a
        // third of the way down, under the gump's Credits (the desktop's
        // login window is the gump's own size).
        float right = (g.X + g.Width) * dpi;
        Vector2 at = window.X - right >= size.X + 16 * art
            ? new Vector2(right + 8 * art, (g.Y + g.Height / 2f) * dpi - size.Y / 2)
            : new Vector2(right - size.X - 8 * art, (g.Y + g.Height * 0.37f) * dpi);
        at.X = Math.Clamp(at.X, 4 * art, window.X - size.X - 4 * art);
        at.Y = Math.Clamp(at.Y, 4 * art, window.Y - size.Y - 4 * art);
        _serversButtonHost.Position = at.Floor();
    }

    // --- a dev build: the dev logins beside the login gump ---------------------------------

    private Control _devHost;
    private HBoxContainer _devRow;
    private PanelContainer _devPlate;
    private string _devNames;
    private readonly System.Collections.Generic.List<(Button Button, Accounts.SavedAccount Account)> _devButtons = new();
    private int _devPressed = -1;

    /// <summary>Whether the dev logins stand beside the login gump now.</summary>
    public static bool DevRowShown => _instance != null && _instance._devHost.Visible;

    /// <summary>For the probe: each dev login's centre in window pixels, by account name.</summary>
    public static System.Collections.Generic.Dictionary<string, Vector2> DevButtonCentres => _instance == null || !DevRowShown
        ? new()
        : _instance._devButtons.ToDictionary(d => d.Account.Name, d => _instance._devHost.Position + (_instance._devRow.Position + d.Button.Position + d.Button.Size / 2) * _instance._devHost.Scale);

    /// <summary>
    /// The row: at the login screen of a dev build with dev accounts saved,
    /// under the Servers button's place, on every layout (the Servers button
    /// itself stands aside when a second screen holds the card).
    /// </summary>
    private void PlaceDevRow()
    {
        var accounts = Accounts.DevLogin.Accounts;
        bool show = accounts.Count > 0 && !OnMain && Client.Game?.Scene is LoginScene && UIManager.GetGump<LoginGump>() is LoginGump g && !g.IsDisposed;
        _devHost.Visible = show;

        if (!show)
        {
            return;
        }

        string names = string.Join("|", accounts.Select(a => a.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase));

        if (names != _devNames)
        {
            _devNames = names;
            _devButtons.Clear();

            foreach (Node n in _devRow.GetChildren())
            {
                _devRow.RemoveChild(n);
                n.QueueFree();
            }

            _devRow.AddChild(UoTheme.Label("Dev logins", UoTheme.Heading));

            foreach (Accounts.SavedAccount a in accounts.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
            {
                Button b = UoTheme.Button(a.Name, 40);
                b.MouseFilter = Control.MouseFilterEnum.Ignore;
                _devRow.AddChild(b);
                _devButtons.Add((b, a));
            }
        }

        LoginGump gump = UIManager.GetGump<LoginGump>();
        float dpi = Client.Game.DpiScale;
        int art = Math.Max(1, UoTheme.PixelScale);
        _devHost.Scale = new Vector2(art, art);
        _devPlate.ResetSize();
        Vector2 size = _devPlate.Size * art;
        Vector2 window = GetTree().Root.Size;

        // Beside the login gump on the left, where the screen has room (the
        // Servers button takes the right); else across the top of the gump,
        // on the chest's lid, clear of the fields and the Login arrow.
        float left = gump.X * dpi;
        Vector2 at = left >= size.X + 16 * art
            ? new Vector2(left - size.X - 8 * art, (gump.Y + gump.Height / 2f) * dpi - size.Y / 2)
            : new Vector2((gump.X + gump.Width / 2f) * dpi - size.X / 2, (gump.Y + 6) * dpi);
        at.X = Math.Clamp(at.X, 4 * art, window.X - size.X - 4 * art);
        at.Y = Math.Clamp(at.Y, 4 * art, window.Y - size.Y - 4 * art);
        _devHost.Position = at.Floor();
    }

    /// <summary>The dev row's button under <paramref name="at"/> (window pixels), or -1.</summary>
    private int DevButtonAt(Vector2 at)
    {
        for (int i = 0; i < _devButtons.Count; i++)
        {
            Button b = _devButtons[i].Button;

            if (new Rect2(_devHost.Position + (b.Position + _devRow.Position) * _devHost.Scale, b.Size * _devHost.Scale).HasPoint(at))
            {
                return i;
            }
        }

        return -1;
    }

    // --- every frame --------------------------------------------------------------------

    public override void _Process(double delta)
    {
        ServerBook.NoteWorld(Client.Game?.UO?.World?.InGame ?? false);
        Accounts.DevLogin.Update();

        if (!_built)
        {
            if (!UoTheme.Ready || (!ShownOnSecond && Client.Game?.Scene is not LoginScene))
            {
                return;
            }

            Build();
            _built = true;
            GD.Print($"[GUO] pregame card: built");
        }

        // The card over the main screen closes when the login screen goes (a login).
        if (_onMain && Client.Game?.Scene is not LoginScene)
        {
            CloseOnMain();
        }

        PlaceServersButton();
        PlaceDevRow();

        if (!Shown)
        {
            return;
        }

        int art = ArtScale;
        Vector2I size;

        if (_onMain)
        {
            Vector2I window = GetTree().Root.Size;
            size = new Vector2I(Math.Min(window.X, MainMax.X * art), Math.Min(window.Y, MainMax.Y * art));
        }
        else
        {
            size = new Vector2I(DualScreen.LogicalWidth, DualScreen.LogicalHeight);
        }

        if (size != _size || art != _scale)
        {
            _size = size;
            _scale = art;
            _viewport.Size = size;
            _root.Scale = new Vector2(art, art);
            _card.Position = Vector2.Zero;
            _card.Size = new Vector2(size.X / art, size.Y / art);
            bool narrow = size.X / art < PregameSettings.NarrowBelow;
            _settings.SetNarrow(narrow);
            _servers.SetNarrow(narrow);
            _close.Visible = _onMain;
            GD.Print($"[GUO] pregame card: {(_onMain ? "main" : "second")} screen {size.X}x{size.Y}, art x{art}{(narrow ? ", narrow" : "")}");
        }

        if (_onMain)
        {
            Vector2I window = GetTree().Root.Size;
            _band.Visible = true;
            _band.Size = window;
            _view.Visible = true;
            _view.Texture = _viewport.GetTexture();
            _view.Size = size;
            _view.Position = ((window - size) / 2);
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
        if (!ShownOnSecond || !_instance._built)
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
        if (!ShownOnSecond || !_instance._built)
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

    /// <summary>
    /// The main window's events, from GameController before the client sees
    /// them: keys while one of the card's fields is being typed in (either
    /// screen); every pointer event while the card is open over the main
    /// screen (it is modal); a tap on the Servers button. True when consumed.
    /// </summary>
    public static bool HandleMainInput(InputEvent e)
    {
        if (_instance == null || !_instance._built)
        {
            return false;
        }

        PregameCard m = _instance;

        if (e is InputEventKey key)
        {
            if (Shown && m._viewport.GuiGetFocusOwner() is LineEdit)
            {
                m._viewport.PushInput(key, true);
                return true;
            }

            if (m._onMain && key.Pressed && key.Keycode == Key.Escape)
            {
                CloseOnMain();
                return true;
            }

            // A dev build: Ctrl+Shift+D switches to the next dev account, from the world or the login screen.
            if (key.Pressed && !key.Echo && key.Keycode == Key.D && key.CtrlPressed && key.ShiftPressed && Accounts.DevLogin.Available)
            {
                Accounts.DevLogin.SwitchToNext();
                return true;
            }

            return false;
        }

        // One kind of pointer event: a finger on a touch screen, the mouse otherwise.
        InputEvent touch = TouchInput.Enabled ? e switch
        {
            InputEventScreenTouch or InputEventScreenDrag => e,
            _ => null,
        } : e switch
        {
            InputEventMouseButton { ButtonIndex: MouseButton.Left } mb => new InputEventScreenTouch { Position = mb.Position, Pressed = mb.Pressed },
            InputEventMouseMotion mm when (mm.ButtonMask & MouseButtonMask.Left) != 0 => new InputEventScreenDrag { Position = mm.Position },
            InputEventMouseButton { ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown } wheel => wheel,
            _ => null,
        };

        if (m._onMain)
        {
            if (touch is InputEventMouseButton wheel)
            {
                if (wheel.Pressed)
                {
                    m.ScrollOpen(wheel.ButtonIndex == MouseButton.WheelUp ? -12 : 12);
                }

                return true;
            }

            if (touch != null)
            {
                Vector2 at = touch is InputEventScreenTouch t ? t.Position : ((InputEventScreenDrag) touch).Position;
                Vector2 local = at - m._view.Position;

                // A press outside the card closes it, as a press outside the window menu does.
                if (touch is InputEventScreenTouch { Pressed: true } && !new Rect2(Vector2.Zero, m._size).HasPoint(local))
                {
                    CloseOnMain();
                    return true;
                }

                m.Pointer(touch, local);
            }

            // Modal: nothing reaches the login gump under it.
            return e is InputEventMouse or InputEventScreenTouch or InputEventScreenDrag;
        }

        // A dev login: pressed and released on the same button.
        if (m._devHost.Visible && touch is InputEventScreenTouch devTouch)
        {
            int i = m.DevButtonAt(devTouch.Position);

            if (devTouch.Pressed && i >= 0)
            {
                m._devPressed = i;
                return true;
            }

            if (!devTouch.Pressed && m._devPressed >= 0)
            {
                if (i == m._devPressed)
                {
                    Accounts.DevLogin.Start(m._devButtons[i].Account);
                }

                m._devPressed = -1;
                return true;
            }
        }

        if (m._serversButtonHost.Visible && touch is InputEventScreenTouch { Pressed: true } press)
        {
            Rect2 r = new(m._serversButtonHost.Position, m._serversButton.Size * m._serversButtonHost.Scale);

            if (r.HasPoint(press.Position))
            {
                m._serversPressed = true;
                return true;
            }
        }

        if (m._serversPressed && touch is InputEventScreenTouch { Pressed: false })
        {
            m._serversPressed = false;
            OpenOnMain();
            return true;
        }

        return false;
    }

    private bool _serversPressed;

    // --- the pad (the pad-first pregame, docs/ui/pregame_3d.md) -------------------------

    /// <summary>
    /// One pad command for the card open over the main screen: a Godot ui_ action
    /// pushed into the card's viewport, so the controls' own focus navigation moves
    /// and presses. With nothing focused yet, the first command only focuses the
    /// selected server (or the first button). False when the card is not open there.
    /// </summary>
    public static bool PadAction(string action)
    {
        if (_instance == null || !_instance._onMain)
        {
            return false;
        }

        SubViewport vp = _instance._viewport;
        Control owner = vp.GuiGetFocusOwner();

        if (owner == null || !GodotObject.IsInstanceValid(owner) || !owner.IsVisibleInTree())
        {
            _instance.FocusFirst();
            return true;
        }

        vp.PushInput(new InputEventAction { Action = action, Pressed = true, Strength = 1f }, true);
        vp.PushInput(new InputEventAction { Action = action, Pressed = false }, true);
        owner = vp.GuiGetFocusOwner();

        // A scrolled list follows the focus.
        for (Node n = owner?.GetParent(); n != null && n != _instance._root; n = n.GetParent())
        {
            if (n is ScrollContainer sc)
            {
                sc.EnsureControlVisible(owner);
                break;
            }
        }

        return true;
    }

    /// <summary>B on the card: answers "no" to the question it asks, else closes it.</summary>
    public static void PadBack()
    {
        if (_instance == null || !_instance._onMain)
        {
            return;
        }

        Button no = _instance._servers?.NoButton;

        if (no != null && GodotObject.IsInstanceValid(no) && no.IsVisibleInTree())
        {
            no.EmitSignal(BaseButton.SignalName.Pressed);
            return;
        }

        CloseOnMain();
    }

    private void FocusFirst()
    {
        Control target = _servers.Selected != null ? _servers.RowFor(_servers.Selected) : null;

        if (target == null || !target.IsVisibleInTree())
        {
            Control open = _tab == Tab.Servers ? _servers : _settings;
            target = open.FindChildren("*", "BaseButton", true, false).OfType<BaseButton>()
                .FirstOrDefault(b => b.FocusMode == Control.FocusModeEnum.All && !b.Disabled && b.IsVisibleInTree());
        }

        target?.GrabFocus();
    }

    /// <summary>A finger at <paramref name="local"/> (the card's viewport pixels); also the probe's tap.</summary>
    public void Pointer(InputEvent e, Vector2 local)
    {
        switch (e)
        {
            case InputEventScreenTouch { Pressed: true }:
                _dragging = true;
                _dragMoved = false;
                _dragLast = local;
                _dragStart = local;
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
                    _dragMoved |= Math.Abs(dy) > 0.5f * _scale;
                    ScrollOpen(-dy / _scale, _dragStart);
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

                    // A field wants the keyboard: on a phone that means the soft one.
                    if (_viewport.GuiGetFocusOwner() is LineEdit field && DisplayServer.HasFeature(DisplayServer.Feature.VirtualKeyboard))
                    {
                        DisplayServer.VirtualKeyboardShow(field.Text);
                    }
                }

                _dragging = false;
                _sliding = false;
                break;
        }
    }

    private void ScrollOpen(float artPixels, Vector2 at = default)
    {
        if (_settings.Visible)
        {
            _settings.ScrollBy(artPixels);
        }
        else
        {
            _servers.ScrollBy(artPixels, at);
        }
    }

    private static void HideKeyboard()
    {
        if (DisplayServer.HasFeature(DisplayServer.Feature.VirtualKeyboard))
        {
            DisplayServer.VirtualKeyboardHide();
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
        // Into view first, as a finger would scroll to it.
        for (Node n = c.GetParent(); n != null; n = n.GetParent())
        {
            if (n is ScrollContainer scroll)
            {
                scroll.EnsureControlVisible(c);
                break;
            }
        }

        Vector2 at = c.GetGlobalRect().GetCenter();
        Pointer(new InputEventScreenTouch { Pressed = true, Position = at }, at);
        Pointer(new InputEventScreenTouch { Pressed = false, Position = at }, at);
    }

    /// <summary>For the probe: type into the focused field, as the keyboard would.</summary>
    public void Type(string text)
    {
        foreach (char ch in text)
        {
            _viewport.PushInput(new InputEventKey { Unicode = ch, Pressed = true, Keycode = Key.None }, true);
            _viewport.PushInput(new InputEventKey { Unicode = ch, Pressed = false, Keycode = Key.None }, true);
        }
    }

    /// <summary>For the probe: the tab buttons.</summary>
    public Button TabButtonFor(Tab tab) => tab == Tab.Servers ? _tabServers : _tabSettings;

    /// <summary>For the probe: the Servers button's centre in window pixels, when it stands.</summary>
    public static Vector2? ServersButtonCentre => ServersButtonShown
        ? _instance._serversButtonHost.Position + _instance._serversButton.Size * _instance._serversButtonHost.Scale / 2
        : null;

    /// <summary>For the probe: whether the card is taller than its screen (its content would not fit).</summary>
    public bool Overflows => _card != null && _card.Size.Y * _scale > _size.Y + 0.5f;

    /// <summary>For the probe: the card's size and least size in art pixels.</summary>
    public string CardSize => _card == null ? "none" : $"{_card.Size} least {_card.GetCombinedMinimumSize()}";

    /// <summary>For the probe: the card's viewport size in pixels.</summary>
    public Vector2I Size => _size;

    /// <summary>For the probe: the card's art scale and logical size.</summary>
    public string Geometry => $"{_size.X}x{_size.Y} at x{_scale}";
}
