#if TOOLS
namespace GUO.Editor;

using System;
using Godot;

/// <summary>
/// A zoomable, pannable view of a map radar image: wheel zooms about the
/// pointer, a drag pans, a click without a drag picks the image pixel under
/// it. Always nearest sampling: pixel art is never filtered. Used by the Maps
/// tab, which gives it the whole centre.
/// </summary>
[Tool]
public partial class RadarView : Control
{
    private Texture2D _texture;
    private float _zoom = 1f;          // relative to the fit
    private Vector2 _pan;              // image pixel at the view's centre
    private bool _fitted;
    private Vector2 _pressAt;
    private bool _pressed, _dragged;
    private Vector2I? _mark;

    /// <summary>Raised with the image pixel under a click, whether Ctrl was held, and whether it was a double click.</summary>
    public event Action<Vector2I, bool, bool> Picked;

    public Texture2D Texture
    {
        get => _texture;
        set
        {
            Vector2 old = _texture != null ? _texture.GetSize() : Vector2.Zero;
            _texture = value;
            if (value != null && value.GetSize() != old)
            {
                _fitted = false;
            }

            QueueRedraw();
        }
    }

    /// <summary>A pixel to outline, or null.</summary>
    public Vector2I? Mark
    {
        get => _mark;
        set
        {
            _mark = value;
            QueueRedraw();
        }
    }

    public RadarView()
    {
        MouseFilter = MouseFilterEnum.Stop;
        ClipContents = true;
        TextureFilter = TextureFilterEnum.Nearest;
        CustomMinimumSize = new Vector2(200, 200);
        SizeFlagsVertical = SizeFlags.ExpandFill;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
    }

    private float FitScale
    {
        get
        {
            if (_texture == null || Size.X < 1 || Size.Y < 1)
            {
                return 1f;
            }

            Vector2 t = _texture.GetSize();
            return Math.Min(Size.X / t.X, Size.Y / t.Y);
        }
    }

    private float Scale => FitScale * _zoom;

    private Vector2 Origin => Size / 2 - _pan * Scale;

    public void ZoomToFit()
    {
        _zoom = 1f;
        _pan = _texture != null ? _texture.GetSize() / 2 : Vector2.Zero;
        _fitted = true;
        QueueRedraw();
    }

    /// <summary>Centres the view on an image pixel at a zoom (1 = fit).</summary>
    public void Focus(Vector2I px, float zoom)
    {
        _zoom = Math.Clamp(zoom, 1f, 32f);
        _pan = px;
        _fitted = true;
        QueueRedraw();
    }

    public override void _Notification(int what)
    {
        if (what == NotificationResized)
        {
            QueueRedraw();
        }
    }

    public override void _Draw()
    {
        DrawRect(new Rect2(Vector2.Zero, Size), new Color(0.05f, 0.05f, 0.06f));
        if (_texture == null)
        {
            return;
        }

        if (!_fitted && Size.X > 1)
        {
            ZoomToFit();
        }

        float s = Scale;
        Vector2 o = Origin;
        DrawTextureRect(_texture, new Rect2(o, _texture.GetSize() * s), false);
        if (_mark is { } m)
        {
            var r = new Rect2(o + new Vector2(m.X, m.Y) * s, new Vector2(s, s)).Grow(5);
            DrawRect(r.Grow(1.5f), Colors.Black, false, 2f);
            DrawRect(r, new Color(1, 0, 1), false, 2f);
        }
    }

    public override void _GuiInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown } w:
                ZoomAt(w.Position, w.ButtonIndex == MouseButton.WheelUp ? 1.25f : 1f / 1.25f);
                AcceptEvent();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left or MouseButton.Right or MouseButton.Middle } b:
                if (b.Pressed)
                {
                    _pressed = true;
                    _dragged = false;
                    _pressAt = b.Position;
                }
                else
                {
                    bool click = _pressed && !_dragged && b.ButtonIndex == MouseButton.Left;
                    _pressed = false;
                    if (click && _texture != null)
                    {
                        Vector2 p = (b.Position - Origin) / Scale;
                        Vector2 t = _texture.GetSize();
                        if (p.X >= 0 && p.Y >= 0 && p.X < t.X && p.Y < t.Y)
                        {
                            Picked?.Invoke(new Vector2I((int)p.X, (int)p.Y), b.CtrlPressed, b.DoubleClick);
                        }
                    }
                }

                break;
            case InputEventMouseMotion m when _pressed:
                if (!_dragged && m.Position.DistanceTo(_pressAt) > 4)
                {
                    _dragged = true;
                }

                if (_dragged)
                {
                    _pan -= m.Relative / Scale;
                    QueueRedraw();
                }

                break;
        }
    }

    private void ZoomAt(Vector2 at, float factor)
    {
        if (_texture == null)
        {
            return;
        }

        Vector2 before = (at - Origin) / Scale;
        _zoom = Math.Clamp(_zoom * factor, 1f, 32f);
        Vector2 after = (at - Origin) / Scale;
        _pan += before - after;
        QueueRedraw();
    }
}
#endif
