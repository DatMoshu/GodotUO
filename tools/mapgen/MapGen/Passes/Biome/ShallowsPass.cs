using CentrED.MapGen.Data;
using CentrED.MapGen.IR;
using CentrED.MapGen.Noise;
using CentrED.MapGen.Pipeline;

namespace CentrED.MapGen.Passes.Biome;

public sealed class ShallowsParams
{
    [TunableDisplay("Shaped bed", Tooltip = "Shape the dug seabed as Felucca does: the ring next to the shore takes the light bed edge 0x4C-0x57 turned toward the deeper bed, the next ring the mid edge 0x58-0x63 turned toward the shore, the rest the flat bed 0x64.")]
    public bool ShapedBed { get; set; } = true;

    [TunableDisplay("Waterline sand", Tooltip = "Beach sand on the waterline with grass behind it takes Felucca's grass-fringed wet sand for its shape (0x21-0x28 straight, 0x1A-0x1C outward points, 0x1D-0x20 inward corners) and the grass behind turns plain; with more sand behind it, it ripples (0x1A-0x1C).")]
    public bool WaterlineSand { get; set; } = true;
}

/// <summary>
/// Felucca's shallows over the band Dig Shore digs: the seabed is land tiles at <see cref="GenIR.ShoreDigZ"/>,
/// 10 below the water statics, in three bands (light, mid, flat) whose edge tiles face the right way, with
/// Felucca's wet-sand edge on the dry side. Measured on the owner's Felucca: which side of each bed tile holds the
/// deeper bed or the shore. Runs last, after Land Transitions has drawn the beach this pass edges.
/// </summary>
public sealed class ShallowsPass : IGenerationPass
{
    public string Name => "Shallows";
    public string Category => "Biome";
    public IrFields Reads => IrFields.Biome | IrFields.LandId | IrFields.Height;
    public IrFields Writes => IrFields.LandId;
    public object CreateDefaultParams() => new ShallowsParams();

    // The light ring is named by where the deeper bed lies, the mid ring by where the shallower side lies,
    // in EdgeShapes' names (N is y-1, E is x+1).
    public static readonly IReadOnlyDictionary<string, ushort> LightRing = new Dictionary<string, ushort>
    {
        ["N"] = 0x52, ["E"] = 0x53, ["S"] = 0x50, ["W"] = 0x51,
        ["NE"] = 0x57, ["SE"] = 0x54, ["SW"] = 0x55, ["NW"] = 0x56,
        ["in_NE"] = 0x4F, ["in_SE"] = 0x4C, ["in_SW"] = 0x4D, ["in_NW"] = 0x4E,
    };
    public static readonly IReadOnlyDictionary<string, ushort> MidRing = new Dictionary<string, ushort>
    {
        ["N"] = 0x5C, ["E"] = 0x5D, ["S"] = 0x5E, ["W"] = 0x5F,
        ["NE"] = 0x59, ["SE"] = 0x5A, ["SW"] = 0x5B, ["NW"] = 0x58,
        ["in_NE"] = 0x61, ["in_SE"] = 0x62, ["in_SW"] = 0x63, ["in_NW"] = 0x60,
    };
    public const ushort FlatBed = 0x64;

    // Wet sand on the waterline, named by where the water lies (measured on the owner's Felucca, each tile
    // 92-98% one shape). Straight sides have two tiles, the first three times as common; outward points are
    // the rippled tiles (0x1C serves both SE and NW), inward corners their own four.
    public static readonly IReadOnlyDictionary<string, ushort[]> Waterline = new Dictionary<string, ushort[]>
    {
        ["N"] = new ushort[] { 0x24, 0x24, 0x24, 0x28 }, ["E"] = new ushort[] { 0x23, 0x23, 0x23, 0x27 },
        ["S"] = new ushort[] { 0x21, 0x21, 0x21, 0x25 }, ["W"] = new ushort[] { 0x22, 0x22, 0x22, 0x26 },
        ["NE"] = new ushort[] { 0x1A }, ["SW"] = new ushort[] { 0x1B }, ["SE"] = new ushort[] { 0x1C }, ["NW"] = new ushort[] { 0x1C },
        ["in_SE"] = new ushort[] { 0x1D }, ["in_SW"] = new ushort[] { 0x1E }, ["in_NE"] = new ushort[] { 0x1F }, ["in_NW"] = new ushort[] { 0x20 },
    };
    /// <summary>Rippled wet sand, for a waterline with more sand behind it: water north or east, south or west, around.</summary>
    public const ushort RippleNE = 0x1A, RippleSW = 0x1B, Ripple = 0x1C;

