// SPDX-License-Identifier: BSD-2-Clause
// Headless arithmetic proof. No world, artwork, rendering device or cache work.
using System;
using GUO.Renderer.Lights;
using N3 = System.Numerics.Vector3;

namespace GUO.Renderer.SpeedMode;

public partial class SkyLightTermsProbe : Godot.Node
{
    public override void _Ready()
    {
        try {
            int checks = Run();
            Godot.GD.Print("[sky-light-terms] PASS checks=" + checks
                + "; all admitted R8 masks, native strengths, hits/misses, roof/color/night-floor; GPU/native=not-tested");
            GetTree().Quit(0);
        } catch (Exception error) {
            Godot.GD.PrintErr("[sky-light-terms] FAIL " + error); GetTree().Quit(1);
        }
    }

    internal static int Run()
    {
        int checks = 0; string context = "guards";
        void Check(bool ok, string reason)
        {
            if (!ok) throw new InvalidOperationException(reason + "; " + context);
            checks++;
        }

        Check(SkyLightTerms.QuantizedSunBudget(0f) == 0f, "zero strength has no direct share");
        Check(SkyLightTerms.QuantizedSunBudget(-1f) == 0f, "negative strength disables the share");
        Check(SkyLightTerms.QuantizedSunBudget(float.NaN) == 0f
            && SkyLightTerms.QuantizedSunBudget(float.PositiveInfinity) == 0f
            && SkyLightTerms.QuantizedSunBudget(float.NegativeInfinity) == 0f, "nonfinite strength disables the share");
        Check(SkyLightTerms.QuantizedSunBudget(1f) == 1f && SkyLightTerms.QuantizedSunBudget(4f) == 1f,
            "finite strengths are capped at one");
        Check(SkyLightTerms.QuantizedSunBudget(.000001f) == 1f / 255f, "small positive strength has a finite R8 budget");

        // Independent expected integer budgets for all strengths actually returned
        // by SunShadowProjection, plus disabled. Do not derive these with the helper.
        float[] strengths = { 0f, .2f, .32f, .55f, .62f };
        int[] maximumMask = { 0, 51, 82, 141, 159 };
        float[] roofs = { 0f, RealtimeLightField.RoofDaylight, .775f, 1f };
        N3 ColorAt(float level)
        {
            var color = RealtimeLightField.AmbientFor(level);
            return new(color.R, color.G, color.B);
        }
        N3[] colors = { N3.Zero, N3.One, ColorAt(.03f), ColorAt(.0625f), ColorAt(.5f), new(1.8f, .4f, .15f) };
        for (int s = 0; s < strengths.Length; s++)
        {
            float strength = strengths[s], budget = SkyLightTerms.QuantizedSunBudget(strength);
            context = "strength=" + strength;
            Check(Bits(budget) == Bits(maximumMask[s] / 255f), "budget matches independent native-strength table");
            Check(budget >= strength && budget <= 1f, "budget encloses the original structural strength");
            for (int mask = 0; mask <= maximumMask[s]; mask++)
            for (int hit = 0; hit < 2; hit++)
            foreach (float roof in roofs)
            foreach (N3 ambient in colors)
            {
                // This is the existing production mask union, before any new
                // bookkeeping: legacy alpha already contains shadow strength.
                float legacy = mask / 255f;
                float oldVisibility = Math.Min(1f - legacy, hit == 0 ? 1f : 1f - strength);
                var terms = SkyLightTerms.Split(ambient, roof, oldVisibility, budget);
                context = $"strength={strength}, mask={mask}, hit={hit}, roof={roof}, ambient={ambient}";
                Check(terms.Admitted, "every admitted current input has valid compatibility terms");
                Check(FiniteNonnegative(terms.Sky) && FiniteNonnegative(terms.Direct), "RGB terms are finite and nonnegative");
                N3 original = new(ambient.X * (roof * oldVisibility), ambient.Y * (roof * oldVisibility),
                    ambient.Z * (roof * oldVisibility));
                Check(SameBits(terms.Neutral, original), "neutral output preserves the original float32 operation order exactly");
                Check(NearUlps(terms.Sky + terms.Direct, original, 4),
                    "compatibility RGB sums to original within four float32 ULPs, not half-float tolerance");
                // Double precision reference is independent of the float32
                // decomposition. Its direct weight is budget-max(mask,hit*S).
                double directWeight = Math.Max(0d, (double)budget - Math.Max((double)legacy, hit * (double)strength));
                Check(NearReference(terms.Direct, ambient, roof, directWeight, original),
                    "direct term matches independently evaluated attenuation without applying strength twice");
                Check(SameBits(terms.Sky, SkyLightTerms.Split(ambient, roof, 1f, budget).Sky),
                    "sky remainder does not depend on whether legacy or structural shadow won");
            }
        }

        context = "moon quantization and invalid-domain witnesses";
        float moonMask = 82f / 255f, moonBudget = SkyLightTerms.QuantizedSunBudget(.32f);
        Check(moonMask > .32f, "real R8 moon mask exceeds its nominal artistic strength");
        var moon = SkyLightTerms.Split(N3.One, 1f, 1f - moonMask, moonBudget);
        Check(moon.Admitted && moon.Direct == N3.Zero && SameBits(moon.Sky, moon.Neutral),
            "quantized moon boundary retains old attenuation with zero direct remainder");
        float naive = 1f - .32f + .32f * Math.Clamp(1f - moonMask / .32f, 0f, 1f);
        Check(Math.Abs(naive - moon.Neutral.X) > .001f,
            "regression witness rejects naive strength normalization followed by visibility clamp");
        Check(!SkyLightTerms.Split(N3.One, 1f, .5f, .2f).Admitted,
            "mask outside supplied budget is explicitly unadmitted, never certified by the direct clamp");
        Check(!SkyLightTerms.Split(N3.One, 1f, float.NaN, .2f).Admitted
            && !SkyLightTerms.Split(N3.One, 1f, 1f, float.NaN).Admitted
            && !SkyLightTerms.Split(N3.One, 1.01f, 1f, .2f).Admitted,
            "invalid visibility, budget and roof inputs cannot claim a valid split");
        Check(SkyLightTerms.OutputGlsl.Contains("light=vec4(pc.ambient_roof.rgb*(roof*sun),1); return;", StringComparison.Ordinal),
            "shared production shader body retains the exact old neutral expression");
        return checks;
    }

