using CentrED.MapGen.IR;
using CentrED.MapGen.Noise;
using CentrED.MapGen.Pipeline;

namespace CentrED.MapGen.Passes.Climate;

public sealed class MoistureClimateParams
{
    // Direction the wind blows TOWARD, in degrees: 0 = toward +X, 90 = toward +Y. The sweep
    // carries moisture from upwind sea cells downwind, so the default 180 means the wind comes
    // from the east (+X side) and blows west.
    [TunableDisplay("Wind direction (deg)", Tooltip = "Direction the wind blows TOWARD. 0 = toward +X, 90 = toward +Y. 180 = wind from the east.")]
    [TunableRange(0, 360)]
    public double WindDirectionDeg { get; set; } = 180;

    // Terrain-domain sea level (this pass runs before Biome Assign rebases heights). Presets
    // keep it in sync with Biome Assign's SeaLevelZ automatically (MapGenPreset.ApplyTo).
    [TunableDisplay("Sea level Z")] [TunableRange(-128, 127)]
    public int SeaLevelZ { get; set; } = 0;

    // Per-cell sweep gains, expressed for a ReferenceSize map. With NormaliseBySize they are
    // scaled by ReferenceSize / mapSize so a 512 island and a 7168 world see the same gradient
    // shape. The sweep only sets the ORDER of wet vs dry cells (see Equalize); the absolute
    // biome mix comes from the Biome Assign thresholds.
    [TunableDisplay("Moisture per sea cell (downwind)")] [TunableRange(0.0, 4.0)]
    public double MoisturePerSeaCell { get; set; } = 0.08;

    [TunableDisplay("Land moisture decay (per tile)")] [TunableRange(0.0, 1.0)]
    public double LandDecay { get; set; } = 0.15;

    [TunableDisplay("Mountain rain shadow (Z above sea > X)")] [TunableRange(0, 127)]
    public int RainShadowZ { get; set; } = 50;

    [TunableDisplay("Lapse rate (Z->T)")] [TunableRange(0.0, 4.0)]
    public double LapseRate { get; set; } = 1.5;

    [TunableDisplay("Base temperature")] [TunableRange(0, 255)]
    public int BaseTemperature { get; set; } = 200;

    [TunableDisplay("Latitude temp drop")] [TunableRange(0.0, 1.0)]
    public double LatitudeFalloff { get; set; } = 0.45;

    [TunableDisplay("Bidirectional", Tooltip = "Sweep moisture in both wind directions and take the max. Use for two-continent maps where one continent would otherwise be in a permanent rain shadow.")]
    public bool Bidirectional { get; set; } = false;

    // Domain warp (ported from ProjectCog): displaces the coordinate used to read moisture
    // and latitude so bands and wind streaks bend organically. On by default now — without
    // it biome edges follow perfectly straight rows/columns.
    [TunableDisplay("Domain warp amplitude", Tooltip = "Max displacement (tiles at ReferenceSize) applied to T/H sample coords. 0 = off.")]
    [TunableRange(0.0, 64.0)]
    public double DomainWarpAmplitude { get; set; } = 24.0;

    [TunableDisplay("Domain warp frequency", Tooltip = "Frequency of the warp noise at ReferenceSize. Smaller = larger, smoother swirls.")]
    [TunableRange(0.001, 0.05)]
    public double DomainWarpFrequency { get; set; } = 0.008;

    [TunableDisplay("Normalise by map size", Tooltip = "Scale sweep gains, blur radius, warp and noise by ReferenceSize / mapSize so presets behave the same at any size.")]
    public bool NormaliseBySize { get; set; } = true;

    [TunableDisplay("Reference size (tiles)", Tooltip = "Map size the per-cell / per-tile values are expressed for.")]
    [TunableRange(128, 8192)]
    public int ReferenceSize { get; set; } = 1024;

