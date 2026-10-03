using CentrED.MapGen.IR;
using CentrED.MapGen.Noise;
using CentrED.MapGen.Pipeline;

namespace CentrED.MapGen.Passes.Terrain;

// Continent-shaping mask blended with the fBm noise. Drives the gross silhouette of
// land vs ocean; the noise still carries all the small-scale detail.
//   None             — no mask, the noise output is used unmodified.
//   Radial           — single circular landmass centred on the map (legacy default).
//   VerticalStrait   — two landmasses E/W of a central N-S sea channel.
//   HorizontalStrait — two landmasses N/S of a central E-W sea channel.
//   Archipelago      — low-frequency mask noise threshold-shaped to keep most of the
//                      map below sea level; produces many discrete islands.
//   WestContinentEastArchipelago — single landmass on the west half (offset radial,
//                      centred at 1/4 width with margin so it never touches edges)
//                      paired with archipelago-style scattered islands on the east
//                      half. Strong sea bias along the central seam keeps a clean
//                      meridional ocean between the two halves.
public enum ContinentShape
{
    None,
    Radial,
    VerticalStrait,
    HorizontalStrait,
    Archipelago,
    WestContinentEastArchipelago,
}

public sealed class NoiseHeightParams
{
    [TunableDisplay("Octaves")] [TunableRange(1, 8)]
    public int Octaves { get; set; } = 5;

    [TunableDisplay("Base frequency", Tooltip = "Cells per tile at octave 0. Smaller = larger features.")]
    [TunableRange(0.0005, 0.05)]
    public double BaseFrequency { get; set; } = 0.004;

    [TunableDisplay("Lacunarity")] [TunableRange(1.5, 3.0)]
    public double Lacunarity { get; set; } = 2.0;

    [TunableDisplay("Gain")] [TunableRange(0.2, 0.8)]
    public double Gain { get; set; } = 0.5;

    [TunableDisplay("Sea level Z")] [TunableRange(-128, 127)]
    public int SeaLevelZ { get; set; } = 0;

    [TunableDisplay("Vertical scale", Tooltip = "Maps normalized [-1,1] noise to Z range.")]
    [TunableRange(8, 127)]
    public int VerticalScale { get; set; } = 64;

    // Legacy single-continent radial knob. Kept so old presets still work; if it is
    // > 0 and Shape == Radial (default), it overrides ShapeStrength below. New
    // presets should set Shape + ShapeStrength explicitly instead.
    [TunableDisplay("Continent falloff (legacy)", Tooltip = "Radial falloff toward map edges. Use Shape=Radial + ShapeStrength on new presets.")]
    [TunableRange(0.0, 1.0)]
    public double ContinentFalloff { get; set; } = 0.35;

    [TunableDisplay("Continent shape", Tooltip = "Mask shape blended with the noise.")]
    public ContinentShape Shape { get; set; } = ContinentShape.Radial;

    [TunableDisplay("Shape strength", Tooltip = "0 = pure noise, 1 = pure mask.")]
    [TunableRange(0.0, 1.0)]
    public double ShapeStrength { get; set; } = 0.35;

    [TunableDisplay("Strait width", Tooltip = "Width of the central sea channel as a fraction of the map (VerticalStrait/HorizontalStrait).")]
    [TunableRange(0.05, 0.6)]
    public double StraitWidth { get; set; } = 0.25;

    [TunableDisplay("Archipelago scale", Tooltip = "Frequency of the low-freq mask noise (Archipelago shape).")]
    [TunableRange(0.002, 0.05)]
    public double ArchipelagoScale { get; set; } = 0.015;

    // -----------------------------------------------------------------------
    // Anisotropic ridge layer. Ported from ProjectCog's HexNoiseUtil.FbmAniso —
    // ridged fBm sampled at two perpendicular scale pairs (short-X/long-Y and
    // long-X/short-Y) then max-combined, so the strongest ridge in each cell
    // dominates. The result is additively blended into the base fBm to produce
    // directional mountain chains instead of isotropic blobs.
    //
    // Default RidgeStrength = 0 → layer fully inert, all existing presets
    // produce the same output as before.
    // -----------------------------------------------------------------------

