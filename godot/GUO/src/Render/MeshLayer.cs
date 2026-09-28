// SPDX-License-Identifier: BSD-2-Clause
//
// Ported from ClassicUO's ClassicUO.Renderer/MeshLayer.cs (BSD-2-Clause).
// See docs/architecture/ADR-0004-world-mesh-on-canvas-meshes.md; the reasoning
// for every deviation below lives there rather than being restated here.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Godot;
using GUO.Compat;

namespace GUO.Renderer
{
    public struct TextureRun
    {
        public Texture2D Texture;
        public int Start;
        public int Count;
    }

    /// <summary>
    /// One quad of a chunk mesh: four corners, each with a position, a texture
    /// coordinate, the packed hue triple and the normal.
    /// </summary>
    /// <remarks>
    /// PORT DEVIATION (GUO): upstream's vertex is
    /// <c>PositionNormalTextureColor4</c>, an FNA vertex-declaration struct
    /// whose layout has to match the shader's input signature. Nothing here
    /// matches a declaration, so the fields are the ones that are read: the
    /// position drops its Z, because Godot's 2D canvas has no depth buffer and
    /// ADR-0001 sorts before the batcher sees anything; and the texture
    /// coordinate drops its unused third component. The normal travels whole,
    /// in CUSTOM0, because upstream lights land per pixel from the
    /// interpolated normal -- see ADR-0004.
    /// </remarks>
    public struct MeshQuad
    {
        public Vector2 Position0;
        public Vector2 Position1;
        public Vector2 Position2;
        public Vector2 Position3;

        public Vector2 TextureCoordinate0;
        public Vector2 TextureCoordinate1;
        public Vector2 TextureCoordinate2;
        public Vector2 TextureCoordinate3;

        public Vector3 Hue0;
        public Vector3 Hue1;
        public Vector3 Hue2;
        public Vector3 Hue3;

        public Vector3 Normal0;
        public Vector3 Normal1;
        public Vector3 Normal2;
        public Vector3 Normal3;
    }

    /// <summary>
    /// A buffered layer of sprites (vertices + textures + visibility) with
    /// per-frame visibility filtering. Sprites must be added in texture-sorted
    /// order (via bucket-insert in the caller) so that draw call batching is
    /// optimal.
    /// </summary>
    /// <remarks>
    /// PORT DEVIATION (GUO): upstream keeps the vertices in a
    /// <c>DynamicVertexBuffer</c> and rebuilds only a
    /// <c>DynamicIndexBuffer</c> per frame. A Godot canvas has no vertex buffer
    /// a caller owns, so the geometry lives here on the CPU and each texture
    /// run becomes an <see cref="ArrayMesh"/>, rebuilt under the same condition
    /// upstream re-uploads under. <c>UploadVertexBuffer</c>,
    /// <c>UploadVisibleIndices</c> and <c>FlushAlphaChanges</c> are gone with
    /// the buffers; the mesh rebuild is when all three used to happen.
    /// </remarks>
    public sealed class MeshLayer
    {
        private const int INITIAL_CAPACITY = 64;

        // Upstream's normalize(LIGHT_DIRECTION), LIGHT_DIRECTION = (0, 1, 1).
        private static readonly Vector3 _lightDirection =
            new Vector3(0f, 1f, 1f).Normalized();

        // Sprite data (parallel arrays, must be in texture-sorted order)
        public MeshQuad[] Vertices = new MeshQuad[INITIAL_CAPACITY];
        public Texture2D[] Textures = new Texture2D[INITIAL_CAPACITY];
        public int Count;

        // Per-frame visibility filtering
        public bool[] Visible = new bool[INITIAL_CAPACITY];
        public int VisibleSpriteCount;
        public TextureRun[] VisibleRuns = new TextureRun[16];

        // PORT DEVIATION (GUO): not upstream's. Counts the times the visible
        // runs were rebuilt, so MergedLand (--merged-land, Epic B) knows when
        // its merged meshes are stale without comparing the sprites.
        public int BuildStamp;
        public int VisibleRunCount;
        private bool _visibilityDirty = true;
        private bool _alphaDirty;
        private bool[] _prevVisible = new bool[INITIAL_CAPACITY];

        // Track which sprite indices had alpha modified (avoids full scan in ResetAlpha)
        private int[] _alphaDirtyIndices = new int[16];
        private int _alphaDirtyCount;

