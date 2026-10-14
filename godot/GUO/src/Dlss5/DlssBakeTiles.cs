// SPDX-License-Identifier: BSD-2-Clause
// PORT DEVIATION (GUO): DLSS5 arbitrary-sprite bake tiling math.
// Pure .NET, no Godot objects: splits any native uint[] sprite (0xAABBGGRR)
// into 320x320 RGB8 tiles with 32px overlap, blends them back with a linear
// ramp, and keeps alpha passthrough (0 stays 0). The neural step itself is
// injected as Func<byte[],byte[]> so this file is unit-testable without the
// engine; DlssBakeWorker supplies the EnhanceStill-backed func.

using System;

namespace GUO.Dlss5
{
    internal static class DlssBakeTiles
    {
        public const int TileSize = 320;
        public const int Overlap = 32;
        public const int Stride = TileSize - Overlap; // 288
        public const int MaxDim = 1024; // mirrors SrQueue's OOM cap

        internal readonly struct Placement
        {
            public readonly int X0;
            public readonly int Y0;
            public readonly bool FirstX;
            public readonly bool LastX;
            public readonly bool FirstY;
            public readonly bool LastY;

            public Placement(int x0, int y0, bool firstX, bool lastX, bool firstY, bool lastY)
            {
                X0 = x0;
                Y0 = y0;
                FirstX = firstX;
                LastX = lastX;
                FirstY = firstY;
                LastY = lastY;
            }
        }

        public static bool IsSizeAllowed(int w, int h)
        {
            return w > 0 && h > 0 && w <= MaxDim && h <= MaxDim;
        }

        /// <summary>
        /// Tile placements covering WxH. Tiny sprites (both dims &lt;= 320)
        /// get one centered tile (negative origin = symmetric pad, cropped
        /// back after enhance; no resampling). Larger sprites step at Stride
        /// with the last tile clamped to the edge (overlap grows there).
        /// </summary>
        public static Placement[] ComputePlacements(int w, int h)
        {
            int nx = w <= TileSize ? 1 : ((w - TileSize + Stride - 1) / Stride + 1);
            int ny = h <= TileSize ? 1 : ((h - TileSize + Stride - 1) / Stride + 1);
            var out_ = new Placement[nx * ny];
            int k = 0;
            for (int j = 0; j < ny; j++)
            {
                for (int i = 0; i < nx; i++)
                {
                    int x0;
                    int y0;
                    if (nx == 1)
                    {
                        x0 = (w - TileSize) / 2; // centered pad for tiny/narrow
                    }
                    else if (i == nx - 1)
                    {
                        x0 = w - TileSize; // clamped last tile
                    }
                    else
                    {
                        x0 = i * Stride;
                    }

                    if (ny == 1)
                    {
                        y0 = (h - TileSize) / 2;
                    }
                    else if (j == ny - 1)
                    {
                        y0 = h - TileSize;
                    }
                    else
                    {
                        y0 = j * Stride;
                    }

                    out_[k++] = new Placement(x0, y0, i == 0, i == nx - 1, j == 0, j == ny - 1);
                }
            }

            return out_;
        }

        /// <summary>uint 0xAABBGGRR to planar RGB8 + binary alpha (0 stays 0).</summary>
        public static void SplitPixels(uint[] pixels, int w, int h, out byte[] rgb, out byte[] alpha)
        {
            rgb = new byte[w * h * 3];
            alpha = new byte[w * h];
            for (int i = 0; i < w * h; i++)
            {
                uint p = pixels[i];
                rgb[i * 3] = (byte)p;
                rgb[i * 3 + 1] = (byte)(p >> 8);
                rgb[i * 3 + 2] = (byte)(p >> 16);
                alpha[i] = (byte)(p >> 24) == 0 ? (byte)0 : (byte)255;
            }
        }

