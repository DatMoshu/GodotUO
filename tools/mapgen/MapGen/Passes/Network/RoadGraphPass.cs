using CentrED.MapGen.Data;
using CentrED.MapGen.IR;
using CentrED.MapGen.Pipeline;

namespace CentrED.MapGen.Passes.Network;

public sealed class RoadGraphParams
{
    [TunableDisplay("Slope penalty")] [TunableRange(0, 32)]
    public int SlopePenalty { get; set; } = 6;

    // Water itself is never crossed. This is the extra cost (x1/16 per step) of walking a
    // cell that touches water, so roads keep off beaches and river banks.
    [TunableDisplay("Water penalty", Tooltip = "Cost/16 added per step on a cell next to water (roads keep a tile off the shore). Water cells are impassable regardless.")]
    [TunableRange(0, 1024)]
    public int WaterPenalty { get; set; } = 256;

    [TunableDisplay("Existing-road bonus", Tooltip = "Discount per step on a cell an earlier road already uses, so roads merge into a network instead of running in parallel.")]
    [TunableRange(0, 8)]
    public int ExistingRoadBonus { get; set; } = 6;

    [TunableDisplay("Mountain penalty", Tooltip = "Extra cost per step through Mountain biome (impassable rock in UO); roads go around ranges.")]
    [TunableRange(0, 4096)]
    public int MountainPenalty { get; set; } = 400;

    [TunableDisplay("Mountain clearance (tiles)", Tooltip = "Roads pay extra within this many tiles of a mountain, falling off with distance, so they keep a margin from the range's foot instead of hugging it. 0 = off.")]
    [TunableRange(0, 32)]
    public int MountainClearance { get; set; } = 6;

    [TunableDisplay("Mountain clearance penalty", Tooltip = "Extra cost per step on a cell next to a mountain; it falls linearly to 0 at the clearance distance.")]
    [TunableRange(0, 1024)]
    public int MountainClearancePenalty { get; set; } = 40;

    // Ignored: water is decided by biome (GenIR.SeaLevelZ frame). Kept for preset compat.
    [TunableDisplay("Sea level Z (legacy, ignored)")] [TunableRange(-128, 127)]
    public int SeaLevelZ { get; set; } = 0;

    [TunableDisplay("Max edges per POI")] [TunableRange(0, 8)]
    public int MaxEdgesPerPoi { get; set; } = 2;

    [TunableDisplay("Max search nodes per edge")] [TunableRange(1024, 1_000_000)]
    public int MaxSearchNodes { get; set; } = 200_000;

    [TunableDisplay("Roads end at town gate",
        Tooltip = "When the destination POI has a Footprint, terminate A* at the gate cell closest to the source (instead of the POI centre). Keeps inter-town roads from cutting through a town's interior.")]
    public bool RoadEndsAtTownGate { get; set; } = true;

    [TunableDisplay("Foreign footprint penalty",
        Tooltip = "Extra A* step cost for cells inside another town's footprint (not from / not to). High value pushes roads to route AROUND third-party towns instead of through them.")]
    [TunableRange(0, 4096)]
    public int ForeignFootprintPenalty { get; set; } = 512;
}

// Connects each POI to its nearest few POIs by A* over a height/water cost field.
// 4-connected (UO movement and road tiles read as continuous only edge-to-edge),
// water impassable, shore/mountain/slope penalised, earlier roads discounted.
// Only records polylines in ir.Roads; Road Centerline paints them (width, Z, edges).
public sealed class RoadGraphPass : IGenerationPass
{
    public string Name => "Road Graph";
    public string Category => "Network";

    public IrFields Reads => IrFields.Pois | IrFields.Height;
    public IrFields Writes => IrFields.Roads;

    public object CreateDefaultParams() => new RoadGraphParams();

