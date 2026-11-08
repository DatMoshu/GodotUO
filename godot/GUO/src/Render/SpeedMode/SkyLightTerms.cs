// SPDX-License-Identifier: BSD-2-Clause
// Compatibility bookkeeping for existing artistic shadow strengths, not physical GI.
using System;
using N3 = System.Numerics.Vector3;

namespace GUO.Renderer.SpeedMode;

internal static class SkyLightTerms
{
    internal readonly record struct Terms(N3 Neutral, N3 Sky, N3 Direct, float Budget, bool Admitted);

    /// <summary>
    /// R8 MAX masks already include strength. Round its budget upward so a
    /// stored mask (notably .32 -> 82/255) cannot exceed the direct-light share.
    /// Nonfinite/negative input disables the share; finite values above one cap
    /// at one. This changes bookkeeping only, never the original shadow mask.
    /// </summary>
    internal static float QuantizedSunBudget(float strength)
    {
        if (!float.IsFinite(strength) || strength <= 0f) return 0f;
        return MathF.Ceiling(Math.Min(strength, 1f) * 255f) / 255f;
    }

    /// <summary>
    /// Split already-shadowed ambient RGB using an explicit quantized budget.
    /// visibility is the existing min(1-legacyMask, 1-structuralOcclusion).
    /// Both terms still contain the existing roof factor and night floor; the
    /// sky remainder is not a measured sky-irradiance estimate. Admitted=false
    /// identifies contradictory inputs rather than certifying a valid split.
    /// Neutral retains the old operation order regardless of the diagnostic terms.
    /// </summary>
    internal static Terms Split(N3 ambient, float roof, float visibility, float quantizedBudget)
    {
        float remainder = 1f - quantizedBudget;
        bool admitted = FiniteNonnegative(ambient) && float.IsFinite(roof) && roof >= 0f && roof <= 1f
            && float.IsFinite(quantizedBudget) && quantizedBudget >= 0f && quantizedBudget <= 1f
            && float.IsFinite(visibility) && visibility >= remainder && visibility <= 1f;
        return new(ambient * (roof * visibility), ambient * (roof * remainder),
            ambient * (roof * Math.Max(0f, visibility - remainder)), quantizedBudget, admitted);
    }

    private static bool FiniteNonnegative(N3 value) => float.IsFinite(value.X) && value.X >= 0f
        && float.IsFinite(value.Y) && value.Y >= 0f && float.IsFinite(value.Z) && value.Z >= 0f;

    // Inserted into the real light fragment after its existing roof/sun queries.
    // The host supplies Q in ambient-only origin_curve.w. Probe terms use the
    // same production shader and preserve alpha=1; their RGB, not alpha, sums.
    // In ordinary mode the original expression and operation order stay literal.
    internal const string OutputGlsl = """
        if(pc.screen_mode.w==1.0) {
            light=vec4(pc.ambient_roof.rgb*(roof*(1.0-pc.origin_curve.w)),1); return;
        }
        if(pc.screen_mode.w==2.0) {
            light=vec4(pc.ambient_roof.rgb*(roof*max(0.0,sun-(1.0-pc.origin_curve.w))),1); return;
        }
        light=vec4(pc.ambient_roof.rgb*(roof*sun),1); return;
""";
}
