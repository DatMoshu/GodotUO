// SPDX-License-Identifier: BSD-2-Clause
using System;
using Godot;
using GUO.Game;
using GUO.Input.Touch;

namespace GUO.Input.Touch.Modern;

/// <summary>
/// A Modern view of a client gump (ADR-0024): Godot controls on UoTheme in a
/// SubViewport, fitted to the main screen below the top bar, over the
/// command bar, modal while it is open. Subclasses build the controls and
/// read and write the game state; this class shows them and routes touch.
/// </summary>
/// <remarks>
/// PORT DEVIATION (GUO): not in ClassicUO; touch builds only. Input is pushed
/// in by hand, as for the window menu and the slot editor (GameController
/// marks every event handled first): a tap presses what is under the
/// finger, a drag that starts on a slider moves it, and any other drag
/// scrolls the scroll container under the finger.
/// </remarks>
internal abstract partial class ModernGump : Node
{
    /// <summary>The Modern view open now, if any: it takes every event.</summary>
    public static ModernGump Current { get; private set; }

    public static bool IsOpen => Current != null && GodotObject.IsInstanceValid(Current) && Current._open;

    protected readonly World World;

    /// <summary>The widest the card is laid out, in art pixels.</summary>
    protected virtual float MaxArtWidth => 640f;

    /// <summary>The card's frame: UoTheme's stone unless the view is styled after its classic gump.</summary>
    protected virtual StyleBox CardStyle => null;

    // Editors need native mouse selection and wheel scrolling, rather than touch-style taps.
    protected virtual bool DirectMouseInput => false;

    private const int Pad = 12; // client pixels above and below, as the fitted classic gumps

    private SubViewport _viewport;
    private Control _root;
    private CanvasLayer _layer;
    private TextureRect _view;
    private bool _built;
    private bool _open;

    /// <summary>Whether this view is open now.</summary>
    protected bool Shown => _open;
    private float _scale = 1f;
    private Rect2 _rect; // client pixels

    /// <summary>The card: its size is set on open; subclasses fill it in <see cref="Build"/>.</summary>
    protected PanelContainer Card { get; private set; }

    protected ModernGump(World world)
    {
        World = world;
    }

    protected abstract void Build(PanelContainer card);

    /// <summary>
    /// Take the classic gump that is being opened (its serial, its kind) before
    /// the view opens for it; false keeps it Classic. By default, any.
    /// </summary>
    public virtual bool Accept(Game.UI.Gumps.Gump classic) => true;

    /// <summary>A finger held still on a control for <see cref="HoldMs"/>: no tap follows.</summary>
    protected virtual void OnHold(Control under) { }

    /// <summary>Milliseconds a still finger takes to be a hold.</summary>
    protected const int HoldMs = 500;

    /// <summary>Called on open, before it shows: read the state into the controls.</summary>
    protected virtual void OnOpen() { }

    /// <summary>A few times a second while open: refresh what the game changed.</summary>
    protected virtual void Refresh() { }

    public void Open()
    {
        if (!_built)
        {
            BuildNodes();
        }

        _open = true;
        Current = this;
        OnOpen();
        Place();
        _view.Visible = true;
        GD.Print($"[GUO] modern: {GetType().Name} open");
    }

    public virtual void Close()
    {
        if (!_open)
        {
            return;
        }

        _open = false;
        _pressing = false;

        if (Current == this)
        {
            Current = null;
        }

        _view.Visible = false;
        _viewport.GuiReleaseFocus();
        if (DisplayServer.HasFeature(DisplayServer.Feature.VirtualKeyboard))
        {
            DisplayServer.VirtualKeyboardHide();
        }
        GD.Print($"[GUO] modern: {GetType().Name} closed");
    }

    private void BuildNodes()
    {
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

        Card = new PanelContainer { Theme = UoTheme.Theme };

        if (CardStyle is StyleBox style)
        {
            Card.AddThemeStyleboxOverride("panel", style);
        }

        _root.AddChild(Card);
        Build(Card);

        _layer = new CanvasLayer { Layer = 99 };
        AddChild(_layer);
        _view = new TextureRect
        {
            Texture = _viewport.GetTexture(),
            MouseFilter = Control.MouseFilterEnum.Ignore,
            StretchMode = TextureRect.StretchModeEnum.Keep,
            TextureFilter = CanvasItem.TextureFilterEnum.Nearest,
            Visible = false,
        };
        _layer.AddChild(_view);
        _built = true;
    }

    private static float Dpi => Math.Max(0.01f, Client.Game?.DpiScale ?? 1f);

