// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;

namespace GUO.Host;

public partial class BatcherProbe
{
    private void VerifyPerfIntervals()
    {
        var baseline = new PerfIntervalSampler.Counts(100, 200, 150, 50, 4, 5,
            6, 70, 70, 1, 2, 3, 4, 1000, 2000, 1000, 1, 1, 0);
        var first = baseline with { Draws = 102, Compared = 220, Reused = 166,
            Submitted = 54, Hides = 75, Reveals = 75, AtlasBytes = 1512,
            AllocatedBytes = 2800, ThreadAllocatedBytes = 1500, Gc0 = 2 };
        var second = first with { Draws = 103, Submitted = 57, AtlasBytes = 2024,
            AllocatedBytes = 3000, ThreadAllocatedBytes = 1700 };
        var samples = new PerfIntervalSampler(4);
        samples.Begin(baseline);
        samples.Add(2500, 2, 1, first, false);
        samples.Add(2750, 3, 2, second, true);
        var rows = samples.Report(new List<double> { 1000, 1500, 250 });
        CheckThat("interval counters use deltas and actual delayed duration",
            rows.Count == 2 && (double)rows[0]["duration_seconds"] == 2.5
            && (long)rows[0]["drawn_frames"] == 2 && (long)rows[0]["compared"] == 20
            && (double)rows[0]["submitted_records_per_draw"] == 2,
            "one delayed sample, no invented second");
        CheckThat("interval counters advance baseline for final partial",
            (bool)rows[1]["partial"] && (long)rows[1]["submitted_records"] == 3
            && (long)rows[1]["atlas_rgba_bytes_submitted"] == 512
            && (long)rows[1]["allocated_bytes"] == 200 && (int)rows[1]["route_index"] == 2,
            "final interval contains only its work");
        CheckThat("interval quantiles include only their own frames",
            (double)rows[0]["p50_ms"] == 1000 && (double)rows[0]["p99_ms"] == 1500
            && (double)rows[1]["p99_ms"] == 250, "no cumulative histogram");
        CheckThat("interval native visibility and thread allocation are explicit",
            (double)rows[0]["visibility_calls_per_draw"] == 5
            && (long)rows[0]["main_thread_allocated_bytes"] == 500
            && (int)rows[0]["gc0"] == 1, "two visibility calls per reused item");

        var noDraws = new PerfIntervalSampler(1);
        noDraws.Begin(baseline);
        noDraws.Add(1000, 1, 0, baseline with { Submitted = 60 }, false);
        var zero = noDraws.Report(new List<double> { 1000 })[0];
        CheckThat("zero-draw intervals do not claim zero submission cost",
            zero["submitted_records_per_draw"] == null && (long)zero["submitted_records"] == 10,
            "undefined per-draw ratios retain raw counts");
        noDraws.Add(1500, 2, 0, second, true);
        CheckThat("interval overflow is bounded and reported", noDraws.Dropped == 1
            && noDraws.Report(new List<double> { 1000, 500 }).Count == 1,
            "no storage growth during timing");
        bool rejected = false;
        try { noDraws.Add(1500, 3, 0, second, true); }
        catch (InvalidOperationException) { rejected = true; }
        CheckThat("interval boundary cannot rewind or repeat time", rejected, "reject misleading duration");

        var bounded = new PerfIntervalSampler(1001);
        bounded.Begin(default);
        bounded.Add(1, 1, 0, default, false); // warm the call before measuring it
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 2; i <= 1001; i++) bounded.Add(i, i, 0, default, false);
        CheckThat("interval capture storage allocates nothing", GC.GetAllocatedBytesForCurrentThread() == allocated,
            "1000 preallocated scalar samples; report built after timing");
    }
}