    [TunableDisplay("Coastal moisture weight", Tooltip = "Weight of the wind-independent distance-to-water term (every coast and lake shore is wetter).")]
    [TunableRange(0.0, 2.0)]
    public double CoastalWeight { get; set; } = 0.6;

    [TunableDisplay("Coastal reach (fraction of map)", Tooltip = "e-folding distance of the coastal term as a fraction of the map size.")]
    [TunableRange(0.01, 0.5)]
    public double CoastalReach { get; set; } = 0.08;

    [TunableDisplay("Moisture noise weight", Tooltip = "Weight of low-frequency fBm patches in the raw moisture — forest/grass/desert patches instead of concentric rings.")]
    [TunableRange(0.0, 2.0)]
    public double MoistureNoise { get; set; } = 0.7;

    [TunableDisplay("Moisture noise frequency", Tooltip = "Frequency of the moisture noise at ReferenceSize.")]
    [TunableRange(0.0005, 0.05)]
    public double MoistureNoiseFrequency { get; set; } = 0.006;

    [TunableDisplay("Lateral diffusion radius", Tooltip = "Box-blur radius (tiles at ReferenceSize) applied to raw moisture so the 1-D wind sweep cannot leave row streaks. 0 = off.")]
    [TunableRange(0, 64)]
    public int DiffusionRadius { get; set; } = 8;

    [TunableDisplay("Equalize", Tooltip = "1 = rank-equalised moisture (uniform 0..255 over land, thresholds map to predictable fractions at any size); 0 = raw scaled moisture.")]
    [TunableRange(0.0, 1.0)]
    public double Equalize { get; set; } = 1.0;

    [TunableDisplay("Moisture bias", Tooltip = "Added to the final land moisture. +40 = wetter world, -40 = drier.")]
    [TunableRange(-128, 128)]
    public int MoistureBias { get; set; } = 0;

    [TunableDisplay("Temperature noise ±", Tooltip = "Low-frequency noise added to temperature so latitude bands get ragged edges.")]
    [TunableRange(0, 64)]
    public int TemperatureNoise { get; set; } = 10;
}

// Moisture = rank-equalised blend of (a) a wind sweep that integrates sea fetch downwind and
// decays over land with a mountain rain shadow, (b) a wind-independent distance-to-water
// term, (c) low-frequency fBm patches. The blend is laterally diffused (box blur), read
// back through a domain warp, then rank-equalised over land so Biome Assign's thresholds
// give the same mix at 512 and 7168 tiles. Temperature = base − lapse·(Z above sea) −
// latitude(warped) + noise.
public sealed class MoistureClimatePass : IGenerationPass
{
    public string Name => "Moisture & Climate";
    public string Category => "Climate";

    public IrFields Reads => IrFields.Height;
    public IrFields Writes => IrFields.Moisture | IrFields.Temperature;

    public object CreateDefaultParams() => new MoistureClimateParams();

