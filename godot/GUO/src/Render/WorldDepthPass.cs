// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using Godot;

namespace GUO.Renderer
{
    /// <summary>
    /// PERF-GPU step 2 (ADR-0007, amendment 2026-10-19; key layout 2026-10-22):
    /// the world pass drawn into a 3D <see cref="SubViewport"/>, where the GPU
    /// orders quads by a depth key instead of the CPU sort ordering canvas
    /// commands. One world depth buffer; land stays on the canvas underneath.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three kinds of quad go in, all through the same vertex placement
    /// (world_pass.gdshaderinc) and the same hueing as the canvas
    /// (uo_hue_core.gdshaderinc):
    /// </para>
    /// <list type="bullet">
    /// <item><b>Persistent chunk statics</b>: a <see cref="MultiMesh"/> per
    /// (chunk, atlas page), written by <c>ChunkInstances</c> when the chunk
    /// changes and shown while the chunk is in the tile walk's range. Their
    /// keys are unique, so their buffers draw in any order (the opaque pass).</item>
    /// <item><b>Queued sprites</b>: whatever the batcher captures while
    /// RenderLists draws its sorted list. A sprite with a key no other sprite
    /// has this frame joins the opaque page buffer of its texture; a blended
    /// sprite, or a later holder of a key already used, goes to the ordered
    /// pass as a segment per page run, in submission order by render priority
    /// (WorldDepthOrder.Plan).</item>
    /// <item><b>Covering land</b>: one mesh a frame over LandPages' array,
    /// the tiles' own baked vertices with their key in CUSTOM1.</item>
    /// </list>
    /// <para>
    /// The probe's step-1 paths (<see cref="AddQuad"/> with a raw key,
    /// <see cref="AddKeyedQuad"/> with a full 28-bit key) still exist: they
    /// are how launchers/dev/depth_probe.bat proves the encoding.
    /// </para>
    /// </remarks>
    public sealed class WorldDepthPass : IDisposable
    {
        private const string OPAQUE_SHADER_PATH = "res://src/Render/shaders/world_sprite_opaque.gdshader";
        private const string BLEND_SHADER_PATH = "res://src/Render/shaders/world_sprite_blend.gdshader";
        private const string LAND_SHADER_PATH = "res://src/Render/shaders/world_land.gdshader";

        // 12 floats of transform, 4 of colour, 4 of custom data.
        public const int Stride = 20;

        /// <summary>Ordered segments a frame may hold: one render priority each (-128..127, the top one spare).</summary>
        /// <summary>
        /// A sanity cap on ordered segments, not a render-priority limit: the
        /// segments order themselves by GeometryInstance3D.SortingOffset.
        /// </summary>
        public const int MaxSegments = 1 << 16;

        private const uint FLAG_INTEGER_KEY = 1;
        private const uint FLAG_CULL = 2;
        private const uint FLAG_TEXEL_RECT = 4;
        private const uint FLAG_ABSOLUTE = 8;

        /// <summary>The flags of a persistent static's instance: integer key, the walk's cull, texel rect, absolute corners.</summary>
        internal const uint StaticFlags = FLAG_INTEGER_KEY | FLAG_CULL | FLAG_TEXEL_RECT | FLAG_ABSOLUTE;

        private static Shader _opaqueShader, _blendShader, _landShader;

        private readonly SubViewport _viewport;
        private readonly Node3D _root;
        private readonly ArrayMesh _quad;

        private readonly Dictionary<Texture2D, ShaderMaterial> _pageMaterials = new();
        private readonly List<ShaderMaterial> _materials = new();
        private readonly Dictionary<string, Variant> _shared = new();

        private readonly Dictionary<Texture2D, InstanceBuffer> _dynamicPages = new();
        private readonly List<InstanceBuffer> _dynamicOrder = new();
        private readonly List<InstanceBuffer> _segments = new();
        private readonly List<ShaderMaterial> _segmentMaterials = new();

        private readonly List<ChunkSet> _shown = new();
        private readonly LandMesh _land;

        // This frame's captured sprites, in submission order.
        private float[] _captured = Array.Empty<float>();
        private Texture2D[] _capturedPage = Array.Empty<Texture2D>();
        private uint[] _capturedKey = Array.Empty<uint>();
        private bool[] _capturedBlended = Array.Empty<bool>();
        private int[] _capturedPageId = Array.Empty<int>();
        private readonly Dictionary<Texture2D, int> _pageIds = new();
        private bool[] _capturedOrdered = Array.Empty<bool>();
        private int _capturedCount;

        private Frame _frame;
        private bool _began;

