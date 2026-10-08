using CentrED.MapGen.IR;
using CentrED.MapGen.Passes.Network;
using CentrED.MapGen.Pipeline;
using CentrED.Network;

namespace CentrED.MapGen.Tests;

// Road Graph's mountain-distance walk visits every mountain cell. A stackalloc inside that walk's
// loop pushes 16 bytes of stack per visited cell until the method returns, which overflows the
// thread stack (an uncatchable crash, exit 127) on a large map. This map is almost all rock.
public class RoadGraphStackTests
{
    [Fact]
    public void MountainDistance_OnA1024MapOfRock_StaysWithinAOneMegabyteStack()
    {
        const int size = 1024;
        var ir = new GenIR(size, size, new RectU16(0, 0, size - 1, size - 1), 5);
        ir.EnsureHeight(); ir.EnsureBiome(); ir.EnsureLandId();
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            int i = ir.Index(x, y);
            bool clearing = x is >= 480 and < 544 && y is >= 480 and < 544;
            ir.Biome![i] = (byte)(clearing ? BiomeId.Grassland : BiomeId.Mountain);
            ir.Height_Z![i] = (sbyte)(clearing ? 0 : 40);
            ir.LandId![i] = (ushort)(clearing ? 0x3 : 0x22C);
        }
        ir.Pois.Add(new PoiStamp(1, PoiKind.ControlPoint, 490, 512, 0));
        ir.Pois.Add(new PoiStamp(2, PoiKind.ControlPoint, 534, 512, 0));

        var ctx = new GenContext { IR = ir, Rng = new Random(0), Report = new PassReport { PassName = "test" } };
        Exception? failure = null;
        // Thread.MaxStackSize is explicit so the check does not depend on the host's default.
        var worker = new Thread(() =>
        {
            try { new RoadGraphPass().Run(ctx, new RoadGraphParams { MountainClearance = 6, MaxEdgesPerPoi = 1 }); }
            catch (Exception e) { failure = e; }
        }, maxStackSize: 1024 * 1024);
        worker.Start();
        worker.Join();

        Assert.Null(failure);
        Assert.Single(ir.Roads);
    }
}
