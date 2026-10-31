// SPDX-License-Identifier: BSD-2-Clause
// Bounded, shared-load Debug mode comparison. Never a Release/FPS benchmark.
#if DEBUG
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using GUO.Network;
using GUO.Renderer.DepthWorld;
using Environment = System.Environment;

namespace GUO.Renderer.SpeedMode.PhysicalWorld;

public partial class NativePhysicalWorldProbe
{
    private const int PerfWarmup = 30, PerfSamples = 120, PerfGpuObservations = 8;
    private readonly List<object> _perfResults = new();
    private object _perfCacheDiagnostics;
    private readonly record struct PerfFrozen(uint Ticks, int Minute, int X, int Y, int Z,
        float Zoom, float OffsetX, float OffsetY, float XX, float XY, float YX, float YY, float OX, float OY);
    private sealed record PerfStamp(string Name, ulong CpuMicroseconds, ulong GpuRawValue, ulong? GpuNanoseconds);
    private sealed record PerfGpuFrame(ulong Frame, uint AvailableMarkers, List<PerfStamp> Markers);

    private PerfFrozen FrozenState()
    {
        var player = _game.UO.World.Player; var camera = _scene.Camera; var matrix = camera.ViewTransform;
        return new(Time.Ticks, _game.UO.World.ShardClock.MinuteAt(Time.Ticks), player.X, player.Y, player.Z,
            camera.Zoom, camera.Offset.X, camera.Offset.Y, matrix.X.X, matrix.X.Y, matrix.Y.X, matrix.Y.Y,
            matrix.Origin.X, matrix.Origin.Y);
    }

