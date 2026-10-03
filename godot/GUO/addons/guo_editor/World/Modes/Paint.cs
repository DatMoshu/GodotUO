#if TOOLS
namespace GUO.Editor;

using System;
using System.Collections.Generic;
using Godot;

/// <summary>
/// What a render mode or a map layer draws with. Two implementations: the
/// Godot canvas (the World tab, the minimap) and a CPU image (the scene pack's
/// mode images, which therefore also render with no display). The same mode
/// code draws on both, so a frame written to disk is what the editor shows.
/// </summary>
internal interface IPaint
{
    /// <summary>A filled four point polygon, corners in order around it.</summary>
    void Quad(Vector2 a, Vector2 b, Vector2 c, Vector2 d, Color colour);

    void Line(Vector2 a, Vector2 b, Color colour, float width = 1f);

    /// <summary>A label with its baseline-left corner at <paramref name="at"/>.</summary>
    void Text(Vector2 at, string text, Color colour, int size = 10);

    void Circle(Vector2 at, float radius, Color colour, bool filled = true);
}

/// <summary>Draws onto a Godot <see cref="CanvasItem"/> inside its own draw callback.</summary>
internal sealed class CanvasPaint : IPaint
{
    private static readonly Vector2[] Pts = new Vector2[4];
    private static readonly Color[] Cols = new Color[4];

    private readonly CanvasItem _node;
    private readonly Font _font;

    public CanvasPaint(CanvasItem node)
    {
        _node = node;
        _font = ThemeDB.FallbackFont;
    }

    public void Quad(Vector2 a, Vector2 b, Vector2 c, Vector2 d, Color colour)
    {
        Pts[0] = a;
        Pts[1] = b;
        Pts[2] = c;
        Pts[3] = d;
        Cols[0] = Cols[1] = Cols[2] = Cols[3] = colour;
        _node.DrawPrimitive(Pts, Cols, Array.Empty<Vector2>());
    }

    public void Line(Vector2 a, Vector2 b, Color colour, float width = 1f) => _node.DrawLine(a, b, colour, width);

    public void Text(Vector2 at, string text, Color colour, int size = 10)
    {
        // A dark shadow keeps a label readable over any art.
        _node.DrawString(_font, at + new Vector2(1, 1), text, HorizontalAlignment.Left, -1, size, new Color(0, 0, 0, 0.85f));
        _node.DrawString(_font, at, text, HorizontalAlignment.Left, -1, size, colour);
    }

    public void Circle(Vector2 at, float radius, Color colour, bool filled = true)
    {
        if (filled)
        {
            _node.DrawCircle(at, radius, colour);
        }
        else
        {
            _node.DrawArc(at, radius, 0, Mathf.Tau, 24, colour, 1.5f);
        }
    }
}

/// <summary>
/// Draws onto a CPU RGBA buffer, alpha blended over what is there. Text is
/// not rasterised; it is kept in <see cref="Labels"/> so a scene pack can
/// list it with its position.
/// </summary>
internal sealed class ImagePaint : IPaint
{
    private readonly byte[] _px;

    public int Width { get; }
    public int Height { get; }

    /// <summary>Labels drawn, in order: text and its viewport position.</summary>
    public List<(string Text, Vector2 At)> Labels { get; } = new();

    public ImagePaint(int width, int height, Image background = null, Color? fill = null)
    {
        Width = width;
        Height = height;
        if (background != null && background.GetWidth() == width && background.GetHeight() == height)
        {
            Image copy = (Image)background.Duplicate();
            copy.Convert(Image.Format.Rgba8);
            _px = copy.GetData();
        }
        else
        {
            _px = new byte[width * height * 4];
            Color c = fill ?? new Color(0.06f, 0.06f, 0.07f);
            for (int i = 0; i < _px.Length; i += 4)
            {
                _px[i] = (byte)(c.R * 255);
                _px[i + 1] = (byte)(c.G * 255);
                _px[i + 2] = (byte)(c.B * 255);
                _px[i + 3] = 255;
            }
        }
    }

    public Image ToImage() => Image.CreateFromData(Width, Height, false, Image.Format.Rgba8, _px);

