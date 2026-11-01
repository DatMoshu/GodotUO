// SPDX-License-Identifier: BSD-2-Clause
// GUO-owned: clock-driven projection; contact rules from bbd51955.
using System;
using GUO.Assets;
using N2 = System.Numerics.Vector2;

namespace GUO.Renderer.SpeedMode.Shadows;

internal static class SunShadowProjection
{
    internal static (N2 Direction,float Length,float Strength,string Source) Resolve(in ShardClockState clock,float brightness,uint now)
    {
        if (clock.Valid)
        {
            var sky = FromShardMinute(clock.MinuteAt(now));
            return (sky.Direction,sky.Length,sky.Strength,"shard-clock");
        }
        // Same explicitly approximate fallback as the parked lighting sprint
        // bbd51955: derived from shard brightness, not a true clock.
        float level = float.IsFinite(brightness) ? Math.Clamp(brightness,0,1) : 0;
        float day = Math.Clamp((level-.25f)/.75f,0,1);
        float angle = -.7f+day*1.4f;
        bool moon = level < .25f;
        float sign = moon ? -1 : 1;
        return (new N2(MathF.Cos(angle)*sign,MathF.Sin(angle)*sign),4-2*day,
            moon ? .32f : .62f,"shard-brightness-approximation");
    }

    // World screen placement stays untouched; only the projection anchor moves
    // to the lowest opaque row. Mobile offsets are already interpolated.
    public static N2 ContactAnchor(N2 screen,int bottomGap,bool mobile,N2 step,float stepZ) =>
        screen+new N2(22,mobile ? 22 : 44-bottomGap)+(mobile ? step-new N2(0,stepZ+3) : N2.Zero);

    public static bool Eligible(TileFlag flags,int height,bool mobile = false)
    {
        // Foliage can have zero tiledata height: its native silhouette still
        // casts. Flat surfaces, stairs and wall pieces must not stamp slabs.
        const TileFlag excluded = TileFlag.Roof | TileFlag.Surface | TileFlag.Bridge |
            TileFlag.Background | TileFlag.Translucent | TileFlag.Wet | TileFlag.Wall |
            TileFlag.Door | TileFlag.Window;
        return (flags & excluded)==0 && (mobile || height>=3 || (flags & TileFlag.Foliage)!=0);
    }

    // minute is the server/shard's clock, never a machine wall clock. The
    // midnight moon uses the opposite solar arc; local time is not consulted.
    public static (N2 Direction,float Length,float Strength) FromShardMinute(int minute)
    {
        minute = ((minute%1440)+1440)%1440;
        bool day = minute is >= 360 and < 1080;
        float phase = ((minute+(day ? -360 : 360))%720)/720f;
        float azimuth = (phase-.5f)*MathF.PI;
        N2 direction = new(MathF.Cos(azimuth),MathF.Sin(azimuth));
        if (!day) direction = -direction;
        float altitude = MathF.Max(.15f,MathF.Sin(phase*MathF.PI));
        return (direction, Math.Clamp(1/altitude,.8f,4), day ? .55f : .2f);
    }
}
