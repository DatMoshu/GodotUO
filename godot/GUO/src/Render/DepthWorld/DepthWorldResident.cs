// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Godot;
using GUO.Renderer;

namespace GUO.Renderer.DepthWorld;

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
        public OrderedWorldDevice.TextureSet Set;
        public bool Land;
    }

    private sealed class Layer
    {
        public int Stamp = int.MinValue, Count = -1, LastFrame, VisibilityStamp = int.MinValue;
        // The quad and visible-index ranges this layer owns; a repack that
        // fits reuses them, so a fade's repeated repacks leave no waste.
        public int QuadBase, VisibleBase, Reserved;
        public readonly List<Group> Groups = new();
    }

    private readonly Dictionary<MeshLayer, Layer> _layers = new(ReferenceEqualityComparer.Instance);
    private readonly List<MeshLayer> _stale = new();
    internal Quad[] Quads = new Quad[16384];
    internal uint[] Visible = new uint[16384];
    internal int QuadCount, VisibleCount;
    internal readonly List<(int Start, int Count)> DirtyQuads = new(), DirtyVisible = new();
    internal bool AllDirty = true;
    // One draw per contiguous texture run within each category. Nonadjacent
    // runs cannot be regrouped: equal-depth primitives use submission order.
    internal readonly List<Draw> Draws = new(512);
    internal uint[] Merged = new uint[16384];
    internal int MergedCount;
    internal bool MergedDirty = true;
    private List<Group> _frameGroups = new(), _lastGroups = new();
    private bool _mergeNeeded = true;
    private Rid[] _rids = new Rid[1024];
    private int _frame;
    internal static long Packed, VisibleRebuilds, ResidentDraws, Resets;

    internal void BeginFrame()
    {
        _frameGroups.Clear();
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
        // A layer not reset since it was last read (its chunk's walk was reused) is unchanged.
        bool same = entry.VisibilityStamp == layer.VisibilityStamp;
        entry.VisibilityStamp = layer.VisibilityStamp;
        foreach (var group in entry.Groups)
        {
            var visible = layer.Visible.AsSpan(group.Start, group.End - group.Start);
            if (!same && !visible.SequenceEqual(group.Previous))
            {
                int n = 0;
                for (int i = 0; i < visible.Length; i++)
                    if (visible[i]) Visible[group.VisibleBase + n++] = (uint)(group.Base + i);
                group.VisibleCount = n;
                visible.CopyTo(group.Previous);
                _mergeNeeded = true;
                VisibleRebuilds++;
            }
            if (group.VisibleCount == 0) continue;
            _frameGroups.Add(group);
        }
        return true;
    }

    private bool Pack(MeshLayer layer, bool land, Layer entry, Rid hue, Rid light, out string reason)
    {
        reason = null;
        entry.Groups.Clear();
        int count = layer.Count;
        if (entry.Reserved < count) Allocate(entry, count);
        // One interop call per texture run (the layer is texture-sorted), not two per quad.
        if (_rids.Length < count) _rids = new Rid[Math.Max(count, _rids.Length * 2)];
        Texture2D last = null;
        Rid lastRid = default;
        int i = 0;
        while (i < count)
        {
            var group = new Group { Start = i, Arts = new OrderedWorldDevice.Textures[OrderedWorldDevice.ArtSlots] };
            int start = i;
            while (i < count)
            {
                Texture2D texture = layer.Textures[i];
                if (texture == null) { reason = "missing resident texture"; return false; }
                if (!ReferenceEquals(texture, last))
                {
                    if (GUO.Renderer.Arts.Art.IsHdTexture(texture)) { reason = "HD texture in a chunk mesh"; return false; }
                    last = texture;
                    lastRid = texture.GetRid();
                }
                Rid rid = _rids[i] = lastRid;
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
            group.Land = land;
            // Canonical art order, so groups on the same pages share one set and one draw.
            Array.Sort(group.Arts, 0, group.ArtCount, ArtOrder);
            group.Set = new OrderedWorldDevice.TextureSet(group.Arts[0]);
            for (int a = 1; a < group.ArtCount; a++) group.Set = group.Set.WithArt(a, group.Arts[a].ArtRs);
            int size = group.End - start;
            group.Base = entry.QuadBase + start;
            group.VisibleBase = entry.VisibleBase + start;
            group.Previous = new bool[size];
            group.VisibleCount = 0;
            for (int q = 0; q < size; q++)
            {
                int index = start + q;
                ref readonly MeshQuad m = ref layer.Vertices[index];
                Rid rid = _rids[index];
                int slot = 0;
                for (int a = 0; a < group.ArtCount; a++) if (group.Arts[a].ArtRs == rid) { slot = a; break; }
                Quads[group.Base + q] = PackQuad(m, land, layer.Depths[index], slot, land ? (sbyte)-128 : layer.ObjectZ[index]);
            }
            DirtyQuads.Add((group.Base, size));
            Packed += size;
            entry.Groups.Add(group);
        }
        _mergeNeeded = true;
        entry.Stamp = layer.ContentStamp;
        entry.Count = count;
        entry.VisibilityStamp = int.MinValue; // fresh groups: compare once
        return true;
    }

    private static readonly Comparer<OrderedWorldDevice.Textures> ArtOrder =
        Comparer<OrderedWorldDevice.Textures>.Create((a, b) => a.ArtRs.Id.CompareTo(b.ArtRs.Id));

    /// <summary>After the frame's Adds: the merged draws, rebuilt when a group's visibility or the group list changed.</summary>
    internal void Merge()
    {
        bool same = !_mergeNeeded && _frameGroups.Count == _lastGroups.Count;
        for (int i = 0; same && i < _frameGroups.Count; i++) same = ReferenceEquals(_frameGroups[i], _lastGroups[i]);
        (_lastGroups, _frameGroups) = (_frameGroups, _lastGroups);
        ResidentDraws += Draws.Count;
        if (same) return;
        _mergeNeeded = false;
        Draws.Clear();
        MergedCount = 0;
        // ADR-0044: land before statics, preserving traversal within each.
        // Only adjacent runs with identical bindings may share a draw. A global
        // texture bucket changes A,B,A into A,A,B and changes equal-depth cover.
        for (int pass = 0; pass < 2; pass++)
        {
            foreach (var group in _lastGroups)
            {
                if (group.Land != (pass == 0)) continue;
                int at = Reserve(ref Merged, ref MergedCount, group.VisibleCount);
                Array.Copy(Visible, group.VisibleBase, Merged, at, group.VisibleCount);
                int previous = Draws.Count - 1;
                if (previous >= 0 && Draws[previous].Land == group.Land
                    && Draws[previous].Textures == group.Set)
                {
                    var draw = Draws[previous];
                    draw.Count += (uint)group.VisibleCount;
                    Draws[previous] = draw;
                }
                else
                {
                    Draws.Add(new Draw { VisibleBase = (uint)at,
                        Count = (uint)group.VisibleCount, Textures = group.Set, Land = group.Land });
                }
            }
        }
        MergedDirty = true;
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

    /// <summary>
    /// A layer's quad and visible-index ranges (the same size, reserved
    /// together): first fit from the ranges dropped or outgrown layers left,
    /// else fresh at the end. Without reuse a long walk filled the buffers
    /// with waste until Collect repacked everything in one frame.
    /// </summary>
    private void Allocate(Layer entry, int count)
    {
        if (entry.Reserved > 0) Free(entry);
        for (int i = 0; i < _free.Count; i++)
        {
            var f = _free[i];
            if (f.Size < count) continue;
            entry.QuadBase = f.Quad; entry.VisibleBase = f.Visible; entry.Reserved = count;
            if (f.Size > count) _free[i] = (f.Quad + count, f.Visible + count, f.Size - count);
            else _free.RemoveAt(i);
            return;
        }
        entry.QuadBase = Reserve(ref Quads, ref QuadCount, count);
        entry.VisibleBase = Reserve(ref Visible, ref VisibleCount, count);
        entry.Reserved = count;
    }

    private void Free(Layer entry)
    {
        if (entry.Reserved > 0 && _free.Count < 4096) _free.Add((entry.QuadBase, entry.VisibleBase, entry.Reserved));
        entry.Reserved = 0;
    }

    private readonly List<(int Quad, int Visible, int Size)> _free = new();

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
            live += pair.Value.Reserved;
        }
        foreach (var layer in _stale) { Free(_layers[layer]); _layers.Remove(layer); }
        if (QuadCount <= 2 * live + 65536) return;
        _layers.Clear();
        _free.Clear();
        QuadCount = VisibleCount = 0;
        DirtyQuads.Clear(); DirtyVisible.Clear();
        AllDirty = true;
        _mergeNeeded = true;
        Resets++;
    }

    internal void Uploaded()
    {
        MergedDirty = false;
        DirtyQuads.Clear();
        DirtyVisible.Clear();
        AllDirty = false;
    }

    internal void Invalidate() => AllDirty = true;
}