    private static uint Bits(float value) => BitConverter.SingleToUInt32Bits(value);
    private static bool SameBits(N3 a, N3 b) => Bits(a.X) == Bits(b.X) && Bits(a.Y) == Bits(b.Y) && Bits(a.Z) == Bits(b.Z);
    private static bool FiniteNonnegative(N3 v) => float.IsFinite(v.X) && v.X >= 0f
        && float.IsFinite(v.Y) && v.Y >= 0f && float.IsFinite(v.Z) && v.Z >= 0f;
    private static bool NearUlps(N3 a, N3 b, int count) => Ulps(a.X, b.X) <= count
        && Ulps(a.Y, b.Y) <= count && Ulps(a.Z, b.Z) <= count;
    private static long Ulps(float a, float b) => Math.Abs((long)Bits(a) - Bits(b));
    private static bool NearReference(N3 actual, N3 ambient, float roof, double weight, N3 scale)
    {
        bool Channel(float value, float color, float basis)
        {
            double expected = color * (double)roof * weight;
            // visibility and1-Q each round once before subtraction. Bound the
            // reference error by four ULPs of the full current output magnitude.
            double ulp = MathF.BitIncrement(basis) - basis;
            return Math.Abs(value - expected) <= Math.Max(4d * ulp, 1e-12);
        }
        return Channel(actual.X, ambient.X, scale.X) && Channel(actual.Y, ambient.Y, scale.Y)
            && Channel(actual.Z, ambient.Z, scale.Z);
    }
}
