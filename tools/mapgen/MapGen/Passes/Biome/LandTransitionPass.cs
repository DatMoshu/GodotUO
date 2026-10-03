using CentrED.MapGen.Data;
using CentrED.MapGen.IR;
using CentrED.MapGen.Noise;
using CentrED.MapGen.Pipeline;

namespace CentrED.MapGen.Passes.Biome;

public sealed class LandTransitionParams
{
    [TunableDisplay("Grass mud banks", Tooltip = "Use Dragon grass2water-dark for grass coasts, matching its terrain-pair configuration.")]
    public bool GrassMudBanks { get; set; } = false;
    [TunableDisplay("Measured transition atlas", Tooltip = "Optional local exact-mask frequencies mined by transition_atlas.py. Missing masks retain the Dragon rules.")]
    public string ReferenceAtlasPath { get; set; } = "";
    [TunableDisplay("Minimum transition samples")]
    [TunableRange(1, 1000)]
    public int ReferenceMinimumSupport { get; set; } = 30;

    // Legacy distance-band knobs from the old Pass B fallback. The brush engine paints
    // exactly one edge tile per boundary cell, the way Felucca does, so these are ignored.
    [TunableDisplay("Beach band width (legacy, ignored)")]
    [TunableRange(0, 8)]
    public int SandBand { get; set; } = 2;

    [TunableDisplay("Dirt band width (legacy, ignored)")]
    [TunableRange(0, 8)]
    public int DirtBand { get; set; } = 0;

    [TunableDisplay("Reserved")] [TunableRange(0, 1)]
    public int Reserved { get; set; } = 0;

    // Legacy: replaced by Sliver passes below (slivers are absorbed, not skipped).
    [TunableDisplay("Min source band (legacy, ignored)")]
    [TunableRange(0, 8)]
    public int MinSourceBand { get; set; } = 0;

    // Was a hard assert that threw out of the pipeline. Now: when true, interior tiles left
    // on a boundary are reported as a warning (never an exception).
    [TunableDisplay("Warn on hard edges", Tooltip = "Report boundary cells that kept a plain interior tile (no transition tile fits). Never throws.")]
    [TunableRange(0, 1)]
    public bool StrictNoGrassSurvivors { get; set; } = true;

    // When the owner cell sits this many Z above the water it faces, the grass->water
    // brush (sand bank with grass top) is used even for sand/forest owners: it reads as a
    // low bluff instead of a flat beach. 0 disables.
    [TunableDisplay("Cliff dz threshold", Tooltip = "Owner cells this far above the water they face use the Grassland->Water bank tiles. 0 = disabled.")]
    [TunableRange(0, 16)]
    public int CliffDzThreshold { get; set; } = 6;

    // Relative flatten: an edge cell between two land biomes is pulled halfway toward the
    // mean Z of the neighbours it blends into (no more absolute Z=0 clamp, which flattened
    // every grass/sand fringe on a hillside into a ditch).
    [TunableDisplay("Flatten gentle transition", Tooltip = "Pull land-land edge cells halfway toward the Z of the biome they blend into.")]
    public bool FlattenGentleTransition { get; set; } = true;

    [TunableDisplay("Sliver passes", Tooltip = "Boundary cells no transition tile can render (1-wide strips, checkerboards) adopt the neighbouring biome; repeated up to N times.")]
    [TunableRange(0, 6)]
    public int SliverPasses { get; set; } = 3;
}

// One generic transition engine driven by the land brush table (DragonMod rules).
//
//  1. Every land cell is classified by BIOME into a class (Grass, Forest, Jungle, Sand,
//     Snow, Mountain, Swamp, Water). LandIds are never used to decide what a cell is, so
//     jungle (0xAC-0xAF) is no longer mistaken for grass and shredded.
//  2. Each unordered class pair has ONE owner side (the brush that holds the
//     transition). Only owner cells are repainted; the other side keeps its interior
//     tile. Pairs not on the allow-list stay hard edges (counted and reported).
//  3. Masks come from the class grid (a snapshot), never from tiles painted in the same
//     pass, so the result is order independent.
//  4. The tile is the brush's minimal superset entry for the 8-neighbour mask, chosen
//     among equals by a per-cell hash (deterministic).
//  5. Owner cells whose mask has no tile (slivers) adopt the other class and the masks
//     are rebuilt, a few times, before painting.
public sealed class LandTransitionPass : IGenerationPass
{
    public string Name => "Land Transitions";
    public string Category => "Biome";