        /// <summary>What the frame needs from the scene: the camera's view and the key's origins.</summary>
        public struct Frame
        {
            /// <summary>The camera's view, as the batcher puts it on the world target's item.</summary>
            public Transform2D View;
            /// <summary>The view with the draw offset composed in, for covering land (the canvas item with SetWorldOffset).</summary>
            public Transform2D LandView;
            public Vector2I Size;
            /// <summary>WorldDepthKey.DepthOrigin of the first diagonal that can show.</summary>
            public uint DepthOrigin;
            /// <summary>The smallest (x - y) the screen's left edge admits, less one.</summary>
            public int MinDiff;
            /// <summary>The tile walk's pixel cull in world pixels before the draw offset: min x, min y, max x, max y.</summary>
            public Vector4 Cull;
            /// <summary>GameScene's draw offset, which persistent instances subtract from their absolute corners.</summary>
            public Vector2 DrawOffset;
        }

        public WorldDepthPass(Node parent, Vector2I size)
        {
            _opaqueShader ??= GD.Load<Shader>(OPAQUE_SHADER_PATH);
            _blendShader ??= GD.Load<Shader>(BLEND_SHADER_PATH);
            _landShader ??= GD.Load<Shader>(LAND_SHADER_PATH);

            _viewport = new SubViewport
            {
                Size = size,
                OwnWorld3D = true,
                TransparentBg = true,
                RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
                Msaa3D = Godot.Viewport.Msaa.Disabled,
                ScreenSpaceAA = Godot.Viewport.ScreenSpaceAAEnum.Disabled,
                UseTaa = false,
                UseDebanding = false,
                Scaling3DScale = 1f,
            };

            // The vertex shader writes clip space itself; the camera only has
            // to exist for the viewport to draw its 3D world at all.
            var camera = new Camera3D
            {
                Projection = Camera3D.ProjectionType.Orthogonal,
                Size = 2f,
                Near = 0.05f,
                Far = 10f,
                Current = true,
                Environment = new Godot.Environment
                {
                    BackgroundMode = Godot.Environment.BGMode.ClearColor,
                    TonemapMode = Godot.Environment.ToneMapper.Linear,
                },
            };

            _root = new Node3D();
            _viewport.AddChild(camera);
            _viewport.AddChild(_root);
            parent.AddChild(_viewport);

            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            // The canvas renderer's rect: corners top-left, bottom-left,
            // bottom-right, top-right; triangles 0 1 2 and 0 2 3.
            arrays[(int)Mesh.ArrayType.Vertex] = new[]
            {
                new Vector3(0, 0, 0), new Vector3(0, 1, 0), new Vector3(1, 1, 0), new Vector3(1, 0, 0),
            };
            // White, so COLOR in the shader is the instance colour alone.
            arrays[(int)Mesh.ArrayType.Color] = new[] { Colors.White, Colors.White, Colors.White, Colors.White };
            arrays[(int)Mesh.ArrayType.Index] = new[] { 0, 1, 2, 0, 2, 3 };

            _quad = new ArrayMesh();
            _quad.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);

            _land = new LandMesh(_root, _landShader);
            _materials.Add(_land.Material);
        }

        public SubViewport Viewport => _viewport;

        public Texture2D Texture => _viewport.GetTexture();

        /// <summary>Clip y sign; Forward+ measured -1 (docs/perf/2026-10-19_depth_probe.md); the probe tries both.</summary>
        public float FlipY { get; set; } = -1f;

        /// <summary>True when the renderer's depth is reverse Z (larger nearer): measured; the probe tries both.</summary>
        public bool ReverseZ { get; set; } = true;

        /// <summary>
        /// Write the texels' sRGB bytes unconverted into an HDR viewport (so
        /// blends happen on the same values the canvas blends), instead of
        /// decoding to linear and letting the renderer encode back. Off: the
        /// path the depth probe proved exact for opaque art. The parity probe
        /// measures the other; --gpu-world-raw switches it.
        /// </summary>
        public bool RawOutput
        {
            get => _rawOutput;
            set
            {
                _rawOutput = value;
                _viewport.UseHdr2D = value;
                SetShared("raw_output", value ? 1f : 0f);
            }
        }
        private bool _rawOutput;

        /// <summary>Sprites captured this frame, and how many went to the ordered pass; for the perf probe.</summary>
        public int CapturedSprites => _capturedCount;
        public int OrderedSprites { get; private set; }
        public int SegmentCount { get; private set; }
        public int CoverTiles => _land.Count;

        /// <summary>True when this frame cannot be drawn through the pass (a key out of range, too many segments).</summary>
        public bool Failed { get; private set; }
        public string FailedWhy { get; private set; }

        /// <summary>Between Begin and End.</summary>
        public bool Began => _began;

