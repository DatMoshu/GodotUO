// SPDX-License-Identifier: BSD-2-Clause
using System;
using Godot;
using GUO.Game.Managers;
using GUO.Game.UI.Gumps;
using GUO.Platform.Android;
using GUO.Renderer;

namespace GUO.Input.Touch;

/// <summary>
/// The window menu: size, pinch lock and screen for one gump, as a card of
/// Godot controls in the client's own look (UoTheme, docs/ui/uo_godot_style.md):
/// the grey stone frame, font 1, the marble plate buttons, UO's checkbox and
/// slider, at a whole-number scale. Opened by GumpPresentation.OpenMenu: a
/// hold-and-release on touch, the handle, or a controller button.
/// </summary>
/// <remarks>
/// PORT DEVIATION (GUO): not in ClassicUO; mobile only (GumpPresentation.Active).
/// The card lives in a SubViewport so it can appear on either screen. On the
/// main screen its texture is shown by a CanvasLayer at the device's own pixels,
/// so it is crisp; on the second screen, which is a bitmap the client draws, it
/// is drawn into that bitmap by DualScreen.Draw. Pointer events over the card
/// are pushed into the viewport (<see cref="HandleInput"/>); a press outside
/// closes it. All of it is UO art, sampled nearest-neighbour (rule 7).
/// </remarks>
internal sealed partial class WindowMenu : Node
{
    private const float CardWidth = 200f;   // art pixels (UoTheme.PixelScale device pixels each)
    private const float Pad = 14f;          // room for the shadow and the notch
    private const float Notch = 9f;
    private const double OpenSeconds = 0.12;

    private static readonly Color Text = UoTheme.Ink, Muted = UoTheme.Muted, Heading = UoTheme.Heading;

    private static WindowMenu _instance;

    /// <summary>The gump the menu is for, or null when closed.</summary>
    public static Gump Target => _instance?._gump;

    public static bool IsOpen => _instance != null && GodotObject.IsInstanceValid(_instance) && _instance._gump != null;

    /// <summary>Set to skip the open animation (reduced motion).</summary>
    public static bool ReducedMotion { get; set; } = OS.GetEnvironment("GUO_REDUCED_MOTION") == "1";

    private Gump _gump;
    private SubViewport _viewport;
    private Control _root;
    private PanelContainer _card;
    private NotchControl _notch;
    private CanvasLayer _layer;
    private TextureRect _mainView;
    private Label _title, _caption, _sizeLabel;
    private HSlider _slider;
    private CheckBox _lock;
    private Button _move;
    private Button _read; // the journal only: its Modern reader (ADR-0024)
    private bool _syncing;
    private bool _dragging;
    private float _scale = 1f;

    /// <summary>Client pixels to device pixels (1 on the Thor).</summary>
    private static float Dpi => Math.Max(0.01f, Client.Game?.DpiScale ?? 1f);

    /// <summary>Art pixels to client pixels: the card's scale over the client's.</summary>
    private float ArtToClient => _scale / Dpi;
    private float _alpha = 1f;

    /// <summary>The card's rectangle in client coordinates (either screen), including the shadow pad.</summary>
    private Rect2 _rect;
    private bool _onSecond;
    private int _notchSide; // 0 left, 1 right, 2 top, 3 bottom (the card side facing the gump)

    public static void Open(Gump g)
    {
        if (g == null || g.IsDisposed || Client.Game == null)
        {
            return;
        }

        if (_instance == null || !GodotObject.IsInstanceValid(_instance))
        {
            _instance = new WindowMenu();
            Client.Game.AddChild(_instance);
        }

        _instance.Show(g);
    }

    public static void Close()
    {
        if (_instance != null && GodotObject.IsInstanceValid(_instance))
        {
            _instance.Hide();
        }
    }

