// SPDX-License-Identifier: BSD-2-Clause
using System;
using Godot;
using GUO.Renderer;
using GUO.Utility;

namespace GUO.Host;

internal static partial class PerfProbe
{
    // Measurement controls must also be restored when an awaited probe fails.
    internal sealed class MeasurementState : IDisposable
    {
        private readonly int _maxFps = Engine.MaxFps;
        private readonly DisplayServer.VSyncMode _vsync = DisplayServer.WindowGetVsyncMode();
        private readonly bool _stages = PerfStages.Enabled, _runtime = RuntimeTimings.Enabled;
        private readonly float[] _interval, _kept;
        private bool _restored;

        internal MeasurementState(float[] interval = null)
        {
            _interval = interval ?? (Client.Game == null ? null : typeof(GameController)
                .GetField("_intervalFixedUpdate", System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.NonPublic)?.GetValue(Client.Game) as float[]);
            _kept = _interval == null ? null : (float[])_interval.Clone();
        }

        internal void Uncap()
        {
            DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
            Engine.MaxFps = 0;
            // Not zero: upstream wraps elapsed time with %=; x % 0 is NaN.
            if (_interval != null) Array.Fill(_interval, 0.001f);
        }

        public void Dispose()
        {
            if (_restored) return;
            _restored = true;
            if (_interval != null) Array.Copy(_kept, _interval, _kept.Length);
            PerfStages.Enabled = _stages;
            RuntimeTimings.Enabled = _runtime;
            Engine.MaxFps = _maxFps;
            DisplayServer.WindowSetVsyncMode(_vsync);
        }
    }

    // Passive observation: never issues movement. Distances on the same map
    // are observed XY displacement, including corrections, not verified steps.
    internal sealed class ObservedRoute
    {
        private readonly int[] _points;
        private int _count, _map, _x, _y, _z;
        private bool _observed;
        internal int Tiles { get; private set; }
        internal int Dropped { get; private set; }
        internal ObservedRoute(int capacity) => _points = new int[capacity * 4];

        internal void Observe(int map, int x, int y, int z)
        {
            if (_observed && map == _map && x == _x && y == _y && z == _z) return;
            if (_observed && map == _map) Tiles += Math.Max(Math.Abs(x - _x), Math.Abs(y - _y));
            _observed = true;
            _map = map; _x = x; _y = y; _z = z;
            if (_count * 4 >= _points.Length) { Dropped++; return; }
            int i = _count++ * 4;
            _points[i] = map; _points[i + 1] = x; _points[i + 2] = y; _points[i + 3] = z;
        }

        internal int[][] Points()
        {
            var result = new int[_count][];
            for (int i = 0; i < result.Length; i++)
                result[i] = new[] { _points[4 * i], _points[4 * i + 1], _points[4 * i + 2], _points[4 * i + 3] };
            return result;
        }
    }
}
