namespace CentrED.MapGen.IR;

public enum RoadKind : byte
{
    Dirt = 0,
    Cobble = 1,
    Stone = 2,
}

public sealed class RoadSegment
{
    public List<(ushort X, ushort Y)> Path { get; } = new();
    public RoadKind Kind { get; init; } = RoadKind.Dirt;
    public int FromPoiId { get; init; }
    public int ToPoiId { get; init; }
}
