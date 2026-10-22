// SPDX-License-Identifier: BSD-2-Clause
using System.Threading;

namespace GUO.Game.Scenes
{
    /// <summary>
    /// Which frame, of which render-list owner, last marked a land tile as
    /// covering something below it: the replacement for a per-frame set of
    /// tiles. No engine types, so tools/render_sort_tests checks it alone.
    /// </summary>
    /// <remarks>
    /// Every owner draws its stamps from one process-wide counter, so two
    /// RenderLists (a scene recreated over the same loaded land, a probe
    /// beside the game) never hold the same value and never read each
    /// other's marks. A tile starts at 0 and the counter starts above it,
    /// so an unmarked tile is never mistaken for a marked one; the counter
    /// is 64-bit, so it does not wrap in any run (a thousand frames a second
    /// for three hundred million years).
    ///
    /// A tile holds one stamp, so this is not a set per owner: if owner B
    /// marked a tile between owner A's gather and A's draw, A would no
    /// longer see its own mark. The supported lifecycle is therefore that an
    /// owner's frame (Clear, gather, DrawWorld) completes before another
    /// owner touches the same land, which is how the client runs (one
    /// RenderLists, one frame at a time, on the main thread); RenderLists
    /// asserts it in debug builds, and tools/render_sort_tests compares
    /// against a set per owner under that lifecycle.
    /// </remarks>
    internal struct CoverStamp
    {
        private static long s_last;
        private long _current;

        /// <summary>The frame the owner is about to gather; its marks from before are void.</summary>
        public void NextFrame() => _current = Interlocked.Increment(ref s_last);

        /// <summary>The stamp this owner is marking with now; 0 before the first frame.</summary>
        public readonly long Current => _current;

        /// <summary>Whether <paramref name="tile"/> was marked by this owner in this frame.</summary>
        public readonly bool Marked(long tile) => tile == _current && _current != 0;

        public readonly void Mark(ref long tile) => tile = _current;
    }
}
