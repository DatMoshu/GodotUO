// SPDX-License-Identifier: BSD-2-Clause
// GUO addition, not a port: upstream ClassicUO's pregame is 2D gumps only.

using System;
using System.Collections.Generic;
using Godot;
using GUO.Game.Scenes;
using GUO.Input;
using GUO.Input.Gamepad;
using GUO.Input.Touch;

namespace GUO.Pregame3D;

/// <summary>
/// The Pregame3D node (docs/ui/pregame_3d.md): a low-resolution SubViewport
/// holding the chest diorama, upscaled nearest with a 15-bit dither pass, the
/// 2D overlay above it, the pad focus and the on-screen keyboard. One
/// <see cref="Stage"/> at a time drives the real login through
/// <see cref="LoginScene"/>, following its <c>CurrentLoginStep</c>.
/// </summary>
/// <remarks>
/// It exists while the client is on the login scene with the 3D pregame on
/// (<see cref="Pregame3DSettings"/>): <see cref="Owns"/>, called from the
/// marked hook in LoginScene, creates it, and it frees itself when the scene
/// changes (into the world). Input reaches it from two marked hooks:
/// GamepadInput (pad, before the walk/click mapping) and GameController
/// (keys and pointer), as WindowMenu takes the D-pad while it is open.
/// </remarks>
internal sealed partial class PregameDiorama : Node
{
    public static PregameDiorama Instance { get; private set; }

    /// <summary>
    /// The hook in LoginScene: whether the 3D pregame owns <paramref name="step"/>
    /// (no classic gump for it). Brings the diorama up the first time.
    /// </summary>
    public static bool Owns(LoginSteps step)
    {
        if (!Pregame3DSettings.Enabled || Client.Game == null)
        {
            return false;
        }

        if (Instance == null || !IsInstanceValid(Instance) || Instance.IsQueuedForDeletion())
        {
            Instance = new PregameDiorama { Name = "Pregame3D" };
            Client.Game.CallDeferred(Node.MethodName.AddChild, Instance);
        }

        return true;
    }

    /// <summary>Whether the diorama is up and takes input.</summary>
    public static bool Active => Instance != null && IsInstanceValid(Instance) && Instance._built && !Instance.IsQueuedForDeletion();

    public DioramaScene Scene { get; private set; }
    public PadFocus Focus { get; } = new();
    public OnScreenKeyboard Keyboard { get; private set; }
    public LoginScene Login => Client.Game?.GetScene<LoginScene>();
    public Stage Stage => _stage;
    public Control OverlayRoot => _overlay;

    // The fixed hotspots of the login step.
    public Hotspot FieldAccount, FieldPassword, LoginButton, Shield, Credits;
    public readonly Hotspot[] Studs = new Hotspot[3];

    private readonly List<Hotspot> _pickable = new();
    private SubViewport _viewport;
    private CanvasLayer _layer;
    private TextureRect _screen;
    private ShaderMaterial _post;
    private Control _overlay;
    private Label _hint;
    private PanelContainer _hintBand;
    private PanelContainer _modal;
    private Action<bool> _modalAnswer;
    private bool _modalConfirm;
    private Stage _stage;
    private LoginSteps? _step;
    private bool _built;
    private Vector2I _windowSize;
    private int _scale = 2;
    private int _uiScale = 1;
    private Vector2 _pointer = new(-1, -1);
    private Tween _cameraTween;
    private Vector3 _cameraLook;
    private double _time;
    private readonly bool[] _stick = new bool[4];

    /// <summary>The internal resolution's target height; vertex snapping keys off the result.</summary>
    public const float InternalLines = 400f;

