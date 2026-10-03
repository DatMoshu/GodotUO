using CentrED.MapGen.IR;
using CentrED.MapGen.Pipeline;

namespace CentrED.MapGen.Passes.Terrain;

public sealed class HydraulicErosionParams
{
    [TunableDisplay("Droplet count")] [TunableRange(1000, 200_000)]
    public int DropletCount { get; set; } = 30_000;

    [TunableDisplay("Lifetime (steps)")] [TunableRange(8, 64)]
    public int Lifetime { get; set; } = 24;

    [TunableDisplay("Inertia")] [TunableRange(0.0, 1.0)]
    public double Inertia { get; set; } = 0.05;

    [TunableDisplay("Capacity factor")] [TunableRange(0.5, 16.0)]
    public double Capacity { get; set; } = 4.0;

    [TunableDisplay("Min slope")] [TunableRange(0.0, 0.05)]
    public double MinSlope { get; set; } = 0.01;

    [TunableDisplay("Erode rate")] [TunableRange(0.0, 1.0)]
    public double ErodeRate { get; set; } = 0.3;

    [TunableDisplay("Deposit rate")] [TunableRange(0.0, 1.0)]
    public double DepositRate { get; set; } = 0.3;

    [TunableDisplay("Evaporation")] [TunableRange(0.0, 0.1)]
    public double Evaporation { get; set; } = 0.01;

    [TunableDisplay("Gravity")] [TunableRange(1.0, 16.0)]
    public double Gravity { get; set; } = 4.0;

    // Terrain-domain sea level (erosion runs before Biome Assign rebases heights). Presets
    // keep it equal to Biome Assign's SeaLevelZ automatically (MapGenPreset.ApplyTo).
    [TunableDisplay("Sea level Z")] [TunableRange(-128, 127)]
    public int SeaLevelZ { get; set; } = 0;

    [TunableDisplay("Erosion brush radius", Tooltip = "Erosion is spread over a disc of this radius (Lague's erosion brush). 1 = legacy-like narrow brush, which digs single-cell pits.")]
    [TunableRange(1, 6)]
    public int BrushRadius { get; set; } = 3;

    [TunableDisplay("Scale droplets by area", Tooltip = "DropletCount is per 1024x1024 tiles; a 512 map gets a quarter, a 7168x4096 map 28x. Off = DropletCount is absolute (legacy).")]
    public bool ScaleByArea { get; set; } = true;
}

// Sebastian-Lague-style droplet hydraulic erosion adapted for sbyte heightfield.
// Works in float space, then quantizes back to sbyte. Skips droplets that start
// below sea level. Does NOT touch Moisture — that's Climate's job.
public sealed class HydraulicErosionPass : IGenerationPass
{
    public string Name => "Hydraulic Erosion";
    public string Category => "Terrain";

    public IrFields Reads => IrFields.Height;
    public IrFields Writes => IrFields.Height;

    public object CreateDefaultParams() => new HydraulicErosionParams();

