// SPDX-License-Identifier: BSD-2-Clause
using System;
using GUO.Game.Scenes;

namespace GUO.Host;

public partial class BatcherProbe
{
    // Parent registers this once in BatcherProbe; no renderer, shard or art needed.
    private void VerifyWorldDepthSort()
    {
        uint[] edgeBits =
        {
            0, 0x80000000, 1, 0x80000001, 0x007fffff, 0x807fffff,
            0x00800000, 0x80800000, 0x3f7fffff, 0x3f800000, 0x3f800001,
            0xbf7fffff, 0xbf800000, 0xbf800001, 0x7f7fffff, 0xff7fffff,
            0x7f800000, 0xff800000, 0x7f800001, 0xff800001,
            0x7fbfffff, 0xffbfffff, 0x7fc00000, 0xffc00000,
            0x7fffffff, 0xffffffff,
        };
        const int guard = 7;
        ulong[] scratch = null;

        void Oracle(string name, ulong[] keys, int count)
        {
            ulong[] expected = (ulong[])keys.Clone();
            Array.Sort(expected, 0, count);
            WorldDepthSort.SortQueueKeys(keys, count, ref scratch);
            CheckThat("world radix exact ulong oracle " + name,
                keys.AsSpan().SequenceEqual(expected), "entire array incl. poisoned inactive tail");
            CheckThat("world radix bounded scratch " + name,
                scratch == null || scratch.Length <= WorldDepthSort.MaxRadixCount,
                "at most 65536 ulongs, no oversized-stream growth");
        }

        ulong[] Make(int count, Func<int, uint> upper)
        {
            var keys = new ulong[count + guard];
            for (int i = 0; i < count; i++) keys[i] = ((ulong)upper(i) << 32) | (uint)i;
            for (int i = count; i < keys.Length; i++) keys[i] = ulong.MaxValue - (ulong)i;
            return keys;
        }

        // Empty/small UI-like world queues must leave even the scratch reference null.
        foreach (int count in new[] { 0, 1, 2, 127, 1023 })
        {
            Oracle("small " + count, Make(count, i => (uint)(count - i)), count);
            CheckThat("world radix small remains lazy " + count, scratch == null, "Array.Sort fallback");
        }
        // An oversized first frame also must not create the capped scratch.
        int over = WorldDepthSort.MaxRadixCount + 1;
        Oracle("oversized first", Make(over, i => (uint)(over - i)), over);
        CheckThat("world radix oversized remains lazy", scratch == null, "Array.Sort fallback");

        Oracle("ordered without scratch", Make(4096, i => (uint)i), 4096);
        Oracle("equal without scratch", Make(4096, _ => 0x87654321), 4096);
        CheckThat("world radix ordered queues remain lazy", scratch == null,
            "already ordered and equal-depth early exits need no scratch");
        Oracle("last-key inversion", Make(4096, i => i == 4095 ? 0u : (uint)i), 4096);

        // Exactly one/two/three/four varying upper bytes exercise skipped passes
        // and both final-buffer parities. Equal depths still need ascending Seq.
        for (int subset = 1; subset < 16; subset++)
        {
            uint mask = 0;
            for (int b = 0; b < 4; b++)
                if ((subset & (1 << b)) != 0) mask |= 0xffu << (8 * b);
            Oracle("varying byte mask " + mask,
                Make(1025, i => (uint)(0x91e5732b * (uint)(1025 - i)) & mask), 1025);
        }

        foreach (int count in new[] { 128, 129, 255, 256, 257, 1024, 1025, 8192, 65535, 65536 })
        {
            Oracle("same depth " + count, Make(count, _ => 0x80000000), count);
            Oracle("ascending " + count, Make(count, i => (uint)i), count);
            Oracle("descending " + count, Make(count, i => uint.MaxValue - (uint)i), count);
            // Use actual DepthKey.Of, including both zeros, signed subnormals,
            // adjacent normal floats, infinities and NaN signs/payloads.
            var edges = new ulong[count + guard];
            for (int i = 0; i < count; i++)
                edges[i] = DepthKey.Of(BitConverter.UInt32BitsToSingle(edgeBits[i % edgeBits.Length]), i);
            for (int i = count; i < edges.Length; i++) edges[i] = ulong.MaxValue;
            Oracle("float bit edges " + count, edges, count);
        }

        // Every byte can matter independently; this catches 2/3-byte shortcuts.
        for (int shift = 0; shift < 32; shift += 8)
        {
            int byteShift = shift;
            Oracle("each upper byte " + shift,
                Make(1024, i => (uint)(255 - (i % 256)) << byteShift), 1024);
        }
        uint[] carries = { 0, 0xff, 0x100, 0xffff, 0x10000, 0xffffff, 0x1000000,
            0x7fffffff, 0x80000000, 0xfffffffe, uint.MaxValue };
        Oracle("byte carry boundaries", Make(4096, i => carries[i % carries.Length]), 4096);

        // Deterministic full 32-bit transformed-depth domain, not just game-range floats.
        uint random = 0x5a17c9e3;
        for (int fixture = 0; fixture < 16; fixture++)
        {
            int count = 128 + fixture * 257;
            var keys = new ulong[count + guard];
            for (int i = 0; i < count; i++)
            {
                random ^= random << 13; random ^= random >> 17; random ^= random << 5;
                keys[i] = ((ulong)random << 32) | (uint)i;
            }
            for (int i = count; i < keys.Length; i++) keys[i] = ulong.MaxValue;
            Oracle("full-domain seed " + fixture, keys, count);
        }

        // Reuse after a high-water frame, oversized fallback, and empty/shrunk streams.
        ulong[] retained = scratch;
        Oracle("oversized after warmup", Make(over, i => (uint)(over - i)), over);
        Oracle("shrink", Make(128, i => (uint)(i % 3)), 128);
        Oracle("empty after warmup", Make(0, _ => 0), 0);
        CheckThat("world radix scratch reused through fallback and shrink",
            ReferenceEquals(retained, scratch), "bounded high-water array retained by owner");

        var repeat = Make(1024, i => (uint)(1024 - i));
        // Warm the production method before observing allocations. Restore fresh
        // queue-order input each time: sorting the already sorted keys violates its contract.
        for (int pass = 0; pass < 8; pass++)
        {
            for (int i = 0; i < 1024; i++) repeat[i] = ((ulong)(1024 - i) << 32) | (uint)i;
            WorldDepthSort.SortQueueKeys(repeat, 1024, ref scratch);
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int pass = 0; pass < 64; pass++)
        {
            for (int i = 0; i < 1024; i++) repeat[i] = ((ulong)(1024 - i) << 32) | (uint)i;
            WorldDepthSort.SortQueueKeys(repeat, 1024, ref scratch);
        }
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        CheckThat("world radix warmed sort allocates nothing", bytes == 0, "not a timing benchmark");
        CheckThat("DepthKey folds signed zero as before",
            DepthKey.Of(0f, 0) == DepthKey.Of(BitConverter.UInt32BitsToSingle(0x80000000), 0),
            "oracle remains existing ulong keys, never float.CompareTo");
    }
}
