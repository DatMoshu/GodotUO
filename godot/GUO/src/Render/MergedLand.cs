// SPDX-License-Identifier: BSD-2-Clause

using System.Collections.Generic;
using Godot;
using GUO.Compat;
using Color = Godot.Color;

namespace GUO.Renderer
{
    /// <summary>
    /// Epic B, B4 fix 1 (--merged-land): the land of every visible chunk drawn
    /// as one mesh per texture instead of one mesh per texture run per chunk.
    /// </summary>
    /// <remarks>
    /// GUO addition, not in ClassicUO. Every <c>canvas_item_add_mesh</c> is a
    /// draw call of its own in Godot's canvas renderer, and zoomed out the land
    /// pass alone was 1,200-4,000 of them (perf probe, 2026-09-27). The land
    /// pass draws before the sorted world, all on the mesh material, so its
    /// meshes can be merged across chunks. The merged meshes are rebuilt only
    /// when the visible chunks, or any chunk's visible land (its
    /// <see cref="MeshLayer.BuildStamp"/>), change -- standing still costs
    /// nothing; stepping across a chunk edge costs one rebuild.
    ///
    /// Grouping by texture changes the order land is drawn in across chunks.
    /// Land tiles only overlap where a stretched tile reaches over its
    /// neighbour, which is where a difference could show; the perf probe's
    /// --perf-parity checks the picture.
    /// </remarks>
    internal sealed class MergedLand
    {
        public static bool Enabled;

        private readonly List<(MeshLayer Layer, int Stamp)> _key = new();
        private readonly List<(MeshLayer Layer, int Stamp)> _now = new();
        private readonly List<Texture2D> _order = new();
        private readonly Dictionary<Texture2D, int> _counts = new();
        private readonly Dictionary<Texture2D, ArrayMesh> _meshes = new();
        private int _sprites;

        public int Rebuilds { get; private set; }

        /// <summary>Draws the land of these layers; returns how many sprites, as DrawMeshLayer does.</summary>
        public int Draw(UltimaBatcher2D batcher, List<MeshLayer> layers)
        {
            _now.Clear();
            foreach (MeshLayer layer in layers)
            {
                if (layer != null && layer.VisibleSpriteCount > 0)
                {
                    _now.Add((layer, layer.BuildStamp));
                }
            }

            if (!Same())
            {
                Rebuild();
                _key.Clear();
                _key.AddRange(_now);
            }

            foreach (Texture2D texture in _order)
            {
                batcher.DrawLandMesh(_meshes[texture], texture);
            }

            return _sprites;
        }

        private bool Same()
        {
            if (_now.Count != _key.Count)
            {
                return false;
            }

            for (int i = 0; i < _now.Count; i++)
            {
                if (!ReferenceEquals(_now[i].Layer, _key[i].Layer) || _now[i].Stamp != _key[i].Stamp)
                {
                    return false;
                }
            }

            return true;
        }

        private void Rebuild()
        {
            Rebuilds++;
            _order.Clear();
            _counts.Clear();
            _sprites = 0;

            // How many sprites each texture has, in first-appearance order.
            foreach ((MeshLayer layer, _) in _now)
            {
                for (int i = 0; i < layer.Count; i++)
                {
                    if (!layer.Visible[i] || layer.Textures[i] == null)
                    {
                        continue;
                    }

                    Texture2D t = layer.Textures[i];
                    if (!_counts.TryGetValue(t, out int n))
                    {
                        _order.Add(t);
                        n = 0;
                    }

                    _counts[t] = n + 1;
                    _sprites++;
                }
            }

            var at = new Dictionary<Texture2D, int>();
            var points = new Dictionary<Texture2D, Vector2[]>();
            var uvs = new Dictionary<Texture2D, Vector2[]>();
            var colors = new Dictionary<Texture2D, Color[]>();
            var custom = new Dictionary<Texture2D, float[]>();
            foreach (Texture2D t in _order)
            {
                int v = _counts[t] * 6;
                at[t] = 0;
                points[t] = new Vector2[v];
                uvs[t] = new Vector2[v];
                colors[t] = new Color[v];
                custom[t] = new float[v * 4];
            }

            // Chunk by chunk, sprite by sprite: within a texture the order is the
            // order the per-chunk meshes drew in.
            foreach ((MeshLayer layer, _) in _now)
            {
                for (int i = 0; i < layer.Count; i++)
                {
                    if (!layer.Visible[i] || layer.Textures[i] == null)
                    {
                        continue;
                    }

                    Texture2D t = layer.Textures[i];
                    int w = at[t];
                    ref MeshQuad q = ref layer.Vertices[i];

                    // MeshLayer.WriteTriangles' winding: 0 1 2, 1 3 2.
                    Put(points[t], uvs[t], colors[t], custom[t], w + 0, q.Position0, q.TextureCoordinate0, q.Hue0, q.Normal0);
                    Put(points[t], uvs[t], colors[t], custom[t], w + 1, q.Position1, q.TextureCoordinate1, q.Hue1, q.Normal1);
                    Put(points[t], uvs[t], colors[t], custom[t], w + 2, q.Position2, q.TextureCoordinate2, q.Hue2, q.Normal2);
                    Put(points[t], uvs[t], colors[t], custom[t], w + 3, q.Position1, q.TextureCoordinate1, q.Hue1, q.Normal1);
                    Put(points[t], uvs[t], colors[t], custom[t], w + 4, q.Position3, q.TextureCoordinate3, q.Hue3, q.Normal3);
                    Put(points[t], uvs[t], colors[t], custom[t], w + 5, q.Position2, q.TextureCoordinate2, q.Hue2, q.Normal2);
                    at[t] = w + 6;
                }
            }

            foreach (Texture2D t in _order)
            {
                if (!_meshes.TryGetValue(t, out ArrayMesh mesh))
                {
                    _meshes[t] = mesh = new ArrayMesh();
                }

                mesh.ClearSurfaces();
                var arrays = new Godot.Collections.Array();
                arrays.Resize((int)Mesh.ArrayType.Max);
                arrays[(int)Mesh.ArrayType.Vertex] = points[t];
                arrays[(int)Mesh.ArrayType.TexUV] = uvs[t];
                arrays[(int)Mesh.ArrayType.Color] = colors[t];
                arrays[(int)Mesh.ArrayType.Custom0] = custom[t];
                mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, null, null,
                    (Mesh.ArrayFormat)((ulong)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom0Shift));
            }
        }

        private static void Put(Vector2[] p, Vector2[] uv, Color[] c, float[] custom, int at, Vector2 position, Vector2 texture,
                                Vector3 hue, Vector3 normal)
        {
            p[at] = position;
            uv[at] = texture;
            c[at] = UltimaBatcher2D.Encode(hue);
            custom[at * 4 + 0] = normal.X;
            custom[at * 4 + 1] = normal.Y;
            custom[at * 4 + 2] = normal.Z;
            custom[at * 4 + 3] = 0f;
        }
    }
}
