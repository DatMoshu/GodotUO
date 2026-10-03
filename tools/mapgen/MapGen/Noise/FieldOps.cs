namespace CentrED.MapGen.Noise;

// Small dense-field helpers shared by the terrain/climate/hydrology passes. All work on
// scope-local float or int arrays (index = ly * w + lx) so a pass can allocate scope-sized
// buffers instead of full-IR ones.
public static class FieldOps
{
    /// <summary>fBm of OpenSimplex2 octaves, normalised to roughly [-1, 1].</summary>
    public static double Fbm(OpenSimplex2[] octaves, double x, double y, double frequency, double gain = 0.5, double lacunarity = 2.0)
    {
        double sum = 0, amp = 1, norm = 0, f = frequency;
        foreach (var n in octaves)
        {
            sum += amp * n.Eval(x * f, y * f);
            norm += amp;
            amp *= gain;
            f *= lacunarity;
        }
        return sum / Math.Max(norm, 1e-9);
    }

    /// <summary>Builds <paramref name="count"/> deterministic noise generators from a seed and a salt.</summary>
    public static OpenSimplex2[] Octaves(ulong seed, ulong salt, int count)
    {
        var arr = new OpenSimplex2[Math.Max(1, count)];
        for (int o = 0; o < arr.Length; o++)
            arr[o] = new OpenSimplex2(unchecked((long)(seed ^ (salt * (ulong)(o + 1)))));
        return arr;
    }

    /// <summary>Separable box blur (radius r) in place, edge-clamped. O(w*h) per pass regardless of r.</summary>
    public static void BoxBlur(float[] f, int w, int h, int r, int passes = 1)
    {
        if (r <= 0 || passes <= 0) return;
        var tmp = new float[f.Length];
        for (int p = 0; p < passes; p++)
        {
            // Horizontal: f -> tmp
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                double acc = 0;
                for (int k = -r; k <= r; k++) acc += f[row + Math.Clamp(k, 0, w - 1)];
                for (int x = 0; x < w; x++)
                {
                    tmp[row + x] = (float)(acc / (2 * r + 1));
                    acc += f[row + Math.Min(x + r + 1, w - 1)] - f[row + Math.Max(x - r, 0)];
                }
            }
            // Vertical: tmp -> f
            for (int x = 0; x < w; x++)
            {
                double acc = 0;
                for (int k = -r; k <= r; k++) acc += tmp[Math.Clamp(k, 0, h - 1) * w + x];
                for (int y = 0; y < h; y++)
                {
                    f[y * w + x] = (float)(acc / (2 * r + 1));
                    acc += tmp[Math.Min(y + r + 1, h - 1) * w + x] - tmp[Math.Max(y - r, 0) * w + x];
                }
            }
        }
    }

    /// <summary>
    /// 4-connected BFS distance (in cells) from every seed cell, capped at <paramref name="cap"/>.
    /// Unreached cells hold <paramref name="cap"/>. <paramref name="passable"/> (optional) limits
    /// which cells the wave may enter.
    /// </summary>
    public static int[] Distance4(bool[] seed, int w, int h, int cap, Func<int, bool>? passable = null)
    {
        var d = new int[w * h];
        var q = new Queue<int>();
        for (int i = 0; i < d.Length; i++)
        {
            if (seed[i]) { d[i] = 0; q.Enqueue(i); }
            else d[i] = cap;
        }
        while (q.Count > 0)
        {
            int i = q.Dequeue();
            int nd = d[i] + 1;
            if (nd >= cap) continue;
            int x = i % w, y = i / w;
            if (x > 0) Visit(i - 1);
            if (x < w - 1) Visit(i + 1);
            if (y > 0) Visit(i - w);
            if (y < h - 1) Visit(i + w);

            void Visit(int j)
            {
                if (d[j] <= nd) return;
                if (passable is not null && !passable(j)) return;
                d[j] = nd;
                q.Enqueue(j);
            }
        }
        return d;
    }

    /// <summary>
    /// Chamfer (3-4) distance transform of a mask, in units of 1/3 cell: 0 outside the mask,
    /// growing inward from the mask boundary. One forward + one backward sweep, no per-region work.
    /// </summary>
    public static int[] ChamferInside(bool[] mask, int w, int h)
    {
        const int Inf = int.MaxValue / 4;
        var d = new int[mask.Length];
        for (int i = 0; i < d.Length; i++) d[i] = mask[i] ? Inf : 0;
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = y * w + x;
            if (d[i] == 0) continue;
            int b = d[i];
            b = Math.Min(b, x > 0 ? d[i - 1] + 3 : 3);
            b = Math.Min(b, y > 0 ? d[i - w] + 3 : 3);
            b = Math.Min(b, x > 0 && y > 0 ? d[i - w - 1] + 4 : 4);
            b = Math.Min(b, x < w - 1 && y > 0 ? d[i - w + 1] + 4 : 4);
            d[i] = b;
        }
        for (int y = h - 1; y >= 0; y--)
        for (int x = w - 1; x >= 0; x--)
        {
            int i = y * w + x;
            if (d[i] == 0) continue;
            int b = d[i];
            b = Math.Min(b, x < w - 1 ? d[i + 1] + 3 : 3);
            b = Math.Min(b, y < h - 1 ? d[i + w] + 3 : 3);
            b = Math.Min(b, x < w - 1 && y < h - 1 ? d[i + w + 1] + 4 : 4);
            b = Math.Min(b, x > 0 && y < h - 1 ? d[i + w - 1] + 4 : 4);
            d[i] = b;
        }
        return d;
    }

    /// <summary>Deterministic per-cell hash in [0, 2^32).</summary>
    public static uint Hash(int x, int y, ulong seed)
    {
        uint h = unchecked((uint)(x * 73856093) ^ (uint)(y * 19349663) ^ (uint)seed ^ (uint)(seed >> 32));
        h ^= h >> 16; h *= 0x7feb352dU;
        h ^= h >> 15; h *= 0x846ca68bU;
        h ^= h >> 16;
        return h;
    }
}