    /// <summary>The whole main screen below the top bar, with a margin above and below, centred.</summary>
    private void Place()
    {
        Compat.Rectangle main = Client.Game.ClientBounds;
        float k = _scale / Dpi; // art to client pixels
        int top = GumpPresentation.FullHeightTop() + Pad;
        _placedFor = (main.Width, main.Height, top, Dpi);
        float w = Math.Min(MaxArtWidth, (main.Width - 2 * Pad) / k);
        float h = Math.Max(40f, (main.Height - top - Pad) / k);
        w = (int)w;
        h = (int)h;
        Card.Position = Vector2.Zero;
        Card.CustomMinimumSize = new Vector2(w, h);
        Card.Size = new Vector2(w, h);

        _rect = new Rect2((int)((main.Width - w * k) / 2f), top, w * k, h * k);
        var px = new Vector2I((int)Math.Ceiling(w * _scale), (int)Math.Ceiling(h * _scale));
        _viewport.Size = px;
        _view.Position = _rect.Position * Dpi;
        _view.Size = px;
    }

    private double _refresh;
    private (int w, int h, int top, float dpi) _placedFor;

    /// <summary>The screen changed under an open view (a resize, a rotation, a screen swap).</summary>
    private bool ScreenChanged()
    {
        Compat.Rectangle main = Client.Game.ClientBounds;
        return _placedFor != (main.Width, main.Height, GumpPresentation.FullHeightTop() + Pad, Dpi);
    }

    public override void _Process(double delta)
    {
        if (!_open)
        {
            return;
        }

        if (!(Client.Game?.UO?.World?.InGame ?? false))
        {
            Close();
            return;
        }

        // Placed again once no finger is down, so taps map through the new rect.
        if (!_pressing && ScreenChanged())
        {
            Place();
        }

        // A finger held still: a hold, and no tap when it lifts.
        if (_pressing && !_dragged && !_onSlider && !_heldDone && Godot.Time.GetTicksMsec() - _pressTime >= HoldMs)
        {
            _heldDone = true;
            OnHold(_pressUnder);
        }

        _refresh -= delta;

        if (_refresh <= 0)
        {
            _refresh = 0.25;
            Refresh();
        }
    }

    // --- input -------------------------------------------------------------------

    private bool _pressing;
    private bool _dragged;
    private bool _onSlider;
    private Vector2 _pressAt;
    private Vector2 _lastAt;
    private ScrollContainer _scrollUnder;
    private TextEdit _textUnder;
    private ulong _pressTime;
    private bool _heldDone;
    private Control _pressUnder;