    /// <summary>The colour at a pixel, for the smoke check.</summary>
    public Color PixelAt(int x, int y)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height)
        {
            return new Color(0, 0, 0, 0);
        }

        int i = (y * Width + x) * 4;
        return new Color(_px[i] / 255f, _px[i + 1] / 255f, _px[i + 2] / 255f, _px[i + 3] / 255f);
    }

    private void Blend(int x, int y, Color c)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height)
        {
            return;
        }

        int i = (y * Width + x) * 4;
        float a = c.A;
        if (a >= 0.999f)
        {
            _px[i] = (byte)(c.R * 255);
            _px[i + 1] = (byte)(c.G * 255);
            _px[i + 2] = (byte)(c.B * 255);
            _px[i + 3] = 255;
            return;
        }

        _px[i] = (byte)(_px[i] * (1 - a) + c.R * 255 * a);
        _px[i + 1] = (byte)(_px[i + 1] * (1 - a) + c.G * 255 * a);
        _px[i + 2] = (byte)(_px[i + 2] * (1 - a) + c.B * 255 * a);
        _px[i + 3] = 255;
    }

    public void Quad(Vector2 a, Vector2 b, Vector2 c, Vector2 d, Color colour)
    {
        Vector2[] p = { a, b, c, d };
        int y0 = Math.Max(0, (int)MathF.Floor(Math.Min(Math.Min(a.Y, b.Y), Math.Min(c.Y, d.Y))));
        int y1 = Math.Min(Height - 1, (int)MathF.Ceiling(Math.Max(Math.Max(a.Y, b.Y), Math.Max(c.Y, d.Y))));
        for (int y = y0; y <= y1; y++)
        {
            float sy = y + 0.5f, lo = float.MaxValue, hi = float.MinValue;
            for (int e = 0; e < 4; e++)
            {
                Vector2 p0 = p[e], p1 = p[(e + 1) & 3];
                if ((p0.Y <= sy && p1.Y > sy) || (p1.Y <= sy && p0.Y > sy))
                {
                    float x = p0.X + (sy - p0.Y) / (p1.Y - p0.Y) * (p1.X - p0.X);
                    lo = Math.Min(lo, x);
                    hi = Math.Max(hi, x);
                }
            }

            if (lo > hi)
            {
                continue;
            }

            int xa = Math.Max(0, (int)MathF.Ceiling(lo - 0.5f)), xb = Math.Min(Width - 1, (int)MathF.Floor(hi - 0.5f));
            for (int x = xa; x <= xb; x++)
            {
                Blend(x, y, colour);
            }
        }
    }

    public void Line(Vector2 a, Vector2 b, Color colour, float width = 1f)
    {
        int x0 = (int)MathF.Round(a.X), y0 = (int)MathF.Round(a.Y), x1 = (int)MathF.Round(b.X), y1 = (int)MathF.Round(b.Y);
        int dx = Math.Abs(x1 - x0), dy = -Math.Abs(y1 - y0), sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1, err = dx + dy;
        int r = Math.Max(0, (int)(width / 2f));
        for (int guard = 0; guard < 20000; guard++)
        {
            for (int ox = -r; ox <= r; ox++)
            {
                for (int oy = -r; oy <= r; oy++)
                {
                    Blend(x0 + ox, y0 + oy, colour);
                }
            }

            if (x0 == x1 && y0 == y1)
            {
                break;
            }

            int e2 = 2 * err;
            if (e2 >= dy)
            {
                err += dy;
                x0 += sx;
            }

            if (e2 <= dx)
            {
                err += dx;
                y0 += sy;
            }
        }
    }

    public void Text(Vector2 at, string text, Color colour, int size = 10) => Labels.Add((text, at));

    public void Circle(Vector2 at, float radius, Color colour, bool filled = true)
    {
        if (filled)
        {
            int r = (int)MathF.Ceiling(radius);
            for (int y = -r; y <= r; y++)
            {
                for (int x = -r; x <= r; x++)
                {
                    if (x * x + y * y <= radius * radius)
                    {
                        Blend((int)MathF.Round(at.X) + x, (int)MathF.Round(at.Y) + y, colour);
                    }
                }
            }

            return;
        }

        Vector2 prev = at + new Vector2(radius, 0);
        for (int i = 1; i <= 24; i++)
        {
            float t = i * Mathf.Tau / 24;
            Vector2 next = at + new Vector2(MathF.Cos(t), MathF.Sin(t)) * radius;
            Line(prev, next, colour);
            prev = next;
        }
    }
}
#endif
