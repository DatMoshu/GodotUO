// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Diagnostics;

namespace GUO.Game.Scenes;

/// <summary>Sorts freshly built DepthKey keys in queue-index order.</summary>
/// <remarks>
/// PORT DEVIATION (GUO): stable radix passes over transformed depth preserve
/// the existing full-ulong order because input key i has unsigned low word i.
/// This is not a general ulong sorter: arbitrary/reordered low words require
/// all eight bytes or Array.Sort. DrawWorld rebuilds this precondition each frame.
/// No float comparison, reconstruction or depth quantization is introduced.
/// </remarks>
internal static class WorldDepthSort
{
    // Small lists keep the primitive sort; measured synthetic cases below this
    // range lose with radix. The upper bound caps scratch at 512 KiB per owner.
    internal const int MinRadixCount = 1024;
    internal const int MaxRadixCount = 65536;

    internal static void SortQueueKeys(ulong[] keys, int count, ref ulong[] scratch)
    {
        if ((uint)count > (uint)keys.Length)
            throw new ArgumentOutOfRangeException(nameof(count));
        if (count < MinRadixCount || count > MaxRadixCount)
        {
            Array.Sort(keys, 0, count);
            return;
        }
#if DEBUG
        for (int i = 0; i < count; i++)
            Debug.Assert(DepthKey.Seq(keys[i]) == i, "radix input must be fresh queue-index order");
#endif
        // Already ordered/equal-depth queues need neither scatter nor scratch.
        int ordered = 1;
        while (ordered < count && keys[ordered - 1] <= keys[ordered]) ordered++;
        if (ordered == count) return;

        // Counting all bytes in one traversal permits skipping constant bytes.
        // Histograms depend on membership, not on intermediate permutation.
        Span<int> counts = stackalloc int[1024];
        counts.Clear();
        for (int i = 0; i < count; i++)
        {
            uint depth = (uint)(keys[i] >> 32);
            counts[(int)(depth & 255)]++;
            counts[256 + (int)((depth >> 8) & 255)]++;
            counts[512 + (int)((depth >> 16) & 255)]++;
            counts[768 + (int)(depth >> 24)]++;
        }
        if (scratch == null || scratch.Length < count)
            scratch = new ulong[Math.Min(MaxRadixCount, Math.Max(count, (scratch?.Length ?? 0) * 2))];

        ulong[] source = keys, destination = scratch;
        for (int pass = 0; pass < 4; pass++)
        {
            Span<int> offsets = counts.Slice(pass * 256, 256);
            int shift = 32 + 8 * pass;
            int oneBucket = (int)((source[0] >> shift) & 255);
            if (offsets[oneBucket] == count) continue;
            int start = 0;
            for (int bucket = 0; bucket < 256; bucket++)
            {
                int size = offsets[bucket]; offsets[bucket] = start; start += size;
            }
            // Forward scatter is stable, including ties spanning low-word carries.
            for (int i = 0; i < count; i++)
            {
                ulong key = source[i];
                destination[offsets[(int)((key >> shift) & 255)]++] = key;
            }
            (source, destination) = (destination, source);
        }
        // An odd number of varying bytes ends in scratch. Inactive tails stay put.
        if (!ReferenceEquals(source, keys)) source.AsSpan(0, count).CopyTo(keys);
    }
}