        // One mesh per visible texture run, in run order. Reused across frames:
        // an ArrayMesh cannot be edited in place, so a rebuild clears its
        // surfaces and adds a new one, which is cheaper than a new resource.
        private ArrayMesh[] _runMeshes = new ArrayMesh[16];

        // Scratch for a rebuild, grown to the largest run seen. Six vertices
        // per sprite: the quad's two triangles, written out, because the canvas
        // mesh path takes no index array.
        private Vector2[] _points = Array.Empty<Vector2>();
        private Vector2[] _uvs = Array.Empty<Vector2>();
        private Godot.Color[] _colors = Array.Empty<Godot.Color>();
        private float[] _custom = Array.Empty<float>();

        /// <summary>
        /// Upstream's <c>get_light</c>, without the <c>Brightlight</c> blend.
        /// </summary>
        /// <remarks>
        /// The shader evaluates this per pixel, as upstream does; this CPU
        /// copy is the reference the batcher probe checks the screen against.
        /// </remarks>
        public static float LightFromNormal(Vector3 normal)
        {
            float dot = normal.Normalized().Dot(_lightDirection);

            return (Math.Max(dot, 0f) / 2f) + 0.5f;
        }

        /// <summary>
        /// Ensures internal arrays can hold at least <paramref name="minCapacity"/> sprites.
        /// </summary>
        public void EnsureCapacity(int minCapacity)
        {
            if (minCapacity <= Vertices.Length)
                return;

            int newSize = Vertices.Length;
            while (newSize < minCapacity)
                newSize *= 2;

            Array.Resize(ref Vertices, newSize);
            Array.Resize(ref Textures, newSize);
            Array.Resize(ref Visible, newSize);
        }

        /// <summary>
        /// Resets all visibility flags to false. Called each frame before
        /// AddTileToRenderList marks visible sprites.
        /// </summary>
        public void ResetVisibility()
        {
            if (Count > 0)
                Array.Clear(Visible, 0, Count);
        }

        /// <summary>
        /// Marks a sprite as visible this frame and updates its vertex alpha.
        /// AlphaHue=0 means "not yet processed" → treat as fully opaque.
        /// AlphaHue=255 means fully opaque. Any other value is a fade in progress.
        /// </summary>
        public void SetVisible(int index, byte alphaHue, bool circletrans = false)
        {
            Visible[index] = true;

            float alpha;
            if (circletrans)
            {
                // Alpha > 1.0 signals the shader to apply circle of transparency
                alpha = (alphaHue == 0 ? 1f : alphaHue / 255f) + 1f;
            }
            else if (alphaHue != 0 && alphaHue != 0xFF)
            {
                alpha = alphaHue / 255f;
            }
            else
            {
                return;
            }

            ref var v = ref Vertices[index];
            v.Hue0.Z = alpha;
            v.Hue1.Z = alpha;
            v.Hue2.Z = alpha;
            v.Hue3.Z = alpha;

            if (_alphaDirtyCount >= _alphaDirtyIndices.Length)
                Array.Resize(ref _alphaDirtyIndices, _alphaDirtyIndices.Length * 2);
            _alphaDirtyIndices[_alphaDirtyCount++] = index;

            MarkVertexDirty();
        }

        /// <summary>
        /// Resets vertex alphas back to fully opaque for sprites that were modified.
        /// Only touches indices tracked by SetVisible, avoiding a full scan.
        /// </summary>
        public void ResetAlpha()
        {
            if (_alphaDirtyCount == 0)
                return;

            for (int i = 0; i < _alphaDirtyCount; i++)
            {
                ref var v = ref Vertices[_alphaDirtyIndices[i]];
                v.Hue0.Z = 1f;
                v.Hue1.Z = 1f;
                v.Hue2.Z = 1f;
                v.Hue3.Z = 1f;
            }

            _alphaDirty = true;
            _alphaDirtyCount = 0;
        }

