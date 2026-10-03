using CentrED.MapGen.IR;
using CentrED.MapGen.Noise;
using CentrED.MapGen.Pipeline;

namespace CentrED.MapGen.Passes.Hydrology;

public sealed class RiverCarveParams
{
    [TunableDisplay("Eight-direction flow", Tooltip = "Allow diagonal drainage paths. Diagonal steps receive a connecting water cell so one-cell streams remain connected.")]
    public bool EightDirectionFlow { get; set; } = false;

    [TunableDisplay("Source count", Tooltip = "River sources per 1024x1024 tiles when ScaleByArea is on (a 512 map gets a quarter).")]
    [TunableRange(0, 256)]
    public int SourceCount { get; set; } = 32;

    [TunableDisplay("Scale sources by area")]
    public bool ScaleByArea { get; set; } = true;

    [TunableDisplay("Fill islets up to (cells)", Tooltip = "Land specks of up to this many cells entirely surrounded by a river/lake become water.")]
    [TunableRange(0, 64)]
    public int FillIsletArea { get; set; } = 6;

    [TunableDisplay("Meander frequency", Tooltip = "Noise that orders the flood across flat ground, so rivers wander instead of running in straight grid lines. 0 = plain breadth-first (straight).")]
    [TunableRange(0, 0.2)]
    public double MeanderFrequency { get; set; } = 0.025;

    [TunableDisplay("Min source Z (above sea)", Tooltip = "Sources are picked on land at least this high above sea level (GenIR.SeaLevelZ, i.e. after Biome Assign's rebase).")]
    [TunableRange(0, 127)]
    public int MinSourceZ { get; set; } = 6;

    [TunableDisplay("Max source Z (above sea)", Tooltip = "Sources higher than this are skipped: a river carves its valley down to the water plane, so a source on a peak would cut a canyon.")]
    [TunableRange(1, 127)]
    public int MaxSourceZ { get; set; } = 22;

    // Ignored: rivers use GenIR.SeaLevelZ / OceanZ. Kept so presets that set it still load.
    [TunableDisplay("Sea level Z (legacy, ignored)")] [TunableRange(-128, 127)]
    public int SeaLevelZ { get; set; } = 0;

    [TunableDisplay("Max path length")] [TunableRange(64, 8192)]
    public int MaxPathLength { get; set; } = 2048;

    [TunableDisplay("Min river length", Tooltip = "Paths shorter than this (source already next to the sea) are dropped.")]
    [TunableRange(2, 512)]
    public int MinLength { get; set; } = 24;

    // Legacy: the bed is now always the water plane (GenIR.OceanZ). Kept for preset compat.
    [TunableDisplay("Carve depth (legacy, ignored)")] [TunableRange(0, 8)]
    public int CarveDepth { get; set; } = 2;

    [TunableDisplay("Lake fill enabled", Tooltip = "A river that runs through a closed basin fills it as a lake (when the basin is no larger than LakeMaxArea).")]
    public bool LakeFillEnabled { get; set; } = true;

    [TunableDisplay("Lake max area")] [TunableRange(16, 100_000)]
    public int LakeMaxArea { get; set; } = 4096;

    [TunableDisplay("Lake min area", Tooltip = "Basins smaller than this are not worth a lake; the river just runs through.")]
    [TunableRange(1, 1024)]
    public int LakeMinArea { get; set; } = 12;

    [TunableDisplay("Max river width", Tooltip = "Rivers widen downstream from 1 tile up to this width.")]
    [TunableRange(1, 7)]
    public int MaxWidth { get; set; } = 3;

    [TunableDisplay("Widen every N tiles", Tooltip = "Width grows by 2 (one tile each side) every N tiles of path.")]
    [TunableRange(4, 512)]
    public int WidenEvery { get; set; } = 48;

    [TunableDisplay("Bank step (Z per tile)", Tooltip = "Banks are lowered toward the water plane with at most this Z step per tile (the shore tiles cannot render steeper).")]
    [TunableRange(1, 8)]
    public int BankStep { get; set; } = 4;

    // Legacy name for the bank band: now the band is as wide as the bank step needs.
    [TunableDisplay("Bank smoothing band (legacy, ignored)")]
    [TunableRange(0, 6)]
    public int BankBand { get; set; } = 2;
}