        /// <summary>This frame's key origins (Frame), for the keys RenderLists plans.</summary>
        public uint FrameDepthOrigin => _frame.DepthOrigin;
        public int FrameMinDiff => _frame.MinDiff;

        /// <summary>
        /// Equal keys in one page buffer keep their submission order on their
        /// own: a buffer draws its instances in order and the depth test
        /// passes a tie (Godot's reverse-Z compare is greater-or-equal). The
        /// depth probe's all-ties scene checks it; off, every later holder of
        /// a key goes to the ordered pass whatever its page.
        /// </summary>
        public bool SamePageTiesInOrder { get; set; } = true;

        /// <summary>The frame cannot go through the pass; the canvas draws it. Said once in the log by GpuWorld.</summary>
        public void Abandon(string why) => Fail(why);

        public void Resize(Vector2I size)
        {
            if (_viewport.Size != size)
            {
                _viewport.Size = size;
            }
        }

        /// <summary>A uniform every material of the pass carries (the hue table, brightlight, the circle of transparency, water).</summary>
        public void SetShared(string name, Variant value)
        {
            _shared[name] = value;
            foreach (ShaderMaterial material in _materials)
            {
                material.SetShaderParameter(name, value);
            }
        }

        private ShaderMaterial NewMaterial(Shader shader)
        {
            var material = new ShaderMaterial { Shader = shader };
            foreach ((string name, Variant value) in _shared)
            {
                material.SetShaderParameter(name, value);
            }

            _materials.Add(material);
            return material;
        }

        /// <summary>The opaque material of a page, shared by every buffer that draws from it.</summary>
        internal ShaderMaterial PageMaterial(Texture2D page)
        {
            if (!_pageMaterials.TryGetValue(page, out ShaderMaterial material))
            {
                material = NewMaterial(_opaqueShader);
                BindPage(material, page);
                _pageMaterials.Add(page, material);
            }

            return material;
        }

        private static void BindPage(ShaderMaterial material, Texture2D page)
        {
            material.SetShaderParameter("page", page);
            // texpixel_size as the canvas renderer computes it (renderer_canvas_render_rd.cpp).
            material.SetShaderParameter("page_pixel",
                new Vector2((float)(1.0 / (float)page.GetWidth()), (float)(1.0 / (float)page.GetHeight())));
        }

        // ---- frame ---------------------------------------------------------------

        public void Begin(in Frame frame)
        {
            _frame = frame;
            _began = true;
            Failed = false;
            FailedWhy = null;
            _capturedCount = 0;
            OrderedSprites = 0;
            SegmentCount = 0;
            Resize(frame.Size);

            foreach (InstanceBuffer page in _dynamicOrder)
            {
                page.Count = 0;
            }

            foreach (ChunkSet set in _shown)
            {
                set.Touched = false;
            }

            _land.Begin();
        }

        /// <summary>The probe's Begin: a view only.</summary>
        public void Begin(Transform2D view)
        {
            Begin(new Frame
            {
                View = view,
                LandView = view,
                Size = _viewport.Size,
                DepthOrigin = 0,
                MinDiff = -100000,
                Cull = new Vector4(-1e9f, -1e9f, 1e9f, 1e9f),
            });
        }

        private void Fail(string why)
        {
            if (!Failed)
            {
                Failed = true;
                FailedWhy = why;
            }
        }

        /// <summary>A persistent chunk set is in the walk's range this frame.</summary>
        internal void Show(ChunkSet set)
        {
            if (!set.Touched)
            {
                set.Touched = true;
                if (!set.Shown)
                {
                    set.Shown = true;
                    _shown.Add(set);
                }
            }
        }

        /// <summary>
        /// One captured sprite: the four corners in world pixels as the batcher
        /// computed them for the canvas, the source rect (texels when
        /// <paramref name="texelRect"/>, the canvas rect command's path; else
        /// normalised, the triangle path's), the hue vector as GetHueVector
        /// built it, and the absolute key parts.
        /// </summary>
        public void AddSprite(Texture2D page, Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, Vector4 source,
            bool texelRect, Vector3 hue, uint depthAbs, uint tieAbs, int s)
        {
            if (Failed)
            {
                return;
            }

            if (!WorldDepthKey.Relative(depthAbs, tieAbs, s, _frame.DepthOrigin, _frame.MinDiff, out uint key))
            {
                Fail("a queued sprite's key is outside the frame's range");
                return;
            }

            int index = _capturedCount;
            Grow(index + 1);
            Span<float> f = _captured.AsSpan(index * Stride, Stride);
            Pack(f, p0, p1, p2, p3, depthAbs, tieAbs, s, 0, 0, FLAG_INTEGER_KEY | (texelRect ? FLAG_TEXEL_RECT : 0), hue, 0f, source);
            _capturedPage[index] = page;
            _capturedPageId[index] = PageId(page);
            _capturedKey[index] = key;
            _capturedBlended[index] = IsBlended(hue);
            _capturedCount = index + 1;
        }

