using CentrED.MapGen.IR;
using CentrED.MapGen.Passes.Network;
using CentrED.MapGen.Passes.Pois;
using CentrED.MapGen.Pipeline;
using CentrED.Network;

namespace CentrED.MapGen.Tests;

// Targeted tests for the Town Sites + Town Roads + RoadGraph gate-termination work.
// Each test wires a minimal synthetic IR (flat land, uniform biome) and exercises one
// behaviour. We don't go through DefaultPipeline because that would pull in passes
// that need real data files (LandBrush, stamps, trees) — these tests stay pure-IR.
public class TownAndRoadTests
{
    private const ushort Size = 256;

    [Fact]
    public void TownSiteFinder_RespectsMinSpacing_BetweenAcceptedSites()
    {
        var ir = NewFlatIR(seed: 1);
        var pass = new TownSiteFinderPass();
        var pars = new TownSiteFinderParams
        {
            AutoGridX = 2,
            AutoGridY = 2,
            TownsPerQuadrant = 4,   // request more than will fit
            TownSize = 16,
            MaxAttempts = 1024,
            MinSpacing = 96,        // bigger than quadrant span / 2 — forces rejection
            MaxRelief = 99,         // accept any flatness on the flat IR
            MinBuildableFraction = 0.0,
            SeaLevelZ = -100,
        };
        var ctx = NewCtx(ir);
        pass.Run(ctx, pars);

        var towns = ir.Pois.Where(p => p.Kind == PoiKind.Town).ToList();
        Assert.NotEmpty(towns);
        // Every pair must be at least MinSpacing apart.
        for (int i = 0; i < towns.Count; i++)
        for (int j = i + 1; j < towns.Count; j++)
        {
            int dx = towns[i].X - towns[j].X;
            int dy = towns[i].Y - towns[j].Y;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            Assert.True(dist >= pars.MinSpacing,
                $"towns {towns[i].Id} and {towns[j].Id} are {dist:F1} apart, < MinSpacing={pars.MinSpacing}");
        }
    }

    [Fact]
    public void TownSiteFinder_PopulatesFootprintAndFourGates()
    {
        var ir = NewFlatIR(seed: 7);
        var pass = new TownSiteFinderPass();
        var pars = new TownSiteFinderParams
        {
            AutoGridX = 1, AutoGridY = 1, TownsPerQuadrant = 1,
            TownSize = 24, MaxAttempts = 64,
            MinSpacing = 0, MaxRelief = 99, MinBuildableFraction = 0.0, SeaLevelZ = -100,
        };
        pass.Run(NewCtx(ir), pars);

        var t = Assert.Single(ir.Pois, p => p.Kind == PoiKind.Town);
        Assert.NotNull(t.Footprint);
        Assert.NotNull(t.Gates);
        Assert.Equal(4, t.Gates!.Count);
        // Gates are N, S, W, E in that order — each must sit on the matching edge.
        var rect = t.Footprint!.Value;
        Assert.Equal(rect.Y1, t.Gates[0].Y);
        Assert.Equal(rect.Y2, t.Gates[1].Y);
        Assert.Equal(rect.X1, t.Gates[2].X);
        Assert.Equal(rect.X2, t.Gates[3].X);
    }

    [Fact]
    public void RoadGraph_EndsAtTownGate_WhenDestinationHasFootprint()
    {
        var ir = NewFlatIR(seed: 1);

        // Two towns ~150 cells apart. Each has a 32-cell-side footprint with gates on
        // the four edges. Place them on the same horizontal line so the obvious A* path
        // is east-west and we can predict which gates should be chosen.
        var townA = MakeTown(id: 100, cx: 60, cy: 128, half: 16);
        var townB = MakeTown(id: 200, cx: 200, cy: 128, half: 16);
        ir.Pois.Add(townA);
        ir.Pois.Add(townB);

        var pass = new RoadGraphPass();
        var pars = new RoadGraphParams
        {
            SeaLevelZ = -100,
            MaxEdgesPerPoi = 1,
            SlopePenalty = 0,    // flat IR — irrelevant
            WaterPenalty = 0,
            RoadEndsAtTownGate = true,
        };
        pass.Run(NewCtx(ir), pars);

        var segs = ir.Roads;
        Assert.NotEmpty(segs);
        var seg = segs[0];
        Assert.NotEmpty(seg.Path);

        // The first cell of the path is on townA's gate set; last cell on townB's.
        var first = seg.Path[0];
        var last = seg.Path[^1];
        Assert.Contains(first, townA.Gates!);
        Assert.Contains(last, townB.Gates!);

        // No cell along the path should lie strictly INSIDE either footprint (the
        // endpoints are gate cells which sit on the boundary, so they're OK; "strict
        // interior" means at least one cell away from every edge).
        AssertNoStrictInterior(seg.Path, townA.Footprint!.Value);
        AssertNoStrictInterior(seg.Path, townB.Footprint!.Value);
    }

