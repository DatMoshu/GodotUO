// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace GUO.Utility;

/// <summary>Main-thread diagnostic wall times, never GPU or exclusive CPU time.</summary>
internal static class RuntimeTimings
{
    internal enum Stage { Process, Input, Packets, Plugins, SceneUpdate, UiUpdate, Pacing, Cursor, Audio, QueuedActions, Draw, Count }
    internal static bool Enabled;
    private static readonly long[] Ticks = new long[(int)Stage.Count];
    private static readonly long[] Calls = new long[(int)Stage.Count];
    internal static long Now() => Enabled ? Stopwatch.GetTimestamp() : 0;
    internal static long Elapsed(long start) => Enabled && start != 0 ? Stopwatch.GetTimestamp() - start : 0;
    internal static long Add(Stage stage, long start)
    {
        if (!Enabled || start == 0) return 0;
        long now = Stopwatch.GetTimestamp();
        Ticks[(int)stage] += now - start;
        Calls[(int)stage]++;
        return now;
    }
    internal static void Reset()
    {
        Array.Clear(Ticks);
        Array.Clear(Calls);
    }
    // Called outside the timed interval. Process includes the other stages;
    // Draw includes existing world/UI scopes. Do not add overlapping totals.
    internal static Dictionary<string, object> Report(long draws)
    {
        var result = new Dictionary<string, object>();
        for (int i = 0; i < (int)Stage.Count; i++)
            result[((Stage)i).ToString()] = new Dictionary<string, object>
            {
                ["calls"] = Calls[i],
                ["total_ms"] = Math.Round(Ticks[i] * 1000.0 / Stopwatch.Frequency, 4),
                ["ms_per_draw"] = draws > 0 ? Math.Round(Ticks[i] * 1000.0 / Stopwatch.Frequency / draws, 6) : 0.0,
            };
        return result;
    }
}
