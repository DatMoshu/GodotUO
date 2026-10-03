// SPDX-License-Identifier: BSD-2-Clause
// GUO addition, not a port: upstream ClassicUO's pregame is its 2D gumps.

using System;
using System.Collections.Generic;
using Godot;
using GUO.Game.Scenes;
using GUO.Input;
using GUO.Input.Gamepad;
using GUO.Input.Touch;

namespace GUO.Pregame3D;

/// <summary>
/// The pad-first pregame (docs/ui/pregame_3d.md): the classic login screen,
/// alive. The client's own login painting is the backdrop (read at runtime,
/// xBR-upscaled, its stone wall mirrored out to fill a wide screen, lit by a
/// flickering warm light, drifting slowly), its own gump pieces stand over it
/// where the classic gump puts them, and a 2D overlay carries the cards, the
/// hints and the keyboard. One <see cref="Stage"/> at a time drives the real
/// login through <see cref="LoginScene"/>, following its CurrentLoginStep.
/// </summary>
/// <remarks>
/// It exists while the client is on the login scene with the pregame on
/// (<see cref="Pregame3DSettings"/>): <see cref="Owns"/>, called from the
/// marked hook in LoginScene, creates it, and it frees itself when the scene
/// changes (into the world). Input reaches it from two marked hooks:
/// GamepadInput (pad, before the walk/click mapping) and GameController
/// (keys and pointer), as WindowMenu takes the D-pad while it is open.
/// </remarks>
internal sealed partial class PregameScreen : Node
{
    public static PregameScreen Instance { get; private set; }

    private const string PaintingShaderPath = "res://assets/pregame/shaders/painting.gdshader";

    /// <summary>
    /// The hook in LoginScene: whether the pregame owns <paramref name="step"/>
    /// (no classic gump for it). Brings the screen up the first time.
    /// </summary>
    public static bool Owns(LoginSteps step)
    {
        if (!Pregame3DSettings.Enabled || Client.Game == null)
        {
            return false;
        }

        if (Instance == null || !IsInstanceValid(Instance) || Instance.IsQueuedForDeletion())
        {
            Instance = new PregameScreen { Name = "Pregame" };
            Client.Game.CallDeferred(Node.MethodName.AddChild, Instance);
        }

        return true;
    }

    /// <summary>The Deck panel's own size.</summary>
    private static readonly Vector2I DeckPanel = new(1280, 800);

    private static bool _deckWindow;

    /// <summary>
    /// The window for the pregame, from LoginScene.Load in place of its
    /// 640x480: on the Steam Deck a borderless window at the panel's 1280x800
    /// (Fullscreen under gamescope takes its 1920x1080 canvas, scaled onto the
    /// panel, and corners fall off); an exported Linux build elsewhere goes
    /// fullscreen; a desktop run keeps the window it has.
    /// </summary>
    public static void PrepareWindow()
    {
        DisplayServer.WindowSetMinSize(Vector2I.Zero);

        if (NativeKeyboard.IsSteamDeck)
        {
            _deckWindow = true;
            ApplyDeckWindow();
            GD.Print($"[GUO] pregame3d: Steam Deck detected (Game Mode {NativeKeyboard.IsGameMode}): borderless {DeckPanel.X}x{DeckPanel.Y} window at 0,0; "
                + $"window is {DisplayServer.WindowGetSize().X}x{DisplayServer.WindowGetSize().Y} after the request");
            return;
        }

        if (OperatingSystem.IsLinux() && OS.HasFeature("template")
            && DisplayServer.WindowGetMode() is not (DisplayServer.WindowMode.Fullscreen or DisplayServer.WindowMode.ExclusiveFullscreen))
        {
            DisplayServer.WindowSetMode(DisplayServer.WindowMode.Fullscreen);
            GD.Print("[GUO] pregame3d: fullscreen (exported Linux build)");
        }
    }

    private static void ApplyDeckWindow()
    {
        DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
        DisplayServer.WindowSetFlag(DisplayServer.WindowFlags.Borderless, true);
        DisplayServer.WindowSetMinSize(DeckPanel);
        DisplayServer.WindowSetPosition(Vector2I.Zero);
        DisplayServer.WindowSetSize(DeckPanel);
    }

    /// <summary>Whether the pregame is up and takes input.</summary>
    public static bool Active => Instance != null && IsInstanceValid(Instance) && Instance._built && !Instance.IsQueuedForDeletion();