    public IrFields Reads => IrFields.LandId | IrFields.Height | IrFields.Biome;
    public IrFields Writes => IrFields.LandId | IrFields.Height;

    public object CreateDefaultParams() => new LandTransitionParams();

    internal enum Cls : byte { None = 0, Grass, Forest, Jungle, Sand, Snow, Mountain, Swamp, Water }

    private static readonly string?[] BrushName =
    {
        null, "Grassland", "Forest", "Jungle", "Beach", "Snow", "Mountain", "Swamp", "Water",
    };

    // (owner, other): the owner side paints. The allow-list of pairs that get transitions.
    private static readonly (Cls Owner, Cls Other)[] Pairs =
    {
        (Cls.Grass, Cls.Sand), (Cls.Grass, Cls.Mountain), (Cls.Grass, Cls.Forest), (Cls.Grass, Cls.Jungle),
        (Cls.Grass, Cls.Snow), (Cls.Grass, Cls.Swamp), (Cls.Grass, Cls.Water),
        (Cls.Forest, Cls.Sand), (Cls.Forest, Cls.Mountain), (Cls.Forest, Cls.Jungle), (Cls.Forest, Cls.Swamp), (Cls.Forest, Cls.Water),
        (Cls.Sand, Cls.Water), (Cls.Sand, Cls.Jungle), (Cls.Sand, Cls.Mountain),
        (Cls.Snow, Cls.Mountain), (Cls.Snow, Cls.Water),
        (Cls.Jungle, Cls.Water),
    };

    // When a cell faces several other classes, the one with most neighbours wins; ties go
    // to the earlier entry here.
    private static readonly Cls[] Priority = { Cls.Water, Cls.Sand, Cls.Mountain, Cls.Snow, Cls.Swamp, Cls.Jungle, Cls.Forest, Cls.Grass };

    internal static Cls ClassOf(BiomeId b) => b switch
    {
        BiomeId.Grassland or BiomeId.Savanna => Cls.Grass,
        BiomeId.Forest or BiomeId.DenseForest => Cls.Forest,
        BiomeId.Jungle => Cls.Jungle,
        BiomeId.Beach or BiomeId.Desert => Cls.Sand,
        BiomeId.Snow or BiomeId.Tundra => Cls.Snow,
        BiomeId.Mountain or BiomeId.HighMountain => Cls.Mountain,
        BiomeId.Swamp or BiomeId.Wetland => Cls.Swamp,
        BiomeId.DeepWater or BiomeId.ShallowWater or BiomeId.River => Cls.Water,
        _ => Cls.None,
    };

    private static BiomeId RepresentativeBiome(Cls c) => c switch
    {
        Cls.Grass => BiomeId.Grassland,
        Cls.Forest => BiomeId.Forest,
        Cls.Jungle => BiomeId.Jungle,
        Cls.Sand => BiomeId.Beach,
        Cls.Snow => BiomeId.Snow,
        Cls.Mountain => BiomeId.Mountain,
        Cls.Swamp => BiomeId.Swamp,
        Cls.Water => BiomeId.ShallowWater,
        _ => BiomeId.Unassigned,
    };

    // A land cell must never be painted with a pure water tile: the DragonMod tables map
    // near-full water masks to 0xAA (CentrED turns such a cell into water).
    private static bool Renders(ushort[]? cands, Cls other) =>
        cands is { Length: > 0 } && (other != Cls.Water || cands.Any(id => !TileFlags.IsWaterLandId(id)));

