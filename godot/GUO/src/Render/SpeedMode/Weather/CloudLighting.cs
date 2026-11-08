// SPDX-License-Identifier: BSD-2-Clause
// PORT DEVIATION (GUO): ADR-0048 cloud and fog daylight. Same 06:00/18:00
// curve as the lighting test clock. No PC wall clock.
using System;
using GUO.Configuration;
using GUO.Game;

namespace GUO.Renderer.SpeedMode.Weather;

internal static class CloudLighting
{
    internal readonly record struct Sky(float Daylight, string Source);

    // Normalized across 1440 minutes. Noon is 1, dawn and dusk and the night
    // floor are 0.1. The shadow length clamp is not reused here.
    internal static float FromMinute(int minute)
    {
        minute=((minute%1440)+1440)%1440;
        return .1f+.9f*Math.Clamp(MathF.Sin((minute-360)*(MathF.PI/720f)),0,1);
    }

    internal static float BrightnessApproximation(float ambient) =>
        float.IsFinite(ambient) ? Math.Clamp(ambient,0,1) : 0;

    internal static Sky Resolve(World world, Profile profile, float ambient)
    {
#if GUO_LABS
        if (GUO.Labs.LightingClock.LightingTestClock.TryMinute(profile, out int testMinute))
            return new(FromMinute(testMinute), "local-test-clock");
#endif
        if (world!=null && world.ShardClock.Valid)
            return new(FromMinute(world.ShardClock.MinuteAt(Time.Ticks)), "shard-clock");
        return new(BrightnessApproximation(ambient), "shard-brightness-approximation");
    }

    internal static void CheckCycle()
    {
        if (FromMinute(12*60)!=1f) throw new InvalidOperationException("Cloud noon");
        if (Math.Abs(FromMinute(0)-.1f)>.0001f) throw new InvalidOperationException("Cloud midnight");
        if (Math.Abs(FromMinute(360)-.1f)>.0001f || Math.Abs(FromMinute(1080)-.1f)>.0001f)
            throw new InvalidOperationException("Cloud dawn and dusk");
        if (FromMinute(361)>=.11f) throw new InvalidOperationException("Cloud minute after dawn stays low");
        if (FromMinute(-720)!=1f || FromMinute(2160)!=1f) throw new InvalidOperationException("Cloud minute wrap");
        if (BrightnessApproximation(.35f)!=.35f || BrightnessApproximation(float.NaN)!=0 || BrightnessApproximation(2)!=1)
            throw new InvalidOperationException("Cloud brightness approximation");
    }
}