    public PadFocus Focus { get; } = new();
    public OnScreenKeyboard Keyboard { get; private set; }
    public LoginScene Login => Client.Game?.GetScene<LoginScene>();
    public Stage Stage => _stage;
    public Control OverlayRoot => _overlay;
    public Painting Art { get; private set; }

    /// <summary>Whether the painting has been composed from the client's gumps.</summary>
    public bool Painted => _painted;

    /// <summary>The layer in the painting's own 640x480 pixels, for the gump pieces that stand on it.</summary>
    public Control Props => _props;

    /// <summary>The overlay's whole-number scale (screen pixels to one overlay pixel).</summary>
    public int UiScale => _uiScale;

    private CanvasLayer _layer;
    private ColorRect _backdrop;
    private ShaderMaterial _paint;
    private Control _props;
    private Control _overlay;
    private Label _hint;
    private PanelContainer _hintBand;
    private PanelContainer _modal;
    private Action<bool> _modalAnswer;
    private bool _modalConfirm;
    private Stage _stage;
    private LoginSteps? _step;
    private bool _built;
    private bool _painted;
    private Vector2I _windowSize;
    private int _uiScale = 1;
    private float _paintScale = 1f;
    private Vector2 _paintOrigin;
    private Vector2 _pointer = new(-1, -1);
    private double _time;
    private float _dim = 1f;
    private Tween _dimTween;
    private int _frames;
    private bool _framesReady;
    private bool _mapStarted;
    private bool _cardWas;
    private Label _loading;
    private readonly List<ShaderMaterial> _propMaterials = new();
    private readonly bool[] _stick = new bool[4];

    public override void _Ready()
    {
        Art = new Painting();
        _layer = new CanvasLayer { Name = "PregameLayer", Layer = 70 };
        AddChild(_layer);

        _paint = ResourceLoader.Exists(PaintingShaderPath) ? new ShaderMaterial { Shader = GD.Load<Shader>(PaintingShaderPath) } : null;
        _backdrop = new ColorRect { Name = "Painting", Color = Colors.Black, MouseFilter = Control.MouseFilterEnum.Ignore, Material = _paint };
        _layer.AddChild(_backdrop);

        _props = new Control { Name = "Props", MouseFilter = Control.MouseFilterEnum.Ignore, TextureFilter = CanvasItem.TextureFilterEnum.Nearest, Theme = UoTheme.Theme };
        _layer.AddChild(_props);

        _overlay = new Control
        {
            Name = "Overlay",
            MouseFilter = Control.MouseFilterEnum.Ignore,
            TextureFilter = CanvasItem.TextureFilterEnum.Nearest,
            Theme = UoTheme.Theme,
        };
        _layer.AddChild(_overlay);

        // Every size change re-lays everything (also polled each frame).
        GetViewport().SizeChanged += Resize;

        BuildOverlay();
        Resize();
        PregameAssets.Start(Art);
        _built = true;

        GD.Print($"[GUO] pregame3d: up ({(Art.Modern ? "the 7.0.64+ login painting 0x014E" : "the older login art")}, from the client's files)");

        if (Pregame3DSettings.Probe)
        {
            Pregame3DProbe.Start();
        }
    }

    public override void _ExitTree()
    {
        GetViewport().SizeChanged -= Resize;

        if (Instance == this)
        {
            Instance = null;
        }
    }

