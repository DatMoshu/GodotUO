// GUO-owned (HD light Phase 3): the sun directional light's state.
// World shaders stay unshaded (ADR-0001): the sun is a screen-space
// directional term in the hd_sun PostFx pass, driven per-frame from here,
// composited over the world albedo before hd_tonemap folds in the torch
// light buffer. The light buffer (and its bloom/heat/shaft thresholds,
// tuned for torch hotspots) is untouched.
//
// Time source: the shard sends no clock. Packet 0x5B (SetTime) arrives but
// PacketHandlers.SetTime is an empty stub, and the only light info on the
// wire is the 0x4F global level (0 bright .. 30 dark) plus 0x4E personal.
// So the hour is manual (Options slider, -hd_sun command, --hd-sun flag)
// with an opt-in auto-cycle; it never follows the shard.

using System;
using Godot;

namespace GUO.Renderer.Lights
{
    /// <summary>
    /// Time-of-day sun state for the hd_sun pass. All fields are plain
    /// statics so the PostFx stack (no world) and the GameScene (world)
    /// read the same hour; the profile owns persistence, the command and
    /// the --hd-sun flag own this run.
    /// </summary>
    internal static class HdSun
    {
        /// <summary>--hd-sun HOUR|auto|off: the hour for this run only (like PostFxStack.RunOverride).</summary>
        public static float? BootHour;

        /// <summary>--hd-sun auto: cycle for this run even if the profile says manual.</summary>
        public static bool BootAuto;

        /// <summary>--hd-sun off: sun term silent for this run (Phase 1/2 output).</summary>
        public static bool BootSilent;

        /// <summary>--hd-sun (any form) forces the HD foundation for the run: HDR targets + light viewport.</summary>
        public static bool ForceHd;

        /// <summary>
        /// Parse a --hd-sun value (HOUR | auto [hour] | off) into the boot
        /// statics. Any form forces the HD foundation for the run.
        /// </summary>
        public static void ApplyBoot(string value)
        {
            ForceHd = true;
            string v = (value ?? "").Trim().ToLowerInvariant();

            if (v == "auto")
            {
                BootAuto = true;
                return;
            }

            if (v.StartsWith("auto "))
            {
                BootAuto = true;
                v = v.Substring(5).Trim();
            }

            if (v == "off")
            {
                BootSilent = true;
                return;
            }

            if (float.TryParse(v, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float at))
            {
                BootHour = Math.Clamp(at, 0f, 23.99f);
            }
        }

        /// <summary>The hour shown, 0..24. Default noon: deterministic, never a surprise.</summary>
        public static float Hour = 12f;

        /// <summary>Opt-in cycle; off by default (ADR-0023: no temporal effects unless bound explicitly).</summary>
        public static bool Auto;

        /// <summary>
        /// The -hd_sun auto|manual command's vote: null follows the profile,
        /// set wins until the Options gump applies (which clears it back).
        /// </summary>
        public static bool? AutoSet;

        /// <summary>Game-hours per real second while cycling: a full day in four minutes.</summary>
        public const float AutoRate = 0.1f;

        /// <summary>Master sun term 0..1; 0 is bit-identical to Phase 1/2 (the shader crossfades to 1.0).</summary>
        public static float Strength = 1f;

        /// <summary>March taps this frame, set by the GameScene governor (desktop 10, mobile/zoom 6).</summary>
        public static int Steps = 10;

        /// <summary>Viewport pixels per Z unit this frame (4 px per Z at zoom 1), set by the GameScene.</summary>
        public static float PxPerZ = 4f;

        /// <summary>One evaluated hour: everything the hd_sun uniforms need.</summary>
        public readonly struct SunSample
        {
            /// <summary>Screen-space direction shadows fall toward (unit-ish, y slightly toward the viewer).</summary>
            public readonly Vector2 ShadowDir;
            public readonly Color Color;
            public readonly float Energy;
            /// <summary>Shadowed-floor tint: what unlit albedo falls back to.</summary>
            public readonly Color Floor;
            /// <summary>0 at the horizon, 1 at the zenith; the moon sits at 0.55.</summary>
            public readonly float Elevation;
            /// <summary>Ray climb in Z per marched pixel, from the elevation.</summary>
            public readonly float Slope;
            public readonly bool IsNight;

            public SunSample(Vector2 shadowDir, Color color, float energy, Color floor, float elevation, float slope, bool isNight)
            {
                ShadowDir = shadowDir;
                Color = color;
                Energy = energy;
                Floor = floor;
                Elevation = elevation;
                Slope = slope;
                IsNight = isNight;
            }
        }

        // The color-temperature curve: warm dawn, harsh noon, blue dusk/night.
        // Hour, sun RGB, energy, floor RGB. Piecewise-linear; 24 repeats 0.
        private static readonly float[,] Curve = new float[,]
        {
            {  0.00f, 0.32f, 0.40f, 0.64f, 0.38f, 0.085f, 0.095f, 0.140f },
            {  4.50f, 0.32f, 0.40f, 0.64f, 0.38f, 0.085f, 0.095f, 0.140f },
            {  5.50f, 0.45f, 0.50f, 0.75f, 0.35f, 0.050f, 0.050f, 0.080f },
            {  6.50f, 1.00f, 0.55f, 0.32f, 0.70f, 0.100f, 0.070f, 0.060f },
            {  8.00f, 1.00f, 0.85f, 0.68f, 1.00f, 0.160f, 0.130f, 0.110f },
            { 12.00f, 1.00f, 1.00f, 1.00f, 1.15f, 0.220f, 0.220f, 0.220f },
            { 16.00f, 1.00f, 0.92f, 0.78f, 1.05f, 0.200f, 0.190f, 0.180f },
            { 17.75f, 1.00f, 0.62f, 0.32f, 0.80f, 0.120f, 0.080f, 0.070f },
            { 19.00f, 0.50f, 0.42f, 0.68f, 0.42f, 0.060f, 0.055f, 0.090f },
            { 20.00f, 0.32f, 0.40f, 0.64f, 0.40f, 0.085f, 0.095f, 0.140f },
            { 24.00f, 0.32f, 0.40f, 0.64f, 0.38f, 0.085f, 0.095f, 0.140f },
        };