    public void Run(GenContext ctx, object parameters)
    {
        var p = (MoistureClimateParams)parameters;
        var ir = ctx.IR;
        ir.EnsureMoisture();
        ir.EnsureTemperature();
        var z = ir.Height_Z!;
        var moisture = ir.Moisture!;
        var temp = ir.Temperature!;
        var scope = ir.Scope;
        int sw = scope.Width, sh = scope.Height;
        int dim = Math.Max(sw, sh);
        double sizeScale = p.NormaliseBySize ? (double)p.ReferenceSize / Math.Max(1, dim) : 1.0;
        // Lengths (blur, warp, noise wavelength) grow with the map; per-cell gains shrink.
        double lengthScale = 1.0 / sizeScale;

        double rad = p.WindDirectionDeg * Math.PI / 180.0;

        // (a) wind sweep
        var acc = new float[ir.Width * ir.Height];
        SweepWind(ctx, ir, p, z, acc, Math.Cos(rad), Math.Sin(rad), sizeScale);
        if (p.Bidirectional)
        {
            var acc2 = new float[ir.Width * ir.Height];
            SweepWind(ctx, ir, p, z, acc2, -Math.Cos(rad), -Math.Sin(rad), sizeScale);
            for (int i = 0; i < acc.Length; i++)
                if (acc2[i] > acc[i]) acc[i] = acc2[i];
        }

        var raw = new float[sw * sh];
        var isWater = new bool[sw * sh];
        float accMax = 1e-6f;
        for (int ly = 0; ly < sh; ly++)
        for (int lx = 0; lx < sw; lx++)
        {
            int gi = ir.Index(scope.X1 + lx, scope.Y1 + ly);
            isWater[ly * sw + lx] = z[gi] < p.SeaLevelZ;
            if (acc[gi] > accMax) accMax = acc[gi];
        }

        // (b) distance to water (sea or lake), wind independent.
        double reach = Math.Max(2.0, p.CoastalReach * dim);
        int cap = (int)Math.Min(1 << 20, reach * 6 + 2);
        var dWater = FieldOps.Distance4(isWater, sw, sh, cap);

        // (c) low-frequency patches.
        var mNoise = FieldOps.Octaves(ir.Seed, 0x5EED_A11C_E0F7_1234UL, 4);
        double mFreq = p.MoistureNoiseFrequency / lengthScale;

        for (int ly = 0; ly < sh; ly++)
        for (int lx = 0; lx < sw; lx++)
        {
            int li = ly * sw + lx;
            int gi = ir.Index(scope.X1 + lx, scope.Y1 + ly);
            double wind = acc[gi] / accMax;
            double coast = Math.Exp(-dWater[li] / reach);
            double n = FieldOps.Fbm(mNoise, scope.X1 + lx, scope.Y1 + ly, mFreq);
            raw[li] = (float)(wind + p.CoastalWeight * coast + p.MoistureNoise * n);
        }

        int blurR = (int)Math.Round(p.DiffusionRadius * lengthScale);
        if (blurR > 0) FieldOps.BoxBlur(raw, sw, sh, blurR, passes: 2);

        OpenSimplex2? warpX = null, warpY = null;
        double warpAmp = p.DomainWarpAmplitude * lengthScale;
        double warpFreq = p.DomainWarpFrequency / lengthScale;
        if (warpAmp > 0.0)
        {
            warpX = new OpenSimplex2(unchecked((long)(ir.Seed ^ 0x4E3B_19F2_8D17_55A1UL)));
            warpY = new OpenSimplex2(unchecked((long)(ir.Seed ^ 0xC7A2_4FE0_31D9_88B7UL)));
        }
        var tNoise = FieldOps.Octaves(ir.Seed, 0x7E39_0F5A_C1D2_6B4DUL, 3);

        var warped = new float[sw * sh];
        var warpedY = new float[sw * sh];
        for (int ly = 0; ly < sh; ly++)
        for (int lx = 0; lx < sw; lx++)
        {
            int x = scope.X1 + lx, y = scope.Y1 + ly;
            double sx = x, sy = y;
            if (warpX is not null)
            {
                sx = x + warpX.Eval(x * warpFreq, y * warpFreq) * warpAmp;
                sy = y + warpY!.Eval(x * warpFreq, y * warpFreq) * warpAmp;
            }
            int rx = Math.Clamp((int)Math.Round(sx) - scope.X1, 0, sw - 1);
            int ry = Math.Clamp((int)Math.Round(sy) - scope.Y1, 0, sh - 1);
            warped[ly * sw + lx] = raw[ry * sw + rx];
            warpedY[ly * sw + lx] = (float)sy;
        }

        // Rank-equalise over land.
        var landVals = new List<float>(sw * sh);
        float rawMin = float.MaxValue, rawMax = float.MinValue;
        for (int i = 0; i < warped.Length; i++)
        {
            if (isWater[i]) continue;
            landVals.Add(warped[i]);
            if (warped[i] < rawMin) rawMin = warped[i];
            if (warped[i] > rawMax) rawMax = warped[i];
        }
        landVals.Sort();
        double span = Math.Max(1e-6, rawMax - rawMin);

        int touched = 0;
        double halfH = ir.Height * 0.5;
        for (int ly = 0; ly < sh; ly++)
        for (int lx = 0; lx < sw; lx++)
        {
            int li = ly * sw + lx;
            int x = scope.X1 + lx, y = scope.Y1 + ly;
            int idx = ir.Index(x, y);
            double m;
            if (isWater[li]) m = 255;
            else
            {
                double rank = landVals.Count > 1 ? (double)LowerBound(landVals, warped[li]) / (landVals.Count - 1) : 0.5;
                double lin = (warped[li] - rawMin) / span;
                m = 255.0 * (p.Equalize * rank + (1.0 - p.Equalize) * lin) + p.MoistureBias;
            }
            moisture[idx] = (byte)Math.Clamp((int)Math.Round(m), 0, 255);

            double latNorm = Math.Min(1.0, Math.Abs((warpedY[li] - halfH) / halfH));
            double latDrop = latNorm * latNorm * p.LatitudeFalloff * 255.0;
            double tn = p.TemperatureNoise > 0 ? FieldOps.Fbm(tNoise, x, y, 0.004 / lengthScale) * p.TemperatureNoise : 0;
            int zAbove = Math.Max(0, z[idx] - p.SeaLevelZ);
            int t = (int)Math.Round(p.BaseTemperature - p.LapseRate * zAbove - latDrop + tn);
            temp[idx] = (byte)Math.Clamp(t, 0, 255);
            touched++;
        }
        ctx.Report.TilesTouched = touched;
        ctx.Report.Notes.Add($"size scale={sizeScale:F2} blur r={blurR} warp amp={warpAmp:F1} equalize={p.Equalize:F2} bias={p.MoistureBias}");
    }

