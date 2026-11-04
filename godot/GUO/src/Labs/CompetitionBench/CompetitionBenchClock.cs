// SPDX-License-Identifier: BSD-2-Clause
// FROZEN HARNESS (task 1155). Entrants may not edit this file.
#if GUO_LABS && DEBUG
using System.Collections.Generic;
using System.Threading;
using Godot;

namespace GUO.Labs.CompetitionBench;

/// <summary>
/// Deterministic simulation time for the bench. While <see cref="Active"/>, GameController
/// advances its game clock by exactly <see cref="FixedDelta"/> (1/60 s) per rendered frame,
/// ignoring wall time. Every consumer of <c>Time.Ticks</c> / <c>Time.Delta</c> (wind, clouds,
/// water, weather, flicker) therefore advances the same amount per frame whatever the FPS,
/// so a faster renderer cannot traverse a different workload.
/// The sun/day clock (ShardClock) is separate and pinned to the scene time every frame.
/// Entrant wind/cloud animation MUST read <c>Time.Ticks</c> (or this class); never wall time.
/// Inert when the bench is not running: <see cref="Active"/> is false and nothing else runs.
/// </summary>
public static class CompetitionBenchClock
{
    /// <summary>Exactly 1/60 s of simulation per rendered frame.</summary>
    public const double FixedDelta = 1.0 / 60.0;
    /// <summary>Simulation seconds every repeat starts from (identical phase and seed in each repeat).</summary>
    public const double SampleBaseSeconds = 100000.0;
    /// <summary>Capture mode renders the still at sim frame N after the base.</summary>
    public const int CaptureFrame = 600;

    internal static volatile bool Active;
    private static double _rebase = -1;
    private static long _frames;

    /// <summary>Simulation frames stepped since the bench started (for the result file).</summary>
    public static long Frames => Interlocked.Read(ref _frames);

    /// <summary>The next game frame starts from this simulation time (seconds).</summary>
    internal static void RebaseTo(double seconds) => _rebase = seconds;

    /// <summary>Called by GameController once per frame; returns the fixed delta.</summary>
    public static double Step(ref double totalGameTime)
    {
        if (_rebase >= 0) { totalGameTime = _rebase; _rebase = -1; }
        Interlocked.Increment(ref _frames);
        return FixedDelta;
    }
}

/// <summary>
/// Raw RenderingDevice timestamps around the whole OrderedWorldCapture render-thread span
/// (world capture, light, shadow, soften and presentation passes). Begin/End stamp on the
/// render thread; results are read one resolution later from the same thread (the existing
/// native-physical-performance pattern): no fence, no readback, no extra thread hop.
/// A frame without a usable pair is INVALID (counted), never zero. Vulkan only: its raw GPU
/// timestamps are nanoseconds (see NativePhysicalWorldProbe.Performance.cs).
/// </summary>
public static class BenchGpuMarkers
{
    public const string BeginName = "guo.bench.predraw.begin", EndName = "guo.bench.predraw.end";
    internal static volatile bool Enabled;

    /// <summary>One resolved frame. Millisecond fields are only meaningful when <see cref="Valid"/>.</summary>
    public readonly record struct Sample(ulong Frame, bool Valid, string Why, double PreDrawGpuMs, double PreDrawCpuMs,
        double WholeFrameGpuMs, int PassMarkers, int PassMarkersInside, double ViewportGpuMs = 0);

    /// <summary>
    /// Viewports whose GPU render time is measured. The viewport value is read on the render thread
    /// at the same moment as the RD timestamps are resolved, so both belong to one resolved frame.
    /// A reading that is not finite or not above zero makes the frame INVALID.
    /// </summary>
    internal static volatile Rid[] Viewports = System.Array.Empty<Rid>();

    private static readonly object Gate = new();
    private static readonly List<Sample> Resolved = new(1 << 16);
    private static ulong _lastFrame = ulong.MaxValue;
    private static bool _verifiedDriver;
    private static long _stamped, _unavailable;

    public static long Stamped => Interlocked.Read(ref _stamped);
    public static long Unavailable => Interlocked.Read(ref _unavailable);