    private static object Distribution(double[] samples)
    {
        var sorted = (double[])samples.Clone(); Array.Sort(sorted);
        double Percentile(double p) => sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];
        return new { count = sorted.Length, mean = sorted.Average(), min = sorted[0], max = sorted[^1],
            p50 = Percentile(.50), p95 = Percentile(.95), p99 = Percentile(.99) };
    }

    private object PerfPassCounters()
    {
        var shadows = Shadows; var lights = Lights;
        return new {
            effectiveEnhanced = _scene.UseSpeedLights, effectiveRealtime = _scene.UseRealtimeLights,
            worldFramesDrawn = OrderedWorldCapture.FramesDrawn, worldFramesFallback = OrderedWorldCapture.FramesFallback,
            enhancedOfferCount = _scene.UseSpeedLights ? lights?.Count : null,
            selectedStructuralLamp = _scene.UseSpeedLights ? shadows?.StructuralLampOffer : null,
            nativeTriangles = shadows?.PhysicalWorld?.Triangles.Length,
            physicalFingerprint = shadows?.PhysicalWorld?.SourceFingerprint,
            physicalGeneration = shadows?.PhysicalWorld?.Key.Generation,
            cacheHits = _scene.SpeedPhysicalCacheHits, cacheBuilds = _scene.SpeedPhysicalCacheBuilds,
            sunReady = shadows?.StructuralSun?.Ready, sunTriangles = shadows?.StructuralSun?.TriangleCount,
            sunDepthDraws = shadows?.StructuralSun?.DepthDraws, sunUploads = shadows?.StructuralSun?.GeometryUploads,
            lampReady = shadows?.StructuralLamp?.Ready, lampTriangles = shadows?.StructuralLamp?.TriangleCount,
            lampDepthDraws = shadows?.StructuralLamp?.DepthDraws, lampUploads = shadows?.StructuralLamp?.GeometryUploads,
            shelterReady = shadows?.Shelter?.Ready, shelterDepthDraws = shadows?.Shelter?.DepthDraws,
            shelterUploads = shadows?.Shelter?.GeometryUploads
        };
    }

    private async Task<object> PerfMemory()
    {
        object deviceMemory = null; string deviceError = null;
        RenderingServer.CallOnRenderThread(Callable.From(() => {
            try {
                var rd = RenderingServer.GetRenderingDevice();
                if (rd == null) { deviceError = "No RenderingDevice"; return; }
                // Pinned GodotSharp.xml: GetMemoryUsage returns bytes (VMA statistics on Vulkan).
                deviceMemory = new { texturesBytes = rd.GetMemoryUsage(RenderingDevice.MemoryType.Textures),
                    buffersBytes = rd.GetMemoryUsage(RenderingDevice.MemoryType.Buffers),
                    totalBytes = rd.GetMemoryUsage(RenderingDevice.MemoryType.Total) };
            } catch (Exception error) { deviceError = error.Message; }
        }));
        await Painted();
        using var process = Process.GetCurrentProcess(); process.Refresh();
        var gc = GC.GetGCMemoryInfo();
        return new { processWorkingSetBytes = process.WorkingSet64, processPrivateMemoryBytes = process.PrivateMemorySize64,
            managedEstimatedLiveBytes = GC.GetTotalMemory(false), lastGcHeapSizeBytes = gc.HeapSizeBytes,
            lastGcFragmentedBytes = gc.FragmentedBytes, gcCommittedBytes = gc.TotalCommittedBytes,
            serverVideoBytes = RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.VideoMemUsed),
            serverTextureBytes = RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TextureMemUsed),
            serverBufferBytes = RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.BufferMemUsed),
            renderingDevice = deviceMemory, renderingDeviceError = deviceError,
            scope = "Process and engine-reported allocation counters; not total adapter VRAM residency. No forced GC." };
    }

    private async Task<object> PerfGpuWindow(Action draw)
    {
        string driver = RenderingServer.GetCurrentRenderingDriverName();
        bool gpuNanosecondsVerified = driver == "vulkan";
        var frames = new List<PerfGpuFrame>(); var seen = new HashSet<ulong>(); var errors = new List<string>();
        for (int observation = 0; observation < PerfGpuObservations; observation++) {
            PerfGpuFrame captured = null; string failure = null;
            draw();
            RenderingServer.CallOnRenderThread(Callable.From(() => {
                try {
                    var rd = RenderingServer.GetRenderingDevice();
                    if (rd == null) { failure = "No RenderingDevice"; return; }
                    uint count = rd.GetCapturedTimestampsCount(); var markers = new List<PerfStamp>();
                    // Results are delayed. Preserve the engine's captured frame ID;
                    // never attribute them to the current DrawFrame invocation.
                    ulong frame = rd.GetCapturedTimestampsFrame();
                    for (uint i = 0; i < Math.Min(count, 4096u); i++) {
                        string name = rd.GetCapturedTimestampName(i);
                        if (!name.StartsWith("speed.", StringComparison.Ordinal)
                            && !name.StartsWith("guo_light_presentation_", StringComparison.Ordinal)) continue;
                        ulong rawGpu = rd.GetCapturedTimestampGpuTime(i);
                        markers.Add(new(name, rd.GetCapturedTimestampCpuTime(i), rawGpu, gpuNanosecondsVerified ? rawGpu : null));
                    }
                    captured = new(frame, count, markers);
                } catch (Exception error) { failure = error.Message; }
            }));
            await Painted();
            if (failure != null) errors.Add(failure);
            if (captured != null && seen.Add(captured.Frame)) frames.Add(captured);
        }
        var samples = new List<(string Pass, ulong Frame, double CpuMs, double? GpuMs)>();
        foreach (var frame in frames) {
            var starts = new Dictionary<string, PerfStamp>();
            foreach (var stamp in frame.Markers) {
                bool begin = stamp.Name.EndsWith(".begin", StringComparison.Ordinal) || stamp.Name.EndsWith("_begin", StringComparison.Ordinal);
                bool end = stamp.Name.EndsWith(".end", StringComparison.Ordinal) || stamp.Name.EndsWith("_end", StringComparison.Ordinal);
                if (begin) starts[stamp.Name[..^6]] = stamp;
                else if (end && starts.Remove(stamp.Name[..^4], out var first)
                    && stamp.CpuMicroseconds >= first.CpuMicroseconds) {
                    // The pinned Vulkan implementation returns nanoseconds,
                    // contrary to the SDK XML. CPU timestamps remain microseconds.
                    double? gpu = first.GpuNanoseconds is ulong firstGpu && firstGpu != 0
                        && stamp.GpuNanoseconds is ulong lastGpu && lastGpu >= firstGpu
                        ? (lastGpu - firstGpu) / 1_000_000d : null;
                    samples.Add((stamp.Name[..^4], frame.Frame,
                        (stamp.CpuMicroseconds - first.CpuMicroseconds) / 1000d, gpu));
                }
            }
        }
        return new { observationDraws = PerfGpuObservations, uniqueCapturedFrames = frames.Count,
            window = "Separate untimed diagnostic window after the 120-frame sample; captured frame IDs are delayed and deduplicated.",
            driver, gpuNanosecondsVerified,
            units = "CPU raw timestamps: microseconds. Verified Vulkan GPU raw timestamps: nanoseconds. Other GPU drivers retain raw values with unverified units and no derived GPU duration. Pair deltas/distributions: milliseconds.",
            note = "Missing/cached depth passes are not executed or not observed, not zero lighting cost. Existing markers do not cover the whole light or soften pass.",
            passes = samples.GroupBy(s => s.Pass).Select(group => new { pass = group.Key, occurrences = group.Count(),
                capturedFrames = group.Select(s => s.Frame).Distinct().ToArray(),
                renderThreadCpuMs = Distribution(group.Select(s => s.CpuMs).ToArray()),
                gpuMs = group.Any(s => s.GpuMs.HasValue) ? Distribution(group.Where(s => s.GpuMs.HasValue).Select(s => s.GpuMs.Value).ToArray()) : null }).ToArray(),
            frames, errors };
    }

    private async Task<object> MeasurePerfMode(Action draw, bool enhanced, PerfFrozen expected)
    {
        _profile.LightingSpeedMode = enhanced; Mutated();
        for (int i = 0; i < PerfWarmup; i++) { draw(); await Painted(); }
        Require(FrozenState() == expected, "fixed native scene/camera/clock after mode warmup");
        Require(!_scene.UseRealtimeLights && _scene.UseSpeedLights == enhanced, "requested lighting mode admitted");
        if (enhanced) Require(Shadows.StructuralSun?.Ready == true && Shadows.StructuralLamp?.Ready == true
            && Shadows.Shelter?.Ready == true && Lights.Count >= 1, "sun/lamp/shelter actually admitted before timing");
        object memoryBefore = await PerfMemory(), passesBefore = PerfPassCounters();
        var snapshot = Shadows?.PhysicalWorld;
        var cpuMs = new double[PerfSamples]; var postDrawMs = new double[PerfSamples];
        var wallIntervalsMs = new double[PerfSamples - 1]; var drawAllocations = new double[PerfSamples];
        int thread = Environment.CurrentManagedThreadId; int[] gcBefore = { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
        long drawnBefore = OrderedWorldCapture.FramesDrawn, fallbackBefore = OrderedWorldCapture.FramesFallback;
        long sequence = SpeedModeBindings.Current.Sequence;
        long allocationStart = GC.GetAllocatedBytesForCurrentThread(); long previousPostDraw = 0;
        for (int i = 0; i < PerfSamples; i++) {
            Require(Environment.CurrentManagedThreadId == thread, "sample stays on one managed main thread");
            long allocated = GC.GetAllocatedBytesForCurrentThread(), start = Stopwatch.GetTimestamp();
            draw();
            long submitted = Stopwatch.GetTimestamp(), allocationEnd = GC.GetAllocatedBytesForCurrentThread();
            cpuMs[i] = (submitted - start) * 1000d / Stopwatch.Frequency;
            drawAllocations[i] = allocationEnd - allocated;
            await Painted();
            long painted = Stopwatch.GetTimestamp();
            Require(Environment.CurrentManagedThreadId == thread, "postdraw resumes on the measured main thread");
            postDrawMs[i] = (painted - start) * 1000d / Stopwatch.Frequency;
            if (i > 0) wallIntervalsMs[i - 1] = (painted - previousPostDraw) * 1000d / Stopwatch.Frequency;
            previousPostDraw = painted;
            if (enhanced) {
                var frame = SpeedModeBindings.Current;
                Require(frame.LightTarget != null && frame.Shelter != null && frame.Sequence > sequence,
                    "every measured enhanced draw publishes a fresh light/shelter frame");
                sequence = frame.Sequence;
            }
        }
        long phaseAllocated = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
        int[] gcAfter = { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
        Require(FrozenState() == expected, "sample preserves frozen native scene/camera/clock");
        Require(OrderedWorldCapture.FramesDrawn - drawnBefore == PerfSamples
            && OrderedWorldCapture.FramesFallback == fallbackBefore, "all measured Depth World draws admitted without fallback");
        if (enhanced) Require(ReferenceEquals(snapshot, Shadows.PhysicalWorld), "stationary physical snapshot reused throughout timed sample");
        object passesAfter = PerfPassCounters(), memoryAfter = await PerfMemory();
        object gpu = await PerfGpuWindow(draw);
        return new { mode = enhanced ? "Enhanced lighting" : "Classic lighting with the same Depth World native renderer",
            warmupFrames = PerfWarmup, sampledFrames = PerfSamples, frozen = expected,
            cpuDrawSubmissionMs = Distribution(cpuMs), postDrawToPostDrawWallMs = Distribution(wallIntervalsMs),
            drawStartToPostDrawWallMs = Distribution(postDrawMs), mainThreadDrawAllocatedBytes = Distribution(drawAllocations),
            mainThreadPhaseAllocatedBytesIncludingHarness = phaseAllocated, verifiedManagedThread = thread,
            collectionCountDelta = gcAfter.Zip(gcBefore, (after, before) => after - before).ToArray(),
            memoryBefore, memoryAfter, passesBefore, passesAfter, gpu,
            samples = new { cpuDrawSubmissionMs = cpuMs, postDrawToPostDrawWallMs = wallIntervalsMs,
                drawStartToPostDrawWallMs = postDrawMs, mainThreadDrawAllocatedBytes = drawAllocations } };
    }

    private async Task RunPerformanceCases()
    {
        object fixture = null; string failure = null;
        try {
            foreach (var item in _items) if (!item.IsDestroyed) item.Destroy();
            int highestGround = _z;
            for (int y = -4; y <= 4; y++) for (int x = -4; x <= 4; x++) {
                _game.UO.World.Map.GetMapZ(X + x, Y + y, out sbyte land, out _); highestGround = Math.Max(highestGround, land);
            }
            _z = highestGround + 8;
            for (int y = -4; y <= 4; y++) for (int x = -4; x <= 4; x++) Place(0x0495, X + x, Y + y, _z);
            ushort wall = FindWall(); for (int y = -1; y <= 1; y++) Place(wall, X + 1, Y + y, _z);
            var roof = FindRoof();
            for (int y = 0; y <= 2; y++) for (int x = 0; x <= 2; x++)
                Place(roof.Graphic, X + 1 + x, Y + 1 + y, _z + 25 + x * roof.Gx + y * roof.Gy);
            Place(0x0B1D, X - 1, Y, _z);
            _game.UO.World.Player.SetInWorldTile(X - 1, Y - 1, (sbyte)_z);
            _game.UO.World.ShardClock.Set(12, 0, 0, Time.Ticks);
            _game.UO.World.Light.Overall = 12; _game.UO.World.Light.Personal = 0;
            _profile.UseCustomLightLevel = false; _profile.RealtimeLights = false;
            _profile.SpeedModeSunShadows = true; _profile.SpeedModeLampShadows = 1; _profile.SpeedModeWeather = true;
            _profile.SpeedModeSoften = 0; _profile.DrawRoofs = false; Mutated();
            _scene.Camera.Update(true, 0, GUO.Input.Mouse.Position);
            var frozen = FrozenState(); var draw = (Action)_draw.CreateDelegate(typeof(Action), _game);
            fixture = new { scene = "Loaded native Britain map plus injected native Items; object counts below describe the injected fixture only.",
                x = X, y = Y, z = _z, facet = _game.UO.World.MapIndex, floorGraphic = 0x0495,
                floorCount = 81, wallGraphic = wall, wallCount = 3, roofGraphic = roof.Graphic, roofCount = 9,
                roof.Gx, roof.Gy, lampGraphic = 0x0B1D, lampCount = 1, frozen,
                precipitationCount = _game.UO.World.Weather.Count,
                settings = new { _profile.RealtimeLights, _profile.SpeedModeSunShadows, _profile.SpeedModeLampShadows,
                    _profile.SpeedModeWeather, _profile.SpeedModeSoften, _profile.SpeedModeNightFloor,
                    _profile.SpeedModeWindowLight, _profile.SpeedModeFog, _profile.SpeedModeReflections,
                    _profile.SpeedModeMorningHaze, _profile.WindStrength, _profile.WaterStyle, _profile.DrawRoofs,
                    _profile.ShadowsEnabled, _profile.ShadowsStatics, _profile.UseCustomLightLevel,
                    nativeOverall = 12, nativePersonal = 0 },
                runtime = GUO.Host.PerfRuntimeIdentity.SceneSnapshot(),
                renderer = RenderingServer.GetCurrentRenderingMethod(), driver = RenderingServer.GetCurrentRenderingDriverName(),
                engineVersion = Engine.GetVersionInfo()["string"].AsString(),
                clientMvid = typeof(NativePhysicalWorldProbe).Assembly.ManifestModule.ModuleVersionId,
                dotnet = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription };
            foreach (bool enhanced in new[] { false, true }) {
                _perfResults.Add(await MeasurePerfMode(draw, enhanced, frozen)); SavePerformance(fixture, null);
            }
            RunPerfCacheDiagnostics();
            // Restore ordinary producer custody after the isolated direct-cache
            // queries and deletion of their temporary wall. Outside all timings.
            Mutated(); draw(); await Painted();
            Require(FrozenState() == frozen, "cache diagnostic leaves fixed scene camera/clock intact");
            Require(!NetClient.Socket.IsConnected, "performance fixture remains disconnected");
        } catch (Exception error) { failure = error.ToString(); throw; }
        finally { SavePerformance(fixture, failure); }
    }

    private void SavePerformance(object fixture, string failure) => File.WriteAllText(
        Path.Combine(_root, "data", "native-physical-performance.json"), JsonSerializer.Serialize(new {
            diagnostic = "Shared-load Debug Classic/enhanced mode comparison; not isolated physical-pass cost, Release performance or FPS.",
            protocol = "Fixed order Classic then enhanced; warmup30/sample120 each; separate8-draw GPU observation window each.",
            timing = "CPU is direct DrawFrame submission; 119 complete postdraw-to-postdraw intervals cover120 sampled draws and include engine pacing, scheduling and harness awaits. No image/texture/buffer readback or GPU profiling/memory queries inside timed loops.",
            percentiles = "Nearest rank: sorted[ceil(p*N)-1]. CPU and wall units milliseconds; allocation/memory units bytes.",
            cachedWork = "Standing scene reuses structural depth; missing GPU depth markers are not zero shading cost. Overall light/soften GPU duration is not instrumented by existing markers.",
            sdkReference = "GodotSharp 4.7.2 XML is correct for CPU microseconds and memory bytes, but incorrectly labels GPU timestamps microseconds. Official pinned ed1daf0bf Vulkan implementation returns nanoseconds; GPU conversion is driver-guarded.",
            gpuImplementationReferences = new[] {
                "https://github.com/godotengine/godot/blob/ed1daf0bf/drivers/vulkan/rendering_device_driver_vulkan.cpp#L6107-L6136",
                "https://github.com/godotengine/godot/blob/ed1daf0bf/servers/rendering/rendering_device.cpp#L7987-L7995" },
            fixture, phases = _perfResults, directCache = _perfCacheDiagnostics, failure }, new JsonSerializerOptions { WriteIndented = true }));
}
#endif
