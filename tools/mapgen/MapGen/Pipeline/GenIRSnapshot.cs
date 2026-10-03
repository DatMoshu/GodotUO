using CentrED.MapGen.IR;

namespace CentrED.MapGen.Pipeline;

/// <summary>
/// Copy of a <see cref="GenIR"/>'s mutable state, taken before a pass runs so a failed or
/// unwanted step can be rolled back (Pass Stepper "retry"). Dense arrays are copied; sparse
/// layers are copied as lists (their elements are records or treated as immutable once added).
/// Data tables (Tables/Trees/Brushes) are shared, never mutated by passes.
/// </summary>
public sealed class GenIRSnapshot
{
    private readonly sbyte[]? _height;
    private readonly byte[]? _moisture, _temperature, _slope, _biome, _dungeonZone;
    private readonly ushort[]? _landId;
    private readonly StaticOp[] _staticOps;
    private readonly RiverSegment[] _rivers;
    private readonly RoadSegment[] _roads;
    private readonly PoiStamp[] _pois;
    private readonly DungeonRoomRect[] _rooms;
    private readonly KeyValuePair<string, PassReport>[] _reports;
    private readonly int _passVersion;

    private GenIRSnapshot(GenIR ir)
    {
        _height = (sbyte[]?)ir.Height_Z?.Clone();
        _moisture = (byte[]?)ir.Moisture?.Clone();
        _temperature = (byte[]?)ir.Temperature?.Clone();
        _slope = (byte[]?)ir.Slope?.Clone();
        _biome = (byte[]?)ir.Biome?.Clone();
        _landId = (ushort[]?)ir.LandId?.Clone();
        _dungeonZone = (byte[]?)ir.DungeonZone?.Clone();
        _staticOps = ir.StaticOps.ToArray();
        _rivers = ir.Rivers.ToArray();
        _roads = ir.Roads.ToArray();
        _pois = ir.Pois.ToArray();
        _rooms = ir.DungeonRooms.ToArray();
        _reports = ir.Reports.ToArray();
        _passVersion = ir.PassVersion;
    }

    public static GenIRSnapshot Capture(GenIR ir) => new(ir);

    /// <summary>Approximate bytes held by the snapshot's dense arrays (for UI display).</summary>
    public long DenseBytes =>
        (long)(_height?.Length ?? 0) + (_moisture?.Length ?? 0) + (_temperature?.Length ?? 0)
        + (_slope?.Length ?? 0) + (_biome?.Length ?? 0) + 2L * (_landId?.Length ?? 0) + (_dungeonZone?.Length ?? 0);

    /// <summary>Writes the captured state back into <paramref name="ir"/> (which must be the IR it was taken from).</summary>
    public void Restore(GenIR ir)
    {
        ir.Height_Z = (sbyte[]?)_height?.Clone();
        ir.Moisture = (byte[]?)_moisture?.Clone();
        ir.Temperature = (byte[]?)_temperature?.Clone();
        ir.Slope = (byte[]?)_slope?.Clone();
        ir.Biome = (byte[]?)_biome?.Clone();
        ir.LandId = (ushort[]?)_landId?.Clone();
        ir.DungeonZone = (byte[]?)_dungeonZone?.Clone();
        Replace(ir.StaticOps, _staticOps);
        Replace(ir.Rivers, _rivers);
        Replace(ir.Roads, _roads);
        Replace(ir.Pois, _pois);
        Replace(ir.DungeonRooms, _rooms);
        ir.Reports.Clear();
        foreach (var kv in _reports) ir.Reports[kv.Key] = kv.Value;
        ir.PassVersion = _passVersion + 1; // a restore is a visible change for previews
    }

    private static void Replace<T>(List<T> target, T[] source)
    {
        target.Clear();
        target.AddRange(source);
    }
}
