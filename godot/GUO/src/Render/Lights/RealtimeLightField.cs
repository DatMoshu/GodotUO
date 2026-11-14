// SPDX-License-Identifier: BSD-2-Clause
// GUO-owned (ADR-0039; drawing path retired 2026-11-14): the tile grid and reach rays that
// lighting speed mode (ADR-0048) shares. The CPU field that summed lights into a texture is gone.

using System;
using System.Collections.Generic;
using Godot;

namespace GUO.Renderer.Lights
{
    /// <summary>
    /// The tile grid light sources are walked through: ground, blockers, wall
    /// lines and cover built from tiledata, and the visibility rays of a light.
    /// Pure CPU, no engine objects, so the probe runs it headless.
    /// </summary>
    /// <remarks>
    /// The grid covers the visible chunks plus a margin. Each cell keeps the
    /// ground Z, the top Z of the tallest light blocker standing on it (walls,
    /// tall impassable statics; doors and windows do not block) and whether a
    /// roof covers it. A light's reach is a small map of its radius, computed
    /// once and cached until the light moves or the blockers around it change.
    /// </remarks>
    internal sealed partial class RealtimeLightField
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

        /// <summary>The tint of an uncoloured light: warm, as a flame is.</summary>
        public static readonly Color WarmLight = new Color(1.0f, 0.72f, 0.38f);

        private const int CacheLimit = 512;

        public readonly struct Source
        {
            public readonly int X, Y, Z;
            public readonly int Radius;
            public readonly Color Color;
            public readonly float OffsetX, OffsetY;
            public readonly LightCurve Curve;
            public readonly float Energy;
            public readonly float[] CustomCurve;

            public Source(int x, int y, int z, int radius, Color color, float offsetX = 0f, float offsetY = 0f,
                LightCurve curve = LightCurve.B, float energy = RealtimeLightField.Energy, float[] customCurve = null)
            {
                X = x;
                Y = y;
                Z = z;
                Radius = Math.Clamp(radius, 1, MaxRadius);
                Color = color;
                OffsetX = offsetX;
                OffsetY = offsetY;
                Curve = curve;
                Energy = energy;
                CustomCurve = customCurve;
            }
        }

        private readonly struct CacheKey : IEquatable<CacheKey>
        {
            public readonly int X, Y, Z, Radius;

            public CacheKey(in Source s)
            {
                X = s.X; Y = s.Y; Z = s.Z; Radius = s.Radius;
            }

            public bool Equals(CacheKey o) => X == o.X && Y == o.Y && Z == o.Z && Radius == o.Radius;
            public override bool Equals(object obj) => obj is CacheKey k && Equals(k);
            public override int GetHashCode() => HashCode.Combine(X, Y, Z, Radius);
        }

        private int _originX, _originY, _width, _height;
        private sbyte[] _groundZ = Array.Empty<sbyte>();
        private short[] _blockTop = Array.Empty<short>();
        private bool[] _roof = Array.Empty<bool>();
        private short[] _roofZ = Array.Empty<short>();
        private bool[] _roofFlag = Array.Empty<bool>();
        private short[] _hTop = Array.Empty<short>(), _vTop = Array.Empty<short>();
        private float[] _hOff = Array.Empty<float>(), _vOff = Array.Empty<float>();
        private int _fineScale = 4;
        private float[] _scratchVis = Array.Empty<float>();
        private float[] _scratchBlur = Array.Empty<float>();

        public int OriginX => _originX;
        public int OriginY => _originY;
        public int Width => _width;
        public int Height => _height;

        /// <summary>Light samples per tile side this frame (4, or 2 on a very large grid).</summary>
        public int FineScale => _fineScale;

        /// <summary>Grids with more cells than this sample 2 per tile side, not 4.</summary>
        public const int FineCellLimit = 192 * 192;

        /// <summary>Reach maps computed since the last <c>BeginSpeedReach</c> (the cost log; the rest were cache hits).</summary>
        public int LastComputed { get; private set; }

