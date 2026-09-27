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
/// Godot controls in the Store window's visual language (charcoal and gold,
/// a clean sans, rounded, finger-sized). Opened by GumpPresentation.OpenMenu:
/// a hold-and-release on touch, the handle, or a controller button.
/// </summary>
/// <remarks>
/// PORT DEVIATION (GUO): not in ClassicUO; mobile only (GumpPresentation.Active).
/// The card lives in a SubViewport so it can appear on either screen. On the
/// main screen its texture is shown by a CanvasLayer at the device's own pixels,
/// so it is crisp; on the second screen, which is a bitmap the client draws, it
/// is drawn into that bitmap by DualScreen.Draw. Pointer events over the card
/// are pushed into the viewport (<see cref="HandleInput"/>); a press outside
/// closes it. Only the card is vector UI with default sampling; UO art keeps
/// nearest-neighbour.
/// </remarks>
internal sealed partial class WindowMenu : Node
{
    private const float CardWidth = 300f;   // client (dp) pixels
    private const float Pad = 14f;          // room for the shadow and the notch
    private const float Notch = 9f;
    private const double OpenSeconds = 0.12;

    private static readonly Color Gold = new("dfbb77"), Text = new("eeeade"), Muted = new("abb5ac");

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
    private CheckButton _lock;
    private Button _move;
    private bool _syncing;
    private bool _dragging;
    private float _scale = 1f;
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
        _scale = Math.Max(1f, Client.Game?.DpiScale ?? 1f);

        _viewport = new SubViewport
        {
            TransparentBg = true,
            Disable3D = true,
            HandleInputLocally = true,
            GuiEmbedSubwindows = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
        };
        AddChild(_viewport);

        _root = new Control { Scale = new Vector2(_scale, _scale), MouseFilter = Control.MouseFilterEnum.Ignore };
        _viewport.AddChild(_root);

        _notch = new NotchControl { MouseFilter = Control.MouseFilterEnum.Ignore };
        _root.AddChild(_notch);

        _card = new PanelContainer { Theme = BuildTheme(), CustomMinimumSize = new Vector2(CardWidth, 0) };
        _card.AddThemeStyleboxOverride("panel", CardStyle());
        _root.AddChild(_card);
        BuildCard();

        _layer = new CanvasLayer { Layer = 100 };
        AddChild(_layer);
        _mainView = new TextureRect
        {
            Texture = _viewport.GetTexture(),
            MouseFilter = Control.MouseFilterEnum.Ignore,
            StretchMode = TextureRect.StretchModeEnum.Keep,
        };
        _layer.AddChild(_mainView);

