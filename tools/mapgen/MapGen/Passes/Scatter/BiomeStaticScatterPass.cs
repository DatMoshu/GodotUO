using System.Threading.Tasks;
using CentrED.MapGen.Data;
using CentrED.MapGen.IR;
using CentrED.MapGen.Pipeline;
using CentrED.MapGen.Stamps;

namespace CentrED.MapGen.Passes.Scatter;

public sealed class BiomeStaticScatterParams
{
    [TunableDisplay("Ground cover only", Tooltip = "Exclude tree groups; Forest Scatter owns the trees and canopy pairs.")]
    public bool GroundCoverOnly { get; set; } = false;

    [TunableDisplay("Biome chance overrides", Tooltip = "Optional percentages, e.g. Forest=30,Grassland=6. Applies before density multiplier.")]
    public string ChanceOverridesCsv { get; set; } = "";

    [TunableDisplay("Catalogue path", Tooltip = "Norad-style XML root. Default: the user's UO Landscaper statics in the generator data folder (guo-mapgen prepare --landscaper DIR)")]
    public string CataloguePath { get; set; } = "mined/landscaper-statics";

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

    [TunableDisplay("Skip biomes already covered by ForestScatter", Tooltip = "Forest, DenseForest, Jungle (avoid double placement)")]
    public bool SkipForestBiomes { get; set; } = false;

    // Dense biomes bypass Bridson Poisson-disc and iterate every cell with a direct
    // chance roll. Bridson's MinRadius=4 caps samples at ~2% of cells globally — fine
    // for sparse Beach/Forest scatter but wrong for Swamp/Jungle/Wetland which Felucca
    // packs at ~20% density (dense cypress + reeds + lilypads, dense jungle canopy).
    [TunableDisplay("Dense biomes (per-cell)", Tooltip = "Comma-separated biome names that bypass the Poisson sampler and use per-cell chance — for Felucca-style dense Swamp/Jungle ground cover. Default: Swamp,Wetland,Jungle.")]
    public string DenseBiomesCsv { get; set; } = "Swamp,Wetland,Jungle";

    [TunableDisplay("Max multi-tile group Z spread", Tooltip = "A multi-tile group (big rock, tree with canopy) is only placed where the land under all its tiles is within this many Z of the origin, so its pieces line up.")]
    [TunableRange(0, 16)]
    public int MaxGroupZSpread { get; set; } = 3;
}

// F-9: generalised biome static scatter. Covers all 13 biomes from Norad's catalogue.
// Sister pass to ForestScatterPass — runs the same Bridson Poisson-disc sampler but
// picks multi-tile static *groups* with Freq weights. Each placed group emits one
// StaticOp per tile (with x/y/z offsets) so multi-tile clusters compose correctly.
//
// Parallelised across Y-chunks (64 rows). Each chunk runs an independent Bridson
// instance plus its own dense-scatter loop, writing to a thread-local List<StaticOp>.
// BiomeDiagnostics are accumulated per-chunk and merged after the parallel phase.
// All output is concatenated in chunk-index order for determinism per ir.Seed (salted
// with a stable FNV hash of the pass name, identical across processes).
//
// A group is placed whole or not at all: every tile must be in scope, dry, and (for
// multi-tile groups) within MaxGroupZSpread of the origin's land Z. Single-tile groups
// sit on their own tile's land Z. Groups are merged through the shared occupancy grid,
// so they never land on a tile already holding a ForestScatter tree, a stamp or
// another group (this is what stopped jungle tiles getting both a tree and a group).
public sealed class BiomeStaticScatterPass : IGenerationPass
{
    private const int ChunkRows = 64;
    private const ulong GoldenPrime = 0x9E3779B97F4A7C15UL;

    public string Name => "Biome Static Scatter";
    public string Category => "Scatter";

    public IrFields Reads => IrFields.Biome | IrFields.Height | IrFields.Slope | IrFields.LandId;
    public IrFields Writes => IrFields.StaticOps;

    public object CreateDefaultParams() => new BiomeStaticScatterParams();

