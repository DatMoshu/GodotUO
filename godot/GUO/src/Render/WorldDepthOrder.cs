// SPDX-License-Identifier: BSD-2-Clause
using System;

namespace GUO.Renderer;

/// <summary>CPU ordering contract for GPU-world; never derives an order from coordinates.</summary>
internal static class WorldDepthOrder
{
    internal readonly record struct Draw(float Depth, int Seq, int Bucket, int Page, int Blend);
    internal readonly record struct Segment(int Start, int Count, int Page, int Blend);

    // This is the existing RenderLists oracle. Depth is supplied by CalculateDepthZ;
    // Seq is the actual queue submission, including mobile passes and covering land.
    internal static int Compare(float aDepth, int aSeq, float bDepth, int bSeq)
    {
        int order = aDepth.CompareTo(bDepth);
        return order != 0 ? order : aSeq.CompareTo(bSeq);
    }

    /// <summary>
    /// Packs an already oracle-ordered run, using all nine low bits as a rank
    /// within the supplied bucket. No four-bit tile index or diagonal surrogate.
    /// The caller must supply the complete run on every membership/order change;
    /// cached static ranks cannot be reused after dynamic insertion/removal.
    /// False requires ordered segments; no partially packed keys may be used.
    /// This does not authorize reordering blended draws across segments.
    /// </summary>
    internal static bool TryPack(ReadOnlySpan<Draw> ordered, Span<uint> keys)
    {
        if (keys.Length < ordered.Length)
            throw new ArgumentException("One output key is required per draw.", nameof(keys));
        keys = keys[..ordered.Length];
        keys.Clear();
        int rank = 0;
        for (int i = 0; i < ordered.Length; i++)
        {
            ref readonly var draw = ref ordered[i];
            if (!float.IsFinite(draw.Depth) || draw.Seq < 0 || draw.Bucket < 0
                || draw.Bucket >= (1 << 19))
                return Reject(keys);
            if (i > 0)
            {
                ref readonly var previous = ref ordered[i - 1];
                if (Compare(previous.Depth, previous.Seq, draw.Depth, draw.Seq) >= 0
                    || draw.Bucket < previous.Bucket)
                    return Reject(keys);
                rank = draw.Bucket == previous.Bucket ? rank + 1 : 0;
            }
            if (rank >= (1 << 9))
                return Reject(keys);
            // Bounds checked before either shift; never mask/truncate an overflow.
            keys[i] = ((uint)draw.Bucket << 9) | (uint)rank;
        }
        return true;
    }

    private static bool Reject(Span<uint> keys)
    {
        keys.Clear();
        return false;
    }

    /// <summary>
    /// Fallback draw runs, in exactly the supplied oracle order. A page or blend
    /// change ends a run, even when that material reappears later. The presenter
    /// must explicitly execute these runs in order, not submit unordered 3D nodes.
    /// </summary>
    internal static int OrderedSegments(ReadOnlySpan<Draw> ordered, Span<Segment> segments)
    {
        // Worst case alternates material every draw. Require capacity up front.
        if (segments.Length < ordered.Length)
            throw new ArgumentException("Fallback needs capacity for one segment per draw.", nameof(segments));
        int count = 0;
        for (int start = 0; start < ordered.Length;)
        {
            int end = start + 1;
            while (end < ordered.Length && ordered[end].Page == ordered[start].Page
                && ordered[end].Blend == ordered[start].Blend)
                end++;
            segments[count++] = new Segment(start, end - start, ordered[start].Page, ordered[start].Blend);
            start = end;
        }
        return count;
    }

    /// <summary>
    /// Which queued draws need the ordered pass. Keys arrive in submission
    /// order and must not decrease (the sort put them there). A draw goes to
    /// the ordered pass when it blends, or when it shares its key with the
    /// draw before it: equal keys cannot order themselves across page buffers,
    /// so every later holder of a key is drawn after the first one, in
    /// submission order. Returns false when a key decreases, which means the
    /// key no longer reproduces the sort and the frame must not use it.
    /// </summary>
    internal static bool Plan(ReadOnlySpan<uint> keys, ReadOnlySpan<bool> blended, Span<bool> ordered)
    {
        if (blended.Length != keys.Length || ordered.Length < keys.Length)
            throw new ArgumentException("One blended flag in and one ordered flag out per key.", nameof(ordered));
        for (int i = 0; i < keys.Length; i++)
        {
            if (i > 0 && keys[i] < keys[i - 1])
            {
                ordered[..keys.Length].Clear();
                return false;
            }
            ordered[i] = blended[i] || (i > 0 && keys[i] == keys[i - 1]);
        }
        return true;
    }

    /// <summary>
    /// Plan with each draw's page known. Equal keys in one page buffer keep
    /// their submission order on their own: a buffer draws its instances in
    /// order and the depth test passes a tie (the depth probe's all-ties scene
    /// verifies it; WorldDepthPass.SamePageTiesInOrder). So a later holder of
    /// a key is ordered once its run has left the first page, or once the
    /// draw before it is ordered (it has to land over that one), and from
    /// then on every later holder is. Returns false when a key decreases.
    /// </summary>
    internal static bool Plan(ReadOnlySpan<uint> keys, ReadOnlySpan<bool> blended, ReadOnlySpan<int> pages, Span<bool> ordered)
    {
        if (blended.Length != keys.Length || pages.Length != keys.Length || ordered.Length < keys.Length)
            throw new ArgumentException("One blended flag and one page in and one ordered flag out per key.", nameof(ordered));
        for (int i = 0; i < keys.Length; i++)
        {
            if (i > 0 && keys[i] < keys[i - 1])
            {
                ordered[..keys.Length].Clear();
                return false;
            }
            bool tie = i > 0 && keys[i] == keys[i - 1];
            ordered[i] = blended[i] || (tie && (ordered[i - 1] || pages[i] != pages[i - 1]));
        }
        return true;
    }
}