    public override void _Process(double delta)
    {
        LoginScene login = Login;

        if (login == null || login.IsDestroyed)
        {
            // Into the world (or anywhere else): the classic client takes over.
            _stage?.Exit();
            _stage = null;
            Focus.Clear();
            QueueFree();
            return;
        }

        _time += delta;
        _frames++;
        KeepDeckWindow();
        Resize();
        Paint();

        // No step is built before everything it can show is loaded (a
        // stand-in would show flat boxes, then the art): the painting with a
        // small "Loading" line meanwhile.
        if (!_framesReady)
        {
            _framesReady = PregameAssets.Ready && _painted && Overlay.FramesReady();
            _loading.Text = $"Loading...  {(int) (PregameAssets.Progress * 100)}%";

            if (!_framesReady)
            {
                return;
            }

            _loading.Visible = false;
            GD.Print($"[GUO] pregame3d: shown after {_frames} frames");
        }

        // After the login: the figure's frames, a little each frame; the Home
        // map as soon as the shard has sent its cities.
        if (login.CurrentLoginStep is not (LoginSteps.Main or LoginSteps.Connecting or LoginSteps.VerifyingAccount))
        {
            PregameAssets.StartAfterLogin();
        }

        PregameAssets.Step();

        if (login.Cities != null && !_mapStarted)
        {
            _mapStarted = true;
            CreationStage.PrefetchMap(login.Cities, _overlay.Size);
        }

        // The safety net: any stand-in handed out after all is swapped for the art.
        if (Overlay.StandInsHandedOut && _frames % 15 == 0)
        {
            Overlay.StandInsHandedOut = Overlay.ReplaceStandIns(_overlay) + Overlay.ReplaceStandIns(_props) > 0;
        }

        if (_step != login.CurrentLoginStep)
        {
            _step = login.CurrentLoginStep;
            SwitchStage(_step.Value);
        }

        // The server card closed (its Close button, a press outside, a login): the stage's hints again.
        if (_cardWas != GUO.Input.Touch.Pregame.PregameCard.OnMain)
        {
            _cardWas = GUO.Input.Touch.Pregame.PregameCard.OnMain;
            RefreshHints();
        }

        Animate();
        _stage?.Update(delta);
        Keyboard.PlaceHint();
        Focus.Update(Dispatch);
        Hover();
    }

    /// <summary>
    /// The Deck's window again until it holds: gamescope can map the window
    /// at the project's 1280x720 after the first request. Logged until it
    /// is right, then once more.
    /// </summary>
    private void KeepDeckWindow()
    {
        if (!_deckWindow || _frames % 30 != 0 || _frames > 600)
        {
            return;
        }

        Vector2I size = DisplayServer.WindowGetSize();

        if (size == DeckPanel)
        {
            GD.Print($"[GUO] pregame3d: Deck window holds at {size.X}x{size.Y}");
            _deckWindow = false;
            return;
        }

        ApplyDeckWindow();
        GD.Print($"[GUO] pregame3d: Deck window was {size.X}x{size.Y}; asked for {DeckPanel.X}x{DeckPanel.Y} again, now {DisplayServer.WindowGetSize().X}x{DisplayServer.WindowGetSize().Y}");
    }

    /// <summary>The painting, once its gumps are in the atlas.</summary>
    private void Paint()
    {
        if (_painted || _paint == null || !PregameAssets.Ready)
        {
            return;
        }

        Image image = Art.Compose();

        if (image == null)
        {
            return;
        }

        _paint.SetShaderParameter("painting", ImageTexture.CreateFromImage(image));
        _paint.SetShaderParameter("painting_size", new Vector2(Painting.Width, Painting.Height));
        var lights = new Godot.Collections.Array<Vector4>();

        foreach (Vector4 l in Art.Lights)
        {
            lights.Add(l);
        }

        _paint.SetShaderParameter("lights", lights);
        _painted = true;
    }

    /// <summary>The light's flicker and the slow drift: the painting a little, the pieces on it a little more.</summary>
    private void Animate()
    {
        var drift = new Vector2(Mathf.Sin((float) _time * 0.13f) * 3f, Mathf.Sin((float) _time * 0.09f + 1f) * 2f);
        _paint?.SetShaderParameter("time_s", (float) _time);
        _paint?.SetShaderParameter("drift", drift);
        _props.Position = _paintOrigin + drift * 1.5f;
    }

    /// <summary>The painting (and its pieces) dimmed behind a step's panels; 1 = as lit.</summary>
    public void SetDim(float dim)
    {
        _dimTween?.Kill();
        _dimTween = CreateTween();
        _dimTween.TweenMethod(Callable.From<float>(ApplyDim), _dim, dim, 0.35);
    }

    private void ApplyDim(float dim)
    {
        _dim = dim;
        _paint?.SetShaderParameter("dim", dim);

        foreach (ShaderMaterial m in _propMaterials)
        {
            m.SetShaderParameter("dim", dim);
        }
    }

    /// <summary>A gump piece onto the painting (its position in the painting's pixels).</summary>
    public void AddProp(Control c, ShaderMaterial material)
    {
        _props.AddChild(c);

        if (material != null)
        {
            material.SetShaderParameter("dim", _dim);
            _propMaterials.Add(material);
        }
    }