    public override void _Ready()
    {
        _viewport = new SubViewport
        {
            Name = "PsxViewport",
            OwnWorld3D = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            Msaa3D = Viewport.Msaa.Disabled,
            Scaling3DMode = Viewport.Scaling3DModeEnum.Bilinear,
            TextureMipmapBias = 0f,
            CanvasItemDefaultTextureFilter = Viewport.DefaultCanvasItemTextureFilter.Nearest,
            Size = new Vector2I(640, 400),
        };
        AddChild(_viewport);

        _layer = new CanvasLayer { Name = "Pregame3DLayer", Layer = 70 };
        AddChild(_layer);

        _screen = new TextureRect
        {
            Name = "Screen",
            Texture = _viewport.GetTexture(),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            TextureFilter = CanvasItem.TextureFilterEnum.Nearest,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };

        var post = ResourceLoader.Exists(DioramaScene.AssetDir + "shaders/psx_post.gdshader") ? GD.Load<Shader>(DioramaScene.AssetDir + "shaders/psx_post.gdshader") : null;

        if (post != null)
        {
            _post = new ShaderMaterial { Shader = post };
            _screen.Material = _post;
        }

        _layer.AddChild(_screen);

        _overlay = new Control
        {
            Name = "Overlay",
            MouseFilter = Control.MouseFilterEnum.Ignore,
            TextureFilter = CanvasItem.TextureFilterEnum.Nearest,
            Theme = UoTheme.Theme,
        };
        _layer.AddChild(_overlay);

        Scene = new DioramaScene();
        _viewport.AddChild(Scene.Root);
        Scene.Build();
        BuildHotspots();
        BuildAnchors();
        BuildOverlay();
        Resize();
        Scene.SetLidOpen(0f);
        _cameraLook = Scene.Layout.CameraLookAt;
        _built = true;

        GD.Print($"[GUO] pregame3d: up (internal {_viewport.Size.X}x{_viewport.Size.Y}, x{_scale}; layout {(Scene.Layout.FromFile ? "from layout.json" : "built in")})");

        if (Pregame3DSettings.Probe)
        {
            Pregame3DProbe.Start();
        }
    }

    public override void _ExitTree()
    {
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
        Resize();

        if (_step != login.CurrentLoginStep)
        {
            _step = login.CurrentLoginStep;
            SwitchStage(_step.Value);
        }

        Scene.Update(_time);
        PlaceAnchored();
        _stage?.Update(delta);
        Focus.Update(Dispatch);
        Hover();
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

        string text = Keyboard.IsOpen ? "" : _modal != null ? (_modalConfirm ? "A  Yes     B  No" : "A  OK") : _stage?.Hints ?? "";
        _hint.Text = text;
        _hintBand.Visible = text.Length > 0;
    }

    // --- input ----------------------------------------------------------------------

    /// <summary>
    /// The hook in GamepadInput: while the diorama is up, the D-pad, the left
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

    /// <summary>The hook in GameController: keys and pointer buttons while the diorama is up.</summary>
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

    /// <summary>What the pointer is over: an overlay row first, then the nearest 3D hotspot.</summary>
    public IFocusable HitAt(Vector2 window)
    {
        if (_stage != null)
        {
            foreach (OverlayItem item in _stage.OverlayItems)
            {
                if (item.CanFocus && Overlay.Hit(item.Control, window))
                {
                    return item;
                }
            }
        }

        Rect2 rect = _screen.GetGlobalRect();

        if (!rect.HasPoint(window) || Scene.Camera == null)
        {
            return null;
        }

        Vector2 vp = (window - rect.Position) / rect.Size * new Vector2(_viewport.Size.X, _viewport.Size.Y);
        Vector3 origin = Scene.Camera.ProjectRayOrigin(vp);
        Vector3 dir = Scene.Camera.ProjectRayNormal(vp);
        Hotspot best = null;
        float bestT = float.MaxValue;

        foreach (Hotspot h in _pickable)
        {
            if (!IsInstanceValid(h))
            {
                continue;
            }

            float? t = h.Pick(origin, dir);

            if (t.HasValue && t.Value < bestT)
            {
                bestT = t.Value;
                best = h;
            }
        }

        return best;
    }

    public void AddPickable(Hotspot h) => _pickable.Add(h);

    public void RemovePickable(Hotspot h) => _pickable.Remove(h);

    // --- layout of the screen -------------------------------------------------------

