// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using System.Text.Json;

namespace GUO.Host;

// Probe-only: called after leaving the sample phase, before sorting durations.
// Reuses the probe's existing chronological samples; no extra hot-path storage.
internal static class PerfRawFrames
{
    internal static void Write(System.IO.Stream output, IReadOnlyList<double> samples,
        int expectedCount, int initialCapacity, bool storageGrew, bool sampleWindowClosed)
    {
        if (!sampleWindowClosed) throw new InvalidOperationException("Raw frames require a closed sample window.");
        if (expectedCount <= 0 || samples.Count != expectedCount)
            throw new InvalidOperationException("Raw frame count does not match the measured interval.");
        if (storageGrew || initialCapacity < samples.Count)
            throw new InvalidOperationException("Sample storage grew during measurement.");
        foreach (double value in samples)
            if (!double.IsFinite(value) || value <= 0)
                throw new InvalidOperationException("Invalid raw frame duration.");
        JsonSerializer.Serialize(output, new
        {
            schema = "guo/raw-frames@1", frames = samples.Count, dropped = 0,
            initial_capacity = initialCapacity, storage_grew = false,
            // Array index is the zero-based measurement order, never percentile order.
            frame_ms = samples
        });
    }
}
