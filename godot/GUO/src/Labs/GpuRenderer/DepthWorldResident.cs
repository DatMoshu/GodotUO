// SPDX-License-Identifier: BSD-2-Clause
#if GUO_LABS
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Godot;
using GUO.Renderer;

namespace GUO.Labs.GpuRenderer;

/// <summary>
/// ADR-0044 spike: chunk land and statics resident on the GPU. Each chunk
/// layer is split into groups of at most four textures (its quads are
/// texture-sorted, so a group is a contiguous range). A group's quads are
/// packed once per content change; its visible-index list is rebuilt only
/// when the layer's visibility flags change, as upstream's MeshLayer does.
/// A warm standing frame packs and uploads nothing per static.
/// </summary>
internal sealed class DepthWorldResident
{
    // 4 corners of 48 bytes, then depth, art slot, object Z (or -128) and a pad.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct Quad
    {
        public OrderedWorldDevice.Vertex A, B, C, D;
        public OrderedWorldDevice.Float4 Aux;
    }
    internal const int QuadBytes = 208;

    internal struct Draw
    {
        public uint VisibleBase, Count;
        public OrderedWorldDevice.TextureSet Textures;
        public bool Land;
    }

    private sealed class Group
    {
        public int Start, End, Base, VisibleBase, VisibleCount;
        public bool[] Previous;
        public OrderedWorldDevice.Textures[] Arts;
        public int ArtCount;
    }

    private sealed class Layer
    {
        public int Stamp = int.MinValue, Count = -1, LastFrame;
        public readonly List<Group> Groups = new();
    }

    private readonly Dictionary<MeshLayer, Layer> _layers = new(ReferenceEqualityComparer.Instance);
    private readonly List<MeshLayer> _stale = new();
    internal Quad[] Quads = new Quad[16384];
    internal uint[] Visible = new uint[16384];
    internal int QuadCount, VisibleCount;
    internal readonly List<(int Start, int Count)> DirtyQuads = new(), DirtyVisible = new();
    internal bool AllDirty = true;
    internal readonly List<Draw> Draws = new(512);
    private int _frame;
    internal static long Packed, VisibleRebuilds, ResidentDraws, Resets;

    internal void BeginFrame()
    {
        Draws.Clear();
        _frame++;
        if (_frame % 600 == 0) Collect();
    }

    /// <summary>Queues one layer's visible groups. False when a texture cannot be drawn here.</summary>
    internal bool Add(MeshLayer layer, bool land, Rid hue, Rid light, out string reason)
    {
        reason = null;
        if (layer.Count == 0) return true;
        if (!_layers.TryGetValue(layer, out var entry))
            _layers.Add(layer, entry = new Layer());
        entry.LastFrame = _frame;
        if (entry.Stamp != layer.ContentStamp || entry.Count != layer.Count)
        {
            if (!Pack(layer, land, entry, hue, light, out reason)) return false;
        }
        foreach (var group in entry.Groups)
        {
            var visible = layer.Visible.AsSpan(group.Start, group.End - group.Start);
            if (!visible.SequenceEqual(group.Previous))
            {
                int n = 0;
                for (int i = 0; i < visible.Length; i++)
                    if (visible[i]) Visible[group.VisibleBase + n++] = (uint)(group.Base + i);
                group.VisibleCount = n;
                visible.CopyTo(group.Previous);
                DirtyVisible.Add((group.VisibleBase, Math.Max(n, 1)));
                VisibleRebuilds++;
            }
            if (group.VisibleCount == 0) continue;
            var set = new OrderedWorldDevice.TextureSet(group.Arts[0]);
            for (int a = 1; a < group.ArtCount; a++) set = set.WithArt(a, group.Arts[a].ArtRs);
            Draws.Add(new Draw { VisibleBase = (uint)group.VisibleBase, Count = (uint)group.VisibleCount, Textures = set, Land = land });
            ResidentDraws++;
        }
        return true;
    }

