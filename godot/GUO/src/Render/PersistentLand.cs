// SPDX-License-Identifier: BSD-2-Clause
// Fixed chunk slots with ordered indices; ADR-0007.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Godot;
using RS = Godot.RenderingServer;

namespace GUO.Renderer;

internal sealed class PersistentLand : IDisposable
{
    private sealed class Slot
    {
        internal int Number, Count = -1, Stamp = -1, Generation = -1;
        internal Vector2 Min, Max;
        internal bool HasBounds;
    }

    private readonly record struct Page(int Layer, Vector2 Scale);
    private readonly Dictionary<MeshLayer, Slot> _slots = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<MeshLayer> _live = new(ReferenceEqualityComparer.Instance);
    private readonly List<MeshLayer> _expired = new();
    private readonly Dictionary<Texture2D, Page> _pages = new(ReferenceEqualityComparer.Instance);
    private readonly Stack<int> _free = new();
    private ArrayMesh _mesh;
    private byte[] _positions = System.Array.Empty<byte>();
    private byte[] _attributes = System.Array.Empty<byte>();
    private byte[] _indices = System.Array.Empty<byte>();
    private int _slotCapacity, _quadsPerSlot, _vertexCount, _indexCapacity;
    private int _positionStride, _attributeStride, _indexStride;
    private int _positionOffset, _colorOffset, _uvOffset, _customOffset;
    private int _previousIndexCount, _smallViewFrames;
    private bool _invalidateAll;
    private int _cachedSprites, _generation = -1;
    private readonly List<(MeshLayer Layer, int Stamp, int Count)> _order = new();
    private Aabb _bounds;

    // Observable accounting; API calls are CPU submissions, not measured GPU time.
    internal long SurfaceRebuilds, DirtySlots, DirtySprites, PositionBytes, AttributeBytes, IndexBytes;
    internal int LiveSlots => _slots.Count;
    internal int SlotCapacity => _slotCapacity;
    internal int QuadsPerSlot => _quadsPerSlot;
    internal int LastDrawCalls { get; private set; }
    internal int LastActiveIndices { get; private set; }

    internal bool TryDraw(UltimaBatcher2D batcher, IReadOnlyList<MeshLayer> orderedLayers,
        out int sprites, out string failure)
    {
        sprites = 0;
        failure = null;
        LastDrawCalls = LastActiveIndices = 0;
        try
        {
            if (_mesh != null && !_invalidateAll && _cachedSprites > 0
                && _generation == LandPages.Generation && SameOrder(orderedLayers)
                && (_smallViewFrames == 0 || ++_smallViewFrames < 120))
            {
                LandPages.Sync();
                sprites = _cachedSprites;
                LastActiveIndices = sprites * 6;
            }
            else
            {
                int maximumCount = Preflight(orderedLayers, out sprites);
                if (sprites == 0)
                {
                    // Preserve _previousIndexCount: next nonempty frame must clear its old tail.
                    _slots.Clear();
                    ResetFreeList();
                    _pages.Clear();
                    _expired.Clear();
                    _order.Clear();
                    _cachedSprites = 0;
                    return true; // BeginFrame has cleared the previous canvas command.
                }

                EnsureCapacity(_live.Count, maximumCount);
                ReconcileSlots(orderedLayers);
                int generation = LandPages.Generation;
                bool haveBounds = false;
                Vector2 min = default, max = default;
                foreach (MeshLayer layer in orderedLayers)
                {
                    if (!_live.Contains(layer)) continue;
                    Slot slot = _slots[layer];
                    if (_invalidateAll || slot.Count != layer.Count || slot.Stamp != layer.BuildStamp
                        || slot.Generation != generation)
                    {
                        PackAndUpload(layer, slot);
                        slot.Count = layer.Count;
                        slot.Stamp = layer.BuildStamp;
                        slot.Generation = generation;
                    }
                    if (slot.HasBounds)
                    {
                        if (!haveBounds) { min = slot.Min; max = slot.Max; haveBounds = true; }
                        else { min = min.Min(slot.Min); max = max.Max(slot.Max); }
                    }
                }

                // Register all pages before the single synchronization. Growth preserves
                // layer indices; Reset changes Generation and forces all live slot repacks.
                LandPages.Sync();
                if (LandPages.Array == null) throw new InvalidOperationException("No land page array.");
                if (_invalidateAll || !SameOrder(orderedLayers))
                {
                    UpdateIndices(orderedLayers, checked(sprites * 6));
                    _order.Clear();
                    foreach (MeshLayer layer in orderedLayers) _order.Add((layer, layer.BuildStamp, layer.Count));
                }

                // Buffer-region writes do not recalculate mesh bounds. Keep a current
                // conservative world-coordinate box; the batcher applies its usual offset.
                if (!haveBounds) throw new InvalidOperationException("Visible geometry has no bounds.");
                var bounds = new Aabb(new Vector3(min.X, min.Y, -1),
                    new Vector3(Math.Max(max.X - min.X, 0.001f), Math.Max(max.Y - min.Y, 0.001f), 2));
                if (_bounds != bounds) { _mesh.CustomAabb = bounds; _bounds = bounds; }
                _invalidateAll = false;
                _cachedSprites = sprites;
                _generation = LandPages.Generation;
            }
        }
        catch (Exception error)
        {
            _invalidateAll = true;
            failure = error.Message;
            sprites = 0;
            return false;
        }

        // All discovery, allocation and uploads completed before the sole draw.
        // Do not catch a submission exception then pretend no command was issued.
        batcher.DrawLandArrayMesh(_mesh, LandPages.Array);
        LastDrawCalls = 1;
        return true;
    }

