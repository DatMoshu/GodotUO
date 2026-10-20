// SPDX-License-Identifier: BSD-2-Clause
// GUO-owned (ADR-0039): the tile-grid light field behind the Realtime lights option.

using System;
using System.Collections.Generic;
using Godot;

namespace GUO.Renderer.Lights
{
    /// <summary>
    /// Light as a field on the tile grid: ambient from the shard's light level,
    /// plus every light source's reach, walked through a blocker grid built from
    /// tiledata. Pure CPU, no engine objects, so the probe runs it headless.
    /// </summary>
    /// <remarks>
    /// The grid covers the visible chunks plus a margin. Each cell keeps the
    /// ground Z, the top Z of the tallest light blocker standing on it (walls,
    /// tall impassable statics; doors and windows do not block) and whether a
    /// roof covers it. A light's reach is a small map of its radius, computed
    /// once and cached until the light moves or the blockers around it change.
    /// </remarks>
    internal sealed class RealtimeLightField
    {
        /// <summary>No blocker on the cell.</summary>
        public const short NoBlock = short.MinValue;

        /// <summary>Largest radius in tiles a light may reach.</summary>
        public const int MaxRadius = 8;

        /// <summary>A blocker only blocks light that starts below its top by this many Z.</summary>
        public const int BlockClearance = 3;

        /// <summary>A static this tall or taller blocks when it is impassable (a crate does not, a wall does).</summary>
        public const int BlockerHeight = 8;

        /// <summary>Daylight ambient under a roof, as a share of outdoor daylight.</summary>
        public const float RoofDaylight = 0.55f;

        /// <summary>A light's peak above ambient: pools saturate near the source and read from afar.</summary>
        public const float Energy = 2.2f;

        /// <summary>A static standing this many Z above every light reaching its tile is above the light (a roof): ambient only.</summary>
        public const int AboveLightZ = 10;

        /// <summary>The tint of a flame (kept for callers that want it).</summary>
        public static readonly Color WarmLight = new Color(1.0f, 0.72f, 0.38f);

        /// <summary>
        /// The tint of an uncoloured light: a warm white. Classic draws an
        /// uncoloured light white and a torch in its own orange; tinting every
        /// light orange made a lantern and a torch the same.
        /// </summary>
        public static readonly Color NeutralLight = new Color(1.0f, 0.88f, 0.70f);

        private const int CacheLimit = 512;

        public readonly struct Source
        {
            public readonly int X, Y, Z;
            public readonly int Radius;
            public readonly Color Color;
            public readonly float OffsetX, OffsetY;
            /// <summary>Scales the light (a flame's flicker); 1 is the light as offered.</summary>
            public readonly float Intensity;

            public Source(int x, int y, int z, int radius, Color color, float offsetX = 0f, float offsetY = 0f, float intensity = 1f)
            {
                X = x;
                Y = y;
                Z = z;
                Radius = Math.Clamp(radius, 1, MaxRadius);
                Color = color;
                OffsetX = offsetX;
                OffsetY = offsetY;
                Intensity = intensity;
            }

            /// <summary>The light's continuous position in tile units (cell centres are integers).</summary>
            public float PosX => X + OffsetX;
            public float PosY => Y + OffsetY;
        }

        /// <summary>One source as lit this frame: its reach, and the neighbour's while it walks.</summary>
        private struct Lit
        {
            public Source Source;
            public Reach Reach, Other;
            public int OtherX, OtherY;
            public float OtherWeight;
        }

        private readonly struct CacheKey : IEquatable<CacheKey>
        {
            public readonly int X, Y, Z, Radius;

            public CacheKey(int x, int y, int z, int radius)
            {
                X = x; Y = y; Z = z; Radius = radius;
            }

            public bool Equals(CacheKey o) => X == o.X && Y == o.Y && Z == o.Z && Radius == o.Radius;
            public override bool Equals(object obj) => obj is CacheKey k && Equals(k);
            public override int GetHashCode() => HashCode.Combine(X, Y, Z, Radius);
        }

