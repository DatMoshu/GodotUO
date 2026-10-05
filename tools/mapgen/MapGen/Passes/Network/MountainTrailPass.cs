using CentrED.MapGen.IR;
using CentrED.MapGen.Noise;
using CentrED.MapGen.Pipeline;

namespace CentrED.MapGen.Passes.Network;

public sealed class MountainTrailParams
{
    [TunableDisplay("Enabled", Tooltip = "Lay a dirt trail across every large mountain range, following its lowest passes.")]
    public bool Enabled { get; set; } = true;

    [TunableDisplay("Min range size (cells)", Tooltip = "Ranges smaller than this get no trail.")]
    [TunableRange(16, 100_000)]
    public int MinRegionSize { get; set; } = 400;

    [TunableDisplay("Trail width (tiles)")] [TunableRange(1, 4)]
    public int PathWidth { get; set; } = 2;

    [TunableDisplay("Height cost", Tooltip = "A* cost per Z above the range's foot: higher = the trail hugs valleys and saddles.")]
    [TunableRange(0, 32)]
    public int HeightCost { get; set; } = 3;

    [TunableDisplay("Slope cost", Tooltip = "A* cost per Z of step between neighbours.")]
    [TunableRange(0, 64)]
    public int SlopeCost { get; set; } = 8;

    [TunableDisplay("Profile smoothing (tiles)", Tooltip = "Half-window of the moving average along the trail's Z.")]
    [TunableRange(0, 16)]
    public int ProfileWindow { get; set; } = 4;

    [TunableDisplay("Max Z step along trail")] [TunableRange(1, 8)]
    public int MaxStep { get; set; } = 3;

    [TunableDisplay("Rock chance (0-1)", Tooltip = "Chance a trail tile gets a small rock static.")]
    [TunableRange(0.0, 1.0)]
    public double RockChance { get; set; } = 0.05;

    [TunableDisplay("Min rock spacing (tiles)")] [TunableRange(1, 8)]
    public int RockMinSpacing { get; set; } = 4;

    [TunableDisplay("Max search nodes per range")] [TunableRange(1024, 4_000_000)]
    public int MaxSearchNodes { get; set; } = 400_000;

    [TunableDisplay("Mines per 1000 rock cells", Tooltip = "Open dirt pockets inside each range (where Britannia's ranges have mining spots and cave mouths), each joined to the trail or the range's edge by a branch path. 0 = none.")]
    [TunableRange(0.0, 4.0)]
    public double MinesPerThousandCells { get; set; } = 0.3;

    [TunableDisplay("Mine radius (tiles)", Tooltip = "Pocket radius; branch paths are 3 tiles wide.")] [TunableRange(1, 12)]
    public int MineRadius { get; set; } = 5;

    [TunableDisplay("Mine min depth (tiles)", Tooltip = "How far inside the rock a pocket sits, at least.")]
    [TunableRange(1, 64)]
    public int MineMinDepth { get; set; } = 6;

    [TunableDisplay("Mine spacing (tiles)")] [TunableRange(4, 256)]
    public int MineSpacing { get; set; } = 24;
}

// Replaces the old Scatter "Mountain Path" (a sine wave across each map column that dug a
// 12-wide, 25-Z trench through the ridge). Same pass Name so presets keep working.
//
// For each mountain range (connected Mountain/HighMountain cells, >= MinRegionSize):
//  - the two boundary cells farthest apart (two BFS sweeps) are the trail ends;
//  - A* through the range with cost = step + HeightCost*(z - foot) + SlopeCost*|dz|, so
//    the trail climbs to the lowest saddle instead of a fixed line;
//  - the trail is PathWidth wide, dirt 0x71-0x78, Biome=Road, its Z a smoothed profile
//    of the ground it crosses (it sits IN the rock, no trench);
//  - the land beside the trail outside the range gets "<biome> -> Dirt" edge tiles; the
//    rock beside it is left to Cliff Edge, which lines trails with cliff faces;
//  - a few small rocks are scattered on the trail;
//  - mines: open dirt pockets deep in the range (MinesPerThousandCells, MineRadius), each with a
//    branch path to the trail or out of the range, as Britannia's ranges have mining spots.
public sealed class MountainTrailPass : IGenerationPass
{
    public string Name => "Mountain Path";
    public string Category => "Network";

