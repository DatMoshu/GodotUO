// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using Godot;
using GUO.Assets;

namespace GUO.Renderer
{
    /// <summary>
    /// Draws gaussian splats (generated multis and deco) as screen-aligned
    /// quads with a radial falloff, at the LOD level the camera's zoom pays
    /// for. Owns its item and meshes the way MeshLayer owns its run meshes.
    /// </summary>
    /// <remarks>
    /// The projection math mirrors the proven reference renderer
    /// (NewestCUO's JarJar.Render, same training convention): normalized
    /// Y-up models, (w,x,y,z) rotations, yaw-folded oblique map, painter's
    /// depth, 3-sigma quads, alpha floor, normalized falloff. Where GUO's
    /// canvas needs its own shape (baked tile-space meshes through
    /// UltimaBatcher2D instead of a device vertex buffer), the numbers going
    /// in are the reference's.
    /// </remarks>
    public sealed class SplatBatcher : IDisposable
    {
        private const string SHADER_PATH = "res://src/Render/shaders/splat_2d.gdshader";

        // SH_C0: DC term to diffuse colour, the standard approximation.
        private const float SH_C0 = 0.2829f;

        private readonly Rid _item;
        private readonly ShaderMaterial _material;
        private SplatLodChain _chain;
        private Vector2 _worldPos;
        private float _worldSize = 1f;
        private readonly ArrayMesh[] _levelMeshes = new ArrayMesh[8];

        /// <summary>Level drawn by the last <see cref="Draw"/>, or -1 when culled.</summary>
        public int LastLevel { get; private set; } = -1;

        /// <summary>Splats in the last drawn mesh.</summary>
        public int DrawnSplats { get; private set; }

        /// <summary>Widest lod0 extent in splat units: what LOD math sizes by.</summary>
        public float WorldSize => _worldSize;

        public SplatBatcher(Rid parentCanvasItem)
        {
            _item = RenderingServer.CanvasItemCreate();
            RenderingServer.CanvasItemSetParent(_item, parentCanvasItem);
            _material = new ShaderMaterial { Shader = GD.Load<Shader>(SHADER_PATH) };
            RenderingServer.CanvasItemSetMaterial(_item, _material.GetRid());
        }

        /// <summary>What to draw and where (screen position) and how big (splat units across).</summary>
        public void SetChain(SplatLodChain chain, Vector2 worldPos)
        {
            if (_chain != chain)
            {
                _chain = chain;
                Array.Clear(_levelMeshes, 0, _levelMeshes.Length);
                if (chain != null && chain.Count > 0)
                {
                    Vector3 size = chain.BoundsMax - chain.BoundsMin;
                    _worldSize = Math.Max(size.X, Math.Max(size.Y, size.Z));
                    if (_worldSize <= 0)
                    {
                        _worldSize = 1f;
                    }
                }
            }

            _worldPos = worldPos;
        }

        /// <summary>
        /// Selects the LOD for this zoom, rebuilds the level mesh once, draws
        /// it on the batcher's own item (the batcher probe's pixel check and
        /// any screen-space caller). The live scene instead bakes tile-space
        /// quads via <see cref="BuildBaked"/> and draws them through
        /// <c>UltimaBatcher2D.DrawSplatMesh</c>, like every working mesh.
        /// </summary>
        public void Draw(float zoom)
        {
            LastLevel = -1;
            DrawnSplats = 0;
            RenderingServer.CanvasItemClear(_item);
            if (_chain == null || _chain.Count == 0)
            {
                return;
            }

            int level = _chain.Select(SplatLodChain.PixelRadius(_worldSize, zoom));
            if (level < 0)
            {
                return;
            }

            LastLevel = level;
            if (_levelMeshes[level] == null)
            {
                _levelMeshes[level] = Build(_chain.Levels[level]);
            }

            RenderingServer.CanvasItemSetTransform(_item, new Transform2D(
                new Vector2(zoom, 0f), new Vector2(0f, zoom), _worldPos));
            RenderingServer.CanvasItemAddMesh(_item, _levelMeshes[level].GetRid());
            DrawnSplats = _chain.Levels[level].Gaussians.Length;
        }

