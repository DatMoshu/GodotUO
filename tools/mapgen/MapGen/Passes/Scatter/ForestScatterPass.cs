using System.Threading.Tasks;
using CentrED.MapGen.Data;
using CentrED.MapGen.IR;
using CentrED.MapGen.Pipeline;
using CentrED.MapGen.Stamps;

namespace CentrED.MapGen.Passes.Scatter;

public sealed class ForestScatterParams
{
    [TunableDisplay("Tree assembly catalogue", Tooltip = "Optional tree-only Norad catalogue. Emits complete weighted trunk/canopy assemblies, including XY/Z offsets and hues.")]
    public string TreeCataloguePath { get; set; } = "";
    [TunableDisplay("Species tables override", Tooltip = "Optional tile-table JSON for tree species and densities. Terrain tile pools are unchanged.")]
    public string SpeciesTablesPath { get; set; } = "";

    [TunableDisplay("Min radius (tiles)")] [TunableRange(2, 32)]
    public int MinRadius { get; set; } = 4;

    [TunableDisplay("Tries per active sample")] [TunableRange(4, 64)]
    public int TriesPerSample { get; set; } = 12;

    [TunableDisplay("Max slope")] [TunableRange(0, 64)]
    public int MaxSlope { get; set; } = 18;

    [TunableDisplay("Sea level offset", Tooltip = "Added to GenIR.SeaLevelZ (the rebased frame: sea level is 0).")] [TunableRange(-128, 127)]
    public int SeaLevelZ { get; set; } = 0;

    [TunableDisplay("Density multiplier")] [TunableRange(0.0, 4.0)]
    public double DensityMultiplier { get; set; } = 1.0;

    [TunableDisplay("Shore buffer (tiles)", Tooltip = "Min distance from water/beach before trees may scatter")]
    [TunableRange(0, 12)]
    public int ShoreBuffer { get; set; } = 3;

    [TunableDisplay("Reject on beach/dirt LandIds", Tooltip = "Skip tiles already painted as beach/dirt/scrub by LandTransitions")]
    [TunableRange(0, 1)]
    public int RejectOnTransitionLand { get; set; } = 1;

    [TunableDisplay("Species jitter", Tooltip = "0 = strict 32-tile groves, 1 = fully random per tree")]
    [TunableRange(0.0, 1.0)]
    public double SpeciesJitter { get; set; } = 0.5;
}

// Bridson Poisson-disc sampling within a single scope. For each placed point, picks a
// species table by biome and a random graphic from that table. Slope and water tiles are
// rejected. Density multiplier scales the per-biome target sample count.
//
// Parallelised across Y-chunks (64 rows each). Each chunk runs an independent Bridson
// instance with its own deterministically-seeded Random so output is deterministic per
// ir.Seed (the pass-name salt is a stable FNV hash, not string.GetHashCode, so two
// processes agree). Trees from each chunk are merged in chunk-index order through the
// shared occupancy grid: a tile already holding a stamp or another scatter object is
// skipped, and every tree marks its tile so later scatter/stamp passes see it.
public sealed class ForestScatterPass : IGenerationPass
{
    private const int ChunkRows = 64;
    private const ulong GoldenPrime = 0x9E3779B97F4A7C15UL;

    public string Name => "Forest Scatter";
    public string Category => "Scatter";

    public IrFields Reads => IrFields.Biome | IrFields.Height | IrFields.Slope | IrFields.LandId;
    public IrFields Writes => IrFields.StaticOps;

    public object CreateDefaultParams() => new ForestScatterParams();

