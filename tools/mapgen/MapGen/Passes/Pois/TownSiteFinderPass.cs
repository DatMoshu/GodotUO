using System.Text.Json;
using CentrED.MapGen.IR;
using CentrED.MapGen.Pipeline;
using CentrED.Network;

namespace CentrED.MapGen.Passes.Pois;

public sealed class TownSiteFinderParams
{
    [TunableDisplay("Quadrant boundaries JSON path",
        Tooltip = "Optional; falls back to AutoGridX×AutoGridY if missing.")]
    public string QuadrantBoundariesPath { get; set; } = "";

    [TunableDisplay("Auto-grid quadrants X")] [TunableRange(1, 16)]
    public int AutoGridX { get; set; } = 4;

    [TunableDisplay("Auto-grid quadrants Y")] [TunableRange(1, 16)]
    public int AutoGridY { get; set; } = 6;

    [TunableDisplay("Towns per quadrant",
        Tooltip = "0 disables. 1=sparse, 2=medium (Britain-Trinsic density), 4=dense.")]
    [TunableRange(0, 8)]
    public int TownsPerQuadrant { get; set; } = 2;

    [TunableDisplay("Town footprint size (tiles)",
        Tooltip = "Square side length. 24 ≈ small village footprint; 48 ≈ medium town.")]
    [TunableRange(8, 96)]
    public int TownSize { get; set; } = 24;

    [TunableDisplay("Max attempts per quadrant", Tooltip = "Random site samples before giving up.")]
    [TunableRange(16, 4096)]
    public int MaxAttempts { get; set; } = 512;

    [TunableDisplay("Sea level offset", Tooltip = "Added to GenIR.SeaLevelZ (the rebased frame: sea level is 0).")] [TunableRange(-128, 127)]
    public int SeaLevelZ { get; set; } = 0;

    [TunableDisplay("Land floor (Z > SeaLevelZ + this)",
        Tooltip = "How far above sea level a cell must be to count as solid land.")]
    [TunableRange(0, 16)]
    public int MinAltitudeAboveSea { get; set; } = 2;

    [TunableDisplay("Max relief in footprint (Z delta)",
        Tooltip = "Reject sites where max(Z)-min(Z) exceeds this. Keeps towns flat enough to build on.")]
    [TunableRange(1, 32)]
    public int MaxRelief { get; set; } = 6;

    [TunableDisplay("Min spacing from existing POIs (tiles)",
        Tooltip = "Reject candidate sites whose centre is within this distance of any existing POI (towns or others).")]
    [TunableRange(0, 1024)]
    public int MinSpacing { get; set; } = 192;

    [TunableDisplay("Allowed-biome fraction floor",
        Tooltip = "Site rejected if fewer than this fraction of footprint cells are in the buildable-biome set.")]
    [TunableRange(0.0, 1.0)]
    public double MinBuildableFraction { get; set; } = 0.92;

    [TunableDisplay("Fixed sites",
        Tooltip = "Explicit town/district footprints placed first, before any random search: 'x,y,w,h[,z];...' in map tiles. z omitted = the footprint's median ground Z. Not checked for relief or biome; skipped with a warning if they leave the scope or touch water.")]
    public string Sites { get; set; } = "";

    [TunableDisplay("Buildable biomes",
        Tooltip = "Comma-separated BiomeId names a random site may stand on. Empty = Grassland, Forest, DenseForest, Savanna, Beach. Add Desert (or Snow, Tundra) for other worlds.")]
    public string BuildableBiomesCsv { get; set; } = "";
}

// Find dry inland flats large enough for a small village/town. Stratifies the world
// across a quadrant grid (same scheme as PoiStampPass), attempts random square
// footprints inside each quadrant, accepts the first one that passes flatness +
// dryness + biome composition + spacing checks. Emits a PoiStamp { Kind=Town,
// Footprint=accepted rect, Gates=midpoints of each side } for every accepted site.
//
// This pass is INDEPENDENT of PoiStampPass — it doesn't read existing POIs as a
// dependency (it just respects their spacing if any happen to be present). The
// downstream TownRoadPass + RoadGraphPass consume the Town entries directly from
// ir.Pois.
public sealed class TownSiteFinderPass : IGenerationPass
{
    public string Name => "Town Sites";
    public string Category => "Pois";

