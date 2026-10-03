using CentrED.MapGen.IR;
using CentrED.MapGen.Noise;
using CentrED.MapGen.Pipeline;

namespace CentrED.MapGen.Passes.Terrain;

public sealed class MountainShapeParams
{
    [TunableDisplay("Steep rock profile", Tooltip = "Rise rapidly from a low foot into a rock plateau, instead of smoothing the foot into the lowland.")]
    public bool SteepProfile { get; set; } = false;

    [TunableDisplay("Rock foot Z", Tooltip = "Height above sea level used by the steep profile.")]
    [TunableRange(0, 20)]
    public int FootZ { get; set; } = 2;

    // Legacy: the pass used to pick mountains by an absolute slope threshold before Biome
    // Assign. It now shapes the Mountain biome itself, so this is ignored. Kept so presets
    // that set it still load.
    [TunableDisplay("Slope threshold (legacy, ignored)", Tooltip = "Ignored. Mountains are the Mountain/HighMountain biome (Biome Assign's MountainZ).")]
    [TunableRange(0, 64)]
    public int SlopeThreshold { get; set; } = 24;

    [TunableDisplay("Min region size (cells)", Tooltip = "Mountain regions smaller than this become Grassland (lone rock specks have no clean transition).")]
    [TunableRange(1, 1024)]
    public int MinRegionSize { get; set; } = 16;

    // Legacy concentric bands. Only the highest value is used now: it sets the peak Z
    // (overrides PeakZ when non-empty). Real Felucca mountain tiles: median Z 46, p95 60,
    // peaks 60..100.
    [TunableDisplay("Altitude bands (CSV, legacy)", Tooltip = "Legacy. When set, its largest value is the peak Z. Leave empty to use PeakZ.")]
    public string AltitudeBandsCsv { get; set; } = "";

    [TunableDisplay("Peak Z", Tooltip = "Z (above sea level) a mountain reaches at the end of its ramp. Felucca peaks sit at 60..100.")]
    [TunableRange(20, 120)]
    public int PeakZ { get; set; } = 72;

    [TunableDisplay("Ramp width (tiles)", Tooltip = "Distance from the mountain edge over which height ramps from the foot to PeakZ. Narrow ranges never reach the peak.")]
    [TunableRange(2, 64)]
    public int RampTiles { get; set; } = 10;

    [TunableDisplay("Max step per tile", Tooltip = "Largest Z difference between neighbouring mountain cells after shaping (bounded with a relaxation sweep).")]
    [TunableRange(2, 20)]
    public int MaxStep { get; set; } = 7;

    [TunableDisplay("Crag noise frequency", Tooltip = "Frequency of the noise that breaks ridges into separate summits and saddles.")]
    [TunableRange(0.005, 0.3)]
    public double CragFrequency { get; set; } = 0.07;

    [TunableDisplay("Peak splatter freq (legacy, ignored)")]
    [TunableRange(0, 1000)]
    public int PeakSplatterFreq { get; set; } = 250;

    [TunableDisplay("Peak altitude variance ±", Tooltip = "Amplitude of the crag noise added on top of the ramp.")]
    [TunableRange(0, 30)]
    public int PeakAltitudeVariance { get; set; } = 10;
}

// Raises the Mountain biome into ranges. Runs AFTER Biome Assign (which rebased heights to
// sea level 0 and compressed the lowland), so mountains are exactly the cells the biome
// table calls mountain — no second, slope-based definition that disagrees with it.
//
// Height = the cell's incoming (compressed) Z + a ramp on the chamfer distance to the
// mountain edge (foot → PeakZ over RampTiles) modulated by low-frequency crag noise, then
// a min-relaxation bounds the step between neighbours to MaxStep so faces are steep but
// not single-tile cliffs. One distance transform for the whole map; no per-region arrays.
//
// Snow cells that touch a mountain region are shaped with it (snow-capped peaks).
//
// Reads:  Height + Biome.
// Writes: Height (mountain cells), Biome (tiny regions demoted to Grassland).
public sealed class MountainShapePass : IGenerationPass
{
    public string Name => "Mountain Shape";
    public string Category => "Terrain";

    public IrFields Reads => IrFields.Height | IrFields.Biome;
    public IrFields Writes => IrFields.Height | IrFields.Biome;

    public object CreateDefaultParams() => new MountainShapeParams();