        /// <summary>Copies nearby opaque RGB into transparent texels so the
        /// network does not smear black halos into edges. Alpha untouched.
        /// Same radius-3 search as SrWorker.FillTransparentRgb.</summary>
        public static void FillTransparentRgb(byte[] rgb, byte[] alpha, int w, int h)
        {
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (alpha[y * w + x] != 0)
                    {
                        continue;
                    }

                    bool found = false;
                    for (int r = 1; r <= 3 && !found; r++)
                    {
                        for (int dy = -r; dy <= r && !found; dy++)
                        {
                            for (int dx = -r; dx <= r && !found; dx++)
                            {
                                if (dx == 0 && dy == 0)
                                {
                                    continue;
                                }

                                int nx = x + dx;
                                int ny = y + dy;
                                if (nx < 0 || ny < 0 || nx >= w || ny >= h)
                                {
                                    continue;
                                }

                                if (alpha[ny * w + nx] != 0)
                                {
                                    rgb[(y * w + x) * 3] = rgb[(ny * w + nx) * 3];
                                    rgb[(y * w + x) * 3 + 1] = rgb[(ny * w + nx) * 3 + 1];
                                    rgb[(y * w + x) * 3 + 2] = rgb[(ny * w + nx) * 3 + 2];
                                    found = true;
                                }
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// One PrepareSquare320-compatible RGB8 tile: same byte layout the
        /// still network runs at (R,G,B order, 0-255, no resize here — the
        /// tile is already 320x320). Out-of-sprite samples edge-replicate
        /// (clamp), so tiny-sprite pad and tile borders never see black.
        /// </summary>
        public static byte[] ExtractTile(byte[] rgb, int w, int h, Placement p)
        {
            var tile = new byte[TileSize * TileSize * 3];
            for (int dy = 0; dy < TileSize; dy++)
            {
                int y = p.Y0 + dy;
                int sy = y < 0 ? 0 : (y >= h ? h - 1 : y);
                for (int dx = 0; dx < TileSize; dx++)
                {
                    int x = p.X0 + dx;
                    int sx = x < 0 ? 0 : (x >= w ? w - 1 : x);
                    int si = (sy * w + sx) * 3;
                    int di = (dy * TileSize + dx) * 3;
                    tile[di] = rgb[si];
                    tile[di + 1] = rgb[si + 1];
                    tile[di + 2] = rgb[si + 2];
                }
            }

            return tile;
        }

        internal static float BlendWeight(int dx, int dy, Placement p)
        {
            float wx = 1f;
            float wy = 1f;
            float denom = Overlap - 1f; // 31: ramp 0 at the border, 1 inside
            if (!(p.FirstX && p.LastX))
            {
                if (!p.FirstX && dx < Overlap)
                {
                    wx = Math.Min(wx, dx / denom);
                }

                if (!p.LastX && dx >= TileSize - Overlap)
                {
                    wx = Math.Min(wx, (TileSize - 1 - dx) / denom);
                }
            }

            if (!(p.FirstY && p.LastY))
            {
                if (!p.FirstY && dy < Overlap)
                {
                    wy = Math.Min(wy, dy / denom);
                }

                if (!p.LastY && dy >= TileSize - Overlap)
                {
                    wy = Math.Min(wy, (TileSize - 1 - dy) / denom);
                }
            }

            return wx * wy;
        }

        /// <summary>Adds one enhanced tile into the float accumulators.</summary>
        public static void Accumulate(float[] accum, float[] weights, int w, int h,
            byte[] enhancedTile, Placement p)
        {
            for (int dy = 0; dy < TileSize; dy++)
            {
                int y = p.Y0 + dy;
                if (y < 0 || y >= h)
                {
                    continue;
                }

                for (int dx = 0; dx < TileSize; dx++)
                {
                    int x = p.X0 + dx;
                    if (x < 0 || x >= w)
                    {
                        continue;
                    }

                    float wt = BlendWeight(dx, dy, p);
                    int ai = (y * w + x) * 3;
                    int ti = (dy * TileSize + dx) * 3;
                    accum[ai] += enhancedTile[ti] * wt;
                    accum[ai + 1] += enhancedTile[ti + 1] * wt;
                    accum[ai + 2] += enhancedTile[ti + 2] * wt;
                    weights[y * w + x] += wt;
                }
            }
        }

        /// <summary>Accumulators to clamped RGB8. Zero-weight texels (cannot
        /// happen: placements cover the image) fall back to the filled base.</summary>
        public static byte[] Normalize(float[] accum, float[] weights, int w, int h, byte[] fallbackRgb)
        {
            var out_ = new byte[w * h * 3];
            for (int i = 0; i < w * h; i++)
            {
                float wt = weights[i];
                if (wt > 1e-6f)
                {
                    out_[i * 3] = (byte)Math.Clamp((int)MathF.Round(accum[i * 3] / wt), 0, 255);
                    out_[i * 3 + 1] = (byte)Math.Clamp((int)MathF.Round(accum[i * 3 + 1] / wt), 0, 255);
                    out_[i * 3 + 2] = (byte)Math.Clamp((int)MathF.Round(accum[i * 3 + 2] / wt), 0, 255);
                }
                else if (fallbackRgb != null && fallbackRgb.Length >= (i + 1) * 3)
                {
                    out_[i * 3] = fallbackRgb[i * 3];
                    out_[i * 3 + 1] = fallbackRgb[i * 3 + 1];
                    out_[i * 3 + 2] = fallbackRgb[i * 3 + 2];
                }
            }

            return out_;
        }

        /// <summary>RGB8 + binary alpha to RGBA8. Alpha passthrough: 0 stays 0.</summary>
        public static byte[] InterleaveRgba(byte[] rgb, byte[] alpha, int w, int h)
        {
            var rgba = new byte[w * h * 4];
            for (int i = 0; i < w * h; i++)
            {
                rgba[i * 4] = rgb[i * 3];
                rgba[i * 4 + 1] = rgb[i * 3 + 1];
                rgba[i * 4 + 2] = rgb[i * 3 + 2];
                rgba[i * 4 + 3] = alpha[i];
            }

            return rgba;
        }

        /// <summary>
        /// Full stitch without any neural call: extracts every tile, runs
        /// <paramref name="enhanceTile"/> (320*320*3 RGB8 in, same out; null
        /// return = passthrough of that tile's input), blends with the linear
        /// ramp. Returns full-res RGB8. No Godot objects.
        /// </summary>
        public static byte[] Stitch(int w, int h, byte[] filledRgb, Func<byte[], byte[]> enhanceTile)
        {
            Placement[] placements = ComputePlacements(w, h);
            var accum = new float[w * h * 3];
            var weights = new float[w * h];
            foreach (Placement p in placements)
            {
                byte[] tileIn = ExtractTile(filledRgb, w, h, p);
                byte[] tileOut = null;
                try
                {
                    tileOut = enhanceTile(tileIn);
                }
                catch
                {
                    tileOut = null;
                }

                if (tileOut == null || tileOut.Length < TileSize * TileSize * 3)
                {
                    tileOut = tileIn; // passthrough fallback for this tile
                }

                Accumulate(accum, weights, w, h, tileOut, p);
            }

            return Normalize(accum, weights, w, h, filledRgb);
        }
    }
}