        /// <summary>Start a frame: the grid covers tiles [originX, originX+width) x [originY, originY+height).</summary>
        public void Begin(int originX, int originY, int width, int height)
        {
            width = Math.Max(1, width);
            height = Math.Max(1, height);
            int cells = width * height;

            int fine = cells <= FineCellLimit ? 4 : 2;
            _fineScale = fine;

            if (_groundZ.Length != cells)
            {
                _hTop = new short[cells];
                _vTop = new short[cells];
                _hOff = new float[cells];
                _vOff = new float[cells];
                _groundZ = new sbyte[cells];
                _blockTop = new short[cells];
                _roof = new bool[cells];
                _roofZ = new short[cells];
                _roofFlag = new bool[cells];
            }

            _originX = originX;
            _originY = originY;
            _width = width;
            _height = height;
            Array.Fill(_blockTop, NoBlock);
            Array.Fill(_hTop, NoBlock);
            Array.Fill(_vTop, NoBlock);
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

        /// <summary>
        /// A wall as a line on its tile, not the whole tile: <paramref name="alongX"/>
        /// is a wall running along X at world Y = y + <paramref name="offset"/>
        /// (art "\"), else one running along Y at world X = x + offset (art "/").
        /// Light does not cross it, from either side; the light on each side is
        /// its own (the tallest wall on the tile wins).
        /// </summary>
        public void AddEdge(int x, int y, bool alongX, float offset, int topZ)
        {
            if (!Contains(x, y)) return;
            int i = Index(x, y);
            short top = (short)Math.Clamp(topZ, short.MinValue + 1, short.MaxValue);
            offset = Math.Clamp(offset, 0f, 1f);
            if (alongX)
            {
                if (top > _hTop[i]) { _hTop[i] = top; _hOff[i] = offset; }
            }
            else if (top > _vTop[i])
            {
                _vTop[i] = top;
                _vOff[i] = offset;
            }
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

        /// <summary>Radial falloff: 1 at the source, 0 at the radius, soft in between (curve B).</summary>
        public static float Falloff(float distance, float radius)
        {
            float t = Math.Clamp(1f - distance / radius, 0f, 1f);
            return t * t * (3f - 2f * t);
        }

        /// <summary>Radial falloff along a profile curve (<see cref="RealtimeLightProfiles.Evaluate"/>).</summary>
        public static float Falloff(float distance, float radius, LightCurve curve, float[] custom = null)
            => curve == LightCurve.B ? Falloff(distance, radius) : RealtimeLightProfiles.Evaluate(curve, distance / radius, custom);

        private float[] ComputeReach(in Source s, float[] map)
        {
            // Samples cover the source's tile and r + 1 tiles around it: one
            // extra tile keeps the support complete while the source
            // interpolates toward a neighbouring tile in either direction.
            int f = _fineScale;
            int r = s.Radius + 1;
            int side = (r * 2 + 1) * f;
            int n = side * side;
            if (map == null || map.Length != n) map = new float[n];
            if (_scratchVis.Length < n)
            {
                _scratchVis = new float[n];
                _scratchBlur = new float[n];
            }

            // Hard visibility per sample: a ray from the source's tile centre
            // to the sample, stopped by a wall line it crosses or a whole-tile
            // blocker strictly between (the source's and the sample's own tile
            // never block as whole tiles: a lamp on a pillar, a pillar's lit
            // face). A wall line blocks from either side, so a wall's two faces
            // take the light of their own side only.
            (float lx, float ly) = LightOrigin(s.X, s.Y);
            int x0 = (s.X - r) * f, y0 = (s.Y - r) * f;
            for (int j = 0; j < side; j++)
            {
                for (int i = 0; i < side; i++)
                {
                    float px = (x0 + i + 0.5f) / f, py = (y0 + j + 0.5f) / f;
                    int tx = (int)MathF.Floor(px), ty = (int)MathF.Floor(py);
                    _scratchVis[j * side + i] = Contains(tx, ty) ? Visible(s.X, s.Y, lx, ly, s.Z, px, py) : 0f;
                }
            }

            // 3x3 box blur of the samples, kept inside the lit region: a soft edge.
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
                    // Never brighter than its own hard value: the soft edge falls on
                    // the lit side, and no light bleeds through a wall to its far face.
                    _scratchBlur[y * side + x] = count > 0 ? Math.Min(_scratchVis[y * side + x], sum / count) : 0f;
                }
            }

            for (int j = 0; j < side; j++)
            {
                for (int i = 0; i < side; i++)
                {
                    int k = j * side + i;
                    // Cache visibility, not distance: falloff follows the
                    // sub-tile source position each frame in Accumulate.
                    float v = _scratchBlur[k];
                    // Ground far below or above the light takes less.
                    int tx = s.X - r + i / f, ty = s.Y - r + j / f;
                    if (Contains(tx, ty))
                    {
                        int dz = Math.Abs(_groundZ[Index(tx, ty)] - s.Z);
                        if (dz > 20) v *= 0.35f;
                    }
                    map[k] = v;
                }
            }

            return map;
        }

        /// <summary>Where a light on a wall line stands off it, in tiles, on the side the camera sees.</summary>
        public const float WallLampStandoff = 0.3f;