    public void Run(GenContext ctx, object parameters)
    {
        var p = (MountainShapeParams)parameters;
        var ir = ctx.IR;
        if (ir.Height_Z is null || ir.Biome is null) return;
        var z = ir.Height_Z;
        var b = ir.Biome;
        var scope = ir.Scope;
        int sw = scope.Width, sh = scope.Height;

        int peakZ = ir.SeaLevelZ + p.PeakZ;
        var bands = ParseBands(p.AltitudeBandsCsv);
        if (bands.Length > 0) peakZ = ir.SeaLevelZ + bands.Max();

        // Mask: mountain + high mountain + snow (snow only where it joins a mountain region).
        var label = new int[sw * sh];
        var mask = new bool[sw * sh];
        var sizes = new List<int> { 0 };
        var hasRock = new List<bool> { false };
        var queue = new Queue<int>();
        for (int i = 0; i < label.Length; i++)
        {
            if (label[i] != 0 || !IsMountainish(b[G(i)])) continue;
            int id = sizes.Count;
            sizes.Add(0);
            hasRock.Add(false);
            label[i] = id;
            queue.Enqueue(i);
            while (queue.Count > 0)
            {
                int c = queue.Dequeue();
                sizes[id]++;
                if ((BiomeId)b[G(c)] is BiomeId.Mountain or BiomeId.HighMountain) hasRock[id] = true;
                int cx = c % sw, cy = c / sw;
                if (cx > 0) Try(c - 1);
                if (cx < sw - 1) Try(c + 1);
                if (cy > 0) Try(c - sw);
                if (cy < sh - 1) Try(c + sw);
                void Try(int n)
                {
                    if (label[n] != 0 || !IsMountainish(b[G(n)])) return;
                    label[n] = id;
                    queue.Enqueue(n);
                }
            }
        }

        int demoted = 0;
        for (int i = 0; i < label.Length; i++)
        {
            int id = label[i];
            if (id == 0 || !hasRock[id]) continue;
            if (sizes[id] < p.MinRegionSize)
            {
                if ((BiomeId)b[G(i)] is BiomeId.Mountain or BiomeId.HighMountain)
                {
                    b[G(i)] = (byte)BiomeId.Grassland;
                    demoted++;
                }
                continue;
            }
            mask[i] = true;
        }

        var dist = FieldOps.ChamferInside(mask, sw, sh); // 1/3 cell units
        var crag = FieldOps.Octaves(ir.Seed, 0x3A7F_19C2_55E1_0B4DUL, 3);
        double ramp = Math.Max(1, p.RampTiles);

        // Foot height: mean incoming Z along the region edges.
        long edgeSum = 0; int edgeN = 0;
        for (int i = 0; i < mask.Length; i++)
            if (mask[i] && dist[i] <= 3) { edgeSum += z[G(i)]; edgeN++; }
        int foot = edgeN > 0 ? (int)(edgeSum / edgeN) : ir.SeaLevelZ;
        if (p.SteepProfile) foot = ir.SeaLevelZ + p.FootZ;
        int lift = Math.Max(0, peakZ - foot);

        var nz = new int[sw * sh];
        int touched = 0;
        for (int ly = 0; ly < sh; ly++)
        for (int lx = 0; lx < sw; lx++)
        {
            int i = ly * sw + lx;
            int g = G(i);
            if (!mask[i]) { nz[i] = z[g]; continue; }
            double d = dist[i] / 3.0;
            double t = Math.Min(1.0, d / ramp);
            double shape = t * t * (3 - 2 * t); // smoothstep: gentle foot, gentle summit
            double n = FieldOps.Fbm(crag, scope.X1 + lx, scope.Y1 + ly, p.CragFrequency); // ~[-1,1]
            double h = z[g] + lift * shape * (0.8 + 0.2 * n) + p.PeakAltitudeVariance * n * t;
            if (p.SteepProfile)
                h = foot + lift * (1.0 - Math.Exp(-d * 3.0 / ramp)) + p.PeakAltitudeVariance * n * t;
            nz[i] = (int)Math.Round(p.SteepProfile ? h : Math.Max(z[g], h));
            touched++;
        }

        if (p.SteepProfile)
        {
            // Keep a low foot instead of inheriting the high noise-field skirt.
            // The cap relaxes outward over two cells and only lowers soft land.
            var near = FieldOps.Distance4(mask, sw, sh, 3);
            for (int i = 0; i < mask.Length; i++)
            {
                if (mask[i] || near[i] < 1 || near[i] > 2) continue;
                var bio = (BiomeId)b[G(i)];
                if (bio is BiomeId.DeepWater or BiomeId.ShallowWater or BiomeId.River) continue;
                int cap = foot + (near[i] - 1) * 3;
                nz[i] = Math.Min(nz[i], cap);
                z[G(i)] = (sbyte)nz[i];
            }
        }

        // Bound neighbour steps: two sweeps of z = min(z, neighbour + MaxStep) over mountain
        // cells (non-mountain cells act as fixed anchors). Converges like a distance transform.
        int step = Math.Max(1, p.MaxStep);
        for (int iter = 0; iter < 2; iter++)
        {
            for (int ly = 0; ly < sh; ly++)
            for (int lx = 0; lx < sw; lx++)
            {
                int i = ly * sw + lx;
                if (!mask[i]) continue;
                int v = nz[i];
                if (lx > 0) v = Math.Min(v, nz[i - 1] + step);
                if (ly > 0) v = Math.Min(v, nz[i - sw] + step);
                nz[i] = v;
            }
            for (int ly = sh - 1; ly >= 0; ly--)
            for (int lx = sw - 1; lx >= 0; lx--)
            {
                int i = ly * sw + lx;
                if (!mask[i]) continue;
                int v = nz[i];
                if (lx < sw - 1) v = Math.Min(v, nz[i + 1] + step);
                if (ly < sh - 1) v = Math.Min(v, nz[i + sw] + step);
                nz[i] = v;
            }
        }

        int maxZ = int.MinValue;
        for (int i = 0; i < mask.Length; i++)
        {
            if (!mask[i]) continue;
            int v = Math.Clamp(nz[i], sbyte.MinValue, sbyte.MaxValue);
            z[G(i)] = (sbyte)v;
            if (v > maxZ) maxZ = v;
        }

        ctx.Report.TilesTouched = touched;
        ctx.Report.Notes.Add($"regions={sizes.Count - 1}, demoted small={demoted}, foot={foot}, peak target={peakZ}, max={(maxZ == int.MinValue ? 0 : maxZ)}");

        int G(int li) => ir.Index(scope.X1 + li % sw, scope.Y1 + li / sw);
    }

    private static bool IsMountainish(byte biome) =>
        (BiomeId)biome is BiomeId.Mountain or BiomeId.HighMountain or BiomeId.Snow;

    private static int[] ParseBands(string csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return Array.Empty<int>();
        var parts = csv.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var list = new List<int>();
        foreach (var part in parts)
            if (int.TryParse(part, out var v)) list.Add(v);
        return list.ToArray();
    }
}