    public void Run(GenContext ctx, object parameters)
    {
        var p = (RoadGraphParams)parameters;
        var ir = ctx.IR;
        if (ir.Height_Z is null || ir.Pois.Count < 2) return;

        // Build neighbour graph on POIs by k-nearest, dedup undirected edges.
        var poi = ir.Pois;
        var edges = new HashSet<(int, int)>();
        for (int i = 0; i < poi.Count; i++)
        {
            var distances = new List<(int otherIdx, int dist2)>();
            for (int j = 0; j < poi.Count; j++)
            {
                if (i == j) continue;
                int dx = poi[i].X - poi[j].X;
                int dy = poi[i].Y - poi[j].Y;
                distances.Add((j, dx * dx + dy * dy));
            }
            distances.Sort((a, b) => a.dist2.CompareTo(b.dist2));
            int take = Math.Min(p.MaxEdgesPerPoi, distances.Count);
            for (int k = 0; k < take; k++)
            {
                int a = i, b = distances[k].otherIdx;
                if (a > b) (a, b) = (b, a);
                edges.Add((a, b));
            }
        }

        // Build a fast cellIndex -> owning-POI-Id lookup over every town footprint, so
        // the A* step function can cheaply penalise "this neighbour cell belongs to a
        // foreign town". Without this, an inter-town road can cut straight through any
        // unrelated town between source and destination.
        var footprintOwner = BuildFootprintOwnerMap(ir);
        var nearMountain = MountainDistance(ir, p.MountainClearance);

        int painted = 0;
        var used = new HashSet<int>();
        foreach (var (i, j) in edges.OrderBy(e => e.Item1).ThenBy(e => e.Item2))
        {
            ctx.Cancellation.ThrowIfCancellationRequested();
            var from = poi[i];
            var to = poi[j];
            // Resolve the actual start/end cells. If RoadEndsAtTownGate and a POI has a
            // footprint, pick the gate cell closest to the other endpoint's centre.
            var startCell = PickEndpoint(from, to, p);
            var goalCell  = PickEndpoint(to, from, p);

            var path = AStar(ir, startCell, goalCell, from.Id, to.Id, footprintOwner, used, p, nearMountain);
            if (path is null) continue;
            var seg = new RoadSegment
            {
                Kind = RoadKind.Dirt,
                FromPoiId = from.Id,
                ToPoiId = to.Id,
            };
            foreach (var (x, y) in path)
            {
                seg.Path.Add((x, y));
                used.Add(ir.Index(x, y));
                painted++;
            }
            ir.Roads.Add(seg);
        }
        ctx.Report.TilesTouched = painted;
        ctx.Report.Notes.Add($"roads={ir.Roads.Count}");
    }

    // Resolve the A* endpoint cell for `self` given the other endpoint `other`. If the
    // user opted into gate-based termination AND self has Gates declared, pick the gate
    // cell minimising straight-line distance to `other`'s centre. Otherwise return the
    // POI's own centre cell (legacy behaviour).
    private static (ushort X, ushort Y) PickEndpoint(PoiStamp self, PoiStamp other, RoadGraphParams p)
    {
        if (!p.RoadEndsAtTownGate || self.Gates is null || self.Gates.Count == 0)
            return (self.X, self.Y);
        (ushort X, ushort Y) best = self.Gates[0];
        long bestSq = long.MaxValue;
        foreach (var g in self.Gates)
        {
            long dx = g.X - other.X;
            long dy = g.Y - other.Y;
            long sq = dx * dx + dy * dy;
            if (sq < bestSq) { bestSq = sq; best = g; }
        }
        return best;
    }

    // Flat dictionary: cellIndex -> PoiId for any cell that lies inside ANY POI's
    // footprint. Built once per Run() and reused across every A* edge. Memory is
    // bounded by total town area (≤ ~96 towns * 24*24 cells ≈ 55k entries on full
    // Felucca with the default knobs).
    private static Dictionary<int, int> BuildFootprintOwnerMap(GenIR ir)
    {
        var map = new Dictionary<int, int>();
        foreach (var poi in ir.Pois)
        {
            if (poi.Footprint is not { } rect) continue;
            for (int y = rect.Y1; y <= rect.Y2; y++)
            for (int x = rect.X1; x <= rect.X2; x++)
                map[ir.Index(x, y)] = poi.Id;
        }
        return map;
    }