    /// <summary>A seabed land tile (light ring, mid ring or flat bed).</summary>
    public static bool IsBed(ushort id) => id is >= 0x4C and <= 0x64;

    private static readonly (int Dx, int Dy, byte Bit)[] Offsets =
    {
        (0, -1, EdgeShapes.N), (1, -1, EdgeShapes.NE), (1, 0, EdgeShapes.E), (1, 1, EdgeShapes.SE),
        (0, 1, EdgeShapes.S), (-1, 1, EdgeShapes.SW), (-1, 0, EdgeShapes.W), (-1, -1, EdgeShapes.NW),
    };

    public void Run(GenContext ctx, object parameters)
    {
        var p = (ShallowsParams)parameters; var ir = ctx.IR;
        if (ir.LandId is null || ir.Height_Z is null || ir.Biome is null) return;
        var l = ir.LandId; var z = ir.Height_Z; var bio = ir.Biome;
        int w = ir.Scope.Width, h = ir.Scope.Height, n = w * h;
        int G(int i) => ir.Index(ir.Scope.X1 + i % w, ir.Scope.Y1 + i / w);

        // 0 dry land, 1 dug bed, 2 other water (open sea, rivers, lakes).
        var kind = new byte[n];
        int beds = 0;
        for (int i = 0; i < n; i++)
        {
            int g = G(i);
            bool ocean = (BiomeId)bio[g] is BiomeId.DeepWater or BiomeId.ShallowWater;
            if (ocean && IsBed(l[g]) && z[g] == ir.ShoreDigZ) { kind[i] = 1; beds++; }
            else if (ocean || (BiomeId)bio[g] is BiomeId.River || TileFlags.IsWaterLandId(l[g])) kind[i] = 2;
        }
        if (beds == 0)
        {
            ctx.Report.Notes.Add("Shallows: no dug bed (Dig Shore off or no open-sea shore)");
            return;
        }

        // Ring: 8-connected distance from dry land through the bed (1 next to the shore).
        var ring = new int[n];
        var q = new Queue<int>();
        for (int i = 0; i < n; i++) if (kind[i] == 0) q.Enqueue(i);
        while (q.Count > 0)
        {
            int c = q.Dequeue(), d = kind[c] == 0 ? 0 : ring[c];
            if (d >= 2) continue;
            foreach (var (dx, dy, _) in Offsets)
            {
                int x = c % w + dx, y = c / w + dy;
                if ((uint)x >= (uint)w || (uint)y >= (uint)h) continue;
                int j = y * w + x;
                if (kind[j] != 1 || ring[j] != 0) continue;
                ring[j] = d + 1; q.Enqueue(j);
            }
        }

        byte Mask(int i, Func<int, bool> test)
        {
            int x = i % w, y = i / w; byte m = 0;
            foreach (var (dx, dy, bit) in Offsets)
            {
                int nx = x + dx, ny = y + dy;
                if ((uint)nx < (uint)w && (uint)ny < (uint)h && test(ny * w + nx)) m |= bit;
            }
            return m;
        }
        // Deeper than the light ring: bed beyond it, or open water. Shallower than the mid ring: the light ring or dry land.
        bool Deeper(int j) => (kind[j] == 1 && ring[j] != 1) || kind[j] == 2;
        bool Shallower(int j) => kind[j] == 0 || (kind[j] == 1 && ring[j] == 1);

        int light = 0, mid = 0, flat = 0, slivers = 0, waterline = 0;
        if (p.ShapedBed)
            for (int i = 0; i < n; i++)
            {
                if (kind[i] != 1) continue;
                ushort id = FlatBed;
                if (ring[i] is 1 or 2)
                {
                    byte m = Mask(i, ring[i] == 1 ? Deeper : Shallower);
                    var table = ring[i] == 1 ? LightRing : MidRing;
                    if (EdgeShapes.ShapeOf(m) is { } shape) { id = table[shape]; if (ring[i] == 1) light++; else mid++; }
                    else slivers++;   // a one-tile sliver (deeper bed on opposite sides) or no deeper side: flat bed
                }
                if (id == FlatBed) flat++;
                l[G(i)] = id;
            }

        int rippled = 0, plainGrass = 0, regrassEdged = 0;
        if (p.WaterlineSand)
        {
            // The waterline: beach sand touching the bed. Felucca's is one cell of wet sand with grass
            // straight behind it (the tile carries the grass fringe), so where only grass lies behind, the
            // cell takes the tile for its shape and the grass behind loses the sand edge it drew toward it.
            // Where more sand lies behind (a wide beach, a desert), the cell ripples instead.
            var beach = new HashSet<ushort>(ir.Tables.Beach);
            var line = new bool[n];
            for (int i = 0; i < n; i++)
                line[i] = kind[i] == 0 && beach.Contains(l[G(i)]) && Mask(i, j => kind[j] == 1) != 0;
            bool Sand(int j) => kind[j] == 0 && (BiomeId)bio[G(j)] is BiomeId.Beach or BiomeId.Desert;
            var fringed = new bool[n];
            const byte northEast = EdgeShapes.NW | EdgeShapes.N | EdgeShapes.NE | EdgeShapes.E | EdgeShapes.SE;
            const byte southWest = EdgeShapes.SE | EdgeShapes.S | EdgeShapes.SW | EdgeShapes.W | EdgeShapes.NW;
            for (int i = 0; i < n; i++)
            {
                if (!line[i]) continue;
                int g = G(i), gx = ir.Scope.X1 + i % w, gy = ir.Scope.Y1 + i / w;
                bool sandBehind = Mask(i, j => Sand(j) && !line[j]) != 0;
                if (!sandBehind && EdgeShapes.ShapeOf(Mask(i, j => kind[j] != 0)) is { } shape)
                {
                    var ids = Waterline[shape];
                    l[g] = ids[FieldOps.Hash(gx, gy, ir.Seed ^ 0x5A4DUL) % (uint)ids.Length];
                    fringed[i] = true;
                    waterline++;
                    continue;
                }
                byte m = Mask(i, j => kind[j] == 1);
                bool ne = (m & (EdgeShapes.N | EdgeShapes.NE | EdgeShapes.E)) != 0, sw = (m & (EdgeShapes.S | EdgeShapes.SW | EdgeShapes.W)) != 0;
                l[g] = ne && (m & ~northEast) == 0 ? RippleNE : sw && (m & ~southWest) == 0 ? RippleSW : Ripple;
                rippled++;
            }

            // Grass behind a fringed waterline: its Grassland>Beach edge tile is redrawn against the sand
            // that is left (none: plain grass).
            if (ir.Brushes.Lookup("Grassland", "Beach") is { } lut && ir.Tables.Land.TryGetValue(BiomeId.Grassland, out var grass) && grass.Length > 0)
            {
                var edgeIds = new HashSet<ushort>(lut.Where(e => e is not null).SelectMany(e => e!));
                for (int i = 0; i < n; i++)
                {
                    int g = G(i);
                    if (kind[i] != 0 || !edgeIds.Contains(l[g]) || Mask(i, j => fringed[j]) == 0) continue;
                    int gx = ir.Scope.X1 + i % w, gy = ir.Scope.Y1 + i / w;
                    byte m = Mask(i, j => Sand(j) && !fringed[j]);
                    if (m == 0) { l[g] = LatticePick.Pick(grass, gx, gy, ir.Seed); plainGrass++; }
                    else if (lut[m] is { Length: > 0 } c) { l[g] = c[FieldOps.Hash(gx, gy, ir.Seed ^ 0x5A4EUL) % (uint)c.Length]; regrassEdged++; }
                }
            }
        }

        ctx.Report.TilesTouched = light + mid + flat + waterline + rippled + plainGrass + regrassEdged;
        ctx.Report.Notes.Add($"Shallows: bed {beds} (light ring {light}, mid ring {mid}, flat {flat}, slivers {slivers}); waterline: fringed {waterline}, rippled {rippled}; grass behind: plain {plainGrass}, re-edged {regrassEdged}");
    }
}
