// SPDX-License-Identifier: BSD-2-Clause

using Godot;

namespace GUO.Host;

/// <summary>
/// Prove ExtractDecal without a shard or a window (--extract-test IN OUT
/// DEST): loads two PNGs, subtracts the capture from the repaint with the
/// given tolerance/feather, saves the decal, and prints opaque-pixel stats.
/// </summary>
internal static class ExtractTest
{
    public static bool Passed { get; private set; }

    public static void Run(string inPath, string outPath, string destPath, float tolerance, float feather)
    {
        Passed = false;
        try
        {
            using var input = Image.LoadFromFile(inPath);
            using var output = Image.LoadFromFile(outPath);
            if (input == null || input.IsEmpty() || output == null || output.IsEmpty())
            {
                GD.Print("[GUO] extract test: could not load inputs");
                return;
            }

            if (input.GetWidth() != output.GetWidth() || input.GetHeight() != output.GetHeight())
            {
                GD.Print("[GUO] extract test: size mismatch "
                    + $"{input.GetWidth()}x{input.GetHeight()} vs {output.GetWidth()}x{output.GetHeight()}");
                return;
            }

            using var decal = GUO.Renderer.TerrainLayers.ExtractDecal(input, output, tolerance, feather);
            long opaque = 0, total = 0;
            double alphaSum = 0;
            for (int y = 0; y < decal.GetHeight(); y++)
            {
                for (int x = 0; x < decal.GetWidth(); x++)
                {
                    float a = decal.GetPixel(x, y).A;
                    total++;
                    alphaSum += a;
                    if (a >= 0.99f)
                    {
                        opaque++;
                    }
                }
            }

            if (decal.SavePng(destPath) != Error.Ok)
            {
                GD.Print($"[GUO] extract test: could not write {destPath}");
                return;
            }

            GD.Print($"[GUO] extract test: {total} px, {opaque} opaque, mean alpha {alphaSum / total:0.000} -> {destPath}");
            Passed = opaque > 0 && opaque < total;
        }
        catch (System.Exception ex)
        {
            GD.Print($"[GUO] extract test: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