        private sealed class Reach
        {
            public float[] Map;
            public ulong BlockerHash;
            public int Frame;
        }

        private int _originX, _originY, _width, _height;
        private sbyte[] _groundZ = Array.Empty<sbyte>();
        private short[] _blockTop = Array.Empty<short>();
        private bool[] _roof = Array.Empty<bool>();
        private short[] _roofZ = Array.Empty<short>();
        private bool[] _roofFlag = Array.Empty<bool>();
        private float[] _field = Array.Empty<float>();
        private float[] _corner = Array.Empty<float>();
        private float[] _cellHead = Array.Empty<float>();
        private bool[] _dim = Array.Empty<bool>();
        private float[] _cornerHead = Array.Empty<float>();
        private Lit[] _lit = Array.Empty<Lit>();
        private int _litCount;
        private short[] _lightTop = Array.Empty<short>();
        private float[] _scratchVis = Array.Empty<float>();
        private float[] _scratchBlur = Array.Empty<float>();
        private readonly Dictionary<CacheKey, Reach> _cache = new();
        private int _frame;
        private ulong _blockerHash;

        public int OriginX => _originX;
        public int OriginY => _originY;
        public int Width => _width;
        public int Height => _height;

        /// <summary>Reach maps computed by the last <see cref="Compute"/> (the cost log; the rest were cache hits).</summary>
        public int LastComputed { get; private set; }

        /// <summary>Cache entries alive.</summary>
        public int CacheCount => _cache.Count;

        /// <summary>Ambient colour of the last <see cref="Compute"/>.</summary>
        public Color Ambient { get; private set; } = Colors.White;

        /// <summary>The ambient a roofed cell gets (roof sprites draw in this, no light reaches their top).</summary>
        public Color RoofAmbient { get; private set; } = Colors.White;

        /// <summary>Start a frame: the grid covers tiles [originX, originX+width) x [originY, originY+height).</summary>
        public void Begin(int originX, int originY, int width, int height)
        {
            width = Math.Max(1, width);
            height = Math.Max(1, height);
            int cells = width * height;

            if (_groundZ.Length != cells)
            {
                _groundZ = new sbyte[cells];
                _blockTop = new short[cells];
                _roof = new bool[cells];
                _roofZ = new short[cells];
                _roofFlag = new bool[cells];
                _field = new float[cells * 3];
                _lightTop = new short[cells];
            }

            if (_corner.Length != (width + 1) * (height + 1) * 3)
            {
                _corner = new float[(width + 1) * (height + 1) * 3];
                _cornerHead = new float[(width + 1) * (height + 1)];
            }

            if (_cellHead.Length != cells) _cellHead = new float[cells];

            if (originX != _originX || originY != _originY || width != _width || height != _height)
            {
                // The reach maps are grid-relative; a moved grid invalidates them.
                _cache.Clear();
            }

            _originX = originX;
            _originY = originY;
            _width = width;
            _height = height;
            Array.Fill(_blockTop, NoBlock);
            Array.Fill(_lightTop, NoBlock);
            Array.Fill(_roof, false);
            Array.Fill(_roofZ, NoBlock);
            Array.Fill(_roofFlag, false);
            Array.Clear(_groundZ);
        }

        public bool Contains(int x, int y)
            => x >= _originX && y >= _originY && x < _originX + _width && y < _originY + _height;

        private int Index(int x, int y) => (y - _originY) * _width + (x - _originX);

        /// <summary>The ground Z of a tile.</summary>
        public void SetGround(int x, int y, sbyte z)
        {
            if (Contains(x, y)) _groundZ[Index(x, y)] = z;
        }

        /// <summary>A blocker standing on a tile: its top Z (the tallest wins).</summary>
        public void AddBlocker(int x, int y, int topZ)
        {
            if (!Contains(x, y)) return;
            int i = Index(x, y);
            short top = (short)Math.Clamp(topZ, short.MinValue + 1, short.MaxValue);
            if (top > _blockTop[i]) _blockTop[i] = top;
        }

