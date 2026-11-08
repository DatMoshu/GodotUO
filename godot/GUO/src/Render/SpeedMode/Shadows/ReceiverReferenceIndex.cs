// SPDX-License-Identifier: BSD-2-Clause
// DEBUG DIAGNOSTIC ONLY. BigInteger/tree/array allocations; never a render path.
#if DEBUG
using System;
using System.Collections.Generic;
using System.Numerics;
using N2 = System.Numerics.Vector2;
using N3 = System.Numerics.Vector3;

namespace GUO.Renderer.SpeedMode.Shadows;

internal enum ReceiverReferenceFailure
{
    None, Domain, Key, Unsealed, Sealed, Duplicate, Missing, Unloaded, Incomplete,
    Unsupported, CellOverflow, GlobalCells, GlobalCovers, HashProbes, Queries,
    Corridor, Events, References, Destination
}

internal readonly record struct ReceiverReferenceTile(int X, int Y) : IComparable<ReceiverReferenceTile>
{
    public int CompareTo(ReceiverReferenceTile b) => Y != b.Y ? Y.CompareTo(b.Y) : X.CompareTo(b.X);
    internal ulong Packed => (uint)X | ((ulong)(uint)Y << 32);
}
internal readonly record struct ReceiverReferenceReceipt(N3 Start, N3 End,
    int Required, int Known, int Loaded, int Covers, ReceiverReferenceFailure Failure,
    ReceiverReferenceTile FirstFailed, int Copied = 0);

// One manually supplied immutable physical snapshot, not a native map reader.
internal sealed class ReceiverReferenceIndex
{
    internal const int CellCap = 8192, CoverCap = 32768, Slots = 16384, ProbeCap = 16;
    internal const int QueryCap = 64, ReferenceCap = 65536, CorridorCap = 1024, EventCap = 1024;
    internal const int CorridorCoverCap = 8192;
    private readonly record struct Cell(ReceiverReferenceTile Tile, ReceiverCover[] Covers,
        bool Loaded, ReceiverReferenceFailure Failure);
    private readonly int[] _slots = new int[Slots]; // index+1; zero is empty
    private readonly List<Cell> _cells = new(CellCap);
    private readonly ReceiverFrameKey _key;
    private int _covers, _queries, _references;
    private bool _sealed;

    internal ReceiverReferenceIndex(ReceiverFrameKey key)
    {
        if (key.Scene == 0 || key.Frame == 0 || key.Topology == 0 || key.Width <= 0 || key.Height <= 0
            || !float.IsFinite(key.View.M11) || !float.IsFinite(key.View.M12)
            || !float.IsFinite(key.View.M21) || !float.IsFinite(key.View.M22)
            || !float.IsFinite(key.View.M31) || !float.IsFinite(key.View.M32)
            || !float.IsFinite(key.View.GetDeterminant()) || key.View.GetDeterminant() == 0)
            throw new ArgumentException("Reference snapshot needs explicit identity");
        _key = key;
    }

    internal static bool Fits(int used, int added, int cap) => used >= 0 && added >= 0 && used <= cap && added <= cap - used;
    internal static int Slot(ReceiverReferenceTile p)
    {
        unchecked
        {
            ulong z = p.Packed + 0x9e3779b97f4a7c15UL;
            z = (z ^ (z >> 30)) * 0xbf58476d1ce4e5b9UL;
            z = (z ^ (z >> 27)) * 0x94d049bb133111ebUL;
            return (int)((z ^ (z >> 31)) & (Slots - 1));
        }
    }
    private int Find(ReceiverReferenceTile tile, out int slot)
    {
        slot = Slot(tile);
        for (int n = 0; n < ProbeCap; n++, slot = (slot + 1) & (Slots - 1))
        {
            int at = _slots[slot] - 1;
            if (at < 0 || _cells[at].Tile == tile) return at;
        }
        return -2;
    }

