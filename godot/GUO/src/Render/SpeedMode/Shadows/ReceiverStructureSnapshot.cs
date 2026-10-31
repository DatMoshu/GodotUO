// SPDX-License-Identifier: BSD-2-Clause
// Task1053 source proposal. Not bound to any scene, surface or callback.
using System;
using System.Numerics;
using N2 = System.Numerics.Vector2;
using N3 = System.Numerics.Vector3;

namespace GUO.Renderer.SpeedMode.Shadows;

internal readonly record struct ReceiverFrameKey(ulong Scene, ulong Frame, ulong Topology,
    int SurfaceX, int SurfaceY, int Width, int Height, Matrix3x2 View);
internal readonly record struct ReceiverPhysicalLayer(ulong Owner, float Z, ReceiverPlaneKind Kind);
internal readonly record struct ReceiverEnumeration(ulong StartTopology, ulong EndTopology,
    int VisitedNodes, int ExpectedNodes, bool Unsupported);

// Builder on the game thread, immutable after Seal. No live map references.
// Own one instance per admitted ticket; release only after that ticket retires.
internal sealed class ReceiverStructureSnapshot
{
    internal const int Side = 32, LayersPerCell = 4, CoversPerCell = 8;
    internal const int CorridorCells = 64, CorridorCovers = CorridorCells * CoversPerCell;
    internal const int ProducerNodes = 64;
    // Native structure domain: sbyte Z + byte tile height <= 382. Unknown or
    // extended structures reject, rather than silently invalidating this sky.
    internal const float SkyZ = 512, MinZ = -128, MaxZ = 382;
    private readonly ReceiverPhysicalLayer[] _layers = new ReceiverPhysicalLayer[Side * Side * LayersPerCell];
    private readonly ReceiverCover[] _covers = new ReceiverCover[Side * Side * CoversPerCell];
    private readonly byte[] _state = new byte[Side * Side]; // 0 unvisited, 1 complete, 2 rejected
    private readonly byte[] _layerCount = new byte[Side * Side], _coverCount = new byte[Side * Side];
    private readonly ReceiverFrameKey _key;
    private readonly int _x, _y, _width, _height;
    private bool _sealed;

    internal ReceiverStructureSnapshot(ReceiverFrameKey key, int x, int y, int width, int height)
    {
        if (key.Scene == 0 || key.Frame == 0 || key.Topology == 0 || key.Width <= 0 || key.Height <= 0
            || !Finite(key.View) || !float.IsFinite(key.View.GetDeterminant()) || key.View.GetDeterminant() == 0
            || width <= 0 || height <= 0 || width > Side || height > Side
            || x < -65536 || y < -65536 || x > 65536 || y > 65536)
            throw new ArgumentException("Invalid bounded receiver snapshot");
        _key = key; _x = x; _y = y; _width = width; _height = height;
    }

    // Call once per cell AFTER full structural enumeration with start/end
    // topology equal to key.Topology. Rejected/duplicate cells never recover
    // within this ticket. Input layers are canonical, increasing by Z.
    internal bool PutCell(int x, int y, ReceiverEnumeration scan,
        ReadOnlySpan<ReceiverPhysicalLayer> layers, ReadOnlySpan<ReceiverCover> covers)
    {
        if (_sealed || !Index(x, y, out int cell)) return false;
        if (_state[cell] != 0) { _state[cell] = 2; return false; }
        _state[cell] = 2;
        if (scan.Unsupported || scan.VisitedNodes < 0 || scan.VisitedNodes > ProducerNodes
            || scan.VisitedNodes != scan.ExpectedNodes || scan.StartTopology != _key.Topology || scan.EndTopology != _key.Topology
            || layers.Length > scan.VisitedNodes || covers.Length > scan.VisitedNodes * 2
            || layers.Length > LayersPerCell || covers.Length > CoversPerCell) return false;
        for (int i = 0; i < layers.Length; i++)
        {
            var layer = layers[i];
            if (layer.Owner == 0 || !float.IsFinite(layer.Z) || layer.Z < MinZ || layer.Z > MaxZ
                || layer.Kind is not (ReceiverPlaneKind.Floor or ReceiverPlaneKind.Roof)
                || (i > 0 && layers[i - 1].Z >= layer.Z)) return false;
        }
        foreach (var cover in covers)
            if (!ReceiverGeometry.ValidCover(cover) || cover.Min.X < x || cover.Max.X > x + 1
                || cover.Min.Y < y || cover.Max.Y > y + 1 || cover.Min.Z < MinZ || cover.Max.Z > MaxZ) return false;
        layers.CopyTo(_layers.AsSpan(cell * LayersPerCell));
        covers.CopyTo(_covers.AsSpan(cell * CoversPerCell));
        _layerCount[cell] = (byte)layers.Length; _coverCount[cell] = (byte)covers.Length;
        _state[cell] = 1;
        return true;
    }