        /// <summary>
        /// Shared baked meshes: identical (Chain, level, scale, yaw, zoom,
        /// disc) placements draw ONE ArrayMesh, baked LOCAL (origin-relative)
        /// with the placement offset riding the draw transform (like every
        /// mesh under the view transform). Walking past a splat is then one
        /// draw call with zero uploads; the old per-instance mesh plus the
        /// per-frame MoveBaked rewrite are gone.
        /// </summary>
        private readonly struct BakeKey : IEquatable<BakeKey>
        {
            public readonly SplatLodChain Chain;
            public readonly int Level;
            public readonly int ScaleBits;
            public readonly int YawBits;
            public readonly int ZoomBits;
            public readonly int DiscBits;

            public BakeKey(SplatLodChain chain, int level, float scale, float yawDeg, float zoom, float disc)
            {
                Chain = chain;
                Level = level;
                ScaleBits = BitConverter.SingleToInt32Bits(scale);
                YawBits = BitConverter.SingleToInt32Bits(yawDeg);
                ZoomBits = BitConverter.SingleToInt32Bits(zoom);
                DiscBits = BitConverter.SingleToInt32Bits(disc);
            }

            public bool Equals(BakeKey other) =>
                ReferenceEquals(Chain, other.Chain) && Level == other.Level
                && ScaleBits == other.ScaleBits && YawBits == other.YawBits
                && ZoomBits == other.ZoomBits && DiscBits == other.DiscBits;

            public override bool Equals(object obj) => obj is BakeKey o && Equals(o);

            public override int GetHashCode()
            {
                int h = Chain != null ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Chain) : 0;
                h = (h * 397) ^ Level;
                h = (h * 397) ^ ScaleBits;
                h = (h * 397) ^ YawBits;
                h = (h * 397) ^ ZoomBits;
                h = (h * 397) ^ DiscBits;
                return h;
            }
        }

        private sealed class SharedBake
        {
            public ArrayMesh Mesh;
            public bool IsBright;
            public int Quads;
            public int Built, SkipAlpha, SkipDegen, SkipData;
        }

        private static readonly Dictionary<BakeKey, SharedBake> _sharedCache = new();

        /// <summary>
        /// Entries in <see cref="_sharedCache"/> (the smoke check sizes it).
        /// </summary>
        public static int SharedCacheCount => _sharedCache.Count;

        /// <summary>Forgets every shared bake (stage reload, tests).</summary>
        public static void ClearSharedCache()
        {
            _sharedCache.Clear();
        }

        /// <summary>
        /// The shared LOCAL bake for (chain, level, scale, yaw, zoom, disc):
        /// same bytes <see cref="BakeLevel"/>+<see cref="UploadBaked"/> at
        /// (0,0) produce, drawn with the placement offset on the mesh
        /// transform. Zoom rides the key because the bake clamp does
        /// ([0.25,96]*zoom); within a frame every placement shares one zoom
        /// so identical (Chain, level, scale, yaw, disc) placements still
        /// share one mesh.
        /// </summary>
        public static ArrayMesh GetSharedBaked(
            SplatLodChain chain, int level, float scale, float yawDeg, float zoom,
            float discSize, out bool isBright, out int quads)
        {
            isBright = false;
            quads = 0;
            if (chain == null || level < 0 || level >= chain.Count)
            {
                return null;
            }

            float disc = Finite(discSize) && discSize > 0f ? discSize : 1f;
            float z = Finite(zoom) ? Math.Max(zoom, 0.01f) : 1f;
            var key = new BakeKey(chain, level, scale, yawDeg, z, disc);
            if (_sharedCache.TryGetValue(key, out SharedBake hit) && hit?.Mesh != null)
            {
                LastBuilt = hit.Built;
                LastSkippedAlpha = hit.SkipAlpha;
                LastSkippedDegenerate = hit.SkipDegen;
                LastSkippedData = hit.SkipData;
                isBright = hit.IsBright;
                quads = hit.Quads;
                return hit.Mesh;
            }

            SplatSet set = chain.Levels[level];
            if (set == null)
            {
                return null;
            }

            BakeLevel(set, scale, yawDeg, z,
                out float[] pos, out Color[] colors, out Vector2[] uvs, out int q, disc);
            var mesh = new ArrayMesh();
            UploadBaked(mesh, pos, colors, uvs, q, 0f, 0f);
            // Godot's canvas cull (Item::get_rect) builds a mesh command's
            // bounds from the mesh AABB alone and ignores the per-mesh offset
            // transform DrawSplatMesh draws with. A LOCAL bake therefore culls
            // as if at the origin: the origin leaves the viewport when zooming
            // in, so on-screen splats vanished zoomed in while drawing zoomed
            // out. Override the bounds so the engine never culls these meshes;
            // the queue-path rect cull already gates off-screen placements.
            mesh.CustomAabb = new Aabb(
                new Vector3(-100000f, -100000f, -1000f),
                new Vector3(200000f, 200000f, 2000f));
            bool bright = IsBright(colors);
            _sharedCache[key] = new SharedBake
            {
                Mesh = mesh,
                IsBright = bright,
                Quads = q,
                Built = LastBuilt,
                SkipAlpha = LastSkippedAlpha,
                SkipDegen = LastSkippedDegenerate,
                SkipData = LastSkippedData,
            };
            isBright = bright;
            quads = q;
            return mesh;
        }