    private bool SameOrder(IReadOnlyList<MeshLayer> layers)
    {
        if (_order.Count != layers.Count) return false;
        for (int i=0;i<layers.Count;i++)
            if (!ReferenceEquals(_order[i].Layer,layers[i]) || _order[i].Stamp!=layers[i].BuildStamp || _order[i].Count!=layers[i].Count)
                return false;
        return true;
    }

    private int Preflight(IReadOnlyList<MeshLayer> layers, out int sprites)
    {
        _live.Clear();
        _pages.Clear();
        sprites = 0;
        int maximumCount = 1;
        foreach (MeshLayer layer in layers)
        {
            if (layer == null || layer.Count == 0) continue;
            if (layer.Count < 0 || layer.Count > layer.Vertices.Length
                || layer.Count > layer.Visible.Length || layer.Count > layer.Textures.Length)
                throw new InvalidOperationException("Invalid MeshLayer count.");
            int visible = 0;
            for (int i = 0; i < layer.Count; ++i)
            {
                Texture2D texture = layer.Textures[i];
                // Match MergedLand's visible/null filtering, not VisibleSpriteCount alone.
                if (!layer.Visible[i] || texture == null) continue;
                ++visible;
                if (_pages.ContainsKey(texture)) continue;
                int page = LandPages.LayerOf(texture);
                if (page < 0) throw new NotSupportedException("Non-array land texture; use full ordered fallback.");
                _pages.Add(texture, new Page(page, LandPages.ScaleOf(texture)));
            }
            if (visible == 0) continue;
            // Normal visibleChunks is unique. Reject anomalous duplicates rather than
            // underestimate fixed index capacity or silently drop a repeated draw.
            if (!_live.Add(layer)) throw new NotSupportedException("Repeated layer; use ordered fallback.");
            sprites = checked(sprites + visible);
            maximumCount = Math.Max(maximumCount, layer.Count);
        }
        return maximumCount;
    }

    private static int PowerOfTwo(int value)
    {
        int result = 1;
        while (result < value) result = checked(result * 2);
        return result;
    }

    private void EnsureCapacity(int layers, int count)
    {
        bool grow = _mesh == null || layers > _slotCapacity || count > _quadsPerSlot;
        bool small = _mesh != null &&
            (layers <= _slotCapacity / 4 || count <= _quadsPerSlot / 4);
        _smallViewFrames = small ? _smallViewFrames + 1 : 0;
        // Avoid reallocating around a power-of-two boundary while still releasing
        // a much larger old view after a sustained teleport/zoom change.
        bool shrink = _smallViewFrames >= 120;
        if (!grow && !shrink) return;
        int slots = PowerOfTwo(layers);
        int quads = PowerOfTwo(count);
        CreateSurface(slots, quads);
        _smallViewFrames = 0;
    }

