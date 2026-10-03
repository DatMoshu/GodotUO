namespace CentrED.MapGen.Stamps;

/// <summary>How the Z values in a stamp file are expressed.</summary>
public enum StampZMode : byte
{
    /// <summary>Absolute world Z copied from the source map (every version-1 stamp).</summary>
    Absolute = 0,
    /// <summary>Z relative to the ground (land Z) of the anchor tile (rx=0, ry=0). Version 2+.</summary>
    Relative = 1,
}

/// <summary>
/// One stamp, normalised for placement. Whatever the file stored, every Z here is
/// RELATIVE to the anchor tile's ground: land Z 0 at the anchor, a tree standing on
/// the anchor tile has Z 0, a roof 20 above it has Z 20. Arrays are parallel
/// (index i of Land* describes one land cell, index i of Static* one static).
/// </summary>
public sealed class LoadedStamp
{
    public string Id = "";
    public string Kind = "other";
    public string? Subtype;
    /// <summary>Path relative to the library root, '/'-separated. Empty for stamps built in memory.</summary>
    public string Path = "";
    public string? SizeClass;
    public IReadOnlyList<string> Tags = Array.Empty<string>();
    public double Weight = 1.0;

    /// <summary>Schema version found in the file.</summary>
    public int Version = 1;
    /// <summary>Z mode found in the file (before normalisation).</summary>
    public StampZMode SourceZMode = StampZMode.Absolute;
    /// <summary>The absolute Z that was subtracted during normalisation (0 for relative files).</summary>
    public int ReferenceZ;
    /// <summary>
    /// Absolute ground Z the stamp was cut from, when known: the file's "reference_z"
    /// (v2, written by MakeStampWindow and the miner) or the subtracted ReferenceZ (v1).
    /// Informational only; placement never uses it.
    /// </summary>
    public int? SourceGroundZ;

    public int BoundsW;
    public int BoundsH;

    public int[] LandRx = Array.Empty<int>();
    public int[] LandRy = Array.Empty<int>();
    public ushort[] LandIds = Array.Empty<ushort>();
    /// <summary>Land Z relative to the anchor ground.</summary>
    public short[] LandZRel = Array.Empty<short>();

    public int[] StaticRx = Array.Empty<int>();
    public int[] StaticRy = Array.Empty<int>();
    public ushort[] StaticIds = Array.Empty<ushort>();
    /// <summary>Static Z relative to the anchor ground.</summary>
    public short[] StaticZRel = Array.Empty<short>();
    public ushort[] StaticHues = Array.Empty<ushort>();
    /// <summary>
    /// For each static, the index of its cluster anchor (itself when it is the anchor), or -1
    /// when the static is not part of a cluster. Cluster members keep their offsets to the
    /// anchor under rotation/mirroring so multi-tile trees stay whole.
    /// </summary>
    public int[] StaticClusterAnchor = Array.Empty<int>();

    private Dictionary<(int, int), int>? _landIndex;

    public int LandCount => LandRx.Length;
    public int StaticCount => StaticRx.Length;

    /// <summary>True when <see cref="StampVariantPolicy"/> lets this stamp be rotated or mirrored.</summary>
    public bool AllowsVariants => StampVariantPolicy.AllowsVariants(Kind);

    /// <summary>Index into the Land arrays of the cell at (rx, ry), or -1.</summary>
    public int LandIndexAt(int rx, int ry)
    {
        if (_landIndex is null)
        {
            var map = new Dictionary<(int, int), int>(LandRx.Length);
            for (int i = 0; i < LandRx.Length; i++) map[(LandRx[i], LandRy[i])] = i;
            _landIndex = map;
        }
        return _landIndex.TryGetValue((rx, ry), out var idx) ? idx : -1;
    }

    /// <summary>Relative land Z under (rx, ry), when the stamp carries land there.</summary>
    public bool TryGetLandZ(int rx, int ry, out int z)
    {
        int i = LandIndexAt(rx, ry);
        z = i >= 0 ? LandZRel[i] : 0;
        return i >= 0;
    }
}

/// <summary>Metadata for one stamp, from index.json or from parsing the file header.</summary>
public sealed record StampIndexRow(
    string Id,
    string Kind,
    string? Subtype,
    string Path,
    int Width,
    int Height,
    int StaticCount,
    int LandCount,
    IReadOnlyList<string> Tags,
    double Weight);