        /// <summary>
        /// Sets the hue (X=hue index, Y=shader type) on a meshed sprite, preserving alpha (Z).
        /// Used per-frame to apply out-of-range color or sync with runtime hue changes.
        /// </summary>
        public void SetHue(int index, float hueX, float hueY)
        {
            ref var v = ref Vertices[index];
            if (v.Hue0.X == hueX && v.Hue0.Y == hueY)
                return;

            float alpha = v.Hue0.Z;
            var h = new Vector3(hueX, hueY, alpha);
            v.Hue0 = v.Hue1 = v.Hue2 = v.Hue3 = h;

            MarkVertexDirty();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void MarkVertexDirty()
        {
            _alphaDirty = true;
        }

        /// <summary>
        /// Writes a quad at a specific index from a texture atlas source rectangle.
        /// Does not modify Count — caller is responsible for setting Count after all writes.
        /// </summary>
        public void WriteQuadAt(int index, Texture2D texture, Rectangle sourceRect, int posX, int posY,
            Vector3 hue, float depth, float uvInset = 0f)
        {
            ref var vertex = ref Vertices[index];

            float sourceX = (sourceRect.X + uvInset) / (float)texture.GetWidth();
            float sourceY = (sourceRect.Y + uvInset) / (float)texture.GetHeight();
            float sourceW = (sourceRect.Width - uvInset * 2f) / (float)texture.GetWidth();
            float sourceH = (sourceRect.Height - uvInset * 2f) / (float)texture.GetHeight();

            vertex.Position0.X = posX;
            vertex.Position0.Y = posY;
            vertex.Position1.X = posX + sourceRect.Width;
            vertex.Position1.Y = posY;
            vertex.Position2.X = posX;
            vertex.Position2.Y = posY + sourceRect.Height;
            vertex.Position3.X = posX + sourceRect.Width;
            vertex.Position3.Y = posY + sourceRect.Height;

            vertex.TextureCoordinate0 = new Vector2(sourceX, sourceY);
            vertex.TextureCoordinate1 = new Vector2(sourceW + sourceX, sourceY);
            vertex.TextureCoordinate2 = new Vector2(sourceX, sourceH + sourceY);
            vertex.TextureCoordinate3 = new Vector2(sourceW + sourceX, sourceH + sourceY);

            // Upstream's flat normal. get_light gives 0.85355339 for it, the
            // value the brightlight blend pulls towards.
            vertex.Normal0 = vertex.Normal1 = vertex.Normal2 = vertex.Normal3 = new Vector3(0f, 0f, 1f);

            vertex.Hue0 = vertex.Hue1 = vertex.Hue2 = vertex.Hue3 = hue;

            Textures[index] = texture;

            MarkVertexDirty();
        }

        /// <summary>
        /// Builds the per-frame visible geometry from the Visible[] flags.
        /// Sprites are already in texture-sorted order, so the visible subset
        /// preserves texture grouping for optimal draw call batching. Skips the
        /// rebuild if neither visibility nor the vertices changed.
        /// </summary>
        public bool BuildVisibleIndices()
        {
            if (Count == 0)
            {
                VisibleSpriteCount = 0;
                VisibleRunCount = 0;
                return false;
            }

            // Check if visibility changed since last frame
            if (!_visibilityDirty && !_alphaDirty)
            {
                if (Visible.AsSpan(0, Count).SequenceEqual(_prevVisible.AsSpan(0, Count)))
                    return VisibleSpriteCount > 0;
            }

            // Save current visibility for next frame comparison
            if (_prevVisible.Length < Count)
                _prevVisible = new bool[Count];
            Visible.AsSpan(0, Count).CopyTo(_prevVisible.AsSpan(0, Count));
            _visibilityDirty = false;

            // PORT DEVIATION (GUO): upstream's second condition. It re-uploaded
            // the vertex buffer separately in FlushAlphaChanges, so a pure
            // alpha or hue change did not have to rebuild indices. Here the
            // vertices and the selection are the same mesh, so either dirties
            // it — which is why _alphaDirty is tested above and cleared here.
            _alphaDirty = false;

            VisibleSpriteCount = 0;
            VisibleRunCount = 0;

            Texture2D curTexture = null;
            int runStart = 0;

            for (int i = 0; i < Count; i++)
            {
                if (!Visible[i])
                    continue;

                var tex = Textures[i];
                if (tex != curTexture)
                {
                    CloseRun(curTexture, runStart);

                    curTexture = tex;
                    runStart = VisibleSpriteCount;
                }

                VisibleSpriteCount++;
            }

            CloseRun(curTexture, runStart);

            BuildRunMeshes();

            return VisibleSpriteCount > 0;
        }

        private void CloseRun(Texture2D texture, int runStart)
        {
            if (texture == null || VisibleSpriteCount <= runStart)
            {
                return;
            }

            if (VisibleRunCount >= VisibleRuns.Length)
            {
                Array.Resize(ref VisibleRuns, VisibleRuns.Length * 2);
                Array.Resize(ref _runMeshes, _runMeshes.Length * 2);
            }

            VisibleRuns[VisibleRunCount++] = new TextureRun
            {
                Texture = texture,
                Start = runStart,
                Count = VisibleSpriteCount - runStart
            };
        }

        /// <summary>
        /// Turns the runs into one <see cref="ArrayMesh"/> each.
        /// </summary>
        /// <remarks>
        /// One mesh per run rather than one mesh with a surface per run,
        /// because <c>canvas_item_add_mesh</c> binds a single texture per call.
        /// Upstream's short index buffer capped a layer at 8191 sprites; there
        /// is no index buffer here and no cap.
        /// </remarks>
        private void BuildRunMeshes()
        {
            BuildStamp++;
            // Walk the visible sprites in the same order the runs were closed
            // in: runs are contiguous stretches of the visible subset, so run
            // N's sprites are simply the next r.Count of them.
            int cursor = 0;

            for (int run = 0; run < VisibleRunCount; run++)
            {
                ref TextureRun r = ref VisibleRuns[run];

                int vertexCount = r.Count * 6;

                if (_points.Length < vertexCount)
                {
                    Array.Resize(ref _points, vertexCount);
                    Array.Resize(ref _uvs, vertexCount);
                    Array.Resize(ref _colors, vertexCount);
                    Array.Resize(ref _custom, vertexCount * 4);
                }

                int w = 0;

                while (w < vertexCount && cursor < Count)
                {
                    if (!Visible[cursor])
                    {
                        cursor++;
                        continue;
                    }

                    WriteTriangles(cursor, w);

                    w += 6;
                    cursor++;
                }

                _runMeshes[run] ??= new ArrayMesh();

                ArrayMesh mesh = _runMeshes[run];
                mesh.ClearSurfaces();

                var arrays = new Godot.Collections.Array();
                arrays.Resize((int)Mesh.ArrayType.Max);
                arrays[(int)Mesh.ArrayType.Vertex] = _points.AsSpan(0, vertexCount).ToArray();
                arrays[(int)Mesh.ArrayType.TexUV] = _uvs.AsSpan(0, vertexCount).ToArray();
                arrays[(int)Mesh.ArrayType.Color] = _colors.AsSpan(0, vertexCount).ToArray();
                arrays[(int)Mesh.ArrayType.Custom0] = _custom.AsSpan(0, vertexCount * 4).ToArray();

                mesh.AddSurfaceFromArrays(
                    Mesh.PrimitiveType.Triangles,
                    arrays,
                    null,
                    null,
                    (Mesh.ArrayFormat)((ulong)Mesh.ArrayCustomFormat.RgbaFloat
                        << (int)Mesh.ArrayFormat.FormatCustom0Shift));
            }
        }

        /// <summary>
        /// Puts one sprite of this layer, alone, into <paramref name="mesh"/>.
        /// </summary>
        /// <remarks>
        /// For land that has to be drawn again inside the sorted pass (see
        /// RenderLists.CoverFromBelow). The sprite batcher cannot draw
        /// stretched land -- the per-vertex light only reaches the shader
        /// through a mesh, ADR-0004 -- so the tile is drawn from the very quad
        /// the bake holds, and comes out pixel for pixel as the bake drew it.
        /// </remarks>
        public void FillSpriteMesh(int index, ArrayMesh mesh)
        {
            if (_points.Length < 6)
            {
                Array.Resize(ref _points, 6);
                Array.Resize(ref _uvs, 6);
                Array.Resize(ref _colors, 6);
                Array.Resize(ref _custom, 24);
            }

            WriteTriangles(index, 0);

            mesh.ClearSurfaces();

            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = _points.AsSpan(0, 6).ToArray();
            arrays[(int)Mesh.ArrayType.TexUV] = _uvs.AsSpan(0, 6).ToArray();
            arrays[(int)Mesh.ArrayType.Color] = _colors.AsSpan(0, 6).ToArray();
            arrays[(int)Mesh.ArrayType.Custom0] = _custom.AsSpan(0, 24).ToArray();

            mesh.AddSurfaceFromArrays(
                Mesh.PrimitiveType.Triangles,
                arrays,
                null,
                null,
                (Mesh.ArrayFormat)((ulong)Mesh.ArrayCustomFormat.RgbaFloat
                    << (int)Mesh.ArrayFormat.FormatCustom0Shift));
        }

        // One mesh per sprite drawn alone, and the quad it was built from.
        private ArrayMesh[] _spriteMeshes = Array.Empty<ArrayMesh>();
        private MeshQuad[] _spriteMeshQuads = Array.Empty<MeshQuad>();

        /// <summary>
        /// The mesh <see cref="FillSpriteMesh"/> makes for one sprite, kept
        /// from frame to frame.
        /// </summary>
        /// <remarks>
        /// Checked against the quad it was built from rather than marked dirty
        /// by the writers, because the quads are written from outside this
        /// class too (ChunkMesh's stretched land, the probes). A quad that did
        /// not change costs a compare; one that did is rebuilt as before.
        /// </remarks>
        public ArrayMesh GetSpriteMesh(int index)
        {
            if (_spriteMeshes.Length <= index)
            {
                int size = Math.Max(Vertices.Length, index + 1);
                Array.Resize(ref _spriteMeshes, size);
                Array.Resize(ref _spriteMeshQuads, size);
            }

            ArrayMesh mesh = _spriteMeshes[index];

            if (mesh != null && SameQuad(ref _spriteMeshQuads[index], ref Vertices[index]))
            {
                return mesh;
            }

            mesh ??= _spriteMeshes[index] = new ArrayMesh();
            FillSpriteMesh(index, mesh);
            _spriteMeshQuads[index] = Vertices[index];

            return mesh;
        }

        private static bool SameQuad(ref MeshQuad a, ref MeshQuad b)
        {
            return MemoryMarshal.AsBytes(new ReadOnlySpan<MeshQuad>(ref a))
                .SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<MeshQuad>(ref b)));
        }