        Hide();
    }

    // --- building -------------------------------------------------------------

    private static StyleBoxFlat CardStyle() => new()
    {
        BgColor = new Color(0.078f, 0.098f, 0.090f, 1f),
        BorderColor = new Color(Gold, 0.45f),
        BorderWidthLeft = 1, BorderWidthRight = 1, BorderWidthTop = 1, BorderWidthBottom = 1,
        CornerRadiusTopLeft = 12, CornerRadiusTopRight = 12, CornerRadiusBottomLeft = 12, CornerRadiusBottomRight = 12,
        ShadowColor = new Color(0, 0, 0, 0.45f), ShadowSize = 10, ShadowOffset = new Vector2(0, 3),
        ContentMarginLeft = 16, ContentMarginRight = 16, ContentMarginTop = 12, ContentMarginBottom = 16,
        AntiAliasing = true,
    };

    private static StyleBoxFlat Box(string bg, string border, int radius = 8) => new()
    {
        BgColor = new Color(bg), BorderColor = new Color(border),
        BorderWidthLeft = 1, BorderWidthRight = 1, BorderWidthTop = 1, BorderWidthBottom = 1,
        CornerRadiusTopLeft = radius, CornerRadiusTopRight = radius, CornerRadiusBottomLeft = radius, CornerRadiusBottomRight = radius,
        ContentMarginLeft = 12, ContentMarginRight = 12, ContentMarginTop = 6, ContentMarginBottom = 6,
    };

    private static Theme BuildTheme()
    {
        var theme = new Theme { DefaultFontSize = 15 };

        foreach (string control in new[] { "Button", "CheckButton" })
        {
            theme.SetStylebox("normal", control, Box("202922", "455342"));
            theme.SetStylebox("hover", control, Box("303d2e", "b4a16b"));
            theme.SetStylebox("pressed", control, Box("3f4931", "dfbb77"));
            theme.SetStylebox("disabled", control, Box("181d1a", "2c332e"));
            theme.SetStylebox("focus", control, new StyleBoxEmpty());
            theme.SetColor("font_color", control, Text);
            theme.SetColor("font_hover_color", control, Text);
            theme.SetColor("font_pressed_color", control, Text);
        }

        theme.SetColor("font_color", "Label", Text);
        return theme;
    }

    private static Label Lbl(string text, int size, Color color)
    {
        var l = new Label { Text = text, VerticalAlignment = VerticalAlignment.Center };
        l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeColorOverride("font_color", color);
        return l;
    }

    private static T Touch<T>(T c, float minWidth = 48f) where T : Control
    {
        c.CustomMinimumSize = new Vector2(Math.Max(c.CustomMinimumSize.X, minWidth), 48f);
        return c;
    }

    private void BuildCard()
    {
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 10);
        _card.AddChild(col);

        // Header: name and caption, and a close button.
        var header = new HBoxContainer();
        col.AddChild(header);
        var titles = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        titles.AddThemeConstantOverride("separation", 0);
        header.AddChild(titles);
        titles.AddChild(_title = Lbl("Window", 20, Text));
        titles.AddChild(_caption = Lbl("", 13, Muted));
        var close = Touch(new Button { Text = "✕", TooltipText = "Close" });
        close.AddThemeFontSizeOverride("font_size", 18);
        close.Pressed += Hide;
        header.AddChild(close);

        col.AddChild(new HSeparator { Modulate = new Color(Gold, 0.35f) });

        // Size: stepper and slider.
        col.AddChild(Lbl("Size", 13, Gold));
        var stepper = new HBoxContainer();
        stepper.AddThemeConstantOverride("separation", 8);
        col.AddChild(stepper);
        var minus = Touch(new Button { Text = "−" }, 64);
        minus.AddThemeFontSizeOverride("font_size", 22);
        minus.Pressed += () => Step(-0.25f);
        stepper.AddChild(minus);
        _sizeLabel = Lbl("100%", 22, Text);
        _sizeLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _sizeLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        stepper.AddChild(_sizeLabel);
        var plus = Touch(new Button { Text = "+" }, 64);
        plus.AddThemeFontSizeOverride("font_size", 22);
        plus.Pressed += () => Step(0.25f);
        stepper.AddChild(plus);

        _slider = new HSlider
        {
            MinValue = GumpPresentation.MinScale * 100, MaxValue = GumpPresentation.MaxScale * 100, Step = 1,
            CustomMinimumSize = new Vector2(0, 32),
        };
        _slider.AddThemeStyleboxOverride("slider", new StyleBoxFlat
        {
            BgColor = new Color("2c352f"), ContentMarginTop = 2, ContentMarginBottom = 2,
            CornerRadiusTopLeft = 2, CornerRadiusTopRight = 2, CornerRadiusBottomLeft = 2, CornerRadiusBottomRight = 2,
        });
        _slider.AddThemeStyleboxOverride("grabber_area", new StyleBoxFlat
        {
            BgColor = Gold, ContentMarginTop = 2, ContentMarginBottom = 2,
            CornerRadiusTopLeft = 2, CornerRadiusTopRight = 2, CornerRadiusBottomLeft = 2, CornerRadiusBottomRight = 2,
        });
        _slider.AddThemeStyleboxOverride("grabber_area_highlight", new StyleBoxFlat { BgColor = Gold });
        _slider.AddThemeIconOverride("grabber", MakeKnob(false));
        _slider.AddThemeIconOverride("grabber_highlight", MakeKnob(true));
        _slider.ValueChanged += v => { if (!_syncing) SetPercent((float)v / 100f); };
        // The card holds still under a dragging finger and finds its place after.
        _slider.DragStarted += () => _dragging = true;
        _slider.DragEnded += _ => { _dragging = false; Place(); };
        col.AddChild(_slider);

        // Lock pinch.
        var lockRow = new VBoxContainer();
        lockRow.AddThemeConstantOverride("separation", 0);
        col.AddChild(lockRow);
        _lock = Touch(new CheckButton { Text = "Lock pinch size" });
        _lock.Toggled += on => { if (!_syncing && _gump != null) _gump.PresentationLocked = on; };
        lockRow.AddChild(_lock);
        lockRow.AddChild(Lbl("Two fingers leave this window's size alone.", 12, Muted));

        // Actions.
        _move = Touch(new Button { Text = "Move to bottom screen" });
        _move.AddThemeStyleboxOverride("normal", Box("b58e42", "dfbb77"));
        _move.AddThemeStyleboxOverride("hover", Box("c79d4d", "f0d08a"));
        _move.AddThemeStyleboxOverride("pressed", Box("9e7a36", "dfbb77"));
        _move.AddThemeColorOverride("font_color", new Color("141917"));
        _move.AddThemeColorOverride("font_hover_color", new Color("141917"));
        _move.AddThemeColorOverride("font_pressed_color", new Color("141917"));
        _move.AddThemeFontSizeOverride("font_size", 16);
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
    }

    /// <summary>A round slider knob: gold with a dark ring, larger while held.</summary>
    private static Texture2D MakeKnob(bool hot)
    {
        int size = hot ? 26 : 22;
        var img = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float d = new Vector2(x + 0.5f - size / 2f, y + 0.5f - size / 2f).Length();
                float r = size / 2f;
                Color c = d > r ? Colors.Transparent : d > r - 2.5f ? new Color("141917") : Gold;
                if (d > r - 1f && d <= r) c.A = r - d;
                img.SetPixel(x, y, c);
            }
        }

        return ImageTexture.CreateFromImage(img);
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
        _caption.Text = (DualScreen.ShelfOn ? (second ? "Bottom screen" : "Top screen") : "This screen") + $" · {s * 100:0}%";
        _sizeLabel.Text = $"{s * 100:0}%";
        _slider.Value = s * 100;
        _lock.ButtonPressed = _gump.PresentationLocked;
        _move.Text = DualScreen.ShelfOn ? (second ? "↑  Move to top screen" : "↓  Move to bottom screen") : "⤢  Fit to screen";
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
        Vector2 card = _card.GetCombinedMinimumSize();
        card.X = Math.Max(card.X, CardWidth);
        _card.Size = card;

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
            ? Math.Clamp(g.Y + g.Height / 2f - y, 24, card.Y - 24)
            : Math.Clamp(g.X + g.Width / 2f - x, 24, card.X - 24);

        _card.Position = new Vector2(Pad, Pad);
        _notch.Setup(_notchSide, along, card, Pad);
        _rect = new Rect2(x - Pad, y - Pad, card.X + Pad * 2, card.Y + Pad * 2);

        var px = new Vector2I((int)Math.Ceiling(_rect.Size.X * _scale), (int)Math.Ceiling(_rect.Size.Y * _scale));
        _viewport.Size = px;
        _mainView.Position = _rect.Position * _scale;
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

        Vector2 local = (client - m._rect.Position) * m._scale;

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
                return _instance._rect.Position + inViewport / _instance._scale;
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
            DrawColoredPolygon(pts, new Color(0.078f, 0.098f, 0.090f, 1f));
            DrawPolyline(new[] { pts[0], pts[1], pts[2] }, new Color(Gold, 0.45f), 1f, true);
        }
    }
}
