// SPDX-License-Identifier: BSD-2-Clause
// GUO-owned UO+ spell bursts, ADR-0039 amendment. Game-loop thread only.
using GUO.Configuration;
using GUO.Game.Data;
using Godot;

namespace GUO.Renderer.Lights;

internal static class SpellLighting
{
    internal static bool Enabled(Profile profile) => profile is { RealtimeLights: true }
        && UoPlusOptions.Enabled(profile, UoPlusFeature.SpellLights);

    internal static int Budget(Profile profile) =>
        (RealtimeLightEffects.MaxSpellLights * UoPlusOptions.Density(profile, UoPlusFeature.SpellLights) + 99) / 100;

    // Graphics describe visible effects, not spell IDs: shards reuse these for other spells.
    // Casts are short stationary source glows; impacts are emitted at actual arrival,
    // never predicted from distance or packet duration. No light follows the projectile.
    internal static int Emit(Profile profile, uint now, GraphicEffectType type, ushort graphic,
        int x, int y, int z, bool impact = false)
    {
        if (!Enabled(profile)) return 0;
        bool moving = type == GraphicEffectType.Moving;
        if (impact && !moving) return 0;

        (int radius, Color color, uint duration, LightCurve curve) = (type, graphic) switch
        {
            (GraphicEffectType.Moving, 0x36D4 or 0x36E4) =>
                (impact ? 4 : 3, new Color(1f, 0.28f, 0.06f), impact ? 800u : 500u, LightCurve.C),
            (GraphicEffectType.Moving, 0x379F) =>
                (impact ? 4 : 3, new Color(0.35f, 0.5f, 1f), impact ? 700u : 450u, LightCurve.E),
            (GraphicEffectType.FixedFrom or GraphicEffectType.FixedXYZ, 0x376A) =>
                (3, new Color(0.3f, 1f, 0.55f), 900u, LightCurve.E),
            (GraphicEffectType.Lightning, _) =>
                (4, new Color(0.6f, 0.75f, 1f), 400u, LightCurve.E),
            _ => (0, default, 0u, LightCurve.E)
        };
        if (duration == 0) return 0;
        int handle = RealtimeLightEffects.AddSpell(now, x, y, z, radius, color, duration, curve, Budget(profile));
        // Lightning strikes and blasts flash: full at once, then an exponential fall (light types addendum).
        if (handle != 0 && (type == GraphicEffectType.Lightning || impact)) RealtimeLightEffects.MakeFlash(handle);
        return handle;
    }
}