    // Pois is a sparse layer (starts empty), so we don't *need* to declare it as Reads.
    // We DO read it imperatively to enforce MinSpacing, but that's a soft check that
    // tolerates an empty list — same pattern as PoiStampPass.
    public IrFields Reads => IrFields.Height | IrFields.Biome;
    public IrFields Writes => IrFields.Pois;

    public object CreateDefaultParams() => new TownSiteFinderParams();

    // Biomes a town is allowed to sit on. Beach is included so coastal villages work;
    // Mountain/Swamp/Water/Snow are excluded — they'd produce visually nonsense towns.
    private static readonly HashSet<BiomeId> DefaultBuildableBiomes = new()
    {
        BiomeId.Grassland,
        BiomeId.Forest,
        BiomeId.DenseForest,
        BiomeId.Savanna,
        BiomeId.Beach, // a small fringe is fine; the fraction floor catches "town on a sandbar"
    };

    public void Run(GenContext ctx, object parameters)
    {
        var p = (TownSiteFinderParams)parameters;
        var ir = ctx.IR;
        if (ir.Height_Z is null || ir.Biome is null) return;
        var buildableBiomes = ParseBiomes(p.BuildableBiomesCsv, ctx);
        int fixedPlaced = PlaceFixedSites(ctx, p);
        if (p.TownsPerQuadrant <= 0)
        {
            ctx.Report.TilesTouched = fixedPlaced;
            ctx.Report.Notes.Add(fixedPlaced > 0 ? $"fixed sites={fixedPlaced}; TownsPerQuadrant=0, no random towns" : "TownsPerQuadrant=0; skipped");
            return;
        }

        var quadrants = LoadQuadrants(p, ir);
        ctx.Report.Notes.Add($"quadrants={quadrants.Count}");
        if (quadrants.Count == 0) { ctx.Report.Warnings.Add("no quadrants intersect scope"); return; }

        int nextId = ir.Pois.Count == 0 ? 1 : ir.Pois.Max(x => x.Id) + 1;
        int placed = 0;
        int considered = 0;
        int half = p.TownSize / 2;

        foreach (var q in quadrants)
        {
            ctx.Cancellation.ThrowIfCancellationRequested();
            int placedInQuadrant = 0;
            int attempts = 0;
            while (placedInQuadrant < p.TownsPerQuadrant && attempts++ < p.MaxAttempts)
            {
                considered++;
                // Pick a random centre such that the full footprint fits inside the quadrant + scope.
                int xMin = Math.Max(q.Rect.X1 + half, ir.Scope.X1 + half);
                int xMax = Math.Min(q.Rect.X2 - half, ir.Scope.X2 - half);
                int yMin = Math.Max(q.Rect.Y1 + half, ir.Scope.Y1 + half);
                int yMax = Math.Min(q.Rect.Y2 - half, ir.Scope.Y2 - half);
                if (xMin > xMax || yMin > yMax) break; // quadrant too small for this town size

                int cx = ctx.Rng.Next(xMin, xMax + 1);
                int cy = ctx.Rng.Next(yMin, yMax + 1);

                // Spacing check against everything already in ir.Pois (existing POIs +
                // towns placed earlier in this same pass).
                if (!RespectsSpacing(ir.Pois, cx, cy, p.MinSpacing)) continue;

                var rect = new RectU16(
                    (ushort)(cx - half),
                    (ushort)(cy - half),
                    (ushort)(cx + half - 1),
                    (ushort)(cy + half - 1));

                if (!IsBuildable(ir, rect, p, buildableBiomes)) continue;

                // Accept. Build gates at the midpoint of each side; the gate Z is the
                // ground Z of that cell (used later by RoadGraph for cost calcs).
                var gates = ComputeGates(rect);
                sbyte centreZ = ir.Height_Z[ir.Index(cx, cy)];
                ir.Pois.Add(new PoiStamp(
                    Id: nextId++,
                    Kind: PoiKind.Town,
                    X: (ushort)cx,
                    Y: (ushort)cy,
                    Z: centreZ,
                    QuadrantId: q.Id,
                    Tag: null,
                    Footprint: rect,
                    Gates: gates));
                placed++;
                placedInQuadrant++;
            }
        }

        ctx.Report.TilesTouched = placed + fixedPlaced; // Towns placed, not tiles
        ctx.Report.Notes.Add($"towns={placed} (considered={considered}), fixed sites={fixedPlaced}");
        if (placed + fixedPlaced == 0)
            ctx.Report.Warnings.Add("no town sites accepted; relax MaxRelief, MinBuildableFraction, or TownSize");
    }