    private static List<(ushort X, ushort Y)>? AStar(
        GenIR ir,
        (ushort X, ushort Y) startCell,
        (ushort X, ushort Y) goalCell,
        int fromPoiId,
        int toPoiId,
        Dictionary<int, int> footprintOwner,
        HashSet<int> used,
        RoadGraphParams p,
        byte[]? nearMountain = null)
    {
        // Compact A* with a binary-heap open set keyed by composite int (gScore for tie-break).
        // Encodes nodes as int = y * Width + x.
        int width = ir.Width;
        int startIdx = startCell.Y * width + startCell.X;
        int goalIdx = goalCell.Y * width + goalCell.X;

        var gScore = new Dictionary<int, int>();
        var came = new Dictionary<int, int>();
        var open = new PriorityQueue<int, long>();
        gScore[startIdx] = 0;
        open.Enqueue(startIdx, (long)Heuristic(startCell.X, startCell.Y, goalCell.X, goalCell.Y) << 20);

        // Tie-break among equal-cost 4-connected paths by distance from the straight
        // start-goal line: on open ground the road becomes a diagonal staircase (as
        // Britannia's roads are) instead of one long L.
        int ldx = goalCell.X - startCell.X, ldy = goalCell.Y - startCell.Y;
        double llen = Math.Max(1.0, Math.Sqrt((double)ldx * ldx + (double)ldy * ldy));
        long CrossTrack(int x, int y)
            => Math.Min(0xF_FFFFL, (long)(Math.Abs((double)(x - startCell.X) * ldy - (double)(y - startCell.Y) * ldx) / llen * 16.0));

        int explored = 0;
        ReadOnlySpan<(int dx, int dy, int cost)> moves = stackalloc (int, int, int)[]
        {
            (1, 0, 10), (-1, 0, 10), (0, 1, 10), (0, -1, 10),
        };

        while (open.TryDequeue(out var current, out _))
        {
            if (current == goalIdx) return Reconstruct(came, current, width);
            if (++explored > p.MaxSearchNodes) return null;

            int cx = current % width;
            int cy = current / width;
            int curG = gScore[current];

            foreach (var m in moves)
            {
                int nx = cx + m.dx;
                int ny = cy + m.dy;
                if (nx < ir.Scope.X1 || ny < ir.Scope.Y1 || nx > ir.Scope.X2 || ny > ir.Scope.Y2) continue;

                int nIdx = ny * width + nx;
                int stepCost = m.cost;

                int nz = ir.Height_Z![nIdx];
                int cz = ir.Height_Z[current];
                stepCost += Math.Abs(nz - cz) * p.SlopePenalty;
                // Hard reject water cells outright — roads in water look terrible.
                if (IsWater(ir, nIdx)) continue;
                if (ir.Biome is not null && (BiomeId)ir.Biome[nIdx] is BiomeId.Mountain or BiomeId.HighMountain)
                    stepCost += p.MountainPenalty;
                else if (nearMountain is not null && nearMountain[nIdx] > 0)
                    stepCost += p.MountainClearancePenalty * (p.MountainClearance + 1 - nearMountain[nIdx]) / p.MountainClearance;
                if (p.WaterPenalty > 0 && TouchesWater(ir, nx, ny))
                    stepCost += p.WaterPenalty / 16;
                if (used.Contains(nIdx))
                    stepCost = Math.Max(2, stepCost - p.ExistingRoadBonus);

                // Foreign-footprint penalty: discourage cutting through some third
                // town's footprint en route between two unrelated towns. The endpoints'
                // own footprints (from / to) are exempt — A* still has to enter them to
                // reach the goal.
                if (p.ForeignFootprintPenalty > 0 && footprintOwner.TryGetValue(nIdx, out var ownerId))
                {
                    if (ownerId != fromPoiId && ownerId != toPoiId)
                        stepCost += p.ForeignFootprintPenalty;
                }

                int tentative = curG + stepCost;
                if (gScore.TryGetValue(nIdx, out var prevG) && prevG <= tentative) continue;
                gScore[nIdx] = tentative;
                came[nIdx] = current;
                int h = Heuristic(nx, ny, goalCell.X, goalCell.Y);
                open.Enqueue(nIdx, ((long)(tentative + h) << 20) | CrossTrack(nx, ny));
            }
        }
        return null;
    }

