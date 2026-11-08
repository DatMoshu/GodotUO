// SPDX-License-Identifier: BSD-2-Clause
// PORT DEVIATION (GUO): client-derived weather state, ADR-0048 section 5.
using System;
using System.Runtime.CompilerServices;
using GUO.Configuration;
using GUO.Game;

namespace GUO.Renderer.SpeedMode.Weather;

internal enum WetnessPhase { Dry, Wetting, Holding, Drying }

/// <summary>
/// Per-world visual state. Call Observe once or more per frame; a repeated tick
/// never integrates twice. The shard's existing weather remains authoritative.
/// </summary>
internal sealed class SpeedModeWeather
{
    private static readonly ConditionalWeakTable<World, SpeedModeWeather> States = new();
    private uint? _lastTick;
    internal float RainIntensity { get; private set; }
    internal float FogDensity { get; private set; }
    internal float Wetness { get; private set; }
    internal float Wind { get; private set; }
    internal WetnessPhase Phase { get; private set; }

    internal static SpeedModeWeather Observe(World world, Profile profile)
    {
        var state = States.GetValue(world, static _ => new SpeedModeWeather());
        uint tick = Time.Ticks;
        float delta = state._lastTick.HasValue ? unchecked(tick - state._lastTick.Value) / 1000f : 0f;
        state._lastTick = tick;
        state.Advance(world.Weather.CurrentWeather, world.Weather.Count,
            world.Weather.Wind, profile?.WindStrength ?? 0f, delta);
        return state;
    }

    // Transition table: target > wetness -> Wetting; target < wetness ->
    // Drying; equal nonzero -> Holding; equal zero -> Dry. A full-range
    // transition lasts 60 s up or 180 s down, independent of frame cadence.
    internal void Advance(WeatherType? type, int count, int wind, float windStrength, float seconds)
    {
        RainIntensity = Water.RainSurfaceQuery.RainIntensity(type, count);
        // ADR-0048: use enum names; snow (2) is not storm-brewing (3).
        // Morning haze is a separate constant preference added by the fog pass.
        FogDensity = type == WeatherType.WT_STORM_BREWING ? RainIntensity
            : type == WeatherType.WT_RAIN ? RainIntensity * 0.25f : 0f;
        float weatherWind = Math.Clamp(wind, -4, 4) / 4f;
        float strength = float.IsFinite(windStrength) ? Math.Clamp(windStrength, 0f, 1f) : 0f;
        Wind = weatherWind + strength * (weatherWind == 0f ? 0.4f : weatherWind);
        if (float.IsFinite(seconds) && seconds > 0f)
        {
            Wetness = RainIntensity > Wetness
                ? Math.Min(RainIntensity, Wetness + seconds / 60f)
                : Math.Max(RainIntensity, Wetness - seconds / 180f);
        }
        Phase = RainIntensity > Wetness ? WetnessPhase.Wetting
            : RainIntensity < Wetness ? WetnessPhase.Drying
            : Wetness > 0f ? WetnessPhase.Holding : WetnessPhase.Dry;
    }
}
