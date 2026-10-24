// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Linq;
using GUO.Renderer;
using GUO.Utility;

namespace GUO.Host;

public partial class BatcherProbe
{
    private void VerifyAtlasProfiling()
    {
        bool was = RuntimeTimings.Enabled;
        using var atlas = new TextureAtlas(16, 8, "probe-metrics");
        try
        {
            RuntimeTimings.Enabled = false;
            var page = atlas.AddSprite(new uint[] { 0xFFFFFFFF }, 1, 1, out var region);
            atlas.Flush();
            TextureAtlas.BeginObservations();
            RuntimeTimings.Enabled = true;
            atlas.Flush();
            CheckThat("atlas profiling excludes clean flush and prior creation",
                TextureAtlas.ObservedUpdates == 0 && TextureAtlas.ObservedCreations == 0, "measurement boundary");
            atlas.AddSprite(new uint[] { 0xFF00FF00 }, 1, 1, out _);
            atlas.AddSprite(new uint[] { 0xFFFF0000 }, 1, 1, out _);
            atlas.Flush(); atlas.Flush();
            CheckThat("atlas profiling counts one update for several packed writes",
                TextureAtlas.ObservedUpdates == 1, "one dirty page");
            atlas.AddSprite(new uint[15 * 7], 15, 7, out _);
            CheckThat("atlas profiling counts populated dedicated creation",
                TextureAtlas.ObservedCreations == 1, "oversized sprite");
            TextureAtlas.TryWriteRegion(page, region, new uint[] { 0xFFFFFFFF });
            atlas.Flush();
            var rows = TextureAtlas.UploadReport().Where(r => (string)r["owner"] == "probe-metrics").ToArray();
            CheckThat("atlas profiling uses actual dimensions for normal and dedicated pages",
                rows.Length == 2 && (long)rows[0]["rgba_bytes_submitted"] == 16L * 8 * 4 * 2
                && (long)rows[1]["rgba_bytes_submitted"] == 15L * 7 * 4, "RGBA byte accounting");
            using (var created = new TextureAtlas(16, 8, "probe-created"))
            {
                created.AddSprite(new uint[] { 0xFFFFFFFF }, 1, 1, out _);
                created.Flush();
                var row = TextureAtlas.UploadReport().Single(r => (string)r["owner"] == "probe-created");
                CheckThat("atlas profiling separates blank page creation and populated flush",
                    (long)row["creations"] == 1 && (long)row["updates"] == 1
                    && (long)row["rgba_bytes_submitted"] == 16L * 8 * 4 * 2, "normal page lifecycle");
            }
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) atlas.Flush();
            CheckThat("profiled clean flushes allocate nothing", GC.GetAllocatedBytesForCurrentThread() == before, "1000 clean flushes");
            atlas.Dispose();
            CheckThat("atlas observations survive resource disposal without holding resources",
                TextureAtlas.UploadReport().Count(r => (string)r["owner"] == "probe-metrics") == 2, "completed uploads retained");
            TextureAtlas.BeginObservations();
            CheckThat("new observation drops disposed atlas history",
                TextureAtlas.UploadReport().All(r => (string)r["owner"] != "probe-metrics"), "no stale pages");
            RuntimeTimings.Enabled = false;
            using var disabled = new TextureAtlas(16, 8, "probe-disabled");
            disabled.AddSprite(new uint[] { 0 }, 1, 1, out _); disabled.Flush();
            CheckThat("disabled atlas observation records no uploads",
                TextureAtlas.ObservedUpdates == 0 && TextureAtlas.ObservedCreations == 0, "ordinary gameplay");

            RuntimeTimings.Reset();
            long disabledAt = RuntimeTimings.Now();
            RuntimeTimings.Add(RuntimeTimings.Stage.Draw, disabledAt);
            var disabledTimes = (System.Collections.Generic.Dictionary<string, object>)RuntimeTimings.Report(1)["Draw"];
            CheckThat("disabled runtime timing records no scopes", disabledAt == 0
                && (long)disabledTimes["calls"] == 0, "ordinary gameplay");
            RuntimeTimings.Enabled = true;
            RuntimeTimings.Add(RuntimeTimings.Stage.Draw, 0);
            long at = RuntimeTimings.Now();
            at = RuntimeTimings.Add(RuntimeTimings.Stage.Draw, at);
            RuntimeTimings.Add(RuntimeTimings.Stage.Draw, at);
            var enabledTimes = (System.Collections.Generic.Dictionary<string, object>)RuntimeTimings.Report(2)["Draw"];
            CheckThat("runtime timing ignores unstarted scopes and chains completed ones",
                (long)enabledTimes["calls"] == 2 && (double)enabledTimes["total_ms"] >= 0,
                "two completed scopes");
            RuntimeTimings.Reset();
            var resetTimes = (System.Collections.Generic.Dictionary<string, object>)RuntimeTimings.Report(0)["Draw"];
            CheckThat("runtime timing resets interval totals and accepts no draws",
                (long)resetTimes["calls"] == 0 && (double)resetTimes["total_ms"] == 0
                && (double)resetTimes["ms_per_draw"] == 0, "fresh measurement boundary");

            RuntimeTimings.Enabled = false;
            using var rolling = new TextureAtlas(8, 8, "probe-rollover");
            rolling.AddSprite(new uint[16], 4, 4, out _);
            TextureAtlas.BeginObservations();
            RuntimeTimings.Enabled = true;
            rolling.Flush();
            rolling.AddSprite(new uint[16], 4, 4, out _); rolling.Flush();
            var rollover = TextureAtlas.UploadReport().Where(r => (string)r["owner"] == "probe-rollover").ToArray();
            CheckThat("atlas profiling counts dirty-at-start and normal-page rollover",
                rollover.Length == 2 && (long)rollover[0]["updates"] == 1 && (long)rollover[0]["creations"] == 0
                && (long)rollover[1]["updates"] == 1 && (long)rollover[1]["creations"] == 1,
                "two distinct page lifecycles");
            var totals = TextureAtlas.UploadReport();
            CheckThat("atlas page counts agree with interval totals",
                totals.Sum(r => (long)r["updates"]) == TextureAtlas.ObservedUpdates
                && totals.Sum(r => (long)r["creations"]) == TextureAtlas.ObservedCreations
                && totals.Sum(r => (long)r["rgba_bytes_submitted"]) == TextureAtlas.ObservedBytes, "all observed owners and bytes");
            TextureAtlas.BeginObservations();
            CheckThat("second observation resets still-live page totals",
                TextureAtlas.ObservedUpdates == 0 && TextureAtlas.ObservedCreations == 0
                && TextureAtlas.ObservedBytes == 0
                && TextureAtlas.UploadReport().All(r => (long)r["updates"] == 0 && (long)r["creations"] == 0),
                "fresh interval with resident pages");

            var route = new PerfProbe.ObservedRoute(3);
            route.Observe(0, 10, 20, 0); route.Observe(0, 10, 20, 0);
            CheckThat("route records first position once", route.Points().Length == 1 && route.Tiles == 0, "no movement");
            route.Observe(0, 10, 20, 1);
            CheckThat("route records elevation without horizontal distance", route.Points().Length == 2 && route.Tiles == 0, "Z-only change");
            route.Observe(0, 11, 20, 1);
            CheckThat("final passive route observation counts movement", route.Points().Length == 3 && route.Tiles == 1, "no Walk call");
            route.Observe(1, 100, 100, 0); route.Observe(1, 100, 100, 0);
            route.Observe(1, 102, 100, 0);
            CheckThat("route overflow tracks distinct observations and ignores cross-map distance",
                route.Points().Length == 3 && route.Tiles == 3 && route.Dropped == 2, "bounded route storage");

            using var restore = new PerfProbe.MeasurementState();
            Godot.Engine.MaxFps = 123;
            PerfStages.Enabled = false; RuntimeTimings.Enabled = false;
            var vsync = Godot.DisplayServer.WindowGetVsyncMode();
            foreach (bool enableTimers in new[] { false, true })
            {
                float[] intervals = { 4f, 217f };
                try
                {
                    using var state = new PerfProbe.MeasurementState(intervals);
                    state.Uncap();
                    if (enableTimers) { PerfStages.Enabled = true; RuntimeTimings.Enabled = true; }
                    throw new InvalidOperationException("synthetic measurement failure");
                }
                catch (InvalidOperationException) { }
                CheckThat("probe restores controls after failure " + (enableTimers ? "during sample" : "before sample"),
                    Godot.Engine.MaxFps == 123 && Godot.DisplayServer.WindowGetVsyncMode() == vsync
                    && !PerfStages.Enabled && !RuntimeTimings.Enabled && intervals.SequenceEqual(new[] { 4f, 217f }),
                    "pacing, vsync and diagnostic flags");
            }
        }
        finally
        {
            RuntimeTimings.Enabled = was;
        }
    }
}
