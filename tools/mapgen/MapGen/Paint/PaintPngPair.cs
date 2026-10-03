using CentrED.MapGen.IR;
using CentrED.MapGen.Passes.Terrain;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace CentrED.MapGen.Paint;

/// <summary>Decoded paint pair: RGB terrain (3 bytes/pixel) + altitude shades (1 byte/pixel).</summary>
public sealed class PaintPngData
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    /// <summary>R,G,B per pixel, row-major, length Width*Height*3.</summary>
    public required byte[] TerrainRgb { get; init; }
    /// <summary>Altitude shade per pixel, row-major. Null when the altitude PNG is missing.</summary>
    public byte[]? Altitude { get; init; }
}

/// <summary>
/// The single reader/writer of the paint PNG pair consumed by ImageImportPass:
///   {prefix}.terrain.png  — Rgba32 PNG, one palette colour per biome (alpha 255)
///   {prefix}.altitude.png — L8 PNG, 0..255 shade mapped to MinZ..MaxZ
/// Used by ImageDumper (IR → PNGs), MapPaint (document save/load) and the procedural
/// paint generators, so all three stay byte-identical. Also hosts the palette helpers
/// (ImageImportPass.Palette.Default is the single source of swatch colours).
/// </summary>
public static class PaintPngPair
{
    /// <summary>Colour written for pixels that have no biome (unset / unknown).</summary>
    public static readonly (byte R, byte G, byte B) UnsetRgb = (255, 0, 255);

    /// <summary>Fills one row: <paramref name="terrainRgb"/> is Width*3 bytes, <paramref name="altitude"/> Width bytes.</summary>
    public delegate void RowFiller(int y, Span<byte> terrainRgb, Span<byte> altitude);

    public static string TerrainPath(string prefix) => prefix + ".terrain.png";
    public static string AltitudePath(string prefix) => prefix + ".altitude.png";

    /// <summary>True when the terrain PNG for <paramref name="prefix"/> exists (altitude is optional on read).</summary>
    public static bool Exists(string prefix) => File.Exists(TerrainPath(prefix));

