using CentrED.MapGen.IR;
using CentrED.MapGen.Pipeline;

namespace CentrED.MapGen.Passes.Scatter;

public sealed class CaveWallStaticsParams
{
    [TunableDisplay("Wall static density (0-1)", Tooltip = "Probability of placing a cave-wall static on each CaveWall biome cell. The NoDraw 0x01AE land under CaveWall renders BLACK without a static on top, so density should be ~1.0 unless intentionally producing patchy ruins.")]
    [TunableRange(0.0, 1.0)]
    public double WallDensity { get; set; } = 1.0;

    [TunableDisplay("Geology density (0-0.5)", Tooltip = "Stalagmites, flowstone, and cave-floor overlay statics on Cave cells. ~0.03 matches the real Felucca surface-cave density.")]
    [TunableRange(0.0, 0.5)]
    public double GeologyDensity { get; set; } = 0.030;

    [TunableDisplay("Camp density (0-0.2)", Tooltip = "Hay, straw pillows, fur, and pile-of-hides statics — indicates a creature has been camping here.")]
    [TunableRange(0.0, 0.2)]
    public double CampDensity { get; set; } = 0.012;

    [TunableDisplay("Bones density (0-0.2)", Tooltip = "Skull piles, bone piles, skeletons, and loose bones — kill-site decor.")]
    [TunableRange(0.0, 0.2)]
    public double BoneDensity { get; set; } = 0.006;

    [TunableDisplay("Rugs density (0-0.05)", Tooltip = "Single-tile furs — rare lair-end accent (multi-tile bearskin rugs are not scattered piecewise).")]
    [TunableRange(0.0, 0.05)]
    public double RugDensity { get; set; } = 0.002;
}

// Places UO surface-cave statics ON TOP of land tiles produced by ImageImportPass:
//   * CaveWall cells (NoDraw 0x01AE) get one of the 0x0241-0x0243 cave-wall statics —
//     this is the visible dungeon wall in the opt-in NoDraw rendering model. Dormant
//     by default because the standard painter uses HighMountain (Mountain land) for
//     walls instead, but kept here so a future painter can switch models cleanly.
//   * Cave cells get up to one decor static per tile, rolled across four categories
//     (geology / camp / bones / rugs) at category-specific densities. Combined default
//     is ~5% — slightly above the real source's 1.7% but produces visible variety.
//
// Tile-id provenance (Felucca dumps 20260517_150323 + 20260517_154932):
//   Wall statics:   0x0241 (665x) + 0x0242 (693x) + 0x0243 (65x) at (5812,420)→(5861,475).
//                   0x0242 is the most common variant, weighted 5x.
//   Geology:        0x08E0-0x08EA stalagmites/flowstone + 0x0551-0x0553, 0x056A
//                   cave-floor overlay statics — the iconic cave geology palette.
//   Camp:           Hay/straw/fur/hides — "something has been sleeping here".
//   Bones:          Single-tile skull piles, bone piles, skulls — kill evidence.
//   Rugs:           Single-tile furs — rare boss-lair accent.
//   Only single-tile decor is scattered here; multi-tile objects (2-piece skeletons,
//   3x3 bearskin rugs) would be cut in pieces by a per-tile roll.
//
// Reads:  Biome, Height.
// Writes: StaticOps.
public sealed class CaveWallStaticsPass : IGenerationPass
{
    public string Name => "Cave Wall Statics";
    public string Category => "Scatter";

    public IrFields Reads => IrFields.Biome | IrFields.Height;
    public IrFields Writes => IrFields.StaticOps;

    public object CreateDefaultParams() => new CaveWallStaticsParams();

    // Cave-wall statics. 0x0242 dominant in source — weighted 5x.
    private static readonly ushort[] WallStatics =
    {
        0x0241, 0x0242, 0x0242, 0x0242, 0x0242, 0x0242, 0x0243,
    };

    // Geology: stalagmites + flowstone (0x08E0-0x08EA) + cave-floor overlay statics
    // (0x0551-0x0553, 0x056A). The overlay statics ARE named "cave floor" in tiledata
    // and look like darker mossy patches on the floor — adds visual depth.
    private static readonly ushort[] GeologyStatics =
    {
        0x08E0, 0x08E1, 0x08E2, 0x08E3, 0x08E4, 0x08E5, 0x08E6, 0x08E7, 0x08E8, 0x08E9, 0x08EA,
        0x0551, 0x0552, 0x0553, 0x056A,
    };

