// SPDX-License-Identifier: BSD-2-Clause
using System;
using System.Runtime.CompilerServices;
using GUO.Configuration;
using GUO.Renderer.SpeedMode.Shadows;
using N2=System.Numerics.Vector2;
namespace GUO.Labs.LightingClock;
/// <summary>Local testing clock. Disabled returns the original shard resolver unchanged.</summary>
internal static class LightingTestClock
{
    private static readonly ConditionalWeakTable<Profile,LightingClockTimeline> Timelines=new();
    internal static double MinuteAt(Profile profile,long now)
    {
        if(profile==null)return 0;
        return Timelines.GetValue(profile,static _=>new LightingClockTimeline()).Read(profile.LocalLightingTestHour*60+profile.LocalLightingTestMinute,
            profile.LightingSpeedMode&&profile.LocalLightingTestTime&&profile.LocalLightingTestAuto,
            profile.LocalLightingTestSpeed,now);
    }
    internal static void Seek(Profile profile,int minute)
    {
        minute=Math.Clamp(minute,0,1439);
        profile.LocalLightingTestHour=minute/60;profile.LocalLightingTestMinute=minute%60;
        Timelines.GetValue(profile,static _=>new LightingClockTimeline()).Seek();
    }
    internal static bool TryMinute(Profile profile,out int minute)
    {
        minute=(int)MinuteAt(profile,Environment.TickCount64);
        return profile?.LightingSpeedMode==true&&profile.LocalLightingTestTime;
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