    public void Run(GenContext ctx, object parameters)
    {
        var p = (BiomeStaticScatterParams)parameters;
        var ir = ctx.IR;
        if (ir.Biome is null || ir.Height_Z is null) return;

        var resolvedPath = RepoRootResolver.Resolve(p.CataloguePath);
        var table = BiomeStaticsTable.LoadFromNorad(resolvedPath, out var unknown);
        if (table.ByBiome.Count == 0)
        {
            ctx.Report.Warnings.Add($"BiomeStaticScatter: no catalogues loaded from {resolvedPath}");
            return;
        }
        ctx.Report.Notes.Add($"loaded {table.ByBiome.Count} biome catalogues from {resolvedPath}");
        if (unknown.Count > 0)
            ctx.Report.Notes.Add($"unmapped catalogue files skipped: {string.Join(", ", unknown)}");

        if (p.GroundCoverOnly)
            foreach (var cat in table.ByBiome.Values)
            {
                cat.Groups.RemoveAll(g => g.Description.Contains("tree", StringComparison.OrdinalIgnoreCase)
                    || g.Tiles.Any(t => ir.Trees.ById.TryGetValue(t.TileID, out var e)
                        && e.Kind is TreeStatics.Kind.WholeTree or TreeStatics.Kind.TrunkOnly or TreeStatics.Kind.LeafOverlay));
                cat.TotalFreq = cat.Groups.Sum(g => g.Freq);
            }
        foreach (string entry in p.ChanceOverridesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = entry.Split('=', StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || !Enum.TryParse<BiomeId>(parts[0], true, out var bio)
                || !int.TryParse(parts[1], out int chance) || chance < 0 || chance > 100)
                throw new ArgumentException($"Invalid biome chance override: {entry}");
            if (table.ByBiome.TryGetValue(bio, out var cat)) cat.ChancePercent = chance;
        }

        var biome = ir.Biome;
        var z = ir.Height_Z;
        var slope = ir.Slope;
        var scope = ir.Scope;

        var denseSet = new HashSet<BiomeId>();
        if (!string.IsNullOrWhiteSpace(p.DenseBiomesCsv))
        {
            foreach (var name in p.DenseBiomesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries))
                if (Enum.TryParse<BiomeId>(name.Trim(), true, out var bId))
                    denseSet.Add(bId);
        }

        int r = Math.Max(2, p.MinRadius);
        int seedStride = r * 4;
        ulong passHash = StableHash.Fnv1a64(Name);

