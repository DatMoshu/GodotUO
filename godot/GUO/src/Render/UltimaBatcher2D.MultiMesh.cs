// SPDX-License-Identifier: BSD-2-Clause
//
// --multimesh-sprites (PERF-MM-01): runs of upright sprites as MultiMesh
// instances. See docs/perf/2026-10-24_multimesh_sprites.md.

using System;
using System.Collections.Generic;
using Godot;

using Color = Godot.Color;

namespace GUO.Renderer
{
    public sealed partial class UltimaBatcher2D
    {
        /// <summary>
        /// --multimesh-sprites (or GUO_MULTIMESH_SPRITES=1): consecutive
        /// upright sprites on one texture in one canvas item are written into
        /// one MultiMesh buffer and drawn by one CanvasItemAddMultimesh,
        /// instead of one CanvasItemAddTextureRectRegion each. Off by default.
        /// </summary>
        /// <remarks>
        /// PORT DEVIATION (GUO), for speed alone, and only when switched on.
        /// The per-sprite rect call was a third of the main thread at the
        /// bank (one managed-to-native crossing per sprite, ~16,000 a frame);
        /// a run costs three. Same corners, same source rect, same packed
        /// hue and the same item, so the picture is meant to be identical
        /// (the perf probe's --perf-parity checks it). A run ends where a
        /// --batched-world run would: a texture change, a new canvas item,
        /// any other command on the item, and End -- FlushQuadRun flushes it,
        /// and every other path reaches FlushQuadRun first. Everything else
        /// keeps its own path: rotated, mirrored or degenerate sprites, custom
        /// and additive blends, the id mirror (ADR-0023), --gpu-world's
        /// capture, and --batched-world when both are on.
        /// </remarks>
        public static bool MultiMeshSprites = System.Environment.GetEnvironmentVariable("GUO_MULTIMESH_SPRITES") == "1";

        /// <summary>This frame's MultiMesh counters; <see cref="LastMultiMesh"/> keeps the previous frame's for the perf probe.</summary>
        public static int MultiMeshInstances, MultiMeshRuns, MultiMeshCalls, MultiMeshFallbacks;

        /// <summary>
        /// The previous frame's: sprites drawn as instances, runs (one
        /// MultiMesh each), RenderingServer calls the runs took, and sprites
        /// that kept the per-sprite path while the switch was on.
        /// </summary>
        public static (int Sprites, int Runs, int Calls, int Fallbacks) LastMultiMesh;

        private const string MULTIMESH_SHADER_PATH = "res://src/Render/shaders/uo_hue_multimesh.gdshader";

        // Transform2D (8) + colour (4) + custom (4), the layout
        // MultimeshSetBuffer takes for a 2D MultiMesh with both on.
        private const int MmStride = 16;

        // MultimeshSetBuffer takes a whole buffer, never part of one, so a run
        // goes into the smallest pooled MultiMesh that holds it and sets its
        // visible count: capacities 16, 32, ... 4096, one pool each. A longer
        // run is split; the second half starts a new run on the same item.
        private const int MmMinShift = 4, MmMaxShift = 12;
        private const int MmMaxInstances = 1 << MmMaxShift;

        private sealed class MmPool
        {
            public readonly List<Rid> MultiMeshes = new();
            public readonly List<int> Visible = new();
            public int Used;
        }

        private readonly MmPool[] _mmPools = new MmPool[MmMaxShift - MmMinShift + 1];
        private readonly float[] _mmBuffer = new float[MmMaxInstances * MmStride];
        private ShaderMaterial _mmMaterial;
        private Rid _mmQuad;
        private Texture2D _mmTexture;
        private Rid _mmItem;
        private int _mmCount;

        /// <summary>The material sprites draw under at the default blend: the instancing variant while the switch is on.</summary>
        private ShaderMaterial SpriteMaterial
        {
            get
            {
                if (MultiMeshSprites && _mmMaterial == null)
                {
                    CreateMultiMeshMaterial();
                }

                return MultiMeshSprites && _mmMaterial != null ? _mmMaterial : _material;
            }
        }

        // Built on first use, from the plain material's uniforms as they stand
        // then; SetOnAllMaterials keeps it in step after that.
        private void CreateMultiMeshMaterial()
        {
            _mmMaterial = new ShaderMaterial { Shader = GD.Load<Shader>(MULTIMESH_SHADER_PATH) };
            foreach (Godot.Collections.Dictionary uniform in _material.Shader.GetShaderUniformList())
            {
                StringName name = uniform["name"].AsStringName();
                _mmMaterial.SetShaderParameter(name, _material.GetShaderParameter(name));
            }
        }

