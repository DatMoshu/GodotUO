// SPDX-License-Identifier: BSD-2-Clause
using System;
using GUO.Game;
using GUO.Game.GameObjects;

namespace GUO.Renderer;

// Shared, toggle-independent upstream weather input, also used by Labs.
internal static class WeatherSignal
{
    internal static float RainIntensity(World world) => world?.Weather == null ? 0 :
        RainIntensity(world.Weather.CurrentWeather, world.Weather.Count);

    internal static float RainIntensity(WeatherType? type, int count) =>
        type is WeatherType.WT_RAIN or WeatherType.WT_STORM_BREWING
            ? Math.Clamp(count / 70f, 0, 1) : 0;
}