    // 8-direction offsets: N, NE, E, SE, S, SW, W, NW = bits 0..7 (brush Direction order).
    private static readonly (int dx, int dy, byte bit)[] DirOffsets =
    {
        (0, -1, 1 << 0), (1, -1, 1 << 1), (1, 0, 1 << 2), (1, 1, 1 << 3),
        (0,  1, 1 << 4), (-1, 1, 1 << 5), (-1, 0, 1 << 6), (-1, -1, 1 << 7),
    };

    public void Run(GenContext ctx, object parameters)
        => Core(ctx.IR, (LandTransitionParams)parameters, null, ctx.Report);

    /// <summary>
    /// Re-blends the land transitions on <paramref name="mask"/> (TileCount long, true where a
    /// stamp repainted land after this pass ran) and a one-tile ring around it. Masked tiles
    /// whose id is an interior tile of a biome take that biome; dirt and cobble become Road
    /// (their neighbours get the brush road edges); any other stamp tile is kept as drawn.
    /// Nothing outside the ring changes. Returns the number of tiles repainted.
    /// Called by MapGen.Stamps.StampSeams after every stamp pass.
    /// </summary>
    public static int RunOnMask(GenIR ir, bool[] mask, LandTransitionParams? parameters = null)
    {
        if (ir.LandId is null || ir.Biome is null || mask.Length != ir.TileCount) return 0;
        var report = new PassReport { PassName = "Land Transitions (stamp seams)" };
        Core(ir, parameters ?? new LandTransitionParams(), mask, report);
        return report.TilesTouched;
    }

