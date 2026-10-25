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
        /// keeps its own path: degenerate sprites, quads no affine map fits,
        /// custom and additive blends, the id mirror (ADR-0023), --gpu-world's
        /// capture, and --batched-world when both are on.
        /// </remarks>
        public static bool MultiMeshSprites = System.Environment.GetEnvironmentVariable("GUO_MULTIMESH_SPRITES") == "1";

        /// <summary>This frame's MultiMesh counters; <see cref="LastMultiMesh"/> keeps the previous frame's for the perf probe.</summary>
        public static int MultiMeshInstances, MultiMeshRuns, MultiMeshCalls, MultiMeshFallbacks, MultiMeshReplayed;

        /// <summary>
        /// Runs shorter than this are sent as the commands they replaced: a
        /// MultiMesh costs a buffer copy, an upload and a draw call of its own,
        /// where consecutive rects share the canvas renderer's batch, so a run
        /// of a few sprites is cheaper as rects. GUO_MULTIMESH_MIN_RUN sets it.
        /// </summary>
        public static int MultiMeshMinRun = int.TryParse(System.Environment.GetEnvironmentVariable("GUO_MULTIMESH_MIN_RUN"), out int minRun) ? minRun : 8;

        /// <summary>
        /// The previous frame's: sprites drawn as instances, runs (one
        /// MultiMesh each), RenderingServer calls the runs took, sprites
        /// that kept the per-sprite path while the switch was on, and sprites
        /// of runs too short to instance, sent as their own commands.
        /// </summary>
        public static (int Sprites, int Runs, int Calls, int Fallbacks, int Replayed) LastMultiMesh;

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

        // Each pending instance's own command, as the per-sprite path would
        // have sent it, for a run too short to instance.
        private struct MmCommand
        {
            public Rect2 Destination, Source;
            public Transform2D Transform;
            public bool Affine;
        }

        private readonly MmCommand[] _mmCommands = new MmCommand[MmMaxInstances];
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
            // Rotated or mirrored: AddQuad decides, through TryAppendMultiMeshAffine.
            if (rotationSin != 0f || rotationCos != 1f || effects != 0)
            {
                return false;
            }

            if (BatchedWorld || _mirroring
                || _mmMaterial == null || !ReferenceEquals(_itemMaterial, _mmMaterial)
                || destinationW <= 0f || destinationH <= 0f || sourceW <= 0f || sourceH <= 0f)
            {
                MultiMeshFallbacks++;
                return false;
            }

            // The rect path hands the server a texel rect (source times size)
            // and the server scales it back by 1 / size; the same two float
            // steps here, so a texel boundary rounds the same way.
            int textureWidth = WidthOf(texture), textureHeight = HeightOf(texture);
            float pixelW = 1f / textureWidth, pixelH = 1f / textureHeight;
            var destination = new Rect2(destinationX - originX * destinationW, destinationY - originY * destinationH, destinationW, destinationH);
            var source = new Rect2(sourceX * textureWidth, sourceY * textureHeight, sourceW * textureWidth, sourceH * textureHeight);
            AppendInstance(texture, new Vector2(destinationW, 0f), new Vector2(0f, destinationH), destination.Position, Encode(color),
                source.Position.X * pixelW, source.Position.Y * pixelH, source.Size.X * pixelW, source.Size.Y * pixelH);
            _mmCommands[_mmCount - 1] = new MmCommand { Destination = destination, Source = source };
            return true;
        }

        /// <summary>
        /// TryAddAffineQuad's transformed rect (a shadow's parallelogram, a
        /// mirror, a rotation, a sitted section) as an instance: the texel
        /// region under the quad's affine map, folded into the instance
        /// transform. False leaves it to the transformed rect command.
        /// </summary>
        private bool TryAppendMultiMeshAffine(Texture2D texture, Vector2 axisX, Vector2 axisY, Vector2 origin, Rect2 region, Color modulate)
        {
            if (BatchedWorld || _mirroring || _mmMaterial == null || !ReferenceEquals(_itemMaterial, _mmMaterial)
                || region.Size.X <= 0f || region.Size.Y <= 0f)
            {
                MultiMeshFallbacks++;
                return false;
            }

            float pixelW = 1f / WidthOf(texture), pixelH = 1f / HeightOf(texture);
            AppendInstance(texture, axisX * region.Size.X, axisY * region.Size.Y,
                origin + axisX * region.Position.X + axisY * region.Position.Y, modulate,
                region.Position.X * pixelW, region.Position.Y * pixelH, region.Size.X * pixelW, region.Size.Y * pixelH);
            _mmCommands[_mmCount - 1] = new MmCommand { Destination = region, Source = region, Transform = new Transform2D(axisX, axisY, origin), Affine = true };
            return true;
        }

        /// <summary>Appends one instance to the run, starting a new run when it must.</summary>
        private void AppendInstance(Texture2D texture, Vector2 basisX, Vector2 basisY, Vector2 origin, Color modulate,
            float u, float v, float uw, float vh)
        {
            FlushCover();
            if (!ReferenceEquals(texture, _mmTexture) || _mmItem != _current || _mmCount == MmMaxInstances)
            {
                FlushMultiMeshRun();
                _mmTexture = texture;
                _mmItem = _current;
            }

            Span<float> i = _mmBuffer.AsSpan(_mmCount * MmStride, MmStride);

            // Transform2D rows: (x.x, y.x, pad, origin.x), (x.y, y.y, pad, origin.y).
            i[0] = basisX.X;
            i[1] = basisY.X;
            i[2] = 0f;
            i[3] = origin.X;
            i[4] = basisX.Y;
            i[5] = basisY.Y;
            i[6] = 0f;
            i[7] = origin.Y;

            i[8] = modulate.R;
            i[9] = modulate.G;
            i[10] = modulate.B;
            i[11] = modulate.A;

            i[12] = u;
            i[13] = v;
            i[14] = uw;
            i[15] = vh;

            _mmCount++;
        }

        /// <summary>Sends the pending run, if there is one, as one MultiMesh on its item.</summary>
        private void FlushMultiMeshRun()
        {
            if (_mmCount == 0)
            {
                _mmTexture = null;
                return;
            }

            if (_mmCount < MultiMeshMinRun)
            {
                ReplayMultiMeshRun();
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

            // PORT DEVIATION (GUO): the retained stream has no MultiMesh
            // command. Materialize its prefix before this native command;
            // its pooled instance buffer changes independently of the RID.
            _canvasCommands?.Bypass();
            RenderingServer.CanvasItemAddMultimesh(_mmItem, multimesh, RidOf(_mmTexture));
            MultiMeshCalls++;
            MultiMeshRuns++;
            MultiMeshInstances += _mmCount;
            Commands++;

            _mmCount = 0;
            _mmTexture = null;
        }

        /// <summary>
        /// Sends a short run as the commands it stood in for, exactly as
        /// AddSprite's rect path and TryAddAffineQuad send them.
        /// </summary>
        private void ReplayMultiMeshRun()
        {
            Rid texture = RidOf(_mmTexture);
            for (int n = 0; n < _mmCount; n++)
            {
                ref MmCommand command = ref _mmCommands[n];
                int at = n * MmStride;
                var modulate = new Color(_mmBuffer[at + 8], _mmBuffer[at + 9], _mmBuffer[at + 10], _mmBuffer[at + 11]);
                Commands++;
                if (command.Affine)
                {
                    Count(1, texture);
                    CanvasAffine(command.Source, texture, modulate, command.Transform);
                }
                else
                {
                    Count(0, texture);
                    CanvasRect(_mmItem, command.Destination, texture, command.Source, modulate);
                }
            }

            MultiMeshReplayed += _mmCount;
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
            LastMultiMesh = (MultiMeshInstances, MultiMeshRuns, MultiMeshCalls, MultiMeshFallbacks, MultiMeshReplayed);
            MultiMeshInstances = MultiMeshRuns = MultiMeshCalls = MultiMeshFallbacks = MultiMeshReplayed = 0;
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