    [TunableDisplay("Ridge strength", Tooltip = "Additive blend amount for anisotropic ridges. 0 = off (legacy behavior), 0.3-0.6 = visible mountain chains.")]
    [TunableRange(0.0, 1.0)]
    public double RidgeStrength { get; set; } = 0.0;

    [TunableDisplay("Ridge short freq", Tooltip = "High-frequency axis of the ridge anisotropy (tight cross-ridge spacing).")]
    [TunableRange(0.005, 0.2)]
    public double RidgeFreqShort { get; set; } = 0.035;

    [TunableDisplay("Ridge long freq", Tooltip = "Low-frequency axis of the ridge anisotropy (long along-ridge wavelength).")]
    [TunableRange(0.001, 0.05)]
    public double RidgeFreqLong { get; set; } = 0.008;

    [TunableDisplay("Ridge octaves")] [TunableRange(1, 6)]
    public int RidgeOctaves { get; set; } = 4;

    // The squared ridged fBm is always >= 0 (mean ~0.5), so adding it raw lifted the WHOLE
    // map by RidgeStrength*VerticalScale*0.5 and shrank every ocean. The ridge term is now
    // centred on this baseline: ridge crests rise, the troughs between them sink, and the
    // mean land/sea split stays where the base fBm put it.
    [TunableDisplay("Ridge baseline", Tooltip = "Subtracted from the [0,1] ridge value before it is added, so ridges do not raise the mean height. 0 = legacy (whole map lifted).")]
    [TunableRange(0.0, 1.0)]
    public double RidgeBaseline { get; set; } = 0.5;

    // -----------------------------------------------------------------------
    // Multiplicative peak erosion (ported from ProjectCog). Modulates the
    // ridge layer by `(1 - strength * erosionNoise)`, lowering tall peaks
    // while preserving the ridge silhouette. This is distinct from the
    // droplet-based HydraulicErosionPass (which simulates water flow); this
    // is a cheap "weathered peaks" tint applied at noise-composition time.
    //
    // Only affects the ridge layer — if RidgeStrength = 0, this is inert
    // regardless of PeakErosionStrength. Default PeakErosionStrength = 0
    // → fully off; existing presets unchanged.
    // -----------------------------------------------------------------------

    [TunableDisplay("Peak erosion strength", Tooltip = "Multiplicative attenuation of ridges. 0 = off. 0.3-0.6 produces noticeably weathered peaks without flattening the ridge silhouette.")]
    [TunableRange(0.0, 1.0)]
    public double PeakErosionStrength { get; set; } = 0.0;

    [TunableDisplay("Peak erosion frequency", Tooltip = "Frequency of the erosion noise. Higher = finer texture, lower = larger weathered patches.")]
    [TunableRange(0.001, 0.1)]
    public double PeakErosionFrequency { get; set; } = 0.04;

    [TunableDisplay("Peak erosion octaves")] [TunableRange(1, 4)]
    public int PeakErosionOctaves { get; set; } = 3;
}

public sealed class NoiseHeightPass : IGenerationPass
{
    public string Name => "Noise Height";
    public string Category => "Terrain";

    public IrFields Reads => IrFields.None;
    public IrFields Writes => IrFields.Height;

    public object CreateDefaultParams() => new NoiseHeightParams();

