// SPDX-License-Identifier: BSD-2-Clause
// GUO-owned (ADR-0039 sun-shadow addendum): sky shadows in the ambient of the light field.

using System;
using System.Collections.Generic;

namespace GUO.Renderer.Lights
{
    internal sealed partial class RealtimeLightField
    {
        /// <summary>
        /// The sky light's shadow for a frame: <see cref="DirX"/>, <see cref="DirY"/>
        /// the unit direction a shadow falls in tile space, <see cref="TanElevation"/>
        /// the light's height, <see cref="Strength"/> how much of the ambient a full
        /// shadow takes away (0 is none).
        /// </summary>
        public readonly struct Sun
        {
            public readonly float DirX, DirY, TanElevation, Strength;

            public Sun(float dirX, float dirY, float tanElevation, float strength)
            {
                float len = MathF.Sqrt(dirX * dirX + dirY * dirY);
                DirX = len > 0f ? dirX / len : 0f;
                DirY = len > 0f ? dirY / len : 1f;
                TanElevation = Math.Max(MinTanElevation, tanElevation);
                Strength = Math.Clamp(strength, 0f, 1f);
            }

            public bool Active => Strength > 0.001f;
        }

        /// <summary>World height of one tile of ground, in Z: a wall of 20 Z casts two tiles at 45 degrees.</summary>
        public const float ZPerTile = 10f;

        /// <summary>The longest sky shadow, in tiles.</summary>
        public const float MaxShadowTiles = 6f;

        /// <summary>Lowest sun: shadows stop at <see cref="MaxShadowTiles"/> long before this matters.</summary>
        public const float MinTanElevation = 0.12f;

        /// <summary>A caster this many Z above what it must clear gives a full shadow; less, a soft tip.</summary>
        public const float ShadowSoftZ = 4f;

        /// <summary>A roof's eave reaches this far above its base Z.</summary>
        public const int RoofCasterZ = 10;

        /// <summary>A mobile's shadow height, in Z (a character is about 16 Z tall).</summary>
        public const int MobileCasterZ = 14;

        /// <summary>Shadow mask samples per tile side (the march is the costly part; the mask is read per fine sample).</summary>
        private const int MaskScale = 2;

        private readonly struct Caster
        {
            public readonly float X, Y;
            public readonly int Z, Height;

            public Caster(float x, float y, int z, int height)
            {
                X = x; Y = y; Z = z; Height = height;
            }
        }

        private Sun _sun;
        private short[] _casterTop = Array.Empty<short>();
        private readonly List<Caster> _casters = new();
        private float[] _mask = Array.Empty<float>();
        private ulong _maskKey;
        private float[] _sunK = Array.Empty<float>();
        private bool _sunApplied;

        /// <summary>Shadow-mask recomputations by the last <see cref="Compute"/> (the cost log; 0 is a cache hit).</summary>
        public int LastShadowComputed { get; private set; }

        /// <summary>The sky shadow for the next <see cref="Compute"/>; default is none.</summary>
        public void SetSun(in Sun sun) => _sun = sun;

        /// <summary>
        /// Something that throws a sky shadow but does not block lamp light
        /// (foliage, a tall static): its top Z. Walls, blockers and roofs cast already.
        /// </summary>
        public void AddCaster(int x, int y, int topZ)
        {
            if (!Contains(x, y)) return;
            int i = Index(x, y);
            short top = (short)Math.Clamp(topZ, short.MinValue + 1, short.MaxValue);
            if (top > _casterTop[i]) _casterTop[i] = top;
        }

        /// <summary>A moving caster (a mobile) at world point (x, y) in tiles, standing at Z, this tall.</summary>
        public void AddMobileCaster(float x, float y, int z, int height = MobileCasterZ)
        {
            if (!Contains((int)MathF.Floor(x), (int)MathF.Floor(y))) return;
            _casters.Add(new Caster(x, y, z, height));
        }

        /// <summary>Begin's share: sized and cleared with the grid.</summary>
        private void BeginSun(int cells)
        {
            if (_casterTop.Length != cells) _casterTop = new short[cells];
            Array.Fill(_casterTop, NoBlock);
            _casters.Clear();
        }

        /// <summary>The tallest sky-shadow caster on a cell, in Z, or <see cref="NoBlock"/>.</summary>
        private short CasterTop(int i)
        {
            short top = _blockTop[i];
            if (_hTop[i] > top) top = _hTop[i];
            if (_vTop[i] > top) top = _vTop[i];
            if (_casterTop[i] > top) top = _casterTop[i];
            if (_roofZ[i] != NoBlock && _roofZ[i] + RoofCasterZ > top) top = (short)(_roofZ[i] + RoofCasterZ);
            return top;
        }

        /// <summary>
        /// After the ambient fill: darken each outdoor sample the sky light
        /// cannot see. The static mask (walls, roofs, trees) is cached on the
        /// blockers and the sun's angle; mobiles stamp their own each frame.
        /// Lamps add on top, so a torch lights a sky shadow as it lights the dark.
        /// </summary>
        private void ApplySun()
        {
            _sunApplied = false;
            LastShadowComputed = 0;
            if (!_sun.Active) return;

            int f = _fineScale, fw = _width * f, fh = _height * f, n = fw * fh;
            if (_sunK.Length != n) _sunK = new float[n];

            ulong key = MaskKey();
            int mw = _width * MaskScale, mh = _height * MaskScale;
            if (_mask.Length != mw * mh || key != _maskKey)
            {
                if (_mask.Length != mw * mh) _mask = new float[mw * mh];
                ComputeMask(mw, mh);
                _maskKey = key;
                LastShadowComputed = 1;
            }

            for (int sy = 0; sy < fh; sy++)
            {
                int my = sy * MaskScale / f;
                for (int sx = 0; sx < fw; sx++)
                {
                    _sunK[sy * fw + sx] = _mask[my * mw + sx * MaskScale / f];
                }
            }

            foreach (var c in _casters) StampMobile(in c, f, fw, fh);

            for (int i = 0, o = 0; i < n; i++, o += 3)
            {
                float k = 1f - _sun.Strength * _sunK[i];
                _sunK[i] = k;
                _fine[o] *= k;
                _fine[o + 1] *= k;
                _fine[o + 2] *= k;
            }

            _sunApplied = true;
        }

