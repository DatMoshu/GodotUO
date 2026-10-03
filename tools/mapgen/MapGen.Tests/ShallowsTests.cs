using CentrED.MapGen.Data;
using CentrED.MapGen.IR;
using CentrED.MapGen.Passes.Biome;
using CentrED.MapGen.Pipeline;
using CentrED.Network;
using CentrED.MapGen.Presets;

namespace CentrED.MapGen.Tests;

// Shallows: Felucca's seabed shapes over Dig Shore's dug band, and rippled sand on the waterline.
public class ShallowsTests
{
    // Beach on x < 10, dug bed on x >= 10 (ocean biome, flat bed at the dig depth).
    private static (GenIR Ir, GenContext Ctx) Shore(int size = 24, int coastX = 10)
    {
        var ir = new GenIR((ushort)size, (ushort)size, new RectU16(0, 0, (ushort)(size - 1), (ushort)(size - 1)), 7);
        ir.EnsureHeight(); ir.EnsureBiome(); ir.EnsureLandId();
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int i = ir.Index(x, y);
                bool sea = x >= coastX;
                ir.Biome![i] = (byte)(sea ? BiomeId.DeepWater : BiomeId.Beach);
                ir.LandId![i] = sea ? (ushort)0x64 : (ushort)0x16;
                ir.Height_Z![i] = (sbyte)(sea ? ir.ShoreDigZ : 0);
            }
        return (ir, new GenContext { IR = ir, Rng = new Random(1), Report = new PassReport { PassName = "test" } });
    }

    [Fact]
    public void StraightCoast_LightThenMidThenFlat_AndRippledSand()
    {
        var (ir, ctx) = Shore();
        new ShallowsPass().Run(ctx, new ShallowsParams());
        ushort At(int x, int y) => ir.LandId![ir.Index(x, y)];
        Assert.Equal(ShallowsPass.LightRing["E"], At(10, 12));   // deeper bed to the east
        Assert.Equal(ShallowsPass.MidRing["W"], At(11, 12));     // shallower ring to the west
        Assert.Equal(ShallowsPass.FlatBed, At(15, 12));
        Assert.Equal(ShallowsPass.RippleNE, At(9, 12));          // the water lies east of this sand
        Assert.Equal((ushort)0x16, At(8, 12));                    // sand behind the waterline is unchanged
        Assert.All(Enumerable.Range(10, 14), x => Assert.Equal(ir.ShoreDigZ, (int)ir.Height_Z![ir.Index(x, 12)]));
    }

    [Fact]
    public void Corners_TakeTheirShapes()
    {
        // A sand headland reaching into the sea: the bed wraps around its tip.
        var (ir, ctx) = Shore();
        for (int x = 10; x <= 13; x++) { int i = ir.Index(x, 12); ir.Biome![i] = (byte)BiomeId.Beach; ir.LandId![i] = 0x16; ir.Height_Z![i] = 0; }
        new ShallowsPass().Run(ctx, new ShallowsParams());
        ushort At(int x, int y) => ir.LandId![ir.Index(x, y)];
        Assert.Equal(ShallowsPass.LightRing["S"], At(12, 13));   // south of the headland: the deeper bed lies south
        Assert.Equal(ShallowsPass.LightRing["N"], At(12, 11));   // north of it: the deeper bed lies north
        Assert.Equal(ShallowsPass.LightRing["SE"], At(14, 13));  // off the tip's corner: deeper bed east and south
    }

    [Fact]
    public void OffByDefault_OnInFeluccaStage18()
    {
        var steps = DefaultPipeline.Build();
        Assert.False(steps.Single(s => s.Pass.Name == "Shallows").Enabled);
        Assert.Equal("Shallows", steps[^1].Pass.Name);   // last, so no earlier step's RNG stream moves
        var preset = MapGenPreset.Load(TestRepo.Path("tools/mapgen/MapGen/presets/felucca-stage18.preset.json"));
        preset.ApplyTo(steps);
        Assert.True(steps.Single(s => s.Pass.Name == "Shallows").Enabled);
    }

    [Fact]
    public void NoDugBed_NothingChanges()
    {
        var (ir, ctx) = Shore();
        for (int i = 0; i < ir.TileCount; i++) if (ir.Biome![i] == (byte)BiomeId.DeepWater) { ir.LandId![i] = 0xA8; ir.Height_Z![i] = (sbyte)ir.OceanZ; }
        var before = (ushort[])ir.LandId!.Clone();
        new ShallowsPass().Run(ctx, new ShallowsParams());
        Assert.Equal(before, ir.LandId);
    }
}
