// SPDX-License-Identifier: BSD-2-Clause
// COMPETITION ENTRIES MAY EDIT ONLY THIS FILE inside src/Labs/CompetitionBench.
//
// Contract
//   * The probe first applies the frozen "enhanced-off" profile (enhanced lighting,
//     sun shadows, every competition addition off). Then, for the variants
//     "terrain" and "terrain-cloud-wind", it calls Apply(profile, variant).
//   * A variant must leave EVERYTHING ELSE equal to enhanced-off: the same scene,
//     camera, clock, window, lights and shadows. It may only switch on its own
//     addition (profile fields, or its own flags read by its own code).
//   * Return true when the variant is implemented and now applied; return false
//     when it is not supported. False is not a failure: the probe records the
//     variant as "unsupported" and exits 0.
//   * The probe still checks admission afterwards (enhanced lighting active,
//     no Depth World fallback); an entry that breaks that FAILS.
//   * The probe snapshots EVERY Profile property before and after Apply and FAILS if anything changed
//     that is not named in AllowedFields below (declare each field your variant switches). The diff
//     is written into bench.json.
//   * This file ONLY maps variant names to option switches. Production renderer code may be edited by
//     entrants within their scope; the harness, runner, fixtures and rubric are immutable to them.
//   * Do not read the clock, scene or variant from anywhere but the arguments.
#if GUO_LABS && DEBUG
using System.Collections.Generic;
using GUO.Configuration;

namespace GUO.Labs.CompetitionBench;

internal static class CompetitionVariants
{
    /// <summary>Variant names in the frozen contract besides classic and enhanced-off.</summary>
    internal const string Terrain = "terrain";
    internal const string TerrainCloudWind = "terrain-cloud-wind";

    /// <summary>Profile property names this variant may change. Empty in the common base.</summary>
    internal static readonly HashSet<string> AllowedFields = new();

    /// <summary>
    /// Apply one competition variant on top of the enhanced-off profile.
    /// Example: <c>case Terrain: profile.SomeTerrainOption = true; return true;</c>
    /// </summary>
    internal static bool Apply(Profile profile, string variant)
    {
        switch (variant)
        {
            case Terrain:
            case TerrainCloudWind:
            default:
                return false; // base harness: no competition addition exists yet
        }
    }
}
#endif
