// SPDX-License-Identifier: BSD-2-Clause
// GUO-owned effect ownership policy (task 1136): which legacy effect yields to
// which replacement. Stored preferences are never rewritten here.
using GUO.Renderer.DepthWorld;
using GUO.Renderer.SpeedMode;

namespace GUO.Configuration;

internal static class GuoEffectPolicy
{
    /// <summary>
    /// Spell-effect lights may be collected by whichever lighting path owns the frame:
    /// the legacy CPU lights (RealtimeLights) or enhanced speed-mode lighting. The
    /// stored RealtimeLights preference is not touched.
    /// </summary>
    internal static bool SpellLightsEligible(Profile profile) =>
        profile != null && UoPlusOptions.Enabled(profile, UoPlusFeature.SpellLights)
        && (profile.RealtimeLights || SpeedModeDebug.Active);

    /// <summary>
    /// True while the enhanced shadow path replaces classic native shadows: enhanced
    /// lighting is requested and supported, its sun or lamp shadows are on, AND the
    /// previous rendered frame was actually lit by the enhanced path.
    /// One-frame boundary: classic shadows are drawn into the world target before the
    /// GPU learns whether enhanced lighting succeeds for the current frame, so the
    /// decision uses the last completed frame. After any failure, fallback, device
    /// loss or disable the next frame draws classic shadows again; the first lit
    /// frame after a recovery still carries classic shadows. The signal is written on
    /// the render thread and read here on the game thread (volatile). The stored
    /// ShadowsEnabled/ShadowsStatics preferences are never rewritten.
    /// </summary>
    internal static bool ClassicShadowsSuppressed(Profile profile) =>
        profile != null && SpeedModeDebug.Active
        && (profile.SpeedModeSunShadows || profile.SpeedModeLampShadows > 0)
        && OrderedWorldCapture.LastFrameEnhancedLit;

    /// <summary>Player-facing effective status of the classic shadow preference.</summary>
    internal static string ClassicShadowsStatus(Profile profile)
    {
        if (profile == null || !profile.ShadowsEnabled) return "Classic shadows: off (your setting)";
        if (ClassicShadowsSuppressed(profile))
            return "Classic shadows: replaced by enhanced shadows while enhanced lighting is active (your setting is kept)";
        if (SpeedModeDebug.Requested && (profile.SpeedModeSunShadows || profile.SpeedModeLampShadows > 0))
            return "Classic shadows: on (enhanced lighting not active)";
        return "Classic shadows: on";
    }
}