    [Fact]
    public void RoadGraph_AvoidsForeignFootprint_WhenPenaltyIsHigh()
    {
        var ir = NewFlatIR(seed: 1);

        // Two endpoints with NO footprint, and a third town's footprint sitting on the
        // straight line between them. With ForeignFootprintPenalty=2048 the A* must
        // route around it; we assert no path cell lies in the foreign footprint.
        var poiA = new PoiStamp(Id: 1, Kind: PoiKind.ControlPoint, X: 30,  Y: 128, Z: 1);
        var poiB = new PoiStamp(Id: 2, Kind: PoiKind.ControlPoint, X: 220, Y: 128, Z: 1);
        var foreign = MakeTown(id: 3, cx: 128, cy: 128, half: 24);
        ir.Pois.Add(poiA);
        ir.Pois.Add(poiB);
        ir.Pois.Add(foreign);

        var pass = new RoadGraphPass();
        pass.Run(NewCtx(ir), new RoadGraphParams
        {
            SeaLevelZ = -100,
            // MaxEdgesPerPoi=2: with 3 collinear POIs every pair gets an edge so we can
            // inspect the A-B edge specifically. MaxEdgesPerPoi=1 would have A and B
            // each link to the foreign town (its their nearest neighbour) — the wrong
            // network shape for this assertion.
            MaxEdgesPerPoi = 2,
            SlopePenalty = 0,
            WaterPenalty = 0,
            RoadEndsAtTownGate = true,
            ForeignFootprintPenalty = 2048,
        });

        // Find the A-B segment specifically.
        var seg = ir.Roads.FirstOrDefault(r =>
            (r.FromPoiId == 1 && r.ToPoiId == 2) || (r.FromPoiId == 2 && r.ToPoiId == 1));
        Assert.NotNull(seg);

        var fp = foreign.Footprint!.Value;
        foreach (var (x, y) in seg!.Path)
        {
            bool inside = x >= fp.X1 && x <= fp.X2 && y >= fp.Y1 && y <= fp.Y2;
            Assert.False(inside,
                $"path cell ({x},{y}) lies inside foreign footprint {fp.X1},{fp.Y1}-{fp.X2},{fp.Y2}");
        }
    }

    // -----------------------------------------------------------------------
    // helpers
    // -----------------------------------------------------------------------
    private static GenIR NewFlatIR(long seed)
    {
        var ir = new GenIR(Size, Size, new RectU16(0, 0, Size - 1, Size - 1), unchecked((ulong)seed));
        ir.EnsureHeight();
        ir.EnsureBiome();
        ir.EnsureLandId();
        for (int i = 0; i < ir.Width * ir.Height; i++)
        {
            ir.Height_Z![i] = 1;                   // 1 above sea, all flat
            ir.Biome![i] = (byte)BiomeId.Grassland;
            ir.LandId![i] = 0x03;                  // arbitrary land tile
        }
        return ir;
    }

    private static GenContext NewCtx(GenIR ir) => new()
    {
        IR = ir,
        Rng = new Random(0),
        Report = new PassReport { PassName = "test" },
        Cancellation = default,
    };

    private static PoiStamp MakeTown(int id, int cx, int cy, int half)
    {
        var rect = new RectU16(
            (ushort)(cx - half), (ushort)(cy - half),
            (ushort)(cx + half - 1), (ushort)(cy + half - 1));
        ushort midX = (ushort)((rect.X1 + rect.X2) / 2);
        ushort midY = (ushort)((rect.Y1 + rect.Y2) / 2);
        var gates = new (ushort X, ushort Y)[]
        {
            (midX, rect.Y1),
            (midX, rect.Y2),
            (rect.X1, midY),
            (rect.X2, midY),
        };
        return new PoiStamp(id, PoiKind.Town, (ushort)cx, (ushort)cy, 1,
            Footprint: rect, Gates: gates);
    }

    private static void AssertNoStrictInterior(
        IEnumerable<(ushort X, ushort Y)> path, RectU16 fp)
    {
        foreach (var (x, y) in path)
        {
            bool strictInterior =
                x > fp.X1 && x < fp.X2 && y > fp.Y1 && y < fp.Y2;
            Assert.False(strictInterior,
                $"path cell ({x},{y}) is strictly inside footprint {fp.X1},{fp.Y1}-{fp.X2},{fp.Y2}");
        }
    }
}
