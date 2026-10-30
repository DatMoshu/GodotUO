// SPDX-License-Identifier: BSD-2-Clause
using System;
using GUO.Configuration;
using GUO.Renderer.SpeedMode.Shadows;
using N2=System.Numerics.Vector2;
namespace GUO.Labs.LightingClock;
/// <summary>Local testing clock. Disabled returns the original shard resolver unchanged.</summary>
internal static class LightingTestClock
{
    internal static bool TryMinute(Profile profile,out int minute)
    {
        minute=0;
        if(profile?.LightingSpeedMode!=true||!profile.LocalLightingTestTime)return false;
        minute=profile.LocalLightingTestHour*60;return true;
    }
    internal static bool TryBrightness(Profile profile,out float brightness)
    {
        brightness=0;if(!TryMinute(profile,out int minute))return false;
        // Fixed 06:00 dawn/18:00 dusk, with a small moonlit floor. Preview approximation.
        brightness=.1f+.9f*Math.Clamp(MathF.Sin((minute-360)*(MathF.PI/720)),0,1);return true;
    }
    internal static (N2 Direction,float Length,float Strength,string Source) Resolve(Profile profile,in GUO.Renderer.SpeedMode.Shadows.ShardClockState clock,float brightness,uint now)
    {
        if(!TryMinute(profile,out int minute))return SunShadowProjection.Resolve(clock,brightness,now);
        var local=SunShadowProjection.FromShardMinute(minute);
        return(local.Direction,local.Length,local.Strength,"local-test-clock");
    }
}
