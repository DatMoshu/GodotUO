// SPDX-License-Identifier: BSD-2-Clause
#if GUO_LABS
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace GUO.Labs.GpuRenderer;

/// <summary>
/// Resident packets addressed by immutable source-revision IDs when available,
/// with exact content lookup for uncached dynamic draws. GPU slots remain private.
/// </summary>
internal sealed class OrderedWorldResidency
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct Quad : IEquatable<Quad>
    {
        internal OrderedWorldDevice.Vertex A, B, C, D;
        public readonly bool Equals(Quad other)
        {
            var self = this;
            return MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref self, 1))
                .SequenceEqual(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref other, 1)));
        }
        public override readonly bool Equals(object obj) => obj is Quad q && Equals(q);
        // Hash narrows candidates only. Exact bytes distinguish every corner,
        // normal, hue, alpha, diagonal, signed zero and NaN payload.
        public override readonly int GetHashCode() => HashCode.Combine(A.PositionUv, A.PackedHue, A.Normal, D.PositionUv);
    }

    private readonly int _limit;
    private readonly Dictionary<Quad, int> _lookup = new();
    private readonly Dictionary<ulong, int> _records = new();
    private ulong[] _slotRecords;
    private Quad[] _slots;
    private bool[] _dirty;
    private long[] _pinned;
    private uint[] _order = Array.Empty<uint>(), _appliedOrder = Array.Empty<uint>();
    private int _count, _orderCount, _appliedOrderCount, _evictionCursor;
    private long _frame;
    private bool _orderValid, _prepared;
    internal long Evictions { get; private set; }
    internal long ContentComparisons { get; private set; }
    internal int Count => _count;
    internal ReadOnlySpan<Quad> Slots => _slots.AsSpan(0, _count);
    internal ReadOnlySpan<uint> Order => _order.AsSpan(0, _orderCount);
    internal bool OrderDirty => !_orderValid || _orderCount != _appliedOrderCount
        || !Order.SequenceEqual(_appliedOrder.AsSpan(0, _appliedOrderCount));

    internal OrderedWorldResidency(int limit = 65536)
    {
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
        _limit = limit;
        int capacity = Math.Min(limit, 64);
        _slots = new Quad[capacity]; _dirty = new bool[capacity]; _pinned = new long[capacity];
        _slotRecords = new ulong[capacity];
    }

    internal void Prepare(ReadOnlySpan<OrderedWorldDevice.Vertex> vertices, ReadOnlySpan<ulong> records = default)
    {
        if (vertices.Length % 4 != 0) throw new ArgumentException("Incomplete quad.");
        ReadOnlySpan<Quad> quads = MemoryMarshal.Cast<OrderedWorldDevice.Vertex, Quad>(vertices);
        if (!records.IsEmpty && records.Length != quads.Length) throw new ArgumentException("Incomplete record IDs.");
        // Common idle frame: validate the previous occurrence-to-slot mapping
        // directly, avoiding dictionary hashes/probes for every unchanged quad.
        // This is CPU-plan validity only; GPU invalidation/dirty bits still apply.
        if (_prepared && quads.Length == _orderCount)
        {
            int i = 0;
            while (i < quads.Length)
            {
                ulong id = records.IsEmpty ? 0 : records[i];
                int slot = (int)_order[i];
                if (id != _slotRecords[slot]) break;
                if (id == 0)
                {
                    ContentComparisons++;
                    if (!quads[i].Equals(_slots[slot])) break;
                }
                i++;
            }
            if (i == quads.Length) return;
        }
        _prepared = false; // A failed rebuild must never reuse a partial plan.
        _frame = checked(_frame + 1);
        _orderCount = quads.Length;
        if (_order.Length < _orderCount) Array.Resize(ref _order, Math.Max(_orderCount, _order.Length * 2));
        // Pin ALL hits before allocating misses: otherwise a miss early in the
        // stream could evict a slot referenced later by this same transaction.
        for (int i = 0; i < quads.Length; i++)
        {
            ulong id = records.IsEmpty ? 0 : records[i];
            if (Find(quads[i], id, out int slot))
            {
                _pinned[slot] = _frame;
                _order[i] = (uint)slot;
            }
            else _order[i] = uint.MaxValue;
        }
        for (int i = 0; i < quads.Length; i++)
        {
            if (_order[i] != uint.MaxValue) continue;
            // Repeated misses become hits after the first occurrence; retain
            // both ordered references, including overlapping translucent cover.
            ulong id = records.IsEmpty ? 0 : records[i];
            if (!Find(quads[i], id, out int slot))
            {
                slot = Allocate();
                _slots[slot] = quads[i];
                _slotRecords[slot] = id;
                if (id == 0) _lookup.Add(quads[i], slot);
                else _records.Add(id, slot);
                _dirty[slot] = true;
            }
            _pinned[slot] = _frame;
            _order[i] = (uint)slot;
        }
        _prepared = true;
    }

    private bool Find(in Quad quad, ulong record, out int slot)
    {
        if (record != 0) return _records.TryGetValue(record, out slot);
        ContentComparisons++;
        return _lookup.TryGetValue(quad, out slot);
    }

    private int Allocate()
    {
        if (_count < _limit)
        {
            if (_count == _slots.Length)
            {
                int capacity = Math.Min(_limit, checked(_count * 2));
                Array.Resize(ref _slots, capacity); Array.Resize(ref _dirty, capacity); Array.Resize(ref _pinned, capacity);
                Array.Resize(ref _slotRecords, capacity);
            }
            return _count++;
        }
        for (int checkedSlots = 0; checkedSlots < _count; checkedSlots++)
        {
            int slot = _evictionCursor;
            _evictionCursor = (_evictionCursor + 1) % _count;
            if (_pinned[slot] == _frame) continue;
            if (_slotRecords[slot] == 0) _lookup.Remove(_slots[slot]);
            else _records.Remove(_slotRecords[slot]);
            Evictions++;
            return slot;
        }
        throw new InvalidOperationException("Resident quad capacity exceeded by this frame; use canvas.");
    }

    internal bool NextDirty(ref int cursor, out int start, out int count)
    {
        while (cursor < _count && !_dirty[cursor]) cursor++;
        start = cursor;
        while (cursor < _count && _dirty[cursor]) cursor++;
        count = cursor - start;
        return count != 0;
    }

    internal void AppliedRange(int start, int count) => Array.Clear(_dirty, start, count);
    internal void AppliedOrder()
    {
        if (_appliedOrder.Length < _orderCount) Array.Resize(ref _appliedOrder, _order.Length);
        Order.CopyTo(_appliedOrder);
        _appliedOrderCount = _orderCount;
        _orderValid = true;
    }
    internal void InvalidateGeometry() => Array.Fill(_dirty, true, 0, _count);
    internal void InvalidateOrder() => _orderValid = false;
    internal void InvalidateGpu() { InvalidateGeometry(); InvalidateOrder(); }
}
#endif
