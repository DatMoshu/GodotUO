using CentrED.MapGen.IR;
using CentrED.MapGen.Pipeline;

namespace CentrED.MapGen.Passes.Scatter;

public sealed class MountainEdgeStaticsParams
{
    [TunableDisplay("Edge density (0-1)", Tooltip = "Probability of placing a boulder on the GROUND cell adjacent to a mountain edge. Boulders go on the grass/dirt next to the rock face, NOT on the mountain itself — mountain textures stay clean.")]
    [TunableRange(0.0, 1.0)]
    public double EdgeDensity { get; set; } = 0.15;

    [TunableDisplay("Interior splatter (0-1)", Tooltip = "Probability of placing a small rock on an interior mountain cell. Default 0 — mountain interior decoration is handled by MountainPathPass.")]
    [TunableRange(0.0, 1.0)]
    public double InteriorDensity { get; set; } = 0.0;

    [TunableDisplay("Min edge spacing (tiles)", Tooltip = "Suppress edge boulders closer than this to another boulder. Larger = sparser scatter along the mountain perimeter.")]
    [TunableRange(1, 8)]
    public int InteriorMinSpacing { get; set; } = 4;
}

// DragonMod's "rock2mountain" idea ported to a pipeline pass: every mountain cell that
// touches a non-mountain neighbour gets a cliff/boulder static dropped on it. Interior
// cells get sparse smaller-rock splatter to break the visible weave of the 0xDC..0xDF
// LandId tiling. Without this pass mountains read as a flat grey carpet (image 20).
//
// Tile ids:
//   Edge (cliff/boulder):    0x1363..0x1367 small stones + 0x1773..0x177B boulders
//                            (the prior 0x1797..0x179C pool was a BUG — those IDs are
//                            water-surface overlay statics shared with DigShorePass /
//                            CoastRimMatcher.OverlayStatics, producing visible blue
//                            water squares scattered on every mountain edge.)
//   Interior (small rock):   0x1363..0x1366 — small loose stones
//
// Reads:  Biome, LandId, Height.
// Writes: StaticOps.
public sealed class MountainEdgeStaticsPass : IGenerationPass
{
    public string Name => "Mountain Edge Statics";
    public string Category => "Scatter";

    public IrFields Reads => IrFields.Biome | IrFields.LandId | IrFields.Height;
    public IrFields Writes => IrFields.StaticOps;

    public object CreateDefaultParams() => new MountainEdgeStaticsParams();

    private static readonly ushort[] EdgeRocks =
    {
        // Large boulders / cliff-edge rocks. Small stones 0x1363-0x1367 are
        // intentionally NOT here — those belong on the mountain path (see
        // MountainPathPass). Edges only get the chunky cliff boulders.
        0x1773, 0x1774, 0x1775, 0x1776, 0x1777, 0x177B,
    };

    // No interior splatter pool — interior mountain decoration is handled by
    // MountainPathPass which scatters small rocks (0x1363-0x1367) along a
    // winding dirt path rather than randomly across the whole ridge.
    private static readonly ushort[] InteriorRocks = { 0x1363, 0x1364, 0x1365, 0x1366 };

    public void Run(GenContext ctx, object parameters)
    {
        var p = (MountainEdgeStaticsParams)parameters;
        var ir = ctx.IR;
        if (ir.Biome is null || ir.Height_Z is null) return;
        var biome = ir.Biome;
        var z = ir.Height_Z;
        var scope = ir.Scope;

        // Cardinal+diagonal neighbours: a single non-mountain neighbour qualifies as edge.
        ReadOnlySpan<(int dx, int dy)> nb = stackalloc (int, int)[]
        {
            (1, 0), (-1, 0), (0, 1), (0, -1),
            (1, 1), (1, -1), (-1, 1), (-1, -1),
        };

        // Track interior placements for spacing rejection. Stored as a scope-local 8-bit
        // grid: 1 = a rock was placed within the last InteriorMinSpacing tiles.
        int sw = scope.Width;
        int sh = scope.Height;
        var placedInterior = new byte[sw * sh];
        int spacing = Math.Max(1, p.InteriorMinSpacing);

        int edge = 0, interior = 0;
        for (ushort y = scope.Y1; y <= scope.Y2; y++)
        for (ushort x = scope.X1; x <= scope.X2; x++)
        {
            int idx = ir.Index(x, y);
            var b = (BiomeId)biome[idx];
            if (b != BiomeId.Mountain && b != BiomeId.HighMountain) continue;

            // Find a non-mountain neighbour cell — that's where the boulder goes.
            // Skipping border tiles (off-map "edge") so boulders don't end up in
            // the void. The boulder sits on grass/sand/dirt next to the rock face,
            // not on the rock itself — matches the user's intent that mountain
            // textures stay clean.
            int boulderX = -1, boulderY = -1, boulderIdx = -1;
            foreach (var (dx, dy) in nb)
            {
                int nx = x + dx, ny = y + dy;
                if ((uint)nx >= ir.Width || (uint)ny >= ir.Height) continue;
                int nIdx = ir.Index(nx, ny);
                var nb_b = (BiomeId)biome[nIdx];
                if (nb_b == BiomeId.Mountain || nb_b == BiomeId.HighMountain) continue;
                // Skip water / deep water — no boulders floating in the ocean.
                if (nb_b == BiomeId.DeepWater || nb_b == BiomeId.ShallowWater) continue;
                boulderX = nx; boulderY = ny; boulderIdx = nIdx;
                break;
            }
            if (boulderIdx < 0)
            {
                if (p.InteriorDensity > 0 && ctx.Rng.NextDouble() < p.InteriorDensity)
                {
                    ushort rock = InteriorRocks[ctx.Rng.Next(InteriorRocks.Length)];
                    ir.StaticOps.Add(new StaticOp(StaticOpKind.Add, x, y, z[idx], rock, 0));
                    interior++;
                }
                continue;
            }

            if (ctx.Rng.NextDouble() > p.EdgeDensity) continue;

            // Min-spacing suppression against other already-placed boulders.
            int lx = boulderX - scope.X1, ly = boulderY - scope.Y1;
            if ((uint)lx >= (uint)sw || (uint)ly >= (uint)sh) continue;
            bool tooClose = false;
            for (int oy = -spacing; oy <= spacing && !tooClose; oy++)
            for (int ox = -spacing; ox <= spacing; ox++)
            {
                int qx = lx + ox, qy = ly + oy;
                if ((uint)qx >= (uint)sw || (uint)qy >= (uint)sh) continue;
                if (placedInterior[qy * sw + qx] != 0) { tooClose = true; break; }
            }
            if (tooClose) continue;

            ushort id = EdgeRocks[ctx.Rng.Next(EdgeRocks.Length)];
            ir.StaticOps.Add(new StaticOp(StaticOpKind.Add, (ushort)boulderX, (ushort)boulderY, z[boulderIdx], id, 0));
            placedInterior[ly * sw + lx] = 1;
            edge++;
        }

        ctx.Report.StaticsAdded += edge + interior;
        ctx.Report.Notes.Add($"mountain edge={edge} interior={interior}");
    }
}