    private void Resize()
    {
        Vector2I size = (Vector2I) GetViewport().GetVisibleRect().Size;

        if (size == _windowSize || size.X <= 0 || size.Y <= 0)
        {
            return;
        }

        _windowSize = size;

        // Whole-number upscale, about 400 lines inside: 1280x800 is 640x400
        // shown 2x (the owner's call); other sizes keep the window's aspect.
        _scale = Math.Max(1, (int) Math.Round(size.Y / InternalLines));
        var inner = new Vector2I(Math.Max(1, size.X / _scale), Math.Max(1, size.Y / _scale));
        _viewport.Size = inner;
        Vector2 shown = new Vector2(inner.X, inner.Y) * _scale;
        _screen.Position = (new Vector2(size.X, size.Y) - shown) / 2f;
        _screen.Size = shown;
        _post?.SetShaderParameter("source_size", new Vector2(inner.X, inner.Y));
        Scene?.SetSnapResolution(new Vector2(inner.X, inner.Y));

        // The 2D layer: a whole-number scale, at least ~480x360 of room.
        int ui = Math.Max(1, Math.Min(size.X / 480, size.Y / 360));
        _uiScale = ui;
        _overlay.Scale = new Vector2(ui, ui);
        _overlay.Position = Vector2.Zero;
        _overlay.Size = new Vector2(size.X, size.Y) / ui;

        GD.Print($"[GUO] pregame3d: window {size.X}x{size.Y}, internal {inner.X}x{inner.Y} x{_scale}, overlay x{ui}");

        // The same step, framed again for the new shape.
        if (_targetPose.HasValue && Scene != null)
        {
            CameraTo((_framePose.pos, _framePose.look, FitFov(_framePose, _frameSafe)), 0.01);
        }
    }

    // --- the fixed hotspots -----------------------------------------------------------

    private void BuildHotspots()
    {
        Node3D root = Scene.Root;

        FieldAccount = WrapInPlace(Scene.FieldAccount, "FieldAccount");
        FieldPassword = WrapInPlace(Scene.FieldPassword, "FieldPassword");
        LoginButton = WrapInPlace(Scene.LoginButton, "LoginButton");
        Shield = WrapInPlace(Scene.Shield, "Shield");
        Credits = WrapInPlace(Scene.CreditsPlaque, "Credits");
        FieldAccount.LiftHeight = FieldPassword.LiftHeight = 0.015f;

        for (int i = 0; i < Studs.Length; i++)
        {
            Placement slot = i < Scene.Layout.StudSlots.Count ? Scene.Layout.StudSlots[i] : new Placement(new Vector3(-0.6f + 0.5f * i, 0.3f, 0.63f), new Vector3(90f, 0f, 0f));
            var holder = new Node3D { Name = "Stud" + i, Transform = slot.Transform };
            Node3D off = Scene.Stud(false);
            off.Name = "StudOff";
            Node3D on = Scene.Stud(true);
            on.Name = "StudOn";
            holder.AddChild(off);
            holder.AddChild(on);
            root.AddChild(holder);
            Studs[i] = Hotspot.Wrap(holder, "Stud" + i, root);
            Studs[i].LiftHeight = 0.02f;
            AddPickable(Studs[i]);
        }
    }

    /// <summary>The "ui_anchors" objects that are hotspots (the Quit shield, the Credits plaque).</summary>
    private void BuildAnchors()
    {
        foreach (KeyValuePair<string, UiAnchor> kv in Scene.Layout.UiAnchors)
        {
            (string _, string node, string _) = DioramaLayout.ParseKey(kv.Key);

            foreach (Hotspot h in new[] { Shield, Credits, LoginButton })
            {
                if (h != null && h.GetChildCount() > 0 && h.GetChild(0) is Node3D visual && (visual.Name == node || visual.Name.ToString().StartsWith(node + "_", StringComparison.Ordinal)))
                {
                    _anchored.Add((h, visual, kv.Value));
                }
            }
        }

        if (_anchored.Count > 0)
        {
            GD.Print($"[GUO] pregame3d: {_anchored.Count} object(s) shown as UI (ui_anchors)");
        }
    }

