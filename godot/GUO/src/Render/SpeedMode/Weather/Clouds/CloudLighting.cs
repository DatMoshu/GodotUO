// SPDX-License-Identifier: BSD-2-Clause
using System;

namespace GUO.Renderer.SpeedMode.Weather;

internal static class CloudLighting
{
    // Consume the sun resolver's result rather than reconstruct another clock.
    // Strength separates its moon/sun lobes; inverse shadow length is its
    // bounded elevation proxy. Unknown/no-clock uses the named ambient fallback.
    internal static float Daylight(float strength, float length, string source, float ambient)
    {
        if (source is "shard-clock" or "local-test-clock")
            return strength < .4f ? .1f : .1f+.9f*Math.Clamp(1/Math.Max(.01f,length),0,1);
        return float.IsFinite(ambient) ? Math.Clamp(ambient,0,1) : 0;
    }
}
