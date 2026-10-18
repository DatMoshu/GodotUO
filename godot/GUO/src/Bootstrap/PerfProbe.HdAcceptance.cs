// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Godot;
using GUO.Renderer.PostFx;

namespace GUO.Host;

internal static partial class PerfProbe
{
    private static bool _hdAcceptance;

    // Pure clock/sample contract also exercised with a synthetic monotonic clock.
    internal sealed class AcceptanceWindow(double startedMs)
    {
        internal int Samples { get; private set; }
        internal bool Ready(double nowMs) => double.IsFinite(nowMs) && nowMs - startedMs >= 10000;
        internal void Sample(double nowMs)
        {
            if (!Ready(nowMs)) throw new InvalidOperationException("Acceptance warmup is incomplete");
            Samples++;
        }
        internal bool Complete => Samples >= 600;
    }

    internal sealed class ViewportMetric
    {
        private readonly Func<Rid?> _resolve;
        private readonly Rid? _id;
        private double _cpu, _gpu;
        private int _cpuN, _gpuN;
        internal object CpuMean => _cpuN > 0 ? _cpu / _cpuN : null;
        internal object GpuMean => _gpuN > 0 ? _gpu / _gpuN : null;
        internal ViewportMetric(Func<Rid?> resolve)
        {
            _resolve = resolve; _id = resolve();
            if (_id is Rid rid && rid.IsValid) RenderingServer.ViewportSetMeasureRenderTime(rid, true);
        }
        internal void Add(double cpu, double gpu)
        {
            if (double.IsFinite(cpu) && cpu > 0) { _cpu += cpu; _cpuN++; }
            if (double.IsFinite(gpu) && gpu > 0) { _gpu += gpu; _gpuN++; }
        }
        internal void Sample()
        {
            if (DisplayServer.GetName() == "headless" || _id is not Rid rid || !rid.IsValid || _resolve() != _id) return;
            Add(RenderingServer.ViewportGetMeasuredRenderTimeCpu(rid), RenderingServer.ViewportGetMeasuredRenderTimeGpu(rid));
        }
        internal Dictionary<string, object> Report() => new()
        {
            ["viewport_rid"] = _id?.Id, ["cpu_ms"] = CpuMean, ["gpu_ms"] = GpuMean,
            ["cpu_samples"] = _cpuN, ["gpu_samples"] = _gpuN,
            ["status"] = _id == null ? "absent" : _cpuN == 0 && _gpuN == 0 ? "unsupported/no-positive-samples" : "measured"
        };
    }

    private sealed class AcceptanceCapture(Node host)
    {
        private static double Now => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
        private readonly double _start = Now;
        private readonly AcceptanceWindow _window = new(Now);
        internal readonly ViewportMetric Root = new(() => host.GetViewport().GetViewportRid());
        private readonly ViewportMetric _light = new(() => (Client.Game?.Scene as GUO.Game.Scenes.GameScene)?.ProbeLightViewport);
        private readonly ViewportMetric _postFx = new(() => PostFxStack.Instance.ProbeViewport);
        internal double WarmupMs { get; private set; }
        internal bool Ready => _window.Ready(Now);
        internal void Begin() => WarmupMs = Now - _start;
        internal void Sample() { _window.Sample(Now); Root.Sample(); _light.Sample(); _postFx.Sample(); }
        internal Dictionary<string, object> Report(double elapsed, int samples)
        {
            if (!_window.Complete || samples != _window.Samples) throw new InvalidOperationException("Incomplete acceptance sample window");
            return AcceptanceSummary(WarmupMs, elapsed, samples,
                new Dictionary<string, object> { ["root"] = Root.Report(), ["light"] = _light.Report(), ["postfx"] = _postFx.Report() });
        }
    }

    internal static Dictionary<string, object> AcceptanceSummary(double warmup, double elapsed, int samples, Dictionary<string, object> viewports)
    {
        if (!double.IsFinite(warmup) || warmup < 10000 || !double.IsFinite(elapsed) || elapsed <= 0 || samples < 600)
            throw new InvalidOperationException("Invalid acceptance report window");
        return new() { ["warmup_elapsed_ms"] = warmup, ["sample_elapsed_ms"] = elapsed, ["sample_count"] = samples, ["viewports"] = viewports };
    }

    private static async Task<AcceptanceCapture> WarmAcceptance(Node host)
    {
        var capture = new AcceptanceCapture(host);
        while (!capture.Ready) await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        capture.Begin();
        return capture;
    }

    private static Dictionary<string, object> AcceptanceDevice() => new()
    {
        ["gpu"] = RenderingServer.GetVideoAdapterName(), ["vendor"] = RenderingServer.GetVideoAdapterVendor(),
        ["backend"] = RenderingServer.GetCurrentRenderingMethod(), ["driver"] = RenderingServer.GetCurrentRenderingDriverName(),
        ["api_version"] = RenderingServer.GetVideoAdapterApiVersion(),
        ["driver_info"] = DisplayServer.GetName() == "headless" ? null : OS.GetVideoAdapterDriverInfo(),
        ["display_server"] = DisplayServer.GetName()
    };
}
