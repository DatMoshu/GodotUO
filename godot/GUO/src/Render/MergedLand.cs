// SPDX-License-Identifier: BSD-2-Clause

using System.Collections.Generic;
using Godot;
using GUO.Compat;
using Color = Godot.Color;

namespace GUO.Renderer
{
    /// <summary>
    /// Epic B, B4 fix 1 (--merged-land): the land of every visible chunk drawn
    /// as a few meshes instead of one mesh per texture run per chunk.
    /// </summary>
    /// <remarks>
    /// GUO addition, not in ClassicUO. Every <c>canvas_item_add_mesh</c> is a
    /// draw call of its own in Godot's canvas renderer, and zoomed out the land
    /// pass alone was 1,200-4,000 of them (perf probe, 2026-09-27). The merged
    /// meshes are rebuilt only when the visible chunks, or any chunk's visible
    /// land (<see cref="MeshLayer.BuildStamp"/>), change.
    ///
    /// Two groupings:
    /// <list type="bullet">
    /// <item>By texture (<c>--merged-land</c>): one mesh per texture, the
    /// fewest calls. It changes the order land is drawn in, and neighbouring
    /// tiles overlap along their diamond edges, so edge pixels can differ
    /// from upstream's (measured: hundreds of pixels a frame on tile
    /// edges).</item>
    /// <item>Ordered (<c>--merged-land=ordered</c>): sprites in exactly the
    /// order the per-chunk runs drew them, a new mesh only where the texture
    /// changes -- runs merge only across chunk edges. The same picture by
    /// construction; fewer calls only where consecutive runs share a
    /// texture.</item>
    /// </list>
    /// </remarks>
    internal sealed class MergedLand
    {
        public static bool Enabled;
        public static bool Ordered;

        /// <summary>
        /// --merged-land=array: all visible land in one mesh, in the original
        /// order, over LandPages' Texture2DArray -- the fewest calls and the
        /// same picture. Falls back to the ordered grouping for a rebuild that
        /// meets a land texture that is not an atlas page.
        /// </summary>
        public static bool Array;
        private bool _arrayBuilt;

        private readonly List<(MeshLayer Layer, int Stamp)> _key = new();
        private readonly List<(MeshLayer Layer, int Stamp)> _now = new();
        private readonly List<(Texture2D Texture, List<(MeshLayer Layer, int Index)> Sprites)> _segments = new();
        private readonly List<ArrayMesh> _meshes = new();
        private bool _keyOrdered;
        private int _sprites;

        public int Rebuilds { get; private set; }

        /// <summary>Meshes drawn a frame, for the perf probe.</summary>
        public static int LastSegments;

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

            if (Array)
            {
                LandPages.Sync();
            }

            if (!Same() || _keyOrdered != Ordered)
            {
                Rebuild();
                _key.Clear();
                _key.AddRange(_now);
                _keyOrdered = Ordered;
            }

            if (_arrayBuilt)
            {
                LandPages.Sync();
                batcher.DrawLandArrayMesh(_meshes[0], LandPages.Array);
                LastSegments = 1;
                return _sprites;
            }

            for (int i = 0; i < _segments.Count; i++)
            {
                batcher.DrawLandMesh(_meshes[i], _segments[i].Texture);
            }

            LastSegments = _segments.Count;
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
            _arrayBuilt = Array && RebuildArray();
            if (_arrayBuilt)
            {
                return;
            }

            _segments.Clear();
            _sprites = 0;
            var byTexture = new Dictionary<Texture2D, int>();

            // Chunk by chunk, sprite by sprite: the order DrawMeshLayer drew in.
            foreach ((MeshLayer layer, _) in _now)
            {
                for (int i = 0; i < layer.Count; i++)
                {
                    Texture2D t = layer.Textures[i];
                    if (!layer.Visible[i] || t == null)
                    {
                        continue;
                    }

                    _sprites++;
                    if (Ordered)
                    {
                        if (_segments.Count == 0 || !ReferenceEquals(_segments[^1].Texture, t))
                        {
                            _segments.Add((t, new List<(MeshLayer, int)>()));
                        }

                        _segments[^1].Sprites.Add((layer, i));
                    }
                    else
                    {
                        if (!byTexture.TryGetValue(t, out int s))
                        {
                            byTexture[t] = s = _segments.Count;
                            _segments.Add((t, new List<(MeshLayer, int)>()));
                        }

                        _segments[s].Sprites.Add((layer, i));
                    }
                }
            }

            while (_meshes.Count < _segments.Count)
            {
                _meshes.Add(new ArrayMesh());
            }