    private static void Core(GenIR ir, LandTransitionParams p, bool[]? dirty, PassReport report)
    {
        if (ir.LandId is null || ir.Biome is null) return;
        var l = ir.LandId;
        var b = ir.Biome;
        var z = ir.Height_Z;
        var scope = ir.Scope;
        int sw = scope.Width, sh = scope.Height;
        int n = sw * sh;
        var brushes = ir.Brushes;

        // Owner lookup per ordered pair; lut per pair (null = no tiles for that pair).
        var owner = new bool[9, 9];
        var lut = new ushort[]?[]?[9, 9];
        var baseLut = new ushort[]?[]?[9, 9];
        int pairsLoaded = 0;
        MeasuredTransitionTable? measured = null;
        if (!string.IsNullOrWhiteSpace(p.ReferenceAtlasPath))
        {
            string atlasPath = RepoRootResolver.Resolve(p.ReferenceAtlasPath);
            if (File.Exists(atlasPath))
            {
                measured = MeasuredTransitionTable.Load(atlasPath, p.ReferenceMinimumSupport);
                report.Notes.Add($"measured transition masks={measured.MaskCount}");
            }
            else report.Warnings.Add($"Measured transition atlas missing: {p.ReferenceAtlasPath}; using Dragon rules");
        }
        foreach (var (o, t) in Pairs)
        {
            owner[(int)o, (int)t] = true;
            var table = brushes.IsLoaded ? brushes.Lookup(BrushName[(int)o]!, BrushName[(int)t]!) : null;
            if (p.GrassMudBanks && o == Cls.Grass && t == Cls.Water && brushes.IsLoaded)
                table = brushes.Lookup("Grassland", "WaterDeep") ?? table;
            if (o == Cls.Grass && t == Cls.Swamp && table is null) table = SwampFallback;
            baseLut[(int)o, (int)t] = table;
            if (measured is not null) table = measured.Overlay(BrushName[(int)o]!, BrushName[(int)t]!, table);
            lut[(int)o, (int)t] = table;
            if (table is not null) pairsLoaded++;
        }
        if (pairsLoaded == 0)
        {
            report.Warnings.Add("Land Transitions: no brush table loaded (landbrush.dragon.json) — boundaries stay hard edges.");
            return;
        }

        int G(int li) => ir.Index(scope.X1 + li % sw, scope.Y1 + li / sw);

        bool FlatQuad(int li)
        {
            if (z is null) return false;
            int x = scope.X1 + li % sw, y = scope.Y1 + li / sw;
            if (x + 1 >= ir.Width || y + 1 >= ir.Height) return false;
            int height = z[ir.Index(x, y)];
            return height == z[ir.Index(x + 1, y)] && height == z[ir.Index(x, y + 1)] && height == z[ir.Index(x + 1, y + 1)];
        }

        // Stamp seams: the region is the dirty mask plus a one-tile ring. Dirty tiles are
        // re-classified from their land id; tiles the tables do not know stay as drawn.
        bool[]? region = null;
        var fixedCell = new bool[n];
        var dirtRoad = new HashSet<int>();
        var cobbleRoad = new HashSet<int>();
        if (dirty is not null)
        {
            region = new bool[ir.TileCount];
            var byId = new Dictionary<ushort, BiomeId>();
            foreach (var (bio, set) in ir.Tables.Land)
                if (ClassOf(bio) != Cls.None)
                    foreach (var id in set) byId.TryAdd(id, bio);
            for (int i = 0; i < n; i++)
            {
                int g = G(i);
                if (!dirty[g]) continue;
                int lx = i % sw, ly = i / sw;
                foreach (var (dx, dy, _) in DirOffsets)
                {
                    int nx = lx + dx, ny = ly + dy;
                    if ((uint)nx < (uint)sw && (uint)ny < (uint)sh) region[G(ny * sw + nx)] = true;
                }
                region[g] = true;
                ushort id = l[g];
                if (Array.IndexOf(Network.RoadPaint.DirtTiles, id) >= 0) { b[g] = (byte)BiomeId.Road; dirtRoad.Add(g); }
                else if (Array.IndexOf(Network.RoadPaint.CobbleTiles, id) >= 0) { b[g] = (byte)BiomeId.Road; cobbleRoad.Add(g); }
                else if (byId.TryGetValue(id, out var bio)) { if (ClassOf(bio) != ClassOf((BiomeId)b[g])) b[g] = (byte)bio; }
                else if (!TileFlags.IsWaterLandId(id)) fixedCell[i] = true;
            }
        }
        bool InRegion(int li) => region is null || region[G(li)];

        // Class snapshot (scope-local).
        var cls = new Cls[n];
        for (int i = 0; i < n; i++) cls[i] = fixedCell[i] ? Cls.None : ClassOf((BiomeId)b[G(i)]);

        // --- bridging -----------------------------------------------------------------
        // Swamp only has brushes against grass and forest: a swamp cell touching sand,
        // water, rock, snow or jungle becomes grass, whose ring blends into both sides
        // (Felucca's swamps sit in grass too). Rock has no brush against water: a mountain
        // cell on a river or the coast becomes sand (sand blends into rock and water).
        int bridged = 0;
        {
            var next = (Cls[])cls.Clone();
            for (int i = 0; i < n; i++)
            {
                var self = cls[i];
                if (self is not (Cls.Swamp or Cls.Mountain) || !InRegion(i)) continue;
                var to = self == Cls.Swamp ? Cls.Grass : Cls.Sand;
                int lx = i % sw, ly = i / sw;
                foreach (var (dx, dy, _) in DirOffsets)
                {
                    int nx = lx + dx, ny = ly + dy;
                    if ((uint)nx >= (uint)sw || (uint)ny >= (uint)sh) continue;
                    var c = cls[ny * sw + nx];
                    if (c == Cls.None || c == self) continue;
                    if (self == Cls.Mountain ? c != Cls.Water : owner[(int)self, (int)c] || owner[(int)c, (int)self]) continue;
                    next[i] = to;
                    break;
                }
                if (next[i] != to) continue;
                int g = G(i);
                var bio = to == Cls.Grass ? BiomeId.Grassland : BiomeId.Beach;
                b[g] = (byte)bio;
                if (ir.Tables.Land.TryGetValue(bio, out var set) && set.Length > 0)
                    l[g] = LatticePick.Pick(set, scope.X1 + lx, scope.Y1 + ly, ir.Seed);
                bridged++;
            }
            cls = next;
        }

        // --- sliver absorption -------------------------------------------------------
        int absorbed = 0;
        var other = new Cls[n];
        var mask = new byte[n];
        for (int pass = 0; pass <= p.SliverPasses; pass++)
        {
            ComputeMasks(cls, sw, sh, owner, other, mask);
            if (pass == p.SliverPasses) break;
            var next = (Cls[])cls.Clone();
            int changed = 0;
            for (int i = 0; i < n; i++)
            {
                var o = other[i];
                if (o == Cls.None || !InRegion(i)) continue;
                var table = (measured is null || FlatQuad(i) ? lut : baseLut)[(int)cls[i], (int)o];
                if (table is null || Renders(table[mask[i]], o)) continue;
                // No tile renders this neighbourhood (or only a pure water tile does, as for a
                // sand speck with 7+ water neighbours): the cell joins the other class.
                next[i] = o;
                changed++;
            }
            if (changed == 0) break;
            for (int i = 0; i < n; i++)
            {
                if (next[i] == cls[i]) continue;
                int g = G(i);
                var nb = RepresentativeBiome(next[i]);
                // Keep the neighbour's exact biome when one is adjacent (Desert stays Desert).
                int lx = i % sw, ly = i / sw;
                foreach (var (dx, dy, _) in DirOffsets)
                {
                    int nx = lx + dx, ny = ly + dy;
                    if ((uint)nx >= (uint)sw || (uint)ny >= (uint)sh) continue;
                    int j = ny * sw + nx;
                    if (cls[j] == next[i]) { nb = (BiomeId)b[G(j)]; break; }
                }
                b[g] = (byte)nb;
                if (ir.Tables.Land.TryGetValue(nb, out var set) && set.Length > 0)
                    l[g] = LatticePick.Pick(set, scope.X1 + lx, scope.Y1 + ly, ir.Seed);
                if (next[i] == Cls.Water && z is not null) z[g] = (sbyte)ir.OceanZ;
                else if (cls[i] == Cls.Water && z is not null) z[g] = (sbyte)Math.Max(z[g], ir.OceanZ + 1);
            }
            cls = next;
            absorbed += changed;
        }

        // Seam ring: a tile that faced a class the stamp has since covered keeps a stale
        // edge tile; reset it to its interior before painting (road edges are redone below).
        int reset = 0;
        if (region is not null)
        {
            for (int i = 0; i < n; i++)
            {
                int g = G(i);
                if (!region[g] || dirty![g] || other[i] != Cls.None || cls[i] is Cls.None or Cls.Water) continue;
                var bio = (BiomeId)b[g];
                if (!ir.Tables.Land.TryGetValue(bio, out var set) || set.Length == 0 || Array.IndexOf(set, l[g]) >= 0) continue;
                bool nearRoad = false;
                int lx = i % sw, ly = i / sw;
                foreach (var (dx, dy, _) in DirOffsets)
                {
                    int nx = lx + dx, ny = ly + dy;
                    if ((uint)nx < (uint)sw && (uint)ny < (uint)sh && (BiomeId)b[G(ny * sw + nx)] == BiomeId.Road) { nearRoad = true; break; }
                }
                if (nearRoad) continue;
                l[g] = LatticePick.Pick(set, scope.X1 + lx, scope.Y1 + ly, ir.Seed);
                reset++;
            }
        }

        // --- paint ---------------------------------------------------------------------
        int painted = 0, hardEdges = 0, bluffs = 0, flattened = 0;
        var pairCounts = new Dictionary<(Cls, Cls), int>();
        var bankLut = brushes.IsLoaded ? brushes.Lookup("Grassland", "Water") : null;
        var newZ = z is null ? null : (sbyte[])z.Clone();
        for (int i = 0; i < n; i++)
        {
            var o = other[i];
            if (o == Cls.None || !InRegion(i)) continue;
            int lx = i % sw, ly = i / sw;
            int gx = scope.X1 + lx, gy = scope.Y1 + ly;
            int g = ir.Index(gx, gy);
            // The measured atlas contains flat quads only. Slopes keep the
            // original brush rules instead of extrapolating that evidence.
            var table = (measured is null || FlatQuad(i) ? lut : baseLut)[(int)cls[i], (int)o];
            uint hash = FieldOps.Hash(gx, gy, ir.Seed ^ 0x7A11_5EEDUL);

            // High owner cell facing water: use the grass-topped bank tiles.
            if (o == Cls.Water && cls[i] != Cls.Grass && bankLut is not null && p.CliffDzThreshold > 0 && z is not null)
            {
                int minWaterZ = int.MaxValue;
                foreach (var (dx, dy, bit) in DirOffsets)
                    if ((mask[i] & bit) != 0)
                    {
                        int nx = gx + dx, ny = gy + dy;
                        minWaterZ = Math.Min(minWaterZ, z[ir.Index(nx, ny)]);
                    }
                if (minWaterZ != int.MaxValue && z[g] - minWaterZ >= p.CliffDzThreshold + GenIR.OceanDepth
                    && bankLut[mask[i]] is { Length: > 0 } bankAll
                    && bankAll.Where(id => !TileFlags.IsWaterLandId(id)).ToArray() is { Length: > 0 } bank)
                {
                    l[g] = bank[hash % (uint)bank.Length];
                    painted++; bluffs++;
                    continue;
                }
            }

            var cands = table?[mask[i]];
            if (o == Cls.Water && cands is { Length: > 0 } && cands.Any(TileFlags.IsWaterLandId))
                cands = cands.Where(id => !TileFlags.IsWaterLandId(id)).ToArray();
            if (cands is not { Length: > 0 })
            {
                hardEdges++;
                continue;
            }
            l[g] = cands[hash % (uint)cands.Length];
            painted++;
            pairCounts[(cls[i], o)] = pairCounts.GetValueOrDefault((cls[i], o)) + 1;

            // Relative flatten for land-land edges.
            if (p.FlattenGentleTransition && newZ is not null && o != Cls.Water && (dirty is null || !dirty[g]))
            {
                int sum = 0, cnt = 0;
                foreach (var (dx, dy, bit) in DirOffsets)
                {
                    if ((mask[i] & bit) == 0) continue;
                    sum += z![ir.Index(gx + dx, gy + dy)];
                    cnt++;
                }
                if (cnt > 0)
                {
                    int target = (int)Math.Round((z![g] + (double)sum / cnt) / 2.0);
                    if (o == Cls.Mountain) target = Math.Min(target, z[g]); // never raise toward a peak
                    if (target != z[g]) { newZ[g] = (sbyte)target; flattened++; }
                }
            }
        }
        if (newZ is not null) Array.Copy(newZ, z!, newZ.Length);

        int roadEdges = 0;
        if (dirtRoad.Count > 0) roadEdges += Network.RoadPaint.PaintEdges(ir, dirtRoad, "Dirt", skipMountain: false);
        if (cobbleRoad.Count > 0) roadEdges += Network.RoadPaint.PaintEdges(ir, cobbleRoad, "Cobble", skipMountain: false);

        // Remaining hard edges between DIFFERENT classes that have no allow-listed pair.
        int unpaired = 0;
        for (int i = 0; i < n; i++)
        {
            var c = cls[i];
            if (c == Cls.None || c == Cls.Water) continue;
            int lx = i % sw, ly = i / sw;
            if (lx + 1 < sw) unpaired += IsUnpaired(c, cls[i + 1]) ? 1 : 0;
            if (ly + 1 < sh) unpaired += IsUnpaired(c, cls[i + sw]) ? 1 : 0;
        }

        report.TilesTouched = painted + roadEdges + reset;
        report.Notes.Add($"transitions painted={painted} (bluff banks={bluffs}), slivers absorbed={absorbed}, bridged (swamp->grass, rock->sand at water)={bridged}, z-flattened={flattened}, pairs: "
            + string.Join(", ", pairCounts.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key.Item1}>{kv.Key.Item2}={kv.Value}")));
        if (p.StrictNoGrassSurvivors && (hardEdges > 0 || unpaired > 0))
            report.Warnings.Add($"Land Transitions: {hardEdges} boundary cells kept an interior tile (no brush tile for their mask); {unpaired} edges between biome classes with no transition pair.");

        bool IsUnpaired(Cls a, Cls c2)
        {
            if (c2 == a || c2 == Cls.None) return false;
            if (c2 == Cls.Water && a == Cls.Mountain) return true;
            return !owner[(int)a, (int)c2] && !owner[(int)c2, (int)a];
        }
    }

    // For each cell: the other class it should blend into (None when it is not an owner of
    // any neighbouring class) and the 8-bit mask of neighbours in that class.
    private static void ComputeMasks(Cls[] cls, int sw, int sh, bool[,] owner, Cls[] other, byte[] mask)
    {
        Span<int> count = stackalloc int[9];
        Span<byte> m = stackalloc byte[9];
        for (int i = 0; i < cls.Length; i++)
        {
            other[i] = Cls.None;
            mask[i] = 0;
            var self = cls[i];
            if (self == Cls.None || self == Cls.Water) continue;
            count.Clear(); m.Clear();
            int lx = i % sw, ly = i / sw;
            foreach (var (dx, dy, bit) in DirOffsets)
            {
                int nx = lx + dx, ny = ly + dy;
                // Off-scope neighbours count as the same class (no edge at the scope border).
                if ((uint)nx >= (uint)sw || (uint)ny >= (uint)sh) continue;
                var c = cls[ny * sw + nx];
                if (c == self || c == Cls.None) continue;
                if (!owner[(int)self, (int)c]) continue;
                count[(int)c]++;
                m[(int)c] |= bit;
            }
            Cls best = Cls.None;
            int bestN = 0;
            foreach (var c in Priority)
                if (count[(int)c] > bestN) { bestN = count[(int)c]; best = c; }
            if (best == Cls.None) continue;
            other[i] = best;
            byte mk = m[(int)best];
            // Sand and water read as one shore for grass/forest owners: the tile faces both.
            if (best == Cls.Sand && owner[(int)self, (int)Cls.Water]) mk |= m[(int)Cls.Water];
            mask[i] = mk;
        }
    }

    // Grass->Swamp tiles (moss family 0x3DD5-0x3DE6) for tables that lack the pair. Built
    // from the same edge/corner semantics as the brush: mask bits = swamp neighbours.
    private static readonly ushort[]?[] SwampFallback = BuildSwampFallback();

    private static ushort[]?[] BuildSwampFallback()
    {
        // (Direction mask, tile) in brush convention, N=1 NE=2 E=4 SE=8 S=16 SW=32 W=64 NW=128.
        var entries = new (int Dir, ushort Tile)[]
        {
            (0x83, 0x3DE1), (0x0E, 0x3DDE), (0x38, 0x3DDF), (0xE0, 0x3DDB),   // edges N, E, S, W
            (0x80, 0x3DD8), (0x02, 0x3DD7), (0x20, 0x3DD5), (0x08, 0x3DD6),   // outer corners
            (0x7F, 0x3DE5), (0xFD, 0x3DE4), (0xDF, 0x3DE3), (0xF7, 0x3DE6),   // inner corners (all but one diagonal)
        };
        var lut = new ushort[]?[256];
        for (int mask = 1; mask < 256; mask++)
        {
            int best = int.MaxValue;
            var ids = new List<ushort>();
            foreach (var (dir, tile) in entries)
            {
                if ((dir & mask) != mask) continue;
                int pc = System.Numerics.BitOperations.PopCount((uint)dir);
                if (pc < best) { best = pc; ids.Clear(); }
                if (pc == best) ids.Add(tile);
            }
            lut[mask] = ids.Count > 0 ? ids.ToArray() : null;
        }
        return lut;
    }
}
