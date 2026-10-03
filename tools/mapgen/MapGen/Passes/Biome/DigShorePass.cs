using CentrED.MapGen.Data;
using CentrED.MapGen.IR;
using CentrED.MapGen.Noise;
using CentrED.MapGen.Pipeline;

namespace CentrED.MapGen.Passes.Biome;

public sealed class DigShoreParams
{
    [TunableDisplay("Dig narrow inlets", Tooltip = "Lower the bed in narrow ocean channels too, allowing shaped water edges instead of full land-water diamonds. Does not extend sand into channels.")]
    public bool DigNarrowInlets { get; set; } = false;
    [TunableDisplay("Enabled", Tooltip = "Replicate Felucca's dug-bottom shore: replaces water LandId near sand with brown-bottom tiles 0x4C-0x64 at negative Z, with water statics overlaid on top to act as the visible water surface. When ON, gates AutoCoastPass.DrawShoreDepth off to avoid double-applying the depth shift.")]
    public bool Enabled { get; set; } = true;

    [TunableDisplay("Dig band width", Tooltip = "Number of water-cell rings outward from the sand frontier that get the brown-bottom + static treatment. Beyond this distance, water remains 0xA8-0xAB at sea level.")]
    [TunableRange(0, 64)]
    public int DigBandWidth { get; set; } = 3;

    [TunableDisplay("Plain deep bed", Tooltip = "Use the uniform bed tile beyond the near-shore rings. Directional gradient art must not be randomly repeated across deep water.")]
    public bool PlainDeepBed { get; set; } = false;

    [TunableDisplay("Sand extension", Tooltip = "Number of water cells touching the original sand to be repainted as sand at SandExtensionZ (extends the visible beach outward by N tiles). Default 2: one tile resolves the surf-line iso step on N/S coasts, two tiles smooths the rim sprite continuity on E/W coasts where the matcher's strict-vs-popcount fallback otherwise produces disconnected corner pieces, and adds the Felucca-style wet-sand gap (image #13) between dry sand interior and the rim/water line.")]
    [TunableRange(0, 4)]
    public int SandExtension { get; set; } = 1;

    [TunableDisplay("Sand extension Z", Tooltip = "Z (relative to sea level) of the SandExtension cells. Clamped into (OceanZ, SeaLevel] = -4..0: sand at or below the water statics' plane (-5) is dry sand drawn under the water. The old default -8 did exactly that.")]
    [TunableRange(-30, 5)]
    public int SandExtensionZ { get; set; } = -2;

    [TunableDisplay("Dig Z (legacy, ignored)", Tooltip = "Ignored: the dug bottom is GenIR.ShoreDigZ (sea level - 15). Uniform Land-tile Z for every dug cell. Felucca samples (image #54/#55) confirm a flat Z=-15 across the whole zone — varying brown texture via LandId, NOT via Z. Z gradients between rings produce visible cliff-face brown pyramids the water statics can't cover.")]
    [TunableRange(-30, 0)]
    public int DigZ { get; set; } = -15;

    [TunableDisplay("Static Z (legacy, ignored)", Tooltip = "Ignored: water statics sit on GenIR.OceanZ (sea level - 5). Elevation for the overlaid water statics. Felucca samples place 0x179A at Z=-5 above Land=0x64 at Z=-15.")]
    [TunableRange(-30, 5)]
    public int StaticZ { get; set; } = -5;

    [TunableDisplay("Ocean Z (legacy, ignored)", Tooltip = "Ignored: open water uses GenIR.OceanZ. Uniform Z for every water LandId cell OUTSIDE the dig band. Default -5 matches the rest of Felucca (the live cells around our paint scope sit at Z=-5). Without this, our scope's main water sits at Z=0 and the rest of the map at Z=-5, producing the visible diagonal step in image #58.")]
    [TunableRange(-30, 5)]
    public int OceanZ { get; set; } = -5;

    [TunableDisplay("Overlay open water", Tooltip = "Legacy opt-in: also emit a water-surface static on every open-water cell beyond the dug band. Real Felucca has almost none there (12.6k of 14.5M cells), so the default is off. Felucca image #59 shows open ocean as Land 0x60 Z=-15 + Static 0x179B Z=-5 — the visible water surface IS the static.")]
    public bool OverlayOpenWater { get; set; } = false;