    /// <summary>
    /// Each frame: the anchored objects parallel to the camera's image plane
    /// (its basis copied, no roll), their screen box in their corner.
    /// </summary>
    private void PlaceAnchored()
    {
        if (_anchored.Count == 0)
        {
            return;
        }

        Camera3D cam = Scene.Camera;
        Transform3D ct = cam.GlobalTransform;
        Basis cb = ct.Basis.Orthonormalized();
        float aspect = _viewport.Size.X / (float) Math.Max(1, _viewport.Size.Y);

        foreach ((Hotspot h, Node3D visual, UiAnchor a) in _anchored)
        {
            if (!h.Visible)
            {
                continue;
            }

            // The visual's own box (its scale left out: the anchor sets it).
            Aabb local = Hotspot.MeshBounds(visual, Transform3D.Identity);

            if (local.Size.Y <= 0f)
            {
                continue;
            }

            float halfH = a.DepthM * Mathf.Tan(Mathf.DegToRad(cam.Fov) / 2f);
            float halfW = halfH * aspect;
            float k = a.HeightFrac * 2f * halfH / local.Size.Y;
            float w = local.Size.X * k, hgt = local.Size.Y * k;
            float mx = a.MarginPx.X / a.ReferenceHeight * 2f * halfH;
            float my = a.MarginPx.Y / a.ReferenceHeight * 2f * halfH;
            bool left = a.Corner.EndsWith("left", StringComparison.OrdinalIgnoreCase);
            bool bottom = a.Corner.StartsWith("bottom", StringComparison.OrdinalIgnoreCase);
            float cx = left ? -halfW + mx + w / 2f : halfW - mx - w / 2f;
            float cy = bottom ? -halfH + my + hgt / 2f : halfH - my - hgt / 2f;
            Vector3 centre = local.GetCenter() * k;

            // The box's centre at (cx, cy) on the plane depth_m ahead; the
            // object's front (+Z) toward the camera, as the camera's own +Z.
            Vector3 at = ct.Origin - cb.Z * a.DepthM + cb.X * (cx - centre.X) + cb.Y * (cy - centre.Y) - cb.Z * (-centre.Z);
            var world = new Transform3D(cb.Scaled(new Vector3(k, k, k)), at);
            // In the hotspot's resting space (the scene root's), so its lift and press still show.
            visual.Transform = world;
            h.InvalidateBounds();
        }
    }

    /// <summary>A hotspot around a node that stays where it is in the tree (a field on the plaque).</summary>
    private Hotspot WrapInPlace(Node3D visual, string id)
    {
        Node3D parent = visual.GetParent() as Node3D ?? Scene.Root;
        Hotspot h = Hotspot.Wrap(visual, id, parent);
        AddPickable(h);
        return h;
    }

    /// <summary>Shows a stud lit or not.</summary>
    public static void SetStud(Hotspot stud, bool on)
    {
        if (stud.FindChild("StudOn", true, false) is Node3D lit)
        {
            lit.Visible = on;
        }

        if (stud.FindChild("StudOff", true, false) is Node3D unlit)
        {
            unlit.Visible = !on;
        }
    }

    // --- 3D text ----------------------------------------------------------------------

    /// <summary>
    /// A Label3D laid on the largest face of <paramref name="anchor"/> that
    /// faces the camera, its lines along the longest side, about
    /// <paramref name="heightFraction"/> of the face's height tall. Pixel font,
    /// unfiltered, unshaded.
    /// </summary>
    public Label3D TextOn(Node3D anchor, string text, float heightFraction = 0.6f, Color? color = null)
    {
        // A hotspot sits at the origin, unrotated: its visual carries the
        // object's own axes, and the text goes on that.
        if (anchor is Hotspot && anchor.GetChildCount() > 0 && anchor.GetChild(0) is Node3D visual)
        {
            anchor = visual;
        }

        Aabb b = Hotspot.MeshBounds(anchor, Transform3D.Identity);
        Label3D label = NewLabel(text, color ?? new Color("1c1812"));
        anchor.AddChild(label);

        if (b.Size == Vector3.Zero)
        {
            label.Billboard = BaseMaterial3D.BillboardModeEnum.Enabled;
            label.PixelSize = 0.008f;
            return label;
        }

        Basis gb = anchor.GlobalBasis;
        Vector3 s = b.Size;
        Vector3[] axes = { Vector3.Right, Vector3.Up, Vector3.Back };
        float[] world = { (gb * axes[0]).Length() * s.X, (gb * axes[1]).Length() * s.Y, (gb * axes[2]).Length() * s.Z };
        int thin = 0;

        for (int i = 1; i < 3; i++)
        {
            if (world[i] < world[thin]) thin = i;
        }

        // Of the face's two sides, the lines run along the one nearest the
        // camera's horizontal (a field's length, a shield's width).
        int a = (thin + 1) % 3, c = (thin + 2) % 3;
        Vector3 camRight = (_targetPose is { } tp ? new Transform3D(Basis.Identity, tp.pos).LookingAt(tp.look, Vector3.Up).Basis.X : Scene.Camera.GlobalBasis.X);
        bool aAcross = Math.Abs((gb * axes[a]).Normalized().Dot(camRight)) >= Math.Abs((gb * axes[c]).Normalized().Dot(camRight));
        int mid = aAcross ? c : a;
        Vector3 centre = anchor.GlobalTransform * b.GetCenter();
        Vector3 n = (gb * axes[thin]).Normalized();
        Vector3 toCamera = Scene.Camera.GlobalPosition - centre;

        if (n.Dot(toCamera) < 0f)
        {
            n = -n;
        }

        Vector3 up = (gb * axes[mid]).Normalized();
        Vector3 camUp = Scene.Camera.GlobalBasis.Y;

        if (up.Dot(camUp) < 0f)
        {
            up = -up;
        }

        Vector3 right = up.Cross(n).Normalized();
        up = n.Cross(right).Normalized();
        label.GlobalTransform = new Transform3D(new Basis(right, up, n), centre + n * (world[thin] * 0.5f + 0.006f));
        float lineHeight = Math.Max(1f, UoTheme.Font.GetHeight(UoTheme.FontSize));

        // Whole font pixels per screen pixel where it is seen from: never
        // smaller than one, so the pixel font stays legible and crisp.
        float unit = PixelUnit(centre);
        float fit = world[mid] * heightFraction / lineHeight;
        label.PixelSize = Math.Max(1f, Mathf.Floor(fit / unit)) * unit;

        return label;
    }