    // Camp evidence: hay piles, straw pillows, fur, pile of hides.
    private static readonly ushort[] CampStatics =
    {
        0x0F34, 0x0F35,                 // hay
        0x1036, 0x1037,                 // hay variants
        0x11EA, 0x11EB,                 // straw pillows
        0x11FB,                         // fur
        0x1078,                         // pile of hides
    };

    // Bones: single-tile pieces only. Every entry is one complete object on one tile.
    // Excluded on purpose: 0x1AD8/0x1AD9/0x1ADB/0x1ADC (the big skull piles — halves of
    // two-tile piles, flagged impassable) and the 0x1D8E-0x1D91 skeletons (each skeleton
    // is two pieces); a lone half drawn on one tile looks cut off.
    private static readonly ushort[] BoneStatics =
    {
        0x1ADA, 0x1ADD, 0x1ADE, 0x1ADF,                           // small skull piles
        0x1AE0, 0x1AE3, 0x1AE4,                                   // skulls
        0x1B0D, 0x1B0E, 0x1B0F, 0x1B17,                           // bone piles + rib cage
        0x1B19, 0x1B1A,                                           // bone shards
        0x1CE9,                                                    // head
        0x0ECC, 0x0ECE,                                            // bones
    };

    // "Rug" accent: single-tile furs (0x11F4-0x11F7). The old bearskin rugs
    // (0x1E36-0x1E48) are 3x3 multi-tile rugs; one random piece looked like a torn scrap.
    private static readonly ushort[] RugStatics =
    {
        0x11F4, 0x11F5, 0x11F6, 0x11F7,
    };

    public void Run(GenContext ctx, object parameters)
    {
        var p = (CaveWallStaticsParams)parameters;
        var ir = ctx.IR;
        if (ir.Biome is null || ir.Height_Z is null) return;
        var biome = ir.Biome;
        var z = ir.Height_Z;
        var scope = ir.Scope;

        int walls = 0, geology = 0, camp = 0, bones = 0, rugs = 0;
        for (ushort y = scope.Y1; y <= scope.Y2; y++)
        for (ushort x = scope.X1; x <= scope.X2; x++)
        {
            int idx = ir.Index(x, y);
            var b = (BiomeId)biome[idx];

            if (b == BiomeId.CaveWall)
            {
                if (ctx.Rng.NextDouble() > p.WallDensity) continue;
                ushort wallId = WallStatics[ctx.Rng.Next(WallStatics.Length)];
                ir.StaticOps.Add(new StaticOp(StaticOpKind.Add, x, y, z[idx], wallId, 0));
                walls++;
            }
            else if (b == BiomeId.Cave)
            {
                // Roll category-by-category. First hit wins (one decor per tile) so the
                // floor stays readable and statics don't stack. Order is "most common"
                // first so densely-decorated tiles bias toward geology over rare rugs.
                if (ctx.Rng.NextDouble() < p.GeologyDensity)
                {
                    ushort id = GeologyStatics[ctx.Rng.Next(GeologyStatics.Length)];
                    ir.StaticOps.Add(new StaticOp(StaticOpKind.Add, x, y, z[idx], id, 0));
                    geology++;
                }
                else if (ctx.Rng.NextDouble() < p.CampDensity)
                {
                    ushort id = CampStatics[ctx.Rng.Next(CampStatics.Length)];
                    ir.StaticOps.Add(new StaticOp(StaticOpKind.Add, x, y, z[idx], id, 0));
                    camp++;
                }
                else if (ctx.Rng.NextDouble() < p.BoneDensity)
                {
                    ushort id = BoneStatics[ctx.Rng.Next(BoneStatics.Length)];
                    ir.StaticOps.Add(new StaticOp(StaticOpKind.Add, x, y, z[idx], id, 0));
                    bones++;
                }
                else if (ctx.Rng.NextDouble() < p.RugDensity)
                {
                    ushort id = RugStatics[ctx.Rng.Next(RugStatics.Length)];
                    ir.StaticOps.Add(new StaticOp(StaticOpKind.Add, x, y, z[idx], id, 0));
                    rugs++;
                }
            }
        }

        ctx.Report.StaticsAdded += walls + geology + camp + bones + rugs;
        ctx.Report.Notes.Add($"cave walls={walls} geology={geology} camp={camp} bones={bones} rugs={rugs}");
    }
}
