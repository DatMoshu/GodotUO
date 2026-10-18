// GUO-owned (HD light Phase 5): the weather-aware light state.
//
// The torch/GI/sun pipeline (Phases 1-4) is weather-blind: a storm darkens
// nothing and thunder moves no light. This class folds the existing Weather
// system into the HD composite without touching the world draw:
//
// - Rain/storm dims the ambient (the realistic viewport's CanvasModulate,
//   via RealisticLightViewport.WeatherDim) and the sun (the stack scales
//   sun_energy by SunDimFactor, so storm clouds dim daylight with no shader
//   change).
// - Wetness (smoothed, slow to dry) reaches the shaders as hd_wet / gi_wet:
//   the tonemap darkens albedo slightly and adds a torch-specular term on
//   damp ground, the bloom lowers its hotspot threshold a little, and the GI
//   bounce strengthens â€” wet ground reflects torchlight.
// - Lightning flashes drive a REAL ambient/flash term wired to thunder
//   timing (Weather.LastThunderTicks, stamped in PlayThunder): a fast
//   double-pulse envelope that lifts the viewport ambient AND the torch
//   energies (RealisticLightViewport.WeatherFlash) and adds a cool flash
//   term in hd_tonemap (hd_flash). Not the lightning.gdshader tint, which
//   is a TIME-sine full-screen mix on its own clock.
//
// All fields are plain statics like HdSun, read per-frame by the GameScene
// (Update) and the PostFx stack (pushes). All shader uniforms default to 0,
// and every weather block is guarded, so 0 is bit-identical to Phase 4.
// Classic (HD off) never updates this and never builds the viewport either
// way, so the off-state is pixel-identical.
//
// Time source: GUO.Time.Ticks (ms, same epoch as the flicker clock and the
// shader TIME epoch). Smoothing uses the real dt the GameScene hands over,
// clamped like the sun clock.

using System;
using GUO.Game;

namespace GUO.Renderer.Lights
{
    internal static class HdWeather
    {
        /// <summary>Smoothed wetness 0..1: wets fast, dries slow.</summary>
        public static float Wetness;

        /// <summary>Smoothed storm dim 0..1: follows the clouds both ways.</summary>
        public static float Dim;

        /// <summary>Lightning envelope 0..1, recomputed every update from thunder timing.</summary>
        public static float Flash;

        /// <summary>Wetting rate per second toward the target.</summary>
        public const float WetRate = 0.6f;

        /// <summary>Drying rate per second back toward dry.</summary>
        public const float DryRate = 0.04f;

        /// <summary>Cloud-dim follow rate per second.</summary>
        public const float DimRate = 2f;

        /// <summary>How long after thunder the flash envelope matters (seconds).</summary>
        public const float FlashWindow = 2f;

        public static void Reset()
        {
            Wetness = 0f;
            Dim = 0f;
            Flash = 0f;
        }

        /// <summary>
        /// One frame: smooth wet/dim toward the weather target, recompute the
        /// flash envelope from thunder timing. Call when the HD toggle is on;
        /// Classic never calls it.
        /// </summary>
        public static void Update(World world, double dtSeconds)
        {
            float dt = (float)Math.Clamp(dtSeconds, 0.0, 5.0);
            uint now = GUO.Time.Ticks;

            WeatherType? current = null;
            byte count = 0;
            uint thunder = 0;

            try
            {
                Weather w = world?.Weather;
                if (w != null)
                {
                    current = w.CurrentWeather;
                    count = w.CurrentCount;
                    thunder = w.LastThunderTicks;
                }
            }
            catch
            {
            }

            float wetTarget = TargetWet(current, count);
            float dimTarget = TargetDim(current, count);

            float rate = wetTarget > Wetness ? WetRate : DryRate;
            Wetness = MoveToward(Wetness, wetTarget, rate * dt);
            Dim = MoveToward(Dim, dimTarget, DimRate * dt);

            float age = thunder == 0 ? float.PositiveInfinity : (now - thunder) / 1000f;
            Flash = FlashEnvelope(age);
        }

        /// <summary>Pure function of the weather: how wet the ground wants to be.</summary>
        public static float TargetWet(WeatherType? current, byte count)
        {
            if (!current.HasValue || count == 0)
            {
                return 0f;
            }

            return current.Value switch
            {
                WeatherType.WT_RAIN => 1f,
                WeatherType.WT_STORM_APPROACH => 1f,
                WeatherType.WT_STORM_BREWING => 0.8f,
                WeatherType.WT_SNOW => 0.25f,
                _ => 0f,
            };
        }

        /// <summary>Pure function of the weather: how much the clouds dim the light.</summary>
        public static float TargetDim(WeatherType? current, byte count)
        {
            if (!current.HasValue || count == 0)
            {
                return 0f;
            }

            return current.Value switch
            {
                WeatherType.WT_STORM_APPROACH => 0.6f,
                WeatherType.WT_STORM_BREWING => 0.55f,
                WeatherType.WT_RAIN => 0.35f,
                WeatherType.WT_SNOW => 0.15f,
                _ => 0f,
            };
        }

        /// <summary>
        /// Pure function of thunder age: a fast double-pulse strike. 1 at the
        /// clap, ~0.1 by half a second, silent past the window. Infinity (no
        /// thunder yet) is 0.
        /// </summary>
        public static float FlashEnvelope(float ageSeconds)
        {
            if (float.IsNaN(ageSeconds) || float.IsInfinity(ageSeconds) || ageSeconds < 0f || ageSeconds > FlashWindow)
            {
                return 0f;
            }

            float first = MathF.Exp(-ageSeconds * 5f);
            float second = ageSeconds > 0.09f ? 0.55f * MathF.Exp(-(ageSeconds - 0.09f) * 5f) : 0f;
            return Math.Clamp(Math.Max(first, second), 0f, 1f);
        }

        /// <summary>
        /// The viewport ambient with weather folded in: clouds dim, lightning
        /// lifts. Pure, probe-safe. Allows above 1: the HDR viewport carries
        /// the flash like a torch hotspot.
        /// </summary>
        public static float ApplyAmbient(float ambient)
        {
            float dimmed = ambient * (1f - 0.38f * Math.Clamp(Dim, 0f, 1f));
            return Math.Clamp(dimmed + Math.Clamp(Flash, 0f, 1f) * 0.9f, 0f, 1.5f);
        }

        /// <summary>The sun scale under clouds. Pure, probe-safe: 1 when clear.</summary>
        public static float SunDimFactor() => 1f - 0.6f * Math.Clamp(Dim, 0f, 1f);

        private static float MoveToward(float from, float to, float maxDelta) =>
            Math.Abs(to - from) <= maxDelta ? to : from + Math.Sign(to - from) * maxDelta;

    }
}
