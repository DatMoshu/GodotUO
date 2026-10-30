// SPDX-License-Identifier: BSD-2-Clause
using System;

namespace GUO.Renderer.SpeedMode.Weather;

/// <summary>Curated cobblestone/paving floor allowlist; data_formats section 51.</summary>
internal static class SpeedModePuddleSurfaces
{
    internal static bool IsCandidate(bool land, int graphic, string tileName)
    {
        // IDs are tile metadata only, never client artwork. Land/static ID
        // spaces are separate; the core still requires the floor surface class.
        bool known = land
            ? graphic is >= 0x03E9 and <= 0x03EC or >= 0x0685 and <= 0x0688
                or >= 0x068D and <= 0x0694 or >= 0x069D and <= 0x06A0 or 0x0563
            : graphic is >= 0x049D and <= 0x04A0 or >= 0x0515 and <= 0x0524;
        if (known) return true;
        return tileName?.Trim() is string name && (
            name.Equals("cobblestone", StringComparison.OrdinalIgnoreCase)
            || name.Equals("cobblestones", StringComparison.OrdinalIgnoreCase)
            || name.Equals("paving", StringComparison.OrdinalIgnoreCase)
            || name.Equals("paving stone", StringComparison.OrdinalIgnoreCase)
            || name.Equals("paving stones", StringComparison.OrdinalIgnoreCase)
            || name.Equals("stone_paver", StringComparison.OrdinalIgnoreCase)
            || name.Equals("stone pavers", StringComparison.OrdinalIgnoreCase)
            || name.Equals("stone pavers dark", StringComparison.OrdinalIgnoreCase)
            || name.Equals("sandstone paver", StringComparison.OrdinalIgnoreCase));
    }
}