        /// <summary>
        /// Adds the sprite to the current run when it can go as an instance;
        /// false leaves it to the per-sprite path, counted as a fallback.
        /// </summary>
        private bool TryAppendMultiMesh(Texture2D texture, float sourceX, float sourceY, float sourceW, float sourceH,
            float destinationX, float destinationY, float destinationW, float destinationH, Vector3 color,
            float originX, float originY, float rotationSin, float rotationCos, byte effects)
        {
            if (rotationSin != 0f || rotationCos != 1f || effects != 0 || BatchedWorld || _mirroring
                || _mmMaterial == null || !ReferenceEquals(_itemMaterial, _mmMaterial)
                || destinationW <= 0f || destinationH <= 0f || sourceW <= 0f || sourceH <= 0f)
            {
                MultiMeshFallbacks++;
                return false;
            }

            FlushCover();
            if (!ReferenceEquals(texture, _mmTexture) || _mmItem != _current || _mmCount == MmMaxInstances)
            {
                FlushMultiMeshRun();
                _mmTexture = texture;
                _mmItem = _current;
            }

            Span<float> i = _mmBuffer.AsSpan(_mmCount * MmStride, MmStride);

            // Transform2D rows: (x.x, y.x, pad, origin.x), (x.y, y.y, pad, origin.y).
            i[0] = destinationW;
            i[1] = 0f;
            i[2] = 0f;
            i[3] = destinationX - originX * destinationW;
            i[4] = 0f;
            i[5] = destinationH;
            i[6] = 0f;
            i[7] = destinationY - originY * destinationH;

            Color modulate = Encode(color);
            i[8] = modulate.R;
            i[9] = modulate.G;
            i[10] = modulate.B;
            i[11] = modulate.A;

            // The rect path hands the server a texel rect (source times size)
            // and the server scales it back by 1 / size; the same two float
            // steps here, so a texel boundary rounds the same way.
            int textureWidth = WidthOf(texture), textureHeight = HeightOf(texture);
            float pixelW = 1f / textureWidth, pixelH = 1f / textureHeight;
            i[12] = sourceX * textureWidth * pixelW;
            i[13] = sourceY * textureHeight * pixelH;
            i[14] = sourceW * textureWidth * pixelW;
            i[15] = sourceH * textureHeight * pixelH;

            _mmCount++;
            MultiMeshInstances++;
            return true;
        }

        /// <summary>Sends the pending run, if there is one, as one MultiMesh on its item.</summary>
        private void FlushMultiMeshRun()
        {
            if (_mmCount == 0)
            {
                _mmTexture = null;
                return;
            }

            int shift = MmMinShift;
            while ((1 << shift) < _mmCount)
            {
                shift++;
            }

            MmPool pool = _mmPools[shift - MmMinShift] ??= new MmPool();
            if (pool.Used == pool.MultiMeshes.Count)
            {
                pool.MultiMeshes.Add(CreateMultiMesh(1 << shift));
                pool.Visible.Add(-1);
            }

            int slot = pool.Used++;
            Rid multimesh = pool.MultiMeshes[slot];
            RenderingServer.MultimeshSetBuffer(multimesh, new ReadOnlySpan<float>(_mmBuffer, 0, (1 << shift) * MmStride));
            MultiMeshCalls++;
            if (pool.Visible[slot] != _mmCount)
            {
                RenderingServer.MultimeshSetVisibleInstances(multimesh, _mmCount);
                pool.Visible[slot] = _mmCount;
                MultiMeshCalls++;
            }

            RenderingServer.CanvasItemAddMultimesh(_mmItem, multimesh, RidOf(_mmTexture));
            MultiMeshCalls++;
            MultiMeshRuns++;
            Commands++;

            _mmCount = 0;
            _mmTexture = null;
        }

        private Rid CreateMultiMesh(int capacity)
        {
            if (!_mmQuad.IsValid)
            {
                // The unit quad, corners TL, TR, BL, BR; UV equals position, so
                // the instance's rect maps it. Split on the diagonal the
                // server's rect command uses, TL-BR, with its vertex order
                // ((TL, BL, BR), (TR, TL, BR)): interpolated UVs then land on
                // texel ties the same way when the view minifies.
                var corners = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) };
                var arrays = new Godot.Collections.Array();
                arrays.Resize((int)Mesh.ArrayType.Max);
                arrays[(int)Mesh.ArrayType.Vertex] = corners;
                arrays[(int)Mesh.ArrayType.TexUV] = corners;
                arrays[(int)Mesh.ArrayType.Color] = new[] { Colors.White, Colors.White, Colors.White, Colors.White };
                arrays[(int)Mesh.ArrayType.Index] = new[] { 0, 2, 3, 1, 0, 3 };
                _mmQuad = RenderingServer.MeshCreate();
                RenderingServer.MeshAddSurfaceFromArrays(_mmQuad, RenderingServer.PrimitiveType.Triangles, arrays);
            }

            Rid multimesh = RenderingServer.MultimeshCreate();
            RenderingServer.MultimeshAllocateData(multimesh, capacity, RenderingServer.MultimeshTransformFormat.Transform2D, true, true);
            RenderingServer.MultimeshSetMesh(multimesh, _mmQuad);

            // A fixed bound, so the server does not rebuild one from every
            // buffer: canvas items are not culled by it in practice (the
            // world items span the screen) and the frame is clipped anyway.
            RenderingServer.MultimeshSetCustomAabb(multimesh, new Aabb(new Vector3(-1e6f, -1e6f, -1f), new Vector3(2e6f, 2e6f, 2f)));
            return multimesh;
        }

        /// <summary>BeginFrame: keep the counters, reuse the pool from the start.</summary>
        private void NextMultiMeshFrame()
        {
            LastMultiMesh = (MultiMeshInstances, MultiMeshRuns, MultiMeshCalls, MultiMeshFallbacks);
            MultiMeshInstances = MultiMeshRuns = MultiMeshCalls = MultiMeshFallbacks = 0;
            foreach (MmPool pool in _mmPools)
            {
                if (pool != null)
                {
                    pool.Used = 0;
                }
            }
        }

        private void DisposeMultiMesh()
        {
            foreach (MmPool pool in _mmPools)
            {
                if (pool == null)
                {
                    continue;
                }

                foreach (Rid multimesh in pool.MultiMeshes)
                {
                    RenderingServer.FreeRid(multimesh);
                }

                pool.MultiMeshes.Clear();
                pool.Visible.Clear();
            }

            if (_mmQuad.IsValid)
            {
                RenderingServer.FreeRid(_mmQuad);
                _mmQuad = default;
            }

            _mmMaterial?.Dispose();
        }
    }
}