        /// <summary>A roof (or an upper floor) over a tile: the roof dim, and cover at its base Z.</summary>
        public void SetRoof(int x, int y, int z = 0, bool roofFlag = true)
        {
            if (!Contains(x, y)) return;
            _roof[Index(x, y)] = true;
            if (roofFlag) _roofFlag[Index(x, y)] = true;
            SetCover(x, y, z);
        }

        /// <summary>Whether the cover over a tile is a roof-flagged static (hidden with the roofs) rather than an upper floor.</summary>
        public bool CoverIsRoofFlag(int x, int y) => Contains(x, y) && _roofFlag[Index(x, y)];

        /// <summary>The ground Z of a tile (0 outside the grid).</summary>
        public sbyte GroundZ(int x, int y) => Contains(x, y) ? _groundZ[Index(x, y)] : (sbyte)0;

        /// <summary>
        /// Something that covers the tile from above at base Z: a roof piece, or
        /// a floor of an upper storey (the lowest cover wins). What stands below
        /// it is hidden by it in the world draw.
        /// </summary>
        public void SetCover(int x, int y, int z)
        {
            if (!Contains(x, y)) return;
            int i = Index(x, y);
            short cz = (short)Math.Clamp(z, short.MinValue + 1, short.MaxValue);
            if (_roofZ[i] == NoBlock || cz < _roofZ[i]) _roofZ[i] = cz;
        }

        /// <summary>The base Z of the lowest cover over a tile, or <see cref="NoBlock"/>.</summary>
        public short CoverZ(int x, int y) => Contains(x, y) ? _roofZ[Index(x, y)] : NoBlock;

        /// <summary>A floor piece this far above the ground is an upper storey (cover), not the floor.</summary>
        public const int UpperFloorZ = 10;

        public bool IsRoofed(int x, int y) => Contains(x, y) && _roof[Index(x, y)];
        public short BlockTop(int x, int y) => Contains(x, y) ? _blockTop[Index(x, y)] : NoBlock;

        /// <summary>
        /// Tiledata rule for what blocks light: a wall, or an impassable static
        /// at least <see cref="BlockerHeight"/> tall; never a door or a window.
        /// </summary>
        public static bool Blocks(bool isWall, bool isImpassable, bool isDoor, bool isWindow, int height)
            => !isDoor && !isWindow && (isWall || (isImpassable && height >= BlockerHeight));

        /// <summary>
        /// The ambient for a shard light level, as the classic grey
        /// (<c>IsometricLight.IsometricLevel</c>: 1.0 full day, 0.0625 darkest).
        /// Day is white, dusk warms, night goes blue-grey; the three channels
        /// retain the classic grey's luminance so equal night levels compare
        /// equal ambient brightness; only the colour changes.
        /// </summary>
        public static Color AmbientFor(float isometricLevel)
        {
            float t = Math.Clamp(isometricLevel, 0f, 1f);
            // Night floor at the darkest level, white at full day.
            var night = new Color(0.24f, 0.26f, 0.38f);
            var day = Colors.White;
            // Perceptual: the classic grey is linear in level; ease it so dusk
            // keeps some light and the last levels fall off to the floor.
            float k = t * t * (3f - 2f * t);
            var c = night.Lerp(day, k);
            // Warm bump through dusk (levels around the middle).
            float bump = Math.Max(0f, 1f - Math.Abs(t - 0.5f) / 0.3f);
            c.R = Math.Min(1f, c.R + 0.10f * bump);
            c.G = Math.Min(1f, c.G + 0.02f * bump);
            c.B = Math.Max(0f, c.B - 0.08f * bump);
            float luminance = c.R * 0.2126f + c.G * 0.7152f + c.B * 0.0722f;
            float scale = t / luminance;
            return new Color(c.R * scale, c.G * scale, c.B * scale);
        }