    /// <summary>
    /// A Label3D that always faces the camera, above <paramref name="anchor"/>'s
    /// top, <paramref name="scale"/> font pixels to a screen pixel where the
    /// step's camera sees it.
    /// </summary>
    public Label3D TextAbove(Node3D anchor, string text, int scale = 1, Color? color = null, float gap = 0.05f)
    {
        Aabb b = Hotspot.MeshBounds(anchor, Transform3D.Identity);
        Label3D label = NewLabel(text, color ?? new Color("eeeade"));
        label.Billboard = BaseMaterial3D.BillboardModeEnum.Enabled;
        label.OutlineSize = 4;
        label.OutlineModulate = new Color("1c1812");
        anchor.AddChild(label);
        Vector3 top = anchor.GlobalTransform * (b.GetCenter() + new Vector3(0f, b.Size.Y * 0.5f, 0f));
        label.GlobalPosition = top + Vector3.Up * gap;
        label.PixelSize = PixelUnit(label.GlobalPosition) * scale;

        return label;
    }

    /// <summary>A camera-facing Label3D just in front of <paramref name="anchor"/> (a scroll's name).</summary>
    public Label3D TextFront(Node3D anchor, string text, int scale = 1, Color? color = null)
    {
        Aabb b = Hotspot.MeshBounds(anchor, Transform3D.Identity);
        Label3D label = NewLabel(text, color ?? new Color("1c1812"));
        label.Billboard = BaseMaterial3D.BillboardModeEnum.Enabled;
        label.OutlineSize = 4;
        label.OutlineModulate = new Color("eeeade");
        anchor.AddChild(label);
        Vector3 centre = anchor.GlobalTransform * b.GetCenter();
        Vector3 eye = _targetPose?.pos ?? Scene.Camera.GlobalPosition;
        float radius = (anchor.GlobalBasis * b.Size).Length() * 0.25f;
        label.GlobalPosition = centre + (eye - centre).Normalized() * (radius + 0.02f);
        label.PixelSize = PixelUnit(label.GlobalPosition) * scale;

        return label;
    }

    /// <summary>
    /// World metres per internal-resolution pixel at <paramref name="at"/>,
    /// seen from where the camera is going (the current step's pose).
    /// </summary>
    public float PixelUnit(Vector3 at)
    {
        Vector3 eye = _targetPose?.pos ?? Scene.Camera.GlobalPosition;
        float fov = _targetPose?.fov ?? Scene.Camera.Fov;
        float d = Math.Max(0.1f, eye.DistanceTo(at));

        return 2f * d * Mathf.Tan(Mathf.DegToRad(fov) / 2f) / Math.Max(1, _viewport.Size.Y);
    }