        private static ArrayMesh Build(SplatSet set) => BuildBaked(set, 0f, 0f, 22f, 180f, 1f, new ArrayMesh());

        /// <summary>
        /// Full-density override (GUO_SPLAT_FULL=1): level 0 always, so every
        /// gaussian draws. Proof mode for judging fidelity, not for playing.
        /// </summary>
        public static bool ForceFull =>
            System.Environment.GetEnvironmentVariable("GUO_SPLAT_FULL") == "1";

        /// <summary>
        /// The LOD level for a camera zoom value and px/unit scale: the
        /// radius is the full normalized 2-unit span in screen px, so a splat
        /// keeps the level its footprint earned instead of shrinking to its
        /// measured extent (a fragment would cull itself at zoom 1). The zoom
        /// value runs opposite to magnification (ZoomIn decreases it; the
        /// view transform scales by 1/zoom), so the radius divides by it:
        /// zooming out picks smaller levels, zooming in picks denser ones.
        /// </summary>
        public static int SelectLevel(SplatLodChain chain, float zoom, float scale)
        {
            if (ForceFull)
            {
                return 0;
            }

            return chain.Select(SplatLodChain.PixelRadius(2f, scale / System.Math.Max(zoom, 0.01f)));
        }

        /// <summary>Last build's accounting (the fidelity proof): kept, skipped by cause.</summary>
        public static int LastBuilt, LastSkippedAlpha, LastSkippedDegenerate, LastSkippedData;

        /// <summary>
        /// Baked brightness probe (diagnostics/fidelity only): whether any
        /// baked vertex reaches this max channel. It must never gate AddLight
        /// on its own — ordinary sunlit art trips any such threshold, so
        /// emission is victim-IsLight-only (see SplatObject.Draw); a
        /// tree/rock/wall replacement never emits regardless of this flag.
        /// </summary>
        public const float BrightThreshold = 0.85f;

        /// <summary>Whether any baked vertex reaches <see cref="BrightThreshold"/> (probe only, not a light gate).</summary>
        public static bool IsBright(Color[] colors)
        {
            if (colors == null)
            {
                return false;
            }

            for (int i = 0; i < colors.Length; i++)
            {
                Color c = colors[i];
                if (c.R >= BrightThreshold || c.G >= BrightThreshold || c.B >= BrightThreshold)
                {
                    return true;
                }
            }

            return false;
        }

        private static int[] _scratchOrder = System.Array.Empty<int>();
        private static float[] _scratchKeys = System.Array.Empty<float>();

        /// <summary>
        /// Model-space point through the yaw'd oblique map, in tile-space
        /// pixels relative to the placement origin. The yaw lives in the J
        /// rows only (reference parity): positions stay raw, like the
        /// ellipse frame. Only the painter's key yaw-folds.
        /// </summary>
        public static void IsoPoint(float scale, float yawDeg, float mx, float my, float mz, out float sx, out float sy)
        {
            double yw = yawDeg * Math.PI / 180.0;
            IsoPointW(scale, (float)Math.Cos(yw), (float)Math.Sin(yw), mx, my, mz, out sx, out sy);
        }