        /// <summary>
        /// Sum ambient and every source into the field. <paramref name="isometricLevel"/>
        /// is the classic grey; <paramref name="nightness"/> out is 0 by day, 1 at the darkest.
        /// </summary>
        public void Compute(float isometricLevel, ReadOnlySpan<Source> sources)
        {
            _frame++;
            Ambient = AmbientFor(isometricLevel);
            float day = Math.Clamp(isometricLevel, 0f, 1f);
            float roofShare = RoofDaylight + (1f - RoofDaylight) * (1f - day);
            RoofAmbient = new Color(Ambient.R * roofShare, Ambient.G * roofShare, Ambient.B * roofShare);
            int cells = _width * _height;

            float headOut = Headroom(Ambient), headRoof = Headroom(RoofAmbient);
            MarkDim();
            for (int i = 0; i < cells; i++)
            {
                float s = _dim[i] ? roofShare : 1f;
                _field[i * 3] = Ambient.R * s;
                _field[i * 3 + 1] = Ambient.G * s;
                _field[i * 3 + 2] = Ambient.B * s;
                _cellHead[i] = _dim[i] ? headRoof : headOut;
            }

            // A corner starts as the mean ambient of the four cells it joins.
            int cw = _width + 1;
            for (int cy = 0; cy <= _height; cy++)
            {
                for (int cx = 0; cx <= _width; cx++)
                {
                    float r = 0f, g = 0f, b = 0f, h = 0f;
                    for (int k = 0; k < 4; k++)
                    {
                        int x = Math.Clamp(cx - 1 + (k & 1), 0, _width - 1);
                        int y = Math.Clamp(cy - 1 + (k >> 1), 0, _height - 1);
                        int i = (y * _width + x) * 3;
                        r += _field[i]; g += _field[i + 1]; b += _field[i + 2];
                        h += _cellHead[y * _width + x];
                    }
                    int c = (cy * cw + cx) * 3;
                    _corner[c] = r * 0.25f; _corner[c + 1] = g * 0.25f; _corner[c + 2] = b * 0.25f;
                    _cornerHead[cy * cw + cx] = h * 0.25f;
                }
            }

            _blockerHash = HashBlockers();
            LastComputed = 0;

            if (_cache.Count > CacheLimit) _cache.Clear();
            if (_lit.Length < sources.Length) _lit = new Lit[Math.Max(sources.Length, 16)];
            _litCount = 0;

            for (int n = 0; n < sources.Length; n++)
            {
                ref readonly Source s = ref sources[n];
                if (!Contains(s.X, s.Y)) continue;

                Reach reach = ReachAt(s.X, s.Y, s.Z, s.Radius);

                // A walking light sits between two tiles: its visibility is
                // the two tiles' maps crossfaded by the offset, so a pool
                // slides past a door post instead of snapping at the seam.
                int ox = MathF.Abs(s.OffsetX) > 0.01f ? Math.Sign(s.OffsetX) : 0;
                int oy = MathF.Abs(s.OffsetY) > 0.01f ? Math.Sign(s.OffsetY) : 0;
                float w = Math.Clamp(Math.Max(MathF.Abs(s.OffsetX), MathF.Abs(s.OffsetY)), 0f, 1f);
                Reach other = null;
                if ((ox != 0 || oy != 0) && w > 0.01f && Contains(s.X + ox, s.Y + oy))
                {
                    other = ReachAt(s.X + ox, s.Y + oy, s.Z, s.Radius);
                }
                else
                {
                    w = 0f;
                }

                _lit[_litCount++] = new Lit { Source = s, Reach = reach, Other = other, OtherX = s.X + ox, OtherY = s.Y + oy, OtherWeight = w };
                Accumulate(in s, s.X, s.Y, reach.Map, 1f - w);
                if (other != null) Accumulate(in s, s.X + ox, s.Y + oy, other.Map, w);
            }

            // Forget lights that have gone out (not seen this frame).
            if (_cache.Count > 0 && (_frame & 63) == 0)
            {
                var dead = new List<CacheKey>();
                foreach (var kv in _cache) if (kv.Value.Frame != _frame) dead.Add(kv.Key);
                foreach (var k in dead) _cache.Remove(k);
            }
        }