        int numChunks = (int)Math.Ceiling((double)scope.Height / ChunkRows);
        var chunkOps = new List<StaticOp[]>[numChunks];
        var chunkDiag = new BiomeDiagnostics[numChunks];
        var chunkCellCounts = new BiomeDiagnostics[numChunks];

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount,
            CancellationToken = ctx.Cancellation,
        };

        // Sparse Bridson + dense per-cell scatter per chunk.
        Parallel.For(0, numChunks, options, chunkIdx =>
        {
            int chunkY1 = scope.Y1 + chunkIdx * ChunkRows;
            int chunkY2 = Math.Min(scope.Y2, chunkY1 + ChunkRows - 1);
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

            var ops = new List<StaticOp[]>();
            var diag = new BiomeDiagnostics();

            // Sparse placement from Bridson samples.
            foreach (var (sx, sy) in samples)
            {
                int x = (int)Math.Round(sx), y = (int)Math.Round(sy);
                if (x < scope.X1 || x > scope.X2 || y < chunkY1 || y > chunkY2) continue;
                int idx = ir.Index(x, y);
                sbyte zv = z[idx];
                var biomeId = (BiomeId)biome[idx];
                if (denseSet.Contains(biomeId)) continue;
                diag.GetOrAdd(biomeId).Evaluated++;

                if (zv < ir.SeaLevelZ + p.SeaLevelZ) { diag.GetOrAdd(biomeId).RejectedSeaLevel++; continue; }
                if (ir.LandId is not null && TileFlags.IsWaterLandId(ir.LandId[idx]))
                    { diag.GetOrAdd(biomeId).RejectedWater++; continue; }
                if (slope is not null && slope[idx] > p.MaxSlope)
                    { diag.GetOrAdd(biomeId).RejectedSlope++; continue; }
                if (p.SkipForestBiomes && biomeId is BiomeId.Forest or BiomeId.DenseForest or BiomeId.Jungle)
                    { diag.GetOrAdd(biomeId).RejectedSkipForest++; continue; }
                if (!table.ByBiome.TryGetValue(biomeId, out var cat))
                    { diag.GetOrAdd(biomeId).RejectedNoCatalogue++; continue; }

                double effChance = (cat.ChancePercent / 100.0) * p.DensityMultiplier;
                var biomeStats = diag.GetOrAdd(biomeId);
                biomeStats.EffectiveChance = effChance;
                if (rng.NextDouble() > effChance) { biomeStats.RejectedChanceGate++; continue; }

                var group = cat.PickGroup(rng);
                if (group is null) { biomeStats.RejectedEmptyGroup++; continue; }

                var placedOps = BuildGroup(ir, group, x, y, p.MaxGroupZSpread);
                if (placedOps is null) { biomeStats.RejectedSlope++; continue; }
                ops.Add(placedOps);
                biomeStats.GroupsPlaced++;
                biomeStats.TilesPlaced += placedOps.Length;
            }

            // Dense per-cell scatter for this chunk's committed Y range.
            if (denseSet.Count > 0)
            {
                for (int y = chunkY1; y <= chunkY2; y++)
                for (int x = scope.X1; x <= scope.X2; x++)
                {
                    int idx = ir.Index(x, y);
                    var biomeId = (BiomeId)biome[idx];
                    if (!denseSet.Contains(biomeId)) continue;
                    var biomeStats = diag.GetOrAdd(biomeId);
                    biomeStats.Evaluated++;

                    sbyte zv = z[idx];
                    if (zv < ir.SeaLevelZ + p.SeaLevelZ) { biomeStats.RejectedSeaLevel++; continue; }
                    if (ir.LandId is not null && TileFlags.IsWaterLandId(ir.LandId[idx]))
                        { biomeStats.RejectedWater++; continue; }
                    if (slope is not null && slope[idx] > p.MaxSlope)
                        { biomeStats.RejectedSlope++; continue; }
                    if (p.SkipForestBiomes && biomeId is BiomeId.Forest or BiomeId.DenseForest or BiomeId.Jungle)
                        { biomeStats.RejectedSkipForest++; continue; }
                    if (!table.ByBiome.TryGetValue(biomeId, out var cat))
                        { biomeStats.RejectedNoCatalogue++; continue; }

                    double effChance = (cat.ChancePercent / 100.0) * p.DensityMultiplier;
                    biomeStats.EffectiveChance = effChance;
                    if (rng.NextDouble() > effChance) { biomeStats.RejectedChanceGate++; continue; }

                    var group = cat.PickGroup(rng);
                    if (group is null) { biomeStats.RejectedEmptyGroup++; continue; }

                    var placedOps = BuildGroup(ir, group, x, y, p.MaxGroupZSpread);
                    if (placedOps is null) { biomeStats.RejectedSlope++; continue; }
                    ops.Add(placedOps);
                    biomeStats.GroupsPlaced++;
                    biomeStats.TilesPlaced += placedOps.Length;
                }
            }

            chunkOps[chunkIdx] = ops;
            chunkDiag[chunkIdx] = diag;

            bool TryAdd(int x, int y)
            {
                if (x < scope.X1 || y < sampleY1 || x > scope.X2 || y > chunkY2) return false;
                double cx = (x - scope.X1) / cellSize;
                double cy = (y - sampleY1) / cellSize;
                int gxI = (int)cx, gyI = (int)cy;
                int rngN = 2;
                for (int gyy = Math.Max(0, gyI - rngN); gyy <= Math.Min(ch - 1, gyI + rngN); gyy++)
                for (int gxx = Math.Max(0, gxI - rngN); gxx <= Math.Min(cw - 1, gxI + rngN); gxx++)
                {
                    int existing = grid[gyy * cw + gxx];
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

        // Cell-count diagnostic loop (parallelised, merged in order).
        Parallel.For(0, numChunks, options, chunkIdx =>
        {
            int chunkY1 = scope.Y1 + chunkIdx * ChunkRows;
            int chunkY2 = Math.Min(scope.Y2, chunkY1 + ChunkRows - 1);
            var cellDiag = new BiomeDiagnostics();
            for (int y = chunkY1; y <= chunkY2; y++)
            for (int x = scope.X1; x <= scope.X2; x++)
            {
                var bId = (BiomeId)biome[ir.Index(x, y)];
                cellDiag.GetOrAdd(bId).CellsInScope++;
            }
            chunkCellCounts[chunkIdx] = cellDiag;
        });

        // Merge in chunk-index order for determinism.
        var mergedDiag = new BiomeDiagnostics();
        int totalTiles = 0, blocked = 0;
        var occ = OccupancyGrid.For(ir);
        for (int i = 0; i < numChunks; i++)
        {
            if (chunkOps[i] is { } groups)
            {
                foreach (var g in groups)
                {
                    bool free = true;
                    foreach (var op in g)
                        if (!occ.IsFree(op.X, op.Y)) { free = false; break; }
                    if (!free) { blocked++; continue; }
                    ir.StaticOps.AddRange(g);
                    totalTiles += g.Length;
                    bool multiTile = false;
                    foreach (var op in g)
                    {
                        occ.MarkSoft(op.X, op.Y);
                        occ.RecordGround(op.X, op.Y, z[ir.Index(op.X, op.Y)]);
                        if (op.X != g[0].X || op.Y != g[0].Y) multiTile = true;
                    }
                    if (multiTile)
                        occ.AddPlacement(new OccupancyGrid.Placement(Name, $"group@{g[0].X},{g[0].Y}",
                            g.Select(op => ir.Index(op.X, op.Y)).Distinct().ToArray(), g, IsStamp: false));
                }
            }
            if (chunkDiag[i] is { } d)
                MergeDiag(mergedDiag, d);
            if (chunkCellCounts[i] is { } cc)
                MergeDiag(mergedDiag, cc);
        }

        ctx.Report.Diagnostics = mergedDiag;
        ctx.Report.StaticsAdded = totalTiles;

        // Reconstruct sparse/dense split for the note (each biome is exclusively in one path).
        int sparseGroups = 0, sparseTiles = 0, denseGroups = 0, denseTiles = 0;
        foreach (var (bId, stats) in mergedDiag.ByBiome)
        {
            if (denseSet.Contains(bId)) { denseGroups += stats.GroupsPlaced; denseTiles += stats.TilesPlaced; }
            else                        { sparseGroups += stats.GroupsPlaced; sparseTiles += stats.TilesPlaced; }
        }
        ctx.Report.Notes.Add(
            $"sparse: groups={sparseGroups}, tiles={sparseTiles}; dense: groups={denseGroups}, tiles={denseTiles}; biomes covered={table.ByBiome.Count}");
        if (blocked > 0) ctx.Report.Notes.Add($"{blocked} groups skipped on occupied tiles");
    }

    // Emits one group anchored at (x, y), or null when it cannot be placed whole: a tile
    // outside scope or on water, or a multi-tile group whose tiles' land differs from the
    // origin's by more than maxSpread. Single-tile groups use their own tile's land Z;
    // multi-tile groups share the origin's land Z so their pieces line up.
    internal static StaticOp[]? BuildGroup(GenIR ir, BiomeStaticsTable.StaticGroup group, int x, int y, int maxSpread)
    {
        var scope = ir.Scope;
        var z = ir.Height_Z!;
        int originZ = z[ir.Index(x, y)];
        bool multiTile = false;
        foreach (var st in group.Tiles)
            if (st.Xoff != 0 || st.Yoff != 0) { multiTile = true; break; }

        var ops = new StaticOp[group.Tiles.Count];
        for (int i = 0; i < group.Tiles.Count; i++)
        {
            var st = group.Tiles[i];
            int gx = x + st.Xoff;
            int gy = y + st.Yoff;
            if (gx < scope.X1 || gy < scope.Y1 || gx > scope.X2 || gy > scope.Y2) return null;
            int idx = ir.Index(gx, gy);
            if (ir.LandId is not null && TileFlags.IsWaterLandId(ir.LandId[idx])) return null;
            int land = z[idx];
            if (multiTile && Math.Abs(land - originZ) > maxSpread) return null;
            int baseZ = multiTile ? originZ : land;
            sbyte gz = (sbyte)Math.Clamp(baseZ + st.Zoff, sbyte.MinValue, sbyte.MaxValue);
            ops[i] = new StaticOp(StaticOpKind.Add, (ushort)gx, (ushort)gy, gz, st.TileID, st.Hue);
        }
        return ops;
    }

    private static void MergeDiag(BiomeDiagnostics target, BiomeDiagnostics source)
    {
        foreach (var (bId, src) in source.ByBiome)
        {
            var dst = target.GetOrAdd(bId);
            dst.CellsInScope       += src.CellsInScope;
            dst.Evaluated          += src.Evaluated;
            dst.GroupsPlaced       += src.GroupsPlaced;
            dst.TilesPlaced        += src.TilesPlaced;
            dst.RejectedSeaLevel   += src.RejectedSeaLevel;
            dst.RejectedWater      += src.RejectedWater;
            dst.RejectedSlope      += src.RejectedSlope;
            dst.RejectedSkipForest += src.RejectedSkipForest;
            dst.RejectedNoCatalogue+= src.RejectedNoCatalogue;
            dst.RejectedChanceGate += src.RejectedChanceGate;
            dst.RejectedEmptyGroup += src.RejectedEmptyGroup;
            if (src.EffectiveChance > 0) dst.EffectiveChance = src.EffectiveChance;
        }
    }
}
