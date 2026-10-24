// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using GUO.Renderer;

namespace GUO.Host;

// Probe-only, main-thread samples. Allocate before timing; serialize after it.
// Delayed boundaries produce one real interval, never fabricated one-second rows.
internal sealed class PerfIntervalSampler
{
    internal readonly record struct Counts(long Draws, long Compared, long Reused,
        long Submitted, long RewriteClears, long Uncached, long OwnerClears,
        long Hides, long Reveals, long LandUploads, long LandRebuilds,
        long AtlasUpdates, long AtlasCreations, long AtlasBytes,
        long AllocatedBytes, long ThreadAllocatedBytes, int Gc0, int Gc1, int Gc2)
    {
        internal static Counts Capture() => new(UltimaBatcher2D.FramesBegun,
            RetainedCanvasCommands.Compared, RetainedCanvasCommands.Reused,
            RetainedCanvasCommands.Submitted, RetainedCanvasCommands.Clears,
            RetainedCanvasCommands.Uncached, UltimaBatcher2D.CanvasOwnerClears,
            UltimaBatcher2D.CanvasHides, UltimaBatcher2D.CanvasReveals,
            LandPages.Uploads, LandPages.Rebuilds, TextureAtlas.ObservedUpdates,
            TextureAtlas.ObservedCreations, TextureAtlas.ObservedBytes,
            GC.GetTotalAllocatedBytes(), GC.GetAllocatedBytesForCurrentThread(),
            GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
    }

    private readonly record struct Sample(double StartMs, double EndMs,
        int StartFrame, int EndFrame, int RouteIndex, bool Partial, Counts Before, Counts After);
    private readonly Sample[] _samples;
    private Counts _last;
    private double _lastMs;
    private int _lastFrame, _count, _thread;
    internal bool ThreadChanged { get; private set; }
    internal int Dropped { get; private set; }

    internal PerfIntervalSampler(int capacity) => _samples = new Sample[capacity];

    internal void Begin(Counts counts)
    {
        _last = counts;
        _thread = Environment.CurrentManagedThreadId;
    }

    internal void Observe(double elapsedMs, int frames, int routeIndex, bool finish = false)
    {
        ThreadChanged |= Environment.CurrentManagedThreadId != _thread;
        if (frames == _lastFrame || (!finish && elapsedMs - _lastMs < 1000)) return;
        Add(elapsedMs, frames, routeIndex, Counts.Capture(), finish);
    }

    // Explicit snapshot seam also permits deterministic delta tests without native calls.
    internal void Add(double elapsedMs, int frames, int routeIndex, Counts counts, bool partial)
    {
        if (elapsedMs <= _lastMs || frames <= _lastFrame)
            throw new InvalidOperationException("Performance intervals must advance time and frames.");
        if (_count < _samples.Length)
            _samples[_count++] = new Sample(_lastMs, elapsedMs, _lastFrame, frames,
                routeIndex, partial, _last, counts);
        else Dropped++;
        _lastMs = elapsedMs;
        _lastFrame = frames;
        _last = counts;
    }

    internal List<Dictionary<string, object>> Report(List<double> frameMs)
    {
        var rows = new List<Dictionary<string, object>>(_count);
        for (int i = 0; i < _count; i++)
        {
            Sample s = _samples[i];
            Counts a = s.Before, b = s.After;
            int frames = s.EndFrame - s.StartFrame;
            long draws = b.Draws - a.Draws;
            var sorted = new double[frames];
            frameMs.CopyTo(s.StartFrame, sorted, 0, frames);
            Array.Sort(sorted);
            double P(double q) => sorted[Math.Max(0, (int)Math.Ceiling(q * frames) - 1)];
            object PerDraw(long value) => draws > 0 ? (object)((double)value / draws) : null;
            rows.Add(new Dictionary<string, object>
            {
                ["start_seconds"] = s.StartMs / 1000, ["end_seconds"] = s.EndMs / 1000,
                ["duration_seconds"] = (s.EndMs - s.StartMs) / 1000, ["partial"] = s.Partial,
                ["process_frames"] = frames, ["drawn_frames"] = draws, ["route_index"] = s.RouteIndex,
                ["draw_fps"] = draws * 1000 / (s.EndMs - s.StartMs),
                ["p50_ms"] = P(.5), ["p95_ms"] = P(.95), ["p99_ms"] = P(.99), ["max_ms"] = sorted[^1],
                ["compared"] = b.Compared - a.Compared, ["prefix_matches"] = b.Reused - a.Reused,
                ["submitted_records"] = b.Submitted - a.Submitted, ["rewrite_clears"] = b.RewriteClears - a.RewriteClears,
                ["uncached_items"] = b.Uncached - a.Uncached, ["owner_clears"] = b.OwnerClears - a.OwnerClears,
                ["visibility_hides"] = b.Hides - a.Hides, ["visibility_reveals"] = b.Reveals - a.Reveals,
                ["compared_per_draw"] = PerDraw(b.Compared - a.Compared),
                ["prefix_matches_per_draw"] = PerDraw(b.Reused - a.Reused),
                ["submitted_records_per_draw"] = PerDraw(b.Submitted - a.Submitted),
                ["visibility_calls_per_draw"] = PerDraw(b.Hides - a.Hides + b.Reveals - a.Reveals),
                ["land_uploads"] = b.LandUploads - a.LandUploads, ["land_rebuilds"] = b.LandRebuilds - a.LandRebuilds,
                ["atlas_updates"] = b.AtlasUpdates - a.AtlasUpdates, ["atlas_creations"] = b.AtlasCreations - a.AtlasCreations,
                ["atlas_rgba_bytes_submitted"] = b.AtlasBytes - a.AtlasBytes,
                ["allocated_bytes"] = b.AllocatedBytes - a.AllocatedBytes,
                ["allocated_bytes_per_draw"] = PerDraw(b.AllocatedBytes - a.AllocatedBytes),
                ["main_thread_allocated_bytes"] = ThreadChanged ? null : (object)(b.ThreadAllocatedBytes - a.ThreadAllocatedBytes),
                ["gc0"] = b.Gc0 - a.Gc0, ["gc1"] = b.Gc1 - a.Gc1, ["gc2"] = b.Gc2 - a.Gc2,
            });
        }
        return rows;
    }
}
