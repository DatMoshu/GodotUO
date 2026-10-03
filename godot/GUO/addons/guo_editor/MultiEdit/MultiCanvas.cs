#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using GUO.Assets;

public enum MultiTool
{
    Select,
    Draw,
    Erase,
    Pipette,
    Rect,
    Line,
    Brush,
    Move,
}

/// <summary>The client's seven per-story vision modes (CUSTOM_HOUSE_FLOOR_VISION_STATE).</summary>
public enum StoryVision
{
    Normal,
    TransparentContent,
    HideContent,
    TransparentFloor,
    HideFloor,
    TranslucentFloor,
    HideAll,
}

/// <summary>
/// The Multi Editor's canvas (ADR-0031): a multi drawn at UO scale (44 px cells, 4 px a z) in the order
/// <see cref="MultiPanel.ClientOrderOf"/> gives, with zoom, pan, a grid, a translucent virtual floor at
/// the editing z, the client's per-story vision modes, the validation overlay and the editing tools.
/// Every texture is sampled nearest (AGENTS.md rule 7).
/// </summary>
[Tool]
public partial class MultiCanvas : Control
{
    private static readonly float[] Zooms = { 0.5f, 1f, 2f, 3f, 4f, 6f };

    private EditorData _data;
    private MultiDocument _doc;
    private HouseTables _tables;

    private readonly Dictionary<(ushort, ushort), (ImageTexture Tex, Image Img)> _art = new();
    private List<MultiPart> _order = new();
    private bool _orderDirty = true;
    private ValidationResult _result;
    private Dictionary<int, List<Finding>> _byUid = new();
    private int _zoomIndex = 2;
    private Vector2 _origin = new(400, 300);
    private (int X, int Y)? _hover;
    private int? _hoverUid;
    private (int X, int Y)? _dragStart;
    private Vector2? _boxStart;
    private Vector2 _mouse;
    private bool _panning;
    private HashSet<int> _eraseSet = new();
    private List<(int X, int Y)> _paintCells = new();
    private (int Dx, int Dy)? _moveDelta;
    private bool _drawingNow;

    public MultiTool Tool { get; set; } = MultiTool.Select;
    public int EditZ { get; set; } = Stories.FloorZ;
    public ushort TileId { get; set; }
    public ushort TileHue { get; set; }
    public int BrushRadius { get; set; }
    public bool ReplaceCell { get; set; }
    public bool ShowGrid { get; set; } = true;
    public bool ShowVirtualFloor { get; set; } = true;
    public bool ShowWalkable { get; set; } = true;
    public bool ShowProblems { get; set; } = true;
    public bool ShowHidden { get; set; } = true;
    public bool CutAbove { get; set; }
    public int ActiveStory => Math.Max(0, Stories.StoryOf(EditZ));
    public int ZMin { get; set; } = -128;
    public int ZMax { get; set; } = 127;
    public StoryVision[] Vision { get; } = new StoryVision[Stories.Max];

    /// <summary>Parts drawn on the last frame, for the smoke check.</summary>
    public int DrawnCount { get; private set; }

    /// <summary>The order the canvas paints in, for the smoke check against the Multis panel.</summary>
    public IReadOnlyList<MultiPart> PaintOrder
    {
        get
        {
            EnsureOrder();
            return _order;
        }
    }

    public event Action<string> Status;
    public event Action<ushort, ushort> Pipetted;
    public event Action<int> EditZChanged;
    public event Action<ushort> TileDropped;

    public MultiCanvas()
    {
        FocusMode = FocusModeEnum.All;
        ClipContents = true;
        TextureFilter = TextureFilterEnum.Nearest;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        MouseDefaultCursorShape = CursorShape.Cross;
    }

    internal void Attach(EditorData data, MultiDocument doc, HouseTables tables)
    {
        _data = data;
        _doc = doc;
        _tables = tables;
        _doc.Changed += OnDocChanged;
    }

    public void Detach()
    {
        if (_doc != null)
        {
            _doc.Changed -= OnDocChanged;
        }

        _art.Clear();
    }

    internal void SetTables(HouseTables tables)
    {
        _tables = tables;
        _orderDirty = true;
        QueueRedraw();
    }

    public void SetResult(ValidationResult result)
    {
        _result = result;
        _byUid = result.Findings.Where(f => f.Uid >= 0).GroupBy(f => f.Uid).ToDictionary(g => g.Key, g => g.ToList());
        QueueRedraw();
    }

