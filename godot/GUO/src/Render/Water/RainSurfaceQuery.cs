// SPDX-License-Identifier: BSD-2-Clause
// PORT DEVIATION (GUO): shared rain input for opt-in lighting water (ADR-0037).
using System;
using System.Runtime.CompilerServices;
using GUO.Game;
using GUO.Game.GameObjects;

namespace GUO.Renderer.Water;

internal sealed class RainSurfaceQuery
{
    private sealed class LightingContext
    {
        internal readonly RainSurfaceQuery Query;
        internal WaterRipples Field;
        internal long Tick = -1;
        internal int Map = -1;
        internal LightingContext(World world) => Query = new RainSurfaceQuery(world);
    }
    private static readonly ConditionalWeakTable<World, LightingContext> LightingStates = new();

    internal static float RainIntensity(WeatherType? type, int count) =>
        type is WeatherType.WT_RAIN or WeatherType.WT_STORM_BREWING
            ? Math.Clamp(count / 70f, 0f, 1f) : 0f;

    internal static void UpdateLighting(World world, WaterMask mask, WaterRipples field,
        RainRippleEmitter emitter, float delta, bool jumped)
    {
        var context = LightingStates.GetValue(world, static value => new LightingContext(value));
        context.Query.Mask = mask;
        bool continuous = context.Map == world.MapIndex && ReferenceEquals(context.Field, field);
        emitter.Update(field, delta, RainIntensity(world.Weather.CurrentWeather, world.Weather.Count),
            !jumped && continuous, RainRippleEmitter.MaxPerFrame,
            world.Player.X, world.Player.Y, context.Query.ExposedWater);
        context.Field = field;
        context.Map = world.MapIndex;
        context.Tick = Time.Ticks;
    }

    // A Labs primitive may be suppressed only by this world's actual styled
    // frame. Disabling the existing Water ripples flag stops all live rain work.
    internal static bool LightingActive(World world) => world?.Player != null
        && WaterRender.Flag(true) != 0
        && WaterRender.EnvironmentEnabled(GUO.Configuration.ProfileManager.CurrentProfile)
        && WaterRender.RipplesEnabled(GUO.Configuration.ProfileManager.CurrentProfile)
        && LightingStates.TryGetValue(world, out var context)
        && context.Tick == Time.Ticks && context.Map == world.MapIndex
        && context.Field.OriginX != int.MinValue && context.Field.Strength > 0f;

    private readonly World _world;
    internal WaterMask Mask;
    internal readonly Func<int, int, bool> ExposedWater;

    internal RainSurfaceQuery(World world)
    {
        _world = world;
        ExposedWater = Check;
    }

    private bool Check(int x, int y)
    {
        if (_world.Map == null || Mask == null || !Mask.WaterAt(x, y, out int maskZ)) return false;
        // The shader mask intentionally treats unknown cells as wet. Never use
        // that alone as evidence of a surface: inspect loaded tile objects too.
        var surface = new RainSurface();
        int objects = 0;
        for (GameObject o = _world.Map.GetTile(x, y, false); o != null; o = o.TNext)
        {
            // Fail closed on pathological decoration stacks. With eight impact
            // attempts, at most 1024 objects are inspected in one frame.
            if (++objects > 128) return false;
            if (o.IsDestroyed) continue;
            bool wet = o is Land land && land.TileData.IsWet || o is Static s && s.ItemData.IsWet;
            bool cover = o is Land || o is Multi
                || o is Static dry && (dry.ItemData.IsRoof || dry.ItemData.IsSurface);
            surface.Add(wet, cover, o.Z);
        }
        return surface.Exposed && surface.WaterZ == maskZ;
    }
}