        /// <summary>
        /// How much of a light shows over an ambient: all of it in the dark,
        /// none in full daylight (a torch at noon neither brightens nor tints
        /// the ground, as classic draws no lights by day), some under a roof by day.
        /// </summary>
        public static float Headroom(Color ambient)
        {
            float lum = ambient.R * 0.2126f + ambient.G * 0.7152f + ambient.B * 0.0722f;
            return Math.Clamp((1f - lum) / 0.75f, 0f, 1f);
        }

        /// <summary>
        /// The sky's light at a tile and height: the roof ambient under cover,
        /// the open ambient on and above it (a roof, a chimney, a rooftop).
        /// </summary>
        public Color AmbientAt(int x, int y, int z)
        {
            if (!Contains(x, y) || !_dim[Index(x, y)]) return Ambient;
            short cover = _roofZ[Index(x, y)];
            return cover != NoBlock && z >= cover ? Ambient : RoofAmbient;
        }

        /// <summary>
        /// Which roofed cells take the indoor dim: not the outer edge of a
        /// building, where a camera-side neighbour (east, south or south-east)
        /// is open sky. What shows there is the outer face of the wall, the
        /// door, the window, the fence under the eaves, which the sky reaches.
        /// </summary>
        private void MarkDim()
        {
            if (_dim.Length != _roof.Length) _dim = new bool[_roof.Length];
            for (int y = 0; y < _height; y++)
            {
                for (int x = 0; x < _width; x++)
                {
                    int i = y * _width + x;
                    _dim[i] = _roof[i]
                        && (x + 1 >= _width || _roof[i + 1])
                        && (y + 1 >= _height || _roof[i + _width])
                        && (x + 1 >= _width || y + 1 >= _height || _roof[i + _width + 1]);
                }
            }
        }

        /// <summary>Whether a cell takes the indoor dim (roofed, and not a building's outer edge).</summary>
        public bool IsDim(int x, int y) => Contains(x, y) && _dim[Index(x, y)];

        private Reach ReachAt(int x, int y, int z, int radius)
        {
            var key = new CacheKey(x, y, z, radius);
            if (!_cache.TryGetValue(key, out Reach reach) || reach.BlockerHash != _blockerHash)
            {
                reach ??= new Reach();
                reach.Map = ComputeReach(x, y, z, radius, reach.Map);
                reach.BlockerHash = _blockerHash;
                _cache[key] = reach;
                LastComputed++;
            }

            reach.Frame = _frame;
            return reach;
        }

        /// <summary>Sources lit by the last <see cref="Compute"/> (those inside the grid).</summary>
        public int LitCount => _litCount;

        /// <summary>The <paramref name="n"/>th lit source.</summary>
        public Source LitSource(int n) => _lit[n].Source;

        /// <summary>
        /// One lit source's raw light at a point (tile units, cell centres at
        /// integers), visibility included: what a shadow of that light takes away.
        /// </summary>
        public Color SourceAt(int n, float wx, float wy)
        {
            ref readonly Lit lit = ref _lit[n];
            ref readonly Source s = ref lit.Source;
            float dx = wx - s.PosX, dy = wy - s.PosY;
            float f = Falloff(MathF.Sqrt(dx * dx + dy * dy), s.Radius);
            if (f <= 0f) return new Color(0, 0, 0);
            int cx = (int)MathF.Floor(wx + 0.5f), cy = (int)MathF.Floor(wy + 0.5f);
            if (!Contains(cx, cy)) return new Color(0, 0, 0);
            f *= _cellHead[Index(cx, cy)];
            float v = Visibility(lit.Reach, s.X, s.Y, s.Radius, cx, cy) * (1f - lit.OtherWeight);
            if (lit.Other != null) v += Visibility(lit.Other, lit.OtherX, lit.OtherY, s.Radius, cx, cy) * lit.OtherWeight;
            float e = f * v * Energy * s.Intensity;
            return new Color(s.Color.R * e, s.Color.G * e, s.Color.B * e);
        }

        private static float Visibility(Reach reach, int sx, int sy, int radius, int x, int y)
        {
            int r = radius + 1, side = r * 2 + 1;
            int dx = x - sx + r, dy = y - sy + r;
            if (dx < 0 || dy < 0 || dx >= side || dy >= side) return 0f;
            return reach.Map[dy * side + dx];
        }

