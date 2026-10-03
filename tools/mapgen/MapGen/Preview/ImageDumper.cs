using CentrED.MapGen.IR;
using CentrED.MapGen.Paint;

namespace CentrED.MapGen.Preview;

// Dumps the IR's Biome[] and Height_Z[] fields as paintable PNGs:
//   {prefix}.terrain.png   — RGB indexed against ImageImportPass.Palette.Default
//   {prefix}.altitude.png  — 8-bit greyscale, MinZ..MaxZ → 0..255
//
// Round-trips back through ImageImportPass: a painter can edit these PNGs in any
// image tool (snap to the swatch palette) and replay the result as the worldgen
// input. Mirrors DragonMod / UO Landscaper's two-BMP authoring model.
public static class ImageDumper
{
    public static void Dump(GenIR ir, string prefix, int minZ = -10, int maxZ = 80)
    {
        if (ir.Biome is null || ir.Height_Z is null)
            throw new InvalidOperationException("ImageDumper requires Biome and Height_Z to be populated.");

        var scope = ir.Scope;
        int w = scope.Width;
        int h = scope.Height;

        // BiomeId → RGB from the canonical palette (unknown biomes = magenta).
        var rgbByBiome = PaintPngPair.BiomeRgbTable();
        int zRange = Math.Max(1, maxZ - minZ);
        var biome = ir.Biome;
        var height = ir.Height_Z;

        // Shared writer keeps ImageDumper, MapPaint and the paint generators byte-identical.
        PaintPngPair.Write(prefix, w, h, (ly, rgb, alt) =>
        {
            for (int lx = 0; lx < w; lx++)
            {
                int idx = ir.Index(scope.X1 + lx, scope.Y1 + ly);
                var c = rgbByBiome[biome[idx]];
                rgb[lx * 3] = c.R; rgb[lx * 3 + 1] = c.G; rgb[lx * 3 + 2] = c.B;
                int zv = Math.Clamp(height[idx], (sbyte)minZ, (sbyte)maxZ);
                alt[lx] = (byte)Math.Clamp(((zv - minZ) * 255) / zRange, 0, 255);
            }
        });
    }
}