    internal void Seal() => _sealed = true;

    // Bound consumer wrappers: the corridor is derived from THIS projected
    // sample/source, never borrowed from another ray and called "complete".
    internal bool TryProject(ReceiverFrameKey key, ReceiverCaster caster, N3 sample,
        int receiverX, int receiverY, ulong receiverOwner, float receiverZ,
        ReceiverSource source, Span<ReceiverCover> scratch, out N3 projected)
    {
        projected = default;
        if (source.Kind == ReceiverSourceKind.Sun && source.SkyZ != SkyZ) return false;
        if (!TryLayer(key, receiverX, receiverY, receiverOwner, receiverZ, out var patch)
            || !ReceiverGeometry.TryProject(caster, sample, patch, source, ReadOnlySpan<ReceiverCover>.Empty, true, out var candidate)
            || !ReceiverGeometry.TrySourceEnd(candidate, source, out var end)
            || !TryCopyCorridor(key, candidate, end, scratch, out int count)) return false;
        return ReceiverGeometry.TryProject(caster, sample, patch, source, scratch.Slice(0, count), true, out projected);
    }

    internal bool TrySheltered(ReceiverFrameKey key, N3 contact, Span<ReceiverCover> scratch, out bool sheltered)
    {
        sheltered = false;
        N3 end = new(contact.X, contact.Y, SkyZ);
        return TryCopyCorridor(key, contact, end, scratch, out int count)
            && ReceiverGeometry.TrySheltered(contact, SkyZ, scratch.Slice(0, count), true, out sheltered);
    }

    internal bool TryLayer(ReceiverFrameKey key, int x, int y, ulong owner, float z, out ReceiverPatch patch)
    {
        patch = default;
        if (!Matches(key) || !Index(x, y, out int cell) || _state[cell] != 1) return false;
        for (int i = 0; i < _layerCount[cell]; i++)
        {
            var layer = _layers[cell * LayersPerCell + i];
            if (layer.Owner != owner || layer.Z != z) continue;
            patch = new(new(x, y), new(x + 1, y + 1), z,
                (uint)(cell * LayersPerCell + i + 1), layer.Kind);
            return true;
        }
        return false;
    }

    // Conservative rectangle supercover. Every intersected/tangent tile is
    // enumerated, even if the line misses its interior. Bounded to64 cells;
    // long/diagonal corridors reject rather than truncate. No allocation.
    internal bool TryCopyCorridor(ReceiverFrameKey key, N3 start, N3 end,
        Span<ReceiverCover> output, out int count)
    {
        count = 0;
        if (!Matches(key) || !Finite(start) || !Finite(end)
            || start.Z < MinZ || end.Z < MinZ || start.Z > SkyZ || end.Z > SkyZ) return false;
        float minX = MathF.Min(start.X, end.X), minY = MathF.Min(start.Y, end.Y);
        float maxX = MathF.Max(start.X, end.X), maxY = MathF.Max(start.Y, end.Y);
        // Include the negative-side tile when a closed box touches an integer edge.
        float left = MathF.Floor(minX) - (minX == MathF.Floor(minX) ? 1 : 0);
        float top = MathF.Floor(minY) - (minY == MathF.Floor(minY) ? 1 : 0);
        float right = MathF.Floor(maxX), bottom = MathF.Floor(maxY);
        if (left < _x || top < _y || right >= _x + _width || bottom >= _y + _height
            || (right - left + 1) * (bottom - top + 1) > CorridorCells) return false;
        int needed = 0;
        for (int y = (int)top; y <= bottom; y++) for (int x = (int)left; x <= right; x++)
        {
            Index(x, y, out int cell);
            if (_state[cell] != 1) return false;
            needed += _coverCount[cell];
        }
        if (needed > output.Length) return false;
        // No partial output is exposed on failure; all gates passed before copy.
        for (int y = (int)top; y <= bottom; y++) for (int x = (int)left; x <= right; x++)
        {
            Index(x, y, out int cell);
            int n = _coverCount[cell];
            _covers.AsSpan(cell * CoversPerCell, n).CopyTo(output.Slice(count)); count += n;
        }
        return true;
    }

    private bool Matches(ReceiverFrameKey key) => _sealed && key == _key;
    private bool Index(int x, int y, out int cell)
    {
        cell = 0;
        if (x < _x || y < _y || x >= _x + _width || y >= _y + _height) return false;
        cell = (y - _y) * Side + x - _x; return true;
    }
    private static bool Finite(N3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
    private static bool Finite(Matrix3x2 m) => float.IsFinite(m.M11) && float.IsFinite(m.M12)
        && float.IsFinite(m.M21) && float.IsFinite(m.M22) && float.IsFinite(m.M31) && float.IsFinite(m.M32);
}