    public void RemoveProp(Control c, ShaderMaterial material)
    {
        _propMaterials.Remove(material);
        c.QueueFree();
    }

    // --- stages ---------------------------------------------------------------------

    private void SwitchStage(LoginSteps step)
    {
        Stage next = step switch
        {
            LoginSteps.Main => _stage as LoginStage ?? new LoginStage(),
            LoginSteps.ServerSelection => new ServerStage(),
            LoginSteps.CharacterSelection => _stage as CharacterStage ?? new CharacterStage(),
            LoginSteps.CharacterCreation => _stage as CreationStage ?? new CreationStage(),
            _ => _stage as StatusStage ?? new StatusStage(),
        };

        if (!ReferenceEquals(next, _stage))
        {
            CloseModal(false, silent: true);
            Keyboard.Close();
            Focus.Clear();
            _stage?.Exit();
            _stage = next;
            _stage.Attach(this);
            _stage.Enter();
        }
        else
        {
            _stage.StepChanged(step);
        }

        RefreshHints();
        GD.Print($"[GUO] pregame3d: step {step} -> {_stage.GetType().Name}");
    }

    public void RefreshHints()
    {
        if (_hint == null)
        {
            return;
        }

        string text = GUO.Input.Touch.Pregame.PregameCard.OnMain ? "D-pad  Move     A  Press     B  Back / Close"
            : Keyboard.IsOpen ? "" : _modal != null ? (_modalConfirm ? "A  Yes     B  No" : "A  OK") : _stage?.Hints ?? "";
        _hint.Text = text;
        _hintBand.Visible = text.Length > 0;
        bool top = _stage?.HintsAtTop == true && _modal == null;
        _hintBand.SetAnchorsPreset(top ? Control.LayoutPreset.TopWide : Control.LayoutPreset.BottomWide);
        _hintBand.GrowVertical = top ? Control.GrowDirection.End : Control.GrowDirection.Begin;
        float h = _hintBand.GetCombinedMinimumSize().Y;
        _hintBand.OffsetTop = top ? 0 : -h;
        _hintBand.OffsetBottom = top ? h : 0;
    }

    // --- input ----------------------------------------------------------------------

    /// <summary>
    /// The hook in GamepadInput: while the pregame is up, the D-pad, the left
    /// stick, A/B/X/Y, Start and the shoulders are its own. The right stick
    /// still moves the pointer (not taken). An unresolved layout's face
    /// buttons go on to GamepadInput, which says where to choose it.
    /// </summary>
    public static bool HandlePad(InputEvent e)
    {
        if (!Active)
        {
            return false;
        }

        return Instance.Pad(e);
    }

    private bool Pad(InputEvent e)
    {
        switch (e)
        {
            case InputEventJoypadButton b:
                switch (b.ButtonIndex)
                {
                    case JoyButton.DpadUp: Focus.Hold(0, b.Pressed, Dispatch); return true;
                    case JoyButton.DpadDown: Focus.Hold(1, b.Pressed, Dispatch); return true;
                    case JoyButton.DpadLeft: Focus.Hold(2, b.Pressed, Dispatch); return true;
                    case JoyButton.DpadRight: Focus.Hold(3, b.Pressed, Dispatch); return true;
                    case JoyButton.Start: if (b.Pressed) Dispatch(PadCmd.Start); return true;
                    case JoyButton.LeftShoulder: if (b.Pressed) Dispatch(PadCmd.LeftShoulder); return true;
                    case JoyButton.RightShoulder: if (b.Pressed) Dispatch(PadCmd.RightShoulder); return true;
                    case JoyButton.A or JoyButton.B or JoyButton.X or JoyButton.Y:
                        break;
                    default:
                        return false;
                }

                GamepadLayout layout = GamepadInput.Resolve(b.Device);

                if (layout == GamepadLayout.Unknown)
                {
                    return false;
                }

                JoyButton printed = layout == GamepadLayout.Swapped
                    ? b.ButtonIndex switch { JoyButton.A => JoyButton.B, JoyButton.B => JoyButton.A, JoyButton.X => JoyButton.Y, _ => JoyButton.X }
                    : b.ButtonIndex;

                if (b.Pressed)
                {
                    Dispatch(printed switch { JoyButton.A => PadCmd.A, JoyButton.B => PadCmd.B, JoyButton.X => PadCmd.X, _ => PadCmd.Y });
                }

                return true;

            case InputEventJoypadMotion m:
                const float dead = 0.5f;

                switch (m.Axis)
                {
                    case JoyAxis.LeftX:
                        StickHold(2, m.AxisValue < -dead);
                        StickHold(3, m.AxisValue > dead);
                        return true;
                    case JoyAxis.LeftY:
                        StickHold(0, m.AxisValue < -dead);
                        StickHold(1, m.AxisValue > dead);
                        return true;
                }

                if (m.Axis == JoyAxis.RightX && _stage != null && _stage.RightStick(m.AxisValue))
                {
                    return true;
                }

                return false; // the right stick keeps the pointer; triggers are not ours
        }

        return false;
    }