    internal ReceiverReferenceFailure Put(ReceiverFrameKey key, ReceiverReferenceTile tile,
        ReceiverEnumeration scan, bool loaded, ReadOnlySpan<ReceiverCover> covers)
    {
        if (_sealed) return ReceiverReferenceFailure.Sealed;
        if (key != _key) return ReceiverReferenceFailure.Key;
        if (tile.X < -65537 || tile.Y < -65537 || tile.X > 65536 || tile.Y > 65536)
            return ReceiverReferenceFailure.Domain;
        int at = Find(tile, out int slot);
        if (at == -2) return ReceiverReferenceFailure.HashProbes;
        if (at >= 0)
        {
            _cells[at] = _cells[at] with { Failure = ReceiverReferenceFailure.Duplicate };
            return ReceiverReferenceFailure.Duplicate;
        }
        if (!Fits(_cells.Count, 1, CellCap)) return ReceiverReferenceFailure.GlobalCells;
        var failure = !loaded ? ReceiverReferenceFailure.Unloaded
            : scan.Unsupported ? ReceiverReferenceFailure.Unsupported
            : scan.StartTopology != _key.Topology || scan.EndTopology != _key.Topology
                || scan.VisitedNodes < 0 || scan.VisitedNodes > ReceiverStructureSnapshot.ProducerNodes
                || scan.VisitedNodes != scan.ExpectedNodes ? ReceiverReferenceFailure.Incomplete
            : covers.Length > ReceiverStructureSnapshot.CoversPerCell
                || covers.Length > scan.VisitedNodes * 2 ? ReceiverReferenceFailure.CellOverflow
            : !Fits(_covers, covers.Length, CoverCap) ? ReceiverReferenceFailure.GlobalCovers
            : ReceiverReferenceFailure.None;
        if (failure == ReceiverReferenceFailure.None) foreach (var c in covers)
            if (!ReceiverGeometry.ValidCover(c) || c.Min.X < tile.X || c.Max.X > tile.X + 1
                || c.Min.Y < tile.Y || c.Max.Y > tile.Y + 1
                || c.Min.Z < ReceiverStructureSnapshot.MinZ || c.Max.Z > ReceiverStructureSnapshot.MaxZ)
                failure = ReceiverReferenceFailure.Unsupported;
        // Allocate/copy ONLY after complete per-cell validity and global caps.
        var copy = failure == ReceiverReferenceFailure.None ? covers.ToArray() : Array.Empty<ReceiverCover>();
        _slots[slot] = _cells.Count + 1;
        _cells.Add(new(tile, copy, loaded, failure));
        _covers += copy.Length;
        return failure;
    }
    internal void Seal() => _sealed = true;
    internal int Cells => _cells.Count;
    internal int CoverRecords => _covers;

    internal bool TryCollectSun(ReceiverFrameKey key, N3 start, N2 shadowDirection,
        float nativeLength, Span<ReceiverCover> destination, out ReceiverReferenceReceipt receipt)
    {
        receipt = new(start, default, 0, 0, 0, 0, ReceiverReferenceFailure.Domain, default);
        return nativeLength >= .8f && nativeLength <= 4
            && ReceiverBillboard.TrySunSource(shadowDirection, nativeLength, out var source)
            && TryCollect(key, start, source, destination, out receipt);
    }

    internal bool TryCollect(ReceiverFrameKey key, N3 start, ReceiverSource source,
        Span<ReceiverCover> destination, out ReceiverReferenceReceipt receipt)
    {
        receipt = new(start, default, 0, 0, 0, 0, ReceiverReferenceFailure.Domain, default);
        if (!float.IsFinite(start.Z) || start.Z < -128 || start.Z > 382
            || (source.Kind == ReceiverSourceKind.Sun && source.SkyZ != 512)
            || (source.Kind == ReceiverSourceKind.Point && source.Value.Z > 512)
            || !ReceiverGeometry.TrySourceEnd(start, source, out var end)) return false;
        // Preserve the EXACT source-bound binary32 endpoint; do not snap its Z.
        receipt = receipt with { End = end };
        var fail = !_sealed ? ReceiverReferenceFailure.Unsealed : key != _key
            ? ReceiverReferenceFailure.Key : _queries >= QueryCap ? ReceiverReferenceFailure.Queries
            : ReceiverReferenceFailure.None;
        if (fail != ReceiverReferenceFailure.None) { receipt = receipt with { Failure = fail }; return false; }
        _queries++;
        if (!Walk(new(start.X, start.Y), new(end.X, end.Y), out var tiles, out fail))
        { receipt = receipt with { Failure = fail }; return false; }
        receipt = receipt with { Required = tiles.Length };
        if (!Fits(_references, tiles.Length, ReferenceCap))
        { receipt = receipt with { Failure = ReceiverReferenceFailure.References }; return false; }
        var resolved = new Cell[tiles.Length]; // explicit diagnostic allocation
        int count = 0, known = 0, loaded = 0;
        ReceiverReferenceTile first = default;
        fail = ReceiverReferenceFailure.None;
        for (int i = 0; i < tiles.Length; i++)
        {
            int at = Find(tiles[i], out _);
            var issue = at == -2 ? ReceiverReferenceFailure.HashProbes
                : at < 0 ? ReceiverReferenceFailure.Missing : _cells[at].Failure;
            if (at >= 0)
            {
                resolved[i] = _cells[at];
                if (resolved[i].Loaded) loaded++;
                if (issue == ReceiverReferenceFailure.None) { known++; count += resolved[i].Covers.Length; }
            }
            if (issue != ReceiverReferenceFailure.None && fail == ReceiverReferenceFailure.None) { fail = issue; first = tiles[i]; }
        }
        if (fail == ReceiverReferenceFailure.None && count > CorridorCoverCap) fail = ReceiverReferenceFailure.Corridor;
        if (fail == ReceiverReferenceFailure.None && count > destination.Length) fail = ReceiverReferenceFailure.Destination;
        receipt = new(start, end, tiles.Length, known, loaded, count, fail, first);
        if (fail != ReceiverReferenceFailure.None) return false;
        int offset = 0;
        foreach (var cell in resolved) { cell.Covers.AsSpan().CopyTo(destination.Slice(offset)); offset += cell.Covers.Length; }
        _references += tiles.Length;
        receipt = receipt with { Copied = count };
        return true;
    }