    private (Vector3 pos, Vector3 look, float fov)? _targetPose;

    /// <summary>The last framing asked for, unfitted, to fit again on a resize.</summary>
    private (Vector3 pos, Vector3 look, float fov) _framePose;
    private Node3D[] _frameSafe = Array.Empty<Node3D>();

    /// <summary>Objects shown as UI ("ui_anchors"): their hotspot, the visual placed, its anchor.</summary>
    private readonly List<(Hotspot hotspot, Node3D visual, UiAnchor anchor)> _anchored = new();

    private static Label3D NewLabel(string text, Color color) => new()
    {
        Text = text,
        Font = UoTheme.Font,
        FontSize = UoTheme.FontSize,
        Modulate = color,
        OutlineSize = 0,
        Shaded = false,
        DoubleSided = false,
        TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,
        AlphaCut = Label3D.AlphaCutMode.Discard,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    // --- camera -----------------------------------------------------------------------

    /// <summary>The login step's view: the layout's "step_cameras.login", else its camera.</summary>
    public (Vector3 pos, Vector3 look, float fov) LoginPose =>
        Scene.Layout.StepCamera("login") is { } c ? (c.position, c.lookAt, c.fovDeg) : (Scene.Layout.CameraPosition, Scene.Layout.CameraLookAt, Scene.Layout.FovDeg);

    /// <summary>A step's view from the layout ("servers", "characters"), else leaning into the chest.</summary>
    public (Vector3 pos, Vector3 look, float fov) StepPose(string step, float sideways = 0f)
    {
        (Vector3 pos, Vector3 look, float fov) pose;

        if (Scene.Layout.StepCamera(step) is { } c)
        {
            pose = (c.position, c.lookAt, c.fovDeg);
        }
        else
        {
            Vector3 look = Scene.ChestInterior.GlobalPosition;
            (Vector3 pos, Vector3 l, float fov) login = LoginPose;
            pose = (look + (login.pos - login.l) * 0.78f + Vector3.Up * 0.15f, look, login.fov);
        }

        if (sideways != 0f)
        {
            Vector3 right = (pose.look - pose.pos).Cross(Vector3.Up).Normalized();
            pose.pos += right * sideways;
            pose.look += right * sideways;
        }

        return pose;
    }

    /// <summary>
    /// Moves the camera to a step's pose, its field of view widened when the
    /// window is narrower than the layout's so that <paramref name="safe"/>
    /// (the step's content: the chest and plaque, the scrolls, the plinths)
    /// still fits across with a margin. Wider windows keep the vertical FOV.
    /// </summary>
    public void Frame((Vector3 pos, Vector3 look, float fov) pose, IEnumerable<Node3D> safe, double seconds = 0.8)
    {
        _framePose = pose;
        _frameSafe = safe == null ? Array.Empty<Node3D>() : new List<Node3D>(safe).ToArray();
        CameraTo((pose.pos, pose.look, FitFov(pose, _frameSafe)), seconds);
    }

    /// <summary>The vertical FOV at which every corner of <paramref name="safe"/>'s bounds fits the width, never less than the pose's own.</summary>
    private float FitFov((Vector3 pos, Vector3 look, float fov) pose, Node3D[] safe)
    {
        const float margin = 1.08f;
        float aspect = _viewport.Size.X / (float) Math.Max(1, _viewport.Size.Y);
        float need = Mathf.Tan(Mathf.DegToRad(pose.fov) / 2f);
        Transform3D inv = new Transform3D(Basis.Identity, pose.pos).LookingAt(pose.look, Vector3.Up).AffineInverse();

        foreach (Node3D n in safe)
        {
            if (n == null || !IsInstanceValid(n) || !n.IsInsideTree())
            {
                continue;
            }

            Aabb box = Hotspot.MeshBounds(n, n.GlobalTransform);

            for (int i = 0; i < 8; i++)
            {
                Vector3 p = inv * box.GetEndpoint(i);
                float z = -p.Z;

                if (z > 0.05f)
                {
                    need = Math.Max(need, Math.Abs(p.X) / z / aspect * margin);
                }
            }
        }

        return Math.Min(100f, Mathf.RadToDeg(2f * Mathf.Atan(need)));
    }

    public void CameraTo((Vector3 pos, Vector3 look, float fov) pose, double seconds = 0.8)
    {
        _targetPose = pose;
        _cameraTween?.Kill();
        Vector3 fromPos = Scene.Camera.Position, fromLook = _cameraLook;
        float fromFov = Scene.Camera.Fov;
        _cameraTween = CreateTween();
        _cameraTween.TweenMethod(Callable.From<float>(t =>
        {
            float e = t * t * (3f - 2f * t);
            _cameraLook = fromLook.Lerp(pose.look, e);
            Scene.Camera.Fov = Mathf.Lerp(fromFov, pose.fov, e);
            Scene.Camera.Transform = new Transform3D(Basis.Identity, fromPos.Lerp(pose.pos, e)).LookingAt(_cameraLook, Vector3.Up);
        }), 0f, 1f, Math.Max(0.01, seconds));
    }

    private Tween _lidTween;
    private float _lid;

    public void LidTo(float open, double seconds)
    {
        _lidTween?.Kill();
        _lidTween = CreateTween();
        _lidTween.TweenMethod(Callable.From<float>(v =>
        {
            _lid = v;
            Scene.SetLidOpen(v);
        }), _lid, open, seconds).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
    }

    public float Lid => _lid;

    // --- overlay cards ----------------------------------------------------------------

    private void BuildOverlay()
    {
        _hintBand = Overlay.BandPanel();
        _hintBand.SetAnchorsPreset(Control.LayoutPreset.BottomWide);
        _hintBand.GrowVertical = Control.GrowDirection.Begin;
        _hint = Overlay.Text("", UoTheme.Cream);
        _hint.HorizontalAlignment = HorizontalAlignment.Center;
        _hintBand.AddChild(_hint);
        _overlay.AddChild(_hintBand);

        Keyboard = new OnScreenKeyboard();
        _overlay.AddChild(Keyboard.Root);
        Keyboard.Root.SetAnchorsPreset(Control.LayoutPreset.CenterBottom);
        Keyboard.Root.GrowHorizontal = Control.GrowDirection.Both;
        Keyboard.Root.GrowVertical = Control.GrowDirection.Begin;
        Keyboard.Root.OffsetBottom = -28;
    }

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
        _modal.SetAnchorsPreset(Control.LayoutPreset.Center);
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
        + "This front end, its models and shaders were made for GUO.\n\n"
        + "This project distributes no copyrighted game assets: it reads your own Ultima Online Classic install.\n"
        + "Ultima Online is a trademark of Electronic Arts Inc.";

    // --- linking slots by where they are on screen --------------------------------------

    /// <summary>Where a hotspot shows on the internal screen.</summary>
    public Vector2 ScreenOf(Hotspot h) => Scene.Camera.UnprojectPosition(h.GlobalTransform * h.Bounds.GetCenter());

    /// <summary>
    /// Neighbours for a set of slot hotspots from where the layout put them:
    /// each way, the nearest one in that direction (within 45 degrees).
    /// Slots are the layout's to place, so their graph follows it.
    /// </summary>
    public void LinkBySpace(IList<Hotspot> items)
    {
        var at = new Vector2[items.Count];

        for (int i = 0; i < items.Count; i++)
        {
            at[i] = ScreenOf(items[i]);
        }

        for (int i = 0; i < items.Count; i++)
        {
            items[i].Up = Nearest(i, new Vector2(0, -1));
            items[i].Down = Nearest(i, new Vector2(0, 1));
            items[i].Left = Nearest(i, new Vector2(-1, 0));
            items[i].Right = Nearest(i, new Vector2(1, 0));
        }

        IFocusable Nearest(int from, Vector2 dir)
        {
            IFocusable best = null;
            float bestScore = float.MaxValue;

            for (int j = 0; j < items.Count; j++)
            {
                if (j == from)
                {
                    continue;
                }

                Vector2 d = at[j] - at[from];
                float along = d.Dot(dir);
                float across = Math.Abs(d.Dot(new Vector2(dir.Y, -dir.X)));

                if (along <= 1f || across > along)
                {
                    continue;
                }

                float score = along + across * 2f;

                if (score < bestScore)
                {
                    bestScore = score;
                    best = items[j];
                }
            }

            return best;
        }
    }
}