        /// <summary>The raw (unscaled) field at a point, bilinear between cell centres.</summary>
        public Color RawAt(float wx, float wy)
        {
            float fx = wx - _originX, fy = wy - _originY;
            int x0 = (int)MathF.Floor(fx), y0 = (int)MathF.Floor(fy);
            float tx = fx - x0, ty = fy - y0;
            Color a = RawCell(x0, y0), b = RawCell(x0 + 1, y0), c = RawCell(x0, y0 + 1), d = RawCell(x0 + 1, y0 + 1);
            return a.Lerp(b, tx).Lerp(c.Lerp(d, tx), ty);
        }

        private Color RawCell(int cx, int cy)
        {
            cx = Math.Clamp(cx, 0, _width - 1);
            cy = Math.Clamp(cy, 0, _height - 1);
            int i = (cy * _width + cx) * 3;
            return new Color(_field[i], _field[i + 1], _field[i + 2]);
        }

        /// <summary>What the target shows for a raw light: brighter than white is scaled by its brightest channel.</summary>
        public static Color Scale(Color raw)
        {
            float m = Math.Max(raw.R, Math.Max(raw.G, raw.B));
            return m > 1f ? new Color(raw.R / m, raw.G / m, raw.B / m) : new Color(raw.R, raw.G, raw.B);
        }

        /// <summary>
        /// What the target shows where <paramref name="blocked"/> of the raw
        /// light <paramref name="raw"/> is taken away: a shadow's light.
        /// </summary>
        public static Color Without(Color raw, Color blocked)
            => Scale(new Color(Math.Max(0f, raw.R - blocked.R), Math.Max(0f, raw.G - blocked.G), Math.Max(0f, raw.B - blocked.B)));

        /// <summary>
        /// The field at a cell (clamped to the grid). Light is summed without
        /// a ceiling; a cell brighter than white is scaled by its brightest
        /// channel, so a torch's orange stays orange near the flame instead
        /// of clipping channel by channel to yellow.
        /// </summary>
        public Color Cell(int x, int y)
        {
            int cx = Math.Clamp(x - _originX, 0, _width - 1);
            int cy = Math.Clamp(y - _originY, 0, _height - 1);
            int i = (cy * _width + cx) * 3;
            return Scale(new Color(_field[i], _field[i + 1], _field[i + 2]));
        }

        /// <summary>
        /// The field for a static at height <paramref name="z"/> on a cell: the
        /// cell's light, or the roof ambient when it stands above every light
        /// that reaches the cell (a roof over a lamp, a floor above).
        /// </summary>
        /// <remarks>
        /// What stands on or above the cell's cover (a roof tile, a flat
        /// rooftop, a chimney) is under the open sky: the open ambient, plus
        /// the light of any lamp standing up there with it.
        /// </remarks>
        public Color CellAt(int x, int y, int z)
        {
            if (Contains(x, y) && _roof[Index(x, y)])
            {
                short cover = _roofZ[Index(x, y)];
                if (cover != NoBlock && z >= cover)
                {
                    short top = _lightTop[Index(x, y)];
                    return top == NoBlock || top < cover - BlockClearance ? Ambient : OpenCell(x, y);
                }
            }

            return IsAboveLight(x, y, z) ? AmbientAt(x, y, z) : Cell(x, y);
        }

        /// <summary>
        /// A cell's light as if no roof dimmed its ambient: for the outer face
        /// of a wall under the eaves, which the sky still reaches.
        /// </summary>
        public Color OpenCell(int x, int y)
        {
            Color raw = RawCell(x - _originX, y - _originY);
            if (Contains(x, y) && _dim[Index(x, y)])
            {
                raw = new Color(raw.R + Ambient.R - RoofAmbient.R, raw.G + Ambient.G - RoofAmbient.G, raw.B + Ambient.B - RoofAmbient.B);
            }

            return Scale(raw);
        }

