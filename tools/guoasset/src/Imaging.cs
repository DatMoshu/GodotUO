// SPDX-License-Identifier: BSD-2-Clause

using System.Runtime.InteropServices;
using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace GUO.AssetMcp;

/// <summary>
/// Writes decoded art to PNG. Pixels are copied unchanged, never resampled:
/// a reference that was filtered on the way out would hide the very
/// differences a parity check looks for.
/// </summary>
internal static class Imaging
{
    public static void WritePng(string path, Pixels px)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        using var image = ToImage(px);
        image.SaveAsPng(path);
    }

    private static Image<Rgba32> ToImage(Pixels px) =>
        Image.LoadPixelData<Rgba32>(MemoryMarshal.Cast<uint, Rgba32>(px.Data.AsSpan()), px.Width, px.Height);

    public sealed record AtlasResult(string PngPath, string JsonPath, int Count, int Width, int Height);

    /// <summary>
    /// Packs images into one sheet in rows, tallest first, and writes a JSON
    /// manifest beside it: { image, size, format, frames: [{ index, frame, center }] }.
    /// </summary>
    public static AtlasResult Atlas(IReadOnlyList<(int id, Pixels px)> items, string outDir, string name,
        int maxWidth = 2048, int padding = 1)
    {
        Directory.CreateDirectory(outDir);
        var pngPath = System.IO.Path.Combine(outDir, name + ".png");
        var jsonPath = System.IO.Path.Combine(outDir, name + ".json");

        var placed = new List<(int id, Pixels px, int x, int y)>();
        int x = 0, y = 0, rowH = 0, sheetW = 0, sheetH = 0;
        foreach (var (id, px) in items.Where(i => !i.px.IsEmpty).OrderByDescending(i => i.px.Height))
        {
            if (x > 0 && x + px.Width > maxWidth)
            {
                x = 0;
                y += rowH + padding;
                rowH = 0;
            }
            placed.Add((id, px, x, y));
            sheetW = Math.Max(sheetW, x + px.Width);
            sheetH = Math.Max(sheetH, y + px.Height);
            x += px.Width + padding;
            rowH = Math.Max(rowH, px.Height);
        }
        if (placed.Count == 0)
            throw new InvalidOperationException("Nothing to pack: every selected id is empty.");

        using (var sheet = new Image<Rgba32>(sheetW, sheetH))
        {
            foreach (var p in placed)
            {
                using var img = ToImage(p.px);
                sheet.Mutate(c => c.DrawImage(img, new Point(p.x, p.y), 1f));
            }
            sheet.SaveAsPng(pngPath);
        }

        var manifest = new
        {
            image = System.IO.Path.GetFileName(pngPath),
            size = new { w = sheetW, h = sheetH },
            format = "RGBA8888",
            frames = placed.OrderBy(p => p.id).Select(p => new
            {
                index = p.id,
                frame = new { x = p.x, y = p.y, w = p.px.Width, h = p.px.Height },
                center = new { x = p.px.Width / 2, y = p.px.Height / 2 },
            }),
        };
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        return new AtlasResult(pngPath, jsonPath, placed.Count, sheetW, sheetH);
    }

    /// <summary>
    /// Stacks a multi's statics into one isometric picture, in the order the
    /// multi lists them: px = (x - y) * 22 - w / 2, py = (x + y) * 22 - z * 4 - h.
    /// A quick look at a multi's shape, not a draw-order reference; the
    /// client sorts by depth and this does not.
    /// </summary>
    public static (int w, int h) MultiIso(IReadOnlyList<(int x, int y, int z, Pixels px)> parts, string path)
    {
        int xMin = int.MaxValue, yMin = int.MaxValue, xMax = int.MinValue, yMax = int.MinValue;
        foreach (var c in parts)
        {
            int px = (c.x - c.y) * 22 - c.px.Width / 2;
            int py = (c.x + c.y) * 22 - c.z * 4 - c.px.Height;
            xMin = Math.Min(xMin, px);
            yMin = Math.Min(yMin, py);
            xMax = Math.Max(xMax, px + c.px.Width);
            yMax = Math.Max(yMax, py + c.px.Height);
        }
        if (parts.Count == 0 || xMax <= xMin || yMax <= yMin)
            throw new InvalidOperationException("The multi has no drawable parts.");

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        using var canvas = new Image<Rgba32>(xMax - xMin, yMax - yMin);
        foreach (var c in parts)
        {
            using var img = ToImage(c.px);
            var at = new Point((c.x - c.y) * 22 - c.px.Width / 2 - xMin, (c.x + c.y) * 22 - c.z * 4 - c.px.Height - yMin);
            canvas.Mutate(m => m.DrawImage(img, at, 1f));
        }
        canvas.SaveAsPng(path);
        return (canvas.Width, canvas.Height);
    }
}