    // Exact binary32 integer coordinates, diagnostic BigInteger heap work.
    internal static readonly BigInteger Unit = BigInteger.One << 149;
    internal static BigInteger Units(float value)
    {
        uint bits = (uint)BitConverter.SingleToInt32Bits(value), exponent = (bits >> 23) & 255;
        BigInteger n = bits & 0x7fffff;
        if (exponent != 0) n = (n + 0x800000) << (int)(exponent - 1);
        return (bits & 0x80000000) == 0 ? n : -n;
    }
    internal static int Floor(BigInteger n)
    {
        BigInteger q = BigInteger.DivRem(n, Unit, out var r);
        return (int)(r.Sign < 0 ? q - 1 : q);
    }

    internal static bool Walk(N2 a, N2 b, out ReceiverReferenceTile[] tiles, out ReceiverReferenceFailure failure)
    {
        tiles = Array.Empty<ReceiverReferenceTile>(); failure = ReceiverReferenceFailure.Domain;
        if (!float.IsFinite(a.X) || !float.IsFinite(a.Y) || !float.IsFinite(b.X) || !float.IsFinite(b.Y)
            || MathF.Abs(a.X) > 65536 || MathF.Abs(a.Y) > 65536 || MathF.Abs(b.X) > 65536 || MathF.Abs(b.Y) > 65536) return false;
        BigInteger ax = Units(a.X), ay = Units(a.Y), bx = Units(b.X), by = Units(b.Y), dx = bx - ax, dy = by - ay;
        if (dx * dx + dy * dy > 330 * 330 * Unit * Unit) return false;
        var cells = new SortedSet<ReceiverReferenceTile>(); // reference-only allocations, no dedup hash collisions
        var issue = ReceiverReferenceFailure.None;
        bool Add(int x, int y)
        {
            var p = new ReceiverReferenceTile(x, y);
            if (cells.Contains(p)) return true;
            if (!Fits(cells.Count, 1, CorridorCap)) { issue = ReceiverReferenceFailure.Corridor; return false; }
            cells.Add(p); return true;
        }
        bool Touch(BigInteger x, BigInteger y)
        {
            int fx = Floor(x), fy = Floor(y);
            bool ix = x % Unit == 0, iy = y % Unit == 0;
            return Add(fx, fy) && (!ix || Add(fx - 1, fy)) && (!iy || Add(fx, fy - 1))
                && (!ix || !iy || Add(fx - 1, fy - 1));
        }
        bool boundaryX = dx.IsZero && ax % Unit == 0, boundaryY = dy.IsZero && ay % Unit == 0;
        bool Emit(int x, int y) => Add(x, y) && (!boundaryX || Add(x - 1, y))
            && (!boundaryY || Add(x, y - 1)) && (!boundaryX || !boundaryY || Add(x - 1, y - 1));
        if (!Touch(ax, ay)) { failure = issue; return false; }
        int sx = dx.Sign, sy = dy.Sign, cx = Floor(ax) - (ax % Unit == 0 && sx < 0 ? 1 : 0),
            cy = Floor(ay) - (ay % Unit == 0 && sy < 0 ? 1 : 0);
        BigInteger absX = BigInteger.Abs(dx), absY = BigInteger.Abs(dy);
        BigInteger nx = BigInteger.Abs((sx > 0 ? cx + 1 : cx) * Unit - ax),
            ny = BigInteger.Abs((sy > 0 ? cy + 1 : cy) * Unit - ay);
        int events = 0;
        while (true)
        {
            bool hitX = sx != 0 && nx < absX, hitY = sy != 0 && ny < absY;
            if (!hitX && !hitY) break;
            if (++events > EventCap) { failure = ReceiverReferenceFailure.Events; return false; }
            int compare = !hitX ? 1 : !hitY ? -1 : (nx * absY).CompareTo(ny * absX);
            bool ok = compare < 0 ? Emit(cx + sx, cy) : compare > 0 ? Emit(cx, cy + sy)
                : Emit(cx + sx, cy) && Emit(cx, cy + sy) && Emit(cx + sx, cy + sy);
            if (!ok) { failure = issue; return false; }
            if (compare <= 0) { cx += sx; nx += Unit; }
            if (compare >= 0) { cy += sy; ny += Unit; }
        }
        if (!Touch(bx, by)) { failure = issue; return false; }
        tiles = new ReceiverReferenceTile[cells.Count]; cells.CopyTo(tiles);
        failure = ReceiverReferenceFailure.None; return true;
    }
}
#endif
