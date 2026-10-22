// SPDX-License-Identifier: BSD-2-Clause
using System.Runtime.CompilerServices;
using GUO.Game;
using GUO.Renderer.Weather;
using GUO.Renderer.Water;

namespace GUO.Renderer.WaterRain;

internal static class WaterRainIntegration
{
    private sealed class Context
    {
        internal readonly RainSurfaceQuery Query;
        internal WaterRipples Field;
        internal long Tick;
        internal int Map;
        internal Context(World world) => Query = new RainSurfaceQuery(world);
    }
    private static readonly ConditionalWeakTable<World, Context> States = new();

    // Called only by the styled-water field, after it follows the player and
    // before its existing step/upload. No extra simulation or texture is created.
    internal static void Update(World world, WaterMask mask, WaterRipples field,
        RainRippleEmitter emitter, float delta, bool jumped)
    {
        var settings = WeatherLab.For(world).Settings;
        WeatherLab.SyncProfile(settings, GUO.Configuration.ProfileManager.CurrentProfile);
        if (!settings.WaterDrops || field.Strength <= 0)
        {
            emitter.Reset();
            return;
        }
        var context = States.GetValue(world, value => new Context(value));
        context.Query.Mask = mask;
        context.Field = field;
        context.Tick = Time.Ticks;
        context.Map = world.MapIndex;
        emitter.Update(field, delta, RainAmount(settings, WeatherLab.RainIntensity(world)), !jumped,
            settings.DropBudget, world.Player.X, world.Player.Y, context.Query.ExposedWater);
    }

    internal static float RainAmount(WeatherSettings settings, float intensity) =>
        settings.WaterDrops ? intensity * System.Math.Clamp(settings.WaterDensity, 0, 100) / 100f : 0;

    /// <summary>Only this world's initialized styled frame may replace primitive water rings.</summary>
    internal static bool IsActive(World world) => world?.Player != null && world.Map != null
        && WaterRender.Flag(true) != 0 && States.TryGetValue(world, out var context)
        && context.Tick == Time.Ticks && context.Map == world.MapIndex
        && context.Field.OriginX != int.MinValue && context.Field.Strength > 0
        && WeatherLab.For(world).Settings.WaterDrops;
}