    private static bool RespectsSpacing(IReadOnlyList<PoiStamp> existing, int cx, int cy, int minSpacing)
    {
        if (minSpacing <= 0) return true;
        long sqLimit = (long)minSpacing * minSpacing;
        foreach (var poi in existing)
        {
            long dx = poi.X - cx;
            long dy = poi.Y - cy;
            if (dx * dx + dy * dy < sqLimit) return false;
        }
        return true;
    }

    private static HashSet<BiomeId> ParseBiomes(string csv, GenContext ctx)
    {
        if (string.IsNullOrWhiteSpace(csv)) return DefaultBuildableBiomes;
        var set = new HashSet<BiomeId>();
        foreach (var raw in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Enum.TryParse<BiomeId>(raw, ignoreCase: true, out var b) && Enum.IsDefined(b)) set.Add(b);
            else ctx.Report.Warnings.Add($"BuildableBiomesCsv: unknown biome '{raw}' (ignored)");
        }
        if (set.Count > 0) return set;
        ctx.Report.Warnings.Add("BuildableBiomesCsv named no known biome; using the default set");
        return DefaultBuildableBiomes;
    }

    // Fixed sites ("x,y,w,h[,z];..."): placed as given, before the random search, so the
    // random towns keep their spacing from them. They use no RNG, so a preset without
    // Sites generates exactly what it did before.
    private static int PlaceFixedSites(GenContext ctx, TownSiteFinderParams p)
    {
        if (string.IsNullOrWhiteSpace(p.Sites)) return 0;
        var ir = ctx.IR;
        int nextId = ir.Pois.Count == 0 ? 1 : ir.Pois.Max(x => x.Id) + 1;
        int placed = 0;
        foreach (var entry in p.Sites.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = entry.Split(',', StringSplitOptions.TrimEntries);
            var nums = new int[parts.Length];
            bool ok = parts.Length is 4 or 5;
            for (int i = 0; ok && i < parts.Length; i++) ok = int.TryParse(parts[i], out nums[i]);
            if (!ok || nums[2] < 1 || nums[3] < 1)
            {
                ctx.Report.Warnings.Add($"Sites: '{entry}' is not x,y,w,h[,z] with w,h >= 1 (skipped)");
                continue;
            }
            int x1 = nums[0], y1 = nums[1], x2 = x1 + nums[2] - 1, y2 = y1 + nums[3] - 1;
            if (x1 < ir.Scope.X1 || y1 < ir.Scope.Y1 || x2 > ir.Scope.X2 || y2 > ir.Scope.Y2)
            {
                ctx.Report.Warnings.Add($"Sites: '{entry}' leaves the map scope (skipped)");
                continue;
            }
            var zs = new List<sbyte>(nums[2] * nums[3]);
            bool water = false;
            for (int y = y1; y <= y2 && !water; y++)
            for (int x = x1; x <= x2; x++)
            {
                int idx = ir.Index(x, y);
                if ((BiomeId)ir.Biome![idx] is BiomeId.DeepWater or BiomeId.ShallowWater or BiomeId.River) { water = true; break; }
                zs.Add(ir.Height_Z![idx]);
            }
            if (water)
            {
                ctx.Report.Warnings.Add($"Sites: '{entry}' touches water (skipped)");
                continue;
            }
            zs.Sort();
            sbyte z = parts.Length == 5 ? (sbyte)Math.Clamp(nums[4], sbyte.MinValue, sbyte.MaxValue) : zs[zs.Count / 2];
            var rect = new RectU16((ushort)x1, (ushort)y1, (ushort)x2, (ushort)y2);
            ir.Pois.Add(new PoiStamp(
                Id: nextId++,
                Kind: PoiKind.Town,
                X: (ushort)((x1 + x2) / 2),
                Y: (ushort)((y1 + y2) / 2),
                Z: z,
                QuadrantId: null,
                Tag: "fixed-site",
                Footprint: rect,
                Gates: ComputeGates(rect)));
            ctx.Report.Notes.Add($"fixed site {x1},{y1} {nums[2]}x{nums[3]} z={z} (ground {zs[0]}..{zs[^1]})");
            placed++;
        }
        return placed;
    }

    private static bool IsBuildable(GenIR ir, RectU16 rect, TownSiteFinderParams p, HashSet<BiomeId> buildableBiomes)
    {
        var z = ir.Height_Z!;
        var b = ir.Biome!;
        sbyte minZ = sbyte.MaxValue;
        sbyte maxZ = sbyte.MinValue;
        int total = 0;
        int buildable = 0;
        int landFloor = ir.SeaLevelZ + p.SeaLevelZ + p.MinAltitudeAboveSea;

        for (int y = rect.Y1; y <= rect.Y2; y++)
        for (int x = rect.X1; x <= rect.X2; x++)
        {
            int idx = ir.Index(x, y);
            sbyte zv = z[idx];
            // Hard reject: any cell at or below the land floor (water / beach edge).
            if (zv < landFloor) return false;
            if (zv < minZ) minZ = zv;
            if (zv > maxZ) maxZ = zv;
            if (maxZ - minZ > p.MaxRelief) return false;
            total++;
            if (buildableBiomes.Contains((BiomeId)b[idx])) buildable++;
        }

        return (double)buildable / total >= p.MinBuildableFraction;
    }

    // Gate placement: midpoint of each edge of the footprint. Gates are the *boundary*
    // cells RoadGraph A* will terminate at — they sit one cell inside the edge so
    // RoadCenterline's flatten radius doesn't reach outside the town's nominal extent.
    private static IReadOnlyList<(ushort X, ushort Y)> ComputeGates(RectU16 rect)
    {
        ushort midX = (ushort)((rect.X1 + rect.X2) / 2);
        ushort midY = (ushort)((rect.Y1 + rect.Y2) / 2);
        return new (ushort X, ushort Y)[]
        {
            (midX, rect.Y1),         // North gate
            (midX, rect.Y2),         // South gate
            (rect.X1, midY),         // West gate
            (rect.X2, midY),         // East gate
        };
    }

    private sealed record Quadrant(string Id, RectU16 Rect);

    // Same quadrant loader as PoiStampPass — duplicated rather than refactored out
    // because the two passes have slightly different param classes and merging them
    // would couple two unrelated systems. If a third pass needs this, extract.
    private static List<Quadrant> LoadQuadrants(TownSiteFinderParams p, GenIR ir)
    {
        var list = new List<Quadrant>();
        if (!string.IsNullOrWhiteSpace(p.QuadrantBoundariesPath) && File.Exists(p.QuadrantBoundariesPath))
        {
            try
            {
                using var fs = File.OpenRead(p.QuadrantBoundariesPath);
                using var doc = JsonDocument.Parse(fs);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    int n = 0;
                    foreach (var el in doc.RootElement.EnumerateArray())
                    {
                        // Deterministic fallback id (was Guid.NewGuid, which changed every run).
                        string id = el.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : $"Q{n}";
                        n++;
                        ushort x1 = (ushort)el.GetProperty("x1").GetInt32();
                        ushort y1 = (ushort)el.GetProperty("y1").GetInt32();
                        ushort x2 = (ushort)el.GetProperty("x2").GetInt32();
                        ushort y2 = (ushort)el.GetProperty("y2").GetInt32();
                        list.Add(new Quadrant(id, new RectU16(x1, y1, x2, y2)));
                    }
                    if (list.Count > 0) return list;
                }
            }
            catch { /* fall through to auto-grid */ }
        }

        int gx = Math.Max(1, p.AutoGridX);
        int gy = Math.Max(1, p.AutoGridY);
        int sw = ir.Scope.Width / gx;
        int sh = ir.Scope.Height / gy;
        for (int j = 0; j < gy; j++)
        for (int i = 0; i < gx; i++)
        {
            ushort x1 = (ushort)(ir.Scope.X1 + i * sw);
            ushort y1 = (ushort)(ir.Scope.Y1 + j * sh);
            ushort x2 = (ushort)Math.Min(ir.Scope.X2, x1 + sw - 1);
            ushort y2 = (ushort)Math.Min(ir.Scope.Y2, y1 + sh - 1);
            list.Add(new Quadrant($"Q{i}-{j}", new RectU16(x1, y1, x2, y2)));
        }
        return list;
    }
}