        private void WriteTriangles(int index, int at)
        {
            ref MeshQuad q = ref Vertices[index];

            // Upstream's winding, from GenerateIndexArray: 0 1 2, 1 3 2.
            Write(at + 0, q.Position0, q.TextureCoordinate0, q.Hue0, q.Normal0);
            Write(at + 1, q.Position1, q.TextureCoordinate1, q.Hue1, q.Normal1);
            Write(at + 2, q.Position2, q.TextureCoordinate2, q.Hue2, q.Normal2);
            Write(at + 3, q.Position1, q.TextureCoordinate1, q.Hue1, q.Normal1);
            Write(at + 4, q.Position3, q.TextureCoordinate3, q.Hue3, q.Normal3);
            Write(at + 5, q.Position2, q.TextureCoordinate2, q.Hue2, q.Normal2);
        }

        private void Write(int at, Vector2 position, Vector2 uv, Vector3 hue, Vector3 normal)
        {
            _points[at] = position;
            _uvs[at] = uv;
            _colors[at] = UltimaBatcher2D.Encode(hue);

            // RGBA_FLOAT: four floats per vertex, the normal in XYZ.
            _custom[at * 4 + 0] = normal.X;
            _custom[at * 4 + 1] = normal.Y;
            _custom[at * 4 + 2] = normal.Z;
            _custom[at * 4 + 3] = 0f;
        }