        /// <summary>IsoPoint with the yaw trig precomputed (the bake loop's hot path).</summary>
        internal static void IsoPointW(float scale, float cyw, float syw, float mx, float my, float mz, out float sx, out float sy)
        {
            sx = scale * ((cyw + syw) * mx + (syw - cyw) * mz);
            sy = scale * ((cyw - syw) * mx - my + (syw + cyw) * mz);
        }

        /// <summary>Model yaw about vertical (matches the ellipse frame).</summary>
        public static void YawFold(float yawDeg, float mx, float mz, out float x1, out float z1)
        {
            double yw = yawDeg * Math.PI / 180.0;
            double cyw = Math.Cos(yw), syw = Math.Sin(yw);
            x1 = (float)(mx * cyw + mz * syw);
            z1 = (float)(-mx * syw + mz * cyw);
        }

        /// <summary>
        /// World shift that moves a placement's projection by (dx, dy)
        /// tile-space px at this scale/yaw (the MoGe replacer's tile-anchor
        /// fix: sprite bbox-center vs the tile origin RSP). Depth stays put
        /// (dmz 0) unless the yaw folds X away (then X rides depth instead).
        /// </summary>
        public static void AnchorShift(
            float dx, float dy, float scale, float yawDeg,
            out float dmx, out float dmy, out float dmz)
        {
            double yw = yawDeg * Math.PI / 180.0;
            float cyw = (float)Math.Cos(yw), syw = (float)Math.Sin(yw);
            float s = scale != 0f ? scale : 1f;
            float jx0 = cyw + syw, jx2 = syw - cyw;
            float jy0 = cyw - syw, jy2 = syw + cyw;
            if (Math.Abs(jx0) > 0.1f)
            {
                dmz = 0f;
                dmx = dx / s / jx0;
                // With dmz 0: dx/s = jx0*dmx, dy/s = jy0*dmx - dmy.
                dmy = jy0 * dmx - dy / s;
            }
            else
            {
                dmx = 0f;
                dmz = dx / s / jx2;
                dmy = jy2 * dmz - dy / s;
            }
        }

        /// <summary>
        /// Bakes depth-sorted ellipse quads in tile space around
        /// (<paramref name="ox"/>, <paramref name="oy"/>) at
        /// <paramref name="scale"/> px per model unit: the expensive step
        /// (sort, covariance, eigen) plus the full surface upload. Live
        /// placements instead share a LOCAL bake via
        /// <see cref="GetSharedBaked"/> and ride the draw transform, so
        /// camera moves cost zero uploads (MoveBaked is a no-op).
        /// The yaw lives in the J rows only (reference parity): raw
        /// positions, raw rotations; only the painter's key yaw-folds.
        /// <paramref name="discSize"/> multiplies the rasterized radii after
        /// the bake clamp (the placer gump's Disc stepper).
        public static ArrayMesh BuildBaked(
            SplatSet set, float ox, float oy, float scale, float yawDeg, float zoom, ArrayMesh mesh, float discSize = 1f)
        {
            BakeLevel(set, scale, yawDeg, zoom,
                out float[] pos, out Color[] colors, out Vector2[] uvs, out int quads, discSize);
            UploadBaked(mesh, pos, colors, uvs, quads, ox, oy);
            return mesh;
        }

