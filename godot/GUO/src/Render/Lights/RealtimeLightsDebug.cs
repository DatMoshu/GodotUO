// SPDX-License-Identifier: BSD-2-Clause
// GUO-owned (ADR-0039): debug output for Realtime lights, off unless asked for.

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Godot;

namespace GUO.Renderer.Lights
{
    /// <summary>
    /// The light budget log and the light dump, both off unless their
    /// environment variable is set (read once, at first use):
    /// <list type="bullet">
    /// <item><c>GUO_LIGHTS_BUDGET=1</c>: one line per frame with the light count and the
    /// cost of each stage in ms, and a 60-frame summary (average and worst).</item>
    /// <item><c>GUO_LIGHTS_DUMP=DIR[;FRAMES[;EVERY]]</c>: writes the light grid as text
    /// (<see cref="RealtimeLightField.DumpAscii"/>) plus the pixel-to-world readout
    /// under the mouse and at the player, every EVERY-th frame (default 30) until
    /// FRAMES files are written (default 10), as <c>DIR\lights_NNNNNN.txt</c>.</item>
    /// </list>
    /// </summary>
    internal static class RealtimeLightsDebug
    {
        public const string BudgetKey = "GUO_LIGHTS_BUDGET";
        public const string DumpKey = "GUO_LIGHTS_DUMP";
        public const int SummaryFrames = 60;

        private static bool _read;
        private static bool _budget;
        private static string _dumpDir;
        private static int _dumpLeft, _dumpEvery = 30;
        private static long _frame;

        // Rolling 60-frame window.
        private static int _n;
        private static double _sumTotal, _maxTotal;
        private static int _sumLights, _maxLights;

        /// <summary>The last frame's budget, whatever the flag (cheap: five numbers).</summary>
        public static Budget Last { get; private set; }

        public readonly struct Budget
        {
            public readonly int Lights, Temporary, Computed;
            public readonly double GridMs, FieldMs, DrawMs;

            public Budget(int lights, int temporary, int computed, double gridMs, double fieldMs, double drawMs)
            {
                Lights = lights;
                Temporary = temporary;
                Computed = computed;
                GridMs = gridMs;
                FieldMs = fieldMs;
                DrawMs = drawMs;
            }

            public double TotalMs => GridMs + FieldMs + DrawMs;
        }

        public static bool BudgetEnabled
        {
            get
            {
                Read();
                return _budget;
            }
        }

        /// <summary>For probes: set the flags as if from the environment.</summary>
        internal static void Configure(bool budget, string dumpSpec)
        {
            _read = true;
            _budget = budget;
            ParseDump(dumpSpec);
            _frame = 0;
            _n = 0;
            _sumTotal = _maxTotal = 0;
            _sumLights = _maxLights = 0;
        }

        private static void Read()
        {
            if (_read) return;
            _read = true;
            _budget = System.Environment.GetEnvironmentVariable(BudgetKey) is "1" or "true";
            ParseDump(System.Environment.GetEnvironmentVariable(DumpKey));
        }

        private static void ParseDump(string spec)
        {
            _dumpDir = null;
            _dumpLeft = 0;
            _dumpEvery = 30;
            if (string.IsNullOrWhiteSpace(spec)) return;
            string[] parts = spec.Split(';');
            _dumpDir = parts[0].Trim();
            _dumpLeft = parts.Length > 1 && int.TryParse(parts[1], out int f) ? Math.Max(0, f) : 10;
            _dumpEvery = parts.Length > 2 && int.TryParse(parts[2], out int e) ? Math.Max(1, e) : 30;
        }

        /// <summary>A stopwatch reading, for <see cref="Ms"/>.</summary>
        public static long Stamp() => Stopwatch.GetTimestamp();

        public static double Ms(long from, long to) => (to - from) * 1000.0 / Stopwatch.Frequency;

        /// <summary>Record a frame; logs it when the budget flag is on. Returns the line logged, or null.</summary>
        public static string Record(in Budget b)
        {
            Read();
            Last = b;
            _frame++;
            if (!_budget) return null;

            string line = string.Create(CultureInfo.InvariantCulture,
                $"[realtime-lights-budget] frame={_frame} lights={b.Lights} temp={b.Temporary} computed={b.Computed} " +
                $"grid={b.GridMs:0.000}ms field={b.FieldMs:0.000}ms draw={b.DrawMs:0.000}ms total={b.TotalMs:0.000}ms");
            Emit(line);

            _n++;
            _sumTotal += b.TotalMs;
            _maxTotal = Math.Max(_maxTotal, b.TotalMs);
            _sumLights += b.Lights;
            _maxLights = Math.Max(_maxLights, b.Lights);
            if (_n >= SummaryFrames)
            {
                Emit(string.Create(CultureInfo.InvariantCulture,
                    $"[realtime-lights-budget] summary frames={_n} lights avg={_sumLights / (double)_n:0.0} max={_maxLights} " +
                    $"total avg={_sumTotal / _n:0.000}ms max={_maxTotal:0.000}ms"));
                _n = 0;
                _sumTotal = _maxTotal = 0;
                _sumLights = _maxLights = 0;
            }

            return line;
        }

        /// <summary>Whether this frame should be dumped (the dump flag, its count and its interval).</summary>
        public static bool DumpDue()
        {
            Read();
            return _dumpDir != null && _dumpLeft > 0 && _frame % _dumpEvery == 0;
        }

        /// <summary>Write one dump; returns its path, or null when it failed (logged).</summary>
        public static string WriteDump(string text)
        {
            if (_dumpDir == null || _dumpLeft <= 0) return null;
            try
            {
                Directory.CreateDirectory(_dumpDir);
                string path = Path.Combine(_dumpDir, $"lights_{_frame:000000}.txt");
                File.WriteAllText(path, text);
                _dumpLeft--;
                Emit($"[realtime-lights] dump {path}");
                return path;
            }
            catch (Exception e)
            {
                _dumpLeft = 0;
                Emit($"[realtime-lights] dump failed, dumps off: {e.Message}");
                return null;
            }
        }

        /// <summary>With a dump: the light target (last frame's) as <c>lights_NNNNNN.png</c> beside it.</summary>
        public static void WriteTargetImage(Texture2D texture)
        {
            if (_dumpDir == null || texture == null) return;
            try
            {
                using Image image = texture.GetImage();
                if (image == null || image.IsEmpty()) return;
                image.SavePng(Path.Combine(_dumpDir, $"lights_{_frame:000000}.png"));
            }
            catch (Exception e)
            {
                Emit($"[realtime-lights] target image failed: {e.Message}");
            }
        }

        private static void Emit(string line)
        {
            GUO.Utility.Logging.Log.Trace(line);
            GD.Print(line);
        }
    }
}