    public override void _Ready()
    {
        // A whole number, so one art pixel is a square of device pixels.
        _scale = UoTheme.PixelScale;

        _viewport = new SubViewport
        {
            TransparentBg = true,
            Disable3D = true,
            HandleInputLocally = true,
            GuiEmbedSubwindows = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            CanvasItemDefaultTextureFilter = Viewport.DefaultCanvasItemTextureFilter.Nearest,
        };
        AddChild(_viewport);

        _root = new Control { Scale = new Vector2(_scale, _scale), MouseFilter = Control.MouseFilterEnum.Ignore };
        _viewport.AddChild(_root);

        _notch = new NotchControl { MouseFilter = Control.MouseFilterEnum.Ignore };
        _root.AddChild(_notch);

        _card = new PanelContainer { Theme = UoTheme.Theme, CustomMinimumSize = new Vector2(CardWidth, 0) };
        _root.AddChild(_card);
        BuildCard();

        _layer = new CanvasLayer { Layer = 100 };
        AddChild(_layer);
        _mainView = new TextureRect
        {
            Texture = _viewport.GetTexture(),
            MouseFilter = Control.MouseFilterEnum.Ignore,
            StretchMode = TextureRect.StretchModeEnum.Keep,
            TextureFilter = CanvasItem.TextureFilterEnum.Nearest,
        };
        _layer.AddChild(_mainView);

        Hide();
    }

    // --- building -------------------------------------------------------------

    /// <summary>A label in font 1: <paramref name="big"/> is 2x, the card's title.</summary>
    private static Label Lbl(string text, bool big, Color color) => UoTheme.Label(text, color, big ? 2 : 1);

    /// <summary>A control at least a plate tall (a finger, once the card is scaled).</summary>
    private static T Touch<T>(T c, float minWidth = 24f) where T : Control
    {
        c.CustomMinimumSize = new Vector2(Math.Max(c.CustomMinimumSize.X, minWidth), UoTheme.ButtonHeight);

        // A plate never stretches taller than itself: its rounded ends would.
        if (c is Button && c is not CheckBox)
        {
            c.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        }

        return c;
    }

    private void BuildCard()
    {
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 5);
        _card.AddChild(col);

        // Header: name and caption, and a close button.
        var header = new HBoxContainer();
        col.AddChild(header);
        var titles = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        titles.AddThemeConstantOverride("separation", 0);
        header.AddChild(titles);
        titles.AddChild(_title = Lbl("Window", true, Heading));
        titles.AddChild(_caption = Lbl("", false, Muted));
        var close = Touch(new Button { Text = "X", TooltipText = "Close" }, 30);
        close.Pressed += Hide;
        header.AddChild(close);

        col.AddChild(new HSeparator());

        // Size: stepper and slider.
        col.AddChild(Lbl("Size", false, Heading));
        var stepper = new HBoxContainer();
        stepper.AddThemeConstantOverride("separation", 4);
        col.AddChild(stepper);
        var minus = Touch(new Button { Text = "-" }, 40);
        minus.Pressed += () => Step(-0.25f);
        stepper.AddChild(minus);
        _sizeLabel = Lbl("100%", true, Text);
        _sizeLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _sizeLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        stepper.AddChild(_sizeLabel);
        var plus = Touch(new Button { Text = "+" }, 40);
        plus.Pressed += () => Step(0.25f);
        stepper.AddChild(plus);
        // The D-pad steps across the size between them, which Godot's own
        // neighbour search does not.
        minus.FocusNeighborRight = minus.GetPathTo(plus);
        plus.FocusNeighborLeft = plus.GetPathTo(minus);

        _slider = new HSlider
        {
            MinValue = GumpPresentation.MinScale * 100, MaxValue = GumpPresentation.MaxScale * 100, Step = 1,
            CustomMinimumSize = new Vector2(0, 16),
        };
        _slider.ValueChanged += v => { if (!_syncing) SetPercent((float)v / 100f); };
        // The card holds still under a dragging finger and finds its place after.
        _slider.DragStarted += () => _dragging = true;
        _slider.DragEnded += _ => { _dragging = false; Place(); };
        col.AddChild(_slider);