    // Distance in tiles (4-connected, 1..clearance) from each cell to the nearest Mountain or
    // HighMountain cell; 0 on the rock itself and beyond the clearance. Null when off.
    private static byte[]? MountainDistance(GenIR ir, int clearance)
    {
        if (clearance <= 0 || ir.Biome is null) return null;
        int w = ir.Width, h = ir.Height;
        var dist = new byte[w * h];
        var seen = new bool[w * h];
        var q = new Queue<int>();
        for (int i = 0; i < w * h; i++)
            if ((BiomeId)ir.Biome[i] is BiomeId.Mountain or BiomeId.HighMountain) { seen[i] = true; q.Enqueue(i); }
        // One buffer for the whole walk: stackalloc memory lives until the method returns, so a
        // stackalloc inside this loop would pile up 16 bytes per dequeued cell and overflow the
        // thread stack on a large map.
        Span<int> nbs = stackalloc int[4];
        while (q.Count > 0)
        {
            int c = q.Dequeue();
            int d = dist[c];
            if (d >= clearance) continue;
            int cx = c % w, cy = c / w;
            nbs[0] = cx > 0 ? c - 1 : -1; nbs[1] = cx < w - 1 ? c + 1 : -1; nbs[2] = cy > 0 ? c - w : -1; nbs[3] = cy < h - 1 ? c + w : -1;
            foreach (int nb in nbs)
            {
                if (nb < 0 || seen[nb]) continue;
                seen[nb] = true;
                dist[nb] = (byte)(d + 1);
                q.Enqueue(nb);
            }
        }
        return dist;
    }

    // Manhattan at the plain step cost. Slightly inadmissible once existing-road discounts
    // apply (a greedier search), which keeps long edges inside MaxSearchNodes.
    private static int Heuristic(int x1, int y1, int x2, int y2)
        => 10 * (Math.Abs(x1 - x2) + Math.Abs(y1 - y2));

    private static bool IsWater(GenIR ir, int idx)
    {
        if (ir.Biome is not null && (BiomeId)ir.Biome[idx] is BiomeId.DeepWater or BiomeId.ShallowWater or BiomeId.River)
            return true;
        return ir.LandId is not null && TileFlags.IsWaterLandId(ir.LandId[idx]);
    }

    private static bool TouchesWater(GenIR ir, int x, int y)
    {
        if (x > 0 && IsWater(ir, ir.Index(x - 1, y))) return true;
        if (x + 1 < ir.Width && IsWater(ir, ir.Index(x + 1, y))) return true;
        if (y > 0 && IsWater(ir, ir.Index(x, y - 1))) return true;
        if (y + 1 < ir.Height && IsWater(ir, ir.Index(x, y + 1))) return true;
        return false;
    }

    private static List<(ushort X, ushort Y)> Reconstruct(Dictionary<int, int> came, int end, int width)
    {
        var path = new List<(ushort X, ushort Y)>();
        int cur = end;
        path.Add(((ushort)(cur % width), (ushort)(cur / width)));
        while (came.TryGetValue(cur, out var prev))
        {
            cur = prev;
            path.Add(((ushort)(cur % width), (ushort)(cur / width)));
        }
        path.Reverse();
        return path;
    }
}
