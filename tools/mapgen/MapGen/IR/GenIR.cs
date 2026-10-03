using CentrED.MapGen.Data;
using CentrED.Network;

namespace CentrED.MapGen.IR;

/// <summary>
/// In-memory intermediate representation built by the pipeline. Every pass reads/writes only
/// this object; <see cref="Commit.StandaloneIrWriter"/> is the sole bridge to CentrED's world data.
/// Dense fields are flat arrays indexed via <see cref="Index"/> = y*Width + x. For Felucca
/// (7168x4096) the full set of dense fields is ~205 MB — fields are lazily allocated.
/// </summary>
public sealed class GenIR
{
    public ushort Width { get; }
    public ushort Height { get; }
    public RectU16 Scope { get; set; }

    // Dense per-tile fields. Allocated lazily via EnsureField(...) so a pipeline that
    // never assigns Moisture (e.g. height-only test) doesn't pay 28 MB.
    public sbyte[]? Height_Z;
    public byte[]? Moisture;
    public byte[]? Temperature;
    public byte[]? Slope;
    public byte[]? Biome;     // BiomeId cast to byte
    public ushort[]? LandId;
    public byte[]? DungeonZone; // DungeonZone enum cast to byte; set by RoomsCarvePass

    // Sparse layers
    public List<StaticOp> StaticOps { get; } = new();
    public List<RiverSegment> Rivers { get; } = new();
    public List<RoadSegment> Roads { get; } = new();
    public List<PoiStamp> Pois { get; } = new();
    public List<DungeonRoomRect> DungeonRooms { get; } = new();

    // Bookkeeping
    public Dictionary<string, PassReport> Reports { get; } = new();
    public ulong Seed { get; set; }
    public int PassVersion { get; set; }
    public TileTables Tables { get; set; } = TileTables.Default;
    public TreeStatics Trees { get; set; } = TreeStatics.Empty;
    public LandBrushTable Brushes { get; set; } = LandBrushTable.Empty;

    /// <summary>Shared stamp/scatter occupancy + land-dirty mask; allocated on first use by Stamps.OccupancyGrid.For(ir).</summary>
    public Stamps.OccupancyGrid? Occupancy { get; set; }

    // ---------------------------------------------------------------------
    // Sea level — the ONE source of truth for every pass after Biome Assign.
    // Biome Assign classifies water with its own SeaLevelZ (the terrain-domain threshold a
    // preset sets), then rebases the heightfield so that threshold becomes this value. Every
    // later pass (coast, dig shore, rivers, roads, validator) derives its water Zs from here
    // instead of carrying its own copy, so the Felucca layout below always holds:
    //   land shoreline ≈ SeaLevelZ, open water land tiles + water statics at OceanZ,
    //   dug shore bottom at ShoreDigZ.
    // ---------------------------------------------------------------------

    /// <summary>Sea level in the IR's current Z frame (0 after Biome Assign's rebase).</summary>
    public int SeaLevelZ { get; set; } = 0;

    /// <summary>Felucca's open-water plane relative to sea level (water land tiles and water statics).</summary>
    public const int OceanDepth = 5;

    /// <summary>Felucca's dug shore bottom relative to sea level.</summary>
    public const int ShoreDigDepth = 15;

    /// <summary>Z of every open-ocean water land tile and of the water-surface statics.</summary>
    public int OceanZ => SeaLevelZ - OceanDepth;

    /// <summary>Z of the brown dug-shore bottom under the water statics.</summary>
    public int ShoreDigZ => SeaLevelZ - ShoreDigDepth;

    public GenIR(ushort width, ushort height, RectU16 scope, ulong seed)
    {
        Width = width;
        Height = height;
        Scope = scope;
        Seed = seed;
    }

    /// <summary>Total tiles in the IR (Width × Height).</summary>
    public int TileCount => Width * Height;

    /// <summary>Flat-array index for tile (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public int Index(int x, int y) => y * Width + x;

    /// <summary>Lazily allocates <see cref="Height_Z"/> if not already present.</summary>
    public void EnsureHeight() => Height_Z ??= new sbyte[TileCount];
    /// <summary>Lazily allocates <see cref="Moisture"/> if not already present.</summary>
    public void EnsureMoisture() => Moisture ??= new byte[TileCount];
    /// <summary>Lazily allocates <see cref="Temperature"/> if not already present.</summary>
    public void EnsureTemperature() => Temperature ??= new byte[TileCount];
    /// <summary>Lazily allocates <see cref="Slope"/> if not already present.</summary>
    public void EnsureSlope() => Slope ??= new byte[TileCount];
    /// <summary>Lazily allocates <see cref="Biome"/> if not already present.</summary>
    public void EnsureBiome() => Biome ??= new byte[TileCount];
    /// <summary>Lazily allocates <see cref="LandId"/> if not already present.</summary>
    public void EnsureLandId() => LandId ??= new ushort[TileCount];
    /// <summary>Lazily allocates <see cref="DungeonZone"/> if not already present.</summary>
    public void EnsureDungeonZone() => DungeonZone ??= new byte[TileCount];

    /// <summary>Allocates every dense field flagged in <paramref name="field"/>. Called by the runner before each pass so passes don't repeat EnsureX().</summary>
    public void EnsureField(IrFields field)
    {
        if ((field & IrFields.Height) != 0) EnsureHeight();
        if ((field & IrFields.Moisture) != 0) EnsureMoisture();
        if ((field & IrFields.Temperature) != 0) EnsureTemperature();
        if ((field & IrFields.Slope) != 0) EnsureSlope();
        if ((field & IrFields.Biome) != 0) EnsureBiome();
        if ((field & IrFields.LandId) != 0) EnsureLandId();
        if ((field & IrFields.DungeonZone) != 0) EnsureDungeonZone();
    }
}