    public void Run(GenContext ctx, object parameters)
    {
        var p = (ForestScatterParams)parameters;
        var ir = ctx.IR;
        if (ir.Biome is null || ir.Height_Z is null) return;
        var speciesTables = ir.Tables;
        if (!string.IsNullOrWhiteSpace(p.SpeciesTablesPath))
        {
            string path = RepoRootResolver.Resolve(p.SpeciesTablesPath);
            if (File.Exists(path)) speciesTables = TileTables.LoadOrDefault(path);
            else ctx.Report.Warnings.Add($"Species tables missing: {p.SpeciesTablesPath}; using configured species");
        }
        var Species = speciesTables.ForestSpecies;
        var assemblies = string.IsNullOrWhiteSpace(p.TreeCataloguePath) ? BiomeStaticsTable.Empty
            : BiomeStaticsTable.LoadFromNorad(RepoRootResolver.Resolve(p.TreeCataloguePath), out _);
        if (!string.IsNullOrWhiteSpace(p.TreeCataloguePath) && assemblies.ByBiome.Count == 0)
            ctx.Report.Warnings.Add("Tree assembly catalogue is empty or missing; using configured trunk/canopy pairs");
        var DensityFactor = speciesTables.ForestDensity;
        var biome = ir.Biome;
        var z = ir.Height_Z;
        var slope = ir.Slope;
        var landId = ir.LandId;
        var scope = ir.Scope;

        int sw = scope.Width;
        int sh = scope.Height;

        // BFS shore-distance (read-only after this block; safe to share across chunks).
        short[]? shoreDist = null;
        if (p.ShoreBuffer > 0 && landId is not null)
        {
            shoreDist = new short[sw * sh];
            for (int i = 0; i < shoreDist.Length; i++) shoreDist[i] = short.MaxValue;
            var sq = new Queue<(int Lx, int Ly)>();
            for (int ly = 0; ly < sh; ly++)
            for (int lx = 0; lx < sw; lx++)
            {
                int gx = scope.X1 + lx, gy = scope.Y1 + ly;
                ushort id = landId[ir.Index(gx, gy)];
                if (TileFlags.IsWaterLandId(id) || LandIdClassifier.IsBeach(id))
                {
                    shoreDist[ly * sw + lx] = 0;
                    sq.Enqueue((lx, ly));
                }
            }
            ReadOnlySpan<(int dx, int dy)> nb = stackalloc (int, int)[]
            {
                (1, 0), (-1, 0), (0, 1), (0, -1),
                (1, 1), (1, -1), (-1, 1), (-1, -1),
            };
            int maxD = p.ShoreBuffer + 1;
            while (sq.Count > 0)
            {
                var (lx, ly) = sq.Dequeue();
                short d = shoreDist[ly * sw + lx];
                if (d >= maxD) continue;
                foreach (var (dx, dy) in nb)
                {
                    int nx = lx + dx, ny = ly + dy;
                    if ((uint)nx >= (uint)sw || (uint)ny >= (uint)sh) continue;
                    if (shoreDist[ny * sw + nx] <= d + 1) continue;
                    shoreDist[ny * sw + nx] = (short)(d + 1);
                    sq.Enqueue((nx, ny));
                }
            }
        }

        int r = Math.Max(2, p.MinRadius);
        int seedStride = r * 4;
        ulong passHash = StableHash.Fnv1a64(Name);

        int numChunks = (int)Math.Ceiling((double)scope.Height / ChunkRows);
        var chunkResults = new List<Tree>[numChunks];

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount,
            CancellationToken = ctx.Cancellation,
        };

