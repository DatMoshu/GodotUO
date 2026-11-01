// SPDX-License-Identifier: BSD-2-Clause
// DEBUG/OFFLINE REFERENCE ONLY: allocates BigInteger, arrays and trees.
#if DEBUG
using System;
using System.Collections.Generic;
using System.Numerics;
using N2 = System.Numerics.Vector2;
using N3 = System.Numerics.Vector3;

namespace GUO.Renderer.SpeedMode.Shadows;

internal static class ReceiverReferenceProbe
{
    private readonly record struct Fraction(BigInteger N, BigInteger D)
    {
        internal static Fraction Of(BigInteger n, BigInteger d) => d.Sign < 0 ? new(-n, -d) : new(n, d);
        internal int Compare(Fraction b) => (N * b.D).CompareTo(b.N * D);
    }
    // Independent input decoder uses IEEE DOUBLE representation of exact float
    // value; it does not call the event walk's binary32 decoder or floor.
    private static BigInteger OracleUnits(float p)
    {
        long bits = BitConverter.DoubleToInt64Bits(p);
        if ((bits & 0x7fffffffffffffffL) == 0) return 0;
        BigInteger n = ((ulong)bits & 0xfffffffffffffUL) | (1UL << 52);
        int shift = (int)(((ulong)bits >> 52) & 2047) - 926;
        n = shift >= 0 ? n << shift : n >> -shift;
        return bits < 0 ? -n : n;
    }
    private static int OracleFloor(BigInteger p, BigInteger unit)
    {
        var q = BigInteger.DivRem(p, unit, out var remainder);
        return (int)(remainder < 0 ? q - 1 : q);
    }
    private static bool Slab(BigInteger p, BigInteger d, int min, int max,
        BigInteger unit, ref Fraction lo, ref Fraction hi)
    {
        if (d.IsZero) return p >= min * unit && p <= max * unit;
        var a = Fraction.Of(min * unit - p, d); var b = Fraction.Of(max * unit - p, d);
        if (a.Compare(b) > 0) (a, b) = (b, a);
        if (a.Compare(lo) > 0) lo = a;
        if (b.Compare(hi) < 0) hi = b;
        return lo.Compare(hi) <= 0;
    }
    // Separate exact closed-box slab oracle; no event-walk reuse.
    private static SortedSet<ReceiverReferenceTile> Oracle(N2 a, N2 b)
    {
        BigInteger unit = BigInteger.One << 149, ax = OracleUnits(a.X), ay = OracleUnits(a.Y),
            bx = OracleUnits(b.X), by = OracleUnits(b.Y);
        BigInteger minX = BigInteger.Min(ax, bx), minY = BigInteger.Min(ay, by);
        int left = OracleFloor(minX, unit) - (minX % unit == 0 ? 1 : 0),
            top = OracleFloor(minY, unit) - (minY % unit == 0 ? 1 : 0),
            right = OracleFloor(BigInteger.Max(ax, bx), unit), bottom = OracleFloor(BigInteger.Max(ay, by), unit);
        if ((long)(right - left + 1) * (bottom - top + 1) > 111000)
            throw new InvalidOperationException("Reference oracle box cap");
        var found = new SortedSet<ReceiverReferenceTile>();
        for (int y = top; y <= bottom; y++) for (int x = left; x <= right; x++)
        {
            Fraction lo = new(0, 1), hi = new(1, 1);
            if (Slab(ax, bx - ax, x, x + 1, unit, ref lo, ref hi)
                && Slab(ay, by - ay, y, y + 1, unit, ref lo, ref hi)) found.Add(new(x, y));
        }
        return found;
    }