        /// <summary>Advance the clock when cycling; call once per frame with real seconds.</summary>
        public static void Advance(double dtSeconds)
        {
            if (!Auto || dtSeconds <= 0)
            {
                return;
            }

            Hour = (Hour + (float)dtSeconds * AutoRate) % 24f;
        }

        /// <summary>Pure function of the hour: the whole sun state, no engine calls (probe-safe).</summary>
        public static SunSample Evaluate(float hour)
        {
            float h = hour % 24f;
            if (h < 0)
            {
                h += 24f;
            }

            int rows = Curve.GetLength(0);
            int i = 0;
            while (i < rows - 2 && h >= Curve[i + 1, 0])
            {
                i++;
            }

            float h0 = Curve[i, 0];
            float h1 = Curve[i + 1, 0];
            float t = h1 > h0 ? Math.Clamp((h - h0) / (h1 - h0), 0f, 1f) : 0f;

            var color = new Color(
                Lerp(Curve[i, 1], Curve[i + 1, 1], t),
                Lerp(Curve[i, 2], Curve[i + 1, 2], t),
                Lerp(Curve[i, 3], Curve[i + 1, 3], t));
            float energy = Lerp(Curve[i, 4], Curve[i + 1, 4], t);
            var floor = new Color(
                Lerp(Curve[i, 5], Curve[i + 1, 5], t),
                Lerp(Curve[i, 6], Curve[i + 1, 6], t),
                Lerp(Curve[i, 7], Curve[i + 1, 7], t));

            // Sun path 5h..19h so the direction is continuous through dawn
            // and dusk (the color already says night there); the moon holds
            // a fixed south-east the rest of the night.
            bool night = h < 5f || h > 19f;
            Vector2 dir;
            float elev;
            if (!night)
            {
                float u = (h - 5f) / 14f;
                float sunX = MathF.Cos(u * MathF.PI); // +1 east at 5h .. -1 west at 19h
                elev = Math.Clamp(MathF.Sin(u * MathF.PI), 0.02f, 1f);
                dir = Norm(new Vector2(-sunX, 0.35f));
            }
            else
            {
                elev = 0.55f;
                dir = Norm(new Vector2(-0.55f, 0.35f));
            }

            // Ray climb: near-zenith climbs ~1.4 Z/px (only tall close
            // casters shadow: short noon shadows), dawn skims at ~0.02 Z/px
            // (long shadows). Matches the height buffer's Z units.
            float slope = Math.Clamp(MathF.Tan(elev * 1.396f) / 4f, 0.01f, 1.5f);

            return new SunSample(dir, color, energy, floor, elev, slope, night);
        }

        private static float Lerp(float a, float b, float t) => a + (b - a) * t;

        private static Vector2 Norm(Vector2 v)
        {
            float len = v.Length();
            return len > 1e-4f ? v / len : new Vector2(-1f, 0.35f);
        }

        /// <summary>
        /// Typed in chat as a client command (the "-" prefix, like -postfix):
        /// -hd_sun 8.5 | -hd_sun auto [rate] | -hd_sun manual | -hd_sun off | -hd_sun on | -hd_sun.
        /// </summary>
        public static void RunCommand(string[] args)
        {
            string verb = args.Length > 1 ? args[1].ToLowerInvariant() : "";
            string rest = args.Length > 2 ? string.Join(' ', args, 2, args.Length - 2) : "";

            switch (verb)
            {
                case "":
                    SunSample s = Evaluate(Hour);
                    Say($"hour {Hour:0.00} {(Auto ? "cycling" : "manual")} " +
                        $"rgb({s.Color.R:0.00},{s.Color.G:0.00},{s.Color.B:0.00}) x{s.Energy:0.00} " +
                        $"{(s.IsNight ? "moon" : $"elev {s.Elevation:0.00}")} strength {Strength:0.00}");
                    break;
                case "auto":
                    Auto = true;
                    AutoSet = true;
                    if (float.TryParse(rest, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float hours) && hours > 0)
                    {
                        Hour = hours % 24f;
                    }
                    Say($"sun cycling from {Hour:0.00} (a day in four minutes; -hd_sun manual to hold)");
                    break;
                case "manual":
                    Auto = false;
                    AutoSet = false;
                    if (float.TryParse(rest, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float held))
                    {
                        Hour = Math.Clamp(held, 0f, 23.99f);
                    }
                    Say($"sun held at {Hour:0.00}");
                    break;
                case "off":
                    Strength = 0f;
                    Say("sun off (Phase 1/2 light)");
                    break;
                case "on":
                    Strength = 1f;
                    Say("sun on");
                    break;
                default:
                    if (float.TryParse(verb, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float at))
                    {
                        Auto = false;
                        Hour = Math.Clamp(at, 0f, 23.99f);
                        Say($"sun held at {Hour:0.00}");
                    }
                    else
                    {
                        Say("-hd_sun HOUR|auto [hour]|manual [hour]|off|on");
                    }
                    break;
            }
        }

        private static void Say(string text)
        {
            Game.GameActions.Print(GUO.Client.Game?.UO?.World, "[hd_sun] " + text);
        }
    }
}