    private bool Pack(MeshLayer layer, bool land, Layer entry, Rid hue, Rid light, out string reason)
    {
        reason = null;
        entry.Groups.Clear();
        int count = layer.Count;
        int i = 0;
        while (i < count)
        {
            var group = new Group { Start = i, Arts = new OrderedWorldDevice.Textures[OrderedWorldDevice.ArtSlots] };
            int start = i;
            while (i < count)
            {
                Texture2D texture = layer.Textures[i];
                if (texture == null) { reason = "missing resident texture"; return false; }
                if (GUO.Renderer.Arts.Art.IsHdTexture(texture)) { reason = "HD texture in a chunk mesh"; return false; }
                Rid rid = texture.GetRid();
                int slot = -1;
                for (int a = 0; a < group.ArtCount; a++) if (group.Arts[a].ArtRs == rid) { slot = a; break; }
                if (slot < 0)
                {
                    if (group.ArtCount == OrderedWorldDevice.ArtSlots) break;
                    slot = group.ArtCount++;
                    group.Arts[slot] = new OrderedWorldDevice.Textures(rid, hue, light);
                }
                i++;
            }
            group.End = i;
            int size = group.End - start;
            group.Base = Reserve(ref Quads, ref QuadCount, size);
            group.VisibleBase = Reserve(ref Visible, ref VisibleCount, size);
            group.Previous = new bool[size];
            group.VisibleCount = 0;
            for (int q = 0; q < size; q++)
            {
                int index = start + q;
                ref readonly MeshQuad m = ref layer.Vertices[index];
                Rid rid = layer.Textures[index].GetRid();
                int slot = 0;
                for (int a = 0; a < group.ArtCount; a++) if (group.Arts[a].ArtRs == rid) { slot = a; break; }
                Quads[group.Base + q] = PackQuad(m, land, layer.Depths[index], slot, land ? (sbyte)-128 : layer.ObjectZ[index]);
            }
            DirtyQuads.Add((group.Base, size));
            DirtyVisible.Add((group.VisibleBase, size));
            Packed += size;
            entry.Groups.Add(group);
        }
        entry.Stamp = layer.ContentStamp;
        entry.Count = count;
        return true;
    }

    private static Quad PackQuad(in MeshQuad m, bool land, float depth, int slot, sbyte z)
    {
        // Land is the canvas mesh path (UNORM8 colours, mesh diagonal); a
        // meshed static is the rect sprite path (float colours, rect diagonal).
        bool mesh = land, rect = !land;
        Color c0 = UltimaBatcher2D.Encode(m.Hue0), c1 = UltimaBatcher2D.Encode(m.Hue1);
        Color c2 = UltimaBatcher2D.Encode(m.Hue2), c3 = UltimaBatcher2D.Encode(m.Hue3);
        return new Quad
        {
            A = OrderedWorldCapture.PackVertex(m.Position0, m.TextureCoordinate0, c0, m.Normal0, mesh, rect),
            B = OrderedWorldCapture.PackVertex(m.Position1, m.TextureCoordinate1, c1, m.Normal1, mesh, rect),
            C = OrderedWorldCapture.PackVertex(m.Position2, m.TextureCoordinate2, c2, m.Normal2, mesh, rect),
            D = OrderedWorldCapture.PackVertex(m.Position3, m.TextureCoordinate3, c3, m.Normal3, mesh, rect),
            Aux = new OrderedWorldDevice.Float4(depth, slot, z, 0),
        };
    }

    private static int Reserve<T>(ref T[] array, ref int used, int size)
    {
        int at = used;
        used += size;
        if (used > array.Length)
        {
            int next = array.Length;
            while (next < used) next *= 2;
            Array.Resize(ref array, next);
        }
        return at;
    }

    /// <summary>Drops layers not drawn for a while; compacts by rebuilding from scratch.</summary>
    private void Collect()
    {
        // Forget layers not drawn for ten seconds; their ranges become waste.
        // Compact only when waste outweighs live quads: a reset repacks and
        // re-uploads everything visible on the next frame.
        _stale.Clear();
        int live = 0;
        foreach (var pair in _layers)
        {
            if (_frame - pair.Value.LastFrame > 600) { _stale.Add(pair.Key); continue; }
            foreach (var group in pair.Value.Groups) live += group.End - group.Start;
        }
        foreach (var layer in _stale) _layers.Remove(layer);
        if (QuadCount <= 2 * live + 65536) return;
        _layers.Clear();
        QuadCount = VisibleCount = 0;
        DirtyQuads.Clear(); DirtyVisible.Clear();
        AllDirty = true;
        Resets++;
    }

    internal void Uploaded()
    {
        DirtyQuads.Clear();
        DirtyVisible.Clear();
        AllDirty = false;
    }

    internal void Invalidate() => AllDirty = true;
}
#endif