    public void Run(GenContext ctx, object parameters)
    {
        var p = (HydraulicErosionParams)parameters;
        var ir = ctx.IR;
        if (ir.Height_Z is null) return;

        // Float working buffer over the full IR so indexing matches ir.Index().
        var z = new float[ir.Width * ir.Height];
        for (int i = 0; i < z.Length; i++) z[i] = ir.Height_Z[i];

        var scope = ir.Scope;
        int touched = 0;
        float sea = p.SeaLevelZ;

        int droplets = p.DropletCount;
        if (p.ScaleByArea)
            droplets = (int)Math.Clamp((long)p.DropletCount * scope.Width * scope.Height / (1024L * 1024L), 1L, 20_000_000L);

        // Erosion brush: weights over a disc, normalised to 1.
        int br = Math.Max(1, p.BrushRadius);
        var brush = new List<(int dx, int dy, float w)>();
        float wsum = 0f;
        for (int dy = -br; dy <= br; dy++)
        for (int dx = -br; dx <= br; dx++)
        {
            float dist = MathF.Sqrt(dx * dx + dy * dy);
            if (dist > br) continue;
            float w = 1f - dist / (br + 0.5f);
            brush.Add((dx, dy, w));
            wsum += w;
        }
        for (int i = 0; i < brush.Count; i++) brush[i] = (brush[i].dx, brush[i].dy, brush[i].w / wsum);

        for (int d = 0; d < droplets; d++)
        {
            if ((d & 0x3FF) == 0) ctx.Cancellation.ThrowIfCancellationRequested();
            float px = ctx.Rng.Next(scope.X1, scope.X2);
            float py = ctx.Rng.Next(scope.Y1, scope.Y2);
            // Skip droplets in water — they don't erode.
            int startIdx = ir.Index((int)px, (int)py);
            if (z[startIdx] < sea) continue;

            float dirX = 0f, dirY = 0f;
            float speed = 1f;
            float water = 1f;
            float sediment = 0f;

            for (int step = 0; step < p.Lifetime; step++)
            {
                int nx = (int)px;
                int ny = (int)py;
                if (nx <= scope.X1 || ny <= scope.Y1 || nx >= scope.X2 || ny >= scope.Y2) break;

                int idx = ir.Index(nx, ny);
                // Reached the sea: the load is lost offshore instead of being piled on the
                // seabed (which raised sandbars right along the coastline).
                if (z[idx] < sea) break;
                float hNW = z[idx];
                float hNE = z[idx + 1];
                float hSW = z[idx + ir.Width];
                float hSE = z[idx + ir.Width + 1];
                float fx = px - nx;
                float fy = py - ny;
                float gx = (hNE - hNW) * (1 - fy) + (hSE - hSW) * fy;
                float gy = (hSW - hNW) * (1 - fx) + (hSE - hNE) * fx;
                float h = hNW * (1 - fx) * (1 - fy) + hNE * fx * (1 - fy) + hSW * (1 - fx) * fy + hSE * fx * fy;

                dirX = (float)(dirX * p.Inertia - gx * (1 - p.Inertia));
                dirY = (float)(dirY * p.Inertia - gy * (1 - p.Inertia));
                float len = MathF.Sqrt(dirX * dirX + dirY * dirY);
                if (len < 1e-6f) break;
                dirX /= len; dirY /= len;
                float npx = px + dirX;
                float npy = py + dirY;

                if (npx <= scope.X1 || npy <= scope.Y1 || npx >= scope.X2 || npy >= scope.Y2) break;

                int nIdx = ir.Index((int)npx, (int)npy);
                float nfx = npx - (int)npx;
                float nfy = npy - (int)npy;
                float nh = z[nIdx] * (1 - nfx) * (1 - nfy)
                         + z[nIdx + 1] * nfx * (1 - nfy)
                         + z[nIdx + ir.Width] * (1 - nfx) * nfy
                         + z[nIdx + ir.Width + 1] * nfx * nfy;
                float dh = nh - h;

                float capacity = MathF.Max(-dh, (float)p.MinSlope) * speed * water * (float)p.Capacity;

                if (sediment > capacity || dh > 0)
                {
                    // Deposit bilinearly onto the 4 corners (Lague) — never into the sea.
                    float deposit = dh > 0
                        ? MathF.Min(dh, sediment)
                        : (sediment - capacity) * (float)p.DepositRate;
                    sediment -= deposit;
                    Deposit(z, idx, deposit * (1 - fx) * (1 - fy), sea);
                    Deposit(z, idx + 1, deposit * fx * (1 - fy), sea);
                    Deposit(z, idx + ir.Width, deposit * (1 - fx) * fy, sea);
                    Deposit(z, idx + ir.Width + 1, deposit * fx * fy, sea);
                }
                else
                {
                    // Erode through the brush so channels become valleys, not single-cell pits.
                    float erode = MathF.Min((capacity - sediment) * (float)p.ErodeRate, -dh);
                    float removed = 0f;
                    foreach (var (bdx, bdy, w) in brush)
                    {
                        int ex = nx + bdx, ey = ny + bdy;
                        if (ex < scope.X1 || ey < scope.Y1 || ex > scope.X2 || ey > scope.Y2) continue;
                        int ei = ir.Index(ex, ey);
                        // Never erode land below sea level (that punched new one-cell lakes).
                        float amt = z[ei] >= sea ? MathF.Min(erode * w, z[ei] - sea) : 0f;
                        z[ei] -= amt;
                        removed += amt;
                    }
                    sediment += removed;
                }

                speed = MathF.Sqrt(MathF.Max(0f, speed * speed + dh * (float)p.Gravity));
                water *= 1f - (float)p.Evaporation;
                px = npx; py = npy;
            }
            touched++;
        }

        for (int y = scope.Y1; y <= scope.Y2; y++)
        for (int x = scope.X1; x <= scope.X2; x++)
        {
            int i = ir.Index(x, y);
            float v = z[i];
            if (v < -128f) v = -128f;
            else if (v > 127f) v = 127f;
            ir.Height_Z[i] = (sbyte)Math.Round(v);
        }

        ctx.Report.TilesTouched = touched;
        ctx.Report.Notes.Add($"droplets={touched}/{droplets} (param {p.DropletCount}, area-scaled={p.ScaleByArea}) lifetime={p.Lifetime} brush r={br}");
    }

    private static void Deposit(float[] z, int i, float amount, float sea)
    {
        if (z[i] < sea) return;
        z[i] += amount;
    }
}