        /// <summary>
        /// The point a light on tile (x, y) shines from: the tile's centre, or,
        /// on a tile with a wall line, just in front of that line on the side
        /// the camera sees (+Y of a wall along X, +X of one along Y). A lamp
        /// hung on a wall hangs on a face; the shown face is the likely one,
        /// and a lamp inside a room hangs on its north or west wall, whose
        /// shown face is the room's.
        /// </summary>
        public (float X, float Y) LightOrigin(int x, int y)
        {
            float lx = x + 0.5f, ly = y + 0.5f;
            if (Contains(x, y))
            {
                int i = Index(x, y);
                if (_hTop[i] != NoBlock) ly = Math.Max(ly, y + _hOff[i] + WallLampStandoff);
                if (_vTop[i] != NoBlock) lx = Math.Max(lx, x + _vOff[i] + WallLampStandoff);
            }

            return (lx, ly);
        }

        /// <summary>
        /// Line of light from (lx, ly) at Z (from tile (sx, sy)) to the point
        /// (px, py), in tile units: 1 clear, 0 blocked. Walks the tiles the
        /// segment crosses; a wall line on any of them blocks when the segment
        /// crosses it, a whole-tile blocker when the tile is neither end's.
        /// Only blockers standing above Z + <see cref="BlockClearance"/> count.
        /// </summary>
        private float Visible(int sx, int sy, float lx, float ly, int z, float px, float py)
        {
            int limit = z + BlockClearance;
            int cx = (int)MathF.Floor(lx), cy = (int)MathF.Floor(ly);
            int ex = (int)MathF.Floor(px), ey = (int)MathF.Floor(py);
            float dx = px - lx, dy = py - ly;
            int stepX = dx > 0 ? 1 : -1, stepY = dy > 0 ? 1 : -1;
            float tDeltaX = dx != 0f ? Math.Abs(1f / dx) : float.MaxValue;
            float tDeltaY = dy != 0f ? Math.Abs(1f / dy) : float.MaxValue;
            float tMaxX = dx != 0f ? (stepX > 0 ? cx + 1 - lx : lx - cx) * tDeltaX : float.MaxValue;
            float tMaxY = dy != 0f ? (stepY > 0 ? cy + 1 - ly : ly - cy) * tDeltaY : float.MaxValue;
            int guard = Math.Abs(ex - cx) + Math.Abs(ey - cy) + 2;

            for (int n = 0; n <= guard; n++)
            {
                if (Contains(cx, cy))
                {
                    int i = Index(cx, cy);
                    bool end = (cx == sx && cy == sy) || (cx == ex && cy == ey);
                    if (!end && _blockTop[i] != NoBlock && _blockTop[i] > limit) return 0f;

                    if (_hTop[i] != NoBlock && _hTop[i] > limit)
                    {
                        float wy = cy + _hOff[i];
                        if ((ly - wy) * (py - wy) < 0f)
                        {
                            float cross = lx + (wy - ly) / dy * dx;
                            if (cross >= cx && cross <= cx + 1) return 0f;
                        }
                    }

                    if (_vTop[i] != NoBlock && _vTop[i] > limit)
                    {
                        float wx = cx + _vOff[i];
                        if ((lx - wx) * (px - wx) < 0f)
                        {
                            float cross = ly + (wx - lx) / dx * dy;
                            if (cross >= cy && cross <= cy + 1) return 0f;
                        }
                    }
                }

                if (cx == ex && cy == ey) break;
                if (tMaxX < tMaxY)
                {
                    tMaxX += tDeltaX;
                    cx += stepX;
                }
                else
                {
                    tMaxY += tDeltaY;
                    cy += stepY;
                }
            }

            return 1f;
        }

        private ulong HashBlockers()
        {
            // FNV-1a over the blocker tops and the grid origin.
            ulong h = 14695981039346656037UL;
            unchecked
            {
                h = (h ^ (ulong)(uint)_originX) * 1099511628211UL;
                h = (h ^ (ulong)(uint)_originY) * 1099511628211UL;
                h = (h ^ (ulong)(uint)_fineScale) * 1099511628211UL;
                for (int i = 0; i < _blockTop.Length; i++)
                {
                    h = (h ^ (ulong)(ushort)_blockTop[i]) * 1099511628211UL;
                    h = (h ^ (ulong)(ushort)_hTop[i]) * 1099511628211UL;
                    h = (h ^ (ulong)(ushort)_vTop[i]) * 1099511628211UL;
                    if (_hTop[i] != NoBlock) h = (h ^ (ulong)BitConverter.SingleToUInt32Bits(_hOff[i])) * 1099511628211UL;
                    if (_vTop[i] != NoBlock) h = (h ^ (ulong)BitConverter.SingleToUInt32Bits(_vOff[i])) * 1099511628211UL;
                }
            }
            return h;
        }
    }
}