    private void CreateSurface(int slots, int quads)
    {
        int vertexCount = checked(1 + slots * quads * 4); // vertex zero is a finite sentinel.
        int indexCount = checked(slots * quads * 6);
        var candidate = new ArrayMesh();
        try
        {
            using var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = new Vector2[vertexCount];
            arrays[(int)Mesh.ArrayType.TexUV] = new Vector2[vertexCount];
            arrays[(int)Mesh.ArrayType.Color] = new Color[vertexCount];
            arrays[(int)Mesh.ArrayType.Custom0] = new float[checked(vertexCount * 4)];
            arrays[(int)Mesh.ArrayType.Index] = new int[indexCount]; // all degenerate initially.
            var flags = Mesh.ArrayFormat.FlagUseDynamicUpdate |
                (Mesh.ArrayFormat)((ulong)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom0Shift);
            // Compression is deliberately absent: match the current land mesh format.
            candidate.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, null, null, flags);
            if (candidate.GetSurfaceCount() != 1) throw new InvalidOperationException("Surface creation failed.");
            var format = (RS.ArrayFormat)(ulong)candidate.SurfaceGetFormat(0);
            int vStride = checked((int)RS.MeshSurfaceGetFormatVertexStride(format, vertexCount));
            int aStride = checked((int)RS.MeshSurfaceGetFormatAttributeStride(format, vertexCount));
            int iStride = checked((int)RS.MeshSurfaceGetFormatIndexStride(format, vertexCount));
            int pOffset = checked((int)RS.MeshSurfaceGetFormatOffset(format, vertexCount, (int)Mesh.ArrayType.Vertex));
            int cOffset = checked((int)RS.MeshSurfaceGetFormatOffset(format, vertexCount, (int)Mesh.ArrayType.Color));
            int uOffset = checked((int)RS.MeshSurfaceGetFormatOffset(format, vertexCount, (int)Mesh.ArrayType.TexUV));
            int nOffset = checked((int)RS.MeshSurfaceGetFormatOffset(format, vertexCount, (int)Mesh.ArrayType.Custom0));
            if (vStride != 8 || aStride != 28 || pOffset != 0 || cOffset != 0
                || uOffset != 4 || nOffset != 12 || (iStride != 2 && iStride != 4))
                throw new NotSupportedException("Unexpected installed mesh buffer layout.");

            // Allocate all CPU mirrors before replacing the current surface.
            byte[] positions = new byte[checked(vertexCount * vStride)];
            byte[] attributes = new byte[checked(vertexCount * aStride)];
            byte[] indices = new byte[checked(indexCount * iStride)];
            _mesh?.Dispose();
            _mesh = candidate;
            candidate = null;
            _positions = positions; _attributes = attributes; _indices = indices;
            _slotCapacity = slots; _quadsPerSlot = quads;
            _vertexCount = vertexCount; _indexCapacity = indexCount;
            _positionStride = vStride; _attributeStride = aStride; _indexStride = iStride;
            _positionOffset = pOffset; _colorOffset = cOffset; _uvOffset = uOffset; _customOffset = nOffset;
            _previousIndexCount = 0; _bounds = default;
            _slots.Clear();
            ResetFreeList();
            _invalidateAll = true;
            ++SurfaceRebuilds;
        }
        finally { candidate?.Dispose(); }
    }

    private void ResetFreeList()
    {
        _free.Clear();
        for (int i = _slotCapacity - 1; i >= 0; --i) _free.Push(i);
    }

    private void ReconcileSlots(IReadOnlyList<MeshLayer> layers)
    {
        _expired.Clear();
        foreach (var pair in _slots)
            if (!_live.Contains(pair.Key)) _expired.Add(pair.Key);
        foreach (MeshLayer layer in _expired)
        {
            _free.Push(_slots[layer].Number);
            _slots.Remove(layer);
        }
        _expired.Clear(); // Do not retain retired layers while the stationary fast path runs.
        foreach (MeshLayer layer in layers)
            if (layer != null && _live.Contains(layer) && !_slots.ContainsKey(layer))
                _slots.Add(layer, new Slot { Number = _free.Pop() });
    }

    private int FirstVertex(Slot slot) => checked(1 + slot.Number * _quadsPerSlot * 4);

    private void PackAndUpload(MeshLayer layer, Slot slot)
    {
        int first = FirstVertex(slot);
        slot.HasBounds = false;
        for (int i = 0; i < layer.Count; ++i)
        {
            Texture2D texture = layer.Textures[i];
            if (!layer.Visible[i] || texture == null) continue;
            Page page = _pages[texture];
            ref MeshQuad q = ref layer.Vertices[i];
            int at = checked(first + i * 4);
            Put(at, q.Position0, q.TextureCoordinate0 * page.Scale, q.Hue0, q.Normal0, page.Layer, slot);
            Put(at + 1, q.Position1, q.TextureCoordinate1 * page.Scale, q.Hue1, q.Normal1, page.Layer, slot);
            Put(at + 2, q.Position2, q.TextureCoordinate2 * page.Scale, q.Hue2, q.Normal2, page.Layer, slot);
            Put(at + 3, q.Position3, q.TextureCoordinate3 * page.Scale, q.Hue3, q.Normal3, page.Layer, slot);
        }
        int vertices = checked(layer.Count * 4);
        int positionStart = checked(first * _positionStride);
        int attributeStart = checked(first * _attributeStride);
        int positionLength = checked(vertices * _positionStride);
        int attributeLength = checked(vertices * _attributeStride);
        _mesh.SurfaceUpdateVertexRegion(0, positionStart, _positions.AsSpan(positionStart, positionLength));
        _mesh.SurfaceUpdateAttributeRegion(0, attributeStart, _attributes.AsSpan(attributeStart, attributeLength));
        ++DirtySlots;
        DirtySprites += layer.VisibleSpriteCount;
        PositionBytes += positionLength; AttributeBytes += attributeLength;
    }

    // Match RenderingServer::_surface_set_data: Color stores float, but multiplication
    // by the C++ double literal 255.0 occurs in double, followed by clamp and truncation.
    // ToRgba32 rounds and is not an equivalent mesh-packing operation.
    internal static byte MeshByte(float value) => (byte)Math.Clamp((double)value * 255.0, 0.0, 255.0);

    private void Put(int vertex, Vector2 p, Vector2 uv, Vector3 hue, Vector3 normal, int page, Slot slot)
    {
        if (!float.IsFinite(p.X) || !float.IsFinite(p.Y))
            throw new NotSupportedException("Nonfinite land position.");
        int v = checked(vertex * _positionStride + _positionOffset);
        Float(_positions, v, p.X); Float(_positions, v + 4, p.Y);
        int a = checked(vertex * _attributeStride);
        Color c = UltimaBatcher2D.Encode(hue);
        _attributes[a + _colorOffset] = MeshByte(c.R);
        _attributes[a + _colorOffset + 1] = MeshByte(c.G);
        _attributes[a + _colorOffset + 2] = MeshByte(c.B);
        _attributes[a + _colorOffset + 3] = MeshByte(c.A);
        Float(_attributes, a + _uvOffset, uv.X); Float(_attributes, a + _uvOffset + 4, uv.Y);
        Float(_attributes, a + _customOffset, normal.X); Float(_attributes, a + _customOffset + 4, normal.Y);
        Float(_attributes, a + _customOffset + 8, normal.Z); Float(_attributes, a + _customOffset + 12, page);
        if (!slot.HasBounds) { slot.Min = slot.Max = p; slot.HasBounds = true; }
        else { slot.Min = slot.Min.Min(p); slot.Max = slot.Max.Max(p); }
    }

    private static void Float(byte[] buffer, int offset, float value) =>
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(offset, 4), value);

    private void UpdateIndices(IReadOnlyList<MeshLayer> layers, int activeCount)
    {
        if (activeCount > _indexCapacity) throw new InvalidOperationException("Index capacity exceeded.");
        int touched = Math.Max(activeCount, _previousIndexCount);
        // The remaining GPU tail is already zero from creation/earlier shrinking.
        // Clearing max(old,new) is essential: clearing only new leaves ghost triangles.
        _indices.AsSpan(0, checked(touched * _indexStride)).Clear();
        int cursor = 0;
        foreach (MeshLayer layer in layers)
        {
            if (layer == null || !_live.Contains(layer)) continue;
            int first = FirstVertex(_slots[layer]);
            for (int i = 0; i < layer.Count; ++i)
            {
                if (!layer.Visible[i] || layer.Textures[i] == null) continue;
                int v = checked(first + i * 4);
                Index(ref cursor, v); Index(ref cursor, v + 1); Index(ref cursor, v + 2);
                Index(ref cursor, v + 1); Index(ref cursor, v + 3); Index(ref cursor, v + 2);
            }
        }
        if (cursor != activeCount) throw new InvalidOperationException("Visible stream changed during capture.");
        if (touched > 0)
        {
            int bytes = checked(touched * _indexStride);
            RS.MeshSurfaceUpdateIndexRegion(_mesh.GetRid(), 0, 0, _indices.AsSpan(0, bytes));
            IndexBytes += bytes;
        }
        _previousIndexCount = activeCount;
        LastActiveIndices = activeCount;
    }

    private void Index(ref int cursor, int vertex)
    {
        if ((uint)vertex >= (uint)_vertexCount) throw new InvalidOperationException("Vertex index out of range.");
        int offset = checked(cursor++ * _indexStride);
        if (_indexStride == 2)
            BinaryPrimitives.WriteUInt16LittleEndian(_indices.AsSpan(offset, 2), checked((ushort)vertex));
        else
            BinaryPrimitives.WriteUInt32LittleEndian(_indices.AsSpan(offset, 4), checked((uint)vertex));
    }

    public void Dispose()
    {
        _mesh?.Dispose(); _mesh = null;
        _slots.Clear(); _live.Clear(); _pages.Clear(); _expired.Clear(); _free.Clear();
        _order.Clear();
        _positions = _attributes = _indices = System.Array.Empty<byte>();
        _slotCapacity = _quadsPerSlot = _vertexCount = _indexCapacity = 0;
        _previousIndexCount = _smallViewFrames = 0;
        _cachedSprites = 0; _generation = -1;
    }
}
