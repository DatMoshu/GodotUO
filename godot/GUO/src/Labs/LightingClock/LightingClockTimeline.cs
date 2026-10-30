// SPDX-License-Identifier: BSD-2-Clause
using System;
namespace GUO.Labs.LightingClock;
// Main-thread local preview timeline. Monotonic milliseconds, no shard state.
internal sealed class LightingClockTimeline
{
    private bool _initialized,_running;
    private int _seed,_speed;
    private long _anchor;
    private double _minute;
    internal double Read(int seed,bool running,int speed,long now)
    {
        seed=Math.Clamp(seed,0,1439);speed=Math.Clamp(speed,1,1440);
        if(!_initialized||seed!=_seed) { _minute=seed;_anchor=now;_initialized=true; }
        else if(running!=_running||speed!=_speed) { _minute=At(now);_anchor=now; }
        _seed=seed;_running=running;_speed=speed;
        return At(now);
    }
    internal void Seek() { _initialized=false; }
    private double At(long now)
    {
        double value=_minute+(_running?Math.Max(0,now-_anchor)*(double)_speed/60000:0);
        return ((value%1440)+1440)%1440;
    }
}