        private int PageId(Texture2D page)
        {
            if (!_pageIds.TryGetValue(page, out int id))
            {
                id = _pageIds.Count;
                _pageIds.Add(page, id);
            }

            return id;
        }

        /// <summary>
        /// What cannot be drawn opaque: a faded sprite, one under the circle
        /// of transparency (GetHueVector adds 1 to the alpha), and the two
        /// modes that set their own alpha in the shader (shadow, spectral).
        /// </summary>
        public static bool IsBlended(Vector3 hue)
        {
            int mode = (int)hue.Y & 0x3F;
            return hue.Z != 1f || mode == ShaderHueTranslator.SHADER_SHADOW || mode == ShaderHueTranslator.SHADER_SPECTRAL;
        }

        /// <summary>
        /// One quad: <paramref name="dest"/> in world pixels, <paramref name="source"/>
        /// in texture pixels (as CanvasItemAddTextureRectRegion takes them),
        /// <paramref name="key"/> in 0..1, larger nearer. The probe's raw path.
        /// </summary>
        public void AddQuad(Texture2D texture, Rect2 dest, Rect2 source, float key)
            => AddProbeQuad(texture, dest, source, key, null, false);

        /// <summary>
        /// Full 28-bit key, split into its depth (15 bits) and tie (13 bits)
        /// parts for the instance buffer; the shader reconstructs the uint
        /// before the bit cast. The probe's exhaustive path.
        /// </summary>
        public void AddKeyedQuad(Texture2D texture, Rect2 dest, Rect2 source, uint key)
        {
            if (key >= WorldDepthKey.Count)
                throw new ArgumentOutOfRangeException(nameof(key));
            AddProbeQuad(texture, dest, source, 0f, key, false);
        }

        /// <summary>
        /// The probe's keyed quad with its key planned as the frame's are
        /// (WorldDepthOrder.Plan): equal keys go to the ordered pass by the
        /// same rule, so the all-ties scene checks what the frame does.
        /// </summary>
        public void AddPlannedQuad(Texture2D texture, Rect2 dest, Rect2 source, uint key)
        {
            if (key >= WorldDepthKey.Count)
                throw new ArgumentOutOfRangeException(nameof(key));
            AddProbeQuad(texture, dest, source, 0f, key, true);
        }

        private void AddProbeQuad(Texture2D texture, Rect2 dest, Rect2 source, float rawKey, uint? integerKey, bool planned)
        {
            int index = _capturedCount;
            Grow(index + 1);
            Span<float> f = _captured.AsSpan(index * Stride, Stride);
            var p0 = dest.Position;
            var p1 = new Vector2(dest.Position.X + dest.Size.X, dest.Position.Y);
            var p2 = new Vector2(dest.Position.X, dest.Position.Y + dest.Size.Y);
            var p3 = dest.Position + dest.Size;
            // Hue 0, mode NONE, alpha 1: the texel's own colour.
            var hue = new Vector3(0f, ShaderHueTranslator.SHADER_NONE, 1f);
            var src = new Vector4(source.Position.X, source.Position.Y, source.Size.X, source.Size.Y);
            if (integerKey is uint full)
            {
                Pack(f, p0, p1, p2, p3, full >> WorldDepthKey.TieBits, full & ((1u << WorldDepthKey.TieBits) - 1), 0, 0, 0,
                    FLAG_INTEGER_KEY | FLAG_TEXEL_RECT, hue, 0f, src);
            }
            else
            {
                Pack(f, p0, p1, p2, p3, 0, 0, 0, 0, 0, FLAG_TEXEL_RECT, hue, rawKey, src);
            }

            _capturedPage[index] = texture;
            _capturedPageId[index] = PageId(texture);
            // The step-1 probe draws in submission order within a page and
            // never two pages at one key; its quads go to the opaque pass as
            // unique keys. The all-ties scene plans its real keys instead.
            _capturedKey[index] = planned && integerKey is uint plannedKey ? plannedKey : (uint)index;
            _capturedBlended[index] = false;
            _capturedCount = index + 1;
        }