    [TunableDisplay("Rim static Z (legacy, ignored)", Tooltip = "Ignored: rim statics use GenIR.OceanZ. Elevation for the directional shoreline rim statics (0x179D-0x17AC). Default -5 = StaticZ so the rim sits on the same plane as the water-surface overlay. Rim is placed on the FIRST dig-ring cell (water side of the boundary), matching Felucca reference image #62 where rim 0x17A0/0x17A3/0x17A6/0x17AC sits on water LandIds 0x004C-0x0058 — NOT on sand. Land-side rim placement (the original AutoCoastPass behaviour) buries rim under SandExtension cells; the bug was visible when lowering sand with Elevate F4 and seeing rim sprites emerge.")]
    [TunableRange(-30, 5)]
    public int RimStaticZ { get; set; } = -5;

    // When true, signals AutoCoastPass to skip its DrawShoreDepth Z-drop so the two
    // passes don't fight over the same water-cell Z values. Set by DefaultPipeline
    // when both DigShorePass and AutoCoastPass are enabled; DigShore owns the Z field
    // in that configuration.
    public bool SuppressAutoShoreDepth { get; set; } = false;
}

// Replicates the Felucca pattern observed in dump screenshots #45/#49/#50:
// near every sand→water boundary, the seabed Land tile is dug down to a
// brown-bottom variant (0x4C-0x64, gradient by depth) at negative Z, with a
// dense overlay of water statics from the 0x1797-0x179C family at Z=-5 acting
// as the visible water surface.
//
// Pipeline order: runs AFTER AutoCoastPass (submerged slivers already converted to
// water). Works on OCEAN water only (Deep/Shallow biome): ocean cells within
// SandExtension of land become beach (Biome=Beach, Z just above the water plane), the
// next DigBandWidth rings become brown bottom at GenIR.ShoreDigZ under water statics at
// GenIR.OceanZ, and rim statics go on the first dug ring. Shore that is not within reach
// of genuinely open water (narrow straits, inlets) is left as plain water.
public sealed class DigShorePass : IGenerationPass
{
    public string Name => "Dig Shore";
    public string Category => "Biome";

    public IrFields Reads => IrFields.LandId | IrFields.Height;
    public IrFields Writes => IrFields.LandId | IrFields.Height | IrFields.StaticOps;

    public object CreateDefaultParams() => new DigShoreParams();

    // Ring-graded brown-bottom Land tile pools. Picked via LatticePick so the
    // distribution is even across each ring without RNG sequence bias. Z is uniform
    // across all rings (DigZ); only the LandId varies by ring distance to give a
    // texture gradient like Felucca image #45 without exposing cliff-face brown.
    private static readonly ushort[] Ring1Tiles = { 0x4C, 0x4D, 0x50, 0x53 };
    private static readonly ushort[] Ring2Tiles = { 0x54, 0x58, 0x5C };
    private static readonly ushort[] Ring3Tiles = { 0x60, 0x64 };

    // Sand pool for the SandExtension cells (water cells right next to original
    // beach repainted as walkable sand). Matches the procgen Beach palette.
    private static readonly ushort[] SandTiles = { 0x16, 0x17, 0x18, 0x19 };

    // Water surface static pool — the "wet" textured statics that Felucca lays over
    // brown-bottom land. Samples from images #49/#50: 0x179A, 0x1797, 0x1798, 0x1799,
    // 0x179B, 0x179C all observed densely overlapping the dug zone.
    private static readonly ushort[] OverlayStatics = { 0x1797, 0x1798, 0x1799, 0x179A, 0x179B, 0x179C };

