// SPDX-License-Identifier: BSD-2-Clause

using System;

namespace GUO.Renderer
{
    /// <summary>
    /// Atlas uploads v2 (PERF-LANE-03, review item G), behind
    /// <c>GUO_ATLAS_V2=1</c>: smaller dynamic pages and a per-frame upload budget.
    /// </summary>
    /// <remarks>
    /// <para>
    /// PORT DEVIATION (GUO). Godot replaces a whole texture on every update
    /// (see the cost table on <see cref="TextureAtlas"/>), so while walking
    /// every page a new sprite lands on is re-sent whole: 147 full 2048x2048
    /// uploads in a 60 s walk, with 41 ms frames where several land together.
    /// Off by default; with it on:
    /// </para>
    /// <list type="bullet">
    /// <item>The dynamic atlases (art, animation, gump, glyph) use
    /// <see cref="V2PageSize"/> pages: a quarter of the bytes per upload.
    /// Land, texmaps and lights keep theirs. Packing, placement order and
    /// nearest sampling are unchanged; only which page and where on it a
    /// sprite sits differ, and every UV is still an exact texel.</item>
    /// <item>In the world, at most <see cref="V2UploadsPerFrame"/> dirty pages
    /// are uploaded per frame, oldest-dirty first; the rest wait for the next
    /// frames. A sprite whose page is still waiting draws what its page held
    /// before (transparent for a fresh region) for those frames, so a newly
    /// decoded sprite may appear one or more frames later than today. That is
    /// the product decision this toggle exists to let the owner test.</item>
    /// <item>Pre-warm: before the world is entered and for
    /// <see cref="V2GraceMs"/> after, the budget does not apply, so everything
    /// decoded while logging in and loading the first view is uploaded at
    /// once, as before.</item>
    /// <item>The minimap draws from its own small texture instead of
    /// rewriting a region of the gump atlas on every step
    /// (<c>MiniMapGump</c>).</item>
    /// </list>
    /// </remarks>
    public sealed partial class TextureAtlas
    {
        /// <summary>GUO_ATLAS_V2=1, read once at start (the batcher probe flips it for its own atlases).</summary>
        public static bool V2 { get; internal set; } = Environment.GetEnvironmentVariable("GUO_ATLAS_V2") == "1";

        public const int V2PageSize = 1024;
        public const int V2UploadsPerFrame = 2;
        public const ulong V2GraceMs = 1000;

        /// <summary>Uploads a budgeted frame left for later frames, since start (main thread).</summary>
        internal static long DeferredUploads;

        private static readonly string[] V2Owners = { "art", "animation", "gump", "glyph" };

        // Monotonic stamp of when each dirty page became dirty; the oldest
        // goes first, so a page dirtied every frame cannot starve the rest.
        private static long _dirtyClock;
        private readonly System.Collections.Generic.List<long> _dirtySince = new();
        private static ulong _budgetFrame = ulong.MaxValue, _countedFrame = ulong.MaxValue, _worldSince;
        private static int _budgetLeft;

        private static int PageCap(string owner) =>
            V2 && Array.IndexOf(V2Owners, owner) >= 0 ? V2PageSize : MaxPageSize;

        private void MarkDirty(int index)
        {
            if (!_dirty[index])
            {
                _dirtySince[index] = ++_dirtyClock;
            }

            _dirty[index] = true;
        }

        /// <summary>Whether this frame's uploads are budgeted: in the world, after the grace period.</summary>
        private static bool Budgeted()
        {
            bool inWorld = GUO.Client.Game?.UO?.World?.InGame == true;
            ulong now = Godot.Time.GetTicksMsec();
            if (!inWorld)
            {
                _worldSince = 0;
                return false;
            }

            if (_worldSince == 0)
            {
                _worldSince = now;
            }

            return now - _worldSince >= V2GraceMs;
        }

        private static void FlushBudgeted()
        {
            ulong frame = Godot.Engine.GetProcessFrames();
            if (frame != _budgetFrame)
            {
                _budgetFrame = frame;
                _budgetLeft = V2UploadsPerFrame;
            }

            UploadOldest(ref _budgetLeft);

            // Once a frame: pages still waiting when the budget ran out.
            if (frame == _countedFrame)
            {
                return;
            }

            _countedFrame = frame;
            for (int a = 0; a < _live.Count; a++)
            {
                TextureAtlas atlas = _live[a];
                for (int i = 0; i < atlas._pages.Count; i++)
                {
                    if (atlas._dirty[i])
                    {
                        DeferredUploads++;
                    }
                }
            }
        }

        /// <summary>Uploads dirty pages, oldest first, while <paramref name="budget"/> lasts.</summary>
        internal static void UploadOldest(ref int budget)
        {
            while (budget > 0)
            {
                TextureAtlas oldest = null;
                int oldestPage = -1;
                long oldestSince = long.MaxValue;
                for (int a = 0; a < _live.Count; a++)
                {
                    TextureAtlas atlas = _live[a];
                    for (int i = 0; i < atlas._pages.Count; i++)
                    {
                        if (atlas._dirty[i] && atlas._dirtySince[i] < oldestSince)
                        {
                            oldest = atlas;
                            oldestPage = i;
                            oldestSince = atlas._dirtySince[i];
                        }
                    }
                }

                if (oldest == null)
                {
                    return;
                }

                oldest.Upload(oldestPage);
                budget--;
            }
        }

        /// <summary>Page width and whether page <paramref name="index"/> waits for upload; for the batcher probe.</summary>
        internal (int Width, bool Dirty) ProbePage(int index) => (_width, _dirty[index]);
    }
}