        /// <summary>Whether a static at <paramref name="z"/> stands above every light reaching its cell.</summary>
        public bool IsAboveLight(int x, int y, int z)
        {
            if (!Contains(x, y)) return false;
            short top = _lightTop[Index(x, y)];
            return top != NoBlock && z > top + AboveLightZ;
        }

        /// <summary>
        /// The field at a tile corner: corner (x, y) is shared by the cells
        /// (x-1..x, y-1..y). The tile (x, y)'s diamond has its top at corner
        /// (x, y), right at (x+1, y), bottom at (x+1, y+1), left at (x, y+1).
        /// </summary>
        /// <remarks>
        /// Each light is evaluated at the corner itself (distance to the
        /// corner point, visibility the mean of the four cells), so a pool's
        /// edge is round and its peak is not averaged away.
        /// </remarks>
        public Color Corner(int x, int y)
        {
            int cx = Math.Clamp(x - _originX, 0, _width);
            int cy = Math.Clamp(y - _originY, 0, _height);
            int i = (cy * (_width + 1) + cx) * 3;
            return Scale(new Color(_corner[i], _corner[i + 1], _corner[i + 2]));
        }

        /// <summary>Radial falloff: 1 at the source, 0 at the radius, soft in between.</summary>
        public static float Falloff(float distance, float radius)
        {
            float t = Math.Clamp(1f - distance / radius, 0f, 1f);
            return t * t * (3f - 2f * t);
        }

        private float[] ComputeReach(int sx, int sy, int sz, int radius, float[] map)
        {
            // One extra cell keeps the support complete while the source
            // interpolates toward a neighbouring tile in either direction.
            int r = radius + 1;
            int side = r * 2 + 1;
            int n = side * side;
            if (map == null || map.Length != n) map = new float[n];
            if (_scratchVis.Length < n)
            {
                _scratchVis = new float[n];
                _scratchBlur = new float[n];
            }

            // Hard visibility per cell: a line from the source to the cell,
            // blocked by any cell strictly between whose blocker top stands
            // above the light. The source's own tile never blocks (a sconce
            // on a wall lights the room it faces), nor does the target's (a
            // wall takes light on either face).
            for (int dy = -r; dy <= r; dy++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    int tx = sx + dx, ty = sy + dy;
                    float vis = Contains(tx, ty)
                        ? Visible(sx, sy, sz, tx, ty)
                        : 0f;
                    _scratchVis[(dy + r) * side + (dx + r)] = vis;
                }
            }

            // 3x3 box blur of the visibility: soft edges where walls cut it.
            for (int y = 0; y < side; y++)
            {
                for (int x = 0; x < side; x++)
                {
                    float sum = 0f;
                    int count = 0;
                    for (int oy = -1; oy <= 1; oy++)
                    {
                        int yy = y + oy;
                        if (yy < 0 || yy >= side) continue;
                        for (int ox = -1; ox <= 1; ox++)
                        {
                            int xx = x + ox;
                            if (xx < 0 || xx >= side) continue;
                            sum += _scratchVis[yy * side + xx];
                            count++;
                        }
                    }
                    _scratchBlur[y * side + x] = count > 0 ? sum / count : 0f;
                }
            }

            for (int dy = -r; dy <= r; dy++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    int i = (dy + r) * side + (dx + r);
                    // Cache visibility, not distance: falloff follows the
                    // sub-tile source position each frame in Accumulate.
                    float v = _scratchBlur[i];
                    // Ground far below or above the light takes less.
                    int tx = sx + dx, ty = sy + dy;
                    if (Contains(tx, ty))
                    {
                        int dz = Math.Abs(_groundZ[Index(tx, ty)] - sz);
                        if (dz > 20) v *= 0.35f;
                    }
                    map[i] = v;
                }
            }