    internal static void Reset()
    {
        lock (Gate) { Resolved.Clear(); _lastFrame = ulong.MaxValue; }
        Interlocked.Exchange(ref _stamped, 0); Interlocked.Exchange(ref _unavailable, 0);
    }

    /// <summary>Snapshot of every resolved frame since the last Reset.</summary>
    public static Sample[] Snapshot() { lock (Gate) return Resolved.ToArray(); }

    /// <summary>Most recent resolved frame, or false if none.</summary>
    public static bool Latest(out Sample sample)
    {
        lock (Gate)
        {
            if (Resolved.Count == 0) { sample = default; return false; }
            sample = Resolved[^1]; return true;
        }
    }

    internal static void PreDrawBegin()
    {
        if (!Enabled) return;
        var rd = RenderingServer.GetRenderingDevice();
        if (rd == null) { Interlocked.Increment(ref _unavailable); return; }
        try { Resolve(rd); } catch (System.Exception error) { Add(new(ulong.MaxValue, false, "resolve: " + error.Message, 0, 0, 0, 0, 0)); }
        rd.CaptureTimestamp(BeginName);
        Interlocked.Increment(ref _stamped);
    }

    internal static void PreDrawEnd()
    {
        if (!Enabled) return;
        RenderingServer.GetRenderingDevice()?.CaptureTimestamp(EndName);
    }

    private static void Add(Sample sample) { lock (Gate) Resolved.Add(sample); }

    private static void Resolve(RenderingDevice rd)
    {
        _verifiedDriver = RenderingServer.GetCurrentRenderingDriverName() == "vulkan";
        ulong frame = rd.GetCapturedTimestampsFrame();
        if (frame == _lastFrame) return; // nothing new resolved yet; the previous one is already stored
        _lastFrame = frame;
        uint count = System.Math.Min(rd.GetCapturedTimestampsCount(), 4096u);
        if (count == 0) { Add(new(frame, false, "no captured timestamps", 0, 0, 0, 0, 0)); return; }
        if (!_verifiedDriver) { Add(new(frame, false, "GPU timestamp units unverified on this driver", 0, 0, 0, 0, 0)); return; }
        int begins = 0, ends = 0; ulong bGpu = 0, eGpu = 0, bCpu = 0, eCpu = 0, first = ulong.MaxValue, last = 0;
        var passGpu = new List<ulong>();
        for (uint i = 0; i < count; i++)
        {
            string name = rd.GetCapturedTimestampName(i);
            ulong gpu = rd.GetCapturedTimestampGpuTime(i);
            if (gpu != 0) { if (gpu < first) first = gpu; if (gpu > last) last = gpu; }
            if (name == BeginName) { begins++; bGpu = gpu; bCpu = rd.GetCapturedTimestampCpuTime(i); }
            else if (name == EndName) { ends++; eGpu = gpu; eCpu = rd.GetCapturedTimestampCpuTime(i); }
            else if (name.StartsWith("speed.", System.StringComparison.Ordinal)
                || name.StartsWith("guo_light_presentation_", System.StringComparison.Ordinal)) passGpu.Add(gpu);
        }
        double viewport = 0;
        foreach (var rid in Viewports) viewport += RenderingServer.ViewportGetMeasuredRenderTimeGpu(rid);
        if (Viewports.Length == 0 || !double.IsFinite(viewport) || viewport <= 0)
        { Add(new(frame, false, "viewport GPU time not above zero", 0, 0, 0, 0, 0, viewport)); return; }
        if (begins != 1 || ends != 1) { Add(new(frame, false, $"markers begin={begins} end={ends}", 0, 0, 0, 0, 0)); return; }
        if (bGpu == 0 || eGpu < bGpu) { Add(new(frame, false, "begin/end order or zero", 0, 0, 0, 0, 0)); return; }
        int inside = 0; foreach (ulong t in passGpu) if (t >= bGpu && t <= eGpu) inside++;
        Add(new(frame, true, null, (eGpu - bGpu) / 1e6, eCpu >= bCpu ? (eCpu - bCpu) / 1000d : 0,
            (last - first) / 1e6, passGpu.Count, inside, viewport));
    }
}
#endif
