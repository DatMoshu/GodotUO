// SPDX-License-Identifier: BSD-2-Clause
// Diagnostic-only complete Capture timings, separate from whole-mode rendering.
#if DEBUG
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using N3 = System.Numerics.Vector3;

namespace GUO.Renderer.SpeedMode.PhysicalWorld;

public partial class NativePhysicalWorldProbe
{
    private static object PerfSnapshotDigest(PhysicalWorldSnapshot snapshot)
    {
        // Canonical little-endian values, never CLR struct padding. Array order
        // is intentional: this detects both geometry changes and reordering.
        // No world identity, frame generation, diagnostic timing or source hash
        // enters these digests; SourceFingerprint is reported separately.
        using var triangles = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var cells = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> record = stackalloc byte[53];
        BinaryPrimitives.WriteInt32LittleEndian(record, snapshot.Triangles.Length);
        triangles.AppendData(record[..4]);
        static void Point(Span<byte> target, N3 p)
        {
            BinaryPrimitives.WriteInt32LittleEndian(target, BitConverter.SingleToInt32Bits(p.X));
            BinaryPrimitives.WriteInt32LittleEndian(target[4..], BitConverter.SingleToInt32Bits(p.Y));
            BinaryPrimitives.WriteInt32LittleEndian(target[8..], BitConverter.SingleToInt32Bits(p.Z));
        }
        foreach (var triangle in snapshot.Triangles.Span) {
            Point(record, triangle.A); Point(record[12..], triangle.B); Point(record[24..], triangle.C);
            record[36] = (byte)triangle.Owner.Kind;
            BinaryPrimitives.WriteUInt64LittleEndian(record[37..], triangle.Owner.Identity);
            BinaryPrimitives.WriteUInt32LittleEndian(record[45..], triangle.Owner.Component);
            record[49] = (byte)triangle.Material; record[50] = (byte)triangle.Provenance;
            BinaryPrimitives.WriteUInt16LittleEndian(record[51..], triangle.Graphic);
            triangles.AppendData(record);
        }
        BinaryPrimitives.WriteInt32LittleEndian(record, snapshot.UnknownCells.Length);
        cells.AppendData(record[..4]);
        foreach (var reason in snapshot.UnknownCells.Span) {
            BinaryPrimitives.WriteUInt16LittleEndian(record, (ushort)reason);
            cells.AppendData(record[..2]);
        }
        byte[] triangleHash = triangles.GetHashAndReset(), cellHash = cells.GetHashAndReset();
        using var combined = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        BinaryPrimitives.WriteInt32LittleEndian(record, 1); // digest schema
        BinaryPrimitives.WriteInt32LittleEndian(record[4..], snapshot.Bounds.X);
        BinaryPrimitives.WriteInt32LittleEndian(record[8..], snapshot.Bounds.Y);
        BinaryPrimitives.WriteInt32LittleEndian(record[12..], snapshot.Bounds.Width);
        BinaryPrimitives.WriteInt32LittleEndian(record[16..], snapshot.Bounds.Height);
        BinaryPrimitives.WriteInt32LittleEndian(record[20..], snapshot.Overflow ? 1 : 0);
        combined.AppendData(record[..24]); combined.AppendData(triangleHash); combined.AppendData(cellHash);
        return new { schema = 1, triangleSha256 = Convert.ToHexString(triangleHash),
            unknownCellsSha256 = Convert.ToHexString(cellHash), snapshotSha256 = Convert.ToHexString(combined.GetHashAndReset()) };
    }

    private PhysicalWorldSnapshot TimedPerfCapture(NativePhysicalWorldCache cache, PhysicalSnapshotKey key,
        PhysicalRect bounds, out double milliseconds, out long allocatedBytes)
    {
        var world = _game.UO.World;
        long allocated = GC.GetAllocatedBytesForCurrentThread(), started = Stopwatch.GetTimestamp();
        var snapshot = cache.Capture(world, key, bounds);
        long finished = Stopwatch.GetTimestamp(), after = GC.GetAllocatedBytesForCurrentThread();
        milliseconds = (finished - started) * 1000d / Stopwatch.Frequency;
        allocatedBytes = after - allocated;
        return snapshot;
    }

