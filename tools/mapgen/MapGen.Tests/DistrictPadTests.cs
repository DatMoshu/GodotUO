using CentrED.MapGen.IR;
using CentrED.MapGen.Passes.Network;
using CentrED.MapGen.Passes.Pois;
using CentrED.MapGen.Pipeline;
using CentrED.Network;

namespace CentrED.MapGen.Tests;

// Fixed town/district sites (Town Sites "Sites"), other worlds' buildable biomes, and a
// bare flattened pad (Town Roads PaintStreets=false, FlattenTarget=poi, FlattenSkirt).
public class DistrictPadTests
{
    private const ushort Size = 64;

    // Desert with a gentle ramp: Z = x / 8, so no 32x32 area is flat.
    private static GenIR RampDesert()
    {
        var ir = new GenIR(Size, Size, new RectU16(0, 0, Size - 1, Size - 1), 5);
        ir.EnsureHeight(); ir.EnsureBiome(); ir.EnsureLandId();
        for (int y = 0; y < Size; y++)
        for (int x = 0; x < Size; x++)
        {
            int i = ir.Index(x, y);
            ir.Height_Z![i] = (sbyte)(2 + x / 8);
            ir.Biome![i] = (byte)BiomeId.Desert;
            ir.LandId![i] = 0x16;
        }
        return ir;
    }

    private static GenContext Ctx(GenIR ir) => new()
    {
        IR = ir, Rng = new Random(0), Report = new PassReport { PassName = "test" },
    };

    [Fact]
    public void FixedSite_PlacesTownWithMedianZ_AndNoRandomTowns()
    {
        var ir = RampDesert();
        var ctx = Ctx(ir);
        new TownSiteFinderPass().Run(ctx, new TownSiteFinderParams { Sites = "16,16,32,32", TownsPerQuadrant = 0 });

        var town = Assert.Single(ir.Pois);
        Assert.Equal(PoiKind.Town, town.Kind);
        Assert.Equal(new RectU16(16, 16, 47, 47), town.Footprint);
        Assert.Equal(4, town.Gates!.Count);
        Assert.Equal(6, town.Z); // x 16..47 -> z 4..7, median 6
    }

    [Fact]
    public void FixedSite_ExplicitZ_BadEntries_AndWaterAreReported()
    {
        var ir = RampDesert();
        for (int x = 0; x < 4; x++) ir.Biome![ir.Index(x, 2)] = (byte)BiomeId.ShallowWater;
        var ctx = Ctx(ir);
        new TownSiteFinderPass().Run(ctx, new TownSiteFinderParams
        {
            Sites = "8,8,8,8,12; 60,60,8,8; 0,0,8,8; nonsense",
            TownsPerQuadrant = 0,
        });

        var town = Assert.Single(ir.Pois);
        Assert.Equal(12, town.Z);
        Assert.Contains(ctx.Report.Warnings, w => w.Contains("leaves the map scope"));
        Assert.Contains(ctx.Report.Warnings, w => w.Contains("touches water"));
        Assert.Contains(ctx.Report.Warnings, w => w.Contains("not x,y,w,h"));
    }

    [Fact]
    public void BuildableBiomes_DefaultRejectsDesert_CsvAcceptsIt()
    {
        var pars = new TownSiteFinderParams
        {
            AutoGridX = 1, AutoGridY = 1, TownsPerQuadrant = 1, TownSize = 16,
            MaxAttempts = 64, MinSpacing = 0, MaxRelief = 99, SeaLevelZ = -100,
        };

        var ir = RampDesert();
        new TownSiteFinderPass().Run(Ctx(ir), pars);
        Assert.Empty(ir.Pois);

        ir = RampDesert();
        pars.BuildableBiomesCsv = "Desert, Savanna";
        new TownSiteFinderPass().Run(Ctx(ir), pars);
        Assert.Single(ir.Pois);

        ir = RampDesert();
        pars.BuildableBiomesCsv = "Dune";
        var ctx = Ctx(ir);
        new TownSiteFinderPass().Run(ctx, pars);
        Assert.Contains(ctx.Report.Warnings, w => w.Contains("unknown biome 'Dune'"));
    }

    [Fact]
    public void BarePad_FlattensToPoiZ_WithSkirt_AndPaintsNoStreets()
    {
        var ir = RampDesert();
        new TownSiteFinderPass().Run(Ctx(ir), new TownSiteFinderParams { Sites = "16,16,32,32,9", TownsPerQuadrant = 0 });
        var landBefore = (ushort[])ir.LandId!.Clone();

        new TownRoadPass().Run(Ctx(ir), new TownRoadParams { PaintStreets = false, FlattenTarget = "poi", FlattenSkirt = 4 });

        for (int y = 16; y <= 47; y++)
        for (int x = 16; x <= 47; x++)
            Assert.Equal(9, ir.Height_Z![ir.Index(x, y)]);
        Assert.Equal(landBefore, ir.LandId);   // no cobble
        Assert.Empty(ir.Roads);
        // Skirt: one tile outside the pad is pulled most of the way toward 9; beyond the skirt is untouched.
        Assert.Equal(8, ir.Height_Z![ir.Index(15, 30)]);   // ground 3, t = 1 - 1/5: 3 + 6 * 0.8 = 7.8

        Assert.Equal((sbyte)(2 + 8 / 8), ir.Height_Z![ir.Index(8, 30)]);
    }

    [Fact]
    public void TownRoads_Defaults_StillPaintStreetsAtCentreZ()
    {
        var ir = RampDesert();
        new TownSiteFinderPass().Run(Ctx(ir), new TownSiteFinderParams { Sites = "16,16,32,32,9", TownsPerQuadrant = 0 });
        new TownRoadPass().Run(Ctx(ir), new TownRoadParams());

        sbyte centre = (sbyte)(2 + 31 / 8);            // the centre cell's own Z, not the site's z=9
        Assert.Equal(centre, ir.Height_Z![ir.Index(20, 20)]);
        Assert.NotEmpty(ir.Roads);
    }
}
