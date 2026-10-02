#if TOOLS
namespace GUO.Editor;

using System;
using Godot;

/// <summary>
/// The minimap in a corner of the UO World view: the Maps tab's radar around
/// the camera, the camera's viewport drawn on it as the (rotated) rectangle
/// of cells it covers, a click or drag jumps there. Hidden from the Guides
/// menu. Never filtered: nearest sampling.
/// </summary>
[Tool]
public partial class MiniMap : Control
{
    private const int Stride = 4;          // cells per radar pixel (MapPanel.Stride)
    private const int Span = 128;          // radar pixels shown across

    private Image _image;
    private ImageTexture _texture;
    private int _facet = -1;
    private Vector2 _centre;               // radar pixels
    private Vector2[] _view = Array.Empty<Vector2>();
    private bool _dragging;

    /// <summary>Raised with the cell clicked.</summary>
    public event Action<int, int> Jump;

    /// <summary>True once a radar image is on show.</summary>
    public bool HasImage => _texture != null;

    /// <summary>The camera's viewport as drawn, in radar pixels (four corners), for the smoke check.</summary>
    public Vector2[] ViewPolygon => _view;

    public MiniMap()
    {
        MouseFilter = MouseFilterEnum.Stop;
        ClipContents = true;
        TextureFilter = TextureFilterEnum.Nearest;
    }

    public override void _Ready()
    {
        float s = EditorInterface.Singleton.GetEditorScale();
        CustomMinimumSize = new Vector2(240 * s, 240 * s);
        SetAnchorsPreset(LayoutPreset.TopRight, true);
        // Top right, inside the view.
        OffsetLeft = -CustomMinimumSize.X - 10 * s;
        OffsetRight = -10 * s;
        OffsetTop = 10 * s;
        OffsetBottom = 10 * s + CustomMinimumSize.Y;
        TooltipText = "Minimap: click or drag to jump. Hide it from the Guides menu.";
    }

    /// <summary>Forgets the radar so the next update fetches it again (after an edit changed the map).</summary>
    public void Invalidate() => _facet = -1;

    internal void Update(WorldHost host, Func<int, Image> source, Vector2I viewport, float zoom)
    {
        int facet = host.Facet;
        if (facet != _facet || _texture == null)
        {
            _facet = facet;
            _image = source?.Invoke(facet);
            _texture = _image != null ? ImageTexture.CreateFromImage(_image) : null;
        }

        _centre = new Vector2(host.X, host.Y) / Stride;
        // A screen offset (sx, sy) at zoom z is x = (sx + sy) / 44, y = (sy - sx) / 44 cells.
        Vector2 half = new Vector2(viewport.X, viewport.Y) * zoom / 2;
        Vector2[] corners = { new(-half.X, -half.Y), new(half.X, -half.Y), new(half.X, half.Y), new(-half.X, half.Y) };
        _view = new Vector2[4];
        for (int i = 0; i < 4; i++)
        {
            _view[i] = _centre + new Vector2(corners[i].X + corners[i].Y, corners[i].Y - corners[i].X) / 44f / Stride;
        }

        QueueRedraw();
    }

    /// <summary>Does what a click on a cell of the minimap does, for scripted use.</summary>
    public void ScriptedJump(int x, int y) => Jump?.Invoke(x, y);

    private float Scale => Size.X / Span;

    private Vector2 Origin => Size / 2 - _centre * Scale;

    public override void _Draw()
    {
        DrawRect(new Rect2(Vector2.Zero, Size), new Color(0.05f, 0.05f, 0.06f));
        if (_texture != null)
        {
            DrawTextureRect(_texture, new Rect2(Origin, _texture.GetSize() * Scale), false);
        }

        if (_view.Length == 4)
        {
            var pts = new Vector2[5];
            for (int i = 0; i < 4; i++)
            {
                pts[i] = Origin + _view[i] * Scale;
            }

            pts[4] = pts[0];
            DrawPolyline(pts, Colors.Black, 4f);
            DrawPolyline(pts, new Color(1f, 0.9f, 0.2f), 2f);
        }

        DrawRect(new Rect2(Vector2.Zero, Size), new Color(0.78f, 0.62f, 0.3f), false, 2f);
    }

    public override void _GuiInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } b:
                _dragging = b.Pressed;
                if (b.Pressed)
                {
                    JumpTo(b.Position);
                }

                AcceptEvent();
                break;
            case InputEventMouseMotion m when _dragging:
                JumpTo(m.Position);
                AcceptEvent();
                break;
        }
    }

    private void JumpTo(Vector2 at)
    {
        Vector2 px = (at - Origin) / Scale;
        Jump?.Invoke((int)(px.X * Stride), (int)(px.Y * Stride));
    }
}
#endif