    public void ForgetArt()
    {
        _art.Clear();
        _orderDirty = true;
        QueueRedraw();
    }

    public void Touch()
    {
        _orderDirty = true;
        QueueRedraw();
    }

    private void OnDocChanged()
    {
        _orderDirty = true;
        QueueRedraw();
    }

    // --- geometry ----------------------------------------------------------------------

    private float Zoom => Zooms[_zoomIndex];

    /// <summary>The cell centre on screen, in world pixels, for (continuous) cell coordinates at z.</summary>
    private static Vector2 P(float fx, float fy, float z) => new((fx - fy) * 22f + 22f, (fx + fy) * 22f + 22f - z * 4f);

    private Vector2 ToWorld(Vector2 screen) => (screen - _origin) / Zoom;

    private (int X, int Y) CellAt(Vector2 screen, int z)
    {
        Vector2 w = ToWorld(screen);
        float a = (w.X - 22f) / 22f;
        float b = (w.Y + z * 4f - 22f) / 22f;
        return ((int)MathF.Floor((a + b) / 2f + 0.5f), (int)MathF.Floor((b - a) / 2f + 0.5f));
    }

    private Rect2 PartRect(MultiPart p, Image img)
    {
        int w = img?.GetWidth() ?? 44, h = img?.GetHeight() ?? 44;
        return new Rect2((p.X - p.Y) * 22 + 22 - w / 2, (p.X + p.Y) * 22 - p.Z * 4 + 44 - h, w, h);
    }

    public void ZoomBy(int steps, Vector2? around = null)
    {
        int next = Math.Clamp(_zoomIndex + steps, 0, Zooms.Length - 1);
        if (next == _zoomIndex)
        {
            return;
        }

        Vector2 at = around ?? Size / 2;
        Vector2 world = ToWorld(at);
        _zoomIndex = next;
        _origin = at - world * Zoom;
        QueueRedraw();
        Status?.Invoke($"zoom {Zoom}x");
    }

    public float ZoomFactor => Zoom;

    /// <summary>Centres the multi and picks the largest zoom that shows all of it.</summary>
    public void FitView()
    {
        var b = _doc.Bounds();
        int x0 = b?.X0 ?? -6, y0 = b?.Y0 ?? -6, x1 = b?.X1 ?? 6, y1 = b?.Y1 ?? 6;
        int zMax = _doc.Parts.Count == 0 ? 7 : _doc.Parts.Max(p => p.Z + 40);
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var (fx, fy, z) in new[] { (x0 - 1f, y0 - 1f, 0f), (x1 + 1f, y0 - 1f, 0f), (x0 - 1f, y1 + 1f, 0f), (x1 + 1f, y1 + 1f, 0f), (x0 - 1f, y0 - 1f, (float)zMax) })
        {
            Vector2 v = P(fx, fy, z);
            minX = Math.Min(minX, v.X);
            minY = Math.Min(minY, v.Y);
            maxX = Math.Max(maxX, v.X);
            maxY = Math.Max(maxY, v.Y);
        }

        Vector2 size = Size.X > 10 ? Size : new Vector2(900, 600);
        _zoomIndex = 0;
        for (int i = Zooms.Length - 1; i >= 0; i--)
        {
            if ((maxX - minX) * Zooms[i] <= size.X * 0.92f && (maxY - minY) * Zooms[i] <= size.Y * 0.92f)
            {
                _zoomIndex = i;
                break;
            }
        }