    /// <summary>
    /// Route an event while a Modern view is open. It is modal: everything is
    /// consumed. Keys go to its focused field.
    /// </summary>
    public static bool HandleInput(InputEvent e)
    {
        if (!IsOpen)
        {
            return false;
        }

        ModernGump m = Current;

        if (m.DirectMouseInput && e is InputEventMouse mouse)
        {
            if (mouse is InputEventMouseButton { Pressed: true } && !m._rect.HasPoint(mouse.Position / Dpi))
                return true;
            using var routed = (InputEventMouse)mouse.Duplicate();
            routed.Position = mouse.Position - m._rect.Position * Dpi;
            routed.GlobalPosition = routed.Position;
            m._viewport.PushInput(routed, true);
            return true;
        }

        if (e is InputEventKey key)
        {
            m._viewport.PushInput(key, true);
            return true;
        }

        Vector2? window = e switch
        {
            InputEventScreenTouch t => t.Position,
            InputEventScreenDrag d => d.Position,
            InputEventMouseButton mb when mb.ButtonIndex == MouseButton.Left => mb.Position,
            InputEventMouseMotion mm => mm.Position,
            _ => null,
        };

        if (window == null)
        {
            return true;
        }

        TouchOverlay.Note(e);
        Vector2 client = window.Value / Dpi;
        Vector2 local = (client - m._rect.Position) * Dpi; // device pixels in the viewport
        bool pressed = e is InputEventScreenTouch { Pressed: true } || e is InputEventMouseButton { Pressed: true };
        bool released = e is InputEventScreenTouch { Pressed: false } || e is InputEventMouseButton { Pressed: false };
        bool moved = e is InputEventScreenDrag || e is InputEventMouseMotion { ButtonMask: MouseButtonMask.Left };

        if (pressed)
        {
            m._pressing = m._rect.HasPoint(client);
            m._dragged = false;
            m._pressAt = m._lastAt = local;
            m._viewport.PushInput(new InputEventMouseMotion { Position = local, GlobalPosition = local }, true);
            Control under = m._viewport.GuiGetHoveredControl();
            m._pressTime = Godot.Time.GetTicksMsec();
            m._heldDone = false;
            m._pressUnder = under;
            m._onSlider = under is Godot.Range and not ScrollBar;
            m._scrollUnder = FindScroll(under);
            m._textUnder = null;
            if (m.DirectMouseInput)
                for (Node node = under; node != null; node = node.GetParent())
                    if (node is TextEdit text) { m._textUnder = text; break; }

            if (m._onSlider)
            {
                // A slider takes the finger from the start, as a mouse.
                m._viewport.PushInput(new InputEventMouseButton { Position = local, GlobalPosition = local, ButtonIndex = MouseButton.Left, Pressed = true, ButtonMask = MouseButtonMask.Left }, true);
            }
        }
        else if (m._pressing && moved)
        {
            if (m._onSlider)
            {
                m._viewport.PushInput(new InputEventMouseMotion { Position = local, GlobalPosition = local, ButtonMask = MouseButtonMask.Left }, true);
            }
            else
            {
                if (!m._dragged && local.DistanceTo(m._pressAt) > 8 * m._scale / 3f)
                {
                    m._dragged = true;
                }

                if (m._dragged && m._textUnder != null)
                {
                    m._textUnder.ScrollVertical -= (local.Y - m._lastAt.Y) / m._scale / Math.Max(1, m._textUnder.GetLineHeight());
                }
                else if (m._dragged && m._scrollUnder != null)
                {
                    m._scrollUnder.ScrollVertical -= (int)Math.Round((local.Y - m._lastAt.Y) / m._scale);
                }
            }

            m._lastAt = local;
        }
        else if (released && m._pressing)
        {
            m._pressing = false;

            if (m._onSlider)
            {
                m._viewport.PushInput(new InputEventMouseButton { Position = local, GlobalPosition = local, ButtonIndex = MouseButton.Left, Pressed = false }, true);
            }
            else if (!m._dragged && !m._heldDone)
            {
                m.Click(local);
            }
        }

        return true;
    }

    private static ScrollContainer FindScroll(Node n)
    {
        for (; n != null; n = n.GetParent())
        {
            if (n is ScrollContainer s)
            {
                return s;
            }
        }

        return null;
    }

    private void Click(Vector2 local)
    {
        _viewport.PushInput(new InputEventMouseMotion { Position = local, GlobalPosition = local }, true);
        _viewport.PushInput(new InputEventMouseButton { Position = local, GlobalPosition = local, ButtonIndex = MouseButton.Left, Pressed = true, ButtonMask = MouseButtonMask.Left }, true);
        _viewport.PushInput(new InputEventMouseButton { Position = local, GlobalPosition = local, ButtonIndex = MouseButton.Left, Pressed = false }, true);

        if (_viewport.GuiGetFocusOwner() is LineEdit field && DisplayServer.HasFeature(DisplayServer.Feature.VirtualKeyboard))
        {
            DisplayServer.VirtualKeyboardShow(field.Text);
        }
        else if (DirectMouseInput && _viewport.GuiGetFocusOwner() is TextEdit { Editable: true } editor &&
                 DisplayServer.HasFeature(DisplayServer.Feature.VirtualKeyboard))
        {
            int cursor = editor.GetCaretColumn();
            for (int line = 0; line < editor.GetCaretLine(); line++) cursor += editor.GetLine(line).Length + 1;
            DisplayServer.VirtualKeyboardShow(editor.Text, type: DisplayServer.VirtualKeyboardType.Multiline, cursorStart: cursor);
        }
    }

    // --- the probe --------------------------------------------------------------------

    /// <summary>For the probe: the centre of a control, in client pixels.</summary>
    public Vector2 CentreOf(Control c)
    {
        // GetGlobalRect already includes the scaled root's transform.
        Vector2 inViewport = c.GetGlobalRect().GetCenter();
        return _rect.Position + inViewport / Dpi;
    }

    /// <summary>For the probe: a point along a control's width (0 to 1), at its middle, in client pixels.</summary>
    public Vector2 AlongOf(Control c, float t)
    {
        Rect2 r = c.GetGlobalRect();
        Vector2 inViewport = new Vector2(r.Position.X + r.Size.X * t, r.Position.Y + r.Size.Y / 2);
        return _rect.Position + inViewport / Dpi;
    }

    /// <summary>For the probe: the card's rectangle, in client pixels.</summary>
    public Rect2 Rect => _rect;
}
