using CentrED.MapGen.IR;
using CentrED.MapGen.Passes.Network;
using CentrED.MapGen.Pipeline;
using CentrED.Network;

namespace CentrED.MapGen.Tests;

// Roads keep a margin from mountain feet (Road Graph MountainClearance), and ranges get mining
// pockets joined by branch paths (Mountain Path MinesPerThousandCells).
public class MountainRoadsTests
{
    private static GenContext Ctx(GenIR ir) => new()
    {
        IR = ir, Rng = new Random(0), Report = new PassReport { PassName = "test" },
    };

    // Flat grass 96x64 with a mountain block in the middle band's north half (x 32..63, y 0..27):
    // a road from (8, 30) to (88, 30) can run along its foot (y 28) or keep off it.
    private static GenIR Valley()
    {
        var ir = new GenIR(96, 64, new RectU16(0, 0, 95, 63), 3);
        ir.EnsureHeight(); ir.EnsureBiome(); ir.EnsureLandId();
        for (int y = 0; y < 64; y++)
        for (int x = 0; x < 96; x++)
        {
            int i = ir.Index(x, y);
            bool rock = x is >= 32 and <= 63 && y <= 27;
            ir.Biome![i] = (byte)(rock ? BiomeId.Mountain : BiomeId.Grassland);
            ir.Height_Z![i] = (sbyte)(rock ? 40 : 0);
            ir.LandId![i] = (ushort)(rock ? 0x22C : 0x3);
        }
        ir.Pois.Add(new PoiStamp(1, PoiKind.ControlPoint, 8, 28, 0));
        ir.Pois.Add(new PoiStamp(2, PoiKind.ControlPoint, 88, 28, 0));
        return ir;
    }

    private static int ClosestToRock(GenIR ir)
    {
        var road = Assert.Single(ir.Roads);
        return road.Path.Where(c => c.X is >= 32 and <= 63).Min(c => c.Y - 27);
    }

    [Fact]
    public void Clearance_KeepsRoadOffTheMountainFoot()
    {
        var hug = Valley();
        new RoadGraphPass().Run(Ctx(hug), new RoadGraphParams { MountainClearance = 0, MaxEdgesPerPoi = 1 });
        var clear = Valley();
        new RoadGraphPass().Run(Ctx(clear), new RoadGraphParams { MountainClearance = 6, MaxEdgesPerPoi = 1 });

        Assert.Equal(1, ClosestToRock(hug));          // along the foot, one cell out
        Assert.True(ClosestToRock(clear) >= 4, $"road came within {ClosestToRock(clear)} of the rock");
    }

    [Fact]
    public void Mines_CarvePocketsWithBranchesOutOfTheRange()
    {
        // one 120x120 range in a 160x160 grass map: big enough for several pockets
        var ir = new GenIR(160, 160, new RectU16(0, 0, 159, 159), 11);
        ir.EnsureHeight(); ir.EnsureBiome(); ir.EnsureLandId();
        for (int y = 0; y < 160; y++)
        for (int x = 0; x < 160; x++)
        {
            int i = ir.Index(x, y);
            bool rock = x is >= 20 and < 140 && y is >= 20 and < 140;
            ir.Biome![i] = (byte)(rock ? BiomeId.Mountain : BiomeId.Grassland);
            ir.Height_Z![i] = (sbyte)(rock ? 20 + Math.Min(Math.Min(x - 20, 139 - x), Math.Min(y - 20, 139 - y)) / 4 : 0);
            ir.LandId![i] = (ushort)(rock ? 0x22C : 0x3);
        }
        var ctx = Ctx(ir);
        new MountainTrailPass().Run(ctx, new MountainTrailParams { MinesPerThousandCells = 0.5, MineRadius = 4, MineSpacing = 24 });

        Assert.Contains(ctx.Report.Notes, n => n.Contains("mines=") && !n.Contains("mines=0"));
        // dirt cells deep inside the rock (beyond the edge band) exist: pockets and their branches
        int deep = 0;
        for (int y = 40; y < 120; y++)
        for (int x = 40; x < 120; x++)
            if ((BiomeId)ir.Biome![ir.Index(x, y)] == BiomeId.Road) deep++;
        Assert.True(deep > 50, $"only {deep} carved cells deep in the range");

        // off: no mines are carved
        var off = new GenIR(160, 160, new RectU16(0, 0, 159, 159), 11);
        off.EnsureHeight(); off.EnsureBiome(); off.EnsureLandId();
        Array.Copy(ir.Height_Z!, off.Height_Z!, 0);
        var octx = Ctx(off);
        new MountainTrailPass().Run(octx, new MountainTrailParams { MinesPerThousandCells = 0 });
        Assert.DoesNotContain(octx.Report.Notes, n => n.Contains("mines=") && !n.Contains("mines=0"));
    }
}