        /// <summary>
        /// The expensive half of a bake: sort, covariance, eigen, colours
        /// and uvs. Positions come out LOCAL (model origin); the placement
        /// offset lands at upload time, or on the draw transform for shared
        /// bakes, so this runs only when the level, scale, yaw, zoom or disc
        /// change, never on camera move.
        /// </summary>
        public static void BakeLevel(
            SplatSet set, float scale, float yawDeg, float zoom,
            out float[] pos, out Color[] colors, out Vector2[] uvs, out int quads, float discSize = 1f)
        {
            // Six vertices per splat (two triangles, no index array: the canvas
            // mesh path takes none, as MeshLayer's scratch rebuild notes).
            int n = set.Gaussians.Length;
            if (_scratchOrder.Length < n)
            {
                _scratchOrder = new int[n];
            }

            if (_scratchKeys.Length < n)
            {
                _scratchKeys = new float[n];
            }

            // The yaw trig once: YawFold per gaussian is a linear fold with
            // these, and the sort below must not pay cos/sin per comparison
            // (millions of calls on a full level).
            double yw = yawDeg * System.Math.PI / 180.0;
            float cyw = (float)System.Math.Cos(yw), syw = (float)System.Math.Sin(yw);

            SplatGaussian[] g = set.Gaussians;
            int[] order = _scratchOrder;
            float[] keys = _scratchKeys;
            for (int i = 0; i < n; i++)
            {
                order[i] = i;
                keys[i] = (g[i].X * cyw + g[i].Z * syw) + (-g[i].X * syw + g[i].Z * cyw) + g[i].Y;
            }

            // Painter's depth like the reference: yaw-folded x1+z1 plus
            // height, ascending (far first), index order breaking ties.
            System.Array.Sort(order, 0, n, System.Collections.Generic.Comparer<int>.Create((a, b) =>
            {
                int c = keys[a].CompareTo(keys[b]);
                return c != 0 ? c : a.CompareTo(b);
            }));

            pos = new float[n * 6 * 3];
            colors = new Color[n * 6];
            uvs = new Vector2[n * 6];
            quads = 0;
            int skipAlpha = 0, skipDegen = 0, skipData = 0;
            // User disc size (the placer gump's Disc stepper): multiplies the
            // rasterized radii after the per-splat clamp, so 4x can bloom past
            // the cap and 0.25x can tighten below the floor. Sanitized once.
            float disc = Finite(discSize) && discSize > 0f ? discSize : 1f;
            for (int k = 0; k < n; k++)
            {
                SplatGaussian s = g[order[k]];
                if (!Finite(s.X) || !Finite(s.Y) || !Finite(s.Z) || !Finite(s.Opacity)
                    || !Finite(s.Dc0) || !Finite(s.Dc1) || !Finite(s.Dc2)
                    || !Finite(s.Scale0) || !Finite(s.Scale1) || !Finite(s.Scale2)
                    || !Finite(s.Rot0) || !Finite(s.Rot1) || !Finite(s.Rot2) || !Finite(s.Rot3))
                {
                    skipData++;
                    continue;
                }

                float alpha = 1f / (1f + MathF.Exp(-s.Opacity));
                if (alpha < 0.03f)
                {
                    skipAlpha++;
                    continue;
                }

                if (alpha < 20f / 255f)
                {
                    alpha = 20f / 255f;
                }

                float fx = s.X, fz = s.Z;
                if (!ScreenEllipse(scale, cyw, syw, fx, s.Y, fz, s, out float rx, out float ry, out float cosA, out float sinA)
                    || rx <= 0f || ry <= 0f)
                {
                    skipDegen++;
                    continue;
                }

                // Like the reference: 3σ quads clamped to [0.25, 96] screen px.
                // Bake space is unzoomed tile px and the view transform scales
                // by 1/zoom, so the bake clamp is [0.25 * zoom, 96 * zoom].
                // The quarter-pixel floor (not 1px) lets dense pixel-billboards
                // (MoGe: one gaussian per infer pixel, sigma 0.25px) combine
                // instead of being re-enlarged to a full pixel, which erased
                // the tool-side sharpening at small scales. Sparse distant
                // splats still cannot vanish (0.25px radius is 0.5px across).
                float z = Math.Max(zoom, 0.01f);
                float minR = 0.25f * z, maxR = 96f * z;
                rx = rx < minR ? minR : rx > maxR ? maxR : rx;
                ry = ry < minR ? minR : ry > maxR ? maxR : ry;
                rx *= disc;
                ry *= disc;
                IsoPointW(scale, cyw, syw, fx, s.Y, fz, out float px, out float py);
                // Straight (non-premultiplied) colour: the shader blends it
                // normally, like the reference.
                var c = new Color(
                    Clamp01(0.5f + SH_C0 * s.Dc0),
                    Clamp01(0.5f + SH_C0 * s.Dc1),
                    Clamp01(0.5f + SH_C0 * s.Dc2),
                    alpha);
                // Rotated corners: ex along the major axis, ey the minor.
                // Local (model origin): the placement offset lands at upload.
                float exx = cosA * rx, exy = sinA * rx;
                float eyx = -sinA * ry, eyy = cosA * ry;
                int v = quads * 6, p3 = quads * 6 * 3;
                // MeshLayer.WriteTriangles' winding: 0 1 2, 1 3 2.
                pos[p3 + 0] = px - exx - eyx; pos[p3 + 1] = py - exy - eyy; pos[p3 + 2] = 0f;
                pos[p3 + 3] = px + exx - eyx; pos[p3 + 4] = py + exy - eyy; pos[p3 + 5] = 0f;
                pos[p3 + 6] = px - exx + eyx; pos[p3 + 7] = py - exy + eyy; pos[p3 + 8] = 0f;
                pos[p3 + 9] = px + exx - eyx; pos[p3 + 10] = py + exy - eyy; pos[p3 + 11] = 0f;
                pos[p3 + 12] = px + exx + eyx; pos[p3 + 13] = py + exy + eyy; pos[p3 + 14] = 0f;
                pos[p3 + 15] = px - exx + eyx; pos[p3 + 16] = py - exy + eyy; pos[p3 + 17] = 0f;
                for (int j = 0; j < 6; j++)
                {
                    colors[v + j] = c;
                }

                // 0..1 across the quad: the shader maps these to -1..1 for
                // the gaussian falloff, so the centre interpolates to (0, 0).
                uvs[v + 0] = new Vector2(0f, 0f);
                uvs[v + 1] = new Vector2(1f, 0f);
                uvs[v + 2] = new Vector2(0f, 1f);
                uvs[v + 3] = new Vector2(1f, 0f);
                uvs[v + 4] = new Vector2(1f, 1f);
                uvs[v + 5] = new Vector2(0f, 1f);
                quads++;
            }

            LastBuilt = quads;
            LastSkippedAlpha = skipAlpha;
            LastSkippedDegenerate = skipDegen;
            LastSkippedData = skipData;
            Array.Resize(ref pos, quads * 6 * 3);
            Array.Resize(ref colors, quads * 6);
            Array.Resize(ref uvs, quads * 6);
        }