        /// <summary>The instance layout of world_sprite.gdshaderinc, written in RenderingServer's float order.</summary>
        internal static void Pack(Span<float> f, Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3,
            uint depthAbs, uint tieAbs, int s, int z, int diff, uint flags, Vector3 hue, float rawKey, Vector4 source)
        {
            // Rows of the Transform3D as the server lays them out: row 0 is
            // basis.x.x, basis.y.x, basis.z.x, origin.x -- the shader's
            // MODEL_MATRIX[0].x, [1].x, [2].x, [3].x.
            f[0] = p0.X; f[1] = p1.X; f[2] = p2.X; f[3] = p3.X;
            f[4] = p0.Y; f[5] = p1.Y; f[6] = p2.Y; f[7] = p3.Y;
            f[8] = depthAbs;
            f[9] = tieAbs;
            f[10] = (uint)s | ((uint)(z + 128) << 14);
            f[11] = (uint)(diff + 8192) | (flags << 14);
            float alpha = hue.Z;
            float mode = hue.Y;
            if (alpha > 1f)
            {
                // GetHueVector signals circle-of-transparency by adding 1 to
                // the alpha; the shader's mode byte carries it in bit 7, as
                // UltimaBatcher2D.Encode does.
                mode = (int)mode | 0x80;
                alpha -= 1f;
            }

            f[12] = hue.X;
            f[13] = mode;
            f[14] = alpha;
            f[15] = rawKey;
            f[16] = source.X; f[17] = source.Y; f[18] = source.Z; f[19] = source.W;
        }

        private void Grow(int count)
        {
            if (count * Stride <= _captured.Length)
            {
                return;
            }

            int capacity = Math.Max(256, Math.Max(count, _captured.Length / Stride * 2));
            Array.Resize(ref _captured, capacity * Stride);
            Array.Resize(ref _capturedPage, capacity);
            Array.Resize(ref _capturedKey, capacity);
            Array.Resize(ref _capturedBlended, capacity);
            Array.Resize(ref _capturedPageId, capacity);
            Array.Resize(ref _capturedOrdered, capacity);
        }

        /// <summary>A covering land tile, from its baked quad in the chunk's land layer.</summary>
        public void AddCoverTile(MeshLayer layer, int index, int page, Vector2 scale, Vector2 offset,
            uint depthAbs, uint tieAbs, int s)
        {
            if (Failed)
            {
                return;
            }

            if (!WorldDepthKey.Relative(depthAbs, tieAbs, s, _frame.DepthOrigin, _frame.MinDiff, out _))
            {
                Fail("a covering tile's key is outside the frame's range");
                return;
            }

            _land.Add(layer, index, page, scale, offset, depthAbs, tieAbs, s);
        }

        public void End()
        {
            if (!_began)
            {
                return;
            }

            _began = false;

            // Hide the chunk sets that left the walk's range.
            for (int i = _shown.Count - 1; i >= 0; i--)
            {
                ChunkSet set = _shown[i];
                if (!set.Touched || set.Disposed)
                {
                    set.Shown = false;
                    set.SetVisible(false);
                    _shown.RemoveAt(i);
                }
                else
                {
                    set.SetVisible(!Failed);
                    if (!Failed)
                    {
                        set.UploadDirty();
                    }
                }
            }

            if (!Failed)
            {
                Plan();
            }

            if (Failed)
            {
                // Nothing of this frame draws through the pass; the canvas
                // takes the whole sorted list. Leave the viewport empty.
                foreach (InstanceBuffer page in _dynamicOrder)
                {
                    page.Hide();
                }

                for (int i = 0; i < _segments.Count; i++)
                {
                    _segments[i].Hide();
                }

                _land.Hide();
                return;
            }

            Vector2I size = _viewport.Size;
            SetViewUniforms(size);

            foreach (InstanceBuffer page in _dynamicOrder)
            {
                page.Upload();
            }

            for (int i = 0; i < _segments.Count; i++)
            {
                if (i < SegmentCount)
                {
                    _segments[i].Upload();
                }
                else
                {
                    _segments[i].Hide();
                }
            }

            _land.Upload();
        }

        /// <summary>
        /// Splits this frame's captured sprites between the opaque page
        /// buffers and the ordered segments (WorldDepthOrder.Plan), and writes
        /// each into its buffer in submission order.
        /// </summary>
        private void Plan()
        {
            var keys = new ReadOnlySpan<uint>(_capturedKey, 0, _capturedCount);
            var blended = new ReadOnlySpan<bool>(_capturedBlended, 0, _capturedCount);
            var pages = new ReadOnlySpan<int>(_capturedPageId, 0, _capturedCount);
            bool planned = SamePageTiesInOrder
                ? WorldDepthOrder.Plan(keys, blended, pages, _capturedOrdered)
                : WorldDepthOrder.Plan(keys, blended, _capturedOrdered);
            if (!planned)
            {
                Fail("a queued sprite's key decreased: the key does not reproduce the sort");
                return;
            }

            int segment = -1;
            Texture2D segmentPage = null;
            for (int i = 0; i < _capturedCount; i++)
            {
                Texture2D page = _capturedPage[i];
                ReadOnlySpan<float> f = _captured.AsSpan(i * Stride, Stride);
                if (!_capturedOrdered[i])
                {
                    DynamicPage(page).Append(f);
                    continue;
                }

                OrderedSprites++;
                if (segment < 0 || !ReferenceEquals(segmentPage, page))
                {
                    segment++;
                    if (segment >= MaxSegments)
                    {
                        Fail("more ordered segments than the pass allows");
                        return;
                    }

                    segmentPage = page;
                    Segment(segment).Bind(page);
                }

                _segments[segment].Append(f);
            }

            SegmentCount = segment + 1;
        }

