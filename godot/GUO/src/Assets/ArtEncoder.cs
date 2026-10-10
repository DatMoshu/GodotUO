// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.IO;

namespace GUO.Assets
{
    /// <summary>
    /// RGBA pixels into raw static-art archive bytes (what ArtLoader.Runs
    /// reads): flags, size, row table, gap/run/pixels rows. The repaint gump
    /// writes these into the override folder; the install is never touched.
    ///
    /// Two one-bit realities: alpha thresholds at 128, and a pixel that
    /// quantizes to zero reads back as transparent, so pure black is stored
    /// as the darkest blue instead (visually identical, loads opaque).
    /// </summary>
    internal static class ArtEncoder
    {
        public static byte[] EncodeStatic(byte[] rgba, int width, int height)
        {
            if (rgba == null || rgba.Length < width * height * 4 || width <= 0 || height <= 0)
            {
                throw new ArgumentException("bad pixels for EncodeStatic");
            }

            if (width > 1024 || height > 1024)
            {
                throw new ArgumentException("art larger than 1024 in either axis is refused");
            }

            var table = new ushort[height];
            var body = new List<ushort>();
            for (int y = 0; y < height; y++)
            {
                // Table counts WORDS from its own end; body is appended in
                // ushort units, so each entry is the word count so far.
                table[y] = (ushort)body.Count;
                int x = 0;
                while (x < width)
                {
                    int gap = 0;
                    while (x < width && IsGap(rgba, y * width + x))
                    {
                        gap++;
                        x++;
                    }

                    int run = 0;
                    var pixels = new List<ushort>();
                    while (x < width && !IsGap(rgba, y * width + x))
                    {
                        pixels.Add(Quantize(rgba, y * width + x));
                        run++;
                        x++;
                    }

                    body.Add((ushort)gap);
                    body.Add((ushort)run);
                    body.AddRange(pixels);
                }

                // Exactly one terminator per row: the reader advances a row
                // on each (0,0), so two would eat the next row.
                body.Add(0);
                body.Add(0);

                if (body.Count > 65000)
                {
                    throw new ArgumentException("art too noisy to encode (row table overflow)");
                }
            }

            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write(0u);
            writer.Write((short)width);
            writer.Write((short)height);
            foreach (ushort offset in table)
            {
                writer.Write(offset);
            }

            foreach (ushort word in body)
            {
                writer.Write(word);
            }

            return stream.ToArray();
        }

        private static bool IsGap(byte[] rgba, int pixel)
        {
            int at = pixel * 4;
            if (rgba[at + 3] < 128)
            {
                return true;
            }

            int r = rgba[at] >> 3, g = rgba[at + 1] >> 3, b = rgba[at + 2] >> 3;
            return (r << 10 | g << 5 | b) == 0;
        }

        private static ushort Quantize(byte[] rgba, int pixel)
        {
            int at = pixel * 4;
            int q = (rgba[at] >> 3) << 10 | (rgba[at + 1] >> 3) << 5 | (rgba[at + 2] >> 3);
            return (ushort)(q == 0 ? 1 : q);
        }
    }
}
