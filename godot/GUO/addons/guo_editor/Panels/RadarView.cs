#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
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

    /// <summary>Screen pixels per radar pixel from which the per-cell detail replaces the 1:4 overview.</summary>
    public const float DetailScale = 6f;
    private const int PerBlock = 2;        // radar pixels per block side (8 cells / the 4-cell stride)
    private const int BudgetPerFrame = 300;

    private readonly Dictionary<long, ImageTexture> _detail = new();
    private readonly HashSet<long> _empty = new();
    private int _detailDrawn;

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

    /// <summary>
    /// Returns the colours of one map block (bx, by) as an 8x8 image, one pixel per
    /// cell, or null if the block has none. Set by the Maps tab; the view caches
    /// each block it is given and draws it nearest-sampled when zoomed in.
    /// </summary>
    public Func<int, int, Image> DetailBlock { get; set; }

    /// <summary>Detail blocks drawn in the last frame (0 while the overview shows), for the smoke check.</summary>
    public int DetailBlocksDrawn => _detailDrawn;

    /// <summary>True when the current zoom shows per-cell detail.</summary>
    public bool ShowingDetail => _texture != null && DetailBlock != null && Scale >= DetailScale;

    /// <summary>Forgets every cached block (another facet, another map source).</summary>
    public void ClearDetail()
    {
        _detail.Clear();
        _empty.Clear();
        QueueRedraw();
    }

    /// <summary>Forgets one cached block (an edit changed it); it is read again when next drawn.</summary>
    public void DropDetail(int bx, int by)
    {
        long key = Key(bx, by);
        _detail.Remove(key);
        _empty.Remove(key);
        QueueRedraw();
    }

    private static long Key(int bx, int by) => ((long)bx << 32) | (uint)by;

    /// <summary>
    /// The detail of a rectangle of blocks as one image (8 pixels per block,
    /// one per cell), for the smoke check and for saving a frame.
    /// </summary>
    public Image ComposeDetail(int bx, int by, int blocksX, int blocksY)
    {
        var image = Image.CreateEmpty(blocksX * 8, blocksY * 8, false, Image.Format.Rgb8);
        image.Fill(new Color(0.05f, 0.05f, 0.06f));
        for (int x = 0; x < blocksX; x++)
        {
            for (int y = 0; y < blocksY; y++)
            {
                Image block = DetailBlock?.Invoke(bx + x, by + y);
                if (block != null)
                {
                    image.BlitRect(block, new Rect2I(0, 0, 8, 8), new Vector2I(x * 8, y * 8));
                }
            }
        }

        return image;
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
        _detailDrawn = 0;
        if (ShowingDetail)
        {
            DrawDetail(o, s);
        }

        if (_mark is { } m)
        {
            var r = new Rect2(o + new Vector2(m.X, m.Y) * s, new Vector2(s, s)).Grow(5);
            DrawRect(r.Grow(1.5f), Colors.Black, false, 2f);
            DrawRect(r, new Color(1, 0, 1), false, 2f);
        }
    }

    /// <summary>
    /// Over the overview, the blocks in view at one pixel per cell: each block's
    /// cached texture, nearest sampled (the view's texture filter, and each
    /// texture is drawn whole). At most a few hundred new blocks are read a
    /// frame; the rest follow on the next.
    /// </summary>
    private void DrawDetail(Vector2 origin, float scale)
    {
        Vector2 t = _texture.GetSize();
        int blocksX = (int)t.X / PerBlock, blocksY = (int)t.Y / PerBlock;
        Vector2 from = (Vector2.Zero - origin) / scale;
        Vector2 to = (Size - origin) / scale;
        int x0 = Math.Max(0, (int)Math.Floor(from.X / PerBlock)), x1 = Math.Min(blocksX - 1, (int)Math.Floor(to.X / PerBlock));
        int y0 = Math.Max(0, (int)Math.Floor(from.Y / PerBlock)), y1 = Math.Min(blocksY - 1, (int)Math.Floor(to.Y / PerBlock));
        float side = PerBlock * scale;
        int budget = BudgetPerFrame;
        bool more = false;
        for (int bx = x0; bx <= x1; bx++)
        {
            for (int by = y0; by <= y1; by++)
            {
                long key = Key(bx, by);
                if (!_detail.TryGetValue(key, out ImageTexture tex))
                {
                    if (_empty.Contains(key))
                    {
                        continue;
                    }

                    if (budget <= 0)
                    {
                        more = true;
                        continue;
                    }

                    budget--;
                    Image image = DetailBlock(bx, by);
                    if (image == null)
                    {
                        _empty.Add(key);
                        continue;
                    }

                    tex = ImageTexture.CreateFromImage(image);
                    _detail[key] = tex;
                }

                DrawTextureRect(tex, new Rect2(origin + new Vector2(bx, by) * PerBlock * scale, new Vector2(side, side)), false);
                _detailDrawn++;
            }
        }

        if (more)
        {
            QueueRedraw();
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