            for (int s = 0; s < _segments.Count; s++)
            {
                List<(MeshLayer Layer, int Index)> sprites = _segments[s].Sprites;
                int v = sprites.Count * 6;
                var points = new Vector2[v];
                var uvs = new Vector2[v];
                var colors = new Color[v];
                var custom = new float[v * 4];
                int w = 0;
                foreach ((MeshLayer layer, int i) in sprites)
                {
                    ref MeshQuad q = ref layer.Vertices[i];

                    // MeshLayer.WriteTriangles' winding: 0 1 2, 1 3 2.
                    Put(points, uvs, colors, custom, w + 0, q.Position0, q.TextureCoordinate0, q.Hue0, q.Normal0);
                    Put(points, uvs, colors, custom, w + 1, q.Position1, q.TextureCoordinate1, q.Hue1, q.Normal1);
                    Put(points, uvs, colors, custom, w + 2, q.Position2, q.TextureCoordinate2, q.Hue2, q.Normal2);
                    Put(points, uvs, colors, custom, w + 3, q.Position1, q.TextureCoordinate1, q.Hue1, q.Normal1);
                    Put(points, uvs, colors, custom, w + 4, q.Position3, q.TextureCoordinate3, q.Hue3, q.Normal3);
                    Put(points, uvs, colors, custom, w + 5, q.Position2, q.TextureCoordinate2, q.Hue2, q.Normal2);
                    w += 6;
                }

                ArrayMesh mesh = _meshes[s];
                mesh.ClearSurfaces();
                var arrays = new Godot.Collections.Array();
                arrays.Resize((int)Mesh.ArrayType.Max);
                arrays[(int)Mesh.ArrayType.Vertex] = points;
                arrays[(int)Mesh.ArrayType.TexUV] = uvs;
                arrays[(int)Mesh.ArrayType.Color] = colors;
                arrays[(int)Mesh.ArrayType.Custom0] = custom;
                mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, null, null,
                    (Mesh.ArrayFormat)((ulong)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom0Shift));
            }
        }

        /// <summary>One mesh of every visible land sprite, in order, over the page array.</summary>
        private bool RebuildArray()
        {
            _segments.Clear();
            _sprites = 0;
            var sprites = new List<(MeshLayer Layer, int Index, int Layer_, Vector2 Scale)>();
            foreach ((MeshLayer layer, _) in _now)
            {
                for (int i = 0; i < layer.Count; i++)
                {
                    Texture2D t = layer.Textures[i];
                    if (!layer.Visible[i] || t == null)
                    {
                        continue;
                    }

                    int page = LandPages.LayerOf(t);
                    if (page < 0)
                    {
                        return false;
                    }

                    sprites.Add((layer, i, page, LandPages.ScaleOf(t)));
                }
            }

            _sprites = sprites.Count;
            int v = sprites.Count * 6;
            var points = new Vector2[v];
            var uvs = new Vector2[v];
            var colors = new Color[v];
            var custom = new float[v * 4];
            int w = 0;
            foreach ((MeshLayer layer, int i, int page, Vector2 scale) in sprites)
            {
                ref MeshQuad q = ref layer.Vertices[i];
                Put(points, uvs, colors, custom, w + 0, q.Position0, q.TextureCoordinate0 * scale, q.Hue0, q.Normal0, page);
                Put(points, uvs, colors, custom, w + 1, q.Position1, q.TextureCoordinate1 * scale, q.Hue1, q.Normal1, page);
                Put(points, uvs, colors, custom, w + 2, q.Position2, q.TextureCoordinate2 * scale, q.Hue2, q.Normal2, page);
                Put(points, uvs, colors, custom, w + 3, q.Position1, q.TextureCoordinate1 * scale, q.Hue1, q.Normal1, page);
                Put(points, uvs, colors, custom, w + 4, q.Position3, q.TextureCoordinate3 * scale, q.Hue3, q.Normal3, page);
                Put(points, uvs, colors, custom, w + 5, q.Position2, q.TextureCoordinate2 * scale, q.Hue2, q.Normal2, page);
                w += 6;
            }

            if (_meshes.Count == 0)
            {
                _meshes.Add(new ArrayMesh());
            }

            ArrayMesh mesh = _meshes[0];
            mesh.ClearSurfaces();
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = points;
            arrays[(int)Mesh.ArrayType.TexUV] = uvs;
            arrays[(int)Mesh.ArrayType.Color] = colors;
            arrays[(int)Mesh.ArrayType.Custom0] = custom;
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, null, null,
                (Mesh.ArrayFormat)((ulong)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom0Shift));
            return true;
        }

        internal static void Put(Vector2[] p, Vector2[] uv, Color[] c, float[] custom, int at, Vector2 position, Vector2 texture,
                                Vector3 hue, Vector3 normal, int layer = 0)
        {
            p[at] = position;
            uv[at] = texture;
            c[at] = UltimaBatcher2D.Encode(hue);
            custom[at * 4 + 0] = normal.X;
            custom[at * 4 + 1] = normal.Y;
            custom[at * 4 + 2] = normal.Z;
            custom[at * 4 + 3] = layer;
        }
    }
}
