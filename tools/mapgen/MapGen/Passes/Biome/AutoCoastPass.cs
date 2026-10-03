using CentrED.MapGen.Data;
using CentrED.MapGen.IR;
using CentrED.MapGen.Pipeline;

namespace CentrED.MapGen.Passes.Biome;

public sealed class AutoCoastParams
{
    // Ignored: the pass uses GenIR.SeaLevelZ / OceanZ. Kept so presets that set it load.
    [TunableDisplay("Sea level Z (legacy, ignored)")] [TunableRange(-128, 127)]
    public int SeaLevelZ { get; set; } = 0;

    [TunableDisplay("Tweak coastal Z", Tooltip = "Adjust adjacent land Z to slope into water")]
    public bool TweakTerrain { get; set; } = true;

    [TunableDisplay("Sample radius", Tooltip = "How far to look for water neighbours")]
    [TunableRange(1, 3)]
    public int SampleRadius { get; set; } = 1;

    [TunableDisplay("Draw shore depth", Tooltip = "Drop the Z of water cells adjacent to land by ShoreDepthZDrop, creating a subtle slope below the sand at the coast. Keeps the cells as water (0xA8-0xAB), so no brown patches or cliff sides are introduced — the only visible effect is a slight dip toward shore.")]
    public bool DrawShoreDepth { get; set; } = true;

    [TunableDisplay("Shore depth Z drop", Tooltip = "How far below sea level to push water cells at the coast. 2-4 reads as a gentle Felucca-like slope; values >5 start producing visible iso side walls.")]
    [TunableRange(0, 10)]
    // Earlier iterations of this pass mirrored CentrED's CoastlineTool by REWRITING the
    // water cell's LandId to a brown bottom-tile (0x4C-0x6F) at z=-10. CentrED's preview
    // covered the resulting cliff side by painting water-object statics on adjacent SAND
    // cells (NW-context check in CoastlineTool.cs:240-244), but our static placement on
    // the brown cell itself left the cliff exposed as a black void (procgen image 13/18).
    // Switched to a simpler approach: drop ONLY the water cell's Z; keep the LandId as
    // water so the water animation continues to render naturally. The visible slope is
    // smaller than CentrED's brown-bottom variant but matches real Felucca (image 10).
    public int ShoreDepthZDrop { get; set; } = 2;
}

// Coast detection pass. Used to also place shoreline rim statics (0x179D-0x17AC),
// but Felucca puts rim on the WATER side of the boundary, not the land side
// (reference image #62/#63), so rim placement now lives in DigShorePass where
// the BFS distance field is available and SandExtension has already painted
// the new sand boundary. This pass keeps the rules that need a land-cell POV:
//
//   1. Convert land→water when 3+ of the 4 cardinal neighbours are water
//      (submerged-cell rule). The cell becomes ShallowWater (River if all its water
//      neighbours are river) at GenIR.OceanZ with a lattice-picked water tile.
//      Critical for DigShorePass which BFS-seeds at sand and propagates through
//      water LandIds — without this conversion, deeply-surrounded land slivers
//      block the BFS and leave brown-pyramid artefacts (procgen image #16).
//
//   2. TweakTerrain: pull adjacent land Z down 2 when the NE corner is water.
//
//   3. DrawShoreDepth: drop water cells touching land by ShoreDepthZDrop.
//      Gated off by DigShorePass when DigShorePass.Enabled (DigShore manages
//      its own Z field), so this only fires in pipelines that skip DigShore.
public sealed class AutoCoastPass : IGenerationPass
{
    public string Name => "Auto Coast";
    public string Category => "Biome";

    public IrFields Reads => IrFields.Height | IrFields.LandId;
    public IrFields Writes => IrFields.Height | IrFields.LandId;

    public object CreateDefaultParams() => new AutoCoastParams();

