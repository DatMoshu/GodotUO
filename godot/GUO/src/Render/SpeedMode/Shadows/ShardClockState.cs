// SPDX-License-Identifier: BSD-2-Clause
// GUO-owned, ADR-0048: last validated 0x5B time, scoped to one World.
namespace GUO.Renderer.SpeedMode.Shadows;

internal struct ShardClockState
{
    internal byte Hour, Minute, Second;
    internal uint ReceivedTicks;
    internal bool Valid;

    internal bool Set(byte hour,byte minute,byte second,uint ticks)
    {
        if (hour > 23 || minute > 59 || second > 59) { this = default; return false; }
        Hour = hour; Minute = minute; Second = second; ReceivedTicks = ticks; Valid = true;
        return true;
    }

    // Advance the received stamp by the client's monotonic elapsed ticks;
    // no DateTime.Now or PC wall-time fallback. Unsigned subtraction handles
    // the ordinary 32-bit tick rollover.
    internal int MinuteOfDay => Hour*60+Minute;
    internal int MinuteAt(uint now) =>
        (int)(((uint)(Hour*3600+Minute*60+Second)+unchecked(now-ReceivedTicks)/1000)%86400)/60;
}
