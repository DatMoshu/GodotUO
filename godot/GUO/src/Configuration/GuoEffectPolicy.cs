// SPDX-License-Identifier: BSD-2-Clause
// GUO-owned effect ownership policy (task 1136): which legacy effect yields to
// which replacement. Stored preferences are never rewritten here.
namespace GUO.Configuration;

internal static class GuoEffectPolicy
{
    /// <summary>Spell-effect lights may be collected by whichever lighting path owns the frame.</summary>
    internal static bool SpellLightsEligible(Profile profile) =>
        profile is { RealtimeLights: true } && UoPlusOptions.Enabled(profile, UoPlusFeature.SpellLights);

    /// <summary>True while the enhanced shadow path replaces classic native shadows.</summary>
    internal static bool ClassicShadowsSuppressed(Profile profile) => false;

    /// <summary>Player-facing effective status of the classic shadow preference.</summary>
    internal static string ClassicShadowsStatus(Profile profile) => "";
}
