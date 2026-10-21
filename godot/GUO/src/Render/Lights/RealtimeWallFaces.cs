// SPDX-License-Identifier: BSD-2-Clause
// GUO-owned (ADR-0039): which wall line a wall's art stands on, for Realtime lights.

using System;
using System.Collections.Generic;

namespace GUO.Renderer.Lights
{
    /// <summary>
    /// A wall's visible faces, read from its art: the bottom of its silhouette
    /// is the base of the face the camera sees. A wall running along X draws
    /// as "\" and shows its +Y (south) face; one running along Y draws as "/"
    /// and shows its +X (east) face; a corner piece shows one of each, split
    /// at a column. Tiledata says a static is a wall, never which way it runs.
    /// </summary>
    internal readonly struct WallFaces
    {
        /// <summary>One face over art columns [From, To).</summary>
        public readonly struct Face
        {
            /// <summary>True: runs along X ("\", face +Y). False: runs along Y ("/", face +X).</summary>
            public readonly bool AlongX;

            /// <summary>The wall line's place on its tile: world Y = y + Offset (along X) or X = x + Offset (along Y).</summary>
            public readonly float Offset;

            /// <summary>The base line in tile-box pixels: Y = X + K (along X) or Y = -X + K (along Y).</summary>
            public readonly float K;

            public readonly int From, To;

            public Face(bool alongX, float k, int from, int to)
            {
                AlongX = alongX;
                K = k;
                // Box coordinates: a tile's diamond is top (22,0), right (44,22),
                // bottom (22,44), left (0,22). Y - X = 44 dy - 22 on a line of
                // constant world y; Y + X = 44 dx + 22 on one of constant x.
                Offset = Math.Clamp(alongX ? (k + 22f) / 44f : (k - 22f) / 44f, 0f, 1f);
                From = from;
                To = to;
            }
        }

        public readonly Face First, Second;
        public readonly int Count;

        public WallFaces(Face first)
        {
            First = first;
            Second = default;
            Count = 1;
        }

        public WallFaces(Face first, Face second)
        {
            First = first;
            Second = second;
            Count = 2;
        }

        public bool IsWall => Count > 0;

        /// <summary>Mean distance in pixels a fit may stray from the silhouette and still be a wall face.</summary>
        public const float MaxResidual = 2.5f;

        /// <summary>Fewest columns a face spans.</summary>
        public const int MinColumns = 6;

        private static readonly Dictionary<ushort, WallFaces> _cache = new();
        private static object _artSource;

        /// <summary>The faces of a static's art (cached per graphic); none when it reads as no wall plane.</summary>
        public static WallFaces For(ushort graphic)
        {
            var arts = Client.Game?.UO?.FileManager?.Arts;
            if (!ReferenceEquals(_artSource, arts))
            {
                _cache.Clear();
                _artSource = arts;
            }
            if (arts == null) return default;
            if (_cache.TryGetValue(graphic, out WallFaces faces)) return faces;
            try
            {
                var art = arts.GetArt((uint)(graphic + GUO.Assets.ArtLoader.MAX_LAND_DATA_INDEX_COUNT));
                if (art.Width <= 0 || art.Height <= 0 || art.Pixels.Length < art.Width * art.Height)
                    return default;
                faces = Classify(art.Pixels, art.Width, art.Height);
            }
            catch (Exception)
            {
                // Loading may be transiently unavailable; retry next time.
                return default;
            }

            _cache[graphic] = faces;
            return faces;
        }

        /// <summary>Forget every classification (the art changed).</summary>
        public static void ClearCache() => _cache.Clear();

        /// <summary>Forget a static whose native art was refreshed.</summary>
        public static void Invalidate(ushort graphic) => _cache.Remove(graphic);

        /// <summary>
        /// Fit the silhouette's bottom edge: one "\" or "/" line, or two
        /// (a "V" corner: "\" then "/"; a "^" corner: "/" then "\"), whichever
        /// fits within <see cref="MaxResidual"/> with the fewest pieces.
        /// Pixels are the art's 32-bit values, 0 transparent; the art stands
        /// centred on its tile with its bottom row at the tile's bottom corner.
        /// </summary>
        public static WallFaces Classify(ReadOnlySpan<uint> pixels, int width, int height)
        {
            if (width <= 0 || height <= 0 || pixels.Length < width * height) return default;

            // Bottom of each column, in tile-box pixels.
            var xs = new List<float>(width);
            var ys = new List<float>(width);
            var cols = new List<int>(width);
            int left = 22 - (width >> 1);
            for (int c = 0; c < width; c++)
            {
                for (int r = height - 1; r >= 0; r--)
                {
                    if (pixels[r * width + c] == 0) continue;
                    xs.Add(left + c + 0.5f);
                    ys.Add(r + 1 + 44 - height);
                    cols.Add(c);
                    break;
                }
            }

            int n = xs.Count;
            if (n < MinColumns) return default;

            // One line.
            Fit(xs, ys, 0, n, true, out float kx, out float rx);
            Fit(xs, ys, 0, n, false, out float ky, out float ry);
            float bestOne = Math.Min(rx, ry);

            // Two lines, split at every column with enough on each side.
            float bestTwo = float.MaxValue;
            int bestSplit = -1;
            bool bestLeftAlongX = false;
            float bestK1 = 0f, bestK2 = 0f;
            for (int s = MinColumns; s <= n - MinColumns; s++)
            {
                foreach (bool leftAlongX in new[] { true, false })
                {
                    Fit(xs, ys, 0, s, leftAlongX, out float k1, out float r1);
                    Fit(xs, ys, s, n, !leftAlongX, out float k2, out float r2);
                    float res = (r1 * s + r2 * (n - s)) / n;
                    if (res < bestTwo)
                    {
                        bestTwo = res;
                        bestSplit = s;
                        bestLeftAlongX = leftAlongX;
                        bestK1 = k1;
                        bestK2 = k2;
                    }
                }
            }

            // A second piece has to earn its place: a clearly better fit.
            if (bestOne <= MaxResidual && bestOne <= bestTwo + 1f)
            {
                return new WallFaces(new Face(rx <= ry, rx <= ry ? kx : ky, 0, width));
            }

            if (bestSplit > 0 && bestTwo <= MaxResidual)
            {
                int at = cols[bestSplit];
                return new WallFaces(new Face(bestLeftAlongX, bestK1, 0, at), new Face(!bestLeftAlongX, bestK2, at, width));
            }

            return default;
        }

        /// <summary>Median intercept and mean absolute residual of Y = X + k (along X) or Y = -X + k over [from, to).</summary>
        private static void Fit(List<float> xs, List<float> ys, int from, int to, bool alongX, out float k, out float residual)
        {
            int n = to - from;
            var ks = new float[n];
            for (int i = 0; i < n; i++)
            {
                ks[i] = alongX ? ys[from + i] - xs[from + i] : ys[from + i] + xs[from + i];
            }

            Array.Sort(ks);
            k = ks[n / 2];
            float sum = 0f;
            for (int i = 0; i < n; i++) sum += Math.Abs(ks[i] - k);
            residual = sum / n;
        }
    }
}