        private InstanceBuffer DynamicPage(Texture2D page)
        {
            if (!_dynamicPages.TryGetValue(page, out InstanceBuffer buffer))
            {
                buffer = new InstanceBuffer(_quad, _root, PageMaterial(page));
                _dynamicPages.Add(page, buffer);
                _dynamicOrder.Add(buffer);
            }

            return buffer;
        }

        private InstanceBuffer Segment(int index)
        {
            while (_segments.Count <= index)
            {
                ShaderMaterial material = NewMaterial(_blendShader);
                _segmentMaterials.Add(material);
                // Godot draws blended geometry back to front by the node's
                // sorting depth, and a larger sorting offset sorts nearer, so
                // draws later (the depth probe's all-ties scene checks the
                // direction). Segment 0 has the smallest offset: it draws first.
                var segmentBuffer = new InstanceBuffer(_quad, _root, material);
                segmentBuffer.SetSortingOffset(_segments.Count);
                _segments.Add(segmentBuffer);
            }

            InstanceBuffer buffer = _segments[index];
            buffer.Count = 0;
            return buffer;
        }

        private void SetViewUniforms(Vector2I size)
        {
            // Transform3D.translate_local(-(w / 2), -(h / 2), 0) then scale(2 / w, 2 / h),
            // in float, as the canvas renderer builds its screen transform.
            float sx = 2.0f / size.X;
            float sy = 2.0f / size.Y;
            var screen = new Vector4(sx, sy, -(size.X / 2.0f) * sx, -(size.Y / 2.0f) * sy);
            foreach (ShaderMaterial material in _materials)
            {
                Transform2D view = ReferenceEquals(material, _land.Material) ? _frame.LandView : _frame.View;
                material.SetShaderParameter("view_x", view.X);
                material.SetShaderParameter("view_y", view.Y);
                material.SetShaderParameter("view_origin", view.Origin);
                material.SetShaderParameter("screen", screen);
                material.SetShaderParameter("flip_y", FlipY);
                material.SetShaderParameter("key_reversed", ReverseZ ? 1f : 0f);
                material.SetShaderParameter("depth_origin", (int)_frame.DepthOrigin);
                material.SetShaderParameter("tie_min_diff", _frame.MinDiff);
                material.SetShaderParameter("cull_rect", _frame.Cull);
                material.SetShaderParameter("draw_offset", _frame.DrawOffset);
            }
        }

        // ---- persistent chunk sets ----------------------------------------------

        /// <summary>A chunk's instances on one page: a MultiMesh the chunk rewrites when it changes.</summary>
        internal sealed class ChunkSet
        {
            private readonly WorldDepthPass _pass;
            private readonly List<InstanceBuffer> _pages = new();
            private readonly List<Texture2D> _textures = new();

            internal bool Touched, Shown, Disposed;

            internal ChunkSet(WorldDepthPass pass)
            {
                _pass = pass;
            }

            internal WorldDepthPass Pass => _pass;

            internal InstanceBuffer Page(int index, Texture2D texture)
            {
                while (_pages.Count <= index)
                {
                    _pages.Add(null);
                    _textures.Add(null);
                }

                if (_pages[index] == null || !ReferenceEquals(_textures[index], texture))
                {
                    _pages[index]?.Dispose();
                    _pages[index] = new InstanceBuffer(_pass._quad, _pass._root, _pass.PageMaterial(texture)) { Persistent = true };
                    _textures[index] = texture;
                    _pages[index].SetVisible(Shown);
                }

                return _pages[index];
            }

            internal int PageCount => _pages.Count;

            /// <summary>Pages beyond <paramref name="count"/> are released.</summary>
            internal void Trim(int count)
            {
                for (int i = _pages.Count - 1; i >= count; i--)
                {
                    _pages[i]?.Dispose();
                    _pages.RemoveAt(i);
                    _textures.RemoveAt(i);
                }
            }

            internal void SetVisible(bool visible)
            {
                foreach (InstanceBuffer page in _pages)
                {
                    page?.SetVisible(visible);
                }
            }