    public IrFields Reads => IrFields.Biome | IrFields.LandId | IrFields.Height;
    public IrFields Writes => IrFields.LandId | IrFields.Height | IrFields.StaticOps;

    public object CreateDefaultParams() => new MountainTrailParams();

    private static readonly ushort[] PathRocks = { 0x1363, 0x1364, 0x1365, 0x1366, 0x1367 };

    public void Run(GenContext ctx, object parameters)
    {
        var p = (MountainTrailParams)parameters;
        if (!p.Enabled) return;
        var ir = ctx.IR;
        if (ir.Biome is null || ir.LandId is null || ir.Height_Z is null) return;
        var b = ir.Biome;
        var z = ir.Height_Z;
        var l = ir.LandId;
        var scope = ir.Scope;
        int sw = scope.Width, sh = scope.Height;
        int n = sw * sh;

        int G(int li) => ir.Index(scope.X1 + li % sw, scope.Y1 + li / sw);
        bool IsRock(int li) => (BiomeId)b[G(li)] is BiomeId.Mountain or BiomeId.HighMountain;

        var label = new int[n];
        var regions = new List<List<int>>();
        var q = new Queue<int>();
        for (int i = 0; i < n; i++)
        {
            if (label[i] != 0 || !IsRock(i)) continue;
            var cells = new List<int>();
            int id = regions.Count + 1;
            label[i] = id;
            q.Enqueue(i);
            while (q.Count > 0)
            {
                int c = q.Dequeue();
                cells.Add(c);
                foreach (int nb in N4(c))
                    if (label[nb] == 0 && IsRock(nb)) { label[nb] = id; q.Enqueue(nb); }
            }
            regions.Add(cells);
        }

        var trail = new HashSet<int>(); // global indices
        int trails = 0;
        for (int r = 0; r < regions.Count; r++)
        {
            ctx.Cancellation.ThrowIfCancellationRequested();
            var cells = regions[r];
            if (cells.Count < p.MinRegionSize) continue;
            int id = r + 1;
            // Boundary cells of the range.
            var boundary = cells.Where(c => N4(c).Any(nb => label[nb] != id)).ToList();
            if (boundary.Count < 2) continue;
            int a = Farthest(boundary[0], id);
            int bEnd = Farthest(a, id);
            if (a == bEnd) continue;
            int foot = int.MaxValue;
            foreach (int c in boundary) foot = Math.Min(foot, z[G(c)]);
            var path = AStar(a, bEnd, id, foot);
            if (path is null || path.Count < 8) continue;

            var pts = path.Select(li => ((ushort)(scope.X1 + li % sw), (ushort)(scope.Y1 + li / sw))).ToList();
            var prof = RoadPaint.SmoothProfile(ir, pts, p.ProfileWindow, Math.Max(1, p.MaxStep));
            int lo = -(Math.Max(1, p.PathWidth) - 1) / 2, hi = lo + Math.Max(1, p.PathWidth) - 1;
            for (int k = 0; k < pts.Count; k++)
            {
                var (cx, cy) = pts[k];
                for (int dy = lo; dy <= hi; dy++)
                for (int dx = lo; dx <= hi; dx++)
                {
                    int x = cx + dx, y = cy + dy;
                    if (x < scope.X1 || y < scope.Y1 || x > scope.X2 || y > scope.Y2) continue;
                    int g = ir.Index(x, y);
                    if (RoadPaint.IsWater(ir, g) || trail.Contains(g)) continue;
                    l[g] = LatticePick.Pick(RoadPaint.DirtTiles, x, y, ir.Seed);
                    z[g] = (sbyte)Math.Clamp(prof[k], sbyte.MinValue, sbyte.MaxValue);
                    b[g] = (byte)BiomeId.Road;
                    trail.Add(g);
                }
            }
            trails++;
        }

        // Mines: open pockets deep in each range, each with a branch path to the trail or the edge.
        int mines = 0;
        if (p.MinesPerThousandCells > 0)
        {
            for (int r = 0; r < regions.Count; r++)
            {
                ctx.Cancellation.ThrowIfCancellationRequested();
                var cells = regions[r];
                if (cells.Count < p.MinRegionSize) continue;
                int id = r + 1;
                int want = (int)Math.Round(cells.Count * p.MinesPerThousandCells / 1000.0);
                if (want <= 0) continue;
                // depth: rock cells from the range's edge
                var depth = new Dictionary<int, int>();
                var dq = new Queue<int>();
                foreach (int c in cells)
                    if (N4(c).Any(nb => label[nb] != id)) { depth[c] = 1; dq.Enqueue(c); }
                while (dq.Count > 0)
                {
                    int c = dq.Dequeue();
                    foreach (int nb in N4(c))
                        if (label[nb] == id && !depth.ContainsKey(nb)) { depth[nb] = depth[c] + 1; dq.Enqueue(nb); }
                }
                var picks = new List<int>();
                foreach (int c in cells.Where(c => depth[c] >= p.MineMinDepth && !trail.Contains(G(c)))
                                       .OrderBy(c => FieldOps.Hash(c % sw, c / sw, ir.Seed ^ 0x3171EUL)))
                {
                    int cx = c % sw, cy = c / sw;
                    if (picks.Any(o => Math.Max(Math.Abs(o % sw - cx), Math.Abs(o / sw - cy)) < p.MineSpacing)) continue;
                    picks.Add(c);
                    if (picks.Count >= want) break;
                }
                foreach (int pocket in picks)
                {
                    var branch = BranchToOpen(pocket, id);
                    if (branch is null) continue;
                    var pts = branch.Select(li => ((ushort)(scope.X1 + li % sw), (ushort)(scope.Y1 + li / sw))).ToList();
                    var prof = RoadPaint.SmoothProfile(ir, pts, p.ProfileWindow, Math.Max(1, p.MaxStep));
                    for (int k = 0; k < pts.Count; k++)
                        Carve(pts[k].Item1, pts[k].Item2, prof[k], k == 0 ? p.MineRadius : 1, 0);
                    mines++;
                }
            }
        }

        int edges = RoadPaint.PaintEdges(ir, trail, "Dirt", skipMountain: true);

        int rocks = 0;
        int spacing = Math.Max(1, p.RockMinSpacing);
        var placed = new List<(int X, int Y)>();
        foreach (int g in trail.OrderBy(v => v))
        {
            int x = g % ir.Width, y = g / ir.Width;
            if (FieldOps.Hash(x, y, ir.Seed ^ 0x7051UL) / (double)uint.MaxValue >= p.RockChance) continue;
            if (placed.Any(r => Math.Abs(r.X - x) <= spacing && Math.Abs(r.Y - y) <= spacing)) continue;
            ushort rock = PathRocks[FieldOps.Hash(y, x, ir.Seed) % (uint)PathRocks.Length];
            ir.StaticOps.Add(new StaticOp(StaticOpKind.Add, (ushort)x, (ushort)y, z[g], rock, 0));
            placed.Add((x, y));
            rocks++;
        }

        ctx.Report.StaticsAdded += rocks;
        ctx.Report.TilesTouched = trail.Count;
        ctx.Report.Notes.Add($"mountain trails={trails} over {regions.Count(c => c.Count >= p.MinRegionSize)} ranges, mines={mines}, trail cells={trail.Count}, edge tiles={edges}, rocks={rocks}");

        // Dirt in a rough disc (radius r) round x, y at height zz; a path cell when r is its half-width.
        void Carve(int x0, int y0, int zz, int r, int extra)
        {
            for (int dy = -r - extra; dy <= r + extra; dy++)
            for (int dx = -r - extra; dx <= r + extra; dx++)
            {
                int x = x0 + dx, y = y0 + dy;
                if (x < scope.X1 || y < scope.Y1 || x > scope.X2 || y > scope.Y2) continue;
                // ragged pocket rim: a hash decides the outer ring cell by cell
                int d2 = dx * dx + dy * dy;
                if (r > 1 && d2 > r * r && (d2 > (r + 1) * (r + 1) || FieldOps.Hash(x, y, ir.Seed ^ 0x9A7EUL) % 2 == 0)) continue;
                int g = ir.Index(x, y);
                if (RoadPaint.IsWater(ir, g) || trail.Contains(g)) continue;
                l[g] = LatticePick.Pick(RoadPaint.DirtTiles, x, y, ir.Seed);
                z[g] = (sbyte)Math.Clamp(zz, sbyte.MinValue, sbyte.MaxValue);
                b[g] = (byte)BiomeId.Road;
                trail.Add(g);
            }
        }

        // From a pocket, the cheapest way (same costs as the trail) to a trail cell or out of the
        // range; the path runs pocket first.
        List<int>? BranchToOpen(int start, int id)
        {
            int foot = z[G(start)];
            var gScore = new Dictionary<int, int> { [start] = 0 };
            var came = new Dictionary<int, int>();
            var open = new PriorityQueue<int, int>();
            open.Enqueue(start, 0);
            int explored = 0;
            while (open.TryDequeue(out int c, out _))
            {
                if (c != start && (label[c] != id || trail.Contains(G(c))))
                {
                    var path = new List<int> { c };
                    while (came.TryGetValue(c, out int prev)) { c = prev; path.Add(c); }
                    path.Reverse();
                    return path;
                }
                if (++explored > p.MaxSearchNodes) return null;
                int cz = z[G(c)];
                foreach (int nb in N4(c))
                {
                    if (RoadPaint.IsWater(ir, G(nb))) continue;
                    int nz = z[G(nb)];
                    int cost = 10 + p.HeightCost * Math.Max(0, nz - Math.Min(foot, cz)) + p.SlopeCost * Math.Abs(nz - cz);
                    int tentative = gScore[c] + cost;
                    if (gScore.TryGetValue(nb, out int old) && old <= tentative) continue;
                    gScore[nb] = tentative;
                    came[nb] = c;
                    open.Enqueue(nb, tentative);
                }
            }
            return null;
        }

        IEnumerable<int> N4(int c)
        {
            int cx = c % sw, cy = c / sw;
            if (cx > 0) yield return c - 1;
            if (cx < sw - 1) yield return c + 1;
            if (cy > 0) yield return c - sw;
            if (cy < sh - 1) yield return c + sw;
        }

        int Farthest(int from, int id)
        {
            var dist = new Dictionary<int, int> { [from] = 0 };
            var bq = new Queue<int>();
            bq.Enqueue(from);
            int last = from;
            while (bq.Count > 0)
            {
                int c = bq.Dequeue();
                last = c;
                foreach (int nb in N4(c))
                    if (label[nb] == id && !dist.ContainsKey(nb)) { dist[nb] = dist[c] + 1; bq.Enqueue(nb); }
            }
            return last;
        }

        List<int>? AStar(int start, int goal, int id, int foot)
        {
            var gScore = new Dictionary<int, int> { [start] = 0 };
            var came = new Dictionary<int, int>();
            var open = new PriorityQueue<int, int>();
            int gx = goal % sw, gy = goal / sw;
            open.Enqueue(start, 0);
            int explored = 0;
            while (open.TryDequeue(out int c, out _))
            {
                if (c == goal)
                {
                    var path = new List<int> { c };
                    while (came.TryGetValue(c, out int prev)) { c = prev; path.Add(c); }
                    path.Reverse();
                    return path;
                }
                if (++explored > p.MaxSearchNodes) return null;
                int cz = z[G(c)];
                foreach (int nb in N4(c))
                {
                    if (label[nb] != id) continue;
                    int nz = z[G(nb)];
                    int cost = 10 + p.HeightCost * Math.Max(0, nz - foot) + p.SlopeCost * Math.Abs(nz - cz);
                    int tentative = gScore[c] + cost;
                    if (gScore.TryGetValue(nb, out int old) && old <= tentative) continue;
                    gScore[nb] = tentative;
                    came[nb] = c;
                    int h = 10 * (Math.Abs(nb % sw - gx) + Math.Abs(nb / sw - gy));
                    open.Enqueue(nb, tentative + h);
                }
            }
            return null;
        }
    }
}
