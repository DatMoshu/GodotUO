// SPDX-License-Identifier: BSD-2-Clause
// GUO-owned UO+ settings registry (ADR-uoplus-settings).
using System;
using System.Collections.Generic;

namespace GUO.Configuration;

[Flags]
internal enum UoPlusFeature
{
    None = 0, Fog = 1, Leaves = 2, Dust = 4, RainRings = 8,
    Puddles = 16, WetPlayer = 32, Sway = 64, SpellLights = 128, WaterRipples = 256
}

/// <summary>The shared flag and density contract. Game-loop thread only.</summary>
internal static class UoPlusOptions
{
    internal readonly record struct Feature(UoPlusFeature Id, string Name, string Hint, bool Available);
    internal static readonly Feature[] Features =
    {
        new(UoPlusFeature.Fog, "Fog banks", "Swamp ground", true),
        new(UoPlusFeature.Leaves, "Drifting leaves", "Forest, jungle and foliage", true),
        new(UoPlusFeature.Dust, "Dry-area dust", "Soft-sand ground", true),
        new(UoPlusFeature.RainRings, "Weather: rain rings", "Requires rain; Labs build", LabsAvailable),
        new(UoPlusFeature.Puddles, "Weather: puddles", "Requires rain and selected ground; Labs build", LabsAvailable),
        new(UoPlusFeature.WetPlayer, "Weather: wet player", "Requires rain; Labs build", LabsAvailable),
        new(UoPlusFeature.Sway, "Tree and plant sway", "Density controls wind amplitude; rain strengthens gusts", true),
        new(UoPlusFeature.SpellLights, "Spell-effect lights", "Requires Realtime lights", true),
        new(UoPlusFeature.WaterRipples, "Water ripples and wakes", "Requires a non-Classic water style", true),
    };
#if GUO_LABS
    internal const bool LabsAvailable = true;
#else
    internal const bool LabsAvailable = false;
#endif
    // Particle and spell flags stay in their existing serialized fields: old profiles
    // continue to read correctly, with no duplicated authoritative flags.
    private const UoPlusFeature ParticleFlags = UoPlusFeature.Fog | UoPlusFeature.Leaves | UoPlusFeature.Dust;
    private const UoPlusFeature LegacyFlags = ParticleFlags | UoPlusFeature.SpellLights;
    private const UoPlusFeature KnownFlags = ParticleFlags | UoPlusFeature.RainRings | UoPlusFeature.Puddles
        | UoPlusFeature.WetPlayer | UoPlusFeature.Sway | UoPlusFeature.SpellLights | UoPlusFeature.WaterRipples;

    internal static UoPlusFeature Flags(Profile p)
    {
        if (p == null) return UoPlusFeature.None;
        var flags = p.UoPlusFlags & KnownFlags & ~LegacyFlags;
        if (p.AmbientFog) flags |= UoPlusFeature.Fog;
        if (p.AmbientLeaves) flags |= UoPlusFeature.Leaves;
        if (p.AmbientDust) flags |= UoPlusFeature.Dust;
        if (p.SpellLighting) flags |= UoPlusFeature.SpellLights;
        return flags;
    }

    internal static bool Requested(Profile p, UoPlusFeature id) => (Flags(p) & id) == id && id != 0;
    internal static bool Available(UoPlusFeature id)
    {
        foreach (var feature in Features) if (feature.Id == id) return feature.Available;
        return false;
    }
    internal static bool Enabled(Profile p, UoPlusFeature id) => Available(id) && Requested(p, id) && Density(p, id) > 0;

    internal static int Density(Profile p, UoPlusFeature id)
    {
        if (p == null) return 0;
        if (p.UoPlusDensities != null && p.UoPlusDensities.TryGetValue(id, out int density)) return Math.Clamp(density, 0, 100);
        // Preserve the old global particle density until a per-feature value is saved.
        return (id & ParticleFlags) != 0 ? Math.Clamp(p.AmbientParticleDensity, 0, 100) : 50;
    }

    internal static void Set(Profile p, UoPlusFeature id, bool enabled, int density)
    {
        if (p == null || !Available(id)) return;
        switch (id)
        {
            case UoPlusFeature.Fog: p.AmbientFog = enabled; break;
            case UoPlusFeature.Leaves: p.AmbientLeaves = enabled; break;
            case UoPlusFeature.Dust: p.AmbientDust = enabled; break;
            case UoPlusFeature.SpellLights: p.SpellLighting = enabled; break;
            default:
                p.UoPlusFlags = enabled ? p.UoPlusFlags | id : p.UoPlusFlags & ~id;
                break;
        }
        p.UoPlusDensities ??= new Dictionary<UoPlusFeature, int>();
        p.UoPlusDensities[id] = Math.Clamp(density, 0, 100);
    }

    internal static void DisableAll(Profile p)
    {
        p.AmbientFog = p.AmbientLeaves = p.AmbientDust = false;
        p.SpellLighting = false;
        p.UoPlusFlags = UoPlusFeature.None;
    }

    internal static void ResetDefaults(Profile p)
    {
        DisableAll(p);
        p.UoPlusDensities = new Dictionary<UoPlusFeature, int>();
        p.AmbientParticleDensity = 50;
        p.AmbientParticleCap = 128;
    }
}