    private void StickHold(int dir, bool on)
    {
        if (_stick[dir] != on)
        {
            _stick[dir] = on;
            Focus.Hold(dir, on, Dispatch);
        }
    }

    /// <summary>The hook in GameController: keys and pointer buttons while the pregame is up.</summary>
    public static bool HandleMainInput(InputEvent e)
    {
        if (!Active)
        {
            return false;
        }

        return Instance.Main(e);
    }

    private bool Main(InputEvent e)
    {
        switch (e)
        {
            case InputEventKey k:
                if (Keyboard.IsOpen)
                {
                    Keyboard.Key(k);
                    RefreshHints();
                    return true;
                }

                if (!k.Pressed)
                {
                    return k.Keycode is Key.Up or Key.Down or Key.Left or Key.Right or Key.Enter or Key.KpEnter or Key.Escape;
                }

                if (_modal == null && _stage != null && _stage.Key(k))
                {
                    return true;
                }

                switch (k.Keycode)
                {
                    case Key.Up: Dispatch(PadCmd.Up); return true;
                    case Key.Down: Dispatch(PadCmd.Down); return true;
                    case Key.Left: Dispatch(PadCmd.Left); return true;
                    case Key.Right: Dispatch(PadCmd.Right); return true;
                    case Key.Tab: Dispatch(k.ShiftPressed ? PadCmd.Up : PadCmd.Down); return true;
                    case Key.Enter or Key.KpEnter: Dispatch(k.CtrlPressed ? PadCmd.Start : PadCmd.A); return true;
                    case Key.Escape: Dispatch(PadCmd.B); return true;
                    case Key.Delete: Dispatch(PadCmd.Y); return true;
                }

                return false;

            case InputEventMouseButton mb:
                if (!mb.Pressed)
                {
                    return true;
                }

                switch (mb.ButtonIndex)
                {
                    case MouseButton.Left:
                        Click(mb.Position);
                        return true;
                    case MouseButton.Right:
                        Dispatch(PadCmd.B);
                        return true;
                    case MouseButton.WheelUp:
                        Dispatch(PadCmd.Up);
                        return true;
                    case MouseButton.WheelDown:
                        Dispatch(PadCmd.Down);
                        return true;
                }

                return true;
        }

        // Pointer motion goes on to the client (its Mouse.Position is the
        // pointer the hover reads, the right stick's included).
        return false;
    }

    /// <summary>One command, to the topmost thing that takes it: a card, the keyboard, the step, the focus.</summary>
    public void Dispatch(PadCmd cmd)
    {
        if (_modal != null)
        {
            if (cmd == PadCmd.A || cmd == PadCmd.Start)
            {
                CloseModal(true);
            }
            else if (cmd == PadCmd.B)
            {
                CloseModal(false);
            }

            return;
        }

        // The server card (the classic Servers/Settings card), open over the pregame: it is
        // Godot controls in its own viewport, so the pad's commands become ui_ actions there.
        if (GUO.Input.Touch.Pregame.PregameCard.OnMain)
        {
            switch (cmd)
            {
                case PadCmd.Up: GUO.Input.Touch.Pregame.PregameCard.PadAction("ui_up"); break;
                case PadCmd.Down: GUO.Input.Touch.Pregame.PregameCard.PadAction("ui_down"); break;
                case PadCmd.Left: GUO.Input.Touch.Pregame.PregameCard.PadAction("ui_left"); break;
                case PadCmd.Right: GUO.Input.Touch.Pregame.PregameCard.PadAction("ui_right"); break;
                case PadCmd.A: GUO.Input.Touch.Pregame.PregameCard.PadAction("ui_accept"); break;
                case PadCmd.B: GUO.Input.Touch.Pregame.PregameCard.PadBack(); break;
            }

            RefreshHints();
            return;
        }

        if (Keyboard.IsOpen)
        {
            Keyboard.Command(cmd);
            RefreshHints();
            return;
        }

        if (_stage != null && _stage.Command(cmd))
        {
            RefreshHints();
            return;
        }

        switch (cmd)
        {
            case PadCmd.Up or PadCmd.Down or PadCmd.Left or PadCmd.Right:
                Focus.Move(cmd);
                break;

            case PadCmd.A:
                Focus.Current?.Press();
                break;
        }

        RefreshHints();
    }