        /// <summary>
        /// Full surface upload of a <see cref="BakeLevel"/> result at a
        /// placement: colours and uvs go over whole, positions translated by
        /// the offset. Runs on level, scale or yaw change.
        /// </summary>
        public static void UploadBaked(
            ArrayMesh mesh, float[] pos, Color[] colors, Vector2[] uvs, int quads, float ox, float oy)
        {
            var verts = new Vector3[quads * 6];
            for (int i = 0, v = 0; i < quads * 6; i++, v += 3)
            {
                verts[i] = new Vector3(pos[v] + ox, pos[v + 1] + oy, pos[v + 2]);
            }

            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = verts;
            arrays[(int)Mesh.ArrayType.Color] = colors;
            arrays[(int)Mesh.ArrayType.TexUV] = uvs;
            mesh.ClearSurfaces();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        }

        /// <summary>
        /// Follows a camera move: no-op since the offset draws (the placement
        /// rides the mesh transform, so moves cost zero uploads). Kept so
        /// older callers still compile; new code draws the shared LOCAL bake
        /// with an offset instead of rewriting vertices.
        /// </summary>
        public static void MoveBaked(ArrayMesh mesh, float[] pos, int quads, float ox, float oy)
        {
        }

        /// <summary>
        /// The screen ellipse of a gaussian under the oblique map: J Σ Jᵀ
        /// eigen-decomposed. Rotation is (w, x, y, z) with rot_0 = w, per the
        /// training convention, used raw: the yaw lives in the J rows alone
        /// (reference parity). The yaw trig arrives precomputed (the bake
        /// loop computes it once). Sigmas arrive LINEAR (Normalize() already
        /// exp'd). Radii come out in tile-space pixels.
        /// </summary>
        internal static bool ScreenEllipse(
            float scale, float cyw, float syw, float fx, float fy, float fz,
            SplatGaussian g, out float rx, out float ry, out float cosA, out float sinA)
        {
            rx = ry = cosA = 1f;
            sinA = 0f;
            float w = g.Rot0, x = g.Rot1, y = g.Rot2, z = g.Rot3;
            float norm = MathF.Sqrt(x * x + y * y + z * z + w * w);
            if (!(norm > 1e-6f))
            {
                return false;
            }

            x /= norm;
            y /= norm;
            z /= norm;
            w /= norm;
            float ex = g.Scale0 * g.Scale0, ey = g.Scale1 * g.Scale1, ez = g.Scale2 * g.Scale2;
            if (!(ex > 0f) || !(ey > 0f) || !(ez > 0f))
            {
                return false;
            }

            float rxx = 1f - 2f * (y * y + z * z), rxy = 2f * (x * y - z * w), rxz = 2f * (x * z + y * w);
            float ryx = 2f * (x * y + z * w), ryy = 1f - 2f * (x * x + z * z), ryz = 2f * (y * z - x * w);
            float rzx = 2f * (x * z - y * w), rzy = 2f * (y * z + x * w), rzz = 1f - 2f * (x * x + y * y);
            float c00 = rxx * rxx * ex + rxy * rxy * ey + rxz * rxz * ez;
            float c01 = rxx * ryx * ex + rxy * ryy * ey + rxz * ryz * ez;
            float c02 = rxx * rzx * ex + rxy * rzy * ey + rxz * rzz * ez;
            float c11 = ryx * ryx * ex + ryy * ryy * ey + ryz * ryz * ez;
            float c12 = ryx * rzx * ex + ryy * rzy * ey + ryz * rzz * ez;
            float c22 = rzx * rzx * ex + rzy * rzy * ey + rzz * rzz * ez;
            // Mirror with the Y-flipped positions (reference parity):
            // orientations follow the same M=diag(1,-1,1) mirror.
            c01 = -c01;
            c12 = -c12;
            // S = J C J' (Jx has no Y component), with the yaw'd oblique rows:
            // jx0 = s(cyw+syw), jx2 = s(syw-cyw),
            // jy0 = s(cyw-syw), jy1 = -s, jy2 = s(syw+cyw).
            float jx0 = scale * (float)(cyw + syw), jx2 = scale * (float)(syw - cyw);
            float jy0 = scale * (float)(cyw - syw), jy1 = -scale, jy2 = scale * (float)(syw + cyw);
            float u0 = c00 * jx0 + c02 * jx2;
            float u2 = c02 * jx0 + c22 * jx2;
            float v0 = c00 * jy0 + c01 * jy1 + c02 * jy2;
            float v1 = c01 * jy0 + c11 * jy1 + c12 * jy2;
            float v2 = c02 * jy0 + c12 * jy1 + c22 * jy2;
            float sxx = jx0 * u0 + jx2 * u2;
            float sxy = jx0 * v0 + jx2 * v2;
            float syy = jy0 * v0 + jy1 * v1 + jy2 * v2;
            float trace = sxx + syy;
            float det = sxx * syy - sxy * sxy;
            float disc = MathF.Sqrt(Math.Max(0f, trace * trace * 0.25f - det));
            float l1 = trace * 0.5f + disc, l2 = trace * 0.5f - disc;
            if (!(l1 > 0f) || !(l2 > 0f))
            {
                return false;
            }

            rx = 3f * MathF.Sqrt(l1);
            ry = 3f * MathF.Sqrt(l2);
            float ang = 0.5f * MathF.Atan2(2f * sxy, sxx - syy);
            cosA = MathF.Cos(ang);
            sinA = MathF.Sin(ang);
            return Finite(rx) && Finite(ry);
        }

        private static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

        private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;

        public void Dispose()
        {
            RenderingServer.CanvasItemClear(_item);
            RenderingServer.FreeRid(_item);
        }
    }
}
