namespace CentrED.MapGen.IR;

// Polyline + per-vertex flow used by RiverCarvePass and the previewer overlay.
public sealed class RiverSegment
{
    public List<(ushort X, ushort Y)> Path { get; } = new();
    public List<float> Flow { get; } = new();
    public int SourceId { get; init; }
}
