// SPDX-License-Identifier: BSD-2-Clause
using GUO.Configuration;
using Godot;

namespace GUO.Renderer.SpeedMode;

internal static class SpeedModeDebug
{
    internal static Color ClassColor(int kind, bool covered, int x, int y)
    {
        if (covered && ((x+y)/4)%2 == 0) return new Color(0.2f,0.2f,0.2f);
        return kind switch { 1 => Colors.Red, 2 => Colors.Green, 3 => Colors.Blue,
            4 => Colors.White, 5 => new Color(1,0,1), 6 => new Color(0,1,1), _ => Colors.Black };
    }
    internal static bool GBuffer;
    internal static bool Probe;
    internal static bool Requested => Probe || ProfileManager.CurrentProfile?.LightingSpeedMode == true;
    internal static bool Supported => DepthWorldProbe.Enabled;
    internal static bool Active => Requested && Supported;
    internal static string UnavailableReason => Supported ? null : "Requires the Depth World; unavailable on Compatibility or canvas world.";
}