    public void Run(GenContext ctx, object parameters)
    {
        var p = (DigShoreParams)parameters;
        if (!p.Enabled || p.DigBandWidth <= 0) return;

        var ir = ctx.IR;
        if (ir.LandId is null || ir.Height_Z is null) return;
        var l = ir.LandId;
        var z = ir.Height_Z;
        var bio = ir.Biome;
        var scope = ir.Scope;
        int sw = scope.Width, sh = scope.Height;
        int n = sw * sh;

        int G(int li) => ir.Index(scope.X1 + li % sw, scope.Y1 + li / sw);
        // Ocean only: rivers and lakes keep plain water against their banks.
        bool IsOcean(int li)
        {
            int g = G(li);
            if (bio is not null)
            {
                var id = (BiomeId)bio[g];
                if (id is BiomeId.DeepWater or BiomeId.ShallowWater) return true;
                if (p.DigNarrowInlets && id == BiomeId.River && z[g] == ir.OceanZ) return true;
                if (id != BiomeId.Unassigned) return false;
            }
            return TileFlags.IsWaterLandId(l[g]);
        }
        bool IsLandCell(int li)
        {
            int g = G(li);
            if (bio is not null && (BiomeId)bio[g] is BiomeId.River) return false;
            return !IsOcean(li) && !TileFlags.IsWaterLandId(l[g]);
        }

        // Felucca's plane: open water and statics at OceanZ, dug bottom at ShoreDigZ. The
        // legacy absolute params are only honoured when they are consistent with it.
        int staticZ = ir.OceanZ;
        int digZ = ir.ShoreDigZ;
        // Sand extension must stay ABOVE the water surface (else it is dry sand drawn under
        // the water statics) and not above sea level.
        int sandZ = Math.Clamp(ir.SeaLevelZ + (p.SandExtensionZ - 0), staticZ + 1, ir.SeaLevelZ);
        int sandExt = Math.Max(0, p.SandExtension);
        int band = Math.Max(0, p.DigBandWidth);
        int reach = sandExt + band;

        // dL: Chebyshev distance from land through ocean cells (0 on land).
        var dL = new int[n];
        var q = new Queue<int>();
        var ocean = new bool[n];
        for (int i = 0; i < n; i++)
        {
            ocean[i] = IsOcean(i);
            if (ocean[i]) dL[i] = int.MaxValue;
            else if (IsLandCell(i)) { dL[i] = 0; q.Enqueue(i); }
            else dL[i] = int.MaxValue; // river/lake: neither seed nor ocean
        }
        Bfs8(q, dL, ocean, sw, sh, int.MaxValue);

        // Openness: a channel narrower than 2*reach has no cell farther than `reach` from land.
        // Only shore within `reach` of such open water is dug/extended; narrow straits and
        // inlets stay plain water instead of being filled with sand.
        var dOpen = new int[n];
        for (int i = 0; i < n; i++)
        {
            if (ocean[i] && dL[i] > reach) { dOpen[i] = 0; q.Enqueue(i); }
            else dOpen[i] = int.MaxValue;
        }
        Bfs8(q, dOpen, ocean, sw, sh, reach + 1);

        var sandSet = new bool[n];
        var digRing = new int[n];
        int sandPainted = 0, ring1 = 0, ring2 = 0, ring3 = 0, statics = 0, narrowSkipped = 0;
        for (int i = 0; i < n; i++)
        {
            if (!ocean[i]) continue;
            int d = dL[i];
            if (d < 1 || d > reach) continue;
            if (dOpen[i] > reach && (!p.DigNarrowInlets || d <= sandExt)) { narrowSkipped++; continue; }
            int g = G(i);
            int gx = scope.X1 + i % sw, gy = scope.Y1 + i / sw;
            if (d <= sandExt)
            {
                l[g] = LatticePick.Pick(SandTiles, gx, gy, ir.Seed);
                z[g] = (sbyte)sandZ;
                if (bio is not null) bio[g] = (byte)BiomeId.Beach;
                sandSet[i] = true;
                sandPainted++;
                continue;
            }
            int ring = d - sandExt;
            digRing[i] = ring;
            ushort newLand;
            if (ring == 1)      { newLand = LatticePick.Pick(Ring1Tiles, gx, gy, ir.Seed); ring1++; }
            else if (ring == 2) { newLand = LatticePick.Pick(Ring2Tiles, gx, gy, ir.Seed); ring2++; }
            else                { newLand = p.PlainDeepBed ? (ushort)0x64 : LatticePick.Pick(Ring3Tiles, gx, gy, ir.Seed); ring3++; }
            l[g] = newLand;
            z[g] = (sbyte)digZ;
            ushort overlayId = LatticePick.Pick(OverlayStatics, gx, gy, ir.Seed ^ 0xC0A571DEUL);
            ir.StaticOps.Add(new StaticOp(StaticOpKind.Add, (ushort)gx, (ushort)gy, (sbyte)staticZ, overlayId, 0));
            statics++;
        }

        // Open water: plain 0xA8-0xAB at the ocean plane, no statics (Felucca has almost
        // none out there). OverlayOpenWater is a legacy opt-in.
        int oceanCells = 0, oceanStatics = 0;
        for (int i = 0; i < n; i++)
        {
            if (!ocean[i] || sandSet[i] || digRing[i] > 0) continue;
            int g = G(i);
            if (z[g] != staticZ) { z[g] = (sbyte)staticZ; oceanCells++; }
            if (p.OverlayOpenWater)
            {
                int gx = scope.X1 + i % sw, gy = scope.Y1 + i / sw;
                ushort overlayId = LatticePick.Pick(OverlayStatics, gx, gy, ir.Seed ^ 0xDEADCAFEUL);
                ir.StaticOps.Add(new StaticOp(StaticOpKind.Add, (ushort)gx, (ushort)gy, (sbyte)staticZ, overlayId, 0));
                oceanStatics++;
            }
        }

        // Rim: on the FIRST dig ring (water side, like Felucca), the mask is the set of land
        // neighbours seen from the water cell, flipped into the land-POV frame the
        // transition table was authored in. Only real land counts — deep water and
        // unvisited cells never set a bit.
        int rimZ = staticZ;
        int rimPlaced = 0;
        for (int i = 0; i < n; i++)
        {
            if (digRing[i] != 1) continue;
            int lx = i % sw, ly = i / sw;
            var landMask = CoastRimMatcher.Dir.None;
            foreach (var (dx, dy, bit) in CoastRimMatcher.Offsets)
            {
                int nx = lx + dx, ny = ly + dy;
                if ((uint)nx >= (uint)sw || (uint)ny >= (uint)sh) continue;
                int j = ny * sw + nx;
                if (sandSet[j] || (!ocean[j] && dL[j] == 0)) landMask |= bit;
            }
            if (landMask == CoastRimMatcher.Dir.None) continue;
            int gx = scope.X1 + lx, gy = scope.Y1 + ly;
            ushort tileId = CoastRimMatcher.Match(CoastRimMatcher.Flip(landMask), FieldOps.Hash(gx, gy, ir.Seed ^ 0x51DEUL));
            if (tileId == 0) continue;
            ir.StaticOps.Add(new StaticOp(StaticOpKind.Add, (ushort)gx, (ushort)gy, (sbyte)rimZ, tileId, 0));
            rimPlaced++;
        }

        ctx.Report.Notes.Add($"DigShore sandExt={sandExt}({sandPainted} at Z{sandZ}) band={band} digZ={digZ}: ring1={ring1} ring2={ring2} ring3={ring3}, dig statics={statics}, narrow-channel cells left as water={narrowSkipped}; open water Z={staticZ} cells={oceanCells} statics={oceanStatics}; rim={rimPlaced}");
        ctx.Report.StaticsAdded += statics + oceanStatics + rimPlaced;
    }

    // Multi-source 8-connected BFS through `passable` cells; dist holds seeds at their
    // value and int.MaxValue elsewhere. Stops expanding at `cap`.
    private static void Bfs8(Queue<int> q, int[] dist, bool[] passable, int w, int h, int cap)
    {
        while (q.Count > 0)
        {
            int c = q.Dequeue();
            int d = dist[c];
            if (d >= cap) continue;
            int cx = c % w, cy = c / w;
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                int nx = cx + dx, ny = cy + dy;
                if ((uint)nx >= (uint)w || (uint)ny >= (uint)h) continue;
                int j = ny * w + nx;
                if (!passable[j] || dist[j] <= d + 1) continue;
                dist[j] = d + 1;
                q.Enqueue(j);
            }
        }
    }
}