    public void Run(GenContext ctx, object parameters)
    {
        var p = (AutoCoastParams)parameters;
        var ir = ctx.IR;
        if (ir.LandId is null || ir.Height_Z is null) return;

        var land = ir.LandId;
        var z = ir.Height_Z;
        var bio = ir.Biome; // optional: kept in sync when present
        var scope = ir.Scope;
        int sea = ir.SeaLevelZ;
        var water = ir.Tables.Water.Length > 0 ? ir.Tables.Water : new ushort[] { 0xA8, 0xA9, 0xAA, 0xAB };
        int converted = 0;
        int adjusted = 0;

        // Submerged-cell rule, on a snapshot per sweep (F-3.4: reading the live array let
        // each row see the previous row's conversions and the cascade ate the coast).
        // Only the 4 CARDINAL neighbours count: with 8, every convex corner of a straight
        // coastline (3 water cells around a diagonal) was eaten, one sweep at a time.
        // Repeats until stable so a 1-wide spit dissolves completely.
        var landSnapshot = new ushort[land.Length];
        for (int sweep = 0; sweep < 4; sweep++)
        {
            Array.Copy(land, landSnapshot, land.Length);
            int convertedThisSweep = 0;
            for (ushort y = scope.Y1; y <= scope.Y2; y++)
            {
                ctx.Cancellation.ThrowIfCancellationRequested();
                for (ushort x = scope.X1; x <= scope.X2; x++)
                {
                    int idx = ir.Index(x, y);
                    if (TileFlags.IsWaterLandId(landSnapshot[idx])) continue;
                    int waterSides = 0, riverSides = 0;
                    for (int k = 0; k < 4; k++)
                    {
                        int nx = x + (k == 0 ? 1 : k == 1 ? -1 : 0);
                        int ny = y + (k == 2 ? 1 : k == 3 ? -1 : 0);
                        if (nx < 0 || ny < 0 || nx >= ir.Width || ny >= ir.Height) continue;
                        int ni = ir.Index(nx, ny);
                        if (!TileFlags.IsWaterLandId(landSnapshot[ni])) continue;
                        waterSides++;
                        if (bio is not null && (BiomeId)bio[ni] == BiomeId.River) riverSides++;
                    }
                    if (waterSides < 3) continue;
                    land[idx] = LatticePick.Pick(water, x, y, ir.Seed);
                    z[idx] = (sbyte)ir.OceanZ;
                    if (bio is not null)
                        bio[idx] = (byte)(riverSides == waterSides ? BiomeId.River : BiomeId.ShallowWater);
                    convertedThisSweep++;
                }
            }
            converted += convertedThisSweep;
            if (convertedThisSweep == 0) break;
        }

        // TweakTerrain: a land cell whose NW (iso "up") neighbour is water drops by 2 (never
        // below sea level) so the water sprite reads as lying on top.
        if (p.TweakTerrain)
        {
            Array.Copy(land, landSnapshot, land.Length);
            for (ushort y = (ushort)Math.Max(1, (int)scope.Y1); y <= scope.Y2; y++)
            for (ushort x = (ushort)Math.Max(1, (int)scope.X1); x <= scope.X2; x++)
            {
                int idx = ir.Index(x, y);
                if (TileFlags.IsWaterLandId(landSnapshot[idx])) continue;
                if (!TileFlags.IsWaterLandId(landSnapshot[ir.Index(x - 1, y - 1)])) continue;
                int target = Math.Max(sea, z[idx] - 2);
                if (target < z[idx]) { z[idx] = (sbyte)target; adjusted++; }
            }
        }

        // Shore depth (only when Dig Shore is off): water touching land sits ShoreDepthZDrop
        // BELOW the ocean plane. It never raises water (the old code lifted -5 water to -2).
        int depthDropped = 0;
        if (p.DrawShoreDepth && p.ShoreDepthZDrop > 0)
        {
            Array.Copy(land, landSnapshot, land.Length);
            int depthZ = Math.Clamp(ir.OceanZ - p.ShoreDepthZDrop, sbyte.MinValue, sbyte.MaxValue);
            for (ushort y = scope.Y1; y <= scope.Y2; y++)
            for (ushort x = scope.X1; x <= scope.X2; x++)
            {
                int idx = ir.Index(x, y);
                if (!TileFlags.IsWaterLandId(landSnapshot[idx])) continue;
                if (bio is not null && (BiomeId)bio[idx] == BiomeId.River) continue;
                bool touchesLand = false;
                foreach (var (dx, dy, _) in CoastRimMatcher.Offsets)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= ir.Width || ny >= ir.Height) continue;
                    if (!TileFlags.IsWaterLandId(landSnapshot[ir.Index(nx, ny)])) { touchesLand = true; break; }
                }
                if (!touchesLand || z[idx] <= depthZ) continue;
                z[idx] = (sbyte)depthZ;
                depthDropped++;
            }
        }

        ctx.Report.Notes.Add($"converted {converted} submerged land cells, adjusted {adjusted} land Z, shore-depth Z drops {depthDropped}");
    }
}