    private static int LowerBound(List<float> sorted, float v)
    {
        int lo = 0, hi = sorted.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (sorted[mid] < v) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    private static void SweepWind(GenContext ctx, GenIR ir, MoistureClimateParams p,
                                  sbyte[] z, float[] acc, double dx, double dy, double sizeScale)
    {
        Array.Clear(acc, 0, acc.Length);
        int xStart = dx >= 0 ? ir.Scope.X1 : ir.Scope.X2;
        int xEnd = dx >= 0 ? ir.Scope.X2 + 1 : ir.Scope.X1 - 1;
        int xStep = dx >= 0 ? 1 : -1;
        int yStart = dy >= 0 ? ir.Scope.Y1 : ir.Scope.Y2;
        int yEnd = dy >= 0 ? ir.Scope.Y2 + 1 : ir.Scope.Y1 - 1;
        int yStep = dy >= 0 ? 1 : -1;
        int rdx = (int)Math.Round(dx);
        int rdy = (int)Math.Round(dy);
        float sea = (float)(p.MoisturePerSeaCell * sizeScale);
        float decay = (float)(p.LandDecay * sizeScale);
        for (int y = yStart; y != yEnd; y += yStep)
        {
            ctx.Cancellation.ThrowIfCancellationRequested();
            for (int x = xStart; x != xEnd; x += xStep)
            {
                int ux = x - rdx;
                int uy = y - rdy;
                float upwind = 0f;
                if (ux >= ir.Scope.X1 && uy >= ir.Scope.Y1 && ux <= ir.Scope.X2 && uy <= ir.Scope.Y2)
                    upwind = acc[uy * ir.Width + ux];
                int idx = ir.Index(x, y);
                float v = upwind;
                if (z[idx] < p.SeaLevelZ)
                    v += sea;
                else
                {
                    v -= decay;
                    if (z[idx] - p.SeaLevelZ > p.RainShadowZ)
                        v -= decay * 4;
                }
                if (v < 0f) v = 0f;
                if (v > 255f) v = 255f;
                acc[idx] = v;
            }
        }
    }
}