    public void Run(GenContext ctx, object parameters)
    {
        var p = (NoiseHeightParams)parameters;
        var ir = ctx.IR;
        ir.EnsureHeight();
        var z = ir.Height_Z!;

        // One noise generator per octave, deterministically seeded from the IR seed.
        // Allocate enough for both base fBm and the (optional) ridge layer.
        int octaveSlots = Math.Max(p.Octaves, p.RidgeStrength > 0 ? p.RidgeOctaves : 0);
        var noises = new OpenSimplex2[octaveSlots];
        for (int o = 0; o < octaveSlots; o++)
            noises[o] = new OpenSimplex2(unchecked((long)(ir.Seed ^ (ulong)(0xA1B2C3D4E5F60718UL * (uint)(o + 1)))));

        // Separate noise stream for the ridge layer so toggling RidgeStrength
        // doesn't re-roll the base fBm octaves' sampled positions.
        OpenSimplex2[]? ridgeNoises = null;
        if (p.RidgeStrength > 0)
        {
            ridgeNoises = new OpenSimplex2[p.RidgeOctaves];
            for (int o = 0; o < p.RidgeOctaves; o++)
                ridgeNoises[o] = new OpenSimplex2(unchecked((long)(ir.Seed ^ (ulong)(0xB12F_5A3D_77E1_9C04UL * (uint)(o + 1)))));
        }

        // Independent noise stream for peak erosion modulation. Stays inert
        // when either PeakErosionStrength or RidgeStrength is 0.
        OpenSimplex2[]? erosionNoises = null;
        bool erosionOn = p.PeakErosionStrength > 0 && p.RidgeStrength > 0;
        if (erosionOn)
        {
            erosionNoises = new OpenSimplex2[p.PeakErosionOctaves];
            for (int o = 0; o < p.PeakErosionOctaves; o++)
                erosionNoises[o] = new OpenSimplex2(unchecked((long)(ir.Seed ^ (ulong)(0x9D8A_4CF1_2E66_71B3UL * (uint)(o + 1)))));
        }

        // Separate seed for the archipelago mask noise so changing ShapeStrength doesn't
        // re-roll the terrain octaves.
        var maskNoise = new OpenSimplex2(unchecked((long)(ir.Seed ^ 0xDEAD_BEEF_CAFE_F00DUL)));

        // Resolve effective shape + strength. Back-compat: a non-zero ContinentFalloff
        // on a default Shape (Radial) wins, so old presets keep working untouched.
        var shape = p.Shape;
        double strength = p.ShapeStrength;
        if (shape == ContinentShape.Radial && p.ContinentFalloff > 0.0 && Math.Abs(p.ShapeStrength - 0.35) < 1e-9)
            strength = p.ContinentFalloff;

        var scope = ir.Scope;
        int touched = 0;

        double halfW = ir.Width * 0.5;
        double halfH = ir.Height * 0.5;
        double maxRadius = Math.Min(halfW, halfH);

        for (ushort y = scope.Y1; y <= scope.Y2; y++)
        {
            ctx.Cancellation.ThrowIfCancellationRequested();
            for (ushort x = scope.X1; x <= scope.X2; x++)
            {
                double amplitude = 1.0;
                double frequency = p.BaseFrequency;
                double sum = 0.0;
                double norm = 0.0;
                for (int o = 0; o < p.Octaves; o++)
                {
                    sum += amplitude * noises[o].Eval(x * frequency, y * frequency);
                    norm += amplitude;
                    amplitude *= p.Gain;
                    frequency *= p.Lacunarity;
                }
                double v = sum / Math.Max(norm, 1e-9);  // ~[-1, 1]

                if (ridgeNoises is not null)
                {
                    // Anisotropic ridges: sample at two perpendicular scale pairs and
                    // take the max so the strongest local ridge dominates. Result is
                    // [0, 1] (ridged-fBm convention) — add directly to v so peaks lift
                    // above the base terrain instead of carving into it.
                    double r = AnisotropicRidge(ridgeNoises, x, y, p);

                    // Multiplicative peak erosion: lower tall ridges by an independent
                    // erosion noise, preserving silhouette. erosionNoise is [0, 1]
                    // (fBm mapped from [-1, 1] → [0, 1]); strength=0 → no change.
                    if (erosionNoises is not null)
                    {
                        double e = PeakErosionFbm(erosionNoises, x, y, p);
                        r *= (1.0 - p.PeakErosionStrength * e);
                        if (r < 0) r = 0;
                    }
                    v += (r - p.RidgeBaseline) * p.RidgeStrength;
                }

                if (shape != ContinentShape.None && strength > 0.0)
                {
                    double mask = ComputeMask(shape, x, y, ir.Width, ir.Height, halfW, halfH, maxRadius, p, maskNoise);
                    v = v * (1.0 - strength) + mask * strength;
                }

                int zRaw = p.SeaLevelZ + (int)Math.Round(v * p.VerticalScale);
                if (zRaw < -128) zRaw = -128;
                else if (zRaw > 127) zRaw = 127;
                z[ir.Index(x, y)] = (sbyte)zRaw;
                touched++;
            }
        }

        ctx.Report.TilesTouched = touched;
        ctx.Report.Notes.Add($"octaves={p.Octaves} freq={p.BaseFrequency} sea={p.SeaLevelZ} vScale={p.VerticalScale} shape={shape} strength={strength:F2}");
    }