            return map;
        }

        /// <summary>Hard line-of-light from (x0, y0) at Z to (x1, y1): 1 clear, 0 blocked.</summary>
        private float Visible(int x0, int y0, int z, int x1, int y1)
        {
            int dx = Math.Abs(x1 - x0), dy = Math.Abs(y1 - y0);
            int steps = Math.Max(dx, dy);
            if (steps <= 1) return 1f;
            int limit = z + BlockClearance;
            // Walk the line in steps of one cell on the longer axis; the
            // shorter axis rounds. Both ends are excluded.
            for (int i = 1; i < steps; i++)
            {
                int cx = x0 + (int)MathF.Round((x1 - x0) * (float)i / steps);
                int cy = y0 + (int)MathF.Round((y1 - y0) * (float)i / steps);
                if (!Contains(cx, cy)) continue;
                short top = _blockTop[Index(cx, cy)];
                if (top != NoBlock && top > limit) return 0f;
            }
            return 1f;
        }

        /// <summary>
        /// Add one source through the reach map of tile (<paramref name="mx"/>,
        /// <paramref name="my"/>), times <paramref name="weight"/>: the cells,
        /// and the corners at their own distance with the mean visibility of
        /// the four cells they join.
        /// </summary>
        private void Accumulate(in Source s, int mx, int my, float[] map, float weight)
        {
            if (weight <= 0f) return;
            int r = s.Radius + 1;
            int side = r * 2 + 1;
            float px = s.PosX, py = s.PosY;
            float energy = Energy * s.Intensity * weight;
            for (int dy = -r; dy <= r; dy++)
            {
                int ty = my + dy;
                if (ty < _originY || ty >= _originY + _height) continue;
                for (int dx = -r; dx <= r; dx++)
                {
                    int tx = mx + dx;
                    if (tx < _originX || tx >= _originX + _width) continue;
                    float v = map[(dy + r) * side + (dx + r)];
                    float fx = tx - px, fy = ty - py;
                    v *= Falloff(MathF.Sqrt(fx * fx + fy * fy), s.Radius);
                    if (v <= 0f) continue;
                    int cell = Index(tx, ty);
                    if (s.Z > _lightTop[cell]) _lightTop[cell] = (short)s.Z;
                    int i = cell * 3;
                    v *= energy * _cellHead[cell];
                    _field[i] += s.Color.R * v;
                    _field[i + 1] += s.Color.G * v;
                    _field[i + 2] += s.Color.B * v;
                }
            }

            // Corner (cx, cy) joins cells (cx-1..cx, cy-1..cy); it sits at
            // (cx - 0.5, cy - 0.5) in cell-centre units.
            int cw = _width + 1;
            for (int dy = -r + 1; dy <= r; dy++)
            {
                int cy = my + dy;
                if (cy < _originY || cy > _originY + _height) continue;
                for (int dx = -r + 1; dx <= r; dx++)
                {
                    int cx = mx + dx;
                    if (cx < _originX || cx > _originX + _width) continue;
                    float fx = cx - 0.5f - px, fy = cy - 0.5f - py;
                    float f = Falloff(MathF.Sqrt(fx * fx + fy * fy), s.Radius);
                    if (f <= 0f) continue;
                    int a0 = (dy + r - 1) * side + (dx + r - 1);
                    float v = (map[a0] + map[a0 + 1] + map[a0 + side] + map[a0 + side + 1]) * 0.25f * f * energy;
                    if (v <= 0f) continue;
                    int ci = (cy - _originY) * cw + (cx - _originX);
                    v *= _cornerHead[ci];
                    int c = ci * 3;
                    _corner[c] += s.Color.R * v;
                    _corner[c + 1] += s.Color.G * v;
                    _corner[c + 2] += s.Color.B * v;
                }
            }
        }

        private ulong HashBlockers()
        {
            // FNV-1a over the blocker tops and the grid origin.
            ulong h = 14695981039346656037UL;
            unchecked
            {
                h = (h ^ (ulong)(uint)_originX) * 1099511628211UL;
                h = (h ^ (ulong)(uint)_originY) * 1099511628211UL;
                for (int i = 0; i < _blockTop.Length; i++)
                {
                    h = (h ^ (ulong)(ushort)_blockTop[i]) * 1099511628211UL;
                }
            }
            return h;
        }
    }
}