// Rivers and lakes as WATER BODIES with one flat surface, the way Felucca has them.
//
// 1. A priority flood from every existing water cell (or the scope border if there is no
//    water) gives each land cell a downstream pointer and its spill height. Following the
//    pointers from any source reaches water without ever climbing above the source's spill
//    height, so a lowland river never cuts through a mountain.
// 2. Sources: random land cells between MinSourceZ and MaxSourceZ above sea.
// 3. A path crossing a closed basin (spill height > ground) of LakeMinArea..LakeMaxArea
//    cells turns the whole basin into a lake.
// 4. Every river/lake cell gets Biome=River and Z = GenIR.OceanZ (one water plane, like the
//    ocean); the ground around is lowered so each tile away from the water may rise by at
//    most BankStep. LandId is written only when it already exists (normally this pass runs
//    before Land ID Resolve, which paints River from the tile table).
public sealed class RiverCarvePass : IGenerationPass
{
    public string Name => "River Carve";
    public string Category => "Hydrology";

    public IrFields Reads => IrFields.Height | IrFields.Biome;
    public IrFields Writes => IrFields.Height | IrFields.Biome | IrFields.Rivers;

    public object CreateDefaultParams() => new RiverCarveParams();

    public void Run(GenContext ctx, object parameters)
    {
        var p = (RiverCarveParams)parameters;
        var ir = ctx.IR;
        if (ir.Height_Z is null || ir.Biome is null) return;
        var z = ir.Height_Z;
        var b = ir.Biome;
        var scope = ir.Scope;
        int sw = scope.Width, sh = scope.Height;
        int n = sw * sh;
        int sea = ir.SeaLevelZ;
        int waterZ = ir.OceanZ;

        int G(int li) => ir.Index(scope.X1 + li % sw, scope.Y1 + li / sw);
        bool IsWater(int li) => (BiomeId)b[G(li)] is BiomeId.DeepWater or BiomeId.ShallowWater or BiomeId.River;

        // --- 1. priority flood -------------------------------------------------------
        // Same fill level: cells are expanded in meander-noise order (low first), so the
        // downstream pointers on flat ground follow winding noise valleys.
        var meander = new int[n];
        if (p.MeanderFrequency > 0)
        {
            var oct = FieldOps.Octaves(ir.Seed, 0x4D45_414EUL, 3);
            for (int i = 0; i < n; i++)
            {
                double v = FieldOps.Fbm(oct, scope.X1 + i % sw, scope.Y1 + i / sw, p.MeanderFrequency);
                meander[i] = Math.Clamp((int)((v + 1.0) * 2048.0), 0, 4095);
            }
        }
        var fill = new int[n];
        var down = new int[n];
        var done = new bool[n];
        var pq = new PriorityQueue<int, long>();
        long order = 0;
        bool anyWater = false;
        for (int i = 0; i < n; i++)
        {
            down[i] = -1;
            if (!IsWater(i)) continue;
            anyWater = true;
            done[i] = true;
            fill[i] = z[G(i)];
            pq.Enqueue(i, Key(fill[i], meander[i], order++));
        }
        if (!anyWater)
        {
            for (int i = 0; i < n; i++)
            {
                int lx = i % sw, ly = i / sw;
                if (lx != 0 && ly != 0 && lx != sw - 1 && ly != sh - 1) continue;
                done[i] = true;
                fill[i] = z[G(i)];
                pq.Enqueue(i, Key(fill[i], meander[i], order++));
            }
        }
        while (pq.TryDequeue(out int c, out _))
        {
            int cx = c % sw, cy = c / sw;
            if (cx > 0) Visit(c - 1);
            if (cx < sw - 1) Visit(c + 1);
            if (cy > 0) Visit(c - sw);
            if (cy < sh - 1) Visit(c + sw);
            if (p.EightDirectionFlow)
            {
                if (cx > 0 && cy > 0) Visit(c - sw - 1);
                if (cx < sw - 1 && cy > 0) Visit(c - sw + 1);
                if (cx > 0 && cy < sh - 1) Visit(c + sw - 1);
                if (cx < sw - 1 && cy < sh - 1) Visit(c + sw + 1);
            }
            void Visit(int nb)
            {
                if (done[nb]) return;
                done[nb] = true;
                fill[nb] = Math.Max(z[G(nb)], fill[c]);
                down[nb] = c;
                pq.Enqueue(nb, Key(fill[nb], meander[nb], order++));
            }
        }

        // --- 2./3. trace rivers ------------------------------------------------------
        var isRiver = new bool[n];
        var lakeTried = new bool[n];
        int lakes = 0, lakeCells = 0;
        int sourceId = 0;
        int sources = p.ScaleByArea
            ? (int)Math.Round(p.SourceCount * (double)n / (1024.0 * 1024.0))
            : p.SourceCount;
        for (int s = 0; s < sources; s++)
        {
            ctx.Cancellation.ThrowIfCancellationRequested();
            int src = -1;
            for (int tries = 0; tries < 96; tries++)
            {
                int li = ctx.Rng.Next(n);
                if (IsWater(li) || isRiver[li]) continue;
                var bio = (BiomeId)b[G(li)];
                if (bio is BiomeId.Mountain or BiomeId.HighMountain or BiomeId.Snow) continue;
                int above = z[G(li)] - sea;
                if (above < p.MinSourceZ || above > p.MaxSourceZ) continue;
                src = li;
                break;
            }
            if (src < 0) continue;

            var path = new List<int>();
            int cur = src;
            bool reached = false;
            while (cur >= 0 && path.Count < p.MaxPathLength)
            {
                if (IsWater(cur) || isRiver[cur]) { reached = true; break; }
                path.Add(cur);
                cur = down[cur];
            }
            if (!reached || path.Count < p.MinLength) continue;

            var seg = new RiverSegment { SourceId = sourceId++ };
            for (int k = 0; k < path.Count; k++)
            {
                int li = path[k];
                int width = Math.Min(p.MaxWidth, 1 + 2 * (k / Math.Max(1, p.WidenEvery)));
                int r = (width - 1) / 2;
                MarkDisc(li, r);
                if (p.EightDirectionFlow && k + 1 < path.Count)
                {
                    int next = path[k + 1];
                    if (next % sw != li % sw && next / sw != li / sw)
                    {
                        int elbowA = li / sw * sw + next % sw;
                        int elbowB = next / sw * sw + li % sw;
                        MarkDisc(z[G(elbowA)] <= z[G(elbowB)] ? elbowA : elbowB, 0);
                    }
                }
                seg.Path.Add(((ushort)(scope.X1 + li % sw), (ushort)(scope.Y1 + li / sw)));
                seg.Flow.Add(k + 1);

                if (p.LakeFillEnabled && fill[li] > z[G(li)] && !lakeTried[li])
                {
                    var basin = Basin(li);
                    foreach (int bc in basin) lakeTried[bc] = true;
                    if (basin.Count >= p.LakeMinArea && basin.Count <= p.LakeMaxArea)
                    {
                        foreach (int bc in basin)
                        {
                            if (!isRiver[bc]) lakeCells++;
                            isRiver[bc] = true;
                        }
                        lakes++;
                    }
                }
            }
            ir.Rivers.Add(seg);
        }

        // Islets: tiny land specks fully enclosed by river/lake water join the water.
        int islets = 0;
        if (p.FillIsletArea > 0)
        {
            var seen = new int[n]; // 0 = unseen, else component number
            int compNo = 0;
            var comp = new List<int>();
            var cq = new Queue<int>();
            for (int i = 0; i < n; i++)
            {
                if (seen[i] != 0 || isRiver[i] || IsWater(i)) continue;
                compNo++;
                comp.Clear();
                bool enclosed = true;
                seen[i] = compNo;
                cq.Enqueue(i);
                while (cq.Count > 0)
                {
                    int c = cq.Dequeue();
                    comp.Add(c);
                    int cx = c % sw, cy = c / sw;
                    if (cx == 0 || cy == 0 || cx == sw - 1 || cy == sh - 1) enclosed = false;
                    foreach (int nb in new[] { cx > 0 ? c - 1 : -1, cx < sw - 1 ? c + 1 : -1, cy > 0 ? c - sw : -1, cy < sh - 1 ? c + sw : -1 })
                    {
                        if (nb < 0) continue;
                        if (isRiver[nb]) continue;
                        if (IsWater(nb)) { enclosed = false; continue; }
                        if (seen[nb] != 0) { if (seen[nb] != compNo) enclosed = false; continue; }
                        seen[nb] = compNo;
                        if (comp.Count + cq.Count <= p.FillIsletArea) cq.Enqueue(nb);
                        else enclosed = false;
                    }
                }
                if (!enclosed || comp.Count > p.FillIsletArea) continue;
                foreach (int c in comp) isRiver[c] = true;
                islets++;
            }
        }

        // --- 4. water plane + banks --------------------------------------------------
        int riverCells = 0;
        var l = ir.LandId;
        var water = ir.Tables.River.Length > 0 ? ir.Tables.River : new ushort[] { 0xA8, 0xA9, 0xAA, 0xAB };
        for (int i = 0; i < n; i++)
        {
            if (!isRiver[i]) continue;
            int g = G(i);
            z[g] = (sbyte)waterZ;
            b[g] = (byte)BiomeId.River;
            if (l is not null)
            {
                int x = scope.X1 + i % sw, y = scope.Y1 + i / sw;
                l[g] = water[FieldOps.Hash(x, y, ir.Seed) % (uint)water.Length];
            }
            riverCells++;
        }
        int banks = riverCells > 0 ? LowerBanks(ir, Math.Max(1, p.BankStep)) : 0;

        ctx.Report.TilesTouched = riverCells;
        ctx.Report.Notes.Add($"rivers={ir.Rivers.Count}, river+lake cells={riverCells}, lakes={lakes} ({lakeCells} cells), islets filled={islets}, banks lowered={banks}, water Z={waterZ}");

        void MarkDisc(int li, int r)
        {
            int cx = li % sw, cy = li / sw;
            for (int dy = -r; dy <= r; dy++)
            for (int dx = -r; dx <= r; dx++)
            {
                if (dx * dx + dy * dy > r * r + r) continue;
                int x = cx + dx, y = cy + dy;
                if ((uint)x >= (uint)sw || (uint)y >= (uint)sh) continue;
                int j = y * sw + x;
                var bio = (BiomeId)b[G(j)];
                if (bio is BiomeId.Mountain or BiomeId.HighMountain) continue;
                isRiver[j] = true;
            }
        }

        // Closed basin containing li: connected cells sharing li's spill height that sit
        // below it. Bounded by LakeMaxArea + 1 so a giant flat does not get walked.
        List<int> Basin(int li)
        {
            int level = fill[li];
            var list = new List<int>();
            var q = new Queue<int>();
            var seen = new HashSet<int> { li };
            q.Enqueue(li);
            while (q.Count > 0 && list.Count <= p.LakeMaxArea)
            {
                int c = q.Dequeue();
                list.Add(c);
                int cx = c % sw, cy = c / sw;
                if (cx > 0) Push(c - 1);
                if (cx < sw - 1) Push(c + 1);
                if (cy > 0) Push(c - sw);
                if (cy < sh - 1) Push(c + sw);
                void Push(int nb)
                {
                    if (!seen.Add(nb)) return;
                    if (fill[nb] != level || z[G(nb)] >= level || IsWater(nb)) return;
                    q.Enqueue(nb);
                }
            }
            return list;
        }
    }