        Parallel.For(0, numChunks, options, chunkIdx =>
        {
            int chunkY1 = scope.Y1 + chunkIdx * ChunkRows;
            int chunkY2 = Math.Min(scope.Y2, chunkY1 + ChunkRows - 1);
            // Expand top edge for Poisson-disc rejection-grid overlap (read-only context).
            int sampleY1 = Math.Max(scope.Y1, chunkY1 - seedStride);

            ulong chunkSeed = ir.Seed ^ (ulong)(chunkIdx + 1) * GoldenPrime ^ passHash;
            var rng = new Random(unchecked((int)(chunkSeed ^ (chunkSeed >> 32))));

            double cellSize = r / Math.Sqrt(2);
            int cw = (int)Math.Ceiling(scope.Width / cellSize) + 1;
            int cScopeH = chunkY2 - sampleY1 + 1;
            int ch = (int)Math.Ceiling((double)cScopeH / cellSize) + 1;
            var grid = new int[cw * ch];
            var samples = new List<(double X, double Y)>();
            var active = new List<int>();

            for (int y = sampleY1; y <= chunkY2; y += seedStride)
            for (int x = scope.X1; x <= scope.X2; x += seedStride)
            {
                int sx = Math.Min(scope.X2, x + rng.Next(seedStride));
                int sy = Math.Min(chunkY2, y + rng.Next(seedStride));
                TryAdd(sx, sy);
            }

            while (active.Count > 0)
            {
                ctx.Cancellation.ThrowIfCancellationRequested();
                int idxInActive = rng.Next(active.Count);
                int sIdx = active[idxInActive];
                var (px, py) = samples[sIdx];
                bool found = false;
                for (int t = 0; t < p.TriesPerSample; t++)
                {
                    double angle = rng.NextDouble() * Math.PI * 2;
                    double radius = r * (1 + rng.NextDouble());
                    double nx = px + Math.Cos(angle) * radius;
                    double ny = py + Math.Sin(angle) * radius;
                    int nxI = (int)Math.Round(nx);
                    int nyI = (int)Math.Round(ny);
                    if (TryAdd(nxI, nyI)) { found = true; break; }
                }
                if (!found) active.RemoveAt(idxInActive);
            }

            var ops = new List<Tree>();
            foreach (var (sx, sy) in samples)
            {
                int x = (int)Math.Round(sx), y = (int)Math.Round(sy);
                // Only emit ops in this chunk's committed Y range (not the overlap zone).
                if (x < scope.X1 || x > scope.X2 || y < chunkY1 || y > chunkY2) continue;
                int idx = ir.Index(x, y);
                sbyte zv = z[idx];
                if (zv < ir.SeaLevelZ + p.SeaLevelZ) continue;
                if (landId is not null && TileFlags.IsWaterLandId(landId[idx])) continue;
                if (shoreDist is not null)
                {
                    int lx = x - scope.X1, ly = y - scope.Y1;
                    if ((uint)lx < (uint)sw && (uint)ly < (uint)sh
                        && shoreDist[ly * sw + lx] <= p.ShoreBuffer)
                        continue;
                }
                if (p.RejectOnTransitionLand != 0 && landId is not null
                    && LandIdClassifier.IsBeachOrTransition(landId[idx]))
                    continue;
                if (slope is not null && slope[idx] > p.MaxSlope) continue;
                var biomeId = (BiomeId)biome[idx];
                if (biomeId is BiomeId.Road or BiomeId.River) continue;   // never a tree on a road or river cell
                if (!Species.TryGetValue(biomeId, out var table)) continue;
                DensityFactor.TryGetValue(biomeId, out var df);
                if (rng.NextDouble() > df * p.DensityMultiplier) continue;
                if (assemblies.ByBiome.TryGetValue(biomeId, out var catalogue) && catalogue.TotalFreq > 0)
                {
                    var group = catalogue.PickGroup(rng);
                    if (group is null) continue;
                    var parts = BiomeStaticScatterPass.BuildGroup(ir, group, x, y, 3);
                    if (parts is not null && parts.Length > 0) ops.Add(new Tree(parts));
                    continue;
                }
                uint regionHash = unchecked((uint)((x >> 5) * 73856093) ^ (uint)((y >> 5) * 19349663) ^ (uint)biomeId);
                ushort treeId = table[regionHash % (uint)table.Length];
                if (p.SpeciesJitter > 0 && rng.NextDouble() < p.SpeciesJitter)
                    treeId = table[rng.Next(table.Length)];
                if (ir.Trees != TreeStatics.Empty && ir.Trees.ById.Count > 0
                    && !ir.Trees.IsWhole(treeId) && !ir.Trees.TrunkToLeaf.ContainsKey(treeId))
                    continue;
                // Co-emit the paired LeafOverlay if this trunk has one. Without this,
                // UO's deciduous trees render as the bare trunk graphic (visually leafless)
                // because the leaves are a SEPARATE static that needs to sit on the same
                // tile. TrunkToLeaf is built from tree-statics.json's "pairWith" rows.
                // Bushes and unpaired trees fall through with no second op.
                ushort leaf = ir.Trees.TrunkToLeaf.TryGetValue(treeId, out var leafId) ? leafId : (ushort)0;
                var pair = new List<StaticOp> { new(StaticOpKind.Add, (ushort)x, (ushort)y, zv, treeId, 0) };
                if (leaf != 0) pair.Add(new(StaticOpKind.Add, (ushort)x, (ushort)y, zv, leaf, 0));
                ops.Add(new Tree(pair.ToArray()));
            }

            chunkResults[chunkIdx] = ops;

            bool TryAdd(int x, int y)
            {
                if (x < scope.X1 || y < sampleY1 || x > scope.X2 || y > chunkY2) return false;
                double cx = (x - scope.X1) / cellSize;
                double cy = (y - sampleY1) / cellSize;
                int gxI = (int)cx, gyI = (int)cy;
                int rngN = 2;
                for (int gy = Math.Max(0, gyI - rngN); gy <= Math.Min(ch - 1, gyI + rngN); gy++)
                for (int gx = Math.Max(0, gxI - rngN); gx <= Math.Min(cw - 1, gxI + rngN); gx++)
                {
                    int existing = grid[gy * cw + gx];
                    if (existing == 0) continue;
                    var (ox, oy) = samples[existing - 1];
                    double ddx = ox - x, ddy = oy - y;
                    if (ddx * ddx + ddy * ddy < (double)r * r) return false;
                }
                samples.Add((x, y));
                int sIdx = samples.Count - 1;
                grid[gyI * cw + gxI] = sIdx + 1;
                active.Add(sIdx);
                return true;
            }
        });

        int added = 0, blocked = 0;
        var occ = OccupancyGrid.For(ir);
        for (int i = 0; i < numChunks; i++)
        {
            if (chunkResults[i] is not { } trees) continue;
            foreach (var t in trees)
            {
                if (t.Parts.Any(op => !occ.IsFree(op.X, op.Y))) { blocked++; continue; }
                foreach (var op in t.Parts)
                {
                    ir.StaticOps.Add(op);
                    added++;
                    occ.MarkSoft(op.X, op.Y);
                    occ.RecordGround(op.X, op.Y, z[ir.Index(op.X, op.Y)]);
                }
                if (t.Parts.Length > 1)
                    occ.AddPlacement(new OccupancyGrid.Placement(Name, $"tree@{t.Parts[0].X},{t.Parts[0].Y}",
                        t.Parts.Select(op => ir.Index(op.X, op.Y)).Distinct().ToArray(), t.Parts, false));
            }
        }

        ctx.Report.StaticsAdded = added;
        if (blocked > 0) ctx.Report.Notes.Add($"{blocked} trees skipped on occupied tiles");
    }

    private readonly record struct Tree(StaticOp[] Parts);
}
