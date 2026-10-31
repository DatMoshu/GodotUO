// SPDX-License-Identifier: BSD-2-Clause

using System.Diagnostics;

namespace GUO.Renderer;

/// <summary>
/// Where a world frame goes, stage by stage (PERF-03): the perf probe turns
/// this on while it measures and reads the totals per frame. Off, every call
/// is one branch. Times are taken at stage boundaries and, in the sorted
/// draw, only when the kind of object drawn changes, so the timers cost a few
/// thousand timestamps a frame rather than one per sprite.
/// </summary>
public static class PerfStages
{
    public enum Stage
    {
        /// <summary>Chunk meshes rebuilt because they were dirty.</summary>
        MeshBuild,
        /// <summary>The tile walk: culling, alpha, roof and height checks, picking and queueing.</summary>
        Gather,
        /// <summary>Foliage fades and the overhead-text entities after the walk.</summary>
        GatherTail,
        /// <summary>Land from the chunk meshes (or the merged land).</summary>
        LandMesh,
        /// <summary>Land outside the meshes (animated water and the like).</summary>
        LandLoose,
        /// <summary>The depth sort of the world list.</summary>
        Sort,
        /// <summary>Statics drawn from the sorted list.</summary>
        Statics,
        /// <summary>Mobiles, their shadows and bodies.</summary>
        Mobiles,
        /// <summary>Items and corpses on the ground.</summary>
        Items,
        /// <summary>Land queued again over what is sunk under it (RenderLists.CoverFromBelow).</summary>
        CoverLand,
        /// <summary>Multi components (houses, boats).</summary>
        Multis,
        /// <summary>Effects, splats, terrain layers and anything else in the sorted list.</summary>
        Other,
        /// <summary>Transparent objects and world gumps, after the sorted list.</summary>
        Transparent,
        /// <summary>Socket collection, including decrypt/decompress (inclusive).</summary>
        NetworkReceive,
        /// <summary>Huffman decode only; nested inside NetworkReceive.</summary>
        NetworkDecompress,
        /// <summary>Packet framing, plugin receive callbacks and dispatch (inclusive).</summary>
        NetworkParse,
        /// <summary>Receive plugin callbacks; nested inside NetworkParse.</summary>
        PluginReceive,
        /// <summary>Game/mod packet dispatch; nested inside NetworkParse.</summary>
        NetworkDispatch,
        /// <summary>Queued sends and network statistics update.</summary>
        NetworkFlush,
        /// <summary>Plugin tick callbacks, outside world simulation.</summary>
        PluginTick,
        /// <summary>UI update, outside world simulation.</summary>
        UiUpdate,
        Count,
    }

    public static bool Enabled;

    /// <summary>Milliseconds per stage since the last <see cref="Take"/>.</summary>
    public static readonly double[] Ms = new double[(int)Stage.Count];

    /// <summary>Objects drawn per stage since the last <see cref="Take"/> (sorted-list stages only).</summary>
    public static readonly int[] Drawn = new int[(int)Stage.Count];

    private static readonly double ToMs = 1000.0 / Stopwatch.Frequency;

    public static long Now() => Enabled ? Stopwatch.GetTimestamp() : 0;

    /// <summary>Allocation-free, exception-safe main-thread timing scope.</summary>
    public readonly struct Scope : System.IDisposable
    {
        private readonly Stage _stage;
        private readonly long _start;
        private readonly bool _enabled;

        public Scope(Stage stage)
        {
            _stage = stage;
            _enabled = Enabled;
            _start = _enabled ? Stopwatch.GetTimestamp() : 0;
        }

        public void Dispose()
        {
            if (_enabled && Enabled)
                Add(_stage, _start);
        }
    }

    /// <summary>Adds the time since <paramref name="since"/> to the stage and returns now.</summary>
    public static long Add(Stage stage, long since)
    {
        if (!Enabled)
        {
            return 0;
        }

        long now = Stopwatch.GetTimestamp();
        Ms[(int)stage] += (now - since) * ToMs;
        return now;
    }

    public static void Count(Stage stage)
    {
        if (Enabled)
        {
            Drawn[(int)stage]++;
        }
    }

    /// <summary>Copies the totals into the caller's arrays and starts again.</summary>
    public static void Take(double[] ms, int[] drawn)
    {
        for (int i = 0; i < (int)Stage.Count; i++)
        {
            ms[i] = Ms[i];
            drawn[i] = Drawn[i];
            Ms[i] = 0;
            Drawn[i] = 0;
        }
    }
}