            /// <summary>Uploads the pages the chunk changed since their last upload.</summary>
            internal void UploadDirty()
            {
                foreach (InstanceBuffer page in _pages)
                {
                    if (page != null && page.Dirty)
                    {
                        page.Upload();
                    }
                }
            }

            internal void Dispose()
            {
                Disposed = true;
                Trim(0);
            }
        }

        internal ChunkSet CreateChunkSet() => new ChunkSet(this);

        // ---- buffers ---------------------------------------------------------------

        /// <summary>One MultiMesh and its node; instances are appended in the order they will draw.</summary>
        internal sealed class InstanceBuffer : IDisposable
        {
            private readonly MultiMesh _multimesh;
            private readonly MultiMeshInstance3D _instance;
            private readonly ShaderMaterial _ownMaterial;
            private float[] _buffer = Array.Empty<float>();
            private int _uploadedCapacity = -1;
            private bool _visible = true;

            public int Count;
            public bool Persistent;

            /// <summary>Edited in place since the last upload (a persistent chunk's hue or drawn state).</summary>
            public bool Dirty;

            public InstanceBuffer(Mesh quad, Node3D root, ShaderMaterial material)
            {
                _multimesh = new MultiMesh
                {
                    TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                    UseColors = true,
                    UseCustomData = true,
                    Mesh = quad,
                    // Positions are not in world space at all; never cull.
                    CustomAabb = new Aabb(new Vector3(-1e7f, -1e7f, -1e7f), new Vector3(2e7f, 2e7f, 2e7f)),
                };

                _ownMaterial = material;
                _instance = new MultiMeshInstance3D
                {
                    Multimesh = _multimesh,
                    MaterialOverride = material,
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                };

                root.AddChild(_instance);
            }

            public void Bind(Texture2D page) => BindPage(_ownMaterial, page);

            /// <summary>Where this buffer sorts among blended geometry: larger is nearer, drawn later.</summary>
            public void SetSortingOffset(float offset)
            {
                _instance.SortingUseAabbCenter = false;
                _instance.SortingOffset = offset;
            }

            public Span<float> Reserve(int count)
            {
                int at = Count * Stride;
                int end = (Count + count) * Stride;
                if (end > _buffer.Length)
                {
                    Array.Resize(ref _buffer, Math.Max(Stride * 64, Math.Max(end, _buffer.Length * 2)));
                }

                Count += count;
                return _buffer.AsSpan(at, count * Stride);
            }

            public void Append(ReadOnlySpan<float> instance)
            {
                instance.CopyTo(Reserve(1));
            }

            /// <summary>The whole buffer as written; a persistent chunk edits in place and uploads again.</summary>
            public Span<float> Instances => _buffer.AsSpan(0, Count * Stride);

            public void Upload()
            {
                int capacity = _buffer.Length / Stride;
                if (capacity != _uploadedCapacity)
                {
                    _multimesh.InstanceCount = capacity;
                    _uploadedCapacity = capacity;
                }

                _multimesh.VisibleInstanceCount = Count;
                if (capacity > 0 && Count > 0)
                {
                    RenderingServer.MultimeshSetBuffer(_multimesh.GetRid(), _buffer);
                }

                Dirty = false;
                SetVisible(Count > 0 && (_visible || !Persistent));
            }

            public void Hide()
            {
                if (_uploadedCapacity > 0)
                {
                    _multimesh.VisibleInstanceCount = 0;
                }
            }

            public void SetVisible(bool visible)
            {
                _visible = visible;
                _instance.Visible = visible && Count > 0;
            }

            public void Dispose()
            {
                _instance.QueueFree();
            }
        }

        /// <summary>The covering-land mesh of a frame: CoverRuns.Build's vertices plus the key.</summary>
        private sealed class LandMesh
        {
            private readonly MeshInstance3D _instance;
            private readonly ArrayMesh _mesh = new();
            private Vector3[] _points = Array.Empty<Vector3>();
            private Vector2[] _uvs = Array.Empty<Vector2>();
            private Color[] _colors = Array.Empty<Color>();
            private float[] _custom0 = Array.Empty<float>();
            private float[] _custom1 = Array.Empty<float>();
            private int _vertices;
            private bool _uploaded;

            public readonly ShaderMaterial Material;
            public int Count;

            public LandMesh(Node3D root, Shader shader)
            {
                Material = new ShaderMaterial { Shader = shader };
                _instance = new MeshInstance3D
                {
                    Mesh = _mesh,
                    MaterialOverride = Material,
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                    CustomAabb = new Aabb(new Vector3(-1e7f, -1e7f, -1e7f), new Vector3(2e7f, 2e7f, 2e7f)),
                    Visible = false,
                };
                root.AddChild(_instance);
            }