    private void Click(Vector2 window)
    {
        if (_modal != null)
        {
            CloseModal(Overlay.Hit(_modal, window));
            return;
        }

        if (Keyboard.IsOpen)
        {
            if (!Keyboard.Click(window))
            {
                Keyboard.Command(PadCmd.Start);
            }

            RefreshHints();
            return;
        }

        IFocusable hit = HitAt(window);

        if (hit != null)
        {
            Focus.Set(hit);
            hit.Press();
            RefreshHints();
        }
    }

    /// <summary>Pointer hover = focus, only when the pointer moved (a still pointer never steals the pad's focus).</summary>
    private void Hover()
    {
        if (Client.Game == null)
        {
            return;
        }

        float dpi = Client.Game.DpiScale;
        var at = new Vector2(GUO.Input.Mouse.Position.X * dpi, GUO.Input.Mouse.Position.Y * dpi);

        if (at == _pointer)
        {
            return;
        }

        bool first = _pointer.X < 0;
        _pointer = at;

        if (first || _modal != null || Keyboard.IsOpen || InputMode.Current == InputKind.Gamepad && InputMode.PointerHidden)
        {
            return;
        }

        IFocusable hit = HitAt(at);

        if (hit != null && !ReferenceEquals(hit, Focus.Current))
        {
            Focus.Set(hit);
            RefreshHints();
        }
    }

    /// <summary>What the pointer is over: the step's rows, cards and gump pieces.</summary>
    public IFocusable HitAt(Vector2 window)
    {
        if (_stage == null)
        {
            return null;
        }

        IFocusable hit = null;

        foreach (IOverlayFocusable item in _stage.OverlayItems)
        {
            // The last drawn wins (a popover's rows over the card's).
            if (item.CanFocus && Overlay.Hit(item.Control, window))
            {
                hit = item;
            }
        }

        return hit;
    }

    // --- layout of the screen -------------------------------------------------------

    private void Resize()
    {
        // The OS window is the truth. Under gamescope (and a WM-less X server)
        // the root viewport can stay at the project's 1280x720 while the
        // window is 1280x800: the root is told the window's size, so what
        // renders is the whole window, and the layout follows the window only.
        Vector2I size = DisplayServer.WindowGetSize();
        Vector2I root = GetTree().Root.Size;

        if (size.X <= 0 || size.Y <= 0)
        {
            return;
        }

        if (root != size)
        {
            GD.Print($"[GUO] pregame3d: window {size.X}x{size.Y} but root viewport {root.X}x{root.Y}: root resized to the window");
            GetTree().Root.Size = size;
        }

        if (size == _windowSize)
        {
            return;
        }

        _windowSize = size;
        var screen = new Vector2(size.X, size.Y);

        // The painting fitted to the height (or, on a narrow window, the width), centred.
        _paintScale = Math.Min(size.Y / (float) Painting.Height, size.X / (float) Painting.Width);
        _paintOrigin = (screen - new Vector2(Painting.Width, Painting.Height) * _paintScale) / 2f;
        _backdrop.Position = Vector2.Zero;
        _backdrop.Size = screen;
        _paint?.SetShaderParameter("screen_size", screen);
        _paint?.SetShaderParameter("origin", _paintOrigin);
        _paint?.SetShaderParameter("scale", _paintScale);
        _props.Scale = new Vector2(_paintScale, _paintScale);
        _props.Size = new Vector2(Painting.Width, Painting.Height);

        // The 2D layer: a whole-number scale, at least ~480x360 of room.
        int ui = Math.Max(1, Math.Min(size.X / 480, size.Y / 360));
        _uiScale = ui;
        _overlay.Scale = new Vector2(ui, ui);
        _overlay.Position = Vector2.Zero;
        _overlay.Size = screen / ui;

        GD.Print($"[GUO] pregame3d: window {size.X}x{size.Y}, root viewport {GetTree().Root.Size.X}x{GetTree().Root.Size.Y}, painting x{_paintScale:0.###} at {_paintOrigin.X:0},{_paintOrigin.Y:0}, overlay x{ui}");
        _stage?.Resized();
    }