    /// <summary>
    /// Lowers land around every River-biome cell so Z rises by at most <paramref name="step"/>
    /// per tile away from the water plane (GenIR.OceanZ). Only lowers. Returns cells changed.
    /// Also used by the validator so later passes (coast conversion, sliver absorption,
    /// road shoulders) cannot leave a river wall behind.
    /// </summary>
    public static int LowerBanks(GenIR ir, int step)
    {
        if (ir.Biome is null || ir.Height_Z is null) return 0;
        var scope = ir.Scope;
        int sw = scope.Width, sh = scope.Height, n = sw * sh;
        var seed = new bool[n];
        bool any = false;
        for (int i = 0; i < n; i++)
        {
            int g = ir.Index(scope.X1 + i % sw, scope.Y1 + i / sw);
            if ((BiomeId)ir.Biome[g] == BiomeId.River) { seed[i] = true; any = true; }
        }
        if (!any) return 0;
        int waterZ = ir.OceanZ;
        int cap = Math.Max(2, (127 - waterZ) / step + 2);
        var dist = FieldOps.Distance4(seed, sw, sh, cap);
        int changed = 0;
        for (int i = 0; i < n; i++)
        {
            int d = dist[i];
            if (d == 0 || d >= cap) continue;
            int g = ir.Index(scope.X1 + i % sw, scope.Y1 + i / sw);
            if ((BiomeId)ir.Biome[g] is BiomeId.DeepWater or BiomeId.ShallowWater) continue;
            int target = waterZ + step * d;
            if (ir.Height_Z[g] <= target) continue;
            ir.Height_Z[g] = (sbyte)target;
            changed++;
        }
        return changed;
    }

    private static long Key(int fillZ, int meander, long order) =>
        ((long)(fillZ + 1024) << 40) | ((long)meander << 28) | (order & 0x0FFF_FFFF);
}