        // Lock pinch.
        var lockRow = new VBoxContainer();
        lockRow.AddThemeConstantOverride("separation", 0);
        col.AddChild(lockRow);
        _lock = Touch(new CheckBox { Text = "Lock pinch size" });
        _lock.Toggled += on => { if (!_syncing && _gump != null) _gump.PresentationLocked = on; };
        lockRow.AddChild(_lock);
        lockRow.AddChild(Lbl("Two fingers leave this window's size alone.", false, Muted));

        // Actions.
        _move = Touch(new Button { Text = "Move to bottom screen" });
        _move.AddThemeColorOverride("font_color", Heading);
        _move.Pressed += MoveScreen;
        col.AddChild(_move);

        var reset = Touch(new Button { Text = "Reset size" });
        reset.Pressed += () =>
        {
            if (_gump == null) return;
            GumpPresentation.Reset(_gump);
            Sync();
            Place();
        };
        col.AddChild(reset);

        // The journal: read it full height in large type (the Modern journal).
        _read = Touch(new Button { Text = "Read" });
        _read.AddThemeColorOverride("font_color", Heading);
        _read.Pressed += () =>
        {
            if (_gump?.World == null) return;
            Game.World world = _gump.World;
            Hide();
            Modern.ModernJournal.OpenReader(world);
        };
        col.AddChild(_read);
    }

    // --- behaviour ------------------------------------------------------------

    private void Step(float delta) => SetPercent((float)(Math.Round(((_gump?.PresentationScale ?? 1f) + delta) * 4) / 4));

    private void SetPercent(float scale)
    {
        if (_gump == null) return;
        bool locked = _gump.PresentationLocked;
        _gump.PresentationLocked = false;
        GumpPresentation.SetScale(_gump, scale, new Compat.Point(_gump.X, _gump.Y));
        _gump.PresentationLocked = locked;
        Sync();

        // Buttons stay under the finger for repeated taps; the card moves only
        // when the growing window would run under it, and never mid-drag.
        if (!_dragging && Overlaps())
        {
            Place();
        }
    }

    private void MoveScreen()
    {
        if (_gump == null) return;

        if (DualScreen.ShelfOn)
        {
            GumpPresentation.Transfer(_gump);
        }
        else
        {
            GumpFlick.Fit(_gump);
        }

        Sync();
        Place();
    }

    private new void Show(Gump g)
    {
        _gump = g;
        _viewport.GuiReleaseFocus();
        Sync();
        Place();

        if (ReducedMotion)
        {
            _alpha = 1f;
            _card.Scale = Vector2.One;
            _card.Modulate = Colors.White;
        }
        else
        {
            _alpha = 0f;
            _card.PivotOffset = _card.Size / 2;
            _card.Scale = new Vector2(0.96f, 0.96f);
            Tween t = CreateTween().SetParallel();
            t.TweenProperty(_card, "scale", Vector2.One, OpenSeconds).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
            t.TweenMethod(Callable.From<float>(a => _alpha = a), 0f, 1f, OpenSeconds);
        }

        GD.Print($"[GUO] window menu: open for {g.GetType().Name} on the {(_onSecond ? "second" : "main")} screen");
    }

    private new void Hide()
    {
        if (_gump != null)
        {
            GD.Print("[GUO] window menu: closed");
        }

        _gump = null;

        if (_mainView != null)
        {
            _mainView.Visible = false;
        }
    }

    public override void _Process(double delta)
    {
        if (_gump == null)
        {
            return;
        }

        if (_gump.IsDisposed || !_gump.IsVisible || !GumpPresentation.Active)
        {
            Hide();
            return;
        }

        _mainView.Visible = !_onSecond;
        _mainView.Modulate = new Color(1, 1, 1, _alpha);
    }

    private void Sync()
    {
        if (_gump == null) return;
        _syncing = true;
        float s = _gump.PresentationScale;
        _title.Text = Title(_gump);
        bool second = GumpPresentation.OnSecond(_gump);
        _caption.Text = (DualScreen.ShelfOn ? (second ? "Bottom screen" : "Top screen") : "This screen") + $", {s * 100:0}%";
        _sizeLabel.Text = $"{s * 100:0}%";
        _slider.Value = s * 100;
        _lock.ButtonPressed = _gump.PresentationLocked;
        _move.Text = DualScreen.ShelfOn ? (second ? "Move to top screen" : "Move to bottom screen") : "Fit to screen";
        _read.Visible = _gump is JournalGump or ResizableJournal;
        _syncing = false;
    }

    public static string Title(Gump g)
    {
        switch (g)
        {
            case PaperDollGump: return "Paperdoll";
            case StatusGumpBase: return "Status";
            case JournalGump or ResizableJournal: return "Journal";
        }

        var item = g.World?.Get(g.LocalSerial) as Game.GameObjects.Item;
        if (item != null && (g is ContainerGump || g is GridContainerGump))
        {
            if (item == g.World.Player?.FindItemByLayer(Game.Data.Layer.Backpack)) return "Backpack";
            return string.IsNullOrEmpty(item.Name) ? item.ItemData.Name : item.Name;
        }

        return g.GetType().Name.Replace("Gump", "");
    }

    /// <summary>Whether the gump now runs under the card by more than an edge.</summary>
    private bool Overlaps()
    {
        Compat.Rectangle g = GumpPresentation.Bounds(_gump);
        var gr = new Rect2(g.X, g.Y, g.Width, g.Height);
        var card = new Rect2(_rect.Position + new Vector2(Pad, Pad), _rect.Size - new Vector2(Pad * 2, Pad * 2));
        Rect2 cut = gr.Intersection(card);
        return cut.Size.X > 24 && cut.Size.Y > 24;
    }

    /// <summary>Beside the gump, on its screen, clear of the touch bar; the notch points at it.</summary>
    private void Place()
    {
        if (_gump == null) return;

        _card.ResetSize();
        Vector2 cardArt = _card.GetCombinedMinimumSize();
        cardArt.X = Math.Max(cardArt.X, CardWidth);
        _card.Size = cardArt;

        // The card is laid out in art pixels; the screen in client pixels.
        // One art pixel is _scale device pixels, one client pixel DpiScale.
        float k = ArtToClient;
        Vector2 card = cardArt * k;
        float pad = Pad * k;

        _onSecond = GumpPresentation.OnSecond(_gump);
        Compat.Rectangle d = GumpPresentation.DisplayBounds(_onSecond);
        Compat.Rectangle g = GumpPresentation.Bounds(_gump);
        float gap = Notch + 2;
        float x, y;

        if (g.Right + gap + card.X <= d.Right)
        {
            _notchSide = 0; x = g.Right + gap; y = g.Y + g.Height / 2f - card.Y / 2f;
        }
        else if (g.X - gap - card.X >= d.X)
        {
            _notchSide = 1; x = g.X - gap - card.X; y = g.Y + g.Height / 2f - card.Y / 2f;
        }
        else if (g.Bottom + gap + card.Y <= d.Bottom)
        {
            _notchSide = 2; x = g.X + g.Width / 2f - card.X / 2f; y = g.Bottom + gap;
        }
        else if (g.Y - gap - card.Y >= d.Y)
        {
            _notchSide = 3; x = g.X + g.Width / 2f - card.X / 2f; y = g.Y - gap - card.Y;
        }
        else
        {
            // No side has room (a large gump on a small screen): dock to the
            // screen edge on the side with more room, so the card covers only
            // the gump's edge, never its middle.
            bool right = d.Right - g.Right >= g.X - d.X;
            _notchSide = right ? 0 : 1;
            x = right ? d.Right - card.X - 4 : d.X + 4;
            y = g.Y + g.Height / 2f - card.Y / 2f;
        }

        x = Math.Clamp(x, d.X + 4, Math.Max(d.X + 4, d.Right - card.X - 4));
        y = Math.Clamp(y, 4, Math.Max(4, d.Bottom - card.Y - 4));

        // Where the notch sits along the facing edge: toward the gump's centre.
        float along = _notchSide <= 1
            ? Math.Clamp(g.Y + g.Height / 2f - y, 24 * k, card.Y - 24 * k)
            : Math.Clamp(g.X + g.Width / 2f - x, 24 * k, card.X - 24 * k);

        _card.Position = new Vector2(Pad, Pad);
        _notch.Setup(_notchSide, along / k, cardArt, Pad);
        _rect = new Rect2(x - pad, y - pad, card.X + pad * 2, card.Y + pad * 2);

        var px = new Vector2I((int)Math.Ceiling((cardArt.X + Pad * 2) * _scale), (int)Math.Ceiling((cardArt.Y + Pad * 2) * _scale));
        _viewport.Size = px;
        _mainView.Position = _rect.Position * Dpi;
        _mainView.Size = px;
    }

    // --- drawing and input ------------------------------------------------------

    /// <summary>The card on the second screen, drawn into its bitmap in client pixels.</summary>
    public static void DrawShelf(UltimaBatcher2D b)
    {
        if (!IsOpen || !_instance._onSecond) return;
        Rect2 r = _instance._rect;
        b.Draw(_instance._viewport.GetTexture(), new Compat.Rectangle((int)r.Position.X, (int)r.Position.Y, (int)r.Size.X, (int)r.Size.Y),
            ShaderHueTranslator.GetHueVector(0, false, _instance._alpha), 0);
    }

    /// <summary>
    /// Route a pointer event while the menu is open: over the card it goes
    /// into the card; a press elsewhere closes the menu. True when consumed.
    /// Positions are window pixels, as Godot and DualScreen deliver them.
    /// </summary>
    public static bool HandleInput(InputEvent e)
    {
        if (!IsOpen) return false;
        WindowMenu m = _instance;

        Vector2? window = e switch
        {
            InputEventScreenTouch t => t.Position,
            InputEventScreenDrag d => d.Position,
            InputEventMouseButton mb => mb.Position,
            InputEventMouseMotion mm => mm.Position,
            _ => null,
        };

        if (window == null) return false;
        TouchOverlay.Note(e);

        float dpi = Client.Game.DpiScale;
        Vector2 client = window.Value / dpi;
        bool pressed = e is InputEventScreenTouch { Pressed: true } || e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left };
        bool released = e is InputEventScreenTouch { Pressed: false } || e is InputEventMouseButton { Pressed: false, ButtonIndex: MouseButton.Left };

        if (!m._rect.HasPoint(client))
        {
            if (pressed) m.Hide();
            return true; // nothing reaches the game while the menu is up
        }

        Vector2 local = (client - m._rect.Position) * Dpi;

        if (e is InputEventScreenDrag || e is InputEventMouseMotion)
        {
            m._viewport.PushInput(new InputEventMouseMotion
            {
                Position = local, GlobalPosition = local,
                ButtonMask = e is InputEventScreenDrag || Godot.Input.IsMouseButtonPressed(MouseButton.Left) ? MouseButtonMask.Left : 0,
            }, true);
        }
        else if (pressed || released)
        {
            m._viewport.PushInput(new InputEventMouseMotion { Position = local, GlobalPosition = local }, true);
            m._viewport.PushInput(new InputEventMouseButton
            {
                Position = local, GlobalPosition = local, ButtonIndex = MouseButton.Left, Pressed = pressed,
                ButtonMask = pressed ? MouseButtonMask.Left : 0,
            }, true);
        }

        return true;
    }

    /// <summary>
    /// A controller's D-pad on the open card: <paramref name="action"/> is
    /// ui_up, ui_down, ui_left or ui_right. The first press selects the card's
    /// first control; after that Godot's own focus navigation moves between
    /// them (left and right step a selected slider). Works on either screen,
    /// as it needs no pointer. True when the menu took it.
    /// </summary>
    public static bool Navigate(string action)
    {
        if (!IsOpen) return false;
        WindowMenu m = _instance;

        if (m._viewport.GuiGetFocusOwner() == null)
        {
            m.FirstFocusable()?.GrabFocus();
            return true;
        }

        m._viewport.PushInput(new InputEventAction { Action = action, Pressed = true }, true);
        m._viewport.PushInput(new InputEventAction { Action = action, Pressed = false }, true);
        return true;
    }

    /// <summary>True when a control of the open card has controller focus.</summary>
    public static bool HasControllerFocus => IsOpen && _instance._viewport.GuiGetFocusOwner() != null;

    /// <summary>A controller's A on the card: press or release the selected control.</summary>
    public static bool Accept(bool pressed)
    {
        if (!HasControllerFocus) return false;
        _instance._viewport.PushInput(new InputEventAction { Action = "ui_accept", Pressed = pressed }, true);
        return true;
    }

    /// <summary>The control with focus, for the probe.</summary>
    public static Control FocusOwner => IsOpen ? _instance._viewport.GuiGetFocusOwner() : null;

    private Control FirstFocusable()
    {
        foreach (Node n in _card.FindChildren("*", "Control", true, false))
        {
            if (n is Control c && c.FocusMode != Control.FocusModeEnum.None && c.IsVisibleInTree())
            {
                return c;
            }
        }

        return null;
    }

    /// <summary>For the probe: press a control of the card by its text.</summary>
    public static bool PressForProbe(string text)
    {
        if (!IsOpen) return false;
        foreach (Node n in _instance._card.FindChildren("*", "BaseButton", true, false))
        {
            if (n is BaseButton b && ((b as Button)?.Text?.Contains(text) ?? false))
            {
                if (b is CheckButton cb) cb.ButtonPressed = !cb.ButtonPressed;
                else b.EmitSignal(BaseButton.SignalName.Pressed);
                return true;
            }
        }
        return false;
    }

    /// <summary>For the probe: the centre of the card's button with this text, in client pixels.</summary>
    public static Vector2? ButtonCentre(string text)
    {
        if (!IsOpen) return null;
        foreach (Node n in _instance._card.FindChildren("*", "BaseButton", true, false))
        {
            if (n is Button b && b.Text == text && b.IsVisibleInTree())
            {
                Vector2 inViewport = b.GetGlobalRect().GetCenter() * _instance._scale;
                return _instance._rect.Position + inViewport / Dpi;
            }
        }
        return null;
    }

    /// <summary>For the probe: the card's rectangle in client pixels and which screen.</summary>
    public static Rect2 CardRect => _instance?._rect ?? default;

    /// <summary>The little pointer toward the gump, drawn behind the card's edge.</summary>
    private sealed partial class NotchControl : Control
    {
        private int _side;
        private float _along;
        private Vector2 _card;
        private float _pad;

        public void Setup(int side, float along, Vector2 card, float pad)
        {
            _side = side; _along = along; _card = card; _pad = pad;
            Size = card + new Vector2(pad * 2, pad * 2);
            QueueRedraw();
        }

        public override void _Draw()
        {
            float n = Notch;
            Vector2 o = new(_pad, _pad);
            Vector2[] pts = _side switch
            {
                0 => new[] { o + new Vector2(1, _along - n), o + new Vector2(-n, _along), o + new Vector2(1, _along + n) },
                1 => new[] { o + new Vector2(_card.X - 1, _along - n), o + new Vector2(_card.X + n, _along), o + new Vector2(_card.X - 1, _along + n) },
                2 => new[] { o + new Vector2(_along - n, 1), o + new Vector2(_along, -n), o + new Vector2(_along + n, 1) },
                _ => new[] { o + new Vector2(_along - n, _card.Y - 1), o + new Vector2(_along, _card.Y + n), o + new Vector2(_along + n, _card.Y - 1) },
            };
            // Stone, outlined in ink, hard-edged: the frame's own colours.
            DrawColoredPolygon(pts, new Color("7c776e"));
            DrawPolyline(new[] { pts[0], pts[1], pts[2] }, UoTheme.Ink, 1f, false);
        }
    }
}
