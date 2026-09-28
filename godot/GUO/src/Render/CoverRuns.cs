// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Godot;
using GUO.Compat;
using Color = Godot.Color;

namespace GUO.Renderer
{
    /// <summary>
    /// Epic B, B4 fix 2c (--merged-cover): the meshes for runs of covering land,
    /// each run of consecutive covering tiles one mesh over LandPages'
    /// Texture2DArray, kept from frame to frame.
    /// </summary>
    /// <remarks>
    /// GUO addition, not in ClassicUO. Covering land (RenderLists.CoverFromBelow)
    /// was one canvas mesh, so one draw call, per tile. A run is the tiles the
    /// sorted pass draws with nothing between them, so one mesh of them draws
    /// the same pixels in the same order. A run is found again next frame by its
    /// tiles (layer, index, atlas layer) and reused while every tile's quad is
    /// byte-for-byte the one it was built from, as MeshLayer.GetSpriteMesh does
    /// for a single tile; a run not drawn for a few frames is dropped.
    /// </remarks>
    internal sealed class CoverRuns
    {
        private sealed class Entry
        {
            public MeshLayer[] Layers;
            public int[] Indices;
            public int[] Pages;
            public MeshQuad[] Quads;
            public ArrayMesh Mesh;
            public long Frame;
        }

        private readonly Dictionary<int, List<Entry>> _entries = new();
        private long _frame;

        /// <summary>Run meshes built (not reused), for the perf probe.</summary>
        public static int Builds;

        public void NextFrame()
        {
            _frame++;
            if (_frame % 60 != 0)
            {
                return;
            }

            var empty = new List<int>();
            foreach ((int hash, List<Entry> list) in _entries)
            {
                list.RemoveAll(e => e.Frame < _frame - 3);
                if (list.Count == 0)
                {
                    empty.Add(hash);
                }
            }

            foreach (int hash in empty)
            {
                _entries.Remove(hash);
            }
        }

        public ArrayMesh Get(List<(MeshLayer Layer, int Index, int Page, Vector2 Scale)> run)
        {
            int hash = 17;
            foreach ((MeshLayer layer, int index, int page, _) in run)
            {
                hash = unchecked(((hash * 31 + RuntimeHelpers.GetHashCode(layer)) * 31 + index) * 31 + page);
            }

            if (!_entries.TryGetValue(hash, out List<Entry> list))
            {
                _entries[hash] = list = new List<Entry>();
            }

            Entry stale = null;
            foreach (Entry e in list)
            {
                if (!SameTiles(e, run))
                {
                    continue;
                }

                if (SameQuads(e, run))
                {
                    e.Frame = _frame;
                    return e.Mesh;
                }

                stale = e;
                break;
            }

            Entry entry = stale ?? new Entry { Mesh = new ArrayMesh() };
            if (stale == null)
            {
                entry.Layers = new MeshLayer[run.Count];
                entry.Indices = new int[run.Count];
                entry.Pages = new int[run.Count];
                list.Add(entry);
            }

            entry.Quads = new MeshQuad[run.Count];
            for (int i = 0; i < run.Count; i++)
            {
                entry.Layers[i] = run[i].Layer;
                entry.Indices[i] = run[i].Index;
                entry.Pages[i] = run[i].Page;
                entry.Quads[i] = run[i].Layer.Vertices[run[i].Index];
            }

            entry.Frame = _frame;
            Build(entry.Mesh, run);
            Builds++;
            return entry.Mesh;
        }

        private static bool SameTiles(Entry e, List<(MeshLayer Layer, int Index, int Page, Vector2 Scale)> run)
        {
            if (e.Layers.Length != run.Count)
            {
                return false;
            }

            for (int i = 0; i < run.Count; i++)
            {
                if (!ReferenceEquals(e.Layers[i], run[i].Layer) || e.Indices[i] != run[i].Index || e.Pages[i] != run[i].Page)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool SameQuads(Entry e, List<(MeshLayer Layer, int Index, int Page, Vector2 Scale)> run)
        {
            for (int i = 0; i < run.Count; i++)
            {
                ReadOnlySpan<byte> built = MemoryMarshal.AsBytes(new ReadOnlySpan<MeshQuad>(ref e.Quads[i]));
                ReadOnlySpan<byte> now = MemoryMarshal.AsBytes(new ReadOnlySpan<MeshQuad>(ref run[i].Layer.Vertices[run[i].Index]));
                if (!built.SequenceEqual(now))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>MergedLand's array mesh, for these tiles only: the same vertices, winding and layer.</summary>
        private static void Build(ArrayMesh mesh, List<(MeshLayer Layer, int Index, int Page, Vector2 Scale)> run)
        {
            int v = run.Count * 6;
            var points = new Vector2[v];
            var uvs = new Vector2[v];
            var colors = new Color[v];
            var custom = new float[v * 4];
            int w = 0;
            foreach ((MeshLayer layer, int i, int page, Vector2 scale) in run)
            {
                ref MeshQuad q = ref layer.Vertices[i];
                MergedLand.Put(points, uvs, colors, custom, w + 0, q.Position0, q.TextureCoordinate0 * scale, q.Hue0, q.Normal0, page);
                MergedLand.Put(points, uvs, colors, custom, w + 1, q.Position1, q.TextureCoordinate1 * scale, q.Hue1, q.Normal1, page);
                MergedLand.Put(points, uvs, colors, custom, w + 2, q.Position2, q.TextureCoordinate2 * scale, q.Hue2, q.Normal2, page);
                MergedLand.Put(points, uvs, colors, custom, w + 3, q.Position1, q.TextureCoordinate1 * scale, q.Hue1, q.Normal1, page);
                MergedLand.Put(points, uvs, colors, custom, w + 4, q.Position3, q.TextureCoordinate3 * scale, q.Hue3, q.Normal3, page);
                MergedLand.Put(points, uvs, colors, custom, w + 5, q.Position2, q.TextureCoordinate2 * scale, q.Hue2, q.Normal2, page);
                w += 6;
            }

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
}
