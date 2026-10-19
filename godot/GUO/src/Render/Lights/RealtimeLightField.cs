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

        /// <summary>The tint of an uncoloured light: warm, as a flame is.</summary>
        public static readonly Color WarmLight = new Color(1.0f, 0.82f, 0.58f);

        private const int CacheLimit = 512;

        public readonly struct Source
        {
            public readonly int X, Y, Z;
            public readonly int Radius;
            public readonly Color Color;

            public Source(int x, int y, int z, int radius, Color color)
            {
                X = x;
                Y = y;
                Z = z;
                Radius = Math.Clamp(radius, 1, MaxRadius);
                Color = color;
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
        /// never drop below the night floor so a torch has albedo to lift.
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
            return c;
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

            for (int i = 0; i < cells; i++)
            {
                float s = _roof[i] ? roofShare : 1f;
                _field[i * 3] = Ambient.R * s;
                _field[i * 3 + 1] = Ambient.G * s;
                _field[i * 3 + 2] = Ambient.B * s;
            }

            _blockerHash = HashBlockers();
            LastComputed = 0;

            if (_cache.Count > CacheLimit) _cache.Clear();

            for (int n = 0; n < sources.Length; n++)
            {
                ref readonly Source s = ref sources[n];
                if (!Contains(s.X, s.Y)) continue;

                var key = new CacheKey(in s);
                if (!_cache.TryGetValue(key, out Reach reach) || reach.BlockerHash != _blockerHash)
                {
                    reach ??= new Reach();
                    reach.Map = ComputeReach(in s, reach.Map);
                    reach.BlockerHash = _blockerHash;
                    _cache[key] = reach;
                    LastComputed++;
                }

                reach.Frame = _frame;
                Accumulate(in s, reach.Map);
            }

            // Forget lights that have gone out (not seen this frame).
            if (_cache.Count > 0 && (_frame & 63) == 0)
            {
                var dead = new List<CacheKey>();
                foreach (var kv in _cache) if (kv.Value.Frame != _frame) dead.Add(kv.Key);
                foreach (var k in dead) _cache.Remove(k);
            }
        }

        /// <summary>The field at a cell (clamped to the grid).</summary>
        public Color Cell(int x, int y)
        {
            int cx = Math.Clamp(x - _originX, 0, _width - 1);
            int cy = Math.Clamp(y - _originY, 0, _height - 1);
            int i = (cy * _width + cx) * 3;
            return new Color(_field[i], _field[i + 1], _field[i + 2]);
        }

        /// <summary>
        /// The field for a static at height <paramref name="z"/> on a cell: the
        /// cell's light, or the roof ambient when it stands above every light
        /// that reaches the cell (a roof over a lamp, a floor above).
        /// </summary>
        public Color CellAt(int x, int y, int z) => IsAboveLight(x, y, z) ? RoofAmbient : Cell(x, y);

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
        public Color Corner(int x, int y)
        {
            Color a = Cell(x - 1, y - 1), b = Cell(x, y - 1), c = Cell(x - 1, y), d = Cell(x, y);
            return new Color(
                (a.R + b.R + c.R + d.R) * 0.25f,
                (a.G + b.G + c.G + d.G) * 0.25f,
                (a.B + b.B + c.B + d.B) * 0.25f);
        }

        /// <summary>Radial falloff: 1 at the source, 0 at the radius, soft in between.</summary>
        public static float Falloff(float distance, float radius)
        {
            float t = Math.Clamp(1f - distance / radius, 0f, 1f);
            return t * t * (3f - 2f * t);
        }

        private float[] ComputeReach(in Source s, float[] map)
        {
            int r = s.Radius;
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
                    int tx = s.X + dx, ty = s.Y + dy;
                    float vis = Contains(tx, ty) && dx * dx + dy * dy <= r * r
                        ? Visible(s.X, s.Y, s.Z, tx, ty)
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
                    float d = MathF.Sqrt(dx * dx + dy * dy);
                    float v = _scratchBlur[i] * Falloff(d, r + 0.5f);
                    // Ground far below or above the light takes less.
                    int tx = s.X + dx, ty = s.Y + dy;
                    if (Contains(tx, ty))
                    {
                        int dz = Math.Abs(_groundZ[Index(tx, ty)] - s.Z);
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

        private void Accumulate(in Source s, float[] map)
        {
            int r = s.Radius;
            int side = r * 2 + 1;
            for (int dy = -r; dy <= r; dy++)
            {
                int ty = s.Y + dy;
                if (ty < _originY || ty >= _originY + _height) continue;
                for (int dx = -r; dx <= r; dx++)
                {
                    int tx = s.X + dx;
                    if (tx < _originX || tx >= _originX + _width) continue;
                    float v = map[(dy + r) * side + (dx + r)];
                    if (v <= 0f) continue;
                    int cell = Index(tx, ty);
                    if (s.Z > _lightTop[cell]) _lightTop[cell] = (short)s.Z;
                    int i = cell * 3;
                    v *= Energy;
                    _field[i] += s.Color.R * v;
                    _field[i + 1] += s.Color.G * v;
                    _field[i + 2] += s.Color.B * v;
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
