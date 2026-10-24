// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace GUO.Host;

/// <summary>
/// Host CPU while a perf sample runs, recorded apart from launch and warm-up
/// (PERF-LANE-01). A frame time taken while another project compiles is not
/// a result for the code under test; the reader needs the load of the
/// sampled seconds, not of the whole run, to tell.
/// </summary>
/// <remarks>
/// A timer thread reads the system and own-process CPU times twice a second
/// (Windows GetSystemTimes and GetProcessTimes: no allocation, no I/O, never
/// on the main thread) and adds each reading to the current phase. "Other"
/// is the host's busy share minus this process's, i.e. the load the client
/// did not cause. Off unless a probe or PerfDump starts it. Elsewhere than
/// Windows it reports itself unavailable instead of guessing.
/// </remarks>
internal static class PerfHostLoad
{
    internal enum Phase { Launch, Warmup, Sample, Between }

    private sealed class Totals
    {
        public int Readings;
        public double BusySum, BusyMax, OtherSum, OtherMax;

        public void Add(double busy, double other)
        {
            Readings++;
            BusySum += busy;
            OtherSum += other;
            BusyMax = Math.Max(BusyMax, busy);
            OtherMax = Math.Max(OtherMax, other);
        }

        public void Clear()
        {
            Readings = 0;
            BusySum = BusyMax = OtherSum = OtherMax = 0;
        }
    }

    private const int PeriodMs = 500;
    private static readonly object Gate = new();
    private static readonly Totals[] Phases = { new(), new(), new(), new() };
    private static Timer _timer;
    private static Phase _phase = Phase.Launch;
    private static long _idle, _kernel, _user, _own;
    private static bool _primed;

    internal static bool Available => OperatingSystem.IsWindows();

    internal static string Source => Available ? $"GetSystemTimes/GetProcessTimes every {PeriodMs} ms" : "unavailable";

    /// <summary>Starts sampling in the launch phase; a second call does nothing.</summary>
    internal static void Start()
    {
        if (!Available || _timer != null) return;
        _timer = new Timer(_ => Read(), null, 0, PeriodMs);
    }

    internal static void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>Readings from now on count toward <paramref name="phase"/>.</summary>
    internal static void Enter(Phase phase)
    {
        lock (Gate) _phase = phase;
    }

    /// <summary>
    /// The warm-up and sample phases since the last call, as report fields,
    /// then clears both; launch accumulates until <see cref="Launch"/>.
    /// </summary>
    internal static Dictionary<string, object> TakeScene()
    {
        lock (Gate)
        {
            Totals warm = Phases[(int)Phase.Warmup], sample = Phases[(int)Phase.Sample];
            var r = new Dictionary<string, object>
            {
                ["host_cpu_sample_readings"] = sample.Readings,
                ["host_cpu_sample_mean_pct"] = Mean(sample.BusySum, sample.Readings),
                ["host_cpu_sample_max_pct"] = Round(sample.BusyMax, sample.Readings),
                ["host_other_cpu_sample_mean_pct"] = Mean(sample.OtherSum, sample.Readings),
                ["host_other_cpu_sample_max_pct"] = Round(sample.OtherMax, sample.Readings),
                ["host_cpu_warmup_max_pct"] = Round(warm.BusyMax, warm.Readings),
                ["host_other_cpu_warmup_max_pct"] = Round(warm.OtherMax, warm.Readings),
            };
            warm.Clear();
            sample.Clear();
            Phases[(int)Phase.Between].Clear();
            return r;
        }
    }

    /// <summary>Launch-phase fields: from the sampler's start to the first warm-up or sample.</summary>
    internal static Dictionary<string, object> Launch()
    {
        lock (Gate)
        {
            Totals launch = Phases[(int)Phase.Launch];
            return new Dictionary<string, object>
            {
                ["host_load_source"] = Source,
                ["host_cpu_launch_readings"] = launch.Readings,
                ["host_cpu_launch_max_pct"] = Round(launch.BusyMax, launch.Readings),
                ["host_other_cpu_launch_max_pct"] = Round(launch.OtherMax, launch.Readings),
            };
        }
    }

    private static object Mean(double sum, int n) => n > 0 ? Math.Round(sum / n, 1) : null;
    private static object Round(double value, int n) => n > 0 ? Math.Round(value, 1) : null;

    private static void Read()
    {
        if (!GetSystemTimes(out long idle, out long kernel, out long user)
            || !GetProcessTimes(GetCurrentProcess(), out _, out _, out long ownKernel, out long ownUser))
            return;
        long own = ownKernel + ownUser;
        lock (Gate)
        {
            if (_primed)
            {
                // Kernel time includes idle time on Windows.
                long total = (kernel - _kernel) + (user - _user);
                if (total > 0)
                {
                    double busy = Math.Clamp(100.0 * (total - (idle - _idle)) / total, 0, 100);
                    double ownShare = Math.Clamp(100.0 * (own - _own) / total, 0, 100);
                    Phases[(int)_phase].Add(busy, Math.Max(0, busy - ownShare));
                }
            }

            _idle = idle; _kernel = kernel; _user = user; _own = own;
            _primed = true;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
}