    // All masks return a value in roughly [-1, 1]: positive = land bias, negative = sea bias.
    private static double ComputeMask(
        ContinentShape shape, int x, int y, int width, int height,
        double halfW, double halfH, double maxRadius,
        NoiseHeightParams p, OpenSimplex2 maskNoise)
    {
        switch (shape)
        {
            case ContinentShape.Radial:
            {
                double dx = (x - halfW) / maxRadius;
                double dy = (y - halfH) / maxRadius;
                double r = Math.Min(1.0, Math.Sqrt(dx * dx + dy * dy));
                return (1.0 - r * r) * 2.0 - 1.0;  // 1 at centre, -1 at corners
            }
            case ContinentShape.VerticalStrait:
            {
                // Distance from vertical centreline, normalised so 0 = centre, 1 = E or W edge.
                double nx = Math.Abs((x - halfW) / halfW);
                double half = Math.Max(0.001, p.StraitWidth * 0.5);
                // Inside the strait band → fully sea (-1). Outside → ramp toward +1 at the edges.
                if (nx < half) return -1.0;
                double t = (nx - half) / Math.Max(0.001, 1.0 - half);
                // Soft N/S taper: edges of the long axis still allow some falloff so the
                // continents don't carpet to the map border.
                double ny = Math.Abs((y - halfH) / halfH);
                double yTaper = 1.0 - Math.Max(0.0, ny - 0.85) * 6.0;
                yTaper = Math.Clamp(yTaper, 0.0, 1.0);
                return (t * 2.0 - 1.0) * yTaper;
            }
            case ContinentShape.HorizontalStrait:
            {
                double ny = Math.Abs((y - halfH) / halfH);
                double half = Math.Max(0.001, p.StraitWidth * 0.5);
                if (ny < half) return -1.0;
                double t = (ny - half) / Math.Max(0.001, 1.0 - half);
                double nx = Math.Abs((x - halfW) / halfW);
                double xTaper = 1.0 - Math.Max(0.0, nx - 0.85) * 6.0;
                xTaper = Math.Clamp(xTaper, 0.0, 1.0);
                return (t * 2.0 - 1.0) * xTaper;
            }
            case ContinentShape.Archipelago:
            {
                // Low-freq noise threshold-shaped: most of the map sits below sea level,
                // a few rounded blobs poke above. Combined with the high-freq fBm, the
                // blobs become organic islands instead of perfect circles.
                double n = maskNoise.Eval(x * p.ArchipelagoScale, y * p.ArchipelagoScale);  // [-1, 1]
                // Bias toward sea: subtract a constant so only the noise peaks become land.
                double biased = n - 0.25;
                // Soft radial taper toward map edges so islands cluster centrally.
                double dx = (x - halfW) / maxRadius;
                double dy = (y - halfH) / maxRadius;
                double r = Math.Min(1.0, Math.Sqrt(dx * dx + dy * dy));
                biased -= r * r * 0.4;
                return Math.Clamp(biased * 2.0, -1.0, 1.0);
            }
            case ContinentShape.WestContinentEastArchipelago:
            {
                if (x < halfW)
                {
                    // West half: offset radial landmass centred at (1/4 W, 1/2 H).
                    // Radius capped at 70% of the half-width so the landmass leaves
                    // ~15% margin to the west edge and ~15% margin to the central
                    // seam — never touches either.
                    double cx = halfW * 0.5;
                    double cy = halfH;
                    double rMax = Math.Min(halfW * 0.7, halfH * 0.85);
                    double dx = (x - cx) / rMax;
                    double dy = (y - cy) / rMax;
                    double r = Math.Min(1.0, Math.Sqrt(dx * dx + dy * dy));
                    return (1.0 - r * r) * 2.0 - 1.0;
                }
                else
                {
                    // East half: archipelago with extra sea bias toward the central
                    // seam so the meridional strait stays open.
                    double n = maskNoise.Eval(x * p.ArchipelagoScale, y * p.ArchipelagoScale);
                    double biased = n - 0.3;
                    // 0 at seam, 1 at east edge.
                    double nxSeam = (x - halfW) / halfW;
                    double ny = Math.Abs((y - halfH) / halfH);
                    // Strong sea bias within first 10% of the east half.
                    double seamBias = Math.Max(0.0, 0.1 - nxSeam) * 8.0;
                    biased -= seamBias;
                    // Soft taper near east and N/S map edges so islands cluster centrally.
                    double edgeBias = 0.0;
                    if (nxSeam > 0.85) edgeBias = (nxSeam - 0.85) * 4.0;
                    if (ny > 0.85) edgeBias = Math.Max(edgeBias, (ny - 0.85) * 4.0);
                    biased -= edgeBias;
                    return Math.Clamp(biased * 2.0, -1.0, 1.0);
                }
            }
            default:
                return 0.0;
        }
    }