        public void Reset()
        {
            Count = 0;
            VisibleSpriteCount = 0;
            VisibleRunCount = 0;
            _visibilityDirty = true;
            _alphaDirty = false;
            _alphaDirtyCount = 0;
        }

        /// <summary>
        /// Resets all state for reuse from a pool. Keeps the meshes and arrays allocated.
        /// </summary>
        public void SoftReset()
        {
            Count = 0;
            VisibleSpriteCount = 0;
            VisibleRunCount = 0;
            _visibilityDirty = true;
            _alphaDirty = false;
            _alphaDirtyCount = 0;

            // Clear texture references so they can be GC'd if atlas rebuilds
            if (Textures.Length > 0)
                Array.Clear(Textures, 0, Textures.Length);
        }

        /// <summary>
        /// Returns the mesh for a visible run, for the batcher to draw.
        /// </summary>
        public ArrayMesh GetRunMesh(int run) => _runMeshes[run];

        /// <remarks>
        /// PORT DEVIATION (GUO): upstream frees its vertex buffer here. The
        /// ArrayMesh is refcounted on the engine side, but its C# wrapper holds
        /// that reference until it is disposed or finalised, so a dropped mesh
        /// kept its vertex buffers until a garbage collection got round to it.
        /// Disposing the wrapper releases them now. Nothing draws a chunk
        /// between its unload and the next Begin, which rebuilds every item.
        /// </remarks>
        public void Dispose()
        {
            for (int i = 0; i < _runMeshes.Length; i++)
            {
                _runMeshes[i]?.Dispose();
                _runMeshes[i] = null;
            }

            for (int i = 0; i < _spriteMeshes.Length; i++)
            {
                _spriteMeshes[i]?.Dispose();
            }

            Array.Clear(_spriteMeshes);
        }
    }
}