    private void BuildOverlay()
    {
        _hintBand = Overlay.BandPanel();
        _hintBand.SetAnchorsPreset(Control.LayoutPreset.BottomWide);
        _hintBand.GrowVertical = Control.GrowDirection.Begin;
        _hint = Overlay.Text("", UoTheme.Cream, wrap: true);
        _hint.HorizontalAlignment = HorizontalAlignment.Center;
        _hintBand.AddChild(_hint);
        _overlay.AddChild(_hintBand);

        _loading = Overlay.Text("Loading...", UoTheme.Cream);
        _loading.AddThemeConstantOverride("outline_size", 4);
        _loading.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.8f));
        _overlay.AddChild(_loading);
        _loading.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterBottom, Control.LayoutPresetMode.Minsize, 40);
        _loading.GrowHorizontal = Control.GrowDirection.Both;
        _loading.GrowVertical = Control.GrowDirection.Begin;

        Keyboard = new OnScreenKeyboard();
        _overlay.AddChild(Keyboard.Root);
        Keyboard.Attach(_overlay);
        Keyboard.Root.SetAnchorsPreset(Control.LayoutPreset.CenterBottom);
        Keyboard.Root.GrowHorizontal = Control.GrowDirection.Both;
        Keyboard.Root.GrowVertical = Control.GrowDirection.Begin;
        Keyboard.Root.OffsetBottom = -28;
    }

    // --- overlay cards ----------------------------------------------------------------

    /// <summary>A message card with OK (A); <paramref name="ok"/> runs when it closes.</summary>
    public void ShowMessage(string text, Action ok = null, ushort frame = Overlay.Parchment) =>
        OpenModal(text, false, _ => ok?.Invoke(), frame);

    /// <summary>A yes/no card: A yes, B no.</summary>
    public void Confirm(string text, Action yes) => OpenModal(text, true, answer =>
    {
        if (answer)
        {
            yes();
        }
    });

    public bool ModalOpen => _modal != null;

    private void OpenModal(string text, bool confirm, Action<bool> answer, ushort frame = Overlay.Parchment)
    {
        CloseModal(false, silent: true);
        _modal = Overlay.Card(frame);
        VBoxContainer col = Overlay.Column(6);
        Label body = Overlay.Text(text, frame == Overlay.Stone ? UoTheme.Ink : UoTheme.Ink, wrap: true);
        body.CustomMinimumSize = new Vector2(300, 0);
        col.AddChild(body);
        col.AddChild(Overlay.Text(confirm ? "A  Yes     B  No" : "A  OK", UoTheme.Heading));
        _modal.AddChild(col);
        _overlay.AddChild(_modal);
        _modal.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.Center, Control.LayoutPresetMode.Minsize);
        _modal.GrowHorizontal = Control.GrowDirection.Both;
        _modal.GrowVertical = Control.GrowDirection.Both;
        _modalAnswer = answer;
        _modalConfirm = confirm;
        RefreshHints();
    }

    private void CloseModal(bool answer, bool silent = false)
    {
        if (_modal == null)
        {
            return;
        }

        _modal.QueueFree();
        _modal = null;
        Action<bool> a = _modalAnswer;
        _modalAnswer = null;
        RefreshHints();

        if (!silent)
        {
            a?.Invoke(answer);
        }
    }

    public const string CreditsText =
        "GUO: Ultima Online Classic on Godot.\n"
        + "A port of ClassicUO (BSD 2-Clause), lead developer Karasho', and its contributors.\n"
        + "This front end and its shaders were made for GUO; every picture in it is read from your own install.\n\n"
        + "This project distributes no copyrighted game assets: it reads your own Ultima Online Classic install.\n"
        + "Ultima Online is a trademark of Electronic Arts Inc.";
}