    // Anisotropic ridge: ridged-fBm sampled at two perpendicular scale pairs.
    // pair A uses (short, long) → vertical chains (long along Y), pair B uses
    // (long, short) → horizontal chains. The max combine lets either family
    // dominate any given cell, producing the directional ridges that pure
    // isotropic fBm cannot. Returns [0, 1].
    private static double AnisotropicRidge(OpenSimplex2[] ns, int x, int y, NoiseHeightParams p)
    {
        double a = RidgedFbm(ns, x * p.RidgeFreqShort, y * p.RidgeFreqLong, p);
        double b = RidgedFbm(ns, x * p.RidgeFreqLong,  y * p.RidgeFreqShort, p);
        return a > b ? a : b;
    }

    private static double RidgedFbm(OpenSimplex2[] ns, double x, double y, NoiseHeightParams p)
    {
        double sum = 0.0;
        double amp = 1.0;
        double norm = 0.0;
        double fx = x, fy = y;
        for (int o = 0; o < p.RidgeOctaves; o++)
        {
            // Ridged inversion: 1 - |noise| turns zero-crossings into sharp ridge lines.
            // Squaring further sharpens the ridge crest and pulls valleys toward zero.
            double n = 1.0 - Math.Abs(ns[o].Eval(fx, fy));
            n *= n;
            sum += n * amp;
            norm += amp;
            amp *= p.Gain;
            fx *= p.Lacunarity;
            fy *= p.Lacunarity;
        }
        return sum / Math.Max(norm, 1e-9);  // [0, 1]
    }

    // Standard fBm normalized to [0, 1] for the peak-erosion modulator.
    // Reuses the same Gain/Lacunarity progression as the base height fBm so
    // the erosion texture matches the terrain detail scale.
    private static double PeakErosionFbm(OpenSimplex2[] ns, int x, int y, NoiseHeightParams p)
    {
        double sum = 0.0, amp = 1.0, norm = 0.0;
        double fx = x * p.PeakErosionFrequency;
        double fy = y * p.PeakErosionFrequency;
        for (int o = 0; o < p.PeakErosionOctaves; o++)
        {
            sum += ns[o].Eval(fx, fy) * amp;
            norm += amp;
            amp *= p.Gain;
            fx *= p.Lacunarity;
            fy *= p.Lacunarity;
        }
        double v = sum / Math.Max(norm, 1e-9);  // [-1, 1]
        return (v + 1.0) * 0.5;                  // [0, 1]
    }
}