    private void RunPerfCacheDiagnostics()
    {
        const int coldCount = 12, hitCount = 120, mutationCount = 12;
        var active = Shadows.PhysicalWorld;
        Require(active != null, "direct cache diagnostic has active native source bounds");
        var bounds = active.Bounds; var originalFrame = FrozenState();
        ulong generation = active.Key.Generation + 1000;
        PhysicalSnapshotKey NextKey() => new(active.Key.World, generation++, active.Key.Facet);
        var rows = new List<object>(coldCount + hitCount + mutationCount); var summary = new List<object>(3);
        int[] gcBefore = { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
        _perfCacheDiagnostics = new { partial = true, rows };
        // Use an actual admitted native wall. All mutation/relinking happens
        // before the Capture stopwatch; nothing renders during these samples.
        ushort graphic = FindReceiverWall(true);
        var wall = Place(graphic, X - 2, Y, _z);
        uint serial = wall.Serial;
        PhysicalTriangle[] Owner(PhysicalWorldSnapshot snapshot)
        {
            var triangles = new List<PhysicalTriangle>(4);
            foreach (var triangle in snapshot.Triangles.Span)
                if (triangle.Owner.Kind == PhysicalSourceKind.Item && triangle.Owner.Identity == serial) triangles.Add(triangle);
            return triangles.ToArray();
        }
        void AddRow(string phase, int sample, double ms, long allocated, PhysicalWorldSnapshot snapshot,
            NativePhysicalWorldCache cache) => rows.Add(new { phase, sample, fullCaptureMilliseconds = ms,
                mainThreadAllocatedBytes = allocated, snapshot.Key.Generation, snapshot.SourceFingerprint,
                triangles = snapshot.Triangles.Length, diagnostics = snapshot.Diagnostics,
                bounds = new { snapshot.Bounds.X, snapshot.Bounds.Y, snapshot.Bounds.Width, snapshot.Bounds.Height },
                wallX = wall.X, wallY = wall.Y, wallZ = wall.Z,
                unknownCellCount = snapshot.UnknownCells.Length - snapshot.CompleteCellCount,
                snapshot.Overflow, exactSnapshot = PerfSnapshotDigest(snapshot),
                cache.CacheHits, cache.CacheBuilds });
        void AddSummary(string phase, double[] ms, double[] bytes) => summary.Add(new {
            phase, fullCaptureMilliseconds = Distribution(ms), mainThreadAllocatedBytes = Distribution(bytes) });
        try {
            var coldMs = new double[coldCount]; var coldBytes = new double[coldCount];
            for (int i = 0; i < coldCount; i++) {
                var fresh = new NativePhysicalWorldCache();
                var next = TimedPerfCapture(fresh, NextKey(), bounds, out coldMs[i], out long bytes);
                coldBytes[i] = bytes;
                Require(fresh.CacheBuilds == 1 && fresh.CacheHits == 0 && Owner(next).Length > 0,
                    "new cache first capture builds actual wall geometry");
                AddRow("cache-cold/shared-art-and-native-file-caches-warm", i, coldMs[i], bytes, next, fresh);
            }
            AddSummary("cache-cold/shared-art-and-native-file-caches-warm", coldMs, coldBytes);

            var cache = new NativePhysicalWorldCache();
            var first = cache.Capture(_game.UO.World, NextKey(), bounds);
            var retainedTriangles = first.Triangles.ToArray(); var retainedKey = first.Key; ulong retainedFingerprint = first.SourceFingerprint;
            var hitMs = new double[hitCount]; var hitBytes = new double[hitCount];
            long builds = cache.CacheBuilds, hits = cache.CacheHits;
            for (int i = 0; i < hitCount; i++) {
                var next = TimedPerfCapture(cache, NextKey(), bounds, out hitMs[i], out long bytes); hitBytes[i] = bytes;
                Require(ReferenceEquals(first, next), "unchanged capture returns exact immutable snapshot despite requested generation");
                AddRow("unchanged-cache-hit", i, hitMs[i], bytes, next, cache);
            }
            Require(cache.CacheBuilds == builds && cache.CacheHits - hits == hitCount, "all direct unchanged captures are hits");
            AddSummary("unchanged-cache-hit", hitMs, hitBytes);

            var mutationMs = new double[mutationCount]; var mutationBytes = new double[mutationCount];
            var previous = first; var previousOwner = Owner(first);
            int previousY = Y; int clearOldCorridors = 0, unknownOldCorridors = 0;
            builds = cache.CacheBuilds; hits = cache.CacheHits;
            for (int i = 0; i < mutationCount; i++) {
                int currentY = (i & 1) == 0 ? Y + 2 : Y;
                wall.SetInWorldTile((ushort)(X - 2), (ushort)currentY, (sbyte)_z);
                var next = TimedPerfCapture(cache, NextKey(), bounds, out mutationMs[i], out long bytes); mutationBytes[i] = bytes;
                var currentOwner = Owner(next); var delta = new N3(0, currentY - previousY, 0);
                Require(!ReferenceEquals(previous, next) && next.Key.Generation > previous.Key.Generation
                    && next.SourceFingerprint != previous.SourceFingerprint, "each live wall move invalidates capture immediately");
                Require(currentOwner.Length == previousOwner.Length && currentOwner.Length > 0,
                    "moved owner preserves its emitted triangle count");
                for (int t = 0; t < currentOwner.Length; t++)
                    Require(currentOwner[t] == (previousOwner[t] with {
                        A = previousOwner[t].A + delta, B = previousOwner[t].B + delta, C = previousOwner[t].C + delta }),
                        "owner geometry exists only at its current translated placement");
                Require(first.Key == retainedKey && first.SourceFingerprint == retainedFingerprint
                    && first.Triangles.Span.SequenceEqual(retainedTriangles), "retained pre-mutation snapshot remains immutable");
                // Independent short ray across an emitted face. Unknown old
                // corridors remain unknown; they are not called certified clear.
                var face = currentOwner[0]; N3 centre = (face.A + face.B + face.C) / 3;
                N3 normal = N3.Normalize(N3.Cross(face.B - face.A, face.C - face.A));
                var blocked = PhysicalWorldOracle.Trace(next, centre - normal * .2f, centre + normal * .2f, PhysicalMaterial.BlocksLocalLight);
                Require(blocked.Blocked && blocked.Owner.Identity == serial && blocked.Owner.Kind == PhysicalSourceKind.Item,
                    "independent ray hits moved wall at its current position");
                var old = PhysicalWorldOracle.Trace(next, centre - delta - normal * .2f, centre - delta + normal * .2f,
                    PhysicalMaterial.BlocksLocalLight);
                Require(!old.Blocked || old.Owner.Kind != PhysicalSourceKind.Item || old.Owner.Identity != serial,
                    "moved wall does not remain at its previous placement");
                if (old.Known && !old.Blocked) clearOldCorridors++; else if (!old.Known) unknownOldCorridors++;
                AddRow("live-wall-mutation-rebuild", i, mutationMs[i], bytes, next, cache);
                previous = next; previousOwner = currentOwner; previousY = currentY;
            }
            Require(cache.CacheBuilds - builds == mutationCount && cache.CacheHits == hits, "all direct mutations rebuild without stale hits");
            AddSummary("live-wall-mutation-rebuild", mutationMs, mutationBytes);
            // Twelve alternating changes return the actual wall to its start.
            Require(wall.Y == Y && previous.SourceFingerprint == retainedFingerprint, "wall restoration returns original source fingerprint");
            int[] gcAfter = { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
            _perfCacheDiagnostics = new { partial = false, counts = new { coldCount, hitCount, mutationCount },
                scope = "Direct game-thread NativePhysicalWorldCache.Capture from entry through return: signature, producer, Seal/copies and cache acceptance. No draw/GPU/readback/await/mutation/check/serialization inside the timed call.",
                coldMeaning = "New cache instance, shared native file/art/recipe caches warm; cache construction excluded. Not cold disk, startup or whole-frame timing.",
                smallSampleLimit = "Only12 cold and12 mutation samples. Nearest-rank p95/p99 therefore equal the observed maximum; no tail-latency estimate is implied. First diagnostic method call may include JIT effects.",
                allocationMeaning = "Current-thread managed bytes allocated by Capture. Validation/fixture overhead occurs between calls and can affect later GC; no forced collection.",
                bounds = new { bounds.X, bounds.Y, bounds.Width, bounds.Height }, wallGraphic = graphic, wallSerial = serial,
                positions = new[] { new { x = X - 2, y = Y, z = _z }, new { x = X - 2, y = Y + 2, z = _z } },
                clearOldCorridors, unknownOldCorridors,
                collectionCountDelta = gcAfter.Zip(gcBefore, (after, before) => after - before).ToArray(), summary, rows };
        } finally { wall.Destroy(); }
        Require(FrozenState() == originalFrame, "direct cache diagnostic preserves clock/camera/player");
    }
}
#endif