        /// <summary>The ambient a sample's floor is held at: its cell's, times its sky shadow.</summary>
        private float SunK(int sample) => _sunApplied && sample < _sunK.Length ? _sunK[sample] : 1f;

        private ulong MaskKey()
        {
            ulong h = _blockerHash;
            unchecked
            {
                // The sun moves slowly: a degree of azimuth, a step of height.
                int az = (int)MathF.Round(MathF.Atan2(_sun.DirY, _sun.DirX) * (180f / MathF.PI));
                int el = (int)MathF.Round(_sun.TanElevation * 50f);
                h = (h ^ (ulong)(uint)az) * 1099511628211UL;
                h = (h ^ (ulong)(uint)el) * 1099511628211UL;
                for (int i = 0; i < _casterTop.Length; i++)
                {
                    h = (h ^ (ulong)(ushort)_casterTop[i]) * 1099511628211UL;
                    h = (h ^ (ulong)(ushort)_roofZ[i]) * 1099511628211UL;
                    h = (h ^ (ulong)(byte)_groundZ[i]) * 1099511628211UL;
                }
            }
            return h;
        }

        /// <summary>March each mask sample toward the light; any caster tall enough on the way shades it.</summary>
        private void ComputeMask(int mw, int mh)
        {
            float step = 1f / MaskScale;
            float rise = _sun.TanElevation * ZPerTile; // Z a caster must clear per tile of distance
            float maxLen = Math.Min(MaxShadowTiles, 40f / rise);
            for (int my = 0; my < mh; my++)
            {
                for (int mx = 0; mx < mw; mx++)
                {
                    int cx = mx / MaskScale, cy = my / MaskScale, own = cy * _width + cx;
                    float shade = 0f;
                    // Under a roof: the roof share already holds the sky back.
                    if (!_roof[own])
                    {
                        float px = _originX + (mx + 0.5f) * step, py = _originY + (my + 0.5f) * step;
                        int gz = _groundZ[own];
                        for (float s = step; s <= maxLen && shade < 1f; s += step)
                        {
                            float qx = px - _sun.DirX * s, qy = py - _sun.DirY * s;
                            int tx = (int)MathF.Floor(qx) - _originX, ty = (int)MathF.Floor(qy) - _originY;
                            if (tx < 0 || ty < 0 || tx >= _width || ty >= _height) break;
                            int c = ty * _width + tx;
                            if (c == own) continue;
                            short top = CasterTop(c);
                            if (top == NoBlock) continue;
                            float over = top - gz - s * rise;
                            if (over > 0f) shade = Math.Max(shade, Math.Min(1f, over / ShadowSoftZ));
                        }
                    }

                    _mask[my * mw + mx] = shade;
                }
            }
        }

        /// <summary>A mobile's shadow: a soft capsule from its feet along the shadow direction.</summary>
        private void StampMobile(in Caster c, int f, int fw, int fh)
        {
            float len = Math.Min(4f, c.Height / (_sun.TanElevation * ZPerTile));
            const float radius = 0.32f, core = 0.16f;
            float ex = c.X + _sun.DirX * len, ey = c.Y + _sun.DirY * len;
            int x0 = (int)MathF.Floor((Math.Min(c.X, ex) - radius - _originX) * f);
            int x1 = (int)MathF.Ceiling((Math.Max(c.X, ex) + radius - _originX) * f);
            int y0 = (int)MathF.Floor((Math.Min(c.Y, ey) - radius - _originY) * f);
            int y1 = (int)MathF.Ceiling((Math.Max(c.Y, ey) + radius - _originY) * f);
            x0 = Math.Max(0, x0); y0 = Math.Max(0, y0);
            x1 = Math.Min(fw - 1, x1); y1 = Math.Min(fh - 1, y1);
            for (int sy = y0; sy <= y1; sy++)
            {
                float py = _originY + (sy + 0.5f) / f;
                for (int sx = x0; sx <= x1; sx++)
                {
                    float px = _originX + (sx + 0.5f) / f;
                    if (_roof[sy / f * _width + sx / f]) continue;
                    // Distance to the segment, and how far along it (the tip fades).
                    float vx = px - c.X, vy = py - c.Y;
                    float t = Math.Clamp((vx * _sun.DirX + vy * _sun.DirY) / Math.Max(0.001f, len), 0f, 1f);
                    float dx = vx - _sun.DirX * len * t, dy = vy - _sun.DirY * len * t;
                    float d = MathF.Sqrt(dx * dx + dy * dy);
                    if (d >= radius) continue;
                    float edge = d <= core ? 1f : 1f - (d - core) / (radius - core);
                    float v = edge * (1f - 0.6f * t);
                    int i = sy * fw + sx;
                    if (v > _sunK[i]) _sunK[i] = v;
                }
            }
        }
    }
}