        Vector2 centre = new((minX + maxX) / 2, (minY + maxY) / 2);
        _origin = size / 2 - centre * Zoom;
        QueueRedraw();
    }

    // --- what is drawn -------------------------------------------------------------------

    private bool IsFloor(MultiPart p)
    {
        if (_tables != null && _tables.FloorIds.Count > 0)
        {
            return _tables.FloorIds.Contains(p.Id);
        }

        StaticTiles[] tiles = _data.Files.TileData.StaticData;
        StaticTiles t = p.Id < tiles.Length ? tiles[p.Id] : default;
        return t.IsSurface && !t.IsImpassable && !t.IsRoof;
    }

    /// <summary>Whether a part is drawn and how opaque, by the client's vision rules and the cut/z filters.</summary>
    public (bool Visible, float Alpha) Display(MultiPart p)
    {
        if (p.Z < ZMin || p.Z > ZMax)
        {
            return (false, 0);
        }

        int story = Stories.StoryOf(p.Z);
        if (CutAbove && story > ActiveStory)
        {
            return (false, 0);
        }

        float alpha = 1f;
        if (story >= 0)
        {
            bool floor = IsFloor(p);
            switch (Vision[story])
            {
                case StoryVision.HideAll:
                    return (false, 0);
                case StoryVision.HideContent when !floor:
                    return (false, 0);
                case StoryVision.TransparentContent when !floor:
                    alpha = 0.4f;
                    break;
                case StoryVision.HideFloor when floor:
                    return (false, 0);
                case StoryVision.TransparentFloor or StoryVision.TranslucentFloor when floor:
                    alpha = 0.4f;
                    break;
            }
        }

        if (!p.Shown)
        {
            if (!ShowHidden)
            {
                return (false, 0);
            }

            alpha = Math.Min(alpha, 0.35f);
        }

        return (true, alpha);
    }

    private void EnsureOrder()
    {
        if (!_orderDirty || _data == null || !_data.IsLoaded)
        {
            return;
        }

        // The Multis panel's sort, on every part: hidden ones included so they can be picked and shown ghosted.
        _order = MultiPanel.ClientOrderOf(_data, _doc.Parts, p => (p.Id, p.X, p.Y, p.Z, true));
        _orderDirty = false;
    }

    private (ImageTexture Tex, Image Img) ArtOf(MultiPart p)
    {
        if (_art.TryGetValue((p.Id, p.Hue), out var hit))
        {
            return hit;
        }

        Image img = _data.ArtImage(EditorData.LandCount + p.Id);
        if (img != null && p.Hue != 0)
        {
            StaticTiles[] tiles = _data.Files.TileData.StaticData;
            bool partial = p.Id < tiles.Length && tiles[p.Id].IsPartialHue;
            img = HueTint.Apply(_data, img, p.Hue, partial);
        }

        var made = (img != null ? ImageTexture.CreateFromImage(img) : null, img);
        _art[(p.Id, p.Hue)] = made;
        return made;
    }

    public override void _Draw()
    {
        DrawRect(new Rect2(Vector2.Zero, Size), new Color(0.11f, 0.10f, 0.09f));
        if (_data == null || !_data.IsLoaded)
        {
            DrawString(ThemeDB.FallbackFont, new Vector2(20, 30), "waiting for the client data...", HorizontalAlignment.Left, -1, 14);
            return;
        }

        EnsureOrder();
        DrawSetTransform(_origin, 0, new Vector2(Zoom, Zoom));

        var b = _doc.Bounds();
        int x0 = Math.Min(b?.X0 ?? 0, -5) - 2, y0 = Math.Min(b?.Y0 ?? 0, -5) - 2;
        int x1 = Math.Max(b?.X1 ?? 0, 5) + 2, y1 = Math.Max(b?.Y1 ?? 0, 5) + 2;

        if (ShowVirtualFloor)
        {
            // The client's virtual floor: a translucent plane at each story up to the one being edited.
            int top = Stories.StoryOf(EditZ);
            for (int s = 0; s <= Math.Max(0, top); s++)
            {
                if (top < 0 && s > 0)
                {
                    break;
                }

                float z = top < 0 ? EditZ : Stories.ZOf(s);
                bool active = s == top || top < 0;
                Color fill = new(0.30f, 0.62f, 0.38f, active ? 0.20f : 0.08f);
                DrawColoredPolygon(new[] { P(x0 - .5f, y0 - .5f, z), P(x1 + .5f, y0 - .5f, z), P(x1 + .5f, y1 + .5f, z), P(x0 - .5f, y1 + .5f, z) }, fill);
            }

            if (top >= 0 && EditZ != Stories.ZOf(top))
            {
                DrawColoredPolygon(new[] { P(x0 - .5f, y0 - .5f, EditZ), P(x1 + .5f, y0 - .5f, EditZ), P(x1 + .5f, y1 + .5f, EditZ), P(x0 - .5f, y1 + .5f, EditZ) }, new Color(0.9f, 0.8f, 0.3f, 0.10f));
            }
        }

        if (ShowGrid)
        {
            var line = new Color(1, 1, 1, 0.16f);
            var axis = new Color(1f, 0.85f, 0.2f, 0.5f);
            for (int x = x0; x <= x1 + 1; x++)
            {
                DrawLine(P(x - .5f, y0 - .5f, EditZ), P(x - .5f, y1 + .5f, EditZ), x == 0 || x == 1 ? axis : line, 1f / Zoom);
            }

            for (int y = y0; y <= y1 + 1; y++)
            {
                DrawLine(P(x0 - .5f, y - .5f, EditZ), P(x1 + .5f, y - .5f, EditZ), y == 0 || y == 1 ? axis : line, 1f / Zoom);
            }
        }

        int drawn = 0;
        foreach (MultiPart p in _order)
        {
            (bool visible, float alpha) = Display(p);
            if (!visible)
            {
                continue;
            }

            MultiPart q = p;
            if (_moveDelta is { } d && _doc.Selection.Contains(p.Uid))
            {
                q.X = (short)(q.X + d.Dx);
                q.Y = (short)(q.Y + d.Dy);
            }

            var (tex, img) = ArtOf(q);
            Rect2 r = PartRect(q, img);
            if (tex != null)
            {
                DrawTextureRect(tex, r, false, new Color(1, 1, 1, alpha));
            }
            else
            {
                Vector2 c = P(q.X, q.Y, q.Z);
                DrawPolyline(new[] { c + new Vector2(0, -8), c + new Vector2(8, 0), c + new Vector2(0, 8), c + new Vector2(-8, 0), c + new Vector2(0, -8) },
                    new Color(1, 0.4f, 1, alpha), 1f / Zoom);
            }

            drawn++;
            DrawMarks(q, r);
        }

        DrawnCount = drawn;

        if (ShowWalkable && _result != null)
        {
            foreach (var (x, y, z) in _result.Walkable)
            {
                Vector2 c = P(x, y, z);
                DrawColoredPolygon(new[] { c + new Vector2(0, -6), c + new Vector2(8, 0), c + new Vector2(0, 6), c + new Vector2(-8, 0) }, new Color(0.2f, 1f, 0.3f, 0.45f));
            }
        }

        if (ShowProblems && _result != null)
        {
            foreach (Finding f in _result.Findings.Where(f => f.Uid < 0 && f.Kind != "no-door" && f.Kind != "too-many" && f.Kind != "legality-skipped"))
            {
                Vector2 c = P(f.X, f.Y, f.Z);
                DrawPolyline(new[] { c + new Vector2(0, -22), c + new Vector2(22, 0), c + new Vector2(0, 22), c + new Vector2(-22, 0), c + new Vector2(0, -22) }, KindColour(f), 2f / Zoom);
            }
        }

        DrawToolPreview();
        DrawSetTransform(Vector2.Zero, 0, Vector2.One);

        if (_boxStart is { } bs)
        {
            var box = new Rect2(bs, _mouse - bs).Abs();
            DrawRect(box, new Color(0.4f, 0.7f, 1f, 0.18f));
            DrawRect(box, new Color(0.4f, 0.7f, 1f, 0.9f), false, 1f);
        }
    }

    private static Color KindColour(Finding f) => f.Kind switch
    {
        "unknown-id" or "z-range" => new Color(1f, 0.15f, 0.1f),
        "double-surface" => new Color(1f, 0.6f, 0.1f),
        "cross-story" => new Color(1f, 0.95f, 0.2f),
        "illegal" => new Color(0.95f, 0.2f, 0.9f),
        "duplicate" => new Color(0.2f, 0.9f, 1f),
        "walls-open" => new Color(1f, 0.4f, 0.2f),
        _ => new Color(0.5f, 0.7f, 1f),
    };

    private void DrawMarks(MultiPart p, Rect2 r)
    {
        float w = 1.5f / Zoom;
        if (_doc.Selection.Contains(p.Uid))
        {
            DrawRect(r, new Color(1f, 0.9f, 0.2f), false, w);
        }
        else if (_eraseSet.Contains(p.Uid))
        {
            DrawRect(r, new Color(1f, 0.1f, 0.1f), false, w);
        }
        else if (_hoverUid == p.Uid && Tool is MultiTool.Select or MultiTool.Erase or MultiTool.Pipette or MultiTool.Move)
        {
            DrawRect(r, new Color(1, 1, 1, 0.8f), false, w);
        }

        if (ShowProblems && _byUid.TryGetValue(p.Uid, out var list) && !_doc.Selection.Contains(p.Uid))
        {
            Finding worst = list.OrderByDescending(f => f.Severity).First();
            DrawRect(r.Grow(1), KindColour(worst), false, w);
        }
    }

    private void DrawToolPreview()
    {
        if (_hover is not { } h)
        {
            return;
        }

        Color ghost = new(1f, 1f, 1f, 0.55f);
        switch (Tool)
        {
            case MultiTool.Draw or MultiTool.Brush or MultiTool.Rect or MultiTool.Line:
                IEnumerable<(int X, int Y)> cells = _drawingNow ? PreviewCells(h) : BrushCells(h, Tool == MultiTool.Brush ? BrushRadius : 0);
                foreach (var (x, y) in cells.Take(4000))
                {
                    if (TileId != 0)
                    {
                        var (tex, img) = ArtOf(new MultiPart { Id = TileId, Hue = TileHue });
                        if (tex != null)
                        {
                            DrawTextureRect(tex, PartRect(new MultiPart { X = (short)x, Y = (short)y, Z = (short)EditZ }, img), false, ghost);
                        }
                    }

                    Vector2 c = P(x, y, EditZ);
                    DrawPolyline(new[] { c + new Vector2(0, -22), c + new Vector2(22, 0), c + new Vector2(0, 22), c + new Vector2(-22, 0), c + new Vector2(0, -22) }, new Color(0.4f, 1f, 0.4f, 0.9f), 1.5f / Zoom);
                }

                break;
            default:
                Vector2 cc = P(h.X, h.Y, EditZ);
                DrawPolyline(new[] { cc + new Vector2(0, -22), cc + new Vector2(22, 0), cc + new Vector2(0, 22), cc + new Vector2(-22, 0), cc + new Vector2(0, -22) }, new Color(1, 1, 1, 0.5f), 1f / Zoom);
                break;
        }
    }

    // --- cells for the shape tools --------------------------------------------------------------

    private static IEnumerable<(int X, int Y)> BrushCells((int X, int Y) c, int radius)
    {
        for (int dx = -radius; dx <= radius; dx++)
        {
            for (int dy = -radius; dy <= radius; dy++)
            {
                yield return (c.X + dx, c.Y + dy);
            }
        }
    }

    public static List<(int X, int Y)> RectCells((int X, int Y) a, (int X, int Y) b, bool hollow)
    {
        var list = new List<(int, int)>();
        for (int x = Math.Min(a.X, b.X); x <= Math.Max(a.X, b.X); x++)
        {
            for (int y = Math.Min(a.Y, b.Y); y <= Math.Max(a.Y, b.Y); y++)
            {
                if (!hollow || x == Math.Min(a.X, b.X) || x == Math.Max(a.X, b.X) || y == Math.Min(a.Y, b.Y) || y == Math.Max(a.Y, b.Y))
                {
                    list.Add((x, y));
                }
            }
        }

        return list;
    }

    /// <summary>Bresenham's line, both ends included.</summary>
    public static List<(int X, int Y)> LineCells((int X, int Y) a, (int X, int Y) b)
    {
        var list = new List<(int, int)>();
        int dx = Math.Abs(b.X - a.X), dy = Math.Abs(b.Y - a.Y), sx = a.X < b.X ? 1 : -1, sy = a.Y < b.Y ? 1 : -1;
        int err = dx - dy, x = a.X, y = a.Y;
        while (true)
        {
            list.Add((x, y));
            if (x == b.X && y == b.Y)
            {
                break;
            }

            int e2 = 2 * err;
            if (e2 > -dy)
            {
                err -= dy;
                x += sx;
            }

            if (e2 < dx)
            {
                err += dx;
                y += sy;
            }
        }

        return list;
    }

    private IEnumerable<(int X, int Y)> PreviewCells((int X, int Y) now)
    {
        switch (Tool)
        {
            case MultiTool.Rect when _dragStart is { } a:
                return RectCells(a, now, Input.IsKeyPressed(Key.Shift));
            case MultiTool.Line when _dragStart is { } a2:
                return LineCells(a2, now);
            case MultiTool.Brush or MultiTool.Draw:
                return _paintCells.Count > 0 ? _paintCells.Concat(BrushCells(now, Tool == MultiTool.Brush ? BrushRadius : 0)) : BrushCells(now, Tool == MultiTool.Brush ? BrushRadius : 0);
            default:
                return BrushCells(now, 0);
        }
    }

    // --- picking -----------------------------------------------------------------------------------

    /// <summary>The topmost visible part under a screen point (by the pixel under it), or null.</summary>
    public MultiPart? PickAt(Vector2 screen)
    {
        EnsureOrder();
        Vector2 w = ToWorld(screen);
        for (int i = _order.Count - 1; i >= 0; i--)
        {
            MultiPart p = _order[i];
            if (!Display(p).Visible)
            {
                continue;
            }

            var (_, img) = ArtOf(p);
            Rect2 r = PartRect(p, img);
            if (!r.HasPoint(w))
            {
                continue;
            }

            if (img == null)
            {
                Vector2 c = P(p.X, p.Y, p.Z);
                if (Math.Abs(w.X - c.X) + Math.Abs(w.Y - c.Y) <= 10)
                {
                    return p;
                }

                continue;
            }

            int px = (int)(w.X - r.Position.X), py = (int)(w.Y - r.Position.Y);
            if (px >= 0 && py >= 0 && px < img.GetWidth() && py < img.GetHeight() && img.GetPixel(px, py).A > 0)
            {
                return p;
            }
        }

        return null;
    }

    /// <summary>Parts whose cell centre falls in a screen box.</summary>
    public List<int> PartsInBox(Rect2 box)
    {
        EnsureOrder();
        var list = new List<int>();
        foreach (MultiPart p in _order)
        {
            if (Display(p).Visible && box.HasPoint(P(p.X, p.Y, p.Z) * Zoom + _origin))
            {
                list.Add(p.Uid);
            }
        }

        return list;
    }

    // --- input ----------------------------------------------------------------------------------------

    public override void _GuiInput(InputEvent e)
    {
        if (_doc == null || _data == null || !_data.IsLoaded)
        {
            return;
        }

        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelUp, Pressed: true } wu:
                ZoomBy(1, wu.Position);
                AcceptEvent();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelDown, Pressed: true } wd:
                ZoomBy(-1, wd.Position);
                AcceptEvent();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Middle or MouseButton.Right } mb:
                _panning = mb.Pressed;
                break;
            case InputEventMouseMotion mm:
                _mouse = mm.Position;
                if (_panning)
                {
                    _origin += mm.Relative;
                    QueueRedraw();
                    break;
                }

                OnMove(mm);
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } lb:
                GrabFocus();
                _mouse = lb.Position;
                if (lb.Pressed)
                {
                    OnPress(lb);
                }
                else
                {
                    OnRelease(lb);
                }

                break;
            case InputEventKey { Pressed: true } k:
                if (OnKey(k))
                {
                    AcceptEvent();
                }

                break;
        }
    }

    private void OnMove(InputEventMouseMotion mm)
    {
        _hover = CellAt(mm.Position, EditZ);
        MultiPart? under = PickAt(mm.Position);
        _hoverUid = under?.Uid;
        if (_drawingNow)
        {
            switch (Tool)
            {
                case MultiTool.Draw or MultiTool.Brush:
                    foreach (var c in BrushCells(_hover.Value, Tool == MultiTool.Brush ? BrushRadius : 0))
                    {
                        if (!_paintCells.Contains(c))
                        {
                            _paintCells.Add(c);
                        }
                    }

                    break;
                case MultiTool.Erase when under is { } u:
                    _eraseSet.Add(u.Uid);
                    break;
                case MultiTool.Move when _dragStart is { } s:
                    _moveDelta = (_hover.Value.X - s.X, _hover.Value.Y - s.Y);
                    break;
            }
        }

        Status?.Invoke(Describe(under));
        QueueRedraw();
    }

    private string Describe(MultiPart? under)
    {
        string cell = _hover is { } h ? $"cell {h.X},{h.Y}  z {EditZ}" : "";
        if (under is { } p)
        {
            string name = p.Id < _data.Files.TileData.StaticData.Length ? _data.Files.TileData.StaticData[p.Id].Name : "?";
            string role = _tables?.RoleOf(p.Id);
            return $"{cell}   0x{p.Id:X4} {name} at {p.X},{p.Y},{p.Z}{(p.Hue != 0 ? $" hue {p.Hue}" : "")}{(p.Shown ? "" : " (hidden)")}{(role != null ? "   " + role : "")}";
        }

        return cell;
    }

    private void OnPress(InputEventMouseButton lb)
    {
        _hover = CellAt(lb.Position, EditZ);
        MultiPart? under = PickAt(lb.Position);
        switch (Tool)
        {
            case MultiTool.Select:
                if (under is { } u)
                {
                    if (lb.CtrlPressed)
                    {
                        if (!_doc.Selection.Remove(u.Uid))
                        {
                            _doc.Selection.Add(u.Uid);
                        }
                    }
                    else if (lb.ShiftPressed)
                    {
                        _doc.Selection.Add(u.Uid);
                    }
                    else
                    {
                        _doc.Selection.Clear();
                        _doc.Selection.Add(u.Uid);
                    }

                    SelectionChanged();
                }
                else
                {
                    _boxStart = lb.Position;
                }

                break;
            case MultiTool.Pipette:
                if (under is { } pp)
                {
                    TileId = pp.Id;
                    TileHue = pp.Hue;
                    Pipetted?.Invoke(pp.Id, pp.Hue);
                    Status?.Invoke($"picked 0x{pp.Id:X4}");
                }

                break;
            case MultiTool.Draw or MultiTool.Brush:
                _drawingNow = true;
                _paintCells = BrushCells(_hover.Value, Tool == MultiTool.Brush ? BrushRadius : 0).ToList();
                break;
            case MultiTool.Rect or MultiTool.Line:
                _drawingNow = true;
                _dragStart = _hover;
                break;
            case MultiTool.Erase:
                _drawingNow = true;
                _eraseSet = new HashSet<int>();
                if (under is { } eu)
                {
                    _eraseSet.Add(eu.Uid);
                }

                break;
            case MultiTool.Move:
                if (under is { } mu && !_doc.Selection.Contains(mu.Uid))
                {
                    _doc.Selection.Clear();
                    _doc.Selection.Add(mu.Uid);
                    SelectionChanged();
                }

                if (_doc.Selection.Count > 0)
                {
                    _drawingNow = true;
                    _dragStart = _hover;
                    _moveDelta = (0, 0);
                }

                break;
        }

        QueueRedraw();
    }

    private void OnRelease(InputEventMouseButton lb)
    {
        _hover = CellAt(lb.Position, EditZ);
        switch (Tool)
        {
            case MultiTool.Select when _boxStart is { } start:
                var box = new Rect2(start, lb.Position - start).Abs();
                var hit = PartsInBox(box);
                if (!lb.ShiftPressed && !lb.CtrlPressed)
                {
                    _doc.Selection.Clear();
                }

                foreach (int uid in hit)
                {
                    if (lb.CtrlPressed)
                    {
                        _doc.Selection.Remove(uid);
                    }
                    else
                    {
                        _doc.Selection.Add(uid);
                    }
                }

                _boxStart = null;
                SelectionChanged();
                break;
            case MultiTool.Draw or MultiTool.Brush when _drawingNow:
                CommitCells(_paintCells, Tool == MultiTool.Brush ? "brush" : "draw");
                break;
            case MultiTool.Rect or MultiTool.Line when _drawingNow && _dragStart is { } a:
                CommitCells(Tool == MultiTool.Rect ? RectCells(a, _hover.Value, lb.ShiftPressed) : LineCells(a, _hover.Value), Tool == MultiTool.Rect ? "rect fill" : "line");
                break;
            case MultiTool.Erase when _drawingNow:
                if (_eraseSet.Count > 0)
                {
                    _doc.Remove(_eraseSet, $"erase {_eraseSet.Count} component(s)");
                }

                break;
            case MultiTool.Move when _drawingNow && _moveDelta is { } d:
                if (d.Dx != 0 || d.Dy != 0)
                {
                    _doc.Move(_doc.Selection, d.Dx, d.Dy, 0, $"move {_doc.Selection.Count} by {d.Dx},{d.Dy}");
                }

                break;
        }

        _drawingNow = false;
        _dragStart = null;
        _moveDelta = null;
        _paintCells = new List<(int X, int Y)>();
        _eraseSet = new HashSet<int>();
        QueueRedraw();
    }

    private void CommitCells(IReadOnlyCollection<(int X, int Y)> cells, string verb)
    {
        if (TileId == 0)
        {
            Status?.Invoke("pick a tile in the palette first");
            return;
        }

        _doc.PlaceMany($"{verb} 0x{TileId:X4} x{cells.Count} at z {EditZ}", TileId, cells, EditZ, TileHue, ReplaceCell);
    }

    private void SelectionChanged()
    {
        SelectionEdited?.Invoke();
        QueueRedraw();
    }

    public event Action SelectionEdited;

    private bool OnKey(InputEventKey k)
    {
        if (k.CtrlPressed && !k.ShiftPressed)
        {
            switch (k.Keycode)
            {
                case Key.Z:
                    _doc.Undo();
                    return true;
                case Key.Y:
                    _doc.Redo();
                    return true;
                case Key.A:
                    _doc.Selection.Clear();
                    foreach (MultiPart p in _doc.Parts.Where(p => Display(p).Visible))
                    {
                        _doc.Selection.Add(p.Uid);
                    }

                    SelectionChanged();
                    return true;
                case Key.S:
                    SaveRequested?.Invoke();
                    return true;
            }
        }

        if (k.CtrlPressed && k.ShiftPressed && k.Keycode == Key.Z)
        {
            _doc.Redo();
            return true;
        }

        if (k.CtrlPressed)
        {
            return false;
        }

        switch (k.Keycode)
        {
            case Key.S: Tool = MultiTool.Select; break;
            case Key.D: Tool = MultiTool.Draw; break;
            case Key.E: Tool = MultiTool.Erase; break;
            case Key.I: Tool = MultiTool.Pipette; break;
            case Key.R: Tool = MultiTool.Rect; break;
            case Key.L: Tool = MultiTool.Line; break;
            case Key.B: Tool = MultiTool.Brush; break;
            case Key.M: Tool = MultiTool.Move; break;
            case Key.G: ShowGrid = !ShowGrid; break;
            case Key.F: ShowVirtualFloor = !ShowVirtualFloor; break;
            case Key.Home: FitView(); break;
            case Key.Key0 or Key.Key1 or Key.Key2 or Key.Key3 or Key.Key4:
                SetEditZ(k.Keycode == Key.Key0 ? 0 : Stories.ZOf((int)k.Keycode - (int)Key.Key1));
                break;
            case Key.Escape:
                _doc.Selection.Clear();
                _drawingNow = false;
                _boxStart = null;
                SelectionChanged();
                break;
            case Key.Delete or Key.Backspace:
                if (_doc.Selection.Count > 0)
                {
                    _doc.Remove(_doc.Selection.ToList(), $"delete {_doc.Selection.Count} component(s)");
                }

                break;
            case Key.H:
                if (_doc.Selection.Count > 0)
                {
                    bool anyShown = _doc.Parts.Any(p => _doc.Selection.Contains(p.Uid) && p.Shown);
                    _doc.SetShown(_doc.Selection.ToList(), !anyShown);
                }

                break;
            case Key.Bracketleft: NudgeZ(-1); break;
            case Key.Bracketright: NudgeZ(1); break;
            case Key.Pageup: NudgeZ(5); break;
            case Key.Pagedown: NudgeZ(-5); break;
            case Key.Up: Nudge(-1, -1); break;
            case Key.Down: Nudge(1, 1); break;
            case Key.Left: Nudge(-1, 1); break;
            case Key.Right: Nudge(1, -1); break;
            case Key.Plus or Key.Equal: ZoomBy(1); break;
            case Key.Minus: ZoomBy(-1); break;
            default: return false;
        }

        ToolKeyPressed?.Invoke();
        QueueRedraw();
        return true;
    }

    public event Action ToolKeyPressed;
    public event Action SaveRequested;

    public void SetEditZ(int z)
    {
        EditZ = Math.Clamp(z, -128, 127);
        EditZChanged?.Invoke(EditZ);
        QueueRedraw();
    }

    /// <summary>With a selection the keys move it (group z); with none they change the editing z.</summary>
    public void NudgeZ(int dz)
    {
        if (_doc.Selection.Count > 0)
        {
            _doc.Move(_doc.Selection.ToList(), 0, 0, dz, $"z {(dz > 0 ? "+" : "")}{dz} on {_doc.Selection.Count}");
        }
        else
        {
            SetEditZ(EditZ + dz);
        }
    }

    public void Nudge(int dx, int dy)
    {
        if (_doc.Selection.Count > 0)
        {
            _doc.Move(_doc.Selection.ToList(), dx, dy, 0, $"nudge {_doc.Selection.Count} by {dx},{dy}");
        }
        else
        {
            _origin -= new Vector2((dx - dy) * 22f, (dx + dy) * 22f) * Zoom;
            QueueRedraw();
        }
    }

    // --- dropping a tile from the UO Assets panel -------------------------------------------------------

    public override bool _CanDropData(Vector2 atPosition, Variant data) => DropId(data) != null;

    public override void _DropData(Vector2 atPosition, Variant data)
    {
        if (DropId(data) is not ushort id || _doc == null)
        {
            return;
        }

        TileId = id;
        TileDropped?.Invoke(id);
        (int x, int y) = CellAt(atPosition, EditZ);
        _doc.Place(id, x, y, EditZ, TileHue, ReplaceCell);
    }

    /// <summary>The static id a drag from the UO Assets panels carries, or null.</summary>
    public static ushort? DropId(Variant data)
    {
        if (data.VariantType != Variant.Type.Dictionary)
        {
            return null;
        }

        var d = data.AsGodotDictionary();
        return d.ContainsKey("guo_static") ? (ushort)d["guo_static"].AsInt32() : null;
    }

    public override void _ExitTree() => Detach();
}
#endif