    /// <summary>Writes both PNGs row by row (no full-size intermediate buffers beyond the images).</summary>
    public static void Write(string prefix, int width, int height, RowFiller fill)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "image size must be positive");
        var dir = Path.GetDirectoryName(prefix);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var terrainImg = new Image<Rgba32>(width, height);
        using var altitudeImg = new Image<L8>(width, height);
        var rgbRow = new byte[width * 3];
        var altRow = new byte[width];

        for (int y = 0; y < height; y++)
        {
            fill(y, rgbRow, altRow);
            var tRow = terrainImg.DangerousGetPixelRowMemory(y).Span;
            var aRow = altitudeImg.DangerousGetPixelRowMemory(y).Span;
            for (int x = 0; x < width; x++)
            {
                int i = x * 3;
                tRow[x] = new Rgba32(rgbRow[i], rgbRow[i + 1], rgbRow[i + 2], 255);
                aRow[x] = new L8(altRow[x]);
            }
        }

        terrainImg.Save(TerrainPath(prefix), new PngEncoder());
        altitudeImg.Save(AltitudePath(prefix), new PngEncoder());
    }

    /// <summary>Writes both PNGs from full buffers (RGB 3 bytes/pixel + 1 shade byte/pixel).</summary>
    public static void Write(string prefix, int width, int height, byte[] terrainRgb, byte[] altitude)
    {
        if (terrainRgb.Length < width * height * 3) throw new ArgumentException("terrain buffer too small", nameof(terrainRgb));
        if (altitude.Length < width * height) throw new ArgumentException("altitude buffer too small", nameof(altitude));
        Write(prefix, width, height, (y, rgb, alt) =>
        {
            terrainRgb.AsSpan(y * width * 3, width * 3).CopyTo(rgb);
            altitude.AsSpan(y * width, width).CopyTo(alt);
        });
    }

    /// <summary>
    /// Reads the pair. Throws <see cref="FileNotFoundException"/> when terrain.png is missing.
    /// A missing altitude.png yields <see cref="PaintPngData.Altitude"/> = null; a smaller one is
    /// read into the overlapping area (rest 0). Any input pixel format is accepted.
    /// </summary>
    public static PaintPngData Read(string prefix)
    {
        var terrainPath = TerrainPath(prefix);
        if (!File.Exists(terrainPath)) throw new FileNotFoundException("terrain.png missing", terrainPath);

        using var terrain = Image.Load<Rgba32>(terrainPath);
        int w = terrain.Width, h = terrain.Height;
        var rgb = new byte[w * h * 3];
        terrain.ProcessPixelRows(acc =>
        {
            for (int y = 0; y < h; y++)
            {
                var row = acc.GetRowSpan(y);
                int o = y * w * 3;
                for (int x = 0; x < w; x++)
                {
                    rgb[o + x * 3] = row[x].R;
                    rgb[o + x * 3 + 1] = row[x].G;
                    rgb[o + x * 3 + 2] = row[x].B;
                }
            }
        });

        byte[]? alt = null;
        var altitudePath = AltitudePath(prefix);
        if (File.Exists(altitudePath))
        {
            using var altitude = Image.Load<L8>(altitudePath);
            alt = new byte[w * h];
            int aw = Math.Min(w, altitude.Width), ah = Math.Min(h, altitude.Height);
            altitude.ProcessPixelRows(acc =>
            {
                for (int y = 0; y < ah; y++)
                {
                    var row = acc.GetRowSpan(y);
                    for (int x = 0; x < aw; x++) alt[y * w + x] = row[x].PackedValue;
                }
            });
        }

        return new PaintPngData { Width = w, Height = h, TerrainRgb = rgb, Altitude = alt };
    }

    // =====================================================================
    // Palette helpers (single source: ImageImportPass.Palette.Default)
    // =====================================================================

    public static IReadOnlyList<ImageImportPass.PaletteEntry> Palette => ImageImportPass.Palette.Default.Entries;

    /// <summary>Palette index of the swatch named <paramref name="name"/> (case-insensitive), or -1.</summary>
    public static int IndexOf(string name)
    {
        var entries = Palette;
        for (int i = 0; i < entries.Count; i++)
            if (string.Equals(entries[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>RGB of the swatch named <paramref name="name"/>. Throws for an unknown name.</summary>
    public static (byte R, byte G, byte B) Rgb(string name)
    {
        int i = IndexOf(name);
        if (i < 0) throw new ArgumentException($"no palette swatch named '{name}'", nameof(name));
        var e = Palette[i];
        return (e.R, e.G, e.B);
    }

    /// <summary>RGB written for <paramref name="biome"/> (same rule as <see cref="BiomeRgbTable"/>: last swatch wins), or <see cref="UnsetRgb"/>.</summary>
    public static (byte R, byte G, byte B) Rgb(BiomeId biome) => BiomeRgbTable()[(byte)biome];

    /// <summary>BiomeId (as byte) → RGB lookup table; unassigned slots are <see cref="UnsetRgb"/>.</summary>
    public static (byte R, byte G, byte B)[] BiomeRgbTable()
    {
        var table = new (byte R, byte G, byte B)[256];
        Array.Fill(table, UnsetRgb);
        foreach (var e in Palette) table[(byte)e.Biome] = (e.R, e.G, e.B);
        return table;
    }

    /// <summary>
    /// Nearest palette index by squared RGB distance; -1 when the nearest swatch is further than
    /// 64²·3 (mirrors ImageImportPass.Palette.Match).
    /// </summary>
    public static int SnapToIndex(byte r, byte g, byte b)
    {
        var entries = Palette;
        int best = -1, bestD = int.MaxValue;
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            int dr = e.R - r, dg = e.G - g, db = e.B - b;
            int d = dr * dr + dg * dg + db * db;
            if (d < bestD) { bestD = d; best = i; }
        }
        return best < 0 || bestD > 64 * 64 * 3 ? -1 : best;
    }

    /// <summary>Altitude shade (0..255) → Z, round-to-nearest (same as ImageImportPass).</summary>
    public static int ShadeToZ(byte shade, int minZ, int maxZ)
    {
        int range = maxZ - minZ;
        return minZ + ((range * shade) + 128) / 255;
    }

    /// <summary>Z → altitude shade, round-to-nearest, clamped.</summary>
    public static byte ZToShade(int z, int minZ, int maxZ)
    {
        int range = maxZ - minZ;
        if (range <= 0) return 0;
        int shade = ((z - minZ) * 255 + range / 2) / range;
        return (byte)Math.Clamp(shade, 0, 255);
    }
}