    internal static int Run()
    {
        int checks = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException("DEBUG receiver reference: " + name);
            checks++;
        }
        ReceiverReferenceTile[] Walk(N2 a, N2 b, int count = -1)
        {
            Check(ReceiverReferenceIndex.Walk(a, b, out var cells, out var failure)
                && failure == ReceiverReferenceFailure.None, "walk admission");
            var oracle = Oracle(a, b);
            Check(oracle.SetEquals(cells), "independent slab/set oracle");
            Check(ReceiverReferenceIndex.Walk(b, a, out var reversed, out _) && oracle.SetEquals(reversed), "exact reversal set");
            if (count >= 0) Check(cells.Length == count, "manual closed-cell count");
            return cells;
        }
        Walk(new(.5f,.5f), new(.5f,.5f), 1); Walk(new(1,1), new(1,1), 4);
        Walk(new(.5f,.5f), new(2.5f,.5f), 3); Walk(new(.5f,1), new(2.5f,1), 6);
        Walk(new(1,.5f), new(1,2.5f), 6); Walk(new(.5f,.5f), new(2.5f,2.5f), 7);
        Walk(new(0,0), new(2,2), 10);
        float next = BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(2.5f) + 1);
        Walk(new(.5f,.5f), new(2.5f,next), 5);
        Walk(new(float.Epsilon,float.Epsilon), new(.5f,.5f));
        Walk(new(-.5f,-.5f), new(-2.5f,-2.5f), 7);
        Walk(new(65535,65535), new(65536,65536));
        Check(!ReceiverReferenceIndex.Walk(N2.Zero, new(331,0), out var empty, out var why)
            && empty.Length == 0 && why == ReceiverReferenceFailure.Domain, "never truncate longer ray");
        Check(!ReceiverReferenceIndex.Walk(new(float.NaN,0), N2.Zero, out _, out _), "nonfinite unknown");

        ReceiverFrameKey key = new(1,2,3,10,20,1280,720,Matrix3x2.Identity);
        ReceiverEnumeration complete = new(3,3,0,0,false);
        ReceiverCover[] none = Array.Empty<ReceiverCover>();
        var sentinel = new ReceiverCover(new(99,99,99), new(100,100,100));
        ReceiverReferenceIndex Populate(ReceiverReferenceTile[] cells, int omit = -1, bool unloaded = false)
        {
            var index = new ReceiverReferenceIndex(key);
            for (int i = 0; i < cells.Length; i++)
                if (i != omit || unloaded)
                {
                    var status = index.Put(key, cells[i], complete, !(i == omit && unloaded), none);
                    Check(status == (i == omit && unloaded ? ReceiverReferenceFailure.Unloaded : ReceiverReferenceFailure.None), "supplied-cell receipt");
                }
            index.Seal(); return index;
        }
        // Nine L/Z cases, X/Y/diagonal, four signs, fractional/integer starts.
        // Every declared-loaded cell is synthetic; this is NOT native coverage.
        foreach (float z in new float[] { -128,0,382 }) foreach (float length in new float[] { .8f,1,4 })
        foreach (int sx in new[] { -1,1 }) foreach (int sy in new[] { -1,1 })
        foreach (int direction in new[] { 0,1,2 }) foreach (var origin in new[] { new N2(.5f,.5f), new N2(4,2) })
        {
            N2 shadow = direction == 0 ? new(sx,0) : direction == 1 ? new(0,sy) : new(sx,sy);
            N3 start = new(origin,z);
            Check(ReceiverBillboard.TrySunSource(shadow, length, out var source)
                && ReceiverGeometry.TrySourceEnd(start, source, out _), "v3 source endpoint");
            ReceiverGeometry.TrySourceEnd(start, source, out var end);
            var cells = Walk(origin, new(end.X,end.Y));
            var index = Populate(cells);
            Check(index.TryCollectSun(key, start, shadow, length, Span<ReceiverCover>.Empty, out var receipt)
                && receipt.Start == start && receipt.End == end && receipt.Known == cells.Length
                && receipt.Loaded == cells.Length && receipt.Copied == 0, "full source unchanged and complete-empty positive");
            // One sparse missing cell invalidates the ENTIRE point query.
            var missing = Populate(cells, cells.Length / 2);
            ReceiverCover[] target = { sentinel };
            Check(!missing.TryCollectSun(key, start, shadow, length, target, out receipt)
                && receipt.Failure == ReceiverReferenceFailure.Missing && receipt.Copied == 0
                && receipt.Known == cells.Length - 1 && target[0] == sentinel
                && receipt.End == end, "missing coverage unknown, not shortened");
        }

        var small = Walk(new(.5f,.5f), new(2.5f,.5f));
        var unloadedIndex = Populate(small, 1, true);
        var point = new ReceiverSource(ReceiverSourceKind.Point, new(2.5f,.5f,100));
        ReceiverCover[] destination = { sentinel };
        Check(!unloadedIndex.TryCollect(key, new(.5f,.5f,0), point, destination, out var r)
            && r.Failure == ReceiverReferenceFailure.Unloaded && r.Loaded == 2 && r.Copied == 0
            && destination[0] == sentinel, "unloaded middle cell diagnostic");
        var valid = Populate(Walk(new(.5f,.5f), new(.5f,.5f)));
        foreach (var wrong in new[] { key with { Frame = 9 }, key with { Scene = 9 }, key with { Topology = 9 },
            key with { SurfaceX = 11 }, key with { Width = 1 }, key with { View = Matrix3x2.CreateTranslation(1,0) } })
            Check(!valid.TryCollect(wrong, new(.5f,.5f,0), point, destination, out r)
                && r.Failure == ReceiverReferenceFailure.Key && r.Copied == 0 && destination[0] == sentinel, "exact key mismatch");
        var vertical = new ReceiverSource(ReceiverSourceKind.Point, new(.5f,.5f,100));
        for (int i = 0; i < 64; i++) Check(valid.TryCollect(key, new(.5f,.5f,0), vertical, Span<ReceiverCover>.Empty, out _), "64query capacity");
        Check(!valid.TryCollect(key, new(.5f,.5f,0), vertical, destination, out r)
            && r.Failure == ReceiverReferenceFailure.Queries && destination[0] == sentinel, "65th query unknown");

        ReceiverCover[] Covers(ReceiverReferenceTile tile, int count)
        {
            var result = new ReceiverCover[count];
            for (int i = 0; i < count; i++) result[i] = new(new(tile.X+.25f,tile.Y+.25f,i+1), new(tile.X+.75f,tile.Y+.75f,i+1));
            return result;
        }
        var capacity = new ReceiverReferenceIndex(key);
        int candidate = 0;
        while (capacity.Cells < 8192 && candidate < 32768)
        {
            var status = capacity.Put(key, new(candidate & 1023,candidate >> 10), complete, true, none); candidate++;
            Check(status is ReceiverReferenceFailure.None or ReceiverReferenceFailure.HashProbes, "global cell fill");
        }
        Check(capacity.Cells == 8192, "exact8192 headers");
        bool cellOverflow = false;
        for (int i = 0; i < 256 && !cellOverflow; i++)
        {
            var status = capacity.Put(key, new(30000+i,30000), complete, true, none);
            Check(status is ReceiverReferenceFailure.GlobalCells or ReceiverReferenceFailure.HashProbes, "full header destination unchanged");
            cellOverflow = status == ReceiverReferenceFailure.GlobalCells;
        }
        Check(cellOverflow && capacity.Cells == 8192, "8193rd exact header-cap cause");
        var coverCapacity = new ReceiverReferenceIndex(key);
        candidate = 0;
        while (coverCapacity.CoverRecords < 32768 && candidate < 16384)
        {
            var tile = new ReceiverReferenceTile(candidate & 1023,candidate >> 10); candidate++;
            var status = coverCapacity.Put(key, tile, new(3,3,4,4,false), true, Covers(tile,8));
            Check(status is ReceiverReferenceFailure.None or ReceiverReferenceFailure.HashProbes, "global cover fill");
        }
        Check(coverCapacity.CoverRecords == 32768, "exact32768 covers");
        bool coverOverflow = false;
        for (int i = 0; i < 256 && !coverOverflow; i++)
        {
            var tile = new ReceiverReferenceTile(30000+i,30000);
            var status = coverCapacity.Put(key, tile, new(3,3,1,1,false), true, Covers(tile,1));
            Check(status is ReceiverReferenceFailure.GlobalCovers or ReceiverReferenceFailure.HashProbes, "full cover destination unchanged");
            coverOverflow = status == ReceiverReferenceFailure.GlobalCovers;
        }
        Check(coverOverflow && coverCapacity.CoverRecords == 32768, "32769th exact cover-cap cause");
        var collisions = new ReceiverReferenceIndex(key); int found = 0;
        for (int n = 0; n < 1000000 && found < 17; n++)
        {
            var tile = new ReceiverReferenceTile(n & 1023,n >> 10);
            if (ReceiverReferenceIndex.Slot(tile) != 0) continue;
            var status = collisions.Put(key,tile,complete,true,none);
            Check(status == (found < 16 ? ReceiverReferenceFailure.None : ReceiverReferenceFailure.HashProbes), "exact16/17 probe cap"); found++;
        }
        Check(found == 17, "collision fixture complete");
        foreach (int cap in new[] { 1024,8192,65536,int.MaxValue })
            Check(ReceiverReferenceIndex.Fits(cap-1,1,cap) && !ReceiverReferenceIndex.Fits(cap,1,cap), "capacity primitive exact/overflow");

        foreach (var scan in new[] { new ReceiverEnumeration(3,4,0,0,false), new(3,3,0,1,false), new(3,3,65,65,false), new(3,3,0,0,true) })
        {
            var index = new ReceiverReferenceIndex(key);
            Check(index.Put(key,new(0,0),scan,true,none) != ReceiverReferenceFailure.None, "unsupported/incomplete source unknown");
            index.Seal();
            Check(!index.TryCollect(key,new(.5f,.5f,0),vertical,destination,out r) && r.Copied == 0 && destination[0] == sentinel, "invalid receipt blocks query");
        }
        var tooMany = new ReceiverReferenceIndex(key);
        Check(tooMany.Put(key,new(0,0),new(3,3,5,5,false),true,Covers(new(0,0),9)) == ReceiverReferenceFailure.CellOverflow, "ninth cell cover");
        var physical = new ReceiverReferenceIndex(key);
        ReceiverCover roof = new(new(.25f,.25f,30),new(.75f,.75f,32));
        ReceiverCover[] original = { roof };
        Check(physical.Put(key,new(0,0),new(3,3,1,1,false),true,original) == ReceiverReferenceFailure.None, "same physical cover record");
        original[0] = default; physical.Seal();
        Check(!physical.TryCollect(key,new(.5f,.5f,0),vertical,Span<ReceiverCover>.Empty,out r) && r.Failure == ReceiverReferenceFailure.Destination, "destination preflight");
        Check(physical.TryCollect(key,new(.5f,.5f,0),vertical,destination,out r) && r.Copied == 1 && destination[0] == roof
            && ReceiverGeometry.TrySheltered(new(.5f,.5f,0),100,destination,true,out bool sheltered) && sheltered, "immutable copy feeds same vertical query");
        Check(physical.Put(key,new(0,0),complete,true,none) == ReceiverReferenceFailure.Sealed, "sealed reference immutable");
        return checks;
    }
}
#endif