            public void Begin()
            {
                Count = 0;
                _vertices = 0;
            }

            public void Add(MeshLayer layer, int index, int page, Vector2 scale, Vector2 offset,
                uint depthAbs, uint tieAbs, int s)
            {
                int v = _vertices + 6;
                if (v > _points.Length)
                {
                    int capacity = Math.Max(6 * 256, Math.Max(v, _points.Length * 2));
                    Array.Resize(ref _points, capacity);
                    Array.Resize(ref _uvs, capacity);
                    Array.Resize(ref _colors, capacity);
                    Array.Resize(ref _custom0, capacity * 4);
                    Array.Resize(ref _custom1, capacity * 4);
                }

                ref MeshQuad q = ref layer.Vertices[index];
                int w = _vertices;
                Put(w + 0, q.Position0 + offset, q.TextureCoordinate0 * scale, q.Hue0, q.Normal0, page, depthAbs, tieAbs, s);
                Put(w + 1, q.Position1 + offset, q.TextureCoordinate1 * scale, q.Hue1, q.Normal1, page, depthAbs, tieAbs, s);
                Put(w + 2, q.Position2 + offset, q.TextureCoordinate2 * scale, q.Hue2, q.Normal2, page, depthAbs, tieAbs, s);
                Put(w + 3, q.Position1 + offset, q.TextureCoordinate1 * scale, q.Hue1, q.Normal1, page, depthAbs, tieAbs, s);
                Put(w + 4, q.Position3 + offset, q.TextureCoordinate3 * scale, q.Hue3, q.Normal3, page, depthAbs, tieAbs, s);
                Put(w + 5, q.Position2 + offset, q.TextureCoordinate2 * scale, q.Hue2, q.Normal2, page, depthAbs, tieAbs, s);
                _vertices = v;
                Count++;
            }

            private void Put(int at, Vector2 position, Vector2 uv, Vector3 hue, Vector3 normal, int page,
                uint depthAbs, uint tieAbs, int s)
            {
                _points[at] = new Vector3(position.X, position.Y, 0f);
                _uvs[at] = uv;
                _colors[at] = UltimaBatcher2D.Encode(hue);
                _custom0[at * 4 + 0] = normal.X;
                _custom0[at * 4 + 1] = normal.Y;
                _custom0[at * 4 + 2] = normal.Z;
                _custom0[at * 4 + 3] = page;
                _custom1[at * 4 + 0] = depthAbs;
                _custom1[at * 4 + 1] = tieAbs;
                _custom1[at * 4 + 2] = s;
                _custom1[at * 4 + 3] = 0f;
            }

            public void Upload()
            {
                _mesh.ClearSurfaces();
                _uploaded = false;
                if (_vertices == 0)
                {
                    _instance.Visible = false;
                    return;
                }

                if (LandPages.Dirty)
                {
                    LandPages.Sync();
                }

                Material.SetShaderParameter("land_pages", LandPages.Array);

                // The arrays are sized exactly: the server takes whole arrays.
                var arrays = new Godot.Collections.Array();
                arrays.Resize((int)Mesh.ArrayType.Max);
                arrays[(int)Mesh.ArrayType.Vertex] = _points.AsSpan(0, _vertices).ToArray();
                arrays[(int)Mesh.ArrayType.TexUV] = _uvs.AsSpan(0, _vertices).ToArray();
                arrays[(int)Mesh.ArrayType.Color] = _colors.AsSpan(0, _vertices).ToArray();
                arrays[(int)Mesh.ArrayType.Custom0] = _custom0.AsSpan(0, _vertices * 4).ToArray();
                arrays[(int)Mesh.ArrayType.Custom1] = _custom1.AsSpan(0, _vertices * 4).ToArray();
                ulong format = ((ulong)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom0Shift)
                    | ((ulong)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom1Shift);
                _mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, null, null, (Mesh.ArrayFormat)format);
                _instance.Visible = true;
                _uploaded = true;
            }

            public void Hide()
            {
                _instance.Visible = false;
                if (_uploaded)
                {
                    _mesh.ClearSurfaces();
                    _uploaded = false;
                }
            }
        }

        public void Dispose()
        {
            foreach (InstanceBuffer page in _dynamicOrder)
            {
                page.Dispose();
            }

            foreach (InstanceBuffer segment in _segments)
            {
                segment.Dispose();
            }

            foreach (ChunkSet set in _shown)
            {
                set.Dispose();
            }

            _dynamicPages.Clear();
            _dynamicOrder.Clear();
            _segments.Clear();
            _shown.Clear();
            _pageMaterials.Clear();
            _materials.Clear();
            _viewport.QueueFree();
        }
    }
}
