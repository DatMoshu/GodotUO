namespace CentrED.MapGen.Paint;

/// <summary>
/// In-memory paint canvas used by the procedural generators: one palette index
/// (into <see cref="PaintPngPair.Palette"/>) and one altitude shade per pixel.
/// Drawing helpers mirror the System.Drawing calls the old PowerShell painters used
/// (FillRectangle / FillEllipse with SmoothingMode.None / round-capped lines).
/// </summary>
public sealed class PaintRaster
{
    public int Width { get; }
    public int Height { get; }
    /// <summary>Palette index per pixel (row-major).</summary>
    public byte[] Terrain { get; }
    /// <summary>Altitude shade per pixel (row-major).</summary>
    public byte[] Altitude { get; }

    public PaintRaster(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "size must be positive");
        Width = width;
        Height = height;
        Terrain = new byte[width * height];
        Altitude = new byte[width * height];
    }

    /// <summary>Palette index for swatch <paramref name="name"/>; throws for unknown names.</summary>
    public static byte Swatch(string name)
    {
        int i = PaintPngPair.IndexOf(name);
        if (i < 0) throw new ArgumentException($"no palette swatch named '{name}'", nameof(name));
        return (byte)i;
    }

    public void Set(int x, int y, byte swatch, byte shade)
    {
        int i = y * Width + x;
        Terrain[i] = swatch;
        Altitude[i] = shade;
    }

    public void SetTerrain(int x, int y, byte swatch) => Terrain[y * Width + x] = swatch;
    public void SetAltitude(int x, int y, byte shade) => Altitude[y * Width + x] = shade;

    public void Fill(byte swatch, byte shade)
    {
        Array.Fill(Terrain, swatch);
        Array.Fill(Altitude, shade);
    }

    /// <summary>Axis-aligned rectangle (clipped).</summary>
    public void FillRect(int x, int y, int w, int h, byte? swatch, byte? shade)
    {
        int x0 = Math.Max(0, x), y0 = Math.Max(0, y);
        int x1 = Math.Min(Width, x + w), y1 = Math.Min(Height, y + h);
        for (int yy = y0; yy < y1; yy++)
        for (int xx = x0; xx < x1; xx++)
        {
            int i = yy * Width + xx;
            if (swatch is { } s) Terrain[i] = s;
            if (shade is { } a) Altitude[i] = a;
        }
    }

    /// <summary>
    /// GDI-style FillEllipse of the circle's bounding rectangle
    /// <c>Rectangle([int](cx-r), [int](cy-r), [int](2r), [int](2r))</c> — PowerShell's [int]
    /// cast rounds half-to-even, reproduced here. Pixel centres inside the ellipse are filled.
    /// An optional clip rectangle (x, y, w, h) restricts the fill (Graphics.SetClip).
    /// </summary>
    public void FillCircle(double cx, double cy, double r, byte? swatch, byte? shade,
        (int X, int Y, int W, int H)? clip = null)
    {
        int rx = PsInt(cx - r), ry = PsInt(cy - r), rw = PsInt(r * 2), rh = rw;
        if (rw <= 0) return;
        double ex = rx + rw / 2.0, ey = ry + rh / 2.0, ax = rw / 2.0, ay = rh / 2.0;
        int x0 = Math.Max(0, rx), y0 = Math.Max(0, ry);
        int x1 = Math.Min(Width - 1, rx + rw), y1 = Math.Min(Height - 1, ry + rh);
        if (clip is { } c)
        {
            x0 = Math.Max(x0, c.X); y0 = Math.Max(y0, c.Y);
            x1 = Math.Min(x1, c.X + c.W - 1); y1 = Math.Min(y1, c.Y + c.H - 1);
        }
        for (int y = y0; y <= y1; y++)
        {
            double dy = (y + 0.5 - ey) / ay;
            for (int x = x0; x <= x1; x++)
            {
                double dx = (x + 0.5 - ex) / ax;
                if (dx * dx + dy * dy > 1.0) continue;
                int i = y * Width + x;
                if (swatch is { } s) Terrain[i] = s;
                if (shade is { } a) Altitude[i] = a;
            }
        }
    }

    /// <summary>Thick line with round caps (GDI Pen width 2*halfWidth, LineCap.Round).</summary>
    public void FillCapsule(double ax, double ay, double bx, double by, double halfWidth, byte? swatch, byte? shade)
    {
        int x0 = Math.Max(0, (int)Math.Floor(Math.Min(ax, bx) - halfWidth));
        int y0 = Math.Max(0, (int)Math.Floor(Math.Min(ay, by) - halfWidth));
        int x1 = Math.Min(Width - 1, (int)Math.Ceiling(Math.Max(ax, bx) + halfWidth));
        int y1 = Math.Min(Height - 1, (int)Math.Ceiling(Math.Max(ay, by) + halfWidth));
        double vx = bx - ax, vy = by - ay, len2 = vx * vx + vy * vy;
        double hw2 = halfWidth * halfWidth;
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            double px = x + 0.5 - ax, py = y + 0.5 - ay;
            double t = len2 > 0 ? Math.Clamp((px * vx + py * vy) / len2, 0, 1) : 0;
            double dx = px - t * vx, dy = py - t * vy;
            if (dx * dx + dy * dy > hw2) continue;
            int i = y * Width + x;
            if (swatch is { } s) Terrain[i] = s;
            if (shade is { } a) Altitude[i] = a;
        }
    }

    /// <summary>Writes {prefix}.terrain.png + {prefix}.altitude.png through <see cref="PaintPngPair"/>.</summary>
    public void Save(string prefix)
    {
        var palette = PaintPngPair.Palette;
        PaintPngPair.Write(prefix, Width, Height, (y, rgb, alt) =>
        {
            int row = y * Width;
            for (int x = 0; x < Width; x++)
            {
                var e = palette[Terrain[row + x]];
                rgb[x * 3] = e.R; rgb[x * 3 + 1] = e.G; rgb[x * 3 + 2] = e.B;
                alt[x] = Altitude[row + x];
            }
        });
    }

    /// <summary>PowerShell's [int] cast of a double: round half to even.</summary>
    public static int PsInt(double v) => (int)Math.Round(v, MidpointRounding.ToEven);
}
